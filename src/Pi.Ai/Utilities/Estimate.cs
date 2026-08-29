using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Estimated token usage for a context and its trailing messages.</summary>
public sealed record ContextUsageEstimate
{
    /// <summary>Estimated total context tokens.</summary>
    public required int Tokens { get; init; }

    /// <summary>Tokens reported by the most recent applicable assistant usage block.</summary>
    public required int UsageTokens { get; init; }

    /// <summary>Estimated tokens after the most recent applicable assistant usage block.</summary>
    public required int TrailingTokens { get; init; }

    /// <summary>Index of the message that provided usage, or null when none exists.</summary>
    public required int? LastUsageIndex { get; init; }
}

/// <summary>Context token estimation helpers used before provider requests.</summary>
public static class EstimateUtilities
{
    private const int _charsPerToken = 4;
    private const int _estimatedImageChars = 4800;

    /// <summary>Returns the provider-reported total, falling back to its component sum.</summary>
    public static int CalculateContextTokens(Usage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.TotalTokens != 0
            ? usage.TotalTokens
            : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
    }

    /// <summary>Estimates tokens in text using Pi's four-characters-per-token heuristic.</summary>
    public static int EstimateTextTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + _charsPerToken - 1) / _charsPerToken;
    }

    /// <summary>Estimates tokens in text and image content.</summary>
    public static int EstimateTextAndImageContentTokens(object content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var characters = content switch
        {
            string text => text.Length,
            IEnumerable<ContentBlock> blocks => blocks.Sum(static block => block switch
            {
                TextContent textBlock => textBlock.Text.Length,
                ImageContent => _estimatedImageChars,
                _ => 0,
            }),
            _ => 0,
        };
        return (characters + _charsPerToken - 1) / _charsPerToken;
    }

    /// <summary>Estimates tokens in one Pi message.</summary>
    public static int EstimateMessageTokens(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is UserMessage user)
        {
            return EstimateTextAndImageContentTokens(user.Content);
        }

        if (message is ToolResultMessage result)
        {
            return EstimateTextAndImageContentTokens(result.Content);
        }

        if (message is not AssistantMessage assistant)
        {
            return 0;
        }

        var characters = 0;
        foreach (var block in assistant.Content)
        {
            characters += block switch
            {
                TextContent text => text.Text.Length,
                ThinkingContent thinking => thinking.Thinking.Length,
                ToolCall toolCall => toolCall.Name.Length + SafeJsonStringify(toolCall.Arguments).Length,
                _ => 0,
            };
        }

        return (characters + _charsPerToken - 1) / _charsPerToken;
    }

    /// <summary>Estimates tokens for an ordered message list.</summary>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return EstimateMessages(messages);
    }

    /// <summary>Estimates tokens for a context, including system prompt and tool definitions.</summary>
    public static ContextUsageEstimate EstimateContextTokens(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var estimate = EstimateMessages(context.Messages);
        if (estimate.LastUsageIndex is not null)
        {
            var addedNames = context.Messages
                .Skip(estimate.LastUsageIndex.Value + 1)
                .OfType<ToolResultMessage>()
                .SelectMany(static message => message.AddedToolNames ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var addedToolTokens = EstimateToolsTokens(context.Tools.Where(tool => addedNames.Contains(tool.Name)));
            return estimate with
            {
                Tokens = estimate.Tokens + addedToolTokens,
                TrailingTokens = estimate.TrailingTokens + addedToolTokens,
            };
        }

        var prefixTokens = (context.SystemPrompt is null ? 0 : EstimateTextTokens(context.SystemPrompt)) +
                           EstimateToolsTokens(context.Tools);
        return estimate with
        {
            Tokens = estimate.Tokens + prefixTokens,
            TrailingTokens = estimate.TrailingTokens + prefixTokens,
        };
    }

    private static ContextUsageEstimate EstimateMessages(IReadOnlyList<Message> messages)
    {
        var usage = GetLastAssistantUsageInfo(messages);
        if (usage is not null)
        {
            var usageTokens = CalculateContextTokens(usage.Value.Usage);
            var trailingTokens = 0;
            for (var index = usage.Value.Index + 1; index < messages.Count; index++)
            {
                trailingTokens += EstimateMessageTokens(messages[index]);
            }

            return new ContextUsageEstimate
            {
                Tokens = usageTokens + trailingTokens,
                UsageTokens = usageTokens,
                TrailingTokens = trailingTokens,
                LastUsageIndex = usage.Value.Index,
            };
        }

        var tokens = messages.Sum(EstimateMessageTokens);
        return new ContextUsageEstimate
        {
            Tokens = tokens,
            UsageTokens = 0,
            TrailingTokens = tokens,
            LastUsageIndex = null,
        };
    }

    private static (Usage Usage, int Index)? GetLastAssistantUsageInfo(IReadOnlyList<Message> messages)
    {
        long latestPrefixTimestamp = long.MinValue;
        (Usage Usage, int Index)? usageInfo = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message is AssistantMessage assistant &&
                assistant.Timestamp >= latestPrefixTimestamp &&
                assistant.StopReason is not StopReasons.Aborted and not StopReasons.Error &&
                CalculateContextTokens(assistant.Usage) > 0)
            {
                usageInfo = (assistant.Usage, index);
            }

            latestPrefixTimestamp = Math.Max(latestPrefixTimestamp, message switch
            {
                UserMessage user => user.Timestamp,
                AssistantMessage assistantMessage => assistantMessage.Timestamp,
                ToolResultMessage result => result.Timestamp,
                _ => long.MinValue,
            });
        }

        return usageInfo;
    }

    private static int EstimateToolsTokens(IEnumerable<Tool> tools)
    {
        var materialized = tools.ToArray();
        if (materialized.Length == 0)
        {
            return 0;
        }

        return EstimateTextTokens(JsonValueUtilities.ToolsToJson(materialized));
    }

    private static string SafeJsonStringify(object value) => JsonValueUtilities.ToJson(value);

    private static string SafeJsonStringify(JsonNode value)
    {
        try
        {
            return value.ToJsonString();
        }
        catch
        {
            return "[unserializable]";
        }
    }
}
