using System.Text;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Utils;
using Pi.Ai;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Details returned when a text read is truncated.</summary>
public sealed record ReadToolDetails(TruncationResult? Truncation = null);

/// <summary>Result returned by an injected image conversion processor.</summary>
public sealed record ReadImageProcessorResult
{
    /// <summary>Whether conversion succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>Base64 image data on success.</summary>
    public string? Data { get; init; }

    /// <summary>Converted MIME type on success.</summary>
    public string? MimeType { get; init; }

    /// <summary>Model-visible conversion hints.</summary>
    public IReadOnlyList<string> Hints { get; init; } = [];

    /// <summary>Failure text on failure.</summary>
    public string? Message { get; init; }

    /// <summary>Creates a successful processor result.</summary>
    public static ReadImageProcessorResult Success(string data, string mimeType, IReadOnlyList<string>? hints = null) => new()
    {
        Ok = true,
        Data = data,
        MimeType = mimeType,
        Hints = hints ?? [],
    };

    /// <summary>Creates a failed processor result.</summary>
    public static ReadImageProcessorResult Failure(string message) => new() { Message = message };
}

/// <summary>Callback used to convert or resize an image before it is returned.</summary>
public delegate Task<ReadImageProcessorResult> ReadImageProcessor(
    byte[] bytes,
    string mimeType,
    bool autoResizeImages);

/// <summary>Options for creating the built-in read tool.</summary>
public sealed class ReadToolOptions
{
    /// <summary>Whether the injected processor should resize images.</summary>
    public bool AutoResizeImages { get; init; } = true;

    /// <summary>Optional image conversion or resizing callback.</summary>
    public ReadImageProcessor? ImageProcessor { get; init; }
}

/// <summary>Factory for the built-in read tool.</summary>
public static class ReadTool
{
    /// <summary>Creates a read tool using the standard execution context.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateReadTool(
        ReadToolOptions? options = null) =>
        CreateReadTool<ExecutionToolContext>(options);

    /// <summary>Creates a read tool using a derived execution context.</summary>
    public static AgentHarnessTool<TContext> CreateReadTool<TContext>(
        ReadToolOptions? options = null)
        where TContext : ExecutionToolContext => new()
        {
            Name = "read",
            Label = "read",
            Description = $"Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.",
            Parameters = ToolHelpers.Schema(
                ("path", "string", "Path to the file to read (relative or absolute)", true),
                ("offset", "number", "Line number to start reading from (1-indexed)", false),
                ("limit", "number", "Maximum number of lines to read", false)),
            Execute = async (toolCallId, parameters, signal, onUpdate, context) =>
            {
                _ = toolCallId;
                _ = onUpdate;
                ArgumentNullException.ThrowIfNull(context);
                var input = ToolHelpers.RequireObject(parameters);
                var path = ToolHelpers.RequireString(input, "path");
                var offsetValue = ToolHelpers.OptionalNumber(input, "offset");
                var limitValue = ToolHelpers.OptionalNumber(input, "limit");
                var offset = offsetValue is null ? (int?)null : checked((int)offsetValue.Value);
                var limit = limitValue is null ? (int?)null : checked((int)limitValue.Value);
                var absolutePath = await ToolPathUtilities.ResolveReadToolPathAsync(context.Env, path, signal).ConfigureAwait(false);
                var bytes = Result.GetOrThrow(await context.Env.ReadBinaryFileAsync(absolutePath, signal).ConfigureAwait(false));
                var mimeType = ImageUtilities.DetectSupportedImageMimeType(bytes);
                if (mimeType is not null)
                {
                    if (options?.ImageProcessor is not null)
                    {
                        var processed = await options.ImageProcessor(bytes, mimeType, options.AutoResizeImages).ConfigureAwait(false);
                        if (!processed.Ok)
                        {
                            return ToolHelpers.TextResult(
                                $"Read image file [{mimeType}]\n{processed.Message}",
                                details: null);
                        }

                        var hints = processed.Hints.Count > 0 ? $"\n{string.Join('\n', processed.Hints)}" : string.Empty;
                        return new AgentToolResult
                        {
                            Content =
                            [
                                new TextContent($"Read image file [{processed.MimeType}]{hints}"),
                                new ImageContent(processed.Data!, processed.MimeType!),
                            ],
                            Details = null,
                        };
                    }

                    if (mimeType == "image/bmp")
                    {
                        return ToolHelpers.TextResult(
                            "Read image file [image/bmp]\n[Image omitted: configure an imageProcessor to convert BMP images.]",
                            details: null);
                    }

                    return new AgentToolResult
                    {
                        Content = [new TextContent($"Read image file [{mimeType}]"), new ImageContent(ImageUtilities.EncodeBase64(bytes), mimeType)],
                        Details = null,
                    };
                }

                var textContent = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
                var allLines = textContent.Split('\n');
                var totalFileLines = allLines.Length;
                var startLine = offset is not null && offset.Value != 0 ? Math.Max(0, offset.Value - 1) : 0;
                var startLineDisplay = startLine + 1;
                if (startLine >= allLines.Length)
                {
                    throw new InvalidOperationException($"Offset {offset} is beyond end of file ({allLines.Length} lines total)");
                }

                string selectedContent;
                int? userLimitedLines = null;
                if (limit is not null)
                {
                    var endLine = Math.Min(startLine + limit.Value, allLines.Length);
                    selectedContent = string.Join('\n', allLines[startLine..endLine]);
                    userLimitedLines = endLine - startLine;
                }
                else
                {
                    selectedContent = string.Join('\n', allLines[startLine..]);
                }

                var truncation = Truncate.TruncateHead(selectedContent);
                string outputText;
                JsonObject? details = null;
                if (truncation.FirstLineExceedsLimit)
                {
                    var firstLineSize = Truncate.FormatSize(new UTF8Encoding(false, false).GetByteCount(allLines[startLine]));
                    outputText = $"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {path} | head -c {Truncate.DefaultMaxBytes}]";
                    details = new JsonObject { ["truncation"] = ToolHelpers.TruncationDetails(truncation) };
                }
                else if (truncation.Truncated)
                {
                    var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
                    var nextOffset = endLineDisplay + 1;
                    outputText = truncation.Content;
                    if (truncation.TruncatedBy == "lines")
                    {
                        outputText += $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines}. Use offset={nextOffset} to continue.]";
                    }
                    else
                    {
                        outputText += $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]";
                    }

                    details = new JsonObject { ["truncation"] = ToolHelpers.TruncationDetails(truncation) };
                }
                else if (userLimitedLines is not null && startLine + userLimitedLines.Value < allLines.Length)
                {
                    var remaining = allLines.Length - (startLine + userLimitedLines.Value);
                    var nextOffset = startLine + userLimitedLines.Value + 1;
                    outputText = $"{truncation.Content}\n\n[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
                }
                else
                {
                    outputText = truncation.Content;
                }

                return ToolHelpers.TextResult(outputText, details);
            },
        };
}
