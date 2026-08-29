namespace Pi.Telemetry;

/// <summary>Shared inert telemetry context used when recording is not configured.</summary>
public sealed class NoopTelemetryContext : TelemetrySpan
{
    /// <summary>The process-wide inert telemetry context.</summary>
    public static NoopTelemetryContext Instance { get; } = new();

    private NoopTelemetryContext()
    {
    }

    /// <inheritdoc />
    public Task<T> StartSpan<T>(
        SpanOptions options,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken = default)
    {
        _ = options;
        _ = cancellationToken;

        try
        {
            return Task.FromResult(callback(this));
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    /// <inheritdoc />
    public Task<T> StartSpanAsync<T>(
        SpanOptions options,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken = default)
    {
        _ = options;
        _ = cancellationToken;

        try
        {
            return callback(this);
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    /// <inheritdoc />
    public void AddEvent(string name, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        _ = name;
        _ = attributes;
    }

    /// <inheritdoc />
    public void SetAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        _ = attributes;
    }

    /// <inheritdoc />
    public void SetStatus(SpanStatus status)
    {
        _ = status;
    }
}

/// <summary>Compatibility holder for the shared no-op telemetry context.</summary>
public static class TelemetryDefaults
{
    /// <summary>The shared inert context.</summary>
    public static TelemetryContext Noop => NoopTelemetryContext.Instance;
}
