using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Utils;
using Pi.Ai;

namespace Pi.AgentCore.Harness.Tools;

internal static class ToolHelpers
{
    public static JsonObject RequireObject(JsonNode parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters as JsonObject ?? throw new ArgumentException("Tool parameters must be a JSON object.", nameof(parameters));
    }

    public static string RequireString(JsonObject parameters, string name)
    {
        if (!parameters.TryGetPropertyValue(name, out var node) || node is null)
        {
            throw new ArgumentException($"Tool parameter '{name}' is required.", nameof(parameters));
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception error)
        {
            throw new ArgumentException($"Tool parameter '{name}' must be a string.", nameof(parameters), error);
        }
    }

    public static string? OptionalString(JsonObject parameters, string name)
    {
        if (!parameters.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception error)
        {
            throw new ArgumentException($"Tool parameter '{name}' must be a string.", nameof(parameters), error);
        }
    }

    public static double? OptionalNumber(JsonObject parameters, string name)
    {
        if (!parameters.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<decimal>(out var decimalValue))
            {
                return (double)decimalValue;
            }
        }

        throw new ArgumentException($"Tool parameter '{name}' must be a number.", nameof(parameters));
    }

    public static AgentToolResult TextResult(string text, JsonNode? details = null) => new()
    {
        Content = [new TextContent(text)],
        Details = details,
    };

    public static JsonObject TruncationDetails(TruncationResult truncation)
    {
        return new JsonObject
        {
            ["content"] = truncation.Content,
            ["truncated"] = truncation.Truncated,
            ["truncatedBy"] = truncation.TruncatedBy,
            ["totalLines"] = truncation.TotalLines,
            ["totalBytes"] = truncation.TotalBytes,
            ["outputLines"] = truncation.OutputLines,
            ["outputBytes"] = truncation.OutputBytes,
            ["lastLinePartial"] = truncation.LastLinePartial,
            ["firstLineExceedsLimit"] = truncation.FirstLineExceedsLimit,
            ["maxLines"] = truncation.MaxLines,
            ["maxBytes"] = truncation.MaxBytes,
        };
    }

    public static JsonObject BashDetails(TruncationResult? truncation, string? fullOutputPath)
    {
        var details = new JsonObject();
        if (truncation is not null)
        {
            details["truncation"] = TruncationDetails(truncation);
        }

        if (fullOutputPath is not null)
        {
            details["fullOutputPath"] = fullOutputPath;
        }

        return details;
    }

    public static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var propertyObject = new JsonObject();
        var required = new JsonArray();
        foreach (var property in properties)
        {
            propertyObject[property.Name] = new JsonObject
            {
                ["type"] = property.Type,
                ["description"] = property.Description,
            };
            if (property.Required)
            {
                required.Add((JsonNode)JsonValue.Create(property.Name)!);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertyObject,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    public static void ThrowIfAborted(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Operation aborted");
        }
    }

    public static string NumberString(double value) => value.ToString(CultureInfo.InvariantCulture);
}
