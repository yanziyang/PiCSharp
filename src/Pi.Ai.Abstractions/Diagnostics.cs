using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Serializable error information attached to an assistant diagnostic.</summary>
public sealed record DiagnosticErrorInfo
{
    /// <summary>Error type name.</summary>
    public string? Name { get; init; }

    /// <summary>Error message.</summary>
    public required string Message { get; init; }

    /// <summary>Error stack when available.</summary>
    public string? Stack { get; init; }

    /// <summary>Provider error code.</summary>
    public JsonNode? Code { get; init; }
}

/// <summary>Redacted provider/runtime diagnostic preserved on an assistant message.</summary>
public sealed record AssistantMessageDiagnostic
{
    /// <summary>Diagnostic category.</summary>
    public required string Type { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; init; }

    /// <summary>Structured error details.</summary>
    public DiagnosticErrorInfo? Error { get; init; }

    /// <summary>Additional diagnostic details.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Details { get; init; }
}

/// <summary>Helpers for creating and attaching assistant diagnostics.</summary>
public static class DiagnosticUtilities
{
    /// <summary>Formats a thrown CLR value using Pi's diagnostic fallback rules.</summary>
    public static string FormatThrownValue(object? value) => value switch
    {
        Exception exception when !string.IsNullOrEmpty(exception.Message) => exception.Message,
        Exception exception => exception.GetType().Name,
        string text => text,
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Extracts serializable diagnostic details from an exception or value.</summary>
    public static DiagnosticErrorInfo ExtractDiagnosticError(object? value)
    {
        if (value is not Exception exception)
        {
            return new DiagnosticErrorInfo
            {
                Name = "ThrownValue",
                Message = FormatThrownValue(value),
            };
        }

        return new DiagnosticErrorInfo
        {
            Name = string.IsNullOrEmpty(exception.GetType().Name) ? null : exception.GetType().Name,
            Message = string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message,
            Stack = exception.StackTrace,
        };
    }

    /// <summary>Creates a timestamped assistant diagnostic.</summary>
    public static AssistantMessageDiagnostic CreateAssistantMessageDiagnostic(
        string type,
        object? error,
        IReadOnlyDictionary<string, JsonNode?>? details = null) =>
        new()
        {
            Type = type,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Error = ExtractDiagnosticError(error),
            Details = details,
        };

    /// <summary>Appends a diagnostic to an assistant message.</summary>
    public static void AppendAssistantMessageDiagnostic(
        AssistantMessage message,
        AssistantMessageDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(diagnostic);
        message.Diagnostics = [.. message.Diagnostics ?? [], diagnostic];
    }
}

/// <summary>Helpers for extracting text from Pi content blocks.</summary>
public static class MessageUtilities
{
    /// <summary>Extracts and joins text blocks from content.</summary>
    public static string ContentText(IEnumerable<ContentBlock> content, string separator = "\n") =>
        string.Join(separator, content.OfType<TextContent>().Select(static block => block.Text));

    /// <summary>Extracts text from either a text content value or content blocks.</summary>
    public static string ContentText(object content, string separator = "\n") => content switch
    {
        string text => text,
        IEnumerable<ContentBlock> blocks => ContentText(blocks, separator),
        _ => string.Empty,
    };
}
