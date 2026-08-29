using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Anthropic Messages provider-specific stream controls.</summary>
public sealed class AnthropicStreamOptions : StreamOptions
{
    /// <summary>Whether extended thinking is enabled.</summary>
    public bool? ThinkingEnabled { get; init; }

    /// <summary>Budget for budget-based extended thinking.</summary>
    public int? ThinkingBudgetTokens { get; init; }

    /// <summary>Adaptive-thinking effort level.</summary>
    public string? Effort { get; init; }

    /// <summary>Adaptive-thinking display mode.</summary>
    public string? ThinkingDisplay { get; init; }

    /// <summary>Whether to request the interleaved-thinking beta feature.</summary>
    public bool InterleavedThinking { get; init; } = true;

    /// <summary>Anthropic tool-choice value.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Anthropic forced tool name when tool choice is a named tool.</summary>
    public string? ToolChoiceName { get; init; }
}

/// <summary>Raw HTTP/SSE implementation of Pi's Anthropic Messages API.</summary>
public sealed class AnthropicMessagesProvider : ProviderStreams
{
    private const string _anthropicVersion = "2023-06-01";
    private const string _fineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private const string _interleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private const string _serverSideFallbackBeta = "server-side-fallback-2026-07-01";
    private const string _claudeCodeVersion = "2.1.75";
    private const string _claudeCodeIdentity = "You are Claude Code, Anthropic's official CLI for Claude.";

    private static readonly string[] _claudeCodeTools =
    [
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion", "EnterPlanMode", "ExitPlanMode",
        "KillShell", "NotebookEdit", "Skill", "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch",
    ];

    private readonly ProviderHttpClient _transport;

    /// <summary>Creates an adapter backed by the supplied HTTP transport.</summary>
    public AnthropicMessagesProvider(ProviderHttpClient? transport = null)
    {
        _transport = transport ?? new ProviderHttpClient();
    }

    /// <summary>Builds an Anthropic Messages streaming request payload.</summary>
    public static JsonObject BuildPayload(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);

        var anthropicOptions = options as AnthropicStreamOptions;
        var isOAuth = IsOAuthToken(options?.ApiKey);
        var compatibility = GetCompatibility(model);
        var cacheControl = ResolveCacheControl(model, options);
        var messages = ConvertMessages(model, context, isOAuth, compatibility.AllowEmptySignature);
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["max_tokens"] = options?.MaxTokens ?? model.MaxTokens,
            ["stream"] = true,
        };

        if (isOAuth)
        {
            var system = new JsonArray
            {
                (JsonNode?)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = _claudeCodeIdentity,
                },
            };
            AppendSystemPrompt(system, context.SystemPrompt, cacheControl);
            payload["system"] = system;
        }
        else if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var system = new JsonArray
            {
                (JsonNode?)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = UnicodeUtilities.SanitizeSurrogates(context.SystemPrompt),
                },
            };
            ApplyCacheControl(system[0] as JsonObject, cacheControl);
            payload["system"] = system;
        }

        var thinkingEnabled = anthropicOptions?.ThinkingEnabled;
        if (model.Reasoning && thinkingEnabled == true)
        {
            var display = anthropicOptions?.ThinkingDisplay ?? "summarized";
            if (compatibility.ForceAdaptiveThinking)
            {
                payload["thinking"] = new JsonObject
                {
                    ["type"] = "adaptive",
                    ["display"] = display,
                };
                if (!string.IsNullOrEmpty(anthropicOptions?.Effort))
                {
                    payload["output_config"] = new JsonObject { ["effort"] = anthropicOptions.Effort };
                }
            }
            else
            {
                payload["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = Math.Max(0, anthropicOptions?.ThinkingBudgetTokens ?? 1024),
                    ["display"] = display,
                };
            }
        }
        else if (model.Reasoning && thinkingEnabled == false && !HasDisabledThinkingMapping(model))
        {
            payload["thinking"] = new JsonObject { ["type"] = "disabled" };
        }

        if (options?.Temperature is not null && thinkingEnabled != true && compatibility.SupportsTemperature)
        {
            payload["temperature"] = options.Temperature.Value;
        }

        var tools = ConvertTools(context.Tools, isOAuth, compatibility);
        if (tools.Count > 0)
        {
            ApplyCacheControl(tools[^1] as JsonObject, cacheControl, topLevel: true);
            payload["tools"] = tools;
        }

        if (!string.IsNullOrEmpty(anthropicOptions?.ToolChoice))
        {
            payload["tool_choice"] = BuildToolChoice(anthropicOptions.ToolChoice, anthropicOptions.ToolChoiceName);
        }

        if (options?.Metadata is not null && options.Metadata.TryGetValue("user_id", out var userId) &&
            userId is not null)
        {
            payload["metadata"] = new JsonObject { ["user_id"] = userId.DeepClone() };
        }

        var fallbacks = GetAllowedFallbackModels(model);
        if (fallbacks.Count > 0)
        {
            var fallbackArray = new JsonArray();
            foreach (var fallback in fallbacks)
            {
                fallbackArray.Add((JsonNode?)new JsonObject { ["model"] = fallback });
            }

            payload["fallbacks"] = fallbackArray;
        }

        ApplyLastUserCacheControl(messages, cacheControl);
        return payload;
    }

    /// <summary>Converts Pi messages to Anthropic Messages input items.</summary>
    public static JsonArray ConvertMessages(Model model, Context context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var compatibility = GetCompatibility(model);
        return ConvertMessages(model, context, IsOAuthToken(null), compatibility.AllowEmptySignature);
    }

    private static JsonArray ConvertMessages(Model model, Context context, bool isOAuth, bool allowEmptySignature)
    {
        var transformed = TransformMessages(model, context.Messages);
        var result = new JsonArray();
        for (var index = 0; index < transformed.Count; index++)
        {
            var message = transformed[index];
            switch (message)
            {
                case UserMessage user:
                    AddUserMessage(result, user, model);
                    break;
                case AssistantMessage assistant:
                    AddAssistantMessage(result, assistant, model, isOAuth, allowEmptySignature);
                    break;
                case ToolResultMessage:
                    var toolResults = new JsonArray();
                    var siblingContent = new JsonArray();
                    while (index < transformed.Count && transformed[index] is ToolResultMessage toolResult)
                    {
                        var converted = ConvertToolResult(toolResult, model, isOAuth);
                        toolResults.Add((JsonNode?)converted.ToolResult);
                        if (converted.SiblingContent.Count > 0)
                        {
                            foreach (var sibling in converted.SiblingContent)
                            {
                                siblingContent.Add(sibling);
                            }
                        }

                        index++;
                    }

                    index--;
                    foreach (var sibling in siblingContent)
                    {
                        toolResults.Add(sibling);
                    }

                    result.Add((JsonNode?)new JsonObject { ["role"] = "user", ["content"] = toolResults });
                    break;
            }
        }

        return result;
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

        if (string.IsNullOrEmpty(options?.Reasoning))
        {
            return Stream(model, context, CreateAnthropicOptions(options, thinkingEnabled: false));
        }

        if (GetCompatibility(model).ForceAdaptiveThinking)
        {
            return Stream(
                model,
                context,
                CreateAnthropicOptions(
                    options,
                    thinkingEnabled: true,
                    effort: MapThinkingLevelToEffort(model, options!.Reasoning!)));
        }

        var reasoningBudget = ThinkingBudgetForLevel(options!.Reasoning!, options.ThinkingBudgets);
        var maximum = options.MaxTokens ?? model.MaxTokens;
        var adjustedMaximum = options.MaxTokens is null
            ? model.MaxTokens
            : Math.Min(options.MaxTokens.Value + reasoningBudget, model.MaxTokens);
        if (adjustedMaximum <= reasoningBudget)
        {
            reasoningBudget = Math.Min(reasoningBudget, Math.Max(0, adjustedMaximum - 1024));
        }

        return Stream(
            model,
            context,
            CreateAnthropicOptions(
                options,
                maxTokens: Math.Max(1, Math.Min(maximum, adjustedMaximum)),
                thinkingEnabled: true,
                thinkingBudgetTokens: reasoningBudget));
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
            var endpoint = ResolveEndpoint(model.BaseUrl);
            using var response = await SendWithRetryAsync(model, context, endpoint, payload, options).ConfigureAwait(false);
            if (response.Content is null)
            {
                throw new InvalidOperationException("Attempted to iterate over an Anthropic response with no body");
            }

            await using var body = await response.Content.ReadAsStreamAsync(options?.Signal ?? default).ConfigureAwait(false);
            stream.Push(new StreamStartEvent(output));
            var state = new StreamState(output);
            var sawMessageStart = false;
            var sawMessageStop = false;

            await foreach (var sse in SseReader.ReadAsync(body, options?.Signal ?? default))
            {
                if (string.Equals(sse.Event, "error", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(sse.Data);
                }

                if (!IsAnthropicMessageEvent(sse.Event))
                {
                    continue;
                }

                var node = JsonParseUtilities.ParseJsonWithRepair(sse.Data) as JsonObject
                    ?? throw new InvalidOperationException($"Could not parse Anthropic SSE event {sse.Event}: {sse.Data}");
                var eventType = StringValue(node["type"]);
                switch (eventType)
                {
                    case "message_start":
                        sawMessageStart = true;
                        HandleMessageStart(state, model, node);
                        break;
                    case "content_block_start":
                        HandleContentBlockStart(state, stream, context, node, IsOAuthToken(options?.ApiKey));
                        break;
                    case "content_block_delta":
                        HandleContentBlockDelta(state, stream, node);
                        break;
                    case "content_block_stop":
                        HandleContentBlockStop(state, stream, node);
                        break;
                    case "message_delta":
                        HandleMessageDelta(state, model, node);
                        break;
                    case "message_stop":
                        sawMessageStop = true;
                        break;
                }
            }

            if (options?.Signal.IsCancellationRequested == true)
            {
                throw new OperationCanceledException(options.Signal);
            }

            if (sawMessageStart && !sawMessageStop)
            {
                throw new InvalidOperationException("Anthropic stream ended before message_stop");
            }

            output = state.Output;
            if (output.StopReason == StopReasons.Pending)
            {
                throw new InvalidOperationException("Anthropic stream ended without a stop reason");
            }

            if (output.StopReason is StopReasons.Aborted or StopReasons.Error)
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
                    : ErrorBodyUtilities.FormatProviderError(ErrorBodyUtilities.NormalizeProviderError(error)),
            };
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Model model,
        Context context,
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
                        CreateRequestOptions(options),
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

    private static AnthropicStreamOptions CreateAnthropicOptions(
        SimpleStreamOptions? options,
        bool? thinkingEnabled = null,
        int? thinkingBudgetTokens = null,
        string? effort = null,
        int? maxTokens = null) => new()
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
            MaxTokens = maxTokens ?? options?.MaxTokens,
            Transport = options?.Transport,
            CacheRetention = options?.CacheRetention,
            SessionId = options?.SessionId,
            WebSocketConnectTimeoutMs = options?.WebSocketConnectTimeoutMs,
            Metadata = options?.Metadata,
            ThinkingEnabled = thinkingEnabled,
            ThinkingBudgetTokens = thinkingBudgetTokens,
            Effort = effort,
            ToolChoice = options?.ToolChoice,
        };

    private static ProviderRequestOptions CreateRequestOptions(StreamOptions? options) => new()
    {
        Signal = options?.Signal ?? default,
        TelemetryContext = options?.TelemetryContext,
        Fetch = options?.Fetch,
        Environment = options?.Environment,
        OnPayload = options?.OnPayload,
        OnResponse = options?.OnResponse,
        Headers = options?.Headers,
        TimeoutMs = options?.TimeoutMs,
        MaxRetries = options?.MaxRetries,
        MaxRetryDelayMs = options?.MaxRetryDelayMs,
    };

    private static Dictionary<string, string?> BuildDefaultHeaders(Model model, Context context, StreamOptions? options)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["accept"] = "application/json",
            ["anthropic-version"] = _anthropicVersion,
            ["user-agent"] = PiUserAgent.GetPiUserAgent(),
        };
        var apiKey = options?.ApiKey;
        if (model.Provider == "github-copilot" || IsOAuthToken(apiKey))
        {
            if (!string.IsNullOrEmpty(apiKey))
            {
                headers["authorization"] = $"Bearer {apiKey}";
            }
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            headers["x-api-key"] = apiKey;
        }

        var compatibility = GetCompatibility(model);
        var cacheRetention = ResolveCacheRetention(options);
        if (compatibility.SendSessionAffinityHeaders && !string.IsNullOrEmpty(options?.SessionId) &&
            cacheRetention != CacheRetentions.None)
        {
            headers["x-session-affinity"] = options.SessionId;
        }

        var beta = new List<string>();
        var anthropic = options as AnthropicStreamOptions;
        if ((anthropic?.InterleavedThinking ?? true) && !compatibility.ForceAdaptiveThinking)
        {
            beta.Add(_interleavedThinkingBeta);
        }

        if (context.Tools.Count > 0 && !compatibility.SupportsEagerToolInputStreaming)
        {
            beta.Add(_fineGrainedToolStreamingBeta);
        }

        if (GetAllowedFallbackModels(model).Count > 0)
        {
            beta.Add(_serverSideFallbackBeta);
        }

        if (beta.Count > 0)
        {
            headers["anthropic-beta"] = string.Join(',', beta);
        }

        return headers;
    }

    private static void HandleMessageStart(StreamState state, Model model, JsonObject node)
    {
        if (node["message"] is not JsonObject message)
        {
            return;
        }

        state.Output = state.Output with
        {
            ResponseId = StringValue(message["id"]),
            ResponseModel = StringValue(message["model"]) is { } responseModel && responseModel != model.Id
                ? responseModel
                : null,
        };
        if (message["usage"] is JsonObject usage)
        {
            UpdateUsage(state, model, usage, replaceExisting: true);
        }
    }

    private static void HandleContentBlockStart(
        StreamState state,
        AssistantMessageEventStream stream,
        Context context,
        JsonObject node,
        bool isOAuth)
    {
        var index = IntValue(node["index"]);
        if (index is null || node["content_block"] is not JsonObject contentBlock)
        {
            return;
        }

        var type = StringValue(contentBlock["type"]);
        var contentIndex = state.Output.Content.Count;
        ContentBlock? block = type switch
        {
            "text" => new TextContent(StringValue(contentBlock["text"]) ?? string.Empty),
            "thinking" => new ThinkingContent(
                StringValue(contentBlock["thinking"]) ?? string.Empty,
                StringValue(contentBlock["signature"]) ?? string.Empty),
            "redacted_thinking" => new ThinkingContent(
                "[Reasoning redacted]",
                StringValue(contentBlock["data"]) ?? string.Empty,
                true),
            "tool_use" => new ToolCall(
                StringValue(contentBlock["id"]) ?? string.Empty,
                isOAuth
                    ? FromClaudeCodeName(StringValue(contentBlock["name"]) ?? string.Empty, context.Tools)
                    : StringValue(contentBlock["name"]) ?? string.Empty,
                contentBlock["input"] as JsonObject ?? new JsonObject()),
            _ => null,
        };
        if (block is null)
        {
            return;
        }

        var content = state.Output.Content.ToList();
        content.Add(block);
        state.Output = state.Output with { Content = content };
        state.Blocks[index.Value] = new ActiveBlock(index.Value, contentIndex, type ?? string.Empty, block);
        switch (block)
        {
            case TextContent:
                stream.Push(new TextStartEvent(contentIndex, state.Output));
                break;
            case ThinkingContent:
                stream.Push(new ThinkingStartEvent(contentIndex, state.Output));
                break;
            case ToolCall:
                stream.Push(new ToolCallStartEvent(contentIndex, state.Output));
                break;
        }
    }

    private static void HandleContentBlockDelta(
        StreamState state,
        AssistantMessageEventStream stream,
        JsonObject node)
    {
        var index = IntValue(node["index"]);
        if (index is null || !state.Blocks.TryGetValue(index.Value, out var active) ||
            node["delta"] is not JsonObject delta)
        {
            return;
        }

        switch (StringValue(delta["type"]))
        {
            case "text_delta" when active.Block is TextContent:
                var text = StringValue(delta["text"]) ?? string.Empty;
                var currentText = (TextContent)active.Block;
                var updatedText = currentText with { Text = currentText.Text + text };
                ReplaceContent(state, active, updatedText);
                stream.Push(new TextDeltaEvent(active.ContentIndex, text, state.Output));
                break;
            case "thinking_delta" when active.Block is ThinkingContent:
                var thinking = StringValue(delta["thinking"]) ?? string.Empty;
                var currentThinking = (ThinkingContent)active.Block;
                var updatedThinking = currentThinking with { Thinking = currentThinking.Thinking + thinking };
                ReplaceContent(state, active, updatedThinking);
                stream.Push(new ThinkingDeltaEvent(active.ContentIndex, thinking, state.Output));
                break;
            case "input_json_delta" when active.Block is ToolCall:
                var partial = StringValue(delta["partial_json"]) ?? string.Empty;
                active.PartialJson += partial;
                var arguments = JsonParseUtilities.ParseStreamingJson(active.PartialJson) as JsonObject ?? new JsonObject();
                var currentTool = (ToolCall)active.Block;
                var updatedTool = currentTool with { Arguments = arguments };
                ReplaceContent(state, active, updatedTool);
                stream.Push(new ToolCallDeltaEvent(active.ContentIndex, partial, state.Output));
                break;
            case "signature_delta" when active.Block is ThinkingContent:
                var signature = StringValue(delta["signature"]) ?? string.Empty;
                var signedThinking = (ThinkingContent)active.Block;
                ReplaceContent(
                    state,
                    active,
                    signedThinking with { ThinkingSignature = (signedThinking.ThinkingSignature ?? string.Empty) + signature });
                break;
        }
    }

    private static void HandleContentBlockStop(
        StreamState state,
        AssistantMessageEventStream stream,
        JsonObject node)
    {
        var index = IntValue(node["index"]);
        if (index is null || !state.Blocks.Remove(index.Value, out var active))
        {
            return;
        }

        switch (active.Block)
        {
            case TextContent text:
                stream.Push(new TextEndEvent(active.ContentIndex, text.Text, state.Output));
                break;
            case ThinkingContent thinking:
                stream.Push(new ThinkingEndEvent(active.ContentIndex, thinking.Thinking, state.Output));
                break;
            case ToolCall tool:
                var arguments = JsonParseUtilities.ParseStreamingJson(active.PartialJson) as JsonObject ?? tool.Arguments;
                var finalized = tool with { Arguments = arguments };
                ReplaceContent(state, active, finalized);
                stream.Push(new ToolCallEndEvent(active.ContentIndex, finalized, state.Output));
                break;
        }
    }

    private static void HandleMessageDelta(StreamState state, Model model, JsonObject node)
    {
        if (node["delta"] is JsonObject delta && StringValue(delta["stop_reason"]) is { } rawReason)
        {
            var mapped = MapStopReason(rawReason, StringValue((delta["stop_details"] as JsonObject)?["explanation"]));
            state.Output = state.Output with
            {
                StopReason = mapped.StopReason,
                RawStopReason = rawReason,
                ErrorMessage = mapped.ErrorMessage,
            };
        }

        if (node["usage"] is JsonObject usage)
        {
            UpdateUsage(state, model, usage, replaceExisting: false);
        }
    }

    private static void UpdateUsage(StreamState state, Model model, JsonObject usage, bool replaceExisting)
    {
        var current = state.Output.Usage;
        var updated = current with
        {
            Input = replaceExisting ? IntValue(usage["input_tokens"]) ?? 0 : IntValue(usage["input_tokens"]) ?? current.Input,
            Output = replaceExisting ? IntValue(usage["output_tokens"]) ?? 0 : IntValue(usage["output_tokens"]) ?? current.Output,
            CacheRead = replaceExisting
                ? IntValue(usage["cache_read_input_tokens"]) ?? 0
                : IntValue(usage["cache_read_input_tokens"]) ?? current.CacheRead,
            CacheWrite = replaceExisting
                ? IntValue(usage["cache_creation_input_tokens"]) ?? 0
                : IntValue(usage["cache_creation_input_tokens"]) ?? current.CacheWrite,
            CacheWrite1h = replaceExisting
                ? IntValue((usage["cache_creation"] as JsonObject)?["ephemeral_1h_input_tokens"]) ?? 0
                : IntValue((usage["cache_creation"] as JsonObject)?["ephemeral_1h_input_tokens"]) ?? current.CacheWrite1h,
            Reasoning = IntValue((usage["output_tokens_details"] as JsonObject)?["thinking_tokens"]) ?? current.Reasoning,
        };
        updated = updated with
        {
            TotalTokens = updated.Input + updated.Output + updated.CacheRead + updated.CacheWrite,
        };
        ModelUtilities.CalculateCost(model, updated);
        state.Output = state.Output with { Usage = updated };
    }

    private static void ReplaceContent(StreamState state, ActiveBlock active, ContentBlock replacement)
    {
        active.Block = replacement;
        var content = state.Output.Content.ToList();
        content[active.ContentIndex] = replacement;
        state.Output = state.Output with { Content = content };
    }

    private static void AddUserMessage(JsonArray destination, UserMessage message, Model model)
    {
        if (message.Content is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                destination.Add((JsonNode?)new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = UnicodeUtilities.SanitizeSurrogates(text),
                });
            }

            return;
        }

        if (message.Content is not IEnumerable<ContentBlock> blocks)
        {
            return;
        }

        var converted = ConvertContentBlocks(blocks, model, "(image omitted: model does not support images)");
        if (converted is JsonArray array && array.Count == 0 || converted is JsonValue value && string.IsNullOrWhiteSpace(value.GetValue<string>()))
        {
            return;
        }

        destination.Add((JsonNode?)new JsonObject { ["role"] = "user", ["content"] = converted });
    }

    private static void AddAssistantMessage(
        JsonArray destination,
        AssistantMessage message,
        Model model,
        bool isOAuth,
        bool allowEmptySignature)
    {
        if (message.StopReason is StopReasons.Error or StopReasons.Aborted)
        {
            return;
        }

        var sameModel = string.Equals(message.Provider, model.Provider, StringComparison.Ordinal) &&
                        string.Equals(message.Api, model.Api, StringComparison.Ordinal) &&
                        string.Equals(message.Model, model.Id, StringComparison.Ordinal);
        var blocks = new JsonArray();
        foreach (var contentBlock in message.Content)
        {
            switch (contentBlock)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    blocks.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = UnicodeUtilities.SanitizeSurrogates(text.Text),
                    });
                    break;
                case ThinkingContent thinking:
                    if (thinking.Redacted == true)
                    {
                        if (sameModel && !string.IsNullOrEmpty(thinking.ThinkingSignature))
                        {
                            blocks.Add((JsonNode?)new JsonObject
                            {
                                ["type"] = "redacted_thinking",
                                ["data"] = thinking.ThinkingSignature,
                            });
                        }

                        break;
                    }

                    var signature = thinking.ThinkingSignature;
                    if (string.IsNullOrWhiteSpace(thinking.Thinking) && string.IsNullOrWhiteSpace(signature))
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(signature))
                    {
                        blocks.Add((JsonNode?)new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = UnicodeUtilities.SanitizeSurrogates(thinking.Thinking),
                            ["signature"] = signature,
                        });
                    }
                    else if (allowEmptySignature)
                    {
                        blocks.Add((JsonNode?)new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = UnicodeUtilities.SanitizeSurrogates(thinking.Thinking),
                            ["signature"] = string.Empty,
                        });
                    }
                    else
                    {
                        blocks.Add((JsonNode?)new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = UnicodeUtilities.SanitizeSurrogates(thinking.Thinking),
                        });
                    }

                    break;
                case ToolCall toolCall:
                    blocks.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = NormalizeToolCallId(toolCall.Id),
                        ["name"] = isOAuth ? ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                        ["input"] = toolCall.Arguments.DeepClone(),
                    });
                    break;
            }
        }

        if (blocks.Count > 0)
        {
            destination.Add((JsonNode?)new JsonObject { ["role"] = "assistant", ["content"] = blocks });
        }
    }

    private static JsonNode ConvertContentBlocks(
        IEnumerable<ContentBlock> content,
        Model model,
        string imagePlaceholder)
    {
        var blocks = content.ToList();
        var hasImages = blocks.OfType<ImageContent>().Any();
        if (!hasImages)
        {
            return JsonValue.Create(UnicodeUtilities.SanitizeSurrogates(
                string.Join("\n", blocks.OfType<TextContent>().Select(static block => block.Text))))!;
        }

        var converted = new JsonArray();
        foreach (var block in blocks)
        {
            if (block is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
            {
                converted.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = UnicodeUtilities.SanitizeSurrogates(text.Text),
                });
            }
            else if (block is ImageContent image)
            {
                if (model.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
                {
                    converted.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MimeType,
                            ["data"] = image.Data,
                        },
                    });
                }
                else if (converted.Count == 0 || StringValue(converted[^1]?["text"]) != imagePlaceholder)
                {
                    converted.Add((JsonNode?)new JsonObject { ["type"] = "text", ["text"] = imagePlaceholder });
                }
            }
        }

        if (converted.Count == 0 || !converted.Any(node => StringValue(node?["type"]) == "text"))
        {
            converted.Insert(0, (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = "(see attached image)" });
        }

        return converted;
    }

    private static (JsonObject ToolResult, List<JsonNode> SiblingContent) ConvertToolResult(
        ToolResultMessage message,
        Model model,
        bool isOAuth)
    {
        var converted = ConvertContentBlocks(message.Content, model, "(tool image omitted: model does not support images)");
        var toolResult = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = NormalizeToolCallId(message.ToolCallId),
            ["content"] = converted,
            ["is_error"] = message.IsError,
        };
        return (toolResult, []);
    }

    private static JsonArray ConvertTools(
        IReadOnlyList<Tool> tools,
        bool isOAuth,
        AnthropicCompatibility compatibility)
    {
        var result = new JsonArray();
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var sourceSchema = tool.Parameters.DeepClone() as JsonObject;
            if (sourceSchema is null)
            {
                sourceSchema = new JsonObject { ["type"] = "object" };
            }

            var strict = compatibility.SupportsStrictTools && tool.ConstrainedSampling is JsonSchemaSampling;
            var legacySchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = sourceSchema["properties"]?.DeepClone() ?? new JsonObject(),
                ["required"] = sourceSchema["required"]?.DeepClone() ?? new JsonArray(),
            };
            var inputSchema = strict ? sourceSchema : legacySchema;

            var converted = new JsonObject
            {
                ["name"] = isOAuth ? ToClaudeCodeName(tool.Name) : tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = inputSchema,
            };
            if (compatibility.SupportsEagerToolInputStreaming)
            {
                converted["eager_input_streaming"] = true;
            }

            if (strict)
            {
                converted["strict"] = true;
            }

            result.Add((JsonNode?)converted);
        }

        return result;
    }

    private static JsonObject BuildToolChoice(string value, string? name) => value switch
    {
        "auto" or "any" or "none" => new JsonObject { ["type"] = value },
        "tool" => new JsonObject
        {
            ["type"] = "tool",
            ["name"] = name ?? string.Empty,
        },
        _ => new JsonObject { ["type"] = value },
    };

    private static List<Message> TransformMessages(Model model, IReadOnlyList<Message> messages)
    {
        var toolCallIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assistant in messages.OfType<AssistantMessage>())
        {
            foreach (var toolCall in assistant.Content.OfType<ToolCall>())
            {
                toolCallIds[toolCall.Id] = NormalizeToolCallId(toolCall.Id);
            }
        }

        var transformed = new List<Message>(messages.Count);
        var pendingToolCalls = new List<ToolCall>();
        var existingToolResultIds = new HashSet<string>(StringComparer.Ordinal);

        void InsertSyntheticToolResults()
        {
            foreach (var toolCall in pendingToolCalls)
            {
                var normalizedId = NormalizeToolCallId(toolCall.Id);
                if (!existingToolResultIds.Contains(normalizedId))
                {
                    transformed.Add(new ToolResultMessage
                    {
                        ToolCallId = normalizedId,
                        ToolName = toolCall.Name,
                        Content = [new TextContent("No result provided")],
                        IsError = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                }
            }

            pendingToolCalls.Clear();
            existingToolResultIds.Clear();
        }

        foreach (var message in messages)
        {
            switch (message)
            {
                case AssistantMessage assistant:
                    InsertSyntheticToolResults();
                    if (assistant.StopReason is StopReasons.Error or StopReasons.Aborted)
                    {
                        continue;
                    }

                    var sameModel = string.Equals(assistant.Provider, model.Provider, StringComparison.Ordinal) &&
                                    string.Equals(assistant.Api, model.Api, StringComparison.Ordinal) &&
                                    string.Equals(assistant.Model, model.Id, StringComparison.Ordinal);
                    var content = new List<ContentBlock>();
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
                                content.Add(new TextContent(thinkingBlock.Thinking));
                            }

                            continue;
                        }

                        if (block is ToolCall toolCall && toolCallIds.TryGetValue(toolCall.Id, out var normalizedId))
                        {
                            content.Add(toolCall with { Id = normalizedId, Arguments = (JsonObject)toolCall.Arguments.DeepClone() });
                        }
                        else
                        {
                            content.Add(block);
                        }
                    }

                    var transformedAssistant = assistant with { Content = content };
                    transformed.Add(transformedAssistant);
                    pendingToolCalls.AddRange(content.OfType<ToolCall>());
                    break;
                case ToolResultMessage toolResult:
                    var normalizedResultId = toolCallIds.TryGetValue(toolResult.ToolCallId, out var mappedId)
                        ? mappedId
                        : NormalizeToolCallId(toolResult.ToolCallId);
                    existingToolResultIds.Add(normalizedResultId);
                    transformed.Add(toolResult with { ToolCallId = normalizedResultId });
                    break;
                case UserMessage user:
                    InsertSyntheticToolResults();
                    transformed.Add(user);
                    break;
                default:
                    transformed.Add(message);
                    break;
            }
        }

        InsertSyntheticToolResults();
        return transformed;
    }

    private static void AppendSystemPrompt(JsonArray system, string? systemPrompt, CacheSettings? cacheControl)
    {
        if (string.IsNullOrEmpty(systemPrompt))
        {
            return;
        }

        var node = new JsonObject
        {
            ["type"] = "text",
            ["text"] = UnicodeUtilities.SanitizeSurrogates(systemPrompt),
        };
        ApplyCacheControl(node, cacheControl);
        system.Add((JsonNode?)node);
    }

    private static void ApplyLastUserCacheControl(JsonArray messages, CacheSettings? cacheControl)
    {
        if (cacheControl is null || messages.Count == 0 || messages[^1] is not JsonObject last ||
            StringValue(last["role"]) != "user")
        {
            return;
        }

        if (last["content"] is JsonArray content && content.Count > 0 && content[^1] is JsonObject block)
        {
            ApplyCacheControl(block, cacheControl);
            return;
        }

        if (last["content"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
        {
            var blocks = new JsonArray
            {
                (JsonNode?)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            };
            ApplyCacheControl(blocks[0] as JsonObject, cacheControl);
            last["content"] = blocks;
        }
    }

    private static void ApplyCacheControl(JsonObject? node, CacheSettings? settings, bool topLevel = false)
    {
        if (node is null || settings is null || !settings.Value.Enabled)
        {
            return;
        }

        var cacheControl = new JsonObject { ["type"] = "ephemeral" };
        if (settings.Value.Ttl is not null)
        {
            cacheControl["ttl"] = settings.Value.Ttl;
        }

        node["cache_control"] = cacheControl;
    }

    private static CacheSettings? ResolveCacheControl(Model model, StreamOptions? options)
    {
        var retention = ResolveCacheRetention(options);
        if (retention == CacheRetentions.None)
        {
            return null;
        }

        var longRetention = retention == CacheRetentions.Long && GetCompatibility(model).SupportsLongCacheRetention;
        return new CacheSettings(true, longRetention ? "1h" : null);
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

    private static bool HasDisabledThinkingMapping(Model model) =>
        model.ThinkingLevelMap is not null &&
        model.ThinkingLevelMap.TryGetValue(ThinkingLevels.Off, out var mapped) &&
        mapped is null;

    private static List<string> GetAllowedFallbackModels(Model model)
    {
        if (model.Compatibility?["allowedFallbackModels"] is not JsonArray fallbacks)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var fallback in fallbacks.OfType<JsonObject>())
        {
            var fallbackModel = StringValue(fallback["model"]);
            if (!string.IsNullOrEmpty(fallbackModel))
            {
                result.Add(fallbackModel);
            }
        }

        return result;
    }

    private static AnthropicCompatibility GetCompatibility(Model model) => new(
        SupportsEagerToolInputStreaming: GetBool(model.Compatibility, "supportsEagerToolInputStreaming", true),
        SupportsLongCacheRetention: GetBool(model.Compatibility, "supportsLongCacheRetention", true),
        SendSessionAffinityHeaders: GetBool(model.Compatibility, "sendSessionAffinityHeaders", false),
        SupportsTemperature: GetBool(model.Compatibility, "supportsTemperature", true),
        ForceAdaptiveThinking: GetBool(model.Compatibility, "forceAdaptiveThinking", false),
        AllowEmptySignature: GetBool(model.Compatibility, "allowEmptySignature", false),
        SupportsStrictTools: GetBool(model.Compatibility, "supportsStrictTools", false));

    private static void EnsureRequestAuth(Model model, StreamOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.ApiKey))
        {
            return;
        }

        if (HasHeader(model.Headers, "authorization") || HasHeader(model.Headers, "x-api-key") ||
            HasHeader(model.Headers, "cf-aig-authorization") || HasHeader(options?.Headers, "authorization") ||
            HasHeader(options?.Headers, "x-api-key") || HasHeader(options?.Headers, "cf-aig-authorization"))
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

    private static bool IsOAuthToken(string? apiKey) => apiKey?.Contains("sk-ant-oat", StringComparison.Ordinal) == true;

    private static string ToClaudeCodeName(string name)
    {
        var match = _claudeCodeTools.FirstOrDefault(tool => string.Equals(tool, name, StringComparison.OrdinalIgnoreCase));
        return match ?? name;
    }

    private static string FromClaudeCodeName(string name, IReadOnlyList<Tool> tools)
    {
        var match = tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? name;
    }

    private static string NormalizeToolCallId(string id)
    {
        var sanitized = new string(id.Select(static character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    private static string MapThinkingLevelToEffort(Model model, string level)
    {
        if (model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(level, out var mapped) &&
            !string.IsNullOrEmpty(mapped))
        {
            return mapped;
        }

        return level switch
        {
            ThinkingLevels.Minimal or ThinkingLevels.Low => "low",
            ThinkingLevels.Medium => "medium",
            ThinkingLevels.High => "high",
            _ => "high",
        };
    }

    private static int ThinkingBudgetForLevel(string level, ThinkingBudgets? custom)
    {
        var budget = level switch
        {
            ThinkingLevels.Minimal => custom?.Minimal ?? 1024,
            ThinkingLevels.Low => custom?.Low ?? 2048,
            ThinkingLevels.Medium => custom?.Medium ?? 8192,
            ThinkingLevels.High or ThinkingLevels.XHigh or ThinkingLevels.Max => custom?.High ?? 16384,
            _ => 1024,
        };
        return Math.Max(0, budget);
    }

    private static Uri ResolveEndpoint(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        var path = normalized.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase)
            ? "messages"
            : "v1/messages";
        return new Uri(new Uri(normalized, UriKind.Absolute), path);
    }

    private static bool IsAnthropicMessageEvent(string? eventName) => eventName is
        "message_start" or "message_delta" or "message_stop" or "content_block_start" or
        "content_block_delta" or "content_block_stop";

    private static (string StopReason, string? ErrorMessage) MapStopReason(string reason, string? explanation) => reason switch
    {
        "end_turn" or "pause_turn" or "stop_sequence" => (StopReasons.Stop, null),
        "max_tokens" => (StopReasons.Length, null),
        "tool_use" => (StopReasons.ToolUse, null),
        "refusal" => (StopReasons.Error, string.IsNullOrEmpty(explanation)
            ? "The model refused to complete the request"
            : explanation),
        "sensitive" => (StopReasons.Error, "Provider stopped with: sensitive"),
        _ => throw new InvalidOperationException($"Unhandled stop reason: {reason}"),
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
                return node?.GetValue<long>() is { } value ? checked((int)value) : null;
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

        public Dictionary<int, ActiveBlock> Blocks { get; } = [];
    }

    private sealed class ActiveBlock(int streamIndex, int contentIndex, string type, ContentBlock block)
    {
        public int StreamIndex { get; } = streamIndex;

        public int ContentIndex { get; } = contentIndex;

        public string Type { get; } = type;

        public ContentBlock Block { get; set; } = block;

        public string PartialJson { get; set; } = string.Empty;
    }

    private readonly record struct CacheSettings(bool Enabled, string? Ttl);

    private readonly record struct AnthropicCompatibility(
        bool SupportsEagerToolInputStreaming,
        bool SupportsLongCacheRetention,
        bool SendSessionAffinityHeaders,
        bool SupportsTemperature,
        bool ForceAdaptiveThinking,
        bool AllowEmptySignature,
        bool SupportsStrictTools);
}
