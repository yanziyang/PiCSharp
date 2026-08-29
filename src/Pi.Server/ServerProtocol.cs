using System.Globalization;
using System.Text.Json.Nodes;

using Pi.Ai;
using Pi.Protocol;

using NodeJsonValue = System.Text.Json.Nodes.JsonValue;
using ProtocolJsonValue = Pi.Protocol.JsonValue;

namespace Pi.Server;

/// <summary>Converts agent messages and runtime values to protocol transcript records.</summary>
public static class ServerProtocol
{
    /// <summary>Converts AI usage to the protocol's non-negative usage shape.</summary>
    public static Pi.Protocol.Usage? ToProtocolUsage(Pi.Ai.Usage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new Pi.Protocol.Usage(
            Math.Max(0, usage.Input),
            Math.Max(0, usage.Output),
            Math.Max(0, usage.CacheRead),
            Math.Max(0, usage.CacheWrite),
            usage.Reasoning is null ? null : Math.Max(0, usage.Reasoning.Value),
            Math.Max(0, usage.TotalTokens),
            new Pi.Protocol.UsageCost(
                NonNegative(usage.Cost.Input),
                NonNegative(usage.Cost.Output),
                NonNegative(usage.Cost.CacheRead),
                NonNegative(usage.Cost.CacheWrite),
                NonNegative(usage.Cost.Total)));
    }

    /// <summary>Converts one Pi AI model description to advertised protocol metadata.</summary>
    public static ModelMetadata ToProtocolModelMetadata(Pi.Ai.Model model, bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(model);
        var input = model.Input.Select(ToProtocolInputKind).ToArray();
        var thinkingLevels = ModelUtilities.GetSupportedThinkingLevels(model)
            .Select(ToProtocolThinkingLevel)
            .ToArray();
        return new ModelMetadata(
            Required(model.Provider, "Model provider"),
            Required(model.Id, "Model id"),
            Required(model.Name, "Model name"),
            Required(model.Api, "Model API"),
            model.Reasoning,
            input,
            Math.Max(1, model.ContextWindow),
            Math.Max(1, model.MaxTokens),
            new Pi.Protocol.ModelCost(
                NonNegative(model.Cost.Input),
                NonNegative(model.Cost.Output),
                NonNegative(model.Cost.CacheRead),
                NonNegative(model.Cost.CacheWrite)),
            thinkingLevels,
            authenticated);
    }

    /// <summary>Converts one agent message to a protocol transcript item.</summary>
    public static TranscriptItem ToProtocolTranscript(
        Message message,
        string itemId,
        ToolCall? toolCall = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        return message switch
        {
            UserMessage user => ToProtocolUserMessage(user, itemId),
            AssistantMessage assistant => ToProtocolAssistantMessage(assistant, itemId),
            ToolResultMessage toolResult when toolCall is not null => ToProtocolToolResultMessage(toolResult, itemId, toolCall),
            ToolResultMessage => throw new ProtocolValidationError("Tool result requires a matching tool call"),
            _ => throw new ProtocolValidationError("Unsupported agent message for protocol transcript"),
        };
    }

    /// <summary>Converts a user message to a protocol transcript item.</summary>
    public static UserTranscriptItem ToProtocolUserMessage(UserMessage message, string itemId)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new UserTranscriptItem(
            Required(itemId, "Transcript item id"),
            ToUserContent(message.Content),
            ValidateTimestamp(message.Timestamp));
    }

    /// <summary>Converts an assistant message to a protocol transcript item.</summary>
    public static AssistantTranscriptItem ToProtocolAssistantMessage(AssistantMessage message, string itemId)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = message.Content.Select(ToAssistantContent).ToArray();
        itemId = Required(itemId, "Transcript item id");
        var model = new ModelRef(Required(message.Provider, "Assistant provider"), Required(message.Model, "Assistant model"));
        var responseModel = message.ResponseModel is null
            ? null
            : Required(message.ResponseModel, "Assistant response model");
        var usage = ToProtocolUsage(message.Usage);
        var timestamp = ValidateTimestamp(message.Timestamp);
        return message.StopReason switch
        {
            StopReasons.Pending => new StreamingAssistantTranscriptItem(itemId, content, model, responseModel, usage, timestamp),
            StopReasons.Stop => new CompleteAssistantTranscriptItem(itemId, content, model, responseModel, usage, timestamp, TranscriptStopReason.Stop),
            StopReasons.Length => new CompleteAssistantTranscriptItem(itemId, content, model, responseModel, usage, timestamp, TranscriptStopReason.Length),
            StopReasons.ToolUse => new CompleteAssistantTranscriptItem(itemId, content, model, responseModel, usage, timestamp, TranscriptStopReason.ToolUse),
            StopReasons.Error when message.ErrorMessage is { Length: 0 } =>
                throw new ProtocolValidationError("Assistant error messages must not be empty"),
            StopReasons.Error => new ErrorAssistantTranscriptItem(itemId, content, model, responseModel, usage, timestamp, message.ErrorMessage),
            StopReasons.Aborted => new AbortedAssistantTranscriptItem(itemId, content, model, responseModel, usage, timestamp, message.ErrorMessage),
            StopReasons.Deferred => throw new ProtocolValidationError("Deferred assistant messages are not supported by protocol v1"),
            _ => throw new ProtocolValidationError($"Unsupported assistant stop reason: {message.StopReason}"),
        };
    }

    /// <summary>Converts a tool result to a protocol transcript item.</summary>
    public static ToolTranscriptItem ToProtocolToolResultMessage(
        ToolResultMessage message,
        string itemId,
        ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(toolCall);
        itemId = Required(itemId, "Transcript item id");
        var callId = Required(message.ToolCallId, "Tool result call id");
        var callName = Required(message.ToolName, "Tool result name");
        if (!string.Equals(callId, toolCall.Id, StringComparison.Ordinal))
        {
            throw new ProtocolValidationError($"Tool result {callId} does not match tool call {toolCall.Id}");
        }

        if (!string.Equals(callName, toolCall.Name, StringComparison.Ordinal))
        {
            throw new ProtocolValidationError($"Tool result {callName} does not match tool call {toolCall.Name}");
        }

        var input = ToProtocolJsonValue(toolCall.Arguments);
        var content = message.Content.Select(ToToolContent).ToArray();
        var details = message.Details is null ? null : SanitizeProtocolDetails(message.Details);
        var usage = ToProtocolUsage(message.Usage);
        var timestamp = ValidateTimestamp(message.Timestamp);
        return message.IsError
            ? new ErrorToolTranscriptItem(itemId, callId, callName, input, content, details, usage, timestamp)
            : new CompleteToolTranscriptItem(itemId, callId, callName, input, content, details, usage, timestamp);
    }

    /// <summary>Converts a JSON node to the strict protocol JSON-value union.</summary>
    public static ProtocolJsonValue ToProtocolJsonValue(JsonNode? value)
    {
        if (value is null)
        {
            return new ProtocolJsonValue.JsonNull();
        }

        if (value is JsonObject obj)
        {
            return new ProtocolJsonValue.JsonObject(
                obj.ToDictionary(
                    static pair => pair.Key,
                    static pair => ToProtocolJsonValue(pair.Value),
                    StringComparer.Ordinal));
        }

        if (value is JsonArray array)
        {
            return new ProtocolJsonValue.JsonArray(array.Select(ToProtocolJsonValue).ToArray());
        }

        if (value is NodeJsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
            {
                return new ProtocolJsonValue.JsonString(text);
            }

            if (jsonValue.TryGetValue<bool>(out var boolean))
            {
                return new ProtocolJsonValue.JsonBoolean(boolean);
            }

            if (jsonValue.TryGetValue<long>(out var integer))
            {
                return new ProtocolJsonValue.JsonNumber(integer);
            }

            if (jsonValue.TryGetValue<double>(out var number))
            {
                if (!double.IsFinite(number))
                {
                    throw new ProtocolValidationError("Protocol JSON numbers must be finite");
                }

                return new ProtocolJsonValue.JsonNumber(number);
            }
        }

        throw new ProtocolValidationError("Protocol JSON values must be finite scalar, array, or object values");
    }

    /// <summary>Lossily sanitizes diagnostic JSON details without affecting execution inputs.</summary>
    public static ProtocolJsonValue? SanitizeProtocolDetails(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        return SanitizeProtocolDetails(value, new HashSet<JsonNode>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>Converts a server-side error into the safe protocol error payload.</summary>
    public static ProtocolError ToProtocolError(Exception error, Action<Exception>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is InternalServerError internalError)
        {
            reportError?.Invoke(internalError.InnerException ?? internalError);
            return new ProtocolError(ProtocolErrorCode.InternalError, ServerErrorMessages.InternalServerError);
        }

        if (error is PiServerError serverError)
        {
            var message = serverError.Code == ProtocolErrorCode.NotImplemented
                ? ServerErrorMessages.NotImplemented
                : serverError.Message;
            return new ProtocolError(serverError.Code, message, serverError.Details);
        }

        if (error is ProtocolValidationError)
        {
            return new ProtocolError(ProtocolErrorCode.InvalidRequest, error.Message);
        }

        reportError?.Invoke(error);
        return new ProtocolError(ProtocolErrorCode.InternalError, ServerErrorMessages.InternalServerError);
    }

    private static Content[] ToUserContent(object content)
    {
        if (content is string text)
        {
            return [new Pi.Protocol.TextContent(text)];
        }

        if (content is IReadOnlyList<ContentBlock> blocks)
        {
            return blocks.Select(block => block switch
            {
                Pi.Ai.TextContent value => new Pi.Protocol.TextContent(value.Text) as Content,
                Pi.Ai.ImageContent value => new Pi.Protocol.ImageContent(value.Data, value.MimeType),
                _ => throw new ProtocolValidationError("Unsupported user content for protocol transcript"),
            }).ToArray();
        }

        throw new ProtocolValidationError("Unsupported user content for protocol transcript");
    }

    private static ProtocolJsonValue? SanitizeProtocolDetails(
        JsonNode value,
        ISet<JsonNode> seen)
    {
        if (!seen.Add(value))
        {
            return new ProtocolJsonValue.JsonString("[Circular]");
        }

        try
        {
            if (value is JsonObject obj)
            {
                var properties = new Dictionary<string, ProtocolJsonValue>(StringComparer.Ordinal);
                foreach (var pair in obj)
                {
                    var normalized = pair.Value is null
                        ? new ProtocolJsonValue.JsonNull()
                        : SanitizeProtocolDetails(pair.Value, seen);
                    if (normalized is not null)
                    {
                        properties[pair.Key] = normalized;
                    }
                }

                return new ProtocolJsonValue.JsonObject(properties);
            }

            if (value is JsonArray array)
            {
                return new ProtocolJsonValue.JsonArray(
                    array.Select(item => item is null
                        ? new ProtocolJsonValue.JsonNull()
                        : SanitizeProtocolDetails(item, seen) ?? new ProtocolJsonValue.JsonNull()).ToArray());
            }

            if (value is NodeJsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<string>(out var text))
                {
                    return new ProtocolJsonValue.JsonString(text);
                }

                if (jsonValue.TryGetValue<bool>(out var boolean))
                {
                    return new ProtocolJsonValue.JsonBoolean(boolean);
                }

                if (jsonValue.TryGetValue<double>(out var number))
                {
                    return double.IsFinite(number)
                        ? new ProtocolJsonValue.JsonNumber(number)
                        : new ProtocolJsonValue.JsonString(number.ToString(CultureInfo.InvariantCulture));
                }

                if (jsonValue.TryGetValue<DateTime>(out var dateTime))
                {
                    return new ProtocolJsonValue.JsonString(dateTime.ToString("O", CultureInfo.InvariantCulture));
                }

                if (jsonValue.TryGetValue<DateTimeOffset>(out var dateTimeOffset))
                {
                    return new ProtocolJsonValue.JsonString(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                }
            }

            return new ProtocolJsonValue.JsonString(value.ToJsonString());
        }
        finally
        {
            seen.Remove(value);
        }
    }

    private static Content ToAssistantContent(ContentBlock block) => block switch
    {
        Pi.Ai.TextContent text => new Pi.Protocol.TextContent(text.Text),
        Pi.Ai.ThinkingContent thinking => new Pi.Protocol.ThinkingContent(thinking.Thinking, thinking.Redacted),
        Pi.Ai.ToolCall call => new ToolCallContent(
            Required(call.Id, "Tool call id"),
            Required(call.Name, "Tool call name"),
            ToProtocolJsonValue(call.Arguments)),
        _ => throw new ProtocolValidationError("Unsupported assistant content for protocol transcript"),
    };

    private static Content ToToolContent(ContentBlock block) => block switch
    {
        Pi.Ai.TextContent text => new Pi.Protocol.TextContent(text.Text),
        Pi.Ai.ImageContent image => new Pi.Protocol.ImageContent(image.Data, image.MimeType),
        _ => throw new ProtocolValidationError("Unsupported tool content for protocol transcript"),
    };

    private static string Required(string value, string label)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ProtocolValidationError($"{label} must be a non-empty string");
        }

        return value;
    }

    private static ModelInputKind ToProtocolInputKind(string value) => value switch
    {
        "text" => ModelInputKind.Text,
        "image" => ModelInputKind.Image,
        _ => throw new ProtocolValidationError($"Unsupported model input kind: {value}"),
    };

    private static ThinkingLevel ToProtocolThinkingLevel(string value) => value switch
    {
        ThinkingLevels.Off => ThinkingLevel.Off,
        ThinkingLevels.Minimal => ThinkingLevel.Minimal,
        ThinkingLevels.Low => ThinkingLevel.Low,
        ThinkingLevels.Medium => ThinkingLevel.Medium,
        ThinkingLevels.High => ThinkingLevel.High,
        ThinkingLevels.XHigh => ThinkingLevel.Xhigh,
        ThinkingLevels.Max => ThinkingLevel.Max,
        _ => throw new ProtocolValidationError($"Unsupported thinking level: {value}"),
    };

    private static long ValidateTimestamp(long value)
    {
        if (value < 0 || value > 9_007_199_254_740_991)
        {
            throw new ProtocolValidationError("Protocol timestamps must be non-negative integers");
        }

        return value;
    }

    private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
