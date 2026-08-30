using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Pi.Ai;

namespace Pi.AgentCore;

/// <summary>Options for routing a Pi assistant stream through an HTTP proxy server.</summary>
public sealed class ProxyStreamOptions : StreamOptions
{
    /// <summary>Bearer token accepted by the proxy server.</summary>
    public required string AuthToken { get; init; }

    /// <summary>Base URL of the proxy server.</summary>
    public required string ProxyUrl { get; init; }

    /// <summary>Requested reasoning level.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Custom reasoning token budgets.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}

/// <summary>Assistant stream event sent by the proxy before client-side reconstruction.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProxyStartEvent), "start")]
[JsonDerivedType(typeof(ProxyTextStartEvent), "text_start")]
[JsonDerivedType(typeof(ProxyTextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(ProxyTextEndEvent), "text_end")]
[JsonDerivedType(typeof(ProxyThinkingStartEvent), "thinking_start")]
[JsonDerivedType(typeof(ProxyThinkingDeltaEvent), "thinking_delta")]
[JsonDerivedType(typeof(ProxyThinkingEndEvent), "thinking_end")]
[JsonDerivedType(typeof(ProxyToolCallStartEvent), "toolcall_start")]
[JsonDerivedType(typeof(ProxyToolCallDeltaEvent), "toolcall_delta")]
[JsonDerivedType(typeof(ProxyToolCallEndEvent), "toolcall_end")]
[JsonDerivedType(typeof(ProxyDoneEvent), "done")]
[JsonDerivedType(typeof(ProxyErrorEvent), "error")]
public abstract record ProxyAssistantMessageEvent
{
    /// <summary>The upstream proxy-event discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>Proxy stream start event.</summary>
public sealed record ProxyStartEvent : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "start";
}

/// <summary>Proxy text-content start event.</summary>
public sealed record ProxyTextStartEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text_start";
}

/// <summary>Proxy text-content delta event.</summary>
public sealed record ProxyTextDeltaEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("delta")] string Delta) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text_delta";
}

/// <summary>Proxy text-content end event.</summary>
public sealed record ProxyTextEndEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("contentSignature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentSignature = null) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text_end";
}

/// <summary>Proxy reasoning-content start event.</summary>
public sealed record ProxyThinkingStartEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking_start";
}

/// <summary>Proxy reasoning-content delta event.</summary>
public sealed record ProxyThinkingDeltaEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("delta")] string Delta) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking_delta";
}

/// <summary>Proxy reasoning-content end event.</summary>
public sealed record ProxyThinkingEndEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("contentSignature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentSignature = null) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking_end";
}

/// <summary>Proxy tool-call start event.</summary>
public sealed record ProxyToolCallStartEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("toolName")] string ToolName) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolcall_start";
}

/// <summary>Proxy tool-call argument delta event.</summary>
public sealed record ProxyToolCallDeltaEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("delta")] string Delta) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolcall_delta";
}

/// <summary>Proxy tool-call end event.</summary>
public sealed record ProxyToolCallEndEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("toolCall")] ToolCall ToolCall) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolcall_end";
}

/// <summary>Successful proxy stream completion event.</summary>
public sealed record ProxyDoneEvent(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("usage")] Usage Usage) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "done";
}

/// <summary>Unsuccessful proxy stream completion event.</summary>
public sealed record ProxyErrorEvent(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("errorMessage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage,
    [property: JsonPropertyName("usage")] Usage Usage) : ProxyAssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "error";
}

/// <summary>Starts assistant streams through Pi's HTTP proxy endpoint.</summary>
public static class Proxy
{
    private static readonly HttpClient _sharedHttpClient = new();

    /// <summary>
    /// Starts a proxy stream. Request and protocol failures are represented by an error event in
    /// the returned stream, matching Pi's stream-function contract.
    /// </summary>
    /// <param name="model">Model metadata sent to the proxy.</param>
    /// <param name="context">Conversation context sent to the proxy.</param>
    /// <param name="options">Proxy credentials, URL, stream options and cancellation signal.</param>
    /// <param name="httpClient">Optional HTTP client used when <see cref="ProviderRequestOptions.Fetch" /> is not supplied.</param>
    public static AssistantMessageEventStream StreamProxy(
        Model model,
        Context context,
        ProxyStreamOptions options,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var stream = new AssistantMessageEventStream();
        _ = RunAsync(stream, model, context, options, httpClient ?? _sharedHttpClient);
        return stream;
    }

    private static async Task RunAsync(
        AssistantMessageEventStream stream,
        Model model,
        Context context,
        ProxyStreamOptions options,
        HttpClient httpClient)
    {
        var partial = new AssistantMessage
        {
            Content = new List<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = EmptyUsage(),
            StopReason = StopReasons.Pending,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var partialJsonByContentIndex = new Dictionary<int, string>();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"{options.ProxyUrl}/api/stream", UriKind.Absolute));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
            request.Content = new StringContent(
                BuildProxyRequest(model, context, options).ToJsonString(),
                Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = options.Fetch is { } fetch
                ? await fetch(request, options.Signal).ConfigureAwait(false)
                : await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        options.Signal)
                    .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await GetProxyErrorMessageAsync(response, options.Signal).ConfigureAwait(false);
                throw new InvalidOperationException(errorMessage);
            }

            await using var body = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);
            var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
            var bytes = ArrayPool<byte>.Shared.Rent(8192);
            var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
            try
            {
                var buffer = string.Empty;
                while (true)
                {
                    var count = await body.ReadAsync(bytes.AsMemory(0, bytes.Length), options.Signal).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    if (options.Signal.IsCancellationRequested)
                    {
                        throw new InvalidOperationException("Request aborted by user");
                    }

                    var characterCount = decoder.GetChars(bytes, 0, count, characters, 0, flush: false);
                    buffer += new string(characters, 0, characterCount);
                    var lines = buffer.Split('\n');
                    buffer = lines[^1];
                    for (var index = 0; index < lines.Length - 1; index++)
                    {
                        var line = lines[index];
                        if (!line.StartsWith("data: ", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var data = line[6..].Trim();
                        if (data.Length == 0)
                        {
                            continue;
                        }

                        var proxyEvent = JsonNode.Parse(data) as JsonObject;
                        if (proxyEvent is null)
                        {
                            continue;
                        }

                        var eventValue = ProcessProxyEvent(
                            proxyEvent,
                            ref partial,
                            partialJsonByContentIndex);
                        if (eventValue is not null)
                        {
                            stream.Push(eventValue);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
                ArrayPool<char>.Shared.Return(characters);
            }

            if (options.Signal.IsCancellationRequested)
            {
                throw new InvalidOperationException("Request aborted by user");
            }

            stream.End();
        }
        catch (Exception error)
        {
            var errorMessage = error.Message;
            var reason = options.Signal.IsCancellationRequested ? StopReasons.Aborted : StopReasons.Error;
            partial = partial with
            {
                StopReason = reason,
                ErrorMessage = errorMessage,
            };
            stream.Push(new StreamErrorEvent(reason, partial));
            stream.End();
        }
    }

    private static AssistantMessageEvent? ProcessProxyEvent(
        JsonObject proxyEvent,
        ref AssistantMessage partial,
        Dictionary<int, string> partialJsonByContentIndex)
    {
        var type = RequiredString(proxyEvent, "type");
        switch (type)
        {
            case "start":
                return new StreamStartEvent(partial);

            case "text_start":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    SetContent(partial, contentIndex, new TextContent(string.Empty));
                    return new TextStartEvent(contentIndex, partial);
                }

            case "text_delta":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    var delta = RequiredString(proxyEvent, "delta");
                    if (ContentAt(partial, contentIndex) is not TextContent content)
                    {
                        throw new InvalidOperationException("Received text_delta for non-text content");
                    }

                    SetContent(partial, contentIndex, content with { Text = content.Text + delta });
                    return new TextDeltaEvent(contentIndex, delta, partial);
                }

            case "text_end":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    if (ContentAt(partial, contentIndex) is not TextContent content)
                    {
                        throw new InvalidOperationException("Received text_end for non-text content");
                    }

                    SetContent(
                        partial,
                        contentIndex,
                        content with { TextSignature = OptionalString(proxyEvent, "contentSignature") });
                    return new TextEndEvent(contentIndex, content.Text, partial);
                }

            case "thinking_start":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    SetContent(partial, contentIndex, new ThinkingContent(string.Empty));
                    return new ThinkingStartEvent(contentIndex, partial);
                }

            case "thinking_delta":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    var delta = RequiredString(proxyEvent, "delta");
                    if (ContentAt(partial, contentIndex) is not ThinkingContent content)
                    {
                        throw new InvalidOperationException("Received thinking_delta for non-thinking content");
                    }

                    SetContent(partial, contentIndex, content with { Thinking = content.Thinking + delta });
                    return new ThinkingDeltaEvent(contentIndex, delta, partial);
                }

            case "thinking_end":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    if (ContentAt(partial, contentIndex) is not ThinkingContent content)
                    {
                        throw new InvalidOperationException("Received thinking_end for non-thinking content");
                    }

                    SetContent(
                        partial,
                        contentIndex,
                        content with { ThinkingSignature = OptionalString(proxyEvent, "contentSignature") });
                    return new ThinkingEndEvent(contentIndex, content.Thinking, partial);
                }

            case "toolcall_start":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    var id = RequiredString(proxyEvent, "id");
                    var toolName = RequiredString(proxyEvent, "toolName");
                    SetContent(partial, contentIndex, new ToolCall(id, toolName, new JsonObject()));
                    partialJsonByContentIndex[contentIndex] = string.Empty;
                    return new ToolCallStartEvent(contentIndex, partial);
                }

            case "toolcall_delta":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    var delta = RequiredString(proxyEvent, "delta");
                    if (ContentAt(partial, contentIndex) is not ToolCall content)
                    {
                        throw new InvalidOperationException("Received toolcall_delta for non-toolCall content");
                    }

                    partialJsonByContentIndex.TryGetValue(contentIndex, out var partialJson);
                    partialJson ??= string.Empty;
                    partialJson += delta;
                    partialJsonByContentIndex[contentIndex] = partialJson;
                    var arguments = JsonParseUtilities.ParseStreamingJson(partialJson) as JsonObject ?? new JsonObject();
                    SetContent(partial, contentIndex, content with { Arguments = arguments });
                    return new ToolCallDeltaEvent(contentIndex, delta, partial);
                }

            case "toolcall_end":
                {
                    var contentIndex = RequiredInt(proxyEvent, "contentIndex");
                    if (ContentAt(partial, contentIndex) is not ToolCall)
                    {
                        return null;
                    }

                    var toolCall = ReadToolCall(proxyEvent["toolCall"]);
                    SetContent(partial, contentIndex, toolCall);
                    partialJsonByContentIndex.Remove(contentIndex);
                    return new ToolCallEndEvent(contentIndex, toolCall, partial);
                }

            case "done":
                {
                    var reason = RequiredString(proxyEvent, "reason");
                    var usage = ReadUsage(proxyEvent["usage"]);
                    partial = partial with
                    {
                        StopReason = reason,
                        Usage = usage,
                    };
                    return new StreamDoneEvent(reason, partial);
                }

            case "error":
                {
                    var reason = RequiredString(proxyEvent, "reason");
                    var usage = ReadUsage(proxyEvent["usage"]);
                    partial = partial with
                    {
                        StopReason = reason,
                        ErrorMessage = OptionalString(proxyEvent, "errorMessage"),
                        Usage = usage,
                    };
                    return new StreamErrorEvent(reason, partial);
                }

            default:
                return null;
        }
    }

    private static async Task<string> GetProxyErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var errorMessage = $"Proxy error: {(int)response.StatusCode} {response.ReasonPhrase ?? string.Empty}";
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject errorData &&
                errorData["error"] is JsonValue errorValue &&
                errorValue.TryGetValue<string>(out var message) &&
                !string.IsNullOrEmpty(message))
            {
                errorMessage = $"Proxy error: {message}";
            }
        }
        catch
        {
            // The status-based message is the fallback when the error body is unavailable or invalid.
        }

        return errorMessage;
    }

    private static JsonObject BuildProxyRequest(Model model, Context context, ProxyStreamOptions options) =>
        new()
        {
            ["model"] = ModelToJson(model),
            ["context"] = ContextToJson(context),
            ["options"] = SerializableOptionsToJson(options),
        };

    private static JsonObject ModelToJson(Model model)
    {
        var result = new JsonObject
        {
            ["id"] = StringValue(model.Id),
            ["name"] = StringValue(model.Name),
            ["api"] = StringValue(model.Api),
            ["provider"] = StringValue(model.Provider),
            ["baseUrl"] = StringValue(model.BaseUrl),
            ["reasoning"] = JsonValue.Create(model.Reasoning),
            ["input"] = StringArray(model.Input),
            ["cost"] = CostToJson(model.Cost),
            ["contextWindow"] = JsonValue.Create(model.ContextWindow),
            ["maxTokens"] = JsonValue.Create(model.MaxTokens),
        };

        if (model.ThinkingLevelMap is not null)
        {
            result["thinkingLevelMap"] = NullableStringMap(model.ThinkingLevelMap);
        }

        if (model.SamplingParameters is not null)
        {
            result["samplingParams"] = NodeMap(model.SamplingParameters);
        }

        if (model.Headers is not null)
        {
            result["headers"] = StringMap(model.Headers);
        }

        if (model.Compatibility is not null)
        {
            result["compat"] = model.Compatibility.DeepClone();
        }

        return result;
    }

    private static JsonObject ContextToJson(Context context)
    {
        var result = new JsonObject
        {
            ["messages"] = new JsonArray(context.Messages.Select(MessageToJson).ToArray()),
        };
        if (context.SystemPrompt is not null)
        {
            result["systemPrompt"] = StringValue(context.SystemPrompt);
        }

        if (context.Tools.Count > 0)
        {
            result["tools"] = new JsonArray(context.Tools.Select(ToolToJson).ToArray());
        }

        return result;
    }

    private static JsonObject SerializableOptionsToJson(ProxyStreamOptions options)
    {
        var result = new JsonObject();
        if (options.Temperature is { } temperature)
        {
            result["temperature"] = JsonValue.Create(temperature);
        }

        if (options.SamplingParameters is not null)
        {
            result["samplingParams"] = NodeMap(options.SamplingParameters);
        }

        if (options.MaxTokens is { } maxTokens)
        {
            result["maxTokens"] = JsonValue.Create(maxTokens);
        }

        AddOptionalString(result, "reasoning", options.Reasoning);
        AddOptionalString(result, "cacheRetention", options.CacheRetention);
        AddOptionalString(result, "sessionId", options.SessionId);
        if (options.Headers is not null)
        {
            result["headers"] = NullableStringMap(options.Headers);
        }

        if (options.Metadata is not null)
        {
            result["metadata"] = NodeMap(options.Metadata);
        }

        AddOptionalString(result, "transport", options.Transport);
        if (options.ThinkingBudgets is not null)
        {
            result["thinkingBudgets"] = ThinkingBudgetsToJson(options.ThinkingBudgets);
        }

        if (options.MaxRetryDelayMs is { } maxRetryDelayMs)
        {
            result["maxRetryDelayMs"] = JsonValue.Create(maxRetryDelayMs);
        }

        return result;
    }

    private static JsonObject ToolToJson(Tool tool)
    {
        var result = new JsonObject
        {
            ["name"] = StringValue(tool.Name),
            ["description"] = StringValue(tool.Description),
            ["parameters"] = tool.Parameters.DeepClone(),
        };
        if (tool.ConstrainedSampling is not null)
        {
            result["constrainedSampling"] = ConstrainedSamplingToJson(tool.ConstrainedSampling);
        }

        return result;
    }

    private static JsonObject ConstrainedSamplingToJson(ConstrainedSamplingConfig config) => config switch
    {
        JsonSchemaSampling jsonSchema => new JsonObject
        {
            ["type"] = StringValue("json_schema"),
            ["strict"] = StringValue(jsonSchema.Strict),
        },
        GrammarSampling grammar => new JsonObject
        {
            ["type"] = StringValue("grammar"),
            ["variants"] = StringMap(grammar.Variants),
        },
        _ => throw new ArgumentException($"Unsupported constrained sampling type: {config.GetType().Name}", nameof(config)),
    };

    private static JsonObject MessageToJson(Message message) => message switch
    {
        UserMessage user => new JsonObject
        {
            ["role"] = StringValue("user"),
            ["content"] = UserContentToJson(user.Content),
            ["timestamp"] = JsonValue.Create(user.Timestamp),
        },
        AssistantMessage assistant => AssistantMessageToJson(assistant),
        ToolResultMessage toolResult => ToolResultMessageToJson(toolResult),
        _ => throw new ArgumentException($"Unsupported Pi message type: {message.GetType().Name}", nameof(message)),
    };

    private static JsonObject AssistantMessageToJson(AssistantMessage message)
    {
        var result = new JsonObject
        {
            ["role"] = StringValue("assistant"),
            ["content"] = new JsonArray(message.Content.Select(ContentToJson).ToArray()),
            ["api"] = StringValue(message.Api),
            ["provider"] = StringValue(message.Provider),
            ["model"] = StringValue(message.Model),
            ["usage"] = UsageToJson(message.Usage),
            ["stopReason"] = StringValue(message.StopReason),
            ["timestamp"] = JsonValue.Create(message.Timestamp),
        };
        AddOptionalString(result, "responseModel", message.ResponseModel);
        AddOptionalString(result, "responseId", message.ResponseId);
        if (message.Diagnostics is not null)
        {
            result["diagnostics"] = new JsonArray(message.Diagnostics.Select(DiagnosticToJson).ToArray());
        }

        if (message.Deferred is not null)
        {
            result["deferred"] = DeferredToJson(message.Deferred);
        }

        AddOptionalString(result, "errorMessage", message.ErrorMessage);
        AddOptionalString(result, "rawStopReason", message.RawStopReason);
        if (message.EndTurn is { } endTurn)
        {
            result["endTurn"] = JsonValue.Create(endTurn);
        }

        return result;
    }

    private static JsonObject ToolResultMessageToJson(ToolResultMessage message)
    {
        var result = new JsonObject
        {
            ["role"] = StringValue("toolResult"),
            ["toolCallId"] = StringValue(message.ToolCallId),
            ["toolName"] = StringValue(message.ToolName),
            ["content"] = new JsonArray(message.Content.Select(ContentToJson).ToArray()),
            ["isError"] = JsonValue.Create(message.IsError),
            ["timestamp"] = JsonValue.Create(message.Timestamp),
        };
        if (message.Details is not null)
        {
            result["details"] = message.Details.DeepClone();
        }

        if (message.Usage is not null)
        {
            result["usage"] = UsageToJson(message.Usage);
        }

        if (message.AddedToolNames is not null)
        {
            result["addedToolNames"] = StringArray(message.AddedToolNames);
        }

        return result;
    }

    private static JsonNode UserContentToJson(object content) => content switch
    {
        JsonNode node => node.DeepClone()!,
        string text => StringValue(text),
        IEnumerable<ContentBlock> blocks => new JsonArray(blocks.Select(ContentToJson).ToArray()),
        _ => throw new ArgumentException("Content must be a JSON node, string, or content-block collection.", nameof(content)),
    };

    private static JsonObject ContentToJson(ContentBlock content)
    {
        switch (content)
        {
            case TextContent text:
                {
                    var result = new JsonObject
                    {
                        ["type"] = StringValue("text"),
                        ["text"] = StringValue(text.Text),
                    };
                    AddOptionalString(result, "textSignature", text.TextSignature);
                    return result;
                }
            case ThinkingContent thinking:
                {
                    var result = new JsonObject
                    {
                        ["type"] = StringValue("thinking"),
                        ["thinking"] = StringValue(thinking.Thinking),
                    };
                    AddOptionalString(result, "thinkingSignature", thinking.ThinkingSignature);
                    if (thinking.Redacted is { } redacted)
                    {
                        result["redacted"] = JsonValue.Create(redacted);
                    }

                    return result;
                }
            case ImageContent image:
                return new JsonObject
                {
                    ["type"] = StringValue("image"),
                    ["data"] = StringValue(image.Data),
                    ["mimeType"] = StringValue(image.MimeType),
                };
            case ToolCall toolCall:
                {
                    var result = new JsonObject
                    {
                        ["type"] = StringValue("toolCall"),
                        ["id"] = StringValue(toolCall.Id),
                        ["name"] = StringValue(toolCall.Name),
                        ["arguments"] = toolCall.Arguments.DeepClone(),
                    };
                    AddOptionalString(result, "thoughtSignature", toolCall.ThoughtSignature);
                    AddOptionalString(result, "namespace", toolCall.Namespace);
                    return result;
                }
            default:
                throw new ArgumentException($"Unsupported content block type: {content.GetType().Name}", nameof(content));
        }
    }

    private static JsonObject DiagnosticToJson(AssistantMessageDiagnostic diagnostic)
    {
        var result = new JsonObject
        {
            ["type"] = StringValue(diagnostic.Type),
            ["timestamp"] = JsonValue.Create(diagnostic.Timestamp),
        };
        if (diagnostic.Error is not null)
        {
            var error = new JsonObject
            {
                ["message"] = StringValue(diagnostic.Error.Message),
            };
            AddOptionalString(error, "name", diagnostic.Error.Name);
            AddOptionalString(error, "stack", diagnostic.Error.Stack);
            if (diagnostic.Error.Code is not null)
            {
                error["code"] = diagnostic.Error.Code.DeepClone();
            }

            result["error"] = error;
        }

        if (diagnostic.Details is not null)
        {
            result["details"] = NodeMap(diagnostic.Details);
        }

        return result;
    }

    private static JsonObject DeferredToJson(DeferredHandle handle)
    {
        var result = new JsonObject
        {
            ["provider"] = StringValue(handle.Provider),
            ["modelId"] = StringValue(handle.ModelId),
            ["api"] = StringValue(handle.Api),
            ["id"] = StringValue(handle.Id),
        };
        if (handle.ExpiresAt is { } expiresAt)
        {
            result["expiresAt"] = JsonValue.Create(expiresAt);
        }

        if (handle.PollAfterMs is { } pollAfterMs)
        {
            result["pollAfterMs"] = JsonValue.Create(pollAfterMs);
        }

        if (handle.Data is not null)
        {
            result["data"] = handle.Data.DeepClone();
        }

        return result;
    }

    private static JsonObject UsageToJson(Usage usage)
    {
        var result = new JsonObject
        {
            ["input"] = JsonValue.Create(usage.Input),
            ["output"] = JsonValue.Create(usage.Output),
            ["cacheRead"] = JsonValue.Create(usage.CacheRead),
            ["cacheWrite"] = JsonValue.Create(usage.CacheWrite),
            ["totalTokens"] = JsonValue.Create(usage.TotalTokens),
            ["cost"] = new JsonObject
            {
                ["input"] = JsonValue.Create(usage.Cost.Input),
                ["output"] = JsonValue.Create(usage.Cost.Output),
                ["cacheRead"] = JsonValue.Create(usage.Cost.CacheRead),
                ["cacheWrite"] = JsonValue.Create(usage.Cost.CacheWrite),
                ["total"] = JsonValue.Create(usage.Cost.Total),
            },
        };
        if (usage.CacheWrite1h is { } cacheWrite1h)
        {
            result["cacheWrite1h"] = JsonValue.Create(cacheWrite1h);
        }

        if (usage.Reasoning is { } reasoning)
        {
            result["reasoning"] = JsonValue.Create(reasoning);
        }

        return result;
    }

    private static JsonObject CostToJson(ModelCost cost)
    {
        var result = new JsonObject
        {
            ["input"] = JsonValue.Create(cost.Input),
            ["output"] = JsonValue.Create(cost.Output),
            ["cacheRead"] = JsonValue.Create(cost.CacheRead),
            ["cacheWrite"] = JsonValue.Create(cost.CacheWrite),
        };
        if (cost.Tiers.Count > 0)
        {
            result["tiers"] = new JsonArray(cost.Tiers.Select(CostTierToJson).ToArray());
        }

        return result;
    }

    private static JsonObject CostTierToJson(ModelCostTier tier) =>
        new()
        {
            ["input"] = JsonValue.Create(tier.Input),
            ["output"] = JsonValue.Create(tier.Output),
            ["cacheRead"] = JsonValue.Create(tier.CacheRead),
            ["cacheWrite"] = JsonValue.Create(tier.CacheWrite),
            ["inputTokensAbove"] = JsonValue.Create(tier.InputTokensAbove),
        };

    private static JsonObject ThinkingBudgetsToJson(ThinkingBudgets budgets)
    {
        var result = new JsonObject();
        if (budgets.Minimal is { } minimal)
        {
            result["minimal"] = JsonValue.Create(minimal);
        }

        if (budgets.Low is { } low)
        {
            result["low"] = JsonValue.Create(low);
        }

        if (budgets.Medium is { } medium)
        {
            result["medium"] = JsonValue.Create(medium);
        }

        if (budgets.High is { } high)
        {
            result["high"] = JsonValue.Create(high);
        }

        return result;
    }

    private static Usage ReadUsage(JsonNode? node)
    {
        if (node is not JsonObject value)
        {
            return EmptyUsage();
        }

        return new Usage
        {
            Input = IntValue(value["input"]),
            Output = IntValue(value["output"]),
            CacheRead = IntValue(value["cacheRead"]),
            CacheWrite = IntValue(value["cacheWrite"]),
            CacheWrite1h = OptionalInt(value["cacheWrite1h"]),
            Reasoning = OptionalInt(value["reasoning"]),
            TotalTokens = IntValue(value["totalTokens"]),
            Cost = ReadUsageCost(value["cost"]),
        };
    }

    private static UsageCost ReadUsageCost(JsonNode? node)
    {
        if (node is not JsonObject value)
        {
            return new UsageCost();
        }

        return new UsageCost
        {
            Input = DoubleValue(value["input"]),
            Output = DoubleValue(value["output"]),
            CacheRead = DoubleValue(value["cacheRead"]),
            CacheWrite = DoubleValue(value["cacheWrite"]),
            Total = DoubleValue(value["total"]),
        };
    }

    private static ToolCall ReadToolCall(JsonNode? node)
    {
        if (node is not JsonObject value)
        {
            throw new JsonException("Proxy toolcall_end event is missing toolCall.");
        }

        return new ToolCall(
            RequiredString(value, "id"),
            RequiredString(value, "name"),
            value["arguments"] is JsonObject arguments ? arguments : new JsonObject(),
            OptionalString(value, "thoughtSignature"),
            OptionalString(value, "namespace"));
    }

    private static void SetContent(AssistantMessage partial, int contentIndex, ContentBlock content)
    {
        var contents = partial.Content as List<ContentBlock> ?? throw new InvalidOperationException("Proxy partial content is not mutable.");
        ArgumentOutOfRangeException.ThrowIfNegative(contentIndex);

        while (contents.Count <= contentIndex)
        {
            contents.Add(null!);
        }

        contents[contentIndex] = content;
    }

    private static ContentBlock? ContentAt(AssistantMessage partial, int contentIndex)
    {
        if (contentIndex < 0 || contentIndex >= partial.Content.Count)
        {
            return null;
        }

        return partial.Content[contentIndex];
    }

    private static Usage EmptyUsage() => new()
    {
        Cost = new UsageCost(),
    };

    private static string RequiredString(JsonObject value, string propertyName)
    {
        if (value[propertyName] is JsonValue json && json.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new JsonException($"Proxy event property '{propertyName}' must be a string.");
    }

    private static int RequiredInt(JsonObject value, string propertyName)
    {
        if (value[propertyName] is JsonNode node)
        {
            return IntValue(node);
        }

        throw new JsonException($"Proxy event property '{propertyName}' must be an integer.");
    }

    private static string? OptionalString(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue json && json.TryGetValue<string>(out var result) ? result : null;

    private static int IntValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return checked((int)longValue);
        }

        return value.TryGetValue<double>(out var number) ? checked((int)number) : 0;
    }

    private static int? OptionalInt(JsonNode? node) => node is null ? null : IntValue(node);

    private static double DoubleValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return value.TryGetValue<long>(out var integer) ? integer : 0;
    }

    private static JsonValue StringValue(string value) => JsonValue.Create(value)!;

    private static JsonValue? OptionalValue(string? value) => value is null ? null : StringValue(value);

    private static void AddOptionalString(JsonObject destination, string name, string? value)
    {
        if (value is not null)
        {
            destination[name] = StringValue(value);
        }
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add((JsonNode)StringValue(value));
        }

        return result;
    }

    private static JsonObject StringMap(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new JsonObject();
        foreach (var pair in values)
        {
            result[pair.Key] = OptionalValue(pair.Value);
        }

        return result;
    }

    private static JsonObject NullableStringMap(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var result = new JsonObject();
        foreach (var pair in values)
        {
            result[pair.Key] = OptionalValue(pair.Value);
        }

        return result;
    }

    private static JsonObject NodeMap(IEnumerable<KeyValuePair<string, JsonNode?>> values)
    {
        var result = new JsonObject();
        foreach (var pair in values)
        {
            result[pair.Key] = pair.Value?.DeepClone();
        }

        return result;
    }
}

/// <summary>Compatibility facade for the proxy stream helper.</summary>
public static class ProxyUtilities
{
    /// <inheritdoc cref="Proxy.StreamProxy(Model, Context, ProxyStreamOptions, HttpClient?)" />
    public static AssistantMessageEventStream StreamProxy(
        Model model,
        Context context,
        ProxyStreamOptions options,
        HttpClient? httpClient = null) =>
        Proxy.StreamProxy(model, context, options, httpClient);
}
