using System.Diagnostics.CodeAnalysis;

namespace Pi.Telemetry;

/// <summary>A scalar or homogeneous array value accepted as telemetry data.</summary>
public readonly record struct AttributeValue(object? Value)
{
    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(string value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(bool value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(int value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(long value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(double value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(string[] value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(bool[] value) => new(value);

    /// <summary>Creates a telemetry attribute value from a supported CLR value.</summary>
    public static implicit operator AttributeValue(double[] value) => new(value);
}

/// <summary>Attribute values attached to a telemetry span or event.</summary>
public sealed class SpanAttributes : Dictionary<string, object?>
{
    /// <summary>Creates an ordinal-keyed attribute collection.</summary>
    public SpanAttributes() : base(StringComparer.Ordinal)
    {
    }

    /// <summary>Creates an ordinal-keyed attribute collection from existing values.</summary>
    public SpanAttributes(IEnumerable<KeyValuePair<string, object?>> values) : this()
    {
        foreach (var value in values)
        {
            Add(value.Key, value.Value);
        }
    }
}

/// <summary>Options used when a telemetry span is started.</summary>
public sealed record SpanOptions
{
    /// <summary>Creates span options.</summary>
    public SpanOptions(string name, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        Name = name;
        Attributes = attributes;
    }

    /// <summary>The logical span name.</summary>
    public string Name { get; }

    /// <summary>Initial span attributes.</summary>
    public IReadOnlyDictionary<string, object?>? Attributes { get; }
}

/// <summary>Details associated with an error span status.</summary>
public sealed record SpanError(string Name, string Message);

/// <summary>The final status of a telemetry span.</summary>
public abstract record SpanStatus
{
    private SpanStatus()
    {
    }

    /// <summary>A successful span status.</summary>
    public sealed record Ok : SpanStatus;

    /// <summary>An unsuccessful span status, optionally with error details.</summary>
    public sealed record Error(SpanError? Details = null) : SpanStatus;
}

/// <summary>Context used to create nested telemetry spans.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi telemetry type name.")]
public interface TelemetryContext
{
    /// <summary>Starts a span and invokes a synchronous callback while it is active.</summary>
    Task<T> StartSpan<T>(
        SpanOptions options,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a span and invokes an asynchronous callback while it is active.</summary>
    Task<T> StartSpanAsync<T>(
        SpanOptions options,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken = default);
}

/// <summary>A telemetry span that can create children and record events and attributes.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi telemetry type name.")]
public interface TelemetrySpan : TelemetryContext
{
    /// <summary>Records an ordered event unless the span has settled.</summary>
    void AddEvent(string name, IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>Merges attributes into the span unless the span has settled.</summary>
    void SetAttributes(IReadOnlyDictionary<string, object?> attributes);

    /// <summary>Sets the explicit span status unless the span has settled.</summary>
    void SetStatus(SpanStatus status);
}

/// <summary>Definition of a telemetry attribute in a serializable schema.</summary>
public sealed record TelemetryAttributeDefinition
{
    /// <summary>Creates an attribute definition.</summary>
    public TelemetryAttributeDefinition(
        string type,
        string description,
        bool sensitive = false,
        string? cardinality = null,
        IReadOnlyList<object?>? values = null,
        IReadOnlyList<object?>? examples = null,
        IReadOnlyList<object?>? elementValues = null)
    {
        Type = type;
        Description = description;
        Sensitive = sensitive;
        Cardinality = cardinality;
        Values = values;
        Examples = examples;
        ElementValues = elementValues;
    }

    /// <summary>The schema type, such as <c>string</c> or <c>number[]</c>.</summary>
    public string Type { get; }

    /// <summary>Human-readable attribute description.</summary>
    public string Description { get; }

    /// <summary>Whether the attribute may contain sensitive data.</summary>
    public bool Sensitive { get; }

    /// <summary>Declared cardinality, when present.</summary>
    public string? Cardinality { get; }

    /// <summary>Allowed scalar values, when present.</summary>
    public IReadOnlyList<object?>? Values { get; }

    /// <summary>Example scalar values, when present.</summary>
    public IReadOnlyList<object?>? Examples { get; }

    /// <summary>Allowed values for an array element, when present.</summary>
    public IReadOnlyList<object?>? ElementValues { get; }
}

/// <summary>An attribute definition with requiredness.</summary>
public sealed record TelemetryRequiredAttributeDefinition(
    TelemetryAttributeDefinition Definition,
    bool Required);

/// <summary>Definition of a named telemetry event.</summary>
public sealed record TelemetryEventDefinition(
    string Description,
    IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> Attributes);

/// <summary>Parent constraint for a telemetry span definition.</summary>
public abstract record TelemetryParentDefinition
{
    private TelemetryParentDefinition()
    {
    }

    /// <summary>Allows any parent.</summary>
    public sealed record Any : TelemetryParentDefinition;

    /// <summary>Allows a root or externally-created parent.</summary>
    public sealed record RootOrExternal : TelemetryParentDefinition;

    /// <summary>Allows only the named parent spans.</summary>
    public sealed record Spans(IReadOnlyList<string> Names) : TelemetryParentDefinition;
}

/// <summary>Definition of one named telemetry span.</summary>
public sealed record TelemetrySpanDefinition(
    string Description,
    TelemetryParentDefinition Parents,
    IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> StartAttributes,
    IReadOnlyDictionary<string, TelemetryAttributeDefinition> EndAttributes,
    IReadOnlyDictionary<string, TelemetryEventDefinition>? Events,
    string ErrorWhen);

/// <summary>Serializable telemetry schema definition.</summary>
public sealed record TelemetrySchemaDefinition(
    int Version,
    IReadOnlyDictionary<string, TelemetrySpanDefinition> Spans);

/// <summary>Helpers for preserving telemetry schema values and creating runtime starters.</summary>
public static class TelemetrySchema
{
    /// <summary>Returns the supplied schema unchanged.</summary>
    public static TelemetrySchemaDefinition Define(TelemetrySchemaDefinition schema) => schema;

    /// <summary>
    /// Creates a runtime span starter bound to the supplied telemetry context and schemas.
    /// C# cannot reproduce TypeScript's conditional and mapped type inference; schemas are retained
    /// for inspection while span names and attributes remain runtime values.
    /// </summary>
    public static TypedSpanStarter CreateTypedSpanStarter(
        TelemetryContext telemetryContext,
        IReadOnlyList<TelemetrySchemaDefinition> schemas) =>
        new(telemetryContext, schemas);
}

/// <summary>Detached event snapshot returned by an in-memory telemetry context.</summary>
public sealed record RecordedTelemetryEvent(
    string Name,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>Detached span snapshot returned by an in-memory telemetry context.</summary>
public sealed record RecordedTelemetrySpan(
    int Id,
    int? ParentId,
    string Name,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<RecordedTelemetryEvent> Events,
    SpanStatus Status,
    bool Settled,
    int? EndSequence);
