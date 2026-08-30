using System.Collections;

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
        ArgumentNullException.ThrowIfNull(schemas);
        Schemas = schemas.ToArray();
        ValidateSchemaNames(Schemas);
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

        var definition = FindSpanDefinition(name);
        ValidateAttributes(name, attributes, definition.StartAttributes);
        return _telemetryContext.StartSpan(
            new SpanOptions(name, attributes),
            span =>
            {
                var validatingSpan = new ValidatingTelemetrySpan(span, name, definition);
                return callback(validatingSpan, new TypedSpanStarter(validatingSpan, Schemas));
            },
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

        var definition = FindSpanDefinition(name);
        ValidateAttributes(name, attributes, definition.StartAttributes);
        return _telemetryContext.StartSpanAsync(
            new SpanOptions(name, attributes),
            span =>
            {
                var validatingSpan = new ValidatingTelemetrySpan(span, name, definition);
                return callback(validatingSpan, new TypedSpanStarter(validatingSpan, Schemas));
            },
            cancellationToken);
    }

    private TelemetrySpanDefinition FindSpanDefinition(string name)
    {
        foreach (var schema in Schemas)
        {
            if (schema.Spans.TryGetValue(name, out var definition))
            {
                return definition;
            }
        }

        throw new ArgumentException($"Unknown telemetry span '{name}'.", nameof(name));
    }

    private static void ValidateSchemaNames(IReadOnlyList<TelemetrySchemaDefinition> schemas)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            ArgumentNullException.ThrowIfNull(schema);
            foreach (var name in schema.Spans.Keys)
            {
                if (!names.Add(name))
                {
                    throw new ArgumentException($"Duplicate telemetry span name '{name}'.", nameof(schemas));
                }
            }
        }
    }

    private static void ValidateAttributes(
        string spanName,
        IReadOnlyDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (definition.Value.Required && !attributes.ContainsKey(definition.Key))
            {
                throw new ArgumentException(
                    $"Telemetry span '{spanName}' is missing required attribute '{definition.Key}'.",
                    nameof(attributes));
            }
        }

        foreach (var attribute in attributes)
        {
            if (!definitions.TryGetValue(attribute.Key, out var definition))
            {
                throw new ArgumentException(
                    $"Telemetry span '{spanName}' does not declare attribute '{attribute.Key}'.",
                    nameof(attributes));
            }

            if (attribute.Value is null)
            {
                throw new ArgumentException(
                    $"Telemetry attribute '{attribute.Key}' on span '{spanName}' cannot be null.",
                    nameof(attributes));
            }

            ValidateAttributeValue(spanName, attribute.Key, attribute.Value, definition.Definition);
        }
    }

    private static void ValidateAttributes(
        string spanName,
        IReadOnlyDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, TelemetryAttributeDefinition> definitions)
    {
        foreach (var attribute in attributes)
        {
            if (!definitions.TryGetValue(attribute.Key, out var definition))
            {
                throw new ArgumentException(
                    $"Telemetry span '{spanName}' does not declare end attribute '{attribute.Key}'.",
                    nameof(attributes));
            }

            if (attribute.Value is null)
            {
                throw new ArgumentException(
                    $"Telemetry attribute '{attribute.Key}' on span '{spanName}' cannot be null.",
                    nameof(attributes));
            }

            ValidateAttributeValue(spanName, attribute.Key, attribute.Value, definition);
        }
    }

    private static void ValidateAttributeValue(
        string spanName,
        string attributeName,
        object value,
        TelemetryAttributeDefinition definition)
    {
        if (!MatchesType(value, definition.Type))
        {
            throw new ArgumentException(
                $"Telemetry attribute '{attributeName}' on span '{spanName}' must have type '{definition.Type}'.",
                nameof(value));
        }

        if (definition.Type.EndsWith("[]", StringComparison.Ordinal))
        {
            if (definition.ElementValues is not null)
            {
                foreach (var item in (IEnumerable)value)
                {
                    if (!ContainsValue(definition.ElementValues, item))
                    {
                        throw new ArgumentException(
                            $"Telemetry attribute '{attributeName}' on span '{spanName}' contains a value outside its schema.",
                            nameof(value));
                    }
                }
            }
        }
        else if (definition.Values is not null && !ContainsValue(definition.Values, value))
        {
            throw new ArgumentException(
                $"Telemetry attribute '{attributeName}' on span '{spanName}' has a value outside its schema.",
                nameof(value));
        }
    }

    private static bool MatchesType(object value, string type) => type switch
    {
        "string" => value is string,
        "number" => IsNumber(value),
        "boolean" => value is bool,
        "string[]" => IsArrayOf(value, static item => item is string),
        "number[]" => IsArrayOf(value, IsNumber),
        "boolean[]" => IsArrayOf(value, static item => item is bool),
        _ => false,
    };

    private static bool IsArrayOf(object value, Func<object?, bool> itemPredicate)
    {
        if (value is string or not IEnumerable enumerable)
        {
            return false;
        }

        foreach (var item in enumerable)
        {
            if (!itemPredicate(item))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumber(object? value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool ContainsValue(IReadOnlyList<object?> values, object? candidate)
    {
        foreach (var value in values)
        {
            if (value is not null && candidate is not null && IsNumber(value) && IsNumber(candidate))
            {
                if (Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) ==
                    Convert.ToDecimal(candidate, System.Globalization.CultureInfo.InvariantCulture))
                {
                    return true;
                }
            }
            else if (Equals(value, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ValidatingTelemetrySpan(
        TelemetrySpan inner,
        string name,
        TelemetrySpanDefinition definition) : TelemetrySpan
    {
        public Task<T> StartSpan<T>(
            SpanOptions options,
            Func<TelemetrySpan, T> callback,
            CancellationToken cancellationToken = default) =>
            inner.StartSpan(options, callback, cancellationToken);

        public Task<T> StartSpanAsync<T>(
            SpanOptions options,
            Func<TelemetrySpan, Task<T>> callback,
            CancellationToken cancellationToken = default) =>
            inner.StartSpanAsync(options, callback, cancellationToken);

        public void AddEvent(string eventName, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            if (definition.Events is null || !definition.Events.TryGetValue(eventName, out var eventDefinition))
            {
                throw new ArgumentException($"Telemetry span '{name}' does not declare event '{eventName}'.", nameof(eventName));
            }

            var actualAttributes = attributes ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            ValidateAttributes(name, actualAttributes, eventDefinition.Attributes);
            inner.AddEvent(eventName, attributes);
        }

        public void SetAttributes(IReadOnlyDictionary<string, object?> attributes)
        {
            ArgumentNullException.ThrowIfNull(attributes);
            ValidateAttributes(name, attributes, definition.EndAttributes);
            inner.SetAttributes(attributes);
        }

        public void SetStatus(SpanStatus status) => inner.SetStatus(status);
    }
}
