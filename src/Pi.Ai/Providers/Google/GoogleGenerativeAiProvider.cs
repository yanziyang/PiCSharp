using System.Net;
using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Google Generative AI provider-specific stream options.</summary>
public sealed class GoogleOptions : StreamOptions
{
    /// <summary>Provider tool choice: <c>auto</c>, <c>none</c>, or <c>any</c>.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Optional Gemini thinking configuration.</summary>
    public GoogleThinkingOptions? Thinking { get; init; }
}

/// <summary>Explicit Gemini thinking configuration.</summary>
public sealed record GoogleThinkingOptions
{
    /// <summary>Whether the provider should expose thinking parts.</summary>
    public bool Enabled { get; init; }

    /// <summary>Budget in thinking tokens; <c>-1</c> requests a dynamic budget.</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>Gemini 3 thinking level.</summary>
    public string? Level { get; init; }
}

/// <summary>Raw HTTP/SSE implementation of Pi's Google Generative AI API.</summary>
public sealed class GoogleGenerativeAiProvider : ProviderStreams
{
    private readonly ProviderHttpClient _transport;
    private static int _toolCallCounter;

    /// <summary>Creates an adapter backed by the supplied HTTP transport.</summary>
    public GoogleGenerativeAiProvider(ProviderHttpClient? transport = null)
    {
        _transport = transport ?? new ProviderHttpClient();
    }

    /// <summary>Builds a Gemini <c>generateContentStream</c> request payload.</summary>
    public static JsonObject BuildPayload(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var googleOptions = options as GoogleOptions;
        var config = new JsonObject();
        if (options?.Temperature is not null)
        {
            config["temperature"] = options.Temperature.Value;
        }

        if (options?.MaxTokens is not null)
        {
            config["maxOutputTokens"] = options.MaxTokens.Value;
        }

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            config["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["text"] = UnicodeUtilities.SanitizeSurrogates(context.SystemPrompt),
                    },
                },
            };
        }

        var supportsStrictMode = GoogleShared.SupportsGoogleStrictToolSampling(model.Id);
        if (context.Tools.Count > 0)
        {
            var tools = GoogleShared.ConvertTools(context.Tools, useParameters: false, supportsStrictMode);
            if (tools is not null)
            {
                config["tools"] = tools;
            }

            var functionCallingMode = GoogleShared.ResolveGoogleFunctionCallingMode(
                context.Tools,
                googleOptions?.ToolChoice,
                supportsStrictMode);
            if (functionCallingMode is not null)
            {
                config["toolConfig"] = new JsonObject
                {
                    ["functionCallingConfig"] = new JsonObject { ["mode"] = functionCallingMode },
                };
            }
        }

        if (model.Reasoning && googleOptions?.Thinking is { } thinking)
        {
            if (thinking.Enabled)
            {
                var thinkingConfig = new JsonObject { ["includeThoughts"] = true };
                if (thinking.Level is not null)
                {
                    thinkingConfig["thinkingLevel"] = thinking.Level;
                }
                else if (thinking.BudgetTokens is not null)
                {
                    thinkingConfig["thinkingBudget"] = thinking.BudgetTokens.Value;
                }

                config["thinkingConfig"] = thinkingConfig;
            }
            else
            {
                config["thinkingConfig"] = GoogleShared.GetDisabledThinkingConfig(model);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["contents"] = GoogleShared.ConvertMessages(model, context),
        };
        if (config.ContainsKey("temperature") || config.ContainsKey("maxOutputTokens"))
        {
            var generationConfig = new JsonObject();
            if (config["temperature"] is { } temperature)
            {
                generationConfig["temperature"] = temperature.DeepClone();
            }

            if (config["maxOutputTokens"] is { } maxOutputTokens)
            {
                generationConfig["maxOutputTokens"] = maxOutputTokens.DeepClone();
            }

            payload["generationConfig"] = generationConfig;
        }

        CopyConfigProperty(payload, config, "systemInstruction");
        CopyConfigProperty(payload, config, "tools");
        CopyConfigProperty(payload, config, "toolConfig");
        CopyConfigProperty(payload, config, "thinkingConfig");
        return payload;
    }

    /// <summary>Converts Pi context messages to Gemini request contents.</summary>
    public static JsonArray ConvertMessages(Model model, Context context) => GoogleShared.ConvertMessages(model, context);

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

        if (string.IsNullOrEmpty(options.Reasoning))
        {
            return Stream(
                model,
                context,
                CopyCommonOptions(
                    model,
                    options,
                    options.ApiKey,
                    new GoogleThinkingOptions { Enabled = false }));
        }

        var clamped = ModelUtilities.ClampThinkingLevel(model, options.Reasoning);
        var resolved = GoogleShared.ResolveThinkingLevel(model, clamped);
        var thinking = IsGemini3ProModel(model) || IsGemini3FlashModel(model) || IsGemma4Model(model)
            ? new GoogleThinkingOptions
            {
                Enabled = true,
                Level = GoogleShared.GetThinkingLevel(model, resolved),
            }
            : new GoogleThinkingOptions
            {
                Enabled = true,
                BudgetTokens = GoogleShared.GetGoogleBudget(model, resolved, options.ThinkingBudgets),
            };
        return Stream(model, context, CopyCommonOptions(model, options, options.ApiKey, thinking));
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
            if (options?.Fetch is not null)
            {
                throw new InvalidOperationException("Custom fetch is not supported by the Google Generative AI adapter");
            }

            if (string.IsNullOrEmpty(options?.ApiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }

            var payload = BuildPayload(model, context, options);
            using var response = await SendWithRetryAsync(model, payload, options).ConfigureAwait(false);
            if (response.Content is null)
            {
                throw new InvalidOperationException("Google Generative AI response has no body");
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
                if (node is not null)
                {
                    HandleChunk(state, stream, model, node);
                }
            }

            CloseCurrentBlock(state, stream);
            output = state.Output;
            if (options?.Signal.IsCancellationRequested == true)
            {
                throw new OperationCanceledException(options.Signal);
            }

            if (!state.HasFinishReason)
            {
                throw new InvalidOperationException("Google stream ended without a finish reason");
            }

            if (output.StopReason is StopReasons.Error or StopReasons.Aborted)
            {
                throw new InvalidOperationException(
                    output.RawStopReason is { Length: > 0 } raw
                        ? $"Provider stopped with: {raw}"
                        : "An unknown error occurred");
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
                ErrorMessage = ErrorBodyUtilities.FormatProviderError(ErrorBodyUtilities.NormalizeProviderError(error)),
            };
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Model model,
        JsonObject payload,
        StreamOptions? options)
    {
        async Task<HttpResponseMessage> SendAttempt()
        {
            try
            {
                var key = options?.ApiKey ?? string.Empty;
                return await _transport.SendAsync(
                        model,
                        HttpMethod.Post,
                        ResolveEndpoint(model.BaseUrl, model.Id, key),
                        payload,
                        WithoutApiKey(options),
                        BuildDefaultHeaders(model, options),
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
        if (StringValue(chunk["responseId"]) is { Length: > 0 } responseId && state.Output.ResponseId is null)
        {
            state.Output = state.Output with { ResponseId = responseId };
        }

        if (chunk["candidates"] is JsonArray candidates && candidates.Count > 0 && candidates[0] is JsonObject candidate)
        {
            if (candidate["content"] is JsonObject content && content["parts"] is JsonArray parts)
            {
                foreach (var part in parts.OfType<JsonObject>())
                {
                    HandlePart(state, stream, model, part);
                }
            }

            if (StringValue(candidate["finishReason"]) is { } finishReason)
            {
                state.HasFinishReason = true;
                var stopReason = GoogleShared.MapStopReason(finishReason);
                if (stopReason == StopReasons.Stop && state.Output.Content.OfType<ToolCall>().Any())
                {
                    stopReason = StopReasons.ToolUse;
                }

                state.Output = state.Output with
                {
                    RawStopReason = finishReason,
                    StopReason = stopReason,
                };
            }
        }

        if (chunk["usageMetadata"] is JsonObject usage)
        {
            state.Output = state.Output with { Usage = ParseUsage(model, usage) };
        }
    }

    private static void HandlePart(
        StreamState state,
        AssistantMessageEventStream stream,
        Model model,
        JsonObject part)
    {
        if (part.ContainsKey("text"))
        {
            var text = StringValue(part["text"]) ?? string.Empty;
            var thinking = GoogleShared.IsThinkingPart(part);
            if (thinking)
            {
                AppendThinking(state, stream, text, StringValue(part["thoughtSignature"]));
            }
            else
            {
                AppendText(state, stream, text, StringValue(part["thoughtSignature"]));
            }
        }

        if (part["functionCall"] is JsonObject functionCall)
        {
            CloseCurrentBlock(state, stream);
            var providedId = StringValue(functionCall["id"]);
            var name = StringValue(functionCall["name"]) ?? string.Empty;
            var duplicate = !string.IsNullOrEmpty(providedId) &&
                            state.Output.Content.OfType<ToolCall>().Any(call => call.Id == providedId);
            var toolCallId = string.IsNullOrEmpty(providedId) || duplicate
                ? $"{name}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _toolCallCounter)}"
                : providedId;
            var arguments = functionCall["args"] as JsonObject ?? new JsonObject();
            var signature = StringValue(part["thoughtSignature"]);
            var toolCall = new ToolCall(
                toolCallId,
                name,
                arguments.DeepClone() as JsonObject ?? new JsonObject(),
                signature);
            var content = state.Output.Content.ToList();
            content.Add(toolCall);
            state.Output = state.Output with { Content = content };
            var index = content.Count - 1;
            stream.Push(new ToolCallStartEvent(index, state.Output));
            var delta = arguments.ToJsonString();
            stream.Push(new ToolCallDeltaEvent(index, delta, state.Output));
            stream.Push(new ToolCallEndEvent(index, toolCall, state.Output));
        }
    }

    private static void AppendText(StreamState state, AssistantMessageEventStream stream, string delta, string? signature)
    {
        if (state.CurrentIndex is { } current && state.Output.Content[current] is ThinkingContent)
        {
            CloseCurrentBlock(state, stream);
        }

        if (state.CurrentIndex is null)
        {
            var content = state.Output.Content.ToList();
            content.Add(new TextContent(string.Empty));
            state.CurrentIndex = content.Count - 1;
            state.Output = state.Output with { Content = content };
            stream.Push(new TextStartEvent(state.CurrentIndex.Value, state.Output));
        }

        var index = state.CurrentIndex.Value;
        var currentText = (TextContent)state.Output.Content[index];
        var next = currentText with
        {
            Text = currentText.Text + delta,
            TextSignature = GoogleShared.RetainThoughtSignature(currentText.TextSignature, signature),
        };
        var updated = state.Output.Content.ToList();
        updated[index] = next;
        state.Output = state.Output with { Content = updated };
        stream.Push(new TextDeltaEvent(index, delta, state.Output));
    }

    private static void AppendThinking(StreamState state, AssistantMessageEventStream stream, string delta, string? signature)
    {
        if (state.CurrentIndex is { } current && state.Output.Content[current] is TextContent)
        {
            CloseCurrentBlock(state, stream);
        }

        if (state.CurrentIndex is null)
        {
            var content = state.Output.Content.ToList();
            content.Add(new ThinkingContent(string.Empty));
            state.CurrentIndex = content.Count - 1;
            state.Output = state.Output with { Content = content };
            stream.Push(new ThinkingStartEvent(state.CurrentIndex.Value, state.Output));
        }

        var index = state.CurrentIndex.Value;
        var currentThinking = (ThinkingContent)state.Output.Content[index];
        var next = currentThinking with
        {
            Thinking = currentThinking.Thinking + delta,
            ThinkingSignature = GoogleShared.RetainThoughtSignature(currentThinking.ThinkingSignature, signature),
        };
        var updated = state.Output.Content.ToList();
        updated[index] = next;
        state.Output = state.Output with { Content = updated };
        stream.Push(new ThinkingDeltaEvent(index, delta, state.Output));
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

    private static Usage ParseUsage(Model model, JsonObject usage)
    {
        var prompt = IntValue(usage["promptTokenCount"]);
        var cached = IntValue(usage["cachedContentTokenCount"]);
        var candidates = IntValue(usage["candidatesTokenCount"]);
        var thoughts = IntValue(usage["thoughtsTokenCount"]);
        var result = new Usage
        {
            Input = (prompt ?? 0) - (cached ?? 0),
            Output = (candidates ?? 0) + (thoughts ?? 0),
            CacheRead = cached ?? 0,
            CacheWrite = 0,
            Reasoning = thoughts ?? 0,
            TotalTokens = IntValue(usage["totalTokenCount"]) ?? 0,
        };
        ModelUtilities.CalculateCost(model, result);
        return result;
    }

    private static GoogleOptions CopyCommonOptions(
        Model model,
        SimpleStreamOptions options,
        string apiKey,
        GoogleThinkingOptions? thinking) => new()
        {
            Signal = options.Signal,
            TelemetryContext = options.TelemetryContext,
            ApiKey = apiKey,
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
            ToolChoice = options.ToolChoice,
            Thinking = thinking,
        };

    private static void CopyConfigProperty(JsonObject destination, JsonObject source, string name)
    {
        if (source[name] is { } value)
        {
            destination[name] = value.DeepClone();
        }
    }

    private static ProviderRequestOptions WithoutApiKey(StreamOptions? options) => new()
    {
        Signal = options?.Signal ?? default,
        TelemetryContext = options?.TelemetryContext,
        Environment = options?.Environment,
        OnPayload = options?.OnPayload,
        OnResponse = options?.OnResponse,
        Headers = options?.Headers,
        TimeoutMs = options?.TimeoutMs,
        MaxRetries = options?.MaxRetries,
        MaxRetryDelayMs = options?.MaxRetryDelayMs,
    };

    private static Dictionary<string, string?> BuildDefaultHeaders(Model model, StreamOptions? options)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "text/event-stream",
            ["User-Agent"] = PiUserAgent.GetPiUserAgent(),
        };
        if (model.Headers is not null)
        {
            foreach (var pair in model.Headers)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        if (options?.Headers is not null)
        {
            foreach (var pair in options.Headers)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        return headers;
    }

    private static Uri ResolveEndpoint(string baseUrl, string modelId, string apiKey)
    {
        var normalized = baseUrl.TrimEnd('/');
        if (!normalized.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/v1beta";
        }

        return new Uri(
            $"{normalized}/models/{Uri.EscapeDataString(modelId)}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(apiKey)}",
            UriKind.Absolute);
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

    private static bool IsGemini3ProModel(Model model) =>
        model.Id.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) &&
        model.Id.Contains("-pro", StringComparison.OrdinalIgnoreCase);

    private static bool IsGemini3FlashModel(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        return (id.Contains("gemini-3", StringComparison.Ordinal) && id.Contains("-flash", StringComparison.Ordinal)) ||
               id is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }

    private static bool IsGemma4Model(Model model) =>
        model.Id.Contains("gemma-4", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("gemma4", StringComparison.OrdinalIgnoreCase);

    private static string? StringValue(JsonNode? value)
    {
        try
        {
            return value?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static int? IntValue(JsonNode? value)
    {
        try
        {
            return value?.GetValue<int>();
        }
        catch
        {
            try
            {
                return value?.GetValue<long>() is { } longValue ? checked((int)longValue) : null;
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

        public int? CurrentIndex { get; set; }

        public bool HasFinishReason { get; set; }
    }
}
