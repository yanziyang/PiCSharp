using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.Ai;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Diff details returned by the built-in edit tool.</summary>
public sealed record EditToolDetails(string Diff, string Patch, int? FirstChangedLine = null);

/// <summary>Factory for the built-in exact-text edit tool.</summary>
public static class EditTool
{
    /// <summary>Creates an edit tool using the standard execution context.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateEditTool() => CreateEditTool<ExecutionToolContext>();

    /// <summary>Creates an edit tool using a derived execution context.</summary>
    public static AgentHarnessTool<TContext> CreateEditTool<TContext>()
        where TContext : ExecutionToolContext => new()
        {
            Name = "edit",
            Label = "edit",
            Description = "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.",
            Parameters = CreateSchema(),
            Execute = async (toolCallId, parameters, signal, onUpdate, context) =>
            {
                _ = toolCallId;
                _ = onUpdate;
                ArgumentNullException.ThrowIfNull(context);
                var input = PrepareEditArguments(ToolHelpers.RequireObject(parameters));
                var (path, edits) = ValidateEditInput(input);
                var absolutePath = await ToolPathUtilities.ResolveToolPathAsync(context.Env, path, signal).ConfigureAwait(false);
                return await FileMutationQueue.WithFileMutationQueueAsync(context.Env, absolutePath, async () =>
                {
                    ToolHelpers.ThrowIfAborted(signal);
                    var info = await context.Env.FileInfoAsync(absolutePath, signal).ConfigureAwait(false);
                    if (!info.Ok)
                    {
                        throw EditAccessError(path, info.Error!);
                    }

                    if (info.Value!.Kind is not (FileKinds.File or FileKinds.Symlink))
                    {
                        throw new InvalidOperationException($"Could not edit file: {path}. Path is not a file.");
                    }

                    var readResult = await context.Env.ReadTextFileAsync(absolutePath, signal).ConfigureAwait(false);
                    if (!readResult.Ok)
                    {
                        throw EditAccessError(path, readResult.Error!);
                    }

                    ToolHelpers.ThrowIfAborted(signal);
                    var (bom, content) = EditDiff.StripBom(readResult.Value!);
                    var originalEnding = EditDiff.DetectLineEnding(content);
                    var normalizedContent = EditDiff.NormalizeToLf(content);
                    var applied = EditDiff.ApplyEditsToNormalizedContent(normalizedContent, edits, path);
                    ToolHelpers.ThrowIfAborted(signal);
                    var finalContent = bom + EditDiff.RestoreLineEndings(applied.NewContent, originalEnding);
                    var writeResult = await context.Env.WriteFileAsync(absolutePath, finalContent, signal).ConfigureAwait(false);
                    if (!writeResult.Ok)
                    {
                        throw EditAccessError(path, writeResult.Error!);
                    }

                    ToolHelpers.ThrowIfAborted(signal);
                    var diff = EditDiff.GenerateDiffString(applied.BaseContent, applied.NewContent);
                    var details = new JsonObject
                    {
                        ["diff"] = diff.Diff,
                        ["patch"] = EditDiff.GenerateUnifiedPatch(path, applied.BaseContent, applied.NewContent),
                    };
                    if (diff.FirstChangedLine is not null)
                    {
                        details["firstChangedLine"] = diff.FirstChangedLine.Value;
                    }

                    return ToolHelpers.TextResult(
                        $"Successfully replaced {edits.Count} block(s) in {path}.",
                        details);
                }).ConfigureAwait(false);
            },
        };

    /// <summary>Normalizes the legacy single-replacement argument shape.</summary>
    public static JsonObject PrepareEditArguments(JsonObject input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.TryGetPropertyValue("edits", out var editsNode) && editsNode is JsonValue editsValue &&
            editsValue.TryGetValue<string>(out var editsJson))
        {
            try
            {
                var parsed = JsonNode.Parse(editsJson);
                if (parsed is JsonArray)
                {
                    input["edits"] = parsed;
                }
                else if (IsSingleEditInput(parsed))
                {
                    input["edits"] = new JsonArray(parsed!.DeepClone());
                }
            }
            catch (JsonException)
            {
                // Validation below reports the same invalid input rather than exposing a parser implementation detail.
            }
        }
        else if (IsSingleEditInput(editsNode))
        {
            input["edits"] = new JsonArray(editsNode!.DeepClone());
        }

        if (input.TryGetPropertyValue("oldText", out var oldTextNode) &&
            input.TryGetPropertyValue("newText", out var newTextNode) &&
            oldTextNode is JsonValue oldValue && newTextNode is JsonValue newValue &&
            oldValue.TryGetValue<string>(out var oldText) && newValue.TryGetValue<string>(out var newText))
        {
            var edits = input["edits"] as JsonArray ?? [];
            edits.Add((JsonNode)new JsonObject { ["oldText"] = oldText, ["newText"] = newText });
            input["edits"] = edits;
            input.Remove("oldText");
            input.Remove("newText");
        }

        return input;
    }

    private static (string Path, List<Edit> Edits) ValidateEditInput(JsonObject input)
    {
        var path = ToolHelpers.RequireString(input, "path");
        if (!input.TryGetPropertyValue("edits", out var editsNode) || editsNode is not JsonArray editsArray || editsArray.Count == 0)
        {
            throw new ArgumentException("Edit tool input is invalid. edits must contain at least one replacement.", nameof(input));
        }

        var edits = new List<Edit>(editsArray.Count);
        foreach (var node in editsArray)
        {
            if (node is not JsonObject edit)
            {
                throw new ArgumentException("Edit tool input is invalid. edits must contain replacement objects.", nameof(input));
            }

            edits.Add(new Edit(
                ToolHelpers.RequireString(edit, "oldText"),
                ToolHelpers.RequireString(edit, "newText")));
        }

        return (path, edits);
    }

    private static bool IsSingleEditInput(JsonNode? value) => value is JsonObject edit &&
        edit.TryGetPropertyValue("oldText", out var oldText) && oldText is JsonValue oldValue &&
        oldValue.TryGetValue<string>(out _) &&
        edit.TryGetPropertyValue("newText", out var newText) && newText is JsonValue newValue &&
        newValue.TryGetValue<string>(out _);

    private static InvalidOperationException EditAccessError(string path, FileError error) =>
        new($"Could not edit file: {path}. Error code: {error.Code}.", error);

    private static JsonObject CreateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to the file to edit (relative or absolute)",
            },
            ["edits"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["oldText"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call.",
                        },
                        ["newText"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Replacement text for this targeted edit.",
                        },
                    },
                    ["required"] = new JsonArray("oldText", "newText"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("path", "edits"),
        ["additionalProperties"] = false,
    };
}
