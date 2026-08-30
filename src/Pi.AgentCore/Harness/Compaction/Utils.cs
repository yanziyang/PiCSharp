using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;

namespace Pi.AgentCore.Harness.Compaction;

/// <summary>File paths touched by a session branch or compaction range.</summary>
public sealed class FileOperations
{
    /// <summary>Files read but not necessarily modified.</summary>
    public HashSet<string> Read { get; } = new(StringComparer.Ordinal);

    /// <summary>Files written by full-file write operations.</summary>
    public HashSet<string> Written { get; } = new(StringComparer.Ordinal);

    /// <summary>Files modified by edit operations.</summary>
    public HashSet<string> Edited { get; } = new(StringComparer.Ordinal);
}

/// <summary>Utilities used to preserve file-operation context in generated summaries.</summary>
public static class CompactionUtilities
{
    private const int _toolResultMaxChars = 2000;

    /// <summary>Creates an empty file-operation accumulator.</summary>
    public static FileOperations CreateFileOps() => new();

    /// <summary>Adds file operations from assistant tool calls to an accumulator.</summary>
    public static void ExtractFileOpsFromMessage(AgentMessage message, FileOperations fileOps)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(fileOps);

        if (message.Role != "assistant")
        {
            return;
        }

        var assistant = HarnessMessageUtilities.TryGetAssistant(message);
        if (assistant is null)
        {
            return;
        }

        foreach (var toolCall in assistant.Content.OfType<ToolCall>())
        {
            var path = toolCall.Arguments["path"] is JsonValue value &&
                       value.TryGetValue<string>(out var stringPath)
                ? stringPath
                : null;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            switch (toolCall.Name)
            {
                case "read":
                    fileOps.Read.Add(path);
                    break;
                case "write":
                    fileOps.Written.Add(path);
                    break;
                case "edit":
                    fileOps.Edited.Add(path);
                    break;
            }
        }
    }

    /// <summary>Computes sorted read-only and modified file lists.</summary>
    public static (IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles) ComputeFileLists(FileOperations fileOps)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        var modified = fileOps.Edited.Concat(fileOps.Written).ToHashSet(StringComparer.Ordinal);
        var readOnly = fileOps.Read
            .Where(path => !modified.Contains(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var modifiedFiles = modified.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        return (readOnly, modifiedFiles);
    }

    /// <summary>Formats file lists as summary metadata tags.</summary>
    public static string FormatFileOperations(IReadOnlyList<string> readFiles, IReadOnlyList<string> modifiedFiles)
    {
        ArgumentNullException.ThrowIfNull(readFiles);
        ArgumentNullException.ThrowIfNull(modifiedFiles);

        var sections = new List<string>(capacity: 2);
        if (readFiles.Count > 0)
        {
            sections.Add($"<read-files>\n{string.Join("\n", readFiles)}\n</read-files>");
        }

        if (modifiedFiles.Count > 0)
        {
            sections.Add($"<modified-files>\n{string.Join("\n", modifiedFiles)}\n</modified-files>");
        }

        return sections.Count == 0 ? string.Empty : $"\n\n{string.Join("\n\n", sections)}";
    }

    /// <summary>Serializes provider messages into the plain-text format used by summary prompts.</summary>
    public static string SerializeConversation(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var parts = new List<string>();

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    {
                        var content = HarnessMessageUtilities.ContentText(user.Content, string.Empty);
                        if (content.Length > 0)
                        {
                            parts.Add($"[User]: {content}");
                        }

                        break;
                    }
                case AssistantMessage assistant:
                    {
                        var thinkingParts = assistant.Content
                            .OfType<ThinkingContent>()
                            .Select(static block => block.Thinking)
                            .ToArray();
                        var toolCalls = assistant.Content
                            .OfType<ToolCall>()
                            .Select(static block =>
                            {
                                var arguments = block.Arguments
                                    .Select(static pair => $"{pair.Key}={SafeJsonStringify(pair.Value)}")
                                    .ToArray();
                                return $"{block.Name}({string.Join(", ", arguments)})";
                            })
                            .ToArray();

                        if (thinkingParts.Length > 0)
                        {
                            parts.Add($"[Assistant thinking]: {string.Join("\n", thinkingParts)}");
                        }

                        if (assistant.Content.Any(static block => block is TextContent))
                        {
                            parts.Add($"[Assistant]: {HarnessMessageUtilities.ContentText(assistant)}");
                        }

                        if (toolCalls.Length > 0)
                        {
                            parts.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
                        }

                        break;
                    }
                case ToolResultMessage toolResult:
                    {
                        var content = HarnessMessageUtilities.ContentText(toolResult, string.Empty);
                        if (content.Length > 0)
                        {
                            parts.Add($"[Tool result]: {TruncateForSummary(content, _toolResultMaxChars)}");
                        }

                        break;
                    }
            }
        }

        return string.Join("\n\n", parts);
    }

    private static string SafeJsonStringify(JsonNode? value)
    {
        return HarnessMessageUtilities.SafeJsonStringify(value);
    }

    private static string TruncateForSummary(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var truncatedChars = text.Length - maxChars;
        return $"{text[..maxChars]}\n\n[... {truncatedChars} more characters truncated]";
    }
}
