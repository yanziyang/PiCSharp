using System.Text.RegularExpressions;

namespace Pi.Ai;

/// <summary>Bounded retry policy for assistant-producing operations.</summary>
public sealed record RetryPolicy
{
    /// <summary>Whether retries are enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Maximum retry attempts after the initial call.</summary>
    public int MaxRetries { get; init; }

    /// <summary>Base backoff duration in milliseconds.</summary>
    public int BaseDelayMs { get; init; }
}

/// <summary>Callbacks emitted around assistant retry backoff.</summary>
public sealed class RetryCallbacks
{
    /// <summary>Called before each retry backoff.</summary>
    public Func<int, int, int, string, Task>? OnRetryScheduled { get; init; }

    /// <summary>Called after backoff and before the retried call starts.</summary>
    public Func<Task>? OnRetryAttemptStart { get; init; }

    /// <summary>Called once when the retry loop completes.</summary>
    public Func<bool, int, string?, Task>? OnRetryFinished { get; init; }
}

/// <summary>Assistant retry classification and execution helpers.</summary>
public static partial class RetryUtilities
{
    private static readonly Regex[] _nonRetryablePatterns =
    [
        new("GoUsageLimitError", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("FreeUsageLimitError", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("Monthly usage limit reached", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("available balance", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("insufficient_quota", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("out of budget", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("quota exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("billing", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    private static readonly Regex[] _retryablePatterns =
    [
        new("overloaded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("rate.?limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("too many requests", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("429", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("500", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("502", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("503", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("504", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("524", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("service.?unavailable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("server.?error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("internal.?error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("provider.?returned.?error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("exceeded request buffer limit while retrying upstream", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("network.?error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("connection.?error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("connection.?refused", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("connection.?lost", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("other side closed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("fetch failed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("getaddrinfo", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("ENOTFOUND", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("EAI_AGAIN", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("upstream.?connect", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("reset before headers", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("socket hang up", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("socket connection was closed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("timed? out", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("timeout", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("terminated", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("websocket.?closed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("websocket.?error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("ended without", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("stream ended before message_stop", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("stream ended before a terminal response event", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("http2 request did not get a response", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("retry delay", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("you can retry your request", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("try your request again", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("please retry your request", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("ResourceExhausted", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>Runs an assistant call with the pinned bounded retry policy.</summary>
    public static async Task<AssistantMessage> RetryAssistantCall(
        Func<Task<AssistantMessage>> produce,
        RetryPolicy? policy,
        RetryCallbacks? callbacks = null,
        CancellationToken signal = default)
    {
        ArgumentNullException.ThrowIfNull(produce);
        var maxAttempts = policy is { Enabled: true } ? Math.Max(0, policy.MaxRetries) : 0;
        var attempt = 0;
        (int Attempt, string ErrorMessage)? lastRetry = null;

        for (; ; )
        {
            var response = await produce().ConfigureAwait(false);
            if (response.StopReason == StopReasons.Aborted)
            {
                if (lastRetry is not null)
                {
                    await InvokeFinished(callbacks, false, lastRetry.Value.Attempt, null).ConfigureAwait(false);
                }

                return response;
            }

            if (response.StopReason != StopReasons.Error)
            {
                if (lastRetry is not null)
                {
                    await InvokeFinished(callbacks, true, lastRetry.Value.Attempt, null).ConfigureAwait(false);
                }

                return response;
            }

            if (attempt >= maxAttempts || !IsRetryableAssistantError(response))
            {
                if (lastRetry is not null)
                {
                    await InvokeFinished(callbacks, false, lastRetry.Value.Attempt, response.ErrorMessage).ConfigureAwait(false);
                }

                return response;
            }

            attempt++;
            lastRetry = (attempt, string.IsNullOrEmpty(response.ErrorMessage) ? "Unknown error" : response.ErrorMessage);
            var delayMs = (int)Math.Min(int.MaxValue, policy!.BaseDelayMs * Math.Pow(2, attempt - 1));
            if (callbacks?.OnRetryScheduled is not null)
            {
                await callbacks.OnRetryScheduled(attempt, maxAttempts, delayMs, lastRetry.Value.ErrorMessage).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(Math.Max(0, delayMs), signal).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await InvokeFinished(callbacks, false, attempt, lastRetry.Value.ErrorMessage).ConfigureAwait(false);
                return response with { StopReason = StopReasons.Aborted, ErrorMessage = null };
            }

            if (callbacks?.OnRetryAttemptStart is not null)
            {
                await callbacks.OnRetryAttemptStart().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Returns whether an assistant error looks transient and retryable.</summary>
    public static bool IsRetryableAssistantError(AssistantMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.StopReason != StopReasons.Error || string.IsNullOrEmpty(message.ErrorMessage))
        {
            return false;
        }

        return !_nonRetryablePatterns.Any(pattern => pattern.IsMatch(message.ErrorMessage)) &&
               _retryablePatterns.Any(pattern => pattern.IsMatch(message.ErrorMessage));
    }

    private static Task InvokeFinished(
        RetryCallbacks? callbacks,
        bool success,
        int attempt,
        string? finalError) =>
        callbacks?.OnRetryFinished is null
            ? Task.CompletedTask
            : callbacks.OnRetryFinished(success, attempt, finalError);
}
