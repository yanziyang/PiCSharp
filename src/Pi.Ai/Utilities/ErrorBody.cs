using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Provider SDK metadata used to preserve HTTP status and response-body diagnostics.</summary>
public sealed record ProviderErrorMetadata
{
    /// <summary>Mistral-style status code.</summary>
    public int? StatusCode { get; init; }

    /// <summary>OpenAI/Google-style status code.</summary>
    public int? Status { get; init; }

    /// <summary>AWS metadata status code.</summary>
    public int? MetadataHttpStatusCode { get; init; }

    /// <summary>AWS response-wrapper status code.</summary>
    public int? ResponseStatusCode { get; init; }

    /// <summary>Raw Mistral-style body text.</summary>
    public string? Body { get; init; }

    /// <summary>Parsed OpenAI-style JSON error body.</summary>
    public JsonNode? ParsedError { get; init; }

    /// <summary>Raw AWS response body text.</summary>
    public string? ResponseBody { get; init; }

    /// <summary>Parsed AWS response body object.</summary>
    public JsonNode? ResponseBodyObject { get; init; }

    /// <summary>Marks a response body as an unreadable stream and therefore not a body reason.</summary>
    public bool ResponseBodyIsReadableStream { get; init; }
}

/// <summary>
/// SDK-neutral exception carrying the response metadata that Pi's provider adapters need.
/// </summary>
public sealed class ProviderErrorMetadataException : Exception
{
    /// <summary>Creates a provider error with structured metadata.</summary>
    public ProviderErrorMetadataException(
        string message,
        ProviderErrorMetadata metadata,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    /// <summary>Structured provider metadata.</summary>
    public ProviderErrorMetadata Metadata { get; }
}

/// <summary>Normalized status/body information for provider error display.</summary>
public sealed record NormalizedProviderError
{
    /// <summary>HTTP status code, when one could be extracted.</summary>
    public int? Status { get; init; }

    /// <summary>Trimmed and capped raw HTTP body reason.</summary>
    public string? Body { get; init; }

    /// <summary>Exception message or safe serialization of a thrown value.</summary>
    public required string Message { get; init; }

    /// <summary>Whether the message already contains the extracted body.</summary>
    public required bool MessageCarriesBody { get; init; }
}

/// <summary>Shared provider HTTP error normalization helpers.</summary>
public static class ErrorBodyUtilities
{
    /// <summary>Maximum response-body characters included in a provider error.</summary>
    public const int MaxProviderErrorBodyChars = 4000;

    /// <summary>Normalizes a provider exception or arbitrary thrown value.</summary>
    public static NormalizedProviderError NormalizeProviderError(object? error)
    {
        if (error is not Exception exception)
        {
            return new NormalizedProviderError
            {
                Message = SafeJsonStringify(error),
                MessageCarriesBody = false,
            };
        }

        var metadata = exception is ProviderErrorMetadataException metadataException
            ? metadataException.Metadata
            : null;
        var status = ExtractStatus(exception, metadata);
        var bodyText = PickBodyText(exception, metadata);
        var body = string.IsNullOrWhiteSpace(bodyText)
            ? null
            : TruncateErrorText(bodyText.Trim(), MaxProviderErrorBodyChars);

        return new NormalizedProviderError
        {
            Status = status,
            Body = body,
            Message = exception.Message,
            MessageCarriesBody = body is null || exception.Message.Contains(body, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Composes a display message. A prefix is rendered as <c>prefix (status): reason</c>.
    /// </summary>
    public static string FormatProviderError(NormalizedProviderError normalized, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (normalized.MessageCarriesBody || normalized.Status is null || normalized.Body is null)
        {
            return prefix is not null && normalized.Status is not null
                ? $"{prefix} ({normalized.Status}): {normalized.Message}"
                : normalized.Message;
        }

        return prefix is not null
            ? $"{prefix} ({normalized.Status}): {normalized.Body}"
            : $"{normalized.Status}: {normalized.Body}";
    }

    /// <summary>Truncates provider text using Pi's diagnostic suffix.</summary>
    public static string TruncateErrorText(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";
    }

    /// <summary>Serializes a thrown value without allowing serialization failures to escape.</summary>
    public static string SafeJsonStringify(object? value)
    {
        try
        {
            return JsonValueUtilities.ToJson(value);
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
    }

    private static int? ExtractStatus(Exception exception, ProviderErrorMetadata? metadata)
    {
        if (metadata?.StatusCode is not null)
        {
            return metadata.StatusCode;
        }

        if (metadata?.Status is not null)
        {
            return metadata.Status;
        }

        if (metadata?.MetadataHttpStatusCode is not null)
        {
            return metadata.MetadataHttpStatusCode;
        }

        if (metadata?.ResponseStatusCode is not null)
        {
            return metadata.ResponseStatusCode;
        }

        if (exception is HttpRequestException { StatusCode: not null } requestException)
        {
            return (int)requestException.StatusCode.Value;
        }

        return null;
    }

    private static string? PickBodyText(Exception exception, ProviderErrorMetadata? metadata)
    {
        if (metadata is not null)
        {
            if (metadata.Body is not null)
            {
                return metadata.Body;
            }

            if (IsPlainNonEmptyObject(metadata.ParsedError))
            {
                return metadata.ParsedError!.ToJsonString();
            }

            if (metadata.ResponseBody is not null)
            {
                return metadata.ResponseBody;
            }

            if (!metadata.ResponseBodyIsReadableStream && IsPlainNonEmptyObject(metadata.ResponseBodyObject))
            {
                return metadata.ResponseBodyObject!.ToJsonString();
            }
        }

        if (exception.Data["body"] is string body)
        {
            return body;
        }

        if (exception.Data["error"] is JsonNode parsedError && IsPlainNonEmptyObject(parsedError))
        {
            return parsedError.ToJsonString();
        }

        if (exception.Data["responseBody"] is string responseBody)
        {
            return responseBody;
        }

        return null;
    }

    private static bool IsPlainNonEmptyObject(JsonNode? node) =>
        node is JsonObject objectNode && objectNode.Count > 0;
}
