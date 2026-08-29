using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>OpenAI Responses provider-specific streaming options.</summary>
public sealed class OpenAiResponsesStreamOptions : StreamOptions
{
    /// <summary>Requested Responses reasoning effort.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Requested reasoning summary mode.</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>OpenAI service tier.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>OpenAI Responses tool-choice value.</summary>
    public JsonNode? ToolChoice { get; init; }
}

/// <summary>Raw HTTP/SSE implementation of Pi's OpenAI Responses API.</summary>
public sealed class OpenAiResponsesProvider : ProviderStreams
{
    private const int _minimumOutputTokens = 16;
    private readonly ProviderHttpClient _transport;

    /// <summary>Creates an adapter backed by the supplied HTTP transport.</summary>
    public OpenAiResponsesProvider(ProviderHttpClient? transport = null)
    {
        _transport = transport ?? new ProviderHttpClient();
    }

    /// <summary>Builds a streamed Responses request payload.</summary>
    public static JsonObject BuildPayload(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var responseOptions = options as OpenAiResponsesStreamOptions;
        var compatibility = GetCompatibility(model);
        var cacheRetention = ResolveCacheRetention(options);
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = ConvertMessages(model, context, compatibility),
            ["stream"] = true,
            ["store"] = false,
        };

        if (cacheRetention != CacheRetentions.None && !string.IsNullOrEmpty(options?.SessionId))
        {
            payload["prompt_cache_key"] = ClampPromptCacheKey(options.SessionId);
        }
        else if (cacheRetention == CacheRetentions.None && compatibility.SupportsExplicitPromptCacheMode)
        {
            payload["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };
        }

        if (cacheRetention == CacheRetentions.Long && compatibility.SupportsLongCacheRetention)
        {
            payload["prompt_cache_retention"] = "24h";
        }

        if (options?.MaxTokens is > 0)
        {
            payload["max_output_tokens"] = Math.Max(options.MaxTokens.Value, _minimumOutputTokens);
        }

        if (options?.Temperature is not null)
        {
            payload["temperature"] = options.Temperature.Value;
        }

        if (responseOptions?.ServiceTier is not null)
        {
            payload["service_tier"] = responseOptions.ServiceTier;
        }

        if (context.Tools.Count > 0)
        {
            var tools = ConvertTools(context.Tools, compatibility);
            if (tools.Count > 0)
            {
                payload["tools"] = tools;
            }
        }

        if (responseOptions?.ToolChoice is not null)
        {
            payload["tool_choice"] = responseOptions.ToolChoice.DeepClone();
        }

        if (model.Reasoning)
        {
            if (!string.IsNullOrEmpty(responseOptions?.ReasoningEffort) || responseOptions?.ReasoningSummary is not null)
            {
                var effort = responseOptions?.ReasoningEffort;
                if (!string.IsNullOrEmpty(effort) && model.ThinkingLevelMap is not null &&
                    model.ThinkingLevelMap.TryGetValue(effort, out var mapped) && mapped is not null)
                {
                    effort = mapped;
                }

                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = effort ?? "medium",
                    ["summary"] = responseOptions?.ReasoningSummary ?? "auto",
                };
                payload["include"] = new JsonArray((JsonNode?)"reasoning.encrypted_content");
            }
            else if (model.Provider != "github-copilot" && !HasDisabledThinkingMapping(model))
            {
                var off = model.ThinkingLevelMap is not null &&
                          model.ThinkingLevelMap.TryGetValue(ThinkingLevels.Off, out var mappedOff)
                    ? mappedOff ?? "none"
                    : "none";
                payload["reasoning"] = new JsonObject { ["effort"] = off };
            }

            if (model.Provider == "xai")
            {
                payload["include"] = new JsonArray((JsonNode?)"reasoning.encrypted_content");
            }
        }

        if (model.SamplingParameters is not null)
        {
            ApplyJsonProperties(payload, model.SamplingParameters);
        }

        if (options?.SamplingParameters is not null)
        {
            ApplyJsonProperties(payload, options.SamplingParameters);
        }

        return payload;
    }

    /// <summary>Converts Pi context messages into Responses input items.</summary>
    public static JsonArray ConvertMessages(Model model, Context context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        return ConvertMessages(model, context, GetCompatibility(model));
    }

    /// <inheritdoc />
    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var stream = new AssistantMessageEventStream();
        _ = RunAsync(stream, model, context, options);
        return stream;
    }

    /// <inheritdoc />
    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        EnsureRequestAuth(model, options);
        var reasoning = options?.Reasoning;
        var clamped = string.IsNullOrEmpty(reasoning) ? null : ModelUtilities.ClampThinkingLevel(model, reasoning);
        var responseOptions = new OpenAiResponsesStreamOptions
        {
            Signal = options?.Signal ?? default,
            TelemetryContext = options?.TelemetryContext,
            ApiKey = options?.ApiKey,
            Fetch = options?.Fetch,
            Environment = options?.Environment,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            Headers = options?.Headers,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            Temperature = options?.Temperature,
            SamplingParameters = options?.SamplingParameters,
            MaxTokens = options?.MaxTokens,
            Transport = options?.Transport,
            CacheRetention = options?.CacheRetention,
            SessionId = options?.SessionId,
            WebSocketConnectTimeoutMs = options?.WebSocketConnectTimeoutMs,
            Metadata = options?.Metadata,
            ToolChoice = options?.ToolChoice is null ? null : JsonValue.Create(options.ToolChoice),
            ReasoningEffort = clamped is ThinkingLevels.Off ? null : clamped,
        };
        return Stream(model, context, responseOptions);
    }

    private async Task RunAsync(
        AssistantMessageEventStream stream,
        Model model,
        Context context,
        StreamOptions? options)
    {
        var output = CreatePendingMessage(model);
        try
        {
            EnsureRequestAuth(model, options);
            var payload = BuildPayload(model, context, options);
            using var response = await SendWithRetryAsync(model, context, payload, options).ConfigureAwait(false);
            if (response.Content is null)
            {
                throw new InvalidOperationException("Attempted to iterate over an OpenAI Responses response with no body");
            }

            await using var body = await response.Content.ReadAsStreamAsync(options?.Signal ?? default).ConfigureAwait(false);
            stream.Push(new StreamStartEvent(output));
            var state = new StreamState(output);
            await foreach (var sse in SseReader.ReadAsync(body, options?.Signal ?? default))
            {
                if (string.IsNullOrWhiteSpace(sse.Data))
                {
                    continue;
                }

                var node = JsonParseUtilities.ParseJsonWithRepair(sse.Data) as JsonObject;
                if (node is null)
                {
                    continue;
                }

                HandleEvent(state, stream, model, node);
            }

            if (options?.Signal.IsCancellationRequested == true)
            {
                throw new OperationCanceledException(options.Signal);
            }

            if (!state.SawTerminalResponseEvent)
            {
                throw new InvalidOperationException("OpenAI Responses stream ended before a terminal response event");
            }

            output = state.Output;
            if (output.StopReason == StopReasons.Pending)
            {
                throw new InvalidOperationException("OpenAI Responses stream ended without a stop reason");
            }

            if (output.StopReason is StopReasons.Error or StopReasons.Aborted)
            {
                throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
            }

            stream.Push(new StreamDoneEvent(output.StopReason, output));
            stream.End(output);
        }
        catch (Exception error)
        {
            var aborted = options?.Signal.IsCancellationRequested == true;
            output = output with
            {
                StopReason = aborted ? StopReasons.Aborted : StopReasons.Error,
                ErrorMessage = aborted
                    ? "Request was aborted"
                    : ErrorBodyUtilities.FormatProviderError(
                        ErrorBodyUtilities.NormalizeProviderError(error),
                        "OpenAI API error"),
            };
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Model model,
        Context context,
        JsonObject payload,
        StreamOptions? options)
    {
        async Task<HttpResponseMessage> SendAttempt()
        {
            try
            {
                return await _transport.SendAsync(
                        model,
                        HttpMethod.Post,
                        ResolveEndpoint(model.BaseUrl),
                        payload,
                        options,
                        BuildDefaultHeaders(model, context, options),
                        options?.Signal ?? default)
                    .ConfigureAwait(false);
            }
            catch (ProviderErrorMetadataException error)
            {
                throw new ProviderRetryException(
                    error.Message,
                    error.Metadata.Status ?? error.Metadata.StatusCode,
                    innerException: error);
            }
        }

        try
        {
            return await ProviderRetryUtilities.RetryProviderRequest(
                    SendAttempt,
                    options?.MaxRetries ?? 0,
                    options?.MaxRetryDelayMs,
                    options?.Signal ?? default)
                .ConfigureAwait(false);
        }
        catch (ProviderRetryException error) when (error.InnerException is ProviderErrorMetadataException original)
        {
            throw original;
        }
    }

    private static void HandleEvent(
        StreamState state,
        AssistantMessageEventStream stream,
        Model model,
        JsonObject node)
    {
        switch (StringValue(node["type"]))
        {
            case "response.created":
                if (node["response"] is JsonObject created && StringValue(created["id"]) is { } createdId)
                {
                    state.Output = state.Output with { ResponseId = createdId };
                }

                break;
            case "response.output_item.added":
                CreateSlot(state, stream, GetInt(node["output_index"]) ?? 0, node["item"] as JsonObject);
                break;
            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
                AppendThinkingDelta(state, stream, GetInt(node["output_index"]) ?? 0, StringValue(node["delta"]) ?? string.Empty);
                break;
            case "response.reasoning_summary_part.done":
                AppendThinkingDelta(state, stream, GetInt(node["output_index"]) ?? 0, "\n\n");
                break;
            case "response.output_text.delta":
            case "response.refusal.delta":
                AppendTextDelta(state, stream, GetInt(node["output_index"]) ?? 0, StringValue(node["delta"]) ?? string.Empty);
                break;
            case "response.function_call_arguments.delta":
                AppendFunctionArgumentsDelta(state, stream, GetInt(node["output_index"]) ?? 0, StringValue(node["delta"]) ?? string.Empty);
                break;
            case "response.function_call_arguments.done":
                CompleteFunctionArguments(state, stream, GetInt(node["output_index"]) ?? 0, StringValue(node["arguments"]));
                break;
            case "response.output_item.done":
                FinalizeOutputItem(state, stream, GetInt(node["output_index"]) ?? 0, node["item"] as JsonObject);
                break;
            case "response.completed":
            case "response.incomplete":
                FinalizeResponse(state, model, node["response"] as JsonObject ?? node);
                break;
            case "response.failed":
                throw new InvalidOperationException(FailedResponseMessage(node["response"] as JsonObject));
            case "error":
                throw new InvalidOperationException(
                    $"Error Code {StringValue(node["code"]) ?? "unknown"}: {StringValue(node["message"]) ?? "unknown"}");
        }
    }

    private static JsonArray ConvertMessages(Model model, Context context, ResponsesCompatibility compatibility)
    {
        var result = new JsonArray();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            result.Add((JsonNode?)new JsonObject
            {
                ["role"] = model.Reasoning && compatibility.SupportsDeveloperRole ? "developer" : "system",
                ["content"] = UnicodeUtilities.SanitizeSurrogates(context.SystemPrompt),
            });
        }

        var transformed = TransformMessages(model, context.Messages);
        var messageIndex = 0;
        foreach (var message in transformed)
        {
            switch (message)
            {
                case UserMessage user:
                    AddUserInput(result, user, model);
                    break;
                case AssistantMessage assistant:
                    AddAssistantInput(result, assistant, model, messageIndex);
                    break;
                case ToolResultMessage toolResult:
                    AddToolOutput(result, toolResult, model);
                    break;
            }

            messageIndex++;
        }

        return result;
    }

    private static void AddUserInput(JsonArray destination, UserMessage message, Model model)
    {
        if (message.Content is string text)
        {
            destination.Add((JsonNode?)new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeUtilities.SanitizeSurrogates(text),
                    },
                },
            });
            return;
        }

        if (message.Content is not IEnumerable<ContentBlock> blocks)
        {
            return;
        }

        var content = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContent textBlock:
                    content.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeUtilities.SanitizeSurrogates(textBlock.Text),
                    });
                    break;
                case ImageContent image when model.Input.Contains("image", StringComparer.OrdinalIgnoreCase):
                    content.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
                    });
                    break;
                case ImageContent:
                    content.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = "(image omitted: model does not support images)",
                    });
                    break;
            }
        }

        if (content.Count > 0)
        {
            destination.Add((JsonNode?)new JsonObject { ["role"] = "user", ["content"] = content });
        }
    }

    private static void AddAssistantInput(JsonArray destination, AssistantMessage message, Model model, int messageIndex)
    {
        if (message.StopReason is StopReasons.Error or StopReasons.Aborted)
        {
            return;
        }

        var sameProviderAndApi = string.Equals(message.Provider, model.Provider, StringComparison.Ordinal) &&
                                 string.Equals(message.Api, model.Api, StringComparison.Ordinal);
        var sameModel = sameProviderAndApi && string.Equals(message.Model, model.Id, StringComparison.Ordinal);
        var differentModel = sameProviderAndApi && !sameModel;
        var textIndex = 0;
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.ThinkingSignature):
                    if (JsonParseUtilities.ParseJsonWithRepair(thinking.ThinkingSignature) is JsonObject reasoning)
                    {
                        destination.Add((JsonNode?)reasoning);
                    }

                    break;
                case TextContent text:
                    var textId = ParseTextSignatureId(text.TextSignature) ??
                                 (textIndex == 0 ? $"msg_pi_{messageIndex}" : $"msg_pi_{messageIndex}_{textIndex}");
                    textIndex++;
                    if (textId.Length > 64)
                    {
                        textId = $"msg_{HashUtilities.ShortHash(textId)}";
                    }

                    var messageItem = new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            (JsonNode?)new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = UnicodeUtilities.SanitizeSurrogates(text.Text),
                                ["annotations"] = new JsonArray(),
                            },
                        },
                        ["status"] = "completed",
                        ["id"] = textId,
                    };
                    if (ParseTextSignaturePhase(text.TextSignature) is { } phase)
                    {
                        messageItem["phase"] = phase;
                    }

                    destination.Add((JsonNode?)messageItem);
                    break;
                case ToolCall toolCall:
                    var pieces = toolCall.Id.Split('|', 2);
                    var callId = NormalizeResponseIdPart(pieces[0]);
                    var rawItemId = pieces.Length > 1 ? pieces[1] : null;
                    var itemId = NormalizeResponseItemId(rawItemId, model, message, differentModel);
                    if (differentModel && itemId?.StartsWith("fc_", StringComparison.Ordinal) == true)
                    {
                        itemId = null;
                    }

                    var functionCall = new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = callId,
                        ["name"] = toolCall.Name,
                        ["arguments"] = toolCall.Arguments.ToJsonString(),
                    };
                    if (!string.IsNullOrEmpty(itemId))
                    {
                        functionCall["id"] = itemId;
                    }

                    if (sameModel && toolCall.Namespace is not null)
                    {
                        functionCall["namespace"] = toolCall.Namespace;
                    }

                    destination.Add((JsonNode?)functionCall);
                    break;
            }
        }
    }

    private static void AddToolOutput(JsonArray destination, ToolResultMessage message, Model model)
    {
        var callId = NormalizeResponseIdPart(message.ToolCallId.Split('|', 2)[0]);
        var text = string.Join("\n", message.Content.OfType<TextContent>().Select(static block => block.Text));
        var images = message.Content.OfType<ImageContent>().ToArray();
        JsonNode output;
        if (images.Length == 0 || !model.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            output = UnicodeUtilities.SanitizeSurrogates(
                text.Length > 0 ? text : images.Length > 0 ? "(see attached image)" : "(no tool output)");
        }
        else
        {
            var content = new JsonArray();
            if (text.Length > 0)
            {
                content.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = UnicodeUtilities.SanitizeSurrogates(text),
                });
            }

            foreach (var image in images)
            {
                content.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "input_image",
                    ["detail"] = "auto",
                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
                });
            }

            output = content;
        }

        destination.Add((JsonNode?)new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output,
        });
    }

    private static JsonArray ConvertTools(IReadOnlyList<Tool> tools, ResponsesCompatibility compatibility)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var schema = tool.Parameters.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object" };
            var strict = compatibility.SupportsStrictMode && tool.ConstrainedSampling is JsonSchemaSampling;
            var converted = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = schema,
                ["strict"] = strict,
            };
            result.Add((JsonNode?)converted);
        }

        return result;
    }

    private static List<Message> TransformMessages(Model model, IReadOnlyList<Message> messages)
    {
        var result = new List<Message>(messages.Count);
        var pending = new List<ToolCall>();
        var existingResults = new HashSet<string>(StringComparer.Ordinal);

        void FlushPending()
        {
            foreach (var call in pending)
            {
                var callId = call.Id.Split('|', 2)[0];
                if (!existingResults.Contains(callId))
                {
                    result.Add(new ToolResultMessage
                    {
                        ToolCallId = callId,
                        ToolName = call.Name,
                        Content = [new TextContent("No result provided")],
                        IsError = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                }
            }

            pending.Clear();
            existingResults.Clear();
        }

        foreach (var message in messages)
        {
            switch (message)
            {
                case AssistantMessage assistant:
                    FlushPending();
                    if (assistant.StopReason is StopReasons.Error or StopReasons.Aborted)
                    {
                        continue;
                    }

                    var sameModel = string.Equals(assistant.Provider, model.Provider, StringComparison.Ordinal) &&
                                    string.Equals(assistant.Api, model.Api, StringComparison.Ordinal) &&
                                    string.Equals(assistant.Model, model.Id, StringComparison.Ordinal);
                    var assistantContent = new List<ContentBlock>();
                    foreach (var block in assistant.Content)
                    {
                        if (block is ThinkingContent thinking && thinking.Redacted == true && !sameModel)
                        {
                            continue;
                        }

                        if (block is ThinkingContent thinkingBlock && !sameModel && thinkingBlock.Redacted != true)
                        {
                            if (!string.IsNullOrWhiteSpace(thinkingBlock.Thinking))
                            {
                                assistantContent.Add(new TextContent(thinkingBlock.Thinking));
                            }

                            continue;
                        }

                        assistantContent.Add(block);
                    }

                    var transformedAssistant = assistant with { Content = assistantContent };
                    result.Add(transformedAssistant);
                    pending.AddRange(assistantContent.OfType<ToolCall>());
                    break;
                case ToolResultMessage toolResult:
                    var callId = toolResult.ToolCallId.Split('|', 2)[0];
                    existingResults.Add(callId);
                    result.Add(toolResult);
                    break;
                case UserMessage user:
                    FlushPending();
                    result.Add(user);
                    break;
                default:
                    result.Add(message);
                    break;
            }
        }

        FlushPending();
        return result;
    }

    private static void CreateSlot(StreamState state, AssistantMessageEventStream stream, int outputIndex, JsonObject? item)
    {
        if (item is null || state.Slots.ContainsKey(outputIndex))
        {
            return;
        }

        var type = StringValue(item["type"]);
        var content = state.Output.Content.ToList();
        ActiveSlot? slot = null;
        switch (type)
        {
            case "reasoning":
                var thinking = new ThinkingContent(string.Empty);
                content.Add(thinking);
                slot = new ActiveSlot(outputIndex, content.Count - 1, "reasoning", thinking)
                {
                    ItemId = StringValue(item["id"]),
                };
                break;
            case "message":
                var text = new TextContent(string.Empty);
                content.Add(text);
                slot = new ActiveSlot(outputIndex, content.Count - 1, "message", text)
                {
                    ItemId = StringValue(item["id"]),
                };
                break;
            case "function_call":
                var functionCall = new ToolCall(
                    BuildToolCallId(item),
                    StringValue(item["name"]) ?? string.Empty,
                    JsonParseUtilities.ParseStreamingJson(StringValue(item["arguments"]) ?? string.Empty) as JsonObject ?? new JsonObject(),
                    Namespace: StringValue(item["namespace"]));
                content.Add(functionCall);
                slot = new ActiveSlot(outputIndex, content.Count - 1, "function_call", functionCall)
                {
                    ItemId = StringValue(item["id"]),
                    CallId = StringValue(item["call_id"]),
                    PartialJson = StringValue(item["arguments"]) ?? string.Empty,
                };
                break;
            case "custom_tool_call":
                var customCall = new ToolCall(
                    BuildToolCallId(item),
                    StringValue(item["name"]) ?? string.Empty,
                    new JsonObject { ["input"] = StringValue(item["input"]) ?? string.Empty },
                    Namespace: StringValue(item["namespace"]));
                content.Add(customCall);
                slot = new ActiveSlot(outputIndex, content.Count - 1, "custom_tool_call", customCall)
                {
                    ItemId = StringValue(item["id"]),
                    CallId = StringValue(item["call_id"]),
                    PartialJson = StringValue(item["input"]) ?? string.Empty,
                };
                break;
        }

        if (slot is null)
        {
            return;
        }

        state.Output = state.Output with { Content = content };
        state.Slots[outputIndex] = slot;
        switch (slot.Kind)
        {
            case "reasoning":
                stream.Push(new ThinkingStartEvent(slot.ContentIndex, state.Output));
                break;
            case "message":
                stream.Push(new TextStartEvent(slot.ContentIndex, state.Output));
                break;
            default:
                stream.Push(new ToolCallStartEvent(slot.ContentIndex, state.Output));
                break;
        }
    }

    private static void AppendThinkingDelta(
        StreamState state,
        AssistantMessageEventStream stream,
        int outputIndex,
        string delta)
    {
        if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "reasoning" || slot.Block is not ThinkingContent thinking)
        {
            return;
        }

        var updated = thinking with { Thinking = thinking.Thinking + delta };
        ReplaceSlot(state, slot, updated);
        stream.Push(new ThinkingDeltaEvent(slot.ContentIndex, delta, state.Output));
    }

    private static void AppendTextDelta(
        StreamState state,
        AssistantMessageEventStream stream,
        int outputIndex,
        string delta)
    {
        if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "message" || slot.Block is not TextContent text)
        {
            return;
        }

        var updated = text with { Text = text.Text + delta };
        ReplaceSlot(state, slot, updated);
        stream.Push(new TextDeltaEvent(slot.ContentIndex, delta, state.Output));
    }

    private static void AppendFunctionArgumentsDelta(
        StreamState state,
        AssistantMessageEventStream stream,
        int outputIndex,
        string delta)
    {
        if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "function_call" || slot.Block is not ToolCall toolCall)
        {
            return;
        }

        slot.PartialJson += delta;
        var arguments = JsonParseUtilities.ParseStreamingJson(slot.PartialJson) as JsonObject ?? new JsonObject();
        var updated = toolCall with { Arguments = arguments };
        ReplaceSlot(state, slot, updated);
        stream.Push(new ToolCallDeltaEvent(slot.ContentIndex, delta, state.Output));
    }

    private static void CompleteFunctionArguments(
        StreamState state,
        AssistantMessageEventStream stream,
        int outputIndex,
        string? arguments)
    {
        if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "function_call" || slot.Block is not ToolCall toolCall || arguments is null)
        {
            return;
        }

        var previous = slot.PartialJson;
        slot.PartialJson = arguments;
        var parsed = JsonParseUtilities.ParseStreamingJson(arguments) as JsonObject ?? new JsonObject();
        ReplaceSlot(state, slot, toolCall with { Arguments = parsed });
        if (arguments.StartsWith(previous, StringComparison.Ordinal) && arguments.Length > previous.Length)
        {
            stream.Push(new ToolCallDeltaEvent(slot.ContentIndex, arguments[previous.Length..], state.Output));
        }
    }

    private static void FinalizeOutputItem(
        StreamState state,
        AssistantMessageEventStream stream,
        int outputIndex,
        JsonObject? item)
    {
        if (item is null)
        {
            return;
        }

        if (!state.Slots.TryGetValue(outputIndex, out var slot))
        {
            CreateSlot(state, stream, outputIndex, item);
            state.Slots.TryGetValue(outputIndex, out slot);
        }

        if (slot is null)
        {
            return;
        }

        switch (slot.Kind)
        {
            case "reasoning" when slot.Block is ThinkingContent thinking:
                var summary = ExtractText(item["summary"], "text");
                var content = ExtractText(item["content"], "text");
                var finalThinking = summary.Length > 0 ? summary : content.Length > 0 ? content : thinking.Thinking;
                var finalThinkingBlock = thinking with
                {
                    Thinking = finalThinking,
                    ThinkingSignature = item.ToJsonString(),
                };
                ReplaceSlot(state, slot, finalThinkingBlock);
                state.ReasoningItems[StringValue(item["id"]) ?? string.Empty] = slot;
                stream.Push(new ThinkingEndEvent(slot.ContentIndex, finalThinking, state.Output));
                state.Slots.Remove(outputIndex);
                break;
            case "message" when slot.Block is TextContent text:
                var finalText = ExtractMessageText(item["content"]);
                var textBlock = text with
                {
                    Text = finalText.Length > 0 ? finalText : text.Text,
                    TextSignature = EncodeTextSignature(StringValue(item["id"]) ?? string.Empty, StringValue(item["phase"])),
                };
                ReplaceSlot(state, slot, textBlock);
                if (StringValue(item["phase"]) == "final_answer")
                {
                    state.Output = state.Output with { StopReason = StopReasons.Stop };
                }

                stream.Push(new TextEndEvent(slot.ContentIndex, textBlock.Text, state.Output));
                state.Slots.Remove(outputIndex);
                break;
            case "function_call" when slot.Block is ToolCall functionCall:
                var finalArguments = StringValue(item["arguments"]) ?? slot.PartialJson;
                var parsedArguments = JsonParseUtilities.ParseStreamingJson(finalArguments) as JsonObject ?? functionCall.Arguments;
                var finalTool = functionCall with
                {
                    Id = BuildToolCallId(item),
                    Name = StringValue(item["name"]) ?? functionCall.Name,
                    Arguments = parsedArguments,
                    Namespace = StringValue(item["namespace"]) ?? functionCall.Namespace,
                };
                ReplaceSlot(state, slot, finalTool);
                stream.Push(new ToolCallEndEvent(slot.ContentIndex, finalTool, state.Output));
                state.Slots.Remove(outputIndex);
                break;
            case "custom_tool_call" when slot.Block is ToolCall customCall:
                var input = StringValue(item["input"]) ?? GetStringArgument(customCall.Arguments, "input");
                var finalCustom = customCall with
                {
                    Id = BuildToolCallId(item),
                    Arguments = new JsonObject { ["input"] = input },
                    Namespace = StringValue(item["namespace"]) ?? customCall.Namespace,
                };
                ReplaceSlot(state, slot, finalCustom);
                stream.Push(new ToolCallEndEvent(slot.ContentIndex, finalCustom, state.Output));
                state.Slots.Remove(outputIndex);
                break;
        }
    }

    private static void FinalizeResponse(StreamState state, Model model, JsonObject response)
    {
        state.SawTerminalResponseEvent = true;
        if (StringValue(response["id"]) is { } responseId)
        {
            state.Output = state.Output with { ResponseId = responseId };
        }

        if (response["usage"] is JsonObject usage)
        {
            var inputTokens = GetInt(usage["input_tokens"]) ?? 0;
            var inputDetails = usage["input_tokens_details"] as JsonObject;
            var cacheRead = GetInt(inputDetails?["cached_tokens"]) ?? 0;
            var cacheWrite = GetInt(inputDetails?["cache_write_tokens"]) ?? 0;
            var outputTokens = GetInt(usage["output_tokens"]) ?? 0;
            var reasoning = GetInt((usage["output_tokens_details"] as JsonObject)?["reasoning_tokens"]) ?? 0;
            var parsedUsage = new Usage
            {
                Input = Math.Max(0, inputTokens - cacheRead - cacheWrite),
                Output = outputTokens,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                Reasoning = reasoning,
                TotalTokens = GetInt(usage["total_tokens"]) ?? 0,
            };
            ModelUtilities.CalculateCost(model, parsedUsage);
            state.Output = state.Output with { Usage = parsedUsage };
        }

        if (response["output"] is JsonArray output)
        {
            BackfillReasoningSignatures(state, output);
        }

        var status = StringValue(response["status"]);
        var incompleteReason = StringValue((response["incomplete_details"] as JsonObject)?["reason"]);
        var mapped = MapStopReason(status, incompleteReason);
        state.Output = state.Output with
        {
            StopReason = mapped.StopReason,
            RawStopReason = string.IsNullOrEmpty(incompleteReason) ? status : $"{status}.{incompleteReason}",
            ErrorMessage = mapped.ErrorMessage,
        };
        if (state.Output.Content.OfType<ToolCall>().Any() && state.Output.StopReason == StopReasons.Stop)
        {
            state.Output = state.Output with { StopReason = StopReasons.ToolUse };
        }
    }

    private static void BackfillReasoningSignatures(StreamState state, JsonArray output)
    {
        foreach (var item in output.OfType<JsonObject>())
        {
            if (StringValue(item["type"]) != "reasoning" || StringValue(item["id"]) is not { } id ||
                StringValue(item["encrypted_content"]) is not { } encrypted)
            {
                continue;
            }

            if (!state.ReasoningItems.TryGetValue(id, out var slot) || slot.Block is not ThinkingContent thinking ||
                string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
            {
                continue;
            }

            var signature = JsonParseUtilities.ParseJsonWithRepair(thinking.ThinkingSignature) as JsonObject;
            if (signature is null || signature["encrypted_content"] is not null)
            {
                continue;
            }

            signature["encrypted_content"] = encrypted;
            ReplaceSlot(state, slot, thinking with { ThinkingSignature = signature.ToJsonString() });
        }
    }

    private static void ReplaceSlot(StreamState state, ActiveSlot slot, ContentBlock replacement)
    {
        slot.Block = replacement;
        var content = state.Output.Content.ToList();
        content[slot.ContentIndex] = replacement;
        state.Output = state.Output with { Content = content };
    }

    private static string BuildToolCallId(JsonObject item)
    {
        var callId = StringValue(item["call_id"]) ?? string.Empty;
        var itemId = StringValue(item["id"]);
        return string.IsNullOrEmpty(itemId) ? callId : $"{callId}|{itemId}";
    }

    private static string ExtractMessageText(JsonNode? content)
    {
        if (content is not JsonArray blocks)
        {
            return string.Empty;
        }

        var values = new List<string>();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var type = StringValue(block["type"]);
            if (type is "output_text" or "refusal")
            {
                values.Add(StringValue(block["text"]) ?? StringValue(block["refusal"]) ?? string.Empty);
            }
        }

        return string.Concat(values);
    }

    private static string ExtractText(JsonNode? value, string field)
    {
        if (value is not JsonArray array)
        {
            return string.Empty;
        }

        return string.Join("\n\n", array.OfType<JsonObject>().Select(item => StringValue(item[field]) ?? string.Empty));
    }

    private static string? ParseTextSignatureId(string? signature)
    {
        if (string.IsNullOrEmpty(signature) || !signature.StartsWith('{'))
        {
            return signature;
        }

        try
        {
            return StringValue(JsonNode.Parse(signature)?["id"]);
        }
        catch
        {
            return signature;
        }
    }

    private static string? ParseTextSignaturePhase(string? signature)
    {
        if (string.IsNullOrEmpty(signature) || !signature.StartsWith('{'))
        {
            return null;
        }

        try
        {
            return StringValue(JsonNode.Parse(signature)?["phase"]);
        }
        catch
        {
            return null;
        }
    }

    private static string EncodeTextSignature(string id, string? phase)
    {
        var signature = new JsonObject
        {
            ["v"] = 1,
            ["id"] = id,
        };
        if (!string.IsNullOrEmpty(phase))
        {
            signature["phase"] = phase;
        }

        return signature.ToJsonString();
    }

    private static string GetStringArgument(JsonObject arguments, string key) => StringValue(arguments[key]) ?? string.Empty;

    private static string FailedResponseMessage(JsonObject? response)
    {
        if (response is null)
        {
            return "Unknown error (no error details in response)";
        }

        if (response["error"] is JsonObject error)
        {
            return $"{StringValue(error["code"]) ?? "unknown"}: {StringValue(error["message"]) ?? "no message"}";
        }

        var reason = StringValue((response["incomplete_details"] as JsonObject)?["reason"]);
        return !string.IsNullOrEmpty(reason) ? $"incomplete: {reason}" : "Unknown error (no error details in response)";
    }

    private static (string StopReason, string? ErrorMessage) MapStopReason(string? status, string? incompleteReason)
    {
        return status switch
        {
            null or "" => (StopReasons.Stop, null),
            "completed" => (StopReasons.Stop, null),
            "incomplete" when incompleteReason == "max_output_tokens" => (StopReasons.Length, null),
            "incomplete" => (StopReasons.Error,
                string.IsNullOrEmpty(incompleteReason)
                    ? "Response incomplete without a provider reason"
                    : $"Response incomplete: {incompleteReason}"),
            "failed" or "cancelled" => (StopReasons.Error, null),
            "in_progress" or "queued" => (StopReasons.Stop, null),
            _ => throw new InvalidOperationException($"Unhandled stop reason: {status}"),
        };
    }

    private static AssistantMessage CreatePendingMessage(Model model) => new()
    {
        Content = [],
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        StopReason = StopReasons.Pending,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static void EnsureRequestAuth(Model model, StreamOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.ApiKey) || HasHeader(model.Headers, "authorization") ||
            HasHeader(model.Headers, "cf-aig-authorization") || HasHeader(options?.Headers, "authorization") ||
            HasHeader(options?.Headers, "cf-aig-authorization"))
        {
            return;
        }

        throw new InvalidOperationException($"No API key for provider: {model.Provider}");
    }

    private static bool HasHeader<T>(IReadOnlyDictionary<string, T>? headers, string name)
    {
        if (headers is null)
        {
            return false;
        }

        return headers.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) &&
                                   pair.Value is string value && !string.IsNullOrWhiteSpace(value));
    }

    private static Uri ResolveEndpoint(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(normalized, UriKind.Absolute), "responses");
    }

    private static Dictionary<string, string?> BuildDefaultHeaders(Model model, Context context, StreamOptions? options)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PiUserAgent.GetPiUserAgent(),
        };
        var compatibility = GetCompatibility(model);
        if (!string.IsNullOrEmpty(options?.SessionId))
        {
            if (compatibility.SessionAffinityFormat == "openrouter")
            {
                headers["x-session-id"] = options.SessionId;
            }
            else
            {
                if (compatibility.SessionAffinityFormat == "openai")
                {
                    headers["session_id"] = options.SessionId;
                }

                headers["x-client-request-id"] = options.SessionId;
            }
        }

        return headers;
    }

    private static string ResolveCacheRetention(StreamOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.CacheRetention))
        {
            return options.CacheRetention;
        }

        return string.Equals(
                ProviderEnvironmentUtilities.GetProviderEnvValue("PI_CACHE_RETENTION", options?.Environment),
                CacheRetentions.Long,
                StringComparison.Ordinal)
            ? CacheRetentions.Long
            : CacheRetentions.Short;
    }

    private static string? ClampPromptCacheKey(string? key) =>
        key is null ? null : key.Length <= 64 ? key : key[..64];

    private static bool HasDisabledThinkingMapping(Model model) =>
        model.ThinkingLevelMap is not null &&
        model.ThinkingLevelMap.TryGetValue(ThinkingLevels.Off, out var mapped) &&
        mapped is null;

    private static void ApplyJsonProperties(JsonObject destination, IReadOnlyDictionary<string, JsonNode?> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static int? GetInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>();
        }
        catch
        {
            try
            {
                return node?.GetValue<long>() is { } value ? checked((int)value) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? StringValue(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeResponseIdPart(string value)
    {
        var sanitized = new string(value.Select(static character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        sanitized = sanitized.Length > 64 ? sanitized[..64] : sanitized;
        return sanitized.TrimEnd('_');
    }

    private static string? NormalizeResponseItemId(
        string? rawItemId,
        Model model,
        AssistantMessage source,
        bool differentModel)
    {
        if (string.IsNullOrEmpty(rawItemId))
        {
            return null;
        }

        var foreign = !string.Equals(source.Provider, model.Provider, StringComparison.Ordinal) ||
                      !string.Equals(source.Api, model.Api, StringComparison.Ordinal);
        if (foreign && (model.Provider is "openai" or "openai-codex" or "opencode"))
        {
            return $"fc_{HashUtilities.ShortHash(rawItemId)}";
        }

        if (differentModel && rawItemId.StartsWith("fc_", StringComparison.Ordinal))
        {
            return rawItemId;
        }

        var normalized = NormalizeResponseIdPart(rawItemId);
        if ((model.Provider is "openai" or "openai-codex" or "opencode") &&
            !normalized.StartsWith("fc_", StringComparison.Ordinal))
        {
            normalized = NormalizeResponseIdPart($"fc_{normalized}");
        }

        return normalized;
    }

    private static ResponsesCompatibility GetCompatibility(Model model)
    {
        var openRouter = model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
        return new ResponsesCompatibility(
            GetBool(model.Compatibility, "supportsDeveloperRole", true),
            GetString(model.Compatibility, "sessionAffinityFormat") ?? (openRouter ? "openrouter" : "openai"),
            GetBool(model.Compatibility, "supportsLongCacheRetention", true),
            GetBool(model.Compatibility, "supportsStrictMode", false),
            GetBool(model.Compatibility, "supportsExplicitPromptCacheMode", false));
    }

    private static bool GetBool(JsonObject? value, string key, bool fallback)
    {
        try
        {
            return value?[key]?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string? GetString(JsonObject? value, string key)
    {
        try
        {
            return value?[key]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private sealed class StreamState(AssistantMessage output)
    {
        public AssistantMessage Output { get; set; } = output;

        public Dictionary<int, ActiveSlot> Slots { get; } = [];

        public Dictionary<string, ActiveSlot> ReasoningItems { get; } = new(StringComparer.Ordinal);

        public bool SawTerminalResponseEvent { get; set; }
    }

    private sealed class ActiveSlot(int outputIndex, int contentIndex, string kind, ContentBlock block)
    {
        public int OutputIndex { get; } = outputIndex;

        public int ContentIndex { get; } = contentIndex;

        public string Kind { get; } = kind;

        public ContentBlock Block { get; set; } = block;

        public string? ItemId { get; set; }

        public string? CallId { get; set; }

        public string PartialJson { get; set; } = string.Empty;
    }

    private readonly record struct ResponsesCompatibility(
        bool SupportsDeveloperRole,
        string SessionAffinityFormat,
        bool SupportsLongCacheRetention,
        bool SupportsStrictMode,
        bool SupportsExplicitPromptCacheMode);
}
