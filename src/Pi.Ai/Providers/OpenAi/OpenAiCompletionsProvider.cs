using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>OpenAI-compatible Chat Completions provider adapter.</summary>
public sealed class OpenAiCompletionsProvider : ProviderStreams
{
    private readonly ProviderHttpClient _transport;

    /// <summary>Creates an adapter backed by the supplied HTTP transport.</summary>
    public OpenAiCompletionsProvider(ProviderHttpClient? transport = null)
    {
        _transport = transport ?? new ProviderHttpClient();
    }

    /// <summary>Builds a provider-compatible streamed Chat Completions payload.</summary>
    public static JsonObject BuildPayload(
        Model model,
        Context context,
        StreamOptions? options = null,
        string? toolChoice = null,
        string? reasoning = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var compatibility = CompatibilityFor(model);
        var simpleOptions = options as SimpleStreamOptions;
        toolChoice ??= simpleOptions?.ToolChoice;
        reasoning ??= simpleOptions?.Reasoning;

        var messages = ConvertMessages(model, context);
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = true,
        };

        if (compatibility.SupportsUsageInStreaming)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (compatibility.SupportsStore)
        {
            payload["store"] = false;
        }

        if (options?.MaxTokens is > 0)
        {
            payload[compatibility.MaxTokensField] = options.MaxTokens.Value;
        }

        if (options?.Temperature is not null)
        {
            payload["temperature"] = options.Temperature.Value;
        }

        var deferredNames = context.Messages
            .OfType<ToolResultMessage>()
            .SelectMany(static message => message.AddedToolNames ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var activeTools = context.Tools.Where(tool => !deferredNames.Contains(tool.Name)).ToArray();
        if (activeTools.Length > 0 || HasToolHistory(context.Messages))
        {
            var tools = new JsonArray();
            foreach (var tool in activeTools)
            {
                tools.Add((JsonNode?)ConvertTool(tool, compatibility.SupportsStrictMode));
            }

            payload["tools"] = tools;
        }

        if (!string.IsNullOrEmpty(toolChoice))
        {
            payload["tool_choice"] = toolChoice;
        }

        if (!string.IsNullOrEmpty(reasoning) && model.Reasoning && compatibility.SupportsReasoningEffort)
        {
            payload["reasoning_effort"] = ResolveThinkingLevel(model, reasoning);
        }

        if (options?.SessionId is not null &&
            !string.Equals(options.CacheRetention, CacheRetentions.None, StringComparison.Ordinal) &&
            model.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["prompt_cache_key"] = options.SessionId;
            if (string.Equals(options.CacheRetention, CacheRetentions.Long, StringComparison.Ordinal) &&
                compatibility.SupportsLongCacheRetention)
            {
                payload["prompt_cache_retention"] = "24h";
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

    /// <summary>Converts Pi context messages to Chat Completions message objects.</summary>
    public static JsonArray ConvertMessages(Model model, Context context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var compatibility = CompatibilityFor(model);
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add((JsonNode?)new JsonObject
            {
                ["role"] = model.Reasoning && compatibility.SupportsDeveloperRole ? "developer" : "system",
                ["content"] = UnicodeUtilities.SanitizeSurrogates(context.SystemPrompt),
            });
        }

        string? previousRole = null;
        foreach (var message in context.Messages)
        {
            if (message is UserMessage user)
            {
                if (compatibility.RequiresAssistantAfterToolResult && previousRole == "tool")
                {
                    messages.Add((JsonNode?)new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "I have processed the tool results.",
                    });
                }

                var userMessage = ConvertUserMessage(model, user);
                if (userMessage is not null)
                {
                    messages.Add((JsonNode?)userMessage);
                    previousRole = "user";
                }

                continue;
            }

            if (message is AssistantMessage assistant)
            {
                var assistantMessage = ConvertAssistantMessage(model, assistant, compatibility);
                if (assistantMessage is not null)
                {
                    messages.Add((JsonNode?)assistantMessage);
                    previousRole = "assistant";
                }

                continue;
            }

            if (message is ToolResultMessage toolResult)
            {
                var toolText = MessageUtilities.ContentText(toolResult.Content);
                var hasImage = toolResult.Content.OfType<ImageContent>().Any();
                var toolMessage = new JsonObject
                {
                    ["role"] = "tool",
                    ["content"] = UnicodeUtilities.SanitizeSurrogates(
                        string.IsNullOrEmpty(toolText)
                            ? hasImage ? "(see attached image)" : "(no tool output)"
                            : toolText),
                    ["tool_call_id"] = NormalizeToolCallId(model, toolResult.ToolCallId),
                };
                if (compatibility.RequiresToolResultName && !string.IsNullOrEmpty(toolResult.ToolName))
                {
                    toolMessage["name"] = toolResult.ToolName;
                }

                messages.Add((JsonNode?)toolMessage);
                previousRole = "tool";
                if (hasImage && model.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
                {
                    var imageParts = new JsonArray
                    {
                        (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" },
                    };
                    foreach (var image in toolResult.Content.OfType<ImageContent>())
                    {
                        imageParts.Add((JsonNode?)new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                            },
                        });
                    }

                    messages.Add((JsonNode?)new JsonObject { ["role"] = "user", ["content"] = imageParts });
                    previousRole = "user";
                }
            }
        }

        return messages;
    }

    /// <inheritdoc />
    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var stream = new AssistantMessageEventStream();
        _ = RunAsync(stream, model, context, options, null, null);
        return stream;
    }

    /// <inheritdoc />
    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var stream = new AssistantMessageEventStream();
        _ = RunAsync(stream, model, context, options, options?.ToolChoice, options?.Reasoning);
        return stream;
    }

    private async Task RunAsync(
        AssistantMessageEventStream stream,
        Model model,
        Context context,
        StreamOptions? options,
        string? toolChoice,
        string? reasoning)
    {
        var output = CreatePendingMessage(model);
        try
        {
            EnsureApiKey(model, options);
            var payload = BuildPayload(model, context, options, toolChoice, reasoning);
            var endpoint = new Uri(new Uri(model.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "chat/completions");
            using var response = await SendWithRetryAsync(model, endpoint, payload, options).ConfigureAwait(false);
            if (response.Content is null)
            {
                throw new InvalidOperationException("Provider response has no body");
            }

            await using var body = await response.Content.ReadAsStreamAsync(options?.Signal ?? default).ConfigureAwait(false);
            stream.Push(new StreamStartEvent(output));
            var state = new StreamState(output);
            var sawDone = false;
            await foreach (var sse in SseReader.ReadAsync(body, options?.Signal ?? default))
            {
                if (sse.Data == "[DONE]")
                {
                    sawDone = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(sse.Data))
                {
                    continue;
                }

                var node = JsonParseUtilities.ParseJsonWithRepair(sse.Data) as JsonObject;
                if (node is null)
                {
                    continue;
                }

                HandleChunk(state, stream, model, node);
            }

            FinishOpenBlocks(state, stream);
            output = state.Output;
            if (options?.Signal.IsCancellationRequested == true)
            {
                throw new OperationCanceledException(options.Signal);
            }

            if (!state.HasFinishReason)
            {
                if (!CompatibilityFor(model).SupportsFinishReason)
                {
                    output = output with
                    {
                        StopReason = output.Content.OfType<ToolCall>().Any()
                            ? StopReasons.ToolUse
                            : StopReasons.Stop,
                    };
                }
                else
                {
                    throw new InvalidOperationException(sawDone
                        ? "Stream ended without finish_reason"
                        : "Stream ended without finish_reason");
                }
            }

            if (output.StopReason == StopReasons.Error)
            {
                throw new InvalidOperationException(output.ErrorMessage ?? "Provider returned an error stop reason");
            }

            stream.Push(new StreamDoneEvent(output.StopReason, output));
            stream.End(output);
        }
        catch (Exception error)
        {
            var stopReason = options?.Signal.IsCancellationRequested == true
                ? StopReasons.Aborted
                : StopReasons.Error;
            output = output with
            {
                StopReason = stopReason,
                ErrorMessage = ErrorBodyUtilities.FormatProviderError(ErrorBodyUtilities.NormalizeProviderError(error)),
            };
            stream.Push(new StreamErrorEvent(stopReason, output));
            stream.End(output);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Model model,
        Uri endpoint,
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
                        endpoint,
                        payload,
                        options,
                        new Dictionary<string, string?> { ["Accept"] = "text/event-stream" },
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

    private static void HandleChunk(
        StreamState state,
        AssistantMessageEventStream stream,
        Model model,
        JsonObject chunk)
    {
        var id = StringValue(chunk["id"]);
        if (!string.IsNullOrEmpty(id) && state.Output.ResponseId is null)
        {
            state.Output = state.Output with { ResponseId = id };
        }

        var responseModel = StringValue(chunk["model"]);
        if (!string.IsNullOrEmpty(responseModel) && !string.Equals(responseModel, model.Id, StringComparison.Ordinal))
        {
            state.Output = state.Output with { ResponseModel = responseModel };
        }

        if (chunk["usage"] is JsonObject usage)
        {
            state.Output = state.Output with { Usage = ParseUsage(model, usage) };
        }

        var choices = chunk["choices"] as JsonArray;
        if (choices is null || choices.Count == 0 || choices[0] is not JsonObject choice)
        {
            return;
        }

        var finishReason = StringValue(choice["finish_reason"]);
        if (finishReason is not null)
        {
            state.HasFinishReason = true;
            var mapped = MapStopReason(finishReason);
            state.Output = state.Output with
            {
                StopReason = mapped.StopReason,
                ErrorMessage = mapped.ErrorMessage,
                RawStopReason = finishReason,
            };
        }

        if (choice["delta"] is not JsonObject delta)
        {
            return;
        }

        var content = StringValue(delta["content"]);
        if (!string.IsNullOrEmpty(content))
        {
            AppendText(state, stream, content);
        }

        foreach (var field in new[] { "reasoning_content", "reasoning", "reasoning_text" })
        {
            var reasoning = StringValue(delta[field]);
            if (!string.IsNullOrEmpty(reasoning))
            {
                AppendThinking(state, stream, reasoning, field);
                break;
            }
        }

        if (delta["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var toolCall in toolCalls.OfType<JsonObject>())
            {
                HandleToolCallDelta(state, stream, toolCall);
            }
        }
    }

    private static void AppendText(StreamState state, AssistantMessageEventStream stream, string delta)
    {
        CloseThinking(state, stream);
        if (state.TextIndex is null)
        {
            var content = state.Output.Content.ToList();
            content.Add(new TextContent(string.Empty));
            state.TextIndex = content.Count - 1;
            state.Output = state.Output with { Content = content };
            stream.Push(new TextStartEvent(state.TextIndex.Value, state.Output));
        }

        var index = state.TextIndex.Value;
        var current = (TextContent)state.Output.Content[index];
        var next = current with { Text = current.Text + delta };
        var updated = state.Output.Content.ToList();
        updated[index] = next;
        state.Output = state.Output with { Content = updated };
        stream.Push(new TextDeltaEvent(index, delta, state.Output));
    }

    private static void AppendThinking(
        StreamState state,
        AssistantMessageEventStream stream,
        string delta,
        string signature)
    {
        CloseText(state, stream);
        if (state.ThinkingIndex is null)
        {
            var content = state.Output.Content.ToList();
            content.Add(new ThinkingContent(string.Empty, signature));
            state.ThinkingIndex = content.Count - 1;
            state.Output = state.Output with { Content = content };
            stream.Push(new ThinkingStartEvent(state.ThinkingIndex.Value, state.Output));
        }

        var index = state.ThinkingIndex.Value;
        var current = (ThinkingContent)state.Output.Content[index];
        var next = current with { Thinking = current.Thinking + delta };
        var updated = state.Output.Content.ToList();
        updated[index] = next;
        state.Output = state.Output with { Content = updated };
        stream.Push(new ThinkingDeltaEvent(index, delta, state.Output));
    }

    private static void HandleToolCallDelta(
        StreamState state,
        AssistantMessageEventStream stream,
        JsonObject delta)
    {
        CloseText(state, stream);
        CloseThinking(state, stream);
        var index = IntValue(delta["index"]) ?? 0;
        if (!state.ToolCalls.TryGetValue(index, out var active))
        {
            var function = delta["function"] as JsonObject;
            active = new ActiveToolCall
            {
                StreamIndex = index,
                ContentIndex = state.Output.Content.Count,
                Id = StringValue(delta["id"]) ?? string.Empty,
                Name = StringValue(function?["name"]) ?? string.Empty,
            };
            var content = state.Output.Content.ToList();
            content.Add(new ToolCall(active.Id, active.Name, new JsonObject()));
            state.Output = state.Output with { Content = content };
            state.ToolCalls[index] = active;
            stream.Push(new ToolCallStartEvent(active.ContentIndex, state.Output));
        }

        var functionDelta = delta["function"] as JsonObject;
        var id = StringValue(delta["id"]);
        var name = StringValue(functionDelta?["name"]);
        var arguments = StringValue(functionDelta?["arguments"]);
        if (id is not null)
        {
            active.Id = id;
        }

        if (name is not null)
        {
            active.Name = name;
        }

        if (arguments is not null)
        {
            active.PartialArguments += arguments;
        }

        var parsed = JsonParseUtilities.ParseStreamingJson(active.PartialArguments) as JsonObject ?? new JsonObject();
        var updated = state.Output.Content.ToList();
        updated[active.ContentIndex] = new ToolCall(active.Id, active.Name, parsed);
        state.Output = state.Output with { Content = updated };
        stream.Push(new ToolCallDeltaEvent(active.ContentIndex, arguments ?? string.Empty, state.Output));
    }

    private static void FinishOpenBlocks(StreamState state, AssistantMessageEventStream stream)
    {
        CloseText(state, stream);
        CloseThinking(state, stream);
        foreach (var active in state.ToolCalls.Values.OrderBy(static value => value.ContentIndex))
        {
            var current = (ToolCall)state.Output.Content[active.ContentIndex];
            var arguments = JsonParseUtilities.ParseStreamingJson(active.PartialArguments) as JsonObject ?? current.Arguments;
            var finalized = current with
            {
                Id = active.Id,
                Name = active.Name,
                Arguments = arguments,
            };
            var content = state.Output.Content.ToList();
            content[active.ContentIndex] = finalized;
            state.Output = state.Output with { Content = content };
            stream.Push(new ToolCallEndEvent(active.ContentIndex, finalized, state.Output));
        }
    }

    private static void CloseText(StreamState state, AssistantMessageEventStream stream)
    {
        if (state.TextIndex is null)
        {
            return;
        }

        var index = state.TextIndex.Value;
        stream.Push(new TextEndEvent(index, ((TextContent)state.Output.Content[index]).Text, state.Output));
        state.TextIndex = null;
    }

    private static void CloseThinking(StreamState state, AssistantMessageEventStream stream)
    {
        if (state.ThinkingIndex is null)
        {
            return;
        }

        var index = state.ThinkingIndex.Value;
        stream.Push(new ThinkingEndEvent(index, ((ThinkingContent)state.Output.Content[index]).Thinking, state.Output));
        state.ThinkingIndex = null;
    }

    private static Usage ParseUsage(Model model, JsonObject usage)
    {
        var promptTokens = IntValue(usage["prompt_tokens"]) ?? 0;
        var outputTokens = IntValue(usage["completion_tokens"]) ?? 0;
        var details = usage["prompt_tokens_details"] as JsonObject;
        var cacheRead = IntValue(details?["cached_tokens"]) ?? IntValue(usage["prompt_cache_hit_tokens"]) ?? IntValue(usage["cached_tokens"]) ?? 0;
        var cacheWrite = IntValue(details?["cache_write_tokens"]) ?? 0;
        var input = Math.Max(0, promptTokens - cacheRead - cacheWrite);
        var result = new Usage
        {
            Input = input,
            Output = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            Reasoning = IntValue((usage["completion_tokens_details"] as JsonObject)?["reasoning_tokens"]) ?? 0,
            TotalTokens = input + outputTokens + cacheRead + cacheWrite,
        };
        ModelUtilities.CalculateCost(model, result);
        return result;
    }

    private static (string StopReason, string? ErrorMessage) MapStopReason(string reason) => reason switch
    {
        "stop" or "end" => (StopReasons.Stop, null),
        "length" => (StopReasons.Length, null),
        "function_call" or "tool_calls" => (StopReasons.ToolUse, null),
        "content_filter" => (StopReasons.Error, "Provider finish_reason: content_filter"),
        "network_error" => (StopReasons.Error, "Provider finish_reason: network_error"),
        _ => (StopReasons.Error, $"Provider finish_reason: {reason}"),
    };

    private static AssistantMessage CreatePendingMessage(Model model) => new()
    {
        Content = [],
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        StopReason = StopReasons.Pending,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static void EnsureApiKey(Model model, StreamOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.ApiKey))
        {
            return;
        }

        if (HasUsableHeader(model.Headers, "authorization") || HasUsableHeader(model.Headers, "x-api-key") ||
            HasUsableHeader(options?.Headers, "authorization") || HasUsableHeader(options?.Headers, "x-api-key"))
        {
            return;
        }

        throw new InvalidOperationException($"No API key for provider: {model.Provider}");
    }

    private static bool HasUsableHeader<T>(IReadOnlyDictionary<string, T>? headers, string name)
    {
        if (headers is null)
        {
            return false;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) &&
                pair.Value is not null &&
                (!pair.Value.Equals(string.Empty) || pair.Value is not string))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject? ConvertUserMessage(Model model, UserMessage message)
    {
        if (message.Content is string text)
        {
            return new JsonObject
            {
                ["role"] = "user",
                ["content"] = UnicodeUtilities.SanitizeSurrogates(text),
            };
        }

        if (message.Content is not IEnumerable<ContentBlock> blocks)
        {
            return null;
        }

        var parts = new JsonArray();
        foreach (var block in blocks)
        {
            if (block is TextContent textBlock)
            {
                parts.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = UnicodeUtilities.SanitizeSurrogates(textBlock.Text),
                });
            }
            else if (block is ImageContent image && model.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                    },
                });
            }
        }

        return parts.Count == 0
            ? null
            : new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    private static JsonObject? ConvertAssistantMessage(
        Model model,
        AssistantMessage message,
        Compatibility compatibility)
    {
        var text = string.Concat(message.Content.OfType<TextContent>().Select(static block => block.Text));
        var thinking = string.Join("\n\n", message.Content.OfType<ThinkingContent>().Select(static block => block.Thinking).Where(static value => value.Length > 0));
        var toolCalls = message.Content.OfType<ToolCall>().ToArray();
        if (text.Length == 0 && thinking.Length == 0 && toolCalls.Length == 0)
        {
            return null;
        }

        var result = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? UnicodeUtilities.SanitizeSurrogates(text) : null,
        };
        if (thinking.Length > 0 && (compatibility.RequiresReasoningContentOnAssistantMessages || model.Reasoning))
        {
            result["reasoning_content"] = UnicodeUtilities.SanitizeSurrogates(thinking);
        }

        if (toolCalls.Length > 0)
        {
            var calls = new JsonArray();
            foreach (var toolCall in toolCalls)
            {
                calls.Add((JsonNode?)new JsonObject
                {
                    ["id"] = NormalizeToolCallId(model, toolCall.Id),
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = toolCall.Name,
                        ["arguments"] = toolCall.Arguments.ToJsonString(),
                    },
                });
            }

            result["tool_calls"] = calls;
        }

        return result;
    }

    private static JsonObject ConvertTool(Tool tool, bool supportsStrictMode)
    {
        var function = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = tool.Parameters.DeepClone(),
        };
        if (supportsStrictMode)
        {
            function["strict"] = false;
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = function,
        };
    }

    private static string NormalizeToolCallId(Model model, string id)
    {
        if (!id.Contains('|', StringComparison.Ordinal))
        {
            return model.Provider == "openai" && id.Length > 40 ? id[..40] : id;
        }

        var separator = id.IndexOf('|');
        var callId = SanitizeToolId(id[..separator]);
        var itemId = SanitizeToolId(id[(separator + 1)..]);
        var combined = itemId.Length > 0 ? $"{callId}_{itemId}" : callId;
        if (combined.Length <= 40)
        {
            return combined;
        }

        var hash = HashUtilities.ShortHash(id)[..8];
        var prefix = callId[..Math.Max(1, Math.Min(callId.Length, 40 - hash.Length - 1))];
        return $"{prefix}_{hash}";
    }

    private static string SanitizeToolId(string value)
    {
        var chars = value.Select(static character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray();
        return new string(chars);
    }

    private static bool HasToolHistory(IReadOnlyList<Message> messages) =>
        messages.Any(message => message is ToolResultMessage ||
            message is AssistantMessage assistant && assistant.Content.OfType<ToolCall>().Any());

    private static string ResolveThinkingLevel(Model model, string reasoning) =>
        model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(reasoning, out var mapped) && mapped is not null
            ? mapped
            : reasoning;

    private static void ApplyJsonProperties(
        JsonObject destination,
        IReadOnlyDictionary<string, JsonNode?> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static Compatibility CompatibilityFor(Model model)
    {
        var provider = model.Provider;
        var baseUrl = model.BaseUrl;
        var nonStandard = provider is "nvidia" or "cerebras" or "xai" or "together" or "deepseek" or "zai" or "zai-coding-cn" or "moonshotai" or "moonshotai-cn" ||
            baseUrl.Contains("chutes.ai", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("api.together.", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("api.z.ai", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("api.moonshot.", StringComparison.OrdinalIgnoreCase);
        var openRouter = provider == "openrouter" || baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
        var useMaxTokens = nonStandard;
        var supportsDeveloper = openRouter && (model.Id.StartsWith("anthropic/", StringComparison.Ordinal) || model.Id.StartsWith("openai/", StringComparison.Ordinal)) ||
                                !nonStandard && !openRouter;
        return new Compatibility(
            GetBool(model.Compatibility, "supportsStore", !nonStandard),
            GetBool(model.Compatibility, "supportsDeveloperRole", supportsDeveloper),
            GetBool(model.Compatibility, "supportsReasoningEffort", !nonStandard && provider is not "xai"),
            GetBool(model.Compatibility, "supportsUsageInStreaming", true),
            GetBool(model.Compatibility, "supportsFinishReason", true),
            GetString(model.Compatibility, "maxTokensField") ?? (useMaxTokens ? "max_tokens" : "max_completion_tokens"),
            GetBool(model.Compatibility, "requiresToolResultName", false),
            GetBool(model.Compatibility, "requiresAssistantAfterToolResult", false),
            GetBool(model.Compatibility, "requiresReasoningContentOnAssistantMessages", provider == "deepseek"),
            GetBool(model.Compatibility, "supportsStrictMode", true),
            !provider.Equals("together", StringComparison.Ordinal) && !provider.Equals("nvidia", StringComparison.Ordinal));
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

    private static int? IntValue(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>();
        }
        catch
        {
            try
            {
                return node?.GetValue<long>() is { } longValue ? checked((int)longValue) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed class StreamState(AssistantMessage output)
    {
        public AssistantMessage Output { get; set; } = output;

        public int? TextIndex { get; set; }

        public int? ThinkingIndex { get; set; }

        public Dictionary<int, ActiveToolCall> ToolCalls { get; } = [];

        public bool HasFinishReason { get; set; }
    }

    private sealed class ActiveToolCall
    {
        public int StreamIndex { get; init; }

        public int ContentIndex { get; init; }

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string PartialArguments { get; set; } = string.Empty;
    }

    private readonly record struct Compatibility(
        bool SupportsStore,
        bool SupportsDeveloperRole,
        bool SupportsReasoningEffort,
        bool SupportsUsageInStreaming,
        bool SupportsFinishReason,
        string MaxTokensField,
        bool RequiresToolResultName,
        bool RequiresAssistantAfterToolResult,
        bool RequiresReasoningContentOnAssistantMessages,
        bool SupportsStrictMode,
        bool SupportsLongCacheRetention);
}
