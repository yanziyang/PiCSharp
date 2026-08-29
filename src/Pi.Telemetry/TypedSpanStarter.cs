namespace Pi.Telemetry;

/// <summary>
/// Runtime equivalent of the TypeScript schema-bound span starter.
/// </summary>
public sealed class TypedSpanStarter
{
    private readonly TelemetryContext _telemetryContext;

    /// <summary>Creates a starter bound to a telemetry context and schema vocabulary.</summary>
    public TypedSpanStarter(
        TelemetryContext telemetryContext,
        IReadOnlyList<TelemetrySchemaDefinition> schemas)
    {
        _telemetryContext = telemetryContext ?? throw new ArgumentNullException(nameof(telemetryContext));
        Schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
    }

    /// <summary>The schemas supplied when this starter was created.</summary>
    public IReadOnlyList<TelemetrySchemaDefinition> Schemas { get; }

    /// <summary>Starts a named span with a synchronous callback.</summary>
    public Task<T> StartSpan<T>(
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, TypedSpanStarter, T> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(callback);

        return _telemetryContext.StartSpan(
            new SpanOptions(name, attributes),
            span => callback(span, new TypedSpanStarter(span, Schemas)),
            cancellationToken);
    }

    /// <summary>Starts a named span with an asynchronous callback.</summary>
    public Task<T> StartSpanAsync<T>(
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, TypedSpanStarter, Task<T>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(callback);

        return _telemetryContext.StartSpanAsync(
            new SpanOptions(name, attributes),
            span => callback(span, new TypedSpanStarter(span, Schemas)),
            cancellationToken);
    }
}
