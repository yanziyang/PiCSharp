using System.Globalization;
using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Mistral Conversations provider-specific stream options.</summary>
public sealed class MistralOptions : StreamOptions
{
    /// <summary>Mistral tool choice as a string or named-function object.</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>Prompt mode used by Magistral reasoning models.</summary>
    public string? PromptMode { get; init; }

    /// <summary>Reasoning effort used by Mistral Small and Medium 3.5 models.</summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>Raw HTTP/SSE implementation of Pi's Mistral Conversations API.</summary>
public sealed class MistralConversationsProvider : ProviderStreams
{
    private const int _mistralToolCallIdLength = 9;
    private const int _maxErrorBodyChars = 4000;
    private readonly ProviderHttpClient _transport;

    /// <summary>Creates an adapter backed by the supplied HTTP transport.</summary>
    public MistralConversationsProvider(ProviderHttpClient? transport = null)
    {
        _transport = transport ?? new ProviderHttpClient();
    }

    /// <summary>Builds the SDK-shaped Mistral streaming payload before wire-name conversion.</summary>
    public static JsonObject BuildPayload(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var mistralOptions = options as MistralOptions;
        var normalizer = CreateMistralToolCallIdNormalizer();
        var messages = TransformMessages(model, context.Messages, normalizer);
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["messages"] = ToChatMessages(messages, model.Input.Contains("image", StringComparer.OrdinalIgnoreCase)),
        };

        if (context.Tools.Count > 0)
        {
            payload["tools"] = ToFunctionTools(context.Tools);
        }

        if (options?.Temperature is not null)
        {
            payload["temperature"] = options.Temperature.Value;
        }

        if (options?.MaxTokens is not null)
        {
            payload["maxTokens"] = options.MaxTokens.Value;
        }

        if (mistralOptions?.ToolChoice is not null)
        {
            payload["toolChoice"] = MapToolChoice(mistralOptions.ToolChoice);
        }

        if (!string.IsNullOrEmpty(mistralOptions?.PromptMode))
        {
            payload["promptMode"] = mistralOptions.PromptMode;
        }

        if (!string.IsNullOrEmpty(mistralOptions?.ReasoningEffort))
        {
            payload["reasoningEffort"] = mistralOptions.ReasoningEffort;
        }

        if (ShouldUsePromptCaching(options))
        {
            payload["promptCacheKey"] = options!.SessionId;
        }

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var existing = payload["messages"] as JsonArray ?? new JsonArray();
            existing.Insert(
                0,
                (JsonNode?)new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = UnicodeUtilities.SanitizeSurrogates(context.SystemPrompt),
                });
            payload["messages"] = existing;
        }

        return payload;
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
        if (string.IsNullOrEmpty(options?.ApiKey))
        {
            throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        }

        var reasoning = string.IsNullOrEmpty(options.Reasoning)
            ? null
            : ModelUtilities.ClampThinkingLevel(model, options.Reasoning);
        var useReasoning = model.Reasoning && reasoning is not null && reasoning != ThinkingLevels.Off;
        var mistralOptions = CopyCommonOptions(model, options, new MistralOptions
        {
            ToolChoice = options.ToolChoice is null ? null : JsonValue.Create(options.ToolChoice),
            PromptMode = useReasoning && UsesPromptModeReasoning(model) ? "reasoning" : null,
            ReasoningEffort = useReasoning && UsesReasoningEffort(model)
                ? MapReasoningEffort(model, reasoning!)
                : null,
        });
        return Stream(model, context, mistralOptions);
    }

    private async Task RunAsync(
        AssistantMessageEventStream stream,
        Model model,
        Context context,
        StreamOptions? options)
    {
        var output = CreateOutput(model);
        try
        {
            if (string.IsNullOrEmpty(options?.ApiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }

            var payload = BuildPayload(model, context, options);
            var nextPayload = options.OnPayload is null
                ? payload
                : await options.OnPayload(payload, model).ConfigureAwait(false) ?? payload;
            if (nextPayload is not JsonObject objectPayload)
            {
                throw new InvalidOperationException("Mistral payload callback must return an object");
            }

            var wirePayload = ToMistralWirePayload(objectPayload);
            using var response = await SendAsync(model, wirePayload, options).ConfigureAwait(false);
            if (response.Content is null)
            {
                throw new InvalidOperationException("Mistral response has no body");
            }

            await using var body = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);
            stream.Push(new StreamStartEvent(output));
            using var streamCancellation = CreateStreamCancellation(options);
            var state = new StreamState(output);
            await foreach (var sse in SseReader.ReadAsync(body, streamCancellation.Token))
            {
                if (string.IsNullOrWhiteSpace(sse.Data))
                {
                    continue;
                }

                if (sse.Data.Trim() == "[DONE]")
                {
                    break;
                }

                var node = JsonParseUtilities.ParseJsonWithRepair(sse.Data) as JsonObject;
                if (node is null || node["choices"] is not JsonArray)
                {
                    throw new InvalidOperationException("Invalid Mistral streaming event");
                }

                HandleChunk(state, stream, model, node);
            }

            CloseCurrentBlock(state, stream);
            FinalizeToolCalls(state, stream);
            output = state.Output;
            if (streamCancellation.IsCancellationRequested && !options.Signal.IsCancellationRequested)
            {
                throw new TimeoutException("Mistral request timeout");
            }

            if (options.Signal.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was aborted", options.Signal);
            }

            if (!state.HasFinishReason)
            {
                throw new InvalidOperationException("Mistral stream ended without a finish reason");
            }

            if (output.StopReason is StopReasons.Error or StopReasons.Aborted)
            {
                throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
            }

            stream.Push(new StreamDoneEvent(output.StopReason, output));
            stream.End(output);
        }
        catch (OperationCanceledException) when (options?.Signal.IsCancellationRequested == true)
        {
            output = output with
            {
                StopReason = StopReasons.Aborted,
                ErrorMessage = "Request was aborted",
            };
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
        catch (OperationCanceledException)
        {
            output = output with
            {
                StopReason = StopReasons.Error,
                ErrorMessage = "Mistral request timeout",
            };
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
        catch (Exception error)
        {
            output = output with
            {
                StopReason = StopReasons.Error,
                ErrorMessage = FormatMistralError(error),
            };
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Model model, JsonObject payload, StreamOptions options)
    {
        try
        {
            var response = await _transport.SendAsync(
                    model,
                    HttpMethod.Post,
                    ResolveEndpoint(model.BaseUrl),
                    payload,
                    TransportOptions(options),
                    BuildDefaultHeaders(model, options),
                    options.Signal)
                .ConfigureAwait(false);

            if (options.OnResponse is not null)
            {
                await options.OnResponse(
                        new ProviderResponse((int)response.StatusCode, ReadResponseHeaders(response)),
                        model)
                    .ConfigureAwait(false);
            }

            return response;
        }
        catch (ProviderErrorMetadataException error)
        {
            throw new MistralHttpException(
                error.Metadata.Status ?? error.Metadata.StatusCode,
                error.Metadata.ResponseBody ?? error.Metadata.Body,
                error);
        }
    }

    private static void HandleChunk(
        StreamState state,
        AssistantMessageEventStream stream,
        Model model,
        JsonObject chunk)
    {
        if (StringValue(chunk["id"]) is { Length: > 0 } responseId && state.Output.ResponseId is null)
        {
            state.Output = state.Output with { ResponseId = responseId };
        }

        if (chunk["usage"] is JsonObject usage)
        {
            state.Output = state.Output with { Usage = ParseUsage(model, usage) };
        }

        if (chunk["choices"] is not JsonArray choices || choices.Count == 0 || choices[0] is not JsonObject choice)
        {
            return;
        }

        if (StringValue(choice["finish_reason"]) is { } finishReason)
        {
            var mapped = MapStopReason(finishReason);
            state.HasFinishReason = true;
            state.Output = state.Output with
            {
                RawStopReason = finishReason,
                StopReason = mapped.StopReason,
                ErrorMessage = mapped.ErrorMessage,
            };
        }

        var delta = choice["delta"] as JsonObject ?? new JsonObject();
        if (delta["content"] is JsonValue contentValue)
        {
            var text = StringValue(contentValue);
            if (text is not null)
            {
                AppendText(state, stream, UnicodeUtilities.SanitizeSurrogates(text));
            }
        }
        else if (delta["content"] is JsonArray contentItems)
        {
            foreach (var item in contentItems.OfType<JsonObject>())
            {
                HandleContentItem(state, stream, item);
            }
        }

        if (delta["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var toolCall in toolCalls.OfType<JsonObject>())
            {
                HandleToolCall(state, stream, toolCall);
            }
        }
    }

    private static void HandleContentItem(StreamState state, AssistantMessageEventStream stream, JsonObject item)
    {
        var type = StringValue(item["type"]);
        if (type == "thinking")
        {
            var thinking = string.Empty;
            if (item["thinking"] is JsonArray thinkingParts)
            {
                thinking = string.Concat(
                    thinkingParts.OfType<JsonObject>().Select(part => StringValue(part["text"]) ?? string.Empty));
            }

            if (thinking.Length > 0)
            {
                AppendThinking(state, stream, UnicodeUtilities.SanitizeSurrogates(thinking));
            }

            return;
        }

        if (type == "text")
        {
            AppendText(state, stream, UnicodeUtilities.SanitizeSurrogates(StringValue(item["text"]) ?? string.Empty));
        }
    }

    private static void AppendText(StreamState state, AssistantMessageEventStream stream, string delta)
    {
        CloseIfCurrentType(state, stream, typeof(ThinkingContent));
        if (state.CurrentIndex is null)
        {
            var content = state.Output.Content.ToList();
            content.Add(new TextContent(string.Empty));
            state.CurrentIndex = content.Count - 1;
            state.Output = state.Output with { Content = content };
            stream.Push(new TextStartEvent(state.CurrentIndex.Value, state.Output));
        }

        var index = state.CurrentIndex.Value;
        var current = (TextContent)state.Output.Content[index];
        var updated = state.Output.Content.ToList();
        updated[index] = current with { Text = current.Text + delta };
        state.Output = state.Output with { Content = updated };
        stream.Push(new TextDeltaEvent(index, delta, state.Output));
    }

    private static void AppendThinking(StreamState state, AssistantMessageEventStream stream, string delta)
    {
        CloseIfCurrentType(state, stream, typeof(TextContent));
        if (state.CurrentIndex is null)
        {
            var content = state.Output.Content.ToList();
            content.Add(new ThinkingContent(string.Empty));
            state.CurrentIndex = content.Count - 1;
            state.Output = state.Output with { Content = content };
            stream.Push(new ThinkingStartEvent(state.CurrentIndex.Value, state.Output));
        }

        var index = state.CurrentIndex.Value;
        var current = (ThinkingContent)state.Output.Content[index];
        var updated = state.Output.Content.ToList();
        updated[index] = current with { Thinking = current.Thinking + delta };
        state.Output = state.Output with { Content = updated };
        stream.Push(new ThinkingDeltaEvent(index, delta, state.Output));
    }

    private static void HandleToolCall(StreamState state, AssistantMessageEventStream stream, JsonObject toolCall)
    {
        CloseCurrentBlock(state, stream);
        var function = toolCall["function"] as JsonObject ?? new JsonObject();
        var id = StringValue(toolCall["id"]);
        var callId = !string.IsNullOrEmpty(id) && id != "null"
            ? id
            : DeriveMistralToolCallId($"toolcall:{IntValue(toolCall["index"]) ?? 0}", 0);
        var key = IntValue(toolCall["index"]) is { } index
            ? $"index:{index.ToString(CultureInfo.InvariantCulture)}"
            : $"id:{callId}";
        if (!state.ToolCalls.TryGetValue(key, out var active))
        {
            active = new ActiveToolCall
            {
                Id = callId,
                Name = StringValue(function["name"]) ?? string.Empty,
                ContentIndex = state.Output.Content.Count,
            };
            state.ToolCalls[key] = active;
            var content = state.Output.Content.ToList();
            content.Add(new ToolCall(active.Id, active.Name, new JsonObject()));
            state.Output = state.Output with { Content = content };
            stream.Push(new ToolCallStartEvent(active.ContentIndex, state.Output));
        }

        var argsDelta = StringValue(function["arguments"]);
        if (argsDelta is null && function["arguments"] is { } arguments)
        {
            argsDelta = arguments.ToJsonString();
        }

        argsDelta ??= "{}";
        active.PartialArguments += argsDelta;
        var parsed = JsonParseUtilities.ParseStreamingJson(active.PartialArguments) as JsonObject ?? new JsonObject();
        var updated = state.Output.Content.ToList();
        updated[active.ContentIndex] = new ToolCall(active.Id, active.Name, parsed);
        state.Output = state.Output with { Content = updated };
        stream.Push(new ToolCallDeltaEvent(active.ContentIndex, argsDelta, state.Output));
    }

    private static void CloseIfCurrentType(
        StreamState state,
        AssistantMessageEventStream stream,
        Type typeToClose)
    {
        if (state.CurrentIndex is { } index && state.Output.Content[index].GetType() == typeToClose)
        {
            CloseCurrentBlock(state, stream);
        }
        else if (state.CurrentIndex is { } current && state.Output.Content[current] is not TextContent and not ThinkingContent)
        {
            state.CurrentIndex = null;
        }
    }

    private static void CloseCurrentBlock(StreamState state, AssistantMessageEventStream stream)
    {
        if (state.CurrentIndex is not { } index)
        {
            return;
        }

        switch (state.Output.Content[index])
        {
            case TextContent text:
                stream.Push(new TextEndEvent(index, text.Text, state.Output));
                break;
            case ThinkingContent thinking:
                stream.Push(new ThinkingEndEvent(index, thinking.Thinking, state.Output));
                break;
        }

        state.CurrentIndex = null;
    }

    private static void FinalizeToolCalls(StreamState state, AssistantMessageEventStream stream)
    {
        foreach (var active in state.ToolCalls.Values.OrderBy(value => value.ContentIndex))
        {
            var parsed = JsonParseUtilities.ParseStreamingJson(active.PartialArguments) as JsonObject ?? new JsonObject();
            var finalized = new ToolCall(active.Id, active.Name, parsed);
            var content = state.Output.Content.ToList();
            content[active.ContentIndex] = finalized;
            state.Output = state.Output with { Content = content };
            stream.Push(new ToolCallEndEvent(active.ContentIndex, finalized, state.Output));
        }
    }

    private static Usage ParseUsage(Model model, JsonObject usage)
    {
        var prompt = IntValue(usage["prompt_tokens"]) ?? 0;
        var cache = GetMistralCachedPromptTokens(usage, prompt);
        var output = IntValue(usage["completion_tokens"]) ?? 0;
        var total = IntValue(usage["total_tokens"]) ?? prompt - cache + output + cache;
        var result = new Usage
        {
            Input = Math.Max(0, prompt - cache),
            Output = output,
            CacheRead = cache,
            CacheWrite = 0,
            TotalTokens = total,
        };
        ModelUtilities.CalculateCost(model, result);
        return result;
    }

    private static int GetMistralCachedPromptTokens(JsonObject usage, int promptTokens)
    {
        var details = usage["prompt_tokens_details"] as JsonObject ?? usage["promptTokensDetails"] as JsonObject;
        var cached = IntValue(details?["cached_tokens"] ?? details?["cachedTokens"]);
        cached ??= IntValue(usage["num_cached_tokens"] ?? usage["numCachedTokens"]);
        return Math.Min(promptTokens, Math.Max(0, cached ?? 0));
    }

    private static (string StopReason, string? ErrorMessage) MapStopReason(string reason) => reason switch
    {
        "stop" => (StopReasons.Stop, null),
        "length" or "model_length" => (StopReasons.Length, null),
        "tool_calls" => (StopReasons.ToolUse, null),
        "error" => (StopReasons.Error, "Provider stopped with: error"),
        _ => (StopReasons.Error, $"Provider stopped with: {reason}"),
    };

    private static JsonObject ToMistralWirePayload(JsonObject payload)
    {
        var wire = CloneObject(payload);
        foreach (var (source, target) in new[]
                 {
                     ("topP", "top_p"), ("maxTokens", "max_tokens"), ("randomSeed", "random_seed"),
                     ("responseFormat", "response_format"), ("toolChoice", "tool_choice"),
                     ("presencePenalty", "presence_penalty"), ("frequencyPenalty", "frequency_penalty"),
                     ("parallelToolCalls", "parallel_tool_calls"), ("reasoningEffort", "reasoning_effort"),
                     ("promptMode", "prompt_mode"), ("promptCacheKey", "prompt_cache_key"),
                     ("safePrompt", "safe_prompt"),
                 })
        {
            Remap(wire, source, target);
        }

        if (wire["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                Remap(message, "toolCalls", "tool_calls");
                Remap(message, "toolCallId", "tool_call_id");
                if (message["content"] is JsonArray chunks)
                {
                    foreach (var chunk in chunks.OfType<JsonObject>())
                    {
                        Remap(chunk, "imageUrl", "image_url");
                        Remap(chunk, "documentUrl", "document_url");
                        Remap(chunk, "documentName", "document_name");
                        Remap(chunk, "fileId", "file_id");
                        Remap(chunk, "referenceIds", "reference_ids");
                        Remap(chunk, "inputAudio", "input_audio");
                    }
                }
            }
        }

        if (wire["response_format"] is JsonObject responseFormat)
        {
            Remap(responseFormat, "jsonSchema", "json_schema");
            if (responseFormat["json_schema"] is JsonObject jsonSchema)
            {
                Remap(jsonSchema, "schemaDefinition", "schema");
            }
        }

        return wire;
    }

    private static JsonArray ToChatMessages(IReadOnlyList<Message> messages, bool supportsImages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    AddUserMessage(result, user, supportsImages);
                    break;
                case AssistantMessage assistant:
                    AddAssistantMessage(result, assistant);
                    break;
                case ToolResultMessage toolResult:
                    AddToolMessage(result, toolResult, supportsImages);
                    break;
            }
        }

        return result;
    }

    private static void AddUserMessage(JsonArray result, UserMessage message, bool supportsImages)
    {
        if (message.Content is string text)
        {
            result.Add((JsonNode?)new JsonObject
            {
                ["role"] = "user",
                ["content"] = UnicodeUtilities.SanitizeSurrogates(text),
            });
            return;
        }

        var blocks = message.Content as IEnumerable<ContentBlock>;
        if (blocks is null)
        {
            return;
        }

        var content = new JsonArray();
        foreach (var block in blocks)
        {
            if (block is TextContent textBlock)
            {
                content.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = UnicodeUtilities.SanitizeSurrogates(textBlock.Text),
                });
            }
            else if (block is ImageContent image && supportsImages)
            {
                content.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "image_url",
                    ["imageUrl"] = $"data:{image.MimeType};base64,{image.Data}",
                });
            }
        }

        if (content.Count > 0)
        {
            result.Add((JsonNode?)new JsonObject { ["role"] = "user", ["content"] = content });
        }
    }

    private static void AddAssistantMessage(JsonArray result, AssistantMessage message)
    {
        var content = new JsonArray();
        var toolCalls = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    content.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = UnicodeUtilities.SanitizeSurrogates(text.Text),
                    });
                    break;
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    content.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = new JsonArray
                        {
                            (JsonNode?)new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = UnicodeUtilities.SanitizeSurrogates(thinking.Thinking),
                            },
                        },
                    });
                    break;
                case ToolCall toolCall:
                    toolCalls.Add((JsonNode?)new JsonObject
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.Arguments.ToJsonString(),
                        },
                        ["index"] = 0,
                    });
                    break;
            }
        }

        if (content.Count == 0 && toolCalls.Count == 0)
        {
            return;
        }

        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["prefix"] = false,
        };
        if (content.Count > 0)
        {
            assistant["content"] = content;
        }

        if (toolCalls.Count > 0)
        {
            assistant["toolCalls"] = toolCalls;
        }

        result.Add((JsonNode?)assistant);
    }

    private static void AddToolMessage(JsonArray result, ToolResultMessage message, bool supportsImages)
    {
        var text = string.Join(
            "\n",
            message.Content.OfType<TextContent>().Select(static part => UnicodeUtilities.SanitizeSurrogates(part.Text)));
        var hasImages = message.Content.OfType<ImageContent>().Any();
        var toolText = BuildToolResultText(text, hasImages, supportsImages, message.IsError);
        var content = new JsonArray
        {
            (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = toolText },
        };
        if (supportsImages)
        {
            foreach (var image in message.Content.OfType<ImageContent>())
            {
                content.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "image_url",
                    ["imageUrl"] = $"data:{image.MimeType};base64,{image.Data}",
                });
            }
        }

        result.Add((JsonNode?)new JsonObject
        {
            ["role"] = "tool",
            ["toolCallId"] = message.ToolCallId,
            ["name"] = message.ToolName,
            ["content"] = content,
        });
    }

    private static string BuildToolResultText(string text, bool hasImages, bool supportsImages, bool isError)
    {
        var trimmed = text.Trim();
        var prefix = isError ? "[tool error] " : string.Empty;
        if (trimmed.Length > 0)
        {
            var suffix = hasImages && !supportsImages
                ? "\n[tool image omitted: model does not support images]"
                : string.Empty;
            return prefix + trimmed + suffix;
        }

        if (hasImages)
        {
            if (supportsImages)
            {
                return prefix + "(see attached image)";
            }

            return prefix + "(image omitted: model does not support images)";
        }

        return prefix + "(no tool output)";
    }

    private static List<Message> TransformMessages(
        Model model,
        IReadOnlyList<Message> messages,
        Func<string, string> normalizeToolCallId)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var transformed = new List<Message>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    transformed.Add(DowngradeUserImages(model, user));
                    break;
                case ToolResultMessage toolResult:
                    var normalized = idMap.TryGetValue(toolResult.ToolCallId, out var mapped)
                        ? mapped
                        : toolResult.ToolCallId;
                    transformed.Add(DowngradeToolImages(model, toolResult) with { ToolCallId = normalized });
                    break;
                case AssistantMessage assistant:
                    var sameModel = assistant.Provider == model.Provider && assistant.Api == model.Api && assistant.Model == model.Id;
                    var blocks = new List<ContentBlock>();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case ThinkingContent thinking:
                                if (thinking.Redacted == true)
                                {
                                    if (sameModel) blocks.Add(thinking);
                                }
                                else if (sameModel && !string.IsNullOrEmpty(thinking.ThinkingSignature))
                                {
                                    blocks.Add(thinking);
                                }
                                else if (!string.IsNullOrWhiteSpace(thinking.Thinking))
                                {
                                    blocks.Add(sameModel ? thinking : new TextContent(thinking.Thinking));
                                }

                                break;
                            case TextContent text:
                                blocks.Add(sameModel ? text : new TextContent(text.Text));
                                break;
                            case ToolCall toolCall:
                                var id = toolCall.Id;
                                if (!sameModel)
                                {
                                    var normalizedId = normalizeToolCallId(id);
                                    if (normalizedId != id) idMap[id] = normalizedId;
                                    id = normalizedId;
                                }

                                blocks.Add(new ToolCall(id, toolCall.Name, toolCall.Arguments.DeepClone() as JsonObject ?? new JsonObject(), sameModel ? toolCall.ThoughtSignature : null));
                                break;
                        }
                    }

                    transformed.Add(assistant with { Content = blocks });
                    break;
            }
        }

        var result = new List<Message>(transformed.Count);
        var pending = new List<ToolCall>();
        var existingResults = new HashSet<string>(StringComparer.Ordinal);
        void InsertSyntheticResults()
        {
            foreach (var call in pending)
            {
                if (!existingResults.Contains(call.Id))
                {
                    result.Add(new ToolResultMessage
                    {
                        ToolCallId = call.Id,
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

        foreach (var message in transformed)
        {
            if (message is AssistantMessage assistant)
            {
                InsertSyntheticResults();
                if (assistant.StopReason is StopReasons.Error or StopReasons.Aborted) continue;
                pending.AddRange(assistant.Content.OfType<ToolCall>());
                result.Add(assistant);
            }
            else if (message is ToolResultMessage toolResult)
            {
                existingResults.Add(toolResult.ToolCallId);
                result.Add(toolResult);
            }
            else
            {
                InsertSyntheticResults();
                result.Add(message);
            }
        }

        InsertSyntheticResults();
        return result;
    }

    private static UserMessage DowngradeUserImages(Model model, UserMessage message)
    {
        if (model.Input.Contains("image", StringComparer.OrdinalIgnoreCase) || message.Content is string)
        {
            return message;
        }

        if (message.Content is not IEnumerable<ContentBlock> blocks)
        {
            return message;
        }

        return message with { Content = ReplaceImages(blocks, "(image omitted: model does not support images)") };
    }

    private static ToolResultMessage DowngradeToolImages(Model model, ToolResultMessage message)
    {
        if (model.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            return message;
        }

        return message with
        {
            Content = ReplaceImages(message.Content, "(tool image omitted: model does not support images)"),
        };
    }

    private static List<ContentBlock> ReplaceImages(IEnumerable<ContentBlock> blocks, string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;
        foreach (var block in blocks)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder) result.Add(new TextContent(placeholder));
                previousWasPlaceholder = true;
                continue;
            }

            result.Add(block);
            previousWasPlaceholder = block is TextContent text && text.Text == placeholder;
        }

        return result;
    }

    private static Func<string, string> CreateMistralToolCallIdNormalizer()
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverseMap = new Dictionary<string, string>(StringComparer.Ordinal);
        return id =>
        {
            if (idMap.TryGetValue(id, out var existing)) return existing;
            var attempt = 0;
            while (true)
            {
                var candidate = DeriveMistralToolCallId(id, attempt);
                if (!reverseMap.TryGetValue(candidate, out var owner) || owner == id)
                {
                    idMap[id] = candidate;
                    reverseMap[candidate] = id;
                    return candidate;
                }

                attempt++;
            }
        };
    }

    private static string DeriveMistralToolCallId(string id, int attempt)
    {
        var normalized = new string(id.Where(char.IsLetterOrDigit).ToArray());
        if (attempt == 0 && normalized.Length == _mistralToolCallIdLength) return normalized;
        var seed = attempt == 0 ? normalized.Length > 0 ? normalized : id : $"{(normalized.Length > 0 ? normalized : id)}:{attempt}";
        return HashUtilities.ShortHash(seed)
            .Where(char.IsLetterOrDigit)
            .Take(_mistralToolCallIdLength)
            .Aggregate(new System.Text.StringBuilder(), static (builder, character) => builder.Append(character))
            .ToString();
    }

    private static JsonArray ToFunctionTools(IReadOnlyList<Tool> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var schema = ResolveToolSchema(tool, out var strict);
            result.Add((JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = schema,
                    ["strict"] = strict,
                },
            });
        }

        return result;
    }

    private static JsonNode MapToolChoice(JsonNode value)
    {
        if (value is JsonValue jsonValue && StringValue(jsonValue) is { } choice)
        {
            return choice;
        }

        if (value is JsonObject named && StringValue(named["type"]) == "function" && named["function"] is JsonObject function)
        {
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = StringValue(function["name"]) ?? string.Empty },
            };
        }

        return value.DeepClone();
    }

    private static JsonNode ResolveToolSchema(Tool tool, out bool strict)
    {
        if (tool.ConstrainedSampling is not JsonSchemaSampling config)
        {
            strict = false;
            return tool.Parameters.DeepClone();
        }

        try
        {
            var clone = tool.Parameters.DeepClone() as JsonObject ?? throw new UnsupportedStrictSchemaException("root schema must have type object");
            MakeStrictSchemaNode(clone);
            if (StringValue(clone["type"]) != "object") throw new UnsupportedStrictSchemaException("root schema must have type object");
            strict = true;
            return clone;
        }
        catch (UnsupportedStrictSchemaException error)
        {
            if (config.Strict == "require")
            {
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {error.Message}.", error);
            }

            strict = false;
            return tool.Parameters.DeepClone();
        }
    }

    private static void MakeStrictSchemaNode(JsonObject schema)
    {
        foreach (var key in new[] { "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties", "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not", "if", "then", "else" })
        {
            if (schema.ContainsKey(key)) throw new UnsupportedStrictSchemaException($"{key} schemas are unsupported");
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            if (anyOf.Count == 0) throw new UnsupportedStrictSchemaException("anyOf must contain at least one schema");
            foreach (var variant in anyOf)
            {
                if (variant is not JsonObject variantObject) throw new UnsupportedStrictSchemaException("boolean schemas are unsupported");
                if (IsStructuredSchema(variantObject)) throw new UnsupportedStrictSchemaException("object and array unions are unsupported");
                MakeStrictSchemaNode(variantObject);
            }
        }
        else if (schema.ContainsKey("anyOf")) throw new UnsupportedStrictSchemaException("anyOf must contain at least one schema");
        if (schema["items"] is JsonArray) throw new UnsupportedStrictSchemaException("tuple schemas are unsupported");
        if (schema["items"] is JsonObject items) MakeStrictSchemaNode(items);

        var isObject = StringValue(schema["type"]) == "object";
        if (schema.ContainsKey("properties") && !isObject) throw new UnsupportedStrictSchemaException("properties require type object");
        if (!isObject) return;
        if (schema["additionalProperties"] is { } additional && BoolValue(additional) != false)
        {
            throw new UnsupportedStrictSchemaException("schema-valued or true additionalProperties is unsupported");
        }

        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject)
        {
            throw new UnsupportedStrictSchemaException("object properties must be a schema map");
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray requiredArray)
        {
            foreach (var item in requiredArray)
            {
                if (StringValue(item) is not { } name) throw new UnsupportedStrictSchemaException("object required must be a string array");
                required.Add(name);
            }
        }
        else if (schema.ContainsKey("required")) throw new UnsupportedStrictSchemaException("object required must be a string array");
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        if (required.Any(name => !properties.ContainsKey(name))) throw new UnsupportedStrictSchemaException("required contains an unknown property");
        foreach (var (name, child) in properties.ToArray())
        {
            if (child is not JsonObject childObject) throw new UnsupportedStrictSchemaException("boolean schemas are unsupported");
            MakeStrictSchemaNode(childObject);
            if (!required.Contains(name) && !AllowsNull(childObject))
            {
                properties[name] = new JsonObject
                {
                    ["anyOf"] = new JsonArray
                    {
                        childObject.DeepClone(),
                        (JsonNode?)new JsonObject { ["type"] = "null" },
                    },
                };
            }
        }

        var requiredNames = new JsonArray();
        foreach (var name in properties.Select(pair => pair.Key)) requiredNames.Add((JsonNode?)name);
        schema["required"] = requiredNames;
        schema["additionalProperties"] = false;
    }

    private static bool IsStructuredSchema(JsonObject schema) =>
        StringValue(schema["type"]) is "object" or "array" || schema.ContainsKey("properties") || schema.ContainsKey("items");

    private static bool AllowsNull(JsonObject schema) =>
        StringValue(schema["type"]) == "null" ||
        schema["type"] is JsonArray types && types.Any(item => StringValue(item) == "null") ||
        schema.ContainsKey("const") && schema["const"] is null ||
        schema["enum"] is JsonArray values && values.Any(item => item is null) ||
        schema["anyOf"] is JsonArray anyOf && anyOf.OfType<JsonObject>().Any(AllowsNull);

    private static MistralOptions CopyCommonOptions(Model model, SimpleStreamOptions options, MistralOptions specifics) => new()
    {
        Signal = options.Signal,
        TelemetryContext = options.TelemetryContext,
        ApiKey = options.ApiKey,
        Fetch = options.Fetch,
        Environment = options.Environment,
        OnPayload = options.OnPayload,
        OnResponse = options.OnResponse,
        Headers = options.Headers,
        TimeoutMs = options.TimeoutMs,
        MaxRetries = options.MaxRetries,
        MaxRetryDelayMs = options.MaxRetryDelayMs,
        Temperature = options.Temperature,
        SamplingParameters = options.SamplingParameters,
        MaxTokens = options.MaxTokens ?? model.MaxTokens,
        Transport = options.Transport,
        CacheRetention = options.CacheRetention,
        SessionId = options.SessionId,
        WebSocketConnectTimeoutMs = options.WebSocketConnectTimeoutMs,
        Metadata = options.Metadata,
        ToolChoice = specifics.ToolChoice,
        PromptMode = specifics.PromptMode,
        ReasoningEffort = specifics.ReasoningEffort,
    };

    private static ProviderRequestOptions TransportOptions(StreamOptions options) => new()
    {
        Signal = options.Signal,
        TelemetryContext = options.TelemetryContext,
        ApiKey = options.ApiKey,
        Fetch = options.Fetch,
        Environment = options.Environment,
        Headers = options.Headers,
        TimeoutMs = options.TimeoutMs ?? 60_000,
        MaxRetries = options.MaxRetries,
        MaxRetryDelayMs = options.MaxRetryDelayMs,
    };

    private static Dictionary<string, string?> BuildDefaultHeaders(Model model, StreamOptions options)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PiUserAgent.GetPiUserAgent(),
            ["Accept"] = "text/event-stream",
        };
        var explicitAffinity = (model.Headers?.Keys ?? []).Any(key => string.Equals(key, "x-affinity", StringComparison.OrdinalIgnoreCase)) ||
                               (options.Headers?.Keys ?? []).Any(key => string.Equals(key, "x-affinity", StringComparison.OrdinalIgnoreCase));
        if (ShouldUsePromptCaching(options) && !explicitAffinity)
        {
            headers["x-affinity"] = options.SessionId;
        }

        return headers;
    }

    private static Dictionary<string, string> ReadResponseHeaders(HttpResponseMessage response)
    {
        var headers = HeaderUtilities.HeadersToRecord(response.Headers);
        if (response.Content is not null)
        {
            foreach (var pair in response.Content.Headers)
            {
                headers[pair.Key] = string.Join(", ", pair.Value);
            }
        }

        return headers;
    }

    private static CancellationTokenSource CreateStreamCancellation(StreamOptions options)
    {
        var source = options.Signal.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(options.Signal)
            : new CancellationTokenSource();
        source.CancelAfter(options.TimeoutMs ?? 60_000);
        return source;
    }

    private static Uri ResolveEndpoint(string baseUrl) =>
        new(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), "v1/chat/completions");

    private static AssistantMessage CreateOutput(Model model) => new()
    {
        Content = [],
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Usage = new Usage(),
        StopReason = StopReasons.Pending,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static bool ShouldUsePromptCaching(StreamOptions? options) =>
        options?.CacheRetention != CacheRetentions.None && !string.IsNullOrEmpty(options?.SessionId);

    private static bool UsesReasoningEffort(Model model) =>
        model.Id is "mistral-small-2603" or "mistral-small-latest" or "mistral-medium-3.5";

    private static bool UsesPromptModeReasoning(Model model) => model.Reasoning && !UsesReasoningEffort(model);

    private static string MapReasoningEffort(Model model, string level) =>
        model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(level, out var mapped)
            ? mapped ?? "high"
            : "high";

    private static JsonObject CloneObject(JsonObject source) =>
        source.DeepClone() as JsonObject ?? new JsonObject();

    private static void Remap(JsonObject record, string source, string target)
    {
        if (!record.ContainsKey(source)) return;
        var value = record[source]?.DeepClone();
        record[target] = value;
        record.Remove(source);
    }

    private static string FormatMistralError(Exception error)
    {
        if (error is MistralHttpException http)
        {
            var body = http.Body?.Trim();
            if (!string.IsNullOrEmpty(body)) return $"Mistral API error ({http.StatusCode}): {Truncate(body)}";
            return $"Mistral API error ({http.StatusCode}): {http.Message}";
        }

        var normalized = ErrorBodyUtilities.NormalizeProviderError(error);
        if (normalized.Status is { } status)
        {
            var reason = !string.IsNullOrEmpty(normalized.Body) ? normalized.Body : normalized.Message;
            return $"Mistral API error ({status}): {Truncate(reason)}";
        }

        return normalized.Message;
    }

    private static string Truncate(string text) => text.Length <= _maxErrorBodyChars
        ? text
        : $"{text[.._maxErrorBodyChars]}... [truncated {text.Length - _maxErrorBodyChars} chars]";

    private static string? StringValue(JsonNode? value)
    {
        try { return value?.GetValue<string>(); }
        catch { return null; }
    }

    private static bool? BoolValue(JsonNode? value)
    {
        try { return value?.GetValue<bool>(); }
        catch { return null; }
    }

    private static int? IntValue(JsonNode? value)
    {
        try { return value?.GetValue<int>(); }
        catch
        {
            try { return value?.GetValue<long>() is { } number ? checked((int)number) : null; }
            catch { return null; }
        }
    }

    private sealed class StreamState(AssistantMessage output)
    {
        public AssistantMessage Output { get; set; } = output;

        public int? CurrentIndex { get; set; }

        public bool HasFinishReason { get; set; }

        public Dictionary<string, ActiveToolCall> ToolCalls { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ActiveToolCall
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required int ContentIndex { get; init; }

        public string PartialArguments { get; set; } = string.Empty;
    }

    private sealed class MistralHttpException(int? statusCode, string? body, Exception innerException) : Exception(
        statusCode is { } status ? $"Request failed with status {status}" : innerException.Message,
        innerException)
    {
        public int? StatusCode { get; } = statusCode;

        public string? Body { get; } = body;
    }

    private sealed class UnsupportedStrictSchemaException(string message) : Exception(message);
}
