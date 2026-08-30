using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using SessionAgentMessage = Pi.AgentCore.Harness.Session.AgentMessage;

namespace Pi.AgentCore.Harness;

/// <summary>Text prefix used for generated branch summaries.</summary>
public static class HarnessMessagePrefixes
{
    /// <summary>Compaction-summary prefix.</summary>
    public const string CompactionSummary = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    /// <summary>Compaction-summary suffix.</summary>
    public const string CompactionSummarySuffix = "\n</summary>";

    /// <summary>Branch-summary prefix.</summary>
    public const string BranchSummary = "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n";

    /// <summary>Branch-summary suffix.</summary>
    public const string BranchSummarySuffix = "</summary>";
}

/// <summary>Shell execution output represented as an agent message.</summary>
public sealed record BashExecutionMessage
{
    /// <summary>Message role discriminator.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "The role discriminator is a constant part of the upstream message shape.")]
    public string Role => "bashExecution";

    /// <summary>Executed command.</summary>
    public required string Command { get; init; }

    /// <summary>Captured command output.</summary>
    public required string Output { get; init; }

    /// <summary>Exit code, or null when the process did not produce one.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether execution was cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Whether output was truncated.</summary>
    public bool Truncated { get; init; }

    /// <summary>Path containing the complete output, when available.</summary>
    public string? FullOutputPath { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; init; }

    /// <summary>Whether this message is hidden from model context.</summary>
    public bool ExcludeFromContext { get; init; }

    /// <summary>Converts this custom message to the session message representation.</summary>
    public SessionAgentMessage ToAgentMessage() => new(ToJson());

    private JsonObject ToJson() => new()
    {
        ["role"] = Role,
        ["command"] = Command,
        ["output"] = Output,
        ["exitCode"] = ExitCode,
        ["cancelled"] = Cancelled,
        ["truncated"] = Truncated,
        ["fullOutputPath"] = FullOutputPath,
        ["timestamp"] = Timestamp,
        ["excludeFromContext"] = ExcludeFromContext,
    };
}

/// <summary>Application-defined custom message.</summary>
public sealed record CustomMessage<TDetails>
{
    /// <summary>Message role discriminator.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "The role discriminator is a constant part of the upstream message shape.")]
    public string Role => "custom";

    /// <summary>Application-defined message type.</summary>
    public required string CustomType { get; init; }

    /// <summary>Text content or multimodal content blocks.</summary>
    public required object Content { get; init; }

    /// <summary>Whether the message is displayed by the application.</summary>
    public bool Display { get; init; }

    /// <summary>Application-defined details.</summary>
    public TDetails? Details { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; init; }

    /// <summary>Converts this custom message to the session message representation.</summary>
    public SessionAgentMessage ToAgentMessage() => new(ToJson());

    private JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["role"] = Role,
            ["customType"] = CustomType,
            ["content"] = HarnessMessageUtilities.SerializeContent(Content),
            ["display"] = Display,
            ["timestamp"] = Timestamp,
        };
        if (Details is not null)
        {
            result["details"] = HarnessMessageUtilities.ToJsonNode(Details);
        }

        return result;
    }
}

/// <summary>Generated branch-summary message.</summary>
public sealed record BranchSummaryMessage
{
    /// <summary>Message role discriminator.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "The role discriminator is a constant part of the upstream message shape.")]
    public string Role => "branchSummary";

    /// <summary>Summary text.</summary>
    public required string Summary { get; init; }

    /// <summary>Entry from which the branch was summarized.</summary>
    public required string FromId { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; init; }

    /// <summary>Converts this custom message to the session message representation.</summary>
    public SessionAgentMessage ToAgentMessage() => new(new JsonObject
    {
        ["role"] = Role,
        ["summary"] = Summary,
        ["fromId"] = FromId,
        ["timestamp"] = Timestamp,
    });
}

/// <summary>Generated compaction-summary message.</summary>
public sealed record CompactionSummaryMessage
{
    /// <summary>Message role discriminator.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "The role discriminator is a constant part of the upstream message shape.")]
    public string Role => "compactionSummary";

    /// <summary>Summary text.</summary>
    public required string Summary { get; init; }

    /// <summary>Estimated tokens before compaction.</summary>
    public long TokensBefore { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; init; }

    /// <summary>Converts this custom message to the session message representation.</summary>
    public SessionAgentMessage ToAgentMessage() => new(new JsonObject
    {
        ["role"] = Role,
        ["summary"] = Summary,
        ["tokensBefore"] = TokensBefore,
        ["timestamp"] = Timestamp,
    });
}

/// <summary>Conversions and text helpers for standard and custom harness messages.</summary>
public static class HarnessMessageUtilities
{
    /// <summary>Renders shell output using Pi's exact summary text format.</summary>
    public static string BashExecutionToText(BashExecutionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = $"Ran `{message.Command}`\n";
        text += string.IsNullOrEmpty(message.Output) ? "(no output)" : $"```\n{message.Output}\n```";
        if (message.Cancelled)
        {
            text += "\n\n(command cancelled)";
        }
        else if (message.ExitCode is not null && message.ExitCode.Value != 0)
        {
            text += $"\n\nCommand exited with code {message.ExitCode.Value}";
        }

        if (message.Truncated && message.FullOutputPath is not null)
        {
            text += $"\n\n[Output truncated. Full output: {message.FullOutputPath}]";
        }

        return text;
    }

    /// <summary>Creates a branch-summary message with a Unix-millisecond timestamp.</summary>
    public static BranchSummaryMessage CreateBranchSummaryMessage(string summary, string fromId, long timestamp) =>
        new() { Summary = summary, FromId = fromId, Timestamp = timestamp };

    /// <summary>Creates a branch-summary message from an ISO timestamp.</summary>
    public static BranchSummaryMessage CreateBranchSummaryMessage(string summary, string fromId, string timestamp) =>
        CreateBranchSummaryMessage(summary, fromId, ParseTimestamp(timestamp));

    /// <summary>Creates a compaction-summary message with a Unix-millisecond timestamp.</summary>
    public static CompactionSummaryMessage CreateCompactionSummaryMessage(string summary, long tokensBefore, long timestamp) =>
        new() { Summary = summary, TokensBefore = tokensBefore, Timestamp = timestamp };

    /// <summary>Creates a compaction-summary message from an ISO timestamp.</summary>
    public static CompactionSummaryMessage CreateCompactionSummaryMessage(string summary, long tokensBefore, string timestamp) =>
        CreateCompactionSummaryMessage(summary, tokensBefore, ParseTimestamp(timestamp));

    /// <summary>Creates an application-defined custom message.</summary>
    public static CustomMessage<object> CreateCustomMessage(
        string customType,
        object content,
        bool display,
        object? details,
        long timestamp) =>
        new()
        {
            CustomType = customType,
            Content = content,
            Display = display,
            Details = details,
            Timestamp = timestamp,
        };

    /// <summary>Converts agent messages to standard provider messages, filtering UI-only roles.</summary>
    public static IReadOnlyList<Message> ConvertToLlm(IEnumerable<SessionAgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<Message>();
        foreach (var message in messages)
        {
            if (TryConvertToLlm(message, out var converted))
            {
                result.Add(converted);
            }
        }

        return result;
    }

    /// <summary>Returns a standard provider message for one agent message, when possible.</summary>
    public static bool TryConvertToLlm(SessionAgentMessage message, out Message converted)
    {
        ArgumentNullException.ThrowIfNull(message);
        var role = message.Role;
        switch (role)
        {
            case "user":
                converted = ParseUserMessage(message.Value);
                return true;
            case "assistant":
            case "toolResult":
                converted = message.ToPiMessage() ?? throw new InvalidOperationException($"Invalid {role} message.");
                return true;
            case "bashExecution":
                {
                    var bash = ParseBashExecution(message.Value);
                    if (bash.ExcludeFromContext)
                    {
                        converted = null!;
                        return false;
                    }

                    converted = UserMessage.Blocks([new TextContent(BashExecutionToText(bash))], bash.Timestamp);
                    return true;
                }
            case "custom":
                {
                    var content = ParseContentValue(message.Value["content"]);
                    converted = UserMessage.Blocks(content, GetInt64(message.Value, "timestamp"));
                    return true;
                }
            case "branchSummary":
                converted = UserMessage.Blocks(
                    [new TextContent(HarnessMessagePrefixes.BranchSummary + GetString(message.Value, "summary") + HarnessMessagePrefixes.BranchSummarySuffix)],
                    GetInt64(message.Value, "timestamp"));
                return true;
            case "compactionSummary":
                converted = UserMessage.Blocks(
                    [new TextContent(HarnessMessagePrefixes.CompactionSummary + GetString(message.Value, "summary") + HarnessMessagePrefixes.CompactionSummarySuffix)],
                    GetInt64(message.Value, "timestamp"));
                return true;
            default:
                converted = null!;
                return false;
        }
    }

    /// <summary>Extracts text blocks from a provider message or open JSON content value.</summary>
    public static string ContentText(Message message, string separator = "\n")
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            UserMessage user => ContentText(user.Content, separator),
            AssistantMessage assistant => string.Join(separator, assistant.Content.OfType<TextContent>().Select(static block => block.Text)),
            ToolResultMessage toolResult => string.Join(separator, toolResult.Content.OfType<TextContent>().Select(static block => block.Text)),
            _ => string.Empty,
        };
    }

    /// <summary>Extracts text from a string, JSON content value, or content-block collection.</summary>
    public static string ContentText(object? content, string separator = "\n")
    {
        return content switch
        {
            null => string.Empty,
            string text => text,
            JsonArray array => string.Join(separator, ParseContentBlocks(array).OfType<TextContent>().Select(static block => block.Text)),
            IEnumerable<ContentBlock> blocks => string.Join(separator, blocks.OfType<TextContent>().Select(static block => block.Text)),
            _ => string.Empty,
        };
    }

    /// <summary>Extracts tool calls from an assistant agent message.</summary>
    public static IReadOnlyList<ToolCall> GetToolCalls(SessionAgentMessage message)
    {
        if (message.Role != "assistant" || message.Value["content"] is not JsonArray content)
        {
            return [];
        }

        return content
            .OfType<JsonObject>()
            .Where(block => GetString(block, "type") == "toolCall")
            .Select(ParseToolCall)
            .ToArray();
    }

    /// <summary>Returns the assistant message represented by an agent message.</summary>
    public static AssistantMessage? TryGetAssistant(SessionAgentMessage message) =>
        message.Role == "assistant" ? message.ToPiMessage() as AssistantMessage : null;

    /// <summary>Returns an agent message's deferred provider handle, when present and valid.</summary>
    public static DeferredHandle? TryGetDeferredHandle(SessionAgentMessage message)
    {
        if (message.Role != "assistant" || message.Value["deferred"] is not JsonObject value)
        {
            return null;
        }

        return new DeferredHandle
        {
            Provider = GetString(value, "provider") ?? string.Empty,
            ModelId = GetString(value, "modelId") ?? string.Empty,
            Api = GetString(value, "api") ?? string.Empty,
            Id = GetString(value, "id") ?? string.Empty,
            ExpiresAt = GetNullableInt64(value, "expiresAt"),
            PollAfterMs = GetNullableInt32(value, "pollAfterMs"),
            Data = value["data"]?.DeepClone(),
        };
    }

    /// <summary>Estimates one agent message using the upstream four-character heuristic.</summary>
    public static int EstimateTokens(SessionAgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chars = message.Role switch
        {
            "user" => EstimateContentChars(message.Value["content"]),
            "assistant" => EstimateAssistantChars(message.Value["content"]),
            "custom" or "toolResult" => EstimateContentChars(message.Value["content"]),
            "bashExecution" => GetString(message.Value, "command").Length + GetString(message.Value, "output").Length,
            "branchSummary" or "compactionSummary" => GetString(message.Value, "summary").Length,
            _ => 0,
        };
        return CeilingDivide(chars, 4);
    }

    /// <summary>Returns a compact JSON representation, or the upstream fallback for unserializable values.</summary>
    public static string SafeJsonStringify(JsonNode? value)
    {
        if (value is null)
        {
            return "undefined";
        }

        try
        {
            return value.ToJsonString(SessionJson.Options);
        }
        catch
        {
            return "[unserializable]";
        }
    }

    internal static JsonNode? SerializeContent(object content)
    {
        return content switch
        {
            string text => JsonValue.Create(text),
            JsonNode node => node.DeepClone(),
            IEnumerable<ContentBlock> blocks => new JsonArray(blocks.Select(SessionJson.ContentToJson).ToArray()),
            _ => ToJsonNode(content),
        };
    }

    internal static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            sbyte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            Enum enumeration => JsonValue.Create(enumeration.ToString()),
            IEnumerable<object?> values => new JsonArray(values.Select(ToJsonNode).ToArray()),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    internal static IReadOnlyList<ContentBlock> ParseContentValue(JsonNode? content)
    {
        return content switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => [new TextContent(text)],
            JsonArray array => ParseContentBlocks(array),
            _ => [],
        };
    }

    internal static IReadOnlyList<ContentBlock> ParseContentBlocks(JsonArray array)
    {
        return array
            .OfType<JsonObject>()
            .Select(ParseContentBlock)
            .Where(static block => block is not null)
            .Cast<ContentBlock>()
            .ToArray();
    }

    private static ContentBlock? ParseContentBlock(JsonObject block)
    {
        return GetString(block, "type") switch
        {
            "text" => new TextContent(GetString(block, "text")),
            "thinking" => new ThinkingContent(GetString(block, "thinking")),
            "image" => new ImageContent(GetString(block, "data"), GetString(block, "mimeType")),
            "toolCall" => ParseToolCall(block),
            _ => null,
        };
    }

    private static ToolCall ParseToolCall(JsonObject block) => new(
        GetString(block, "id"),
        GetString(block, "name"),
        block["arguments"] is JsonObject arguments ? (JsonObject)arguments.DeepClone() : new JsonObject());

    private static UserMessage ParseUserMessage(JsonObject value)
    {
        var content = value["content"];
        var timestamp = GetInt64(value, "timestamp");
        return content is JsonValue json && json.TryGetValue<string>(out var text)
            ? UserMessage.Text(text, timestamp)
            : UserMessage.Blocks(ParseContentValue(content), timestamp);
    }

    private static BashExecutionMessage ParseBashExecution(JsonObject value) => new()
    {
        Command = GetString(value, "command"),
        Output = GetString(value, "output"),
        ExitCode = GetNullableInt32(value, "exitCode"),
        Cancelled = GetBool(value, "cancelled"),
        Truncated = GetBool(value, "truncated"),
        FullOutputPath = GetNullableString(value, "fullOutputPath"),
        Timestamp = GetInt64(value, "timestamp"),
        ExcludeFromContext = GetBool(value, "excludeFromContext"),
    };

    private static int EstimateContentChars(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text.Length;
        }

        if (content is not JsonArray array)
        {
            return 0;
        }

        var chars = 0;
        foreach (var block in array.OfType<JsonObject>())
        {
            switch (GetString(block, "type"))
            {
                case "text":
                    chars += GetString(block, "text").Length;
                    break;
                case "image":
                    chars += 4800;
                    break;
            }
        }

        return chars;
    }

    private static int EstimateAssistantChars(JsonNode? content)
    {
        if (content is not JsonArray array)
        {
            return 0;
        }

        var chars = 0;
        foreach (var block in array.OfType<JsonObject>())
        {
            switch (GetString(block, "type"))
            {
                case "text":
                    chars += GetString(block, "text").Length;
                    break;
                case "thinking":
                    chars += GetString(block, "thinking").Length;
                    break;
                case "toolCall":
                    chars += GetString(block, "name").Length + SafeJsonStringify(block["arguments"]).Length;
                    break;
            }
        }

        return chars;
    }

    private static int CeilingDivide(int value, int divisor) => value == 0 ? 0 : (value + divisor - 1) / divisor;

    private static long ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static string GetString(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string? GetNullableString(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;

    private static long GetInt64(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<long>(out var number) ? number : 0;

    private static long? GetNullableInt64(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<long>(out var number) ? number : null;

    private static int? GetNullableInt32(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<int>(out var number) ? number : null;

    private static bool GetBool(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<bool>(out var boolean) && boolean;
}
