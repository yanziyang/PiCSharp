using System.Text.RegularExpressions;

namespace Pi.Ai;

/// <summary>Detects provider responses that indicate context-window overflow.</summary>
public static partial class OverflowUtilities
{
    private static readonly Regex[] _overflowPatterns =
    [
        new("prompt is too long", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("request_too_large", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("input is too long for requested model", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeds the context window", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\\d,]+ tokens?|\\s*\\([\\d,]+\\))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("input token count.*exceeds the maximum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("maximum prompt length is \\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("reduce the length of the messages", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("maximum context length is \\d+ tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeds (?:the )?maximum allowed input length of [\\d,]+ tokens?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("input \\(\\d+ tokens\\) is longer than the model'?s context length \\(\\d+ tokens\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeds the limit of \\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeds the available context size", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("greater than the context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("context window exceeds limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeded model token limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("too large for model with \\d+ maximum context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("prompt has [\\d,]+ tokens?, but the configured context size is [\\d,]+ tokens?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("model_context_window_exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("range of input length should be", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("too many tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("token limit exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("^4(?:00|13)\\s*(?:status code)?\\s*\\(no body\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    private static readonly Regex[] _nonOverflowPatterns =
    [
        new("^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("rate limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("too many requests", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>Returns whether the assistant response indicates context overflow.</summary>
    public static bool IsContextOverflow(AssistantMessage message, int? contextWindow = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var errorMessage = message.ErrorMessage;
        if (message.StopReason == StopReasons.Error && !string.IsNullOrEmpty(errorMessage))
        {
            if (!_nonOverflowPatterns.Any(pattern => pattern.IsMatch(errorMessage)) &&
                _overflowPatterns.Any(pattern => pattern.IsMatch(errorMessage)))
            {
                return true;
            }
        }

        if (contextWindow is > 0 && message.StopReason == StopReasons.Stop)
        {
            var inputTokens = message.Usage.Input + message.Usage.CacheRead;
            if (inputTokens > contextWindow.Value)
            {
                return true;
            }
        }

        if (contextWindow is > 0 && message.StopReason == StopReasons.Length && message.Usage.Output == 0)
        {
            var inputTokens = message.Usage.Input + message.Usage.CacheRead;
            if (inputTokens >= contextWindow.Value * 0.99)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a length stop ended below the caller's desired output limit and may be
    /// recovered by one bounded compact-and-retry attempt.
    /// </summary>
    public static bool IsRecoverableLength(AssistantMessage message, int desiredMaxOutput) =>
        message.StopReason == StopReasons.Length && desiredMaxOutput > 0 && message.Usage.Output < desiredMaxOutput;

    /// <summary>Returns copies of the overflow patterns for diagnostic tests.</summary>
    public static IReadOnlyList<Regex> GetOverflowPatterns() => _overflowPatterns.ToArray();
}
