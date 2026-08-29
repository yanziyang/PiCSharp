namespace Pi.Ai;

/// <summary>Pure model metadata and pricing helpers shared by provider/runtime code.</summary>
public static class ModelUtilities
{
    private static readonly string[] _extendedThinkingLevels =
    [
        ThinkingLevels.Off,
        ThinkingLevels.Minimal,
        ThinkingLevels.Low,
        ThinkingLevels.Medium,
        ThinkingLevels.High,
        ThinkingLevels.XHigh,
        ThinkingLevels.Max,
    ];

    /// <summary>Checks an open model API identifier at runtime.</summary>
    public static bool HasApi(Model model, string api)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(api);
        return string.Equals(model.Api, api, StringComparison.Ordinal);
    }

    /// <summary>
    /// Calculates the cost breakdown in dollars and mutates the usage cost object, matching Pi's
    /// request-wide tier and one-hour cache-write rules.
    /// </summary>
    public static UsageCost CalculateCost(Model model, Usage usage)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        var inputTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        ModelCostRates rates = model.Cost;
        var matchedThreshold = -1;
        foreach (var tier in model.Cost.Tiers)
        {
            if (inputTokens > tier.InputTokensAbove && tier.InputTokensAbove > matchedThreshold)
            {
                rates = tier;
                matchedThreshold = tier.InputTokensAbove;
            }
        }

        // Anthropic charges two times base input for one-hour cache writes.
        var longWrite = usage.CacheWrite1h ?? 0;
        var shortWrite = usage.CacheWrite - longWrite;
        usage.Cost.Input = rates.Input / 1_000_000 * usage.Input;
        usage.Cost.Output = rates.Output / 1_000_000 * usage.Output;
        usage.Cost.CacheRead = rates.CacheRead / 1_000_000 * usage.CacheRead;
        usage.Cost.CacheWrite = (rates.CacheWrite * shortWrite + rates.Input * 2 * longWrite) / 1_000_000;
        usage.Cost.Total = usage.Cost.Input + usage.Cost.Output + usage.Cost.CacheRead + usage.Cost.CacheWrite;
        return usage.Cost;
    }

    /// <summary>Returns the Pi thinking levels supported by a model.</summary>
    public static IReadOnlyList<string> GetSupportedThinkingLevels(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Reasoning)
        {
            return [ThinkingLevels.Off];
        }

        return _extendedThinkingLevels
            .Where(level =>
            {
                var mapped = model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(level, out var value)
                    ? value
                    : null;
                var hasMapping = model.ThinkingLevelMap is not null && model.ThinkingLevelMap.ContainsKey(level);
                if (mapped is null && hasMapping)
                {
                    return false;
                }

                return level is not (ThinkingLevels.XHigh or ThinkingLevels.Max) || hasMapping;
            })
            .ToArray();
    }

    /// <summary>Clamps an unavailable thinking level to the nearest supported level.</summary>
    public static string ClampThinkingLevel(Model model, string level)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(level);
        var available = GetSupportedThinkingLevels(model);
        if (available.Contains(level, StringComparer.Ordinal))
        {
            return level;
        }

        var requestedIndex = Array.IndexOf(_extendedThinkingLevels, level);
        if (requestedIndex < 0)
        {
            return available.Count > 0 ? available[0] : ThinkingLevels.Off;
        }

        for (var index = requestedIndex; index < _extendedThinkingLevels.Length; index++)
        {
            if (available.Contains(_extendedThinkingLevels[index], StringComparer.Ordinal))
            {
                return _extendedThinkingLevels[index];
            }
        }

        for (var index = requestedIndex - 1; index >= 0; index--)
        {
            if (available.Contains(_extendedThinkingLevels[index], StringComparer.Ordinal))
            {
                return _extendedThinkingLevels[index];
            }
        }

        return available.Count > 0 ? available[0] : ThinkingLevels.Off;
    }

    /// <summary>Compares models by provider and model identifier.</summary>
    public static bool ModelsAreEqual(Model? first, Model? second) =>
        first is not null && second is not null &&
        string.Equals(first.Id, second.Id, StringComparison.Ordinal) &&
        string.Equals(first.Provider, second.Provider, StringComparison.Ordinal);
}
