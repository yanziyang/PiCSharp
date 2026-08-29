namespace Pi.Ai;

/// <summary>Result of separating immediate and transcript-loaded tool definitions.</summary>
public sealed record DeferredToolSplit(
    IReadOnlyList<Tool> Immediate,
    IReadOnlyDictionary<string, Tool> Deferred);

/// <summary>Helpers for Pi's deferred tool loading behavior.</summary>
public static class DeferredToolUtilities
{
    /// <summary>
    /// Splits current tools into immediate definitions and tools introduced by prior tool results.
    /// Duplicate normalized names keep the last definition, matching JavaScript Map semantics.
    /// </summary>
    public static DeferredToolSplit SplitDeferredTools(
        Context context,
        bool enabled,
        Func<string, string>? normalizeName = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        normalizeName ??= static name => name;

        var uniqueTools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var tool in context.Tools)
        {
            uniqueTools[normalizeName(tool.Name)] = tool;
        }

        if (!enabled)
        {
            return new DeferredToolSplit(uniqueTools.Values.ToArray(), new Dictionary<string, Tool>(StringComparer.Ordinal));
        }

        var deferredNames = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in context.Messages)
        {
            if (message is AssistantMessage assistant)
            {
                foreach (var block in assistant.Content.OfType<ToolCall>())
                {
                    usedNames.Add(normalizeName(block.Name));
                }
            }
            else if (message is ToolResultMessage toolResult)
            {
                foreach (var name in toolResult.AddedToolNames ?? [])
                {
                    var normalizedName = normalizeName(name);
                    if (!usedNames.Contains(normalizedName))
                    {
                        deferredNames.Add(normalizedName);
                    }
                }
            }
        }

        var immediate = new List<Tool>();
        var deferred = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var pair in uniqueTools)
        {
            if (deferredNames.Contains(pair.Key))
            {
                deferred[pair.Key] = pair.Value;
            }
            else
            {
                immediate.Add(pair.Value);
            }
        }

        return new DeferredToolSplit(immediate, deferred);
    }
}
