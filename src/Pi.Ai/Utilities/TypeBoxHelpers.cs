using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Helpers for provider-compatible JSON Schema construction.</summary>
public static class TypeBoxHelpers
{
    /// <summary>
    /// Creates a string enum schema using an explicit <c>enum</c> array, which is accepted by
    /// Google and providers that do not support TypeBox's <c>anyOf</c>/<c>const</c> output.
    /// </summary>
    public static JsonObject StringEnum(
        IReadOnlyList<string> values,
        string? description = null,
        string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var schema = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(static value => (JsonNode?)value).ToArray()),
        };
        if (!string.IsNullOrEmpty(description))
        {
            schema["description"] = description;
        }

        if (!string.IsNullOrEmpty(defaultValue))
        {
            schema["default"] = defaultValue;
        }

        return schema;
    }
}
