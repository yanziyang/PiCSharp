using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.AgentCore;

/// <summary>Validates and coerces tool-call arguments using the tool JSON Schema.</summary>
public static partial class ToolArguments
{
    private static readonly JsonSerializerOptions _prettyJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Clones, normalizes, coerces, and validates the arguments for a tool call. The returned
    /// object is deliberately mutable: the before-tool-call hook receives this same object and
    /// its mutations are executed without a second validation pass, matching Pi.
    /// </summary>
    public static JsonObject Validate(AgentTool tool, ToolCallLike toolCall)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(toolCall);
        var arguments = toolCall.Arguments.DeepClone().AsObject();
        NormalizeOptionalNulls(arguments, tool.Parameters);
        var coerced = Coerce(arguments, tool.Parameters);
        if (coerced is JsonObject coercedObject)
        {
            arguments = coercedObject;
        }

        var errors = new List<string>();
        ValidateNode(arguments, tool.Parameters, "root", errors);
        if (errors.Count == 0)
        {
            return arguments;
        }

        var formatted = string.Join(
            Environment.NewLine,
            errors.Select(error => $"  - {error}"));
        throw new InvalidOperationException(
            $"Validation failed for tool \"{toolCall.Name}\":{Environment.NewLine}{formatted}"
            + $"{Environment.NewLine}{Environment.NewLine}Received arguments:{Environment.NewLine}"
            + toolCall.Arguments.ToJsonString(_prettyJsonOptions));
    }

    /// <summary>Small tool-call shape used by validation so the helper does not depend on stream events.</summary>
    public sealed record ToolCallLike(string Name, JsonObject Arguments);

    private static JsonNode? Coerce(JsonNode? value, JsonNode schemaNode)
    {
        if (schemaNode is not JsonObject schema)
        {
            return value;
        }

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var nested in allOf.OfType<JsonObject>())
            {
                value = Coerce(value, nested);
            }
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            value = CoerceUnion(value, anyOf);
        }
        else if (schema["oneOf"] is JsonArray oneOf)
        {
            value = CoerceUnion(value, oneOf);
        }

        var types = SchemaTypes(schema);
        if (types.Count > 0 && !types.Any(type => MatchesType(value, type)))
        {
            foreach (var type in types)
            {
                var candidate = CoercePrimitive(value, type);
                if (!ReferenceEquals(candidate, value))
                {
                    value = candidate;
                    break;
                }
            }
        }

        if (value is JsonObject objectValue && types.Contains("object", StringComparer.Ordinal))
        {
            if (schema["properties"] is JsonObject properties)
            {
                foreach (var pair in properties)
                {
                    if (objectValue.ContainsKey(pair.Key) && pair.Value is not null)
                    {
                        objectValue[pair.Key] = Coerce(objectValue[pair.Key], pair.Value);
                    }
                }
            }

            if (schema["additionalProperties"] is JsonObject additionalSchema)
            {
                var defined = schema["properties"] is JsonObject definedProperties
                    ? definedProperties.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in objectValue.Select(pair => pair.Key).ToArray())
                {
                    if (!defined.Contains(key))
                    {
                        objectValue[key] = Coerce(objectValue[key], additionalSchema);
                    }
                }
            }
        }
        else if (value is JsonArray arrayValue && schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < arrayValue.Count; index++)
            {
                arrayValue[index] = Coerce(arrayValue[index], itemSchema);
            }
        }

        return value;
    }

    private static JsonNode? CoerceUnion(JsonNode? value, JsonArray schemas)
    {
        foreach (var schema in schemas.OfType<JsonObject>())
        {
            var candidate = value?.DeepClone();
            candidate = Coerce(candidate, schema);
            if (ValidateNode(candidate, schema, "root", []) == 0)
            {
                return candidate;
            }
        }

        return value;
    }

    private static JsonNode? CoercePrimitive(JsonNode? value, string type)
    {
        if (value is JsonValue jsonValue)
        {
            if (type is "number" or "integer")
            {
                if (jsonValue.TryGetValue<string>(out var text) &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    double.IsFinite(parsed) && (type != "integer" || Math.Truncate(parsed) == parsed))
                {
                    return type == "integer" ? JsonValue.Create((long)parsed) : JsonValue.Create(parsed);
                }

                if (jsonValue.TryGetValue<bool>(out var boolean))
                {
                    return type == "integer"
                        ? JsonValue.Create(boolean ? 1L : 0L)
                        : JsonValue.Create(boolean ? 1D : 0D);
                }

                if (jsonValue.TryGetValue<int>(out var integer))
                {
                    return type == "integer" ? JsonValue.Create((long)integer) : JsonValue.Create((double)integer);
                }

                if (jsonValue.TryGetValue<double>(out var number) && double.IsFinite(number) &&
                    (type != "integer" || Math.Truncate(number) == number))
                {
                    return type == "integer" ? JsonValue.Create((long)number) : JsonValue.Create(number);
                }
            }

            if (type == "boolean")
            {
                if (jsonValue.TryGetValue<string>(out var text))
                {
                    if (text == "true")
                    {
                        return JsonValue.Create(true);
                    }

                    if (text == "false")
                    {
                        return JsonValue.Create(false);
                    }
                }

                if (jsonValue.TryGetValue<int>(out var integer) && integer is 0 or 1)
                {
                    return JsonValue.Create(integer == 1);
                }
            }

            if (type == "string")
            {
                if (jsonValue.TryGetValue<string>(out var text))
                {
                    return JsonValue.Create(text);
                }

                if (jsonValue.TryGetValue<bool>(out var boolean))
                {
                    return JsonValue.Create(boolean ? "true" : "false");
                }

                if (jsonValue.TryGetValue<double>(out var number))
                {
                    return JsonValue.Create(number.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (type == "null" && IsFalsyJsonValue(jsonValue))
            {
                return null;
            }
        }

        return value;
    }

    private static bool IsFalsyJsonValue(JsonValue value) =>
        value.TryGetValue<string>(out var text) && text.Length == 0 ||
        value.TryGetValue<bool>(out var boolean) && !boolean ||
        value.TryGetValue<double>(out var number) && number == 0;

    private static void NormalizeOptionalNulls(JsonNode? value, JsonNode schemaNode)
    {
        if (value is JsonArray array && schemaNode is JsonObject arraySchema && arraySchema["items"] is JsonObject itemSchema)
        {
            foreach (var item in array)
            {
                NormalizeOptionalNulls(item, itemSchema);
            }

            return;
        }

        if (value is not JsonObject objectValue || schemaNode is not JsonObject schema || schema["properties"] is not JsonObject properties)
        {
            return;
        }

        var required = schema["required"] is JsonArray requiredArray
            ? requiredArray.Select(GetString).Where(static item => item is not null).Select(static item => item!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            if (!objectValue.ContainsKey(pair.Key) || pair.Value is null)
            {
                continue;
            }

            if (objectValue[pair.Key] is null && !required.Contains(pair.Key) && !AllowsNull(pair.Value))
            {
                objectValue.Remove(pair.Key);
            }
            else
            {
                NormalizeOptionalNulls(objectValue[pair.Key], pair.Value);
            }
        }
    }

    private static int ValidateNode(JsonNode? value, JsonNode schemaNode, string path, List<string> errors)
    {
        if (schemaNode is not JsonObject schema)
        {
            errors.Add($"{path}: schema must be an object");
            return errors.Count;
        }

        var before = errors.Count;
        if (schema["const"] is JsonNode constant && !JsonEquals(value, constant))
        {
            errors.Add($"{path}: must be equal to constant");
        }

        if (schema["enum"] is JsonArray values && !values.Any(candidate => JsonEquals(value, candidate)))
        {
            errors.Add($"{path}: must be one of the allowed values");
        }

        var types = SchemaTypes(schema);
        if (types.Count > 0 && !types.Any(type => MatchesType(value, type)))
        {
            errors.Add($"{path}: must be {string.Join(" or ", types)}");
            return errors.Count - before;
        }

        if (schema["anyOf"] is JsonArray anyOf && !anyOf.OfType<JsonObject>().Any(candidate => ValidateNode(value?.DeepClone(), candidate, path, []) == 0))
        {
            errors.Add($"{path}: must match at least one schema");
        }

        if (schema["oneOf"] is JsonArray oneOf && oneOf.OfType<JsonObject>().Count(candidate => ValidateNode(value?.DeepClone(), candidate, path, []) == 0) != 1)
        {
            errors.Add($"{path}: must match exactly one schema");
        }

        if (value is JsonObject objectValue && types.Contains("object", StringComparer.Ordinal))
        {
            if (schema["required"] is JsonArray required)
            {
                foreach (var requiredName in required.Select(GetString).Where(static item => item is not null).Select(static item => item!))
                {
                    if (!objectValue.ContainsKey(requiredName))
                    {
                        errors.Add($"{path}.{requiredName}: is required");
                    }
                }
            }

            var properties = schema["properties"] as JsonObject;
            foreach (var pair in objectValue)
            {
                var childPath = path == "root" ? pair.Key : $"{path}.{pair.Key}";
                if (properties?.TryGetPropertyValue(pair.Key, out var propertySchema) == true && propertySchema is not null)
                {
                    ValidateNode(pair.Value, propertySchema, childPath, errors);
                }
                else if (schema["additionalProperties"] is JsonValue additionalProperties &&
                         additionalProperties.TryGetValue<bool>(out var allowsAdditional) && !allowsAdditional)
                {
                    errors.Add($"{childPath}: must NOT have additional properties");
                }
                else if (schema["additionalProperties"] is JsonObject additionalSchema)
                {
                    ValidateNode(pair.Value, additionalSchema, childPath, errors);
                }
            }
        }

        if (value is JsonArray arrayValue && schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < arrayValue.Count; index++)
            {
                ValidateNode(arrayValue[index], itemSchema, $"{path}[{index}]", errors);
            }

            if (GetInt(schema["minItems"]) is { } minItems && arrayValue.Count < minItems)
            {
                errors.Add($"{path}: must NOT have fewer than {minItems} items");
            }
        }

        if (value is JsonValue scalar)
        {
            if (GetStringValue(scalar) is { } text)
            {
                if (GetInt(schema["minLength"]) is { } minLength && text.Length < minLength)
                {
                    errors.Add($"{path}: must NOT have fewer than {minLength} characters");
                }

                if (GetInt(schema["maxLength"]) is { } maxLength && text.Length > maxLength)
                {
                    errors.Add($"{path}: must NOT have more than {maxLength} characters");
                }

                if (GetString(schema["pattern"]) is { } pattern)
                {
                    try
                    {
                        if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant))
                        {
                            errors.Add($"{path}: must match pattern \"{pattern}\"");
                        }
                    }
                    catch (ArgumentException)
                    {
                        errors.Add($"{path}: invalid pattern");
                    }
                }
            }

            if (GetDouble(scalar) is { } number)
            {
                if (GetDouble(schema["minimum"]) is { } minimum && number < minimum)
                {
                    errors.Add($"{path}: must be >= {minimum.ToString(CultureInfo.InvariantCulture)}");
                }

                if (GetDouble(schema["maximum"]) is { } maximum && number > maximum)
                {
                    errors.Add($"{path}: must be <= {maximum.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        return errors.Count - before;
    }

    private static List<string> SchemaTypes(JsonObject schema)
    {
        if (GetString(schema["type"]) is { } type)
        {
            return [type];
        }

        return schema["type"] is JsonArray types
            ? types.Select(GetString).Where(static item => item is not null).Select(static item => item!).ToList()
            : [];
    }

    private static bool MatchesType(JsonNode? value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue json && json.TryGetValue<string>(out _),
        "boolean" => value is JsonValue json && json.TryGetValue<bool>(out _),
        "null" => value is null,
        "number" => GetDouble(value) is not null,
        "integer" => GetDouble(value) is { } number && Math.Truncate(number) == number,
        _ => false,
    };

    private static bool AllowsNull(JsonNode schemaNode)
    {
        if (schemaNode is not JsonObject schema)
        {
            return false;
        }

        if (GetString(schema["type"]) == "null" || SchemaTypes(schema).Contains("null", StringComparer.Ordinal))
        {
            return true;
        }

        if (schema.ContainsKey("const") && schema["const"] is null)
        {
            return true;
        }

        return schema["enum"] is JsonArray values && values.Any(static value => value is null) ||
               schema["anyOf"] is JsonArray anyOf && anyOf.OfType<JsonObject>().Any(AllowsNull);
    }

    private static bool JsonEquals(JsonNode? left, JsonNode? right) =>
        string.Equals(left?.ToJsonString(), right?.ToJsonString(), StringComparison.Ordinal);

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? GetStringValue(JsonValue value) =>
        value.TryGetValue<string>(out var text) ? text : null;

    private static int? GetInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var integer) ? integer : null;

    private static double? GetDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number) && double.IsFinite(number))
        {
            return number;
        }

        return value.TryGetValue<int>(out var integer) ? integer : null;
    }
}
