using System.Globalization;

namespace Pi.Ai;

/// <summary>Provider exception with the status and headers used by SDK retry policies.</summary>
public sealed class ProviderRetryException : Exception
{
    /// <summary>Creates a retry-classifiable provider exception.</summary>
    public ProviderRetryException(
        string message,
        int? status = null,
        IReadOnlyDictionary<string, string>? headers = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Headers = headers;
    }

    /// <summary>HTTP status, when available.</summary>
    public int? Status { get; }

    /// <summary>HTTP response headers, when available.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }
}

/// <summary>Interruptible retry behavior matching the pinned provider SDK policy.</summary>
public static class ProviderRetryUtilities
{
    private const int _defaultMaxRetryDelayMs = 60_000;

    /// <summary>
    /// Repeats a provider request for retryable status/header failures with interruptible backoff.
    /// </summary>
    public static async Task<T> RetryProviderRequest<T>(
        Func<Task<T>> request,
        int maxRetries = 0,
        int? maxRetryDelayMs = null,
        CancellationToken signal = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var retriesRemaining = maxRetries;
        for (; ; )
        {
            try
            {
                return await request().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (signal.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request aborted", signal);
                }

                if (retriesRemaining <= 0 || !TryGetProviderError(error, out var providerError) ||
                    !IsRetryableProviderError(providerError))
                {
                    throw;
                }

                var retryIndex = maxRetries - retriesRemaining;
                retriesRemaining--;
                await AbortableSleep(
                    GetRetryDelayMs(providerError, retryIndex, maxRetryDelayMs),
                    signal).ConfigureAwait(false);
            }
        }
    }

    private static bool TryGetProviderError(Exception error, out ProviderRetryException providerError)
    {
        if (error is ProviderRetryException typed)
        {
            providerError = typed;
            return true;
        }

        if (error is HttpRequestException requestException)
        {
            providerError = new ProviderRetryException(
                requestException.Message,
                requestException.StatusCode is null ? null : (int)requestException.StatusCode.Value);
            return true;
        }

        providerError = null!;
        return false;
    }

    private static bool IsRetryableProviderError(ProviderRetryException error)
    {
        var shouldRetry = GetHeader(error.Headers, "x-should-retry");
        if (string.Equals(shouldRetry, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(shouldRetry, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return error.Status is null || error.Status is 408 or 409 or 429 or >= 500;
    }

    private static int GetRetryDelayMs(
        ProviderRetryException error,
        int retryIndex,
        int? maxRetryDelayMs)
    {
        var retryAfterMs = GetHeader(error.Headers, "retry-after-ms");
        if (double.TryParse(retryAfterMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return ValidateServerRetryDelayMs(milliseconds, maxRetryDelayMs, error.Message);
        }

        var retryAfter = GetHeader(error.Headers, "retry-after");
        if (!string.IsNullOrEmpty(retryAfter))
        {
            double delayMs;
            if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                delayMs = seconds * 1000;
            }
            else if (DateTimeOffset.TryParse(
                         retryAfter,
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                         out var date))
            {
                delayMs = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
            }
            else
            {
                delayMs = 0;
            }

            return ValidateServerRetryDelayMs(delayMs, maxRetryDelayMs, error.Message);
        }

        var exponentialDelay = Math.Min(0.5 * Math.Pow(2, retryIndex), 8) * 1000;
        return (int)Math.Max(0, exponentialDelay * (1 - Random.Shared.NextDouble() * 0.25));
    }

    private static int ValidateServerRetryDelayMs(double delayMs, int? maxRetryDelayMs, string providerErrorMessage)
    {
        var maxDelayMs = maxRetryDelayMs ?? _defaultMaxRetryDelayMs;
        if (maxDelayMs > 0 && delayMs > maxDelayMs)
        {
            throw new InvalidOperationException(
                $"Server requested {Math.Ceiling(delayMs / 1000):0}s retry delay (max: {Math.Ceiling(maxDelayMs / 1000d):0}s). {providerErrorMessage}");
        }

        return (int)Math.Max(0, Math.Min(int.MaxValue, delayMs));
    }

    private static async Task AbortableSleep(int milliseconds, CancellationToken signal)
    {
        try
        {
            await Task.Delay(Math.Max(0, milliseconds), signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Request aborted", signal);
        }
    }

    private static string? GetHeader(
        IReadOnlyDictionary<string, string>? headers,
        string name)
    {
        if (headers is null)
        {
            return null;
        }

        return headers.TryGetValue(name, out var value) ? value :
            headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
