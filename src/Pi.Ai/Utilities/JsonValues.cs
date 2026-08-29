using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Ai;

internal static class JsonValueUtilities
{
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool boolean => JsonValue.Create(boolean),
        byte number => JsonValue.Create(number),
        sbyte number => JsonValue.Create(number),
        short number => JsonValue.Create(number),
        ushort number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        uint number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        ulong number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        IReadOnlyDictionary<string, string?> dictionary => ToStringDictionaryNode(dictionary),
        IDictionary dictionary => ToDictionaryNode(dictionary),
        IEnumerable enumerable => ToArrayNode(enumerable),
        _ => JsonValue.Create(value.ToString()),
    };

    public static string ToJson(object? value) => ToNode(value)?.ToJsonString() ?? "null";

    public static string ToolsToJson(IEnumerable<Tool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var node = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
            };
            if (tool.ConstrainedSampling is JsonSchemaSampling jsonSchema)
            {
                node["constrainedSampling"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["strict"] = jsonSchema.Strict,
                };
            }
            else if (tool.ConstrainedSampling is GrammarSampling grammar)
            {
                var variants = new JsonObject();
                foreach (var pair in grammar.Variants)
                {
                    variants[pair.Key] = pair.Value;
                }

                node["constrainedSampling"] = new JsonObject
                {
                    ["type"] = "grammar",
                    ["variants"] = variants,
                };
            }

            array.Add((JsonNode?)node);
        }

        return array.ToJsonString();
    }

    private static JsonObject ToStringDictionaryNode(IReadOnlyDictionary<string, string?> dictionary)
    {
        var result = new JsonObject();
        foreach (var pair in dictionary)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static JsonObject ToDictionaryNode(IDictionary dictionary)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key)
            {
                result[key] = ToNode(entry.Value);
            }
        }

        return result;
    }

    private static JsonArray ToArrayNode(IEnumerable enumerable)
    {
        var result = new JsonArray();
        foreach (var item in enumerable)
        {
            result.Add(ToNode(item));
        }

        return result;
    }
}
