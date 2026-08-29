using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.Documents;

using AwsContentBlock = Amazon.BedrockRuntime.Model.ContentBlock;
using AwsContentBlockDelta = Amazon.BedrockRuntime.Model.ContentBlockDelta;
using AwsContentBlockStart = Amazon.BedrockRuntime.Model.ContentBlockStart;
using AwsMessage = Amazon.BedrockRuntime.Model.Message;
using AwsTool = Amazon.BedrockRuntime.Model.Tool;

namespace Pi.Ai;

/// <summary>Bedrock Converse provider-specific stream controls.</summary>
public sealed class BedrockOptions : StreamOptions
{
    /// <summary>Explicit AWS region, overriding provider-scoped environment values.</summary>
    public string? Region { get; init; }

    /// <summary>Explicit AWS shared-credentials profile.</summary>
    public string? Profile { get; init; }

    /// <summary>Bedrock tool choice: <c>auto</c>, <c>any</c>, <c>none</c>, or a named-tool object.</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>Requested Bedrock reasoning level.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Custom reasoning token budgets.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }

    /// <summary>Whether to request Anthropic interleaved thinking on fixed-budget models.</summary>
    public bool? InterleavedThinking { get; init; }

    /// <summary>Controls summarized versus omitted Anthropic reasoning content.</summary>
    public string? ThinkingDisplay { get; init; }

    /// <summary>Cost-allocation tags attached to the Bedrock request.</summary>
    public IReadOnlyDictionary<string, string>? RequestMetadata { get; init; }

    /// <summary>Bearer token for Bedrock API-key authentication.</summary>
    public string? BearerToken { get; init; }
}

/// <summary>Base event translated from a Bedrock Converse event stream.</summary>
public abstract record BedrockConverseEvent;

/// <summary>Bedrock assistant message start event.</summary>
public sealed record BedrockMessageStartEvent(string Role) : BedrockConverseEvent;

/// <summary>Bedrock content-block start event.</summary>
public sealed record BedrockContentBlockStartEvent(int ContentBlockIndex) : BedrockConverseEvent
{
    /// <summary>Tool-use identifier, when the block is a tool call.</summary>
    public string? ToolUseId { get; init; }

    /// <summary>Tool name, when the block is a tool call.</summary>
    public string? ToolName { get; init; }
}

/// <summary>Bedrock content-block delta event.</summary>
public sealed record BedrockContentBlockDeltaEvent(int ContentBlockIndex) : BedrockConverseEvent
{
    /// <summary>Text delta.</summary>
    public string? Text { get; init; }

    /// <summary>Incremental JSON tool-input delta.</summary>
    public string? ToolInput { get; init; }

    /// <summary>Reasoning text delta.</summary>
    public string? ReasoningText { get; init; }

    /// <summary>Anthropic reasoning signature delta.</summary>
    public string? Signature { get; init; }

    /// <summary>Opaque encrypted reasoning bytes.</summary>
    public byte[]? RedactedContent { get; init; }
}

/// <summary>Bedrock content-block stop event.</summary>
public sealed record BedrockContentBlockStopEvent(int ContentBlockIndex) : BedrockConverseEvent;

/// <summary>Bedrock assistant message stop event.</summary>
public sealed record BedrockMessageStopEvent(string? StopReason) : BedrockConverseEvent;

/// <summary>Bedrock usage metadata event.</summary>
public sealed record BedrockMetadataEvent(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheWriteInputTokens,
    int TotalTokens) : BedrockConverseEvent;

/// <summary>Options presented to an injected Bedrock transport.</summary>
public sealed record BedrockTransportOptions
{
    /// <summary>Resolved AWS region.</summary>
    public string? Region { get; init; }

    /// <summary>Resolved profile name.</summary>
    public string? Profile { get; init; }

    /// <summary>Whether the endpoint is explicitly pinned.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Resolved access key id, when static credentials are selected.</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>Resolved secret access key, when static credentials are selected.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>Resolved session token, when static credentials are selected.</summary>
    public string? SessionToken { get; init; }

    /// <summary>Resolved bearer token, when Bedrock API-key authentication is selected.</summary>
    public string? BearerToken { get; init; }

    /// <summary>Whether SigV4 credentials are intentionally bypassed.</summary>
    public bool SkipAuth { get; init; }

    /// <summary>Whether the caller explicitly requested the HTTP/1.1 transport.</summary>
    public bool ForceHttp1 { get; init; }

    /// <summary>Caller-supplied headers that are safe to add to the request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>Response returned by an injected Bedrock Converse transport.</summary>
public sealed record BedrockConverseResponse
{
    /// <summary>HTTP status received from Bedrock.</summary>
    public int Status { get; init; }

    /// <summary>HTTP response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bedrock request identifier.</summary>
    public string? RequestId { get; init; }

    /// <summary>Translated event stream.</summary>
    public required IAsyncEnumerable<BedrockConverseEvent> Events { get; init; }
}

/// <summary>Transport exception carrying Bedrock response metadata.</summary>
public sealed class BedrockConverseTransportException : Exception
{
    /// <summary>Creates an exception with optional Bedrock metadata.</summary>
    public BedrockConverseTransportException(
        string message,
        int? status = null,
        string? errorCode = null,
        string? requestId = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        ErrorCode = errorCode;
        RequestId = requestId;
        Headers = headers;
        ResponseBody = responseBody;
    }

    /// <summary>HTTP status code, when available.</summary>
    public int? Status { get; }

    /// <summary>Provider error code, when available.</summary>
    public string? ErrorCode { get; }

    /// <summary>Provider request id, when available.</summary>
    public string? RequestId { get; }

    /// <summary>Response headers, when available.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>Raw response body, when available.</summary>
    public string? ResponseBody { get; }
}

/// <summary>
/// Injection seam for deterministic Bedrock tests and applications that own the AWS client.
/// The default implementation uses <see cref="AmazonBedrockRuntimeClient"/> and AWS SDK
/// credential resolution/SigV4 signing.
/// </summary>
public interface IBedrockConverseTransport
{
    /// <summary>Sends one Converse streaming request.</summary>
    Task<BedrockConverseResponse> SendAsync(
        JsonObject payload,
        BedrockTransportOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Pi provider implementation for Amazon Bedrock Converse streaming.</summary>
public sealed class BedrockConverseProvider : ProviderStreams
{
    private const string _emptyTextPlaceholder = "<empty>";
    private const string _redactedThinkingPlaceholder = "[Reasoning redacted]";
    private const string _bedrockDataRetentionDocsUrl = "https://docs.aws.amazon.com/bedrock/latest/userguide/data-retention.html";
    private const int _maxDiagnosticValueChars = 200;
    private readonly IBedrockConverseTransport _transport;

    /// <summary>Creates a provider backed by AWS SDK Bedrock transport, or an injected transport.</summary>
    public BedrockConverseProvider(IBedrockConverseTransport? transport = null)
    {
        _transport = transport ?? new AwsBedrockConverseTransport();
    }

    /// <summary>Builds the AWS SDK-shaped Converse request payload.</summary>
    public static JsonObject BuildPayload(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);

        var bedrockOptions = options as BedrockOptions;
        var environment = options?.Environment;
        var cacheRetention = ResolveCacheRetention(options?.CacheRetention, environment);
        var payload = new JsonObject
        {
            ["modelId"] = model.Id,
            ["messages"] = ConvertMessages(context, model, cacheRetention, environment),
            ["inferenceConfig"] = new JsonObject(),
        };

        var maxTokens = options?.MaxTokens ?? (IsAnthropicClaudeModel(model) ? model.MaxTokens : null);
        if (maxTokens is not null)
        {
            ((JsonObject)payload["inferenceConfig"]!)["maxTokens"] = maxTokens.Value;
        }

        if (options?.Temperature is not null)
        {
            ((JsonObject)payload["inferenceConfig"]!)["temperature"] = options.Temperature.Value;
        }

        var system = BuildSystemPrompt(context.SystemPrompt, model, cacheRetention, environment);
        if (system is not null)
        {
            payload["system"] = system;
        }

        var supportsStrictMode = GetBool(model.Compatibility, "supportsStrictMode", false);
        var toolConfig = ConvertToolConfig(context.Tools, bedrockOptions?.ToolChoice, supportsStrictMode);
        if (toolConfig is not null)
        {
            payload["toolConfig"] = toolConfig;
        }

        var additional = BuildAdditionalModelRequestFields(model, bedrockOptions);
        if (additional is not null)
        {
            payload["additionalModelRequestFields"] = additional;
        }

        if (bedrockOptions?.RequestMetadata is not null)
        {
            var metadata = new JsonObject();
            foreach (var pair in bedrockOptions.RequestMetadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            payload["requestMetadata"] = metadata;
        }

        return payload;
    }

    /// <summary>Starts a Bedrock Converse stream.</summary>
    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var stream = new AssistantMessageEventStream();
        _ = RunAsync(stream, model, context, options);
        return stream;
    }

    /// <summary>Starts a provider-neutral Bedrock stream.</summary>
    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);

        var baseOptions = CopyCommonOptions(options);
        var reasoning = string.IsNullOrEmpty(options?.Reasoning)
            ? null
            : ModelUtilities.ClampThinkingLevel(model, options!.Reasoning!);
        if (string.IsNullOrEmpty(reasoning) || reasoning == ThinkingLevels.Off)
        {
            return Stream(model, context, CopyWith(baseOptions, baseOptions.MaxTokens, null, null));
        }

        if (IsAnthropicClaudeModel(model) && !SupportsAdaptiveThinking(model))
        {
            var adjusted = AdjustMaxTokensForThinking(
                options?.MaxTokens,
                model,
                reasoning,
                options?.ThinkingBudgets,
                context);
            baseOptions = CopyWith(baseOptions, adjusted.MaxTokens, reasoning, adjusted.Budgets);
        }
        else
        {
            baseOptions = CopyWith(baseOptions, baseOptions.MaxTokens, reasoning, options?.ThinkingBudgets);
        }

        return Stream(model, context, baseOptions);
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
                throw new InvalidOperationException("Custom fetch is not supported by the Bedrock Converse adapter");
            }

            var payload = BuildPayload(model, context, options);
            var nextPayload = options?.OnPayload is null
                ? payload
                : await options.OnPayload(payload, model).ConfigureAwait(false) as JsonObject ?? payload;
            var transportOptions = BuildTransportOptions(model, options);
            var response = await ProviderRetryUtilities.RetryProviderRequest(
                    () => _transport.SendAsync(nextPayload, transportOptions, options?.Signal ?? default),
                    options?.MaxRetries ?? 0,
                    options?.MaxRetryDelayMs,
                    options?.Signal ?? default)
                .ConfigureAwait(false);

            if (options?.OnResponse is not null)
            {
                await options.OnResponse(
                        new ProviderResponse(response.Status, response.Headers),
                        model)
                    .ConfigureAwait(false);
            }

            stream.Push(new StreamStartEvent(output));
            var state = new StreamState(output);
            await foreach (var item in response.Events.WithCancellation(options?.Signal ?? default))
            {
                switch (item)
                {
                    case BedrockMessageStartEvent start:
                        if (!string.Equals(start.Role, "assistant", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "Unexpected assistant message start but got user message start instead");
                        }

                        break;
                    case BedrockContentBlockStartEvent blockStart:
                        HandleContentBlockStart(blockStart, state, stream);
                        break;
                    case BedrockContentBlockDeltaEvent blockDelta:
                        HandleContentBlockDelta(blockDelta, state, stream);
                        break;
                    case BedrockContentBlockStopEvent blockStop:
                        HandleContentBlockStop(blockStop, state, stream);
                        break;
                    case BedrockMessageStopEvent messageStop:
                        state.HasStopReason = true;
                        var mapped = MapStopReason(messageStop.StopReason);
                        state.Output = state.Output with
                        {
                            RawStopReason = messageStop.StopReason,
                            StopReason = mapped.StopReason,
                            ErrorMessage = mapped.ErrorMessage,
                        };
                        break;
                    case BedrockMetadataEvent metadata:
                        var usage = new Usage
                        {
                            Input = Math.Max(0, metadata.InputTokens),
                            Output = Math.Max(0, metadata.OutputTokens),
                            CacheRead = Math.Max(0, metadata.CacheReadInputTokens),
                            CacheWrite = Math.Max(0, metadata.CacheWriteInputTokens),
                            TotalTokens = metadata.TotalTokens > 0
                                ? metadata.TotalTokens
                                : Math.Max(0, metadata.InputTokens) + Math.Max(0, metadata.OutputTokens),
                        };
                        ModelUtilities.CalculateCost(model, usage);
                        state.Output = state.Output with { Usage = usage };
                        break;
                }
            }

            FinalizeBlocks(state, stream);
            output = state.Output;
            if (options?.Signal.IsCancellationRequested == true)
            {
                throw new OperationCanceledException("Request was aborted", options.Signal);
            }

            if (!state.HasStopReason)
            {
                throw new InvalidOperationException("Bedrock stream ended without a stop reason");
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
        catch (Exception error)
        {
            output = output with
            {
                StopReason = StopReasons.Error,
                ErrorMessage = FormatBedrockError(error),
            };
            AppendFailureDiagnostic(output, error);
            stream.Push(new StreamErrorEvent(output.StopReason, output));
            stream.End(output);
        }
    }

    private static BedrockTransportOptions BuildTransportOptions(Model model, StreamOptions? options)
    {
        var bedrockOptions = options as BedrockOptions;
        var environment = options?.Environment;
        var explicitProfile = FirstNonEmpty(bedrockOptions?.Profile, GetScopedEnv("AWS_PROFILE", environment));
        var ambientProfile = ProviderEnvironmentUtilities.GetProviderEnvValue("AWS_PROFILE");
        var profile = explicitProfile ?? ambientProfile;
        var configuredRegion = FirstNonEmpty(
            bedrockOptions?.Region,
            GetScopedEnv("AWS_REGION", environment),
            GetScopedEnv("AWS_DEFAULT_REGION", environment));
        var endpointRegion = GetStandardBedrockEndpointRegion(model.BaseUrl);
        var useExplicitEndpoint = endpointRegion is null ||
                                  configuredRegion is null && ambientProfile is null;
        var arnRegion = GetArnRegion(model.Id);
        var region = arnRegion ?? configuredRegion ?? (useExplicitEndpoint ? endpointRegion : null);
        if (region is null && ambientProfile is null)
        {
            region = "us-east-1";
        }

        var skipAuth = string.Equals(
            ProviderEnvironmentUtilities.GetProviderEnvValue("AWS_BEDROCK_SKIP_AUTH", environment),
            "1",
            StringComparison.Ordinal);
        var bearerToken = FirstNonEmpty(
            bedrockOptions?.BearerToken,
            options?.ApiKey,
            GetScopedEnv("AWS_BEARER_TOKEN_BEDROCK", environment));
        if (skipAuth)
        {
            bearerToken = null;
        }

        var headers = (HeaderUtilities.ProviderHeadersToRecord(options?.Headers) ??
                       new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .Where(pair => !IsReservedBedrockHeader(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new BedrockTransportOptions
        {
            Region = region,
            Profile = profile,
            Endpoint = useExplicitEndpoint ? model.BaseUrl : null,
            AccessKeyId = explicitProfile is null
                ? GetScopedEnv("AWS_ACCESS_KEY_ID", environment)
                : null,
            SecretAccessKey = explicitProfile is null
                ? GetScopedEnv("AWS_SECRET_ACCESS_KEY", environment)
                : null,
            SessionToken = explicitProfile is null
                ? GetScopedEnv("AWS_SESSION_TOKEN", environment)
                : null,
            BearerToken = bearerToken,
            SkipAuth = skipAuth,
            ForceHttp1 = string.Equals(
                ProviderEnvironmentUtilities.GetProviderEnvValue("AWS_BEDROCK_FORCE_HTTP1", environment),
                "1",
                StringComparison.Ordinal),
            Headers = headers,
            TimeoutMs = options?.TimeoutMs,
        };
    }

    private static JsonArray ConvertMessages(
        Context context,
        Model model,
        string cacheRetention,
        IReadOnlyDictionary<string, string>? environment)
    {
        var transformed = TransformMessages(context.Messages ?? [], model);
        var result = new JsonArray();
        for (var index = 0; index < transformed.Count; index++)
        {
            switch (transformed[index])
            {
                case UserMessage user:
                    result.Add((JsonNode?)ConvertUserMessage(user, model));
                    break;
                case AssistantMessage assistant:
                    var assistantMessage = ConvertAssistantMessage(assistant, model);
                    if (assistantMessage is not null)
                    {
                        result.Add((JsonNode?)assistantMessage);
                    }

                    break;
                case ToolResultMessage toolResult:
                    var content = new JsonArray
                    {
                        (JsonNode?)new JsonObject { ["toolResult"] = ConvertToolResult(toolResult, model) },
                    };
                    var next = index + 1;
                    while (next < transformed.Count && transformed[next] is ToolResultMessage sibling)
                    {
                        content.Add((JsonNode?)new JsonObject { ["toolResult"] = ConvertToolResult(sibling, model) });
                        next++;
                    }

                    index = next - 1;
                    result.Add((JsonNode?)new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });
                    break;
            }
        }

        if (cacheRetention != CacheRetentions.None &&
            SupportsPromptCaching(model, environment) &&
            result.LastOrDefault() is JsonObject lastUser &&
            lastUser["role"]?.GetValue<string>() == "user" &&
            lastUser["content"] is JsonArray lastContent)
        {
            lastContent.Add((JsonNode?)CreateCachePoint(cacheRetention));
        }

        return result;
    }

    private static JsonObject ConvertUserMessage(UserMessage message, Model model)
    {
        var content = new JsonArray();
        if (message.Content is string text)
        {
            AddRequiredText(content, text);
        }
        else if (message.Content is IEnumerable<ContentBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case TextContent textBlock:
                        AddNonBlankText(content, textBlock.Text);
                        break;
                    case ImageContent image when SupportsImages(model):
                        content.Add((JsonNode?)CreateImageBlock(image));
                        break;
                    case ImageContent:
                        AddNonBlankText(content, "(image omitted: model does not support images)");
                        break;
                }
            }

            if (content.Count == 0)
            {
                AddEmptyText(content);
            }
        }
        else
        {
            AddEmptyText(content);
        }

        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = content,
        };
    }

    private static JsonObject? ConvertAssistantMessage(AssistantMessage message, Model model)
    {
        if (message.Content.Count == 0)
        {
            return null;
        }

        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    AddNonBlankText(content, text.Text);
                    break;
                case ToolCall toolCall:
                    content.Add((JsonNode?)new JsonObject
                    {
                        ["toolUse"] = new JsonObject
                        {
                            ["toolUseId"] = toolCall.Id,
                            ["name"] = toolCall.Name,
                            ["input"] = SanitizeBedrockDocument(toolCall.Arguments),
                        },
                    });
                    break;
                case ThinkingContent thinking:
                    if (thinking.Redacted == true)
                    {
                        var bytes = DecodeBase64(thinking.ThinkingSignature);
                        if (bytes is not null && bytes.Length > 0)
                        {
                            content.Add((JsonNode?)new JsonObject
                            {
                                ["reasoningContent"] = new JsonObject
                                {
                                    ["redactedContent"] = Convert.ToBase64String(bytes),
                                },
                            });
                        }

                        break;
                    }

                    var sanitizedThinking = UnicodeUtilities.SanitizeSurrogates(thinking.Thinking);
                    if (sanitizedThinking.Trim().Length == 0)
                    {
                        break;
                    }

                    var reasoningText = new JsonObject { ["text"] = sanitizedThinking };
                    if (SupportsThinkingSignature(model) && !string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        reasoningText["signature"] = thinking.ThinkingSignature;
                    }

                    content.Add((JsonNode?)new JsonObject
                    {
                        ["reasoningContent"] = new JsonObject { ["reasoningText"] = reasoningText },
                    });
                    break;
            }
        }

        return content.Count == 0
            ? null
            : new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content,
            };
    }

    private static JsonObject ConvertToolResult(ToolResultMessage message, Model model)
    {
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    AddNonBlankText(content, text.Text);
                    break;
                case ImageContent image when SupportsImages(model):
                    content.Add((JsonNode?)CreateImageBlock(image));
                    break;
                case ImageContent:
                    AddNonBlankText(content, "(tool image omitted: model does not support images)");
                    break;
            }
        }

        if (content.Count == 0)
        {
            AddEmptyText(content);
        }

        return new JsonObject
        {
            ["toolUseId"] = message.ToolCallId,
            ["content"] = content,
            ["status"] = message.IsError ? "error" : "success",
        };
    }

    private static JsonArray? BuildSystemPrompt(
        string? systemPrompt,
        Model model,
        string cacheRetention,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (string.IsNullOrEmpty(systemPrompt))
        {
            return null;
        }

        var text = new JsonObject { ["text"] = UnicodeUtilities.SanitizeSurrogates(systemPrompt) };
        if (cacheRetention != CacheRetentions.None && SupportsPromptCaching(model, environment))
        {
            return new JsonArray { (JsonNode?)text, (JsonNode?)CreateCachePoint(cacheRetention) };
        }

        return new JsonArray { (JsonNode?)text };
    }

    private static JsonObject CreateCachePoint(string cacheRetention)
    {
        var cachePoint = new JsonObject { ["type"] = "default" };
        if (cacheRetention == CacheRetentions.Long)
        {
            cachePoint["ttl"] = "1h";
        }

        return new JsonObject { ["cachePoint"] = cachePoint };
    }

    private static JsonObject? ConvertToolConfig(
        IReadOnlyList<Tool> tools,
        JsonNode? toolChoice,
        bool supportsStrictMode)
    {
        if (tools.Count == 0 || IsString(toolChoice, "none"))
        {
            return null;
        }

        var converted = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var schema = strict ? MakeStrictJsonSchema(tool.Parameters) : tool.Parameters.DeepClone();
            var spec = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = new JsonObject { ["json"] = schema },
            };
            if (strict)
            {
                spec["strict"] = true;
            }

            converted.Add((JsonNode?)new JsonObject { ["toolSpec"] = spec });
        }

        var result = new JsonObject { ["tools"] = converted };
        if (IsString(toolChoice, "auto"))
        {
            result["toolChoice"] = new JsonObject { ["auto"] = new JsonObject() };
        }
        else if (IsString(toolChoice, "any"))
        {
            result["toolChoice"] = new JsonObject { ["any"] = new JsonObject() };
        }
        else if (toolChoice is JsonObject named &&
                 StringValue(named["type"]) == "tool" &&
                 StringValue(named["name"]) is { } name)
        {
            result["toolChoice"] = new JsonObject
            {
                ["tool"] = new JsonObject { ["name"] = name },
            };
        }

        return result;
    }

    private static JsonObject? BuildAdditionalModelRequestFields(Model model, BedrockOptions? options)
    {
        if (options?.Reasoning is null || !model.Reasoning || !IsAnthropicClaudeModel(model))
        {
            return null;
        }

        var display = IsGovCloudBedrockTarget(model, options) ? null : options.ThinkingDisplay ?? "summarized";
        JsonObject result;
        if (SupportsAdaptiveThinking(model))
        {
            var thinking = new JsonObject { ["type"] = "adaptive" };
            if (display is not null)
            {
                thinking["display"] = display;
            }

            result = new JsonObject
            {
                ["thinking"] = thinking,
                ["output_config"] = new JsonObject
                {
                    ["effort"] = MapThinkingLevelToEffort(model, options.Reasoning),
                },
            };
        }
        else
        {
            var level = options.Reasoning is ThinkingLevels.XHigh or ThinkingLevels.Max
                ? ThinkingLevels.High
                : options.Reasoning;
            var budget = GetThinkingBudget(options.ThinkingBudgets, level, options.Reasoning);
            var thinking = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget,
            };
            if (display is not null)
            {
                thinking["display"] = display;
            }

            result = new JsonObject
            {
                ["thinking"] = thinking,
            };
        }

        if (!SupportsAdaptiveThinking(model) && options.InterleavedThinking != false)
        {
            result["anthropic_beta"] = new JsonArray("interleaved-thinking-2025-05-14");
        }

        return result;
    }

    private static void HandleContentBlockStart(
        BedrockContentBlockStartEvent item,
        StreamState state,
        AssistantMessageEventStream stream)
    {
        if (item.ToolUseId is null && item.ToolName is null)
        {
            return;
        }

        var content = state.Output.Content.ToList();
        var tool = new ToolCall(item.ToolUseId ?? string.Empty, item.ToolName ?? string.Empty, new JsonObject());
        content.Add(tool);
        state.Output = state.Output with { Content = content };
        state.Blocks[item.ContentBlockIndex] = new ActiveBlock
        {
            ContentIndex = content.Count - 1,
            Kind = BlockKind.Tool,
            ToolId = tool.Id,
            ToolName = tool.Name,
        };
        stream.Push(new ToolCallStartEvent(content.Count - 1, state.Output));
    }

    private static void HandleContentBlockDelta(
        BedrockContentBlockDeltaEvent item,
        StreamState state,
        AssistantMessageEventStream stream)
    {
        if (item.Text is not null)
        {
            var active = GetOrCreateTextBlock(item.ContentBlockIndex, state, stream);
            var current = (TextContent)state.Output.Content[active.ContentIndex];
            var updated = state.Output.Content.ToList();
            updated[active.ContentIndex] = current with
            {
                Text = current.Text + UnicodeUtilities.SanitizeSurrogates(item.Text),
            };
            state.Output = state.Output with { Content = updated };
            stream.Push(new TextDeltaEvent(active.ContentIndex, UnicodeUtilities.SanitizeSurrogates(item.Text), state.Output));
        }

        if (item.ToolInput is not null && state.Blocks.TryGetValue(item.ContentBlockIndex, out var toolBlock) &&
            toolBlock.Kind == BlockKind.Tool)
        {
            toolBlock.PartialJson += item.ToolInput;
            var updated = state.Output.Content.ToList();
            updated[toolBlock.ContentIndex] = new ToolCall(
                toolBlock.ToolId!,
                toolBlock.ToolName!,
                JsonParseUtilities.ParseStreamingJson(toolBlock.PartialJson) as JsonObject ?? new JsonObject());
            state.Output = state.Output with { Content = updated };
            stream.Push(new ToolCallDeltaEvent(toolBlock.ContentIndex, item.ToolInput, state.Output));
        }

        if (item.ReasoningText is not null || item.Signature is not null || item.RedactedContent is not null)
        {
            var active = GetOrCreateThinkingBlock(item.ContentBlockIndex, state, stream);
            var current = (ThinkingContent)state.Output.Content[active.ContentIndex];
            var nextThinking = current.Thinking;
            var nextSignature = current.ThinkingSignature;
            var redacted = current.Redacted;
            if (item.ReasoningText is { } reasoningText)
            {
                nextThinking += reasoningText;
                stream.Push(new ThinkingDeltaEvent(
                    active.ContentIndex,
                    reasoningText,
                    state.Output with
                    {
                        Content = ReplaceContent(state.Output.Content, active.ContentIndex, current with { Thinking = nextThinking }),
                    }));
            }

            if (item.Signature is { Length: > 0 } signature && redacted != true)
            {
                nextSignature += signature;
            }

            if (item.RedactedContent is { Length: > 0 } encrypted)
            {
                if (redacted != true)
                {
                    redacted = true;
                    nextSignature = string.Empty;
                    nextThinking += _redactedThinkingPlaceholder;
                    stream.Push(new ThinkingDeltaEvent(
                        active.ContentIndex,
                        _redactedThinkingPlaceholder,
                        state.Output with
                        {
                            Content = ReplaceContent(
                                state.Output.Content,
                                active.ContentIndex,
                                current with
                                {
                                    Thinking = nextThinking,
                                    ThinkingSignature = nextSignature,
                                    Redacted = redacted,
                                }),
                        }));
                }

                active.RedactedChunks.Add(encrypted);
            }

            state.Output = state.Output with
            {
                Content = ReplaceContent(
                    state.Output.Content,
                    active.ContentIndex,
                    current with
                    {
                        Thinking = nextThinking,
                        ThinkingSignature = nextSignature,
                        Redacted = redacted,
                    }),
            };
        }
    }

    private static void HandleContentBlockStop(
        BedrockContentBlockStopEvent item,
        StreamState state,
        AssistantMessageEventStream stream)
    {
        if (!state.Blocks.TryGetValue(item.ContentBlockIndex, out var active) || active.Finalized)
        {
            return;
        }

        active.Finalized = true;
        switch (state.Output.Content[active.ContentIndex])
        {
            case TextContent text:
                stream.Push(new TextEndEvent(active.ContentIndex, text.Text, state.Output));
                break;
            case ThinkingContent thinking:
                FlushRedactedContent(active, state, thinking);
                var finalThinking = (ThinkingContent)state.Output.Content[active.ContentIndex];
                stream.Push(new ThinkingEndEvent(active.ContentIndex, finalThinking.Thinking, state.Output));
                break;
            case ToolCall toolCall:
                var finalized = toolCall with
                {
                    Arguments = JsonParseUtilities.ParseStreamingJson(active.PartialJson) as JsonObject ?? new JsonObject(),
                };
                state.Output = state.Output with
                {
                    Content = ReplaceContent(state.Output.Content, active.ContentIndex, finalized),
                };
                stream.Push(new ToolCallEndEvent(active.ContentIndex, finalized, state.Output));
                break;
        }
    }

    private static ActiveBlock GetOrCreateTextBlock(
        int streamIndex,
        StreamState state,
        AssistantMessageEventStream stream)
    {
        if (state.Blocks.TryGetValue(streamIndex, out var active))
        {
            return active;
        }

        var content = state.Output.Content.ToList();
        content.Add(new TextContent(string.Empty));
        state.Output = state.Output with { Content = content };
        active = new ActiveBlock { ContentIndex = content.Count - 1, Kind = BlockKind.Text };
        state.Blocks[streamIndex] = active;
        stream.Push(new TextStartEvent(active.ContentIndex, state.Output));
        return active;
    }

    private static ActiveBlock GetOrCreateThinkingBlock(
        int streamIndex,
        StreamState state,
        AssistantMessageEventStream stream)
    {
        if (state.Blocks.TryGetValue(streamIndex, out var active))
        {
            return active;
        }

        var content = state.Output.Content.ToList();
        content.Add(new ThinkingContent(string.Empty, string.Empty));
        state.Output = state.Output with { Content = content };
        active = new ActiveBlock { ContentIndex = content.Count - 1, Kind = BlockKind.Thinking };
        state.Blocks[streamIndex] = active;
        stream.Push(new ThinkingStartEvent(active.ContentIndex, state.Output));
        return active;
    }

    private static void FinalizeBlocks(StreamState state, AssistantMessageEventStream stream)
    {
        foreach (var active in state.Blocks.Values.OrderBy(block => block.ContentIndex))
        {
            if (active.Finalized || active.ContentIndex >= state.Output.Content.Count)
            {
                continue;
            }

            switch (state.Output.Content[active.ContentIndex])
            {
                case TextContent text:
                    stream.Push(new TextEndEvent(active.ContentIndex, text.Text, state.Output));
                    break;
                case ThinkingContent thinking:
                    FlushRedactedContent(active, state, thinking);
                    var finalThinking = (ThinkingContent)state.Output.Content[active.ContentIndex];
                    stream.Push(new ThinkingEndEvent(active.ContentIndex, finalThinking.Thinking, state.Output));
                    break;
                case ToolCall toolCall:
                    var finalized = toolCall with
                    {
                        Arguments = JsonParseUtilities.ParseStreamingJson(active.PartialJson) as JsonObject ?? new JsonObject(),
                    };
                    state.Output = state.Output with
                    {
                        Content = ReplaceContent(state.Output.Content, active.ContentIndex, finalized),
                    };
                    stream.Push(new ToolCallEndEvent(active.ContentIndex, finalized, state.Output));
                    break;
            }

            active.Finalized = true;
        }
    }

    private static void FlushRedactedContent(ActiveBlock active, StreamState state, ThinkingContent thinking)
    {
        if (thinking.Redacted != true || active.RedactedChunks.Count == 0)
        {
            return;
        }

        var bytes = active.RedactedChunks.SelectMany(static chunk => chunk).ToArray();
        state.Output = state.Output with
        {
            Content = ReplaceContent(
                state.Output.Content,
                active.ContentIndex,
                thinking with { ThinkingSignature = Convert.ToBase64String(bytes) }),
        };
    }

    private static List<Message> TransformMessages(IReadOnlyList<Message> messages, Model model)
    {
        var normalized = messages.Select(message => NormalizeImages(message, model)).ToList();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var transformed = new List<Message>(normalized.Count);
        foreach (var message in normalized)
        {
            switch (message)
            {
                case AssistantMessage assistant:
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
                        switch (block)
                        {
                            case ThinkingContent thinking when thinking.Redacted == true:
                                if (sameModel) content.Add(thinking);
                                break;
                            case ThinkingContent thinking:
                                if (string.IsNullOrWhiteSpace(thinking.Thinking) && string.IsNullOrEmpty(thinking.ThinkingSignature))
                                {
                                    continue;
                                }

                                content.Add(sameModel
                                    ? thinking
                                    : new TextContent(thinking.Thinking));
                                break;
                            case TextContent text:
                                content.Add(new TextContent(UnicodeUtilities.SanitizeSurrogates(text.Text), text.TextSignature));
                                break;
                            case ToolCall toolCall:
                                var normalizedId = sameModel ? toolCall.Id : NormalizeToolCallId(toolCall.Id);
                                if (!sameModel && normalizedId != toolCall.Id)
                                {
                                    idMap[toolCall.Id] = normalizedId;
                                }

                                content.Add(toolCall with { Id = normalizedId });
                                break;
                            default:
                                content.Add(block);
                                break;
                        }
                    }

                    transformed.Add(assistant with { Content = content });
                    break;
                case ToolResultMessage toolResult:
                    transformed.Add(idMap.TryGetValue(toolResult.ToolCallId, out var mapped)
                        ? toolResult with { ToolCallId = mapped }
                        : toolResult);
                    break;
                default:
                    transformed.Add(message);
                    break;
            }
        }

        var result = new List<Message>(transformed.Count);
        var pending = new List<ToolCall>();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        void FlushPending()
        {
            foreach (var call in pending)
            {
                if (!existing.Contains(call.Id))
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
            existing.Clear();
        }

        foreach (var message in transformed)
        {
            switch (message)
            {
                case AssistantMessage assistant:
                    FlushPending();
                    var calls = assistant.Content.OfType<ToolCall>().ToList();
                    if (calls.Count > 0)
                    {
                        pending.AddRange(calls);
                    }

                    result.Add(assistant);
                    break;
                case ToolResultMessage toolResult:
                    existing.Add(toolResult.ToolCallId);
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

    private static Message NormalizeImages(Message message, Model model)
    {
        if (SupportsImages(model))
        {
            return message;
        }

        if (message is UserMessage { Content: IEnumerable<ContentBlock> blocks } user)
        {
            return user with { Content = ReplaceImages(blocks, "(image omitted: model does not support images)") };
        }

        if (message is ToolResultMessage toolResult)
        {
            return toolResult with
            {
                Content = ReplaceImages(toolResult.Content, "(tool image omitted: model does not support images)"),
            };
        }

        return message;
    }

    private static List<ContentBlock> ReplaceImages(
        IEnumerable<ContentBlock> blocks,
        string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;
        foreach (var block in blocks)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder)
                {
                    result.Add(new TextContent(placeholder));
                }

                previousWasPlaceholder = true;
                continue;
            }

            result.Add(block);
            previousWasPlaceholder = block is TextContent text && text.Text == placeholder;
        }

        return result;
    }

    private static JsonObject SanitizeBedrockDocument(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            var result = new JsonObject();
            foreach (var pair in objectNode)
            {
                if (pair.Key.Length > 0)
                {
                    result[pair.Key] = pair.Value is null ? null : SanitizeBedrockNode(pair.Value);
                }
            }

            return result;
        }

        return new JsonObject();
    }

    private static JsonNode SanitizeBedrockNode(JsonNode node) => node switch
    {
        JsonObject objectNode => SanitizeBedrockDocument(objectNode),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : SanitizeBedrockNode(item)).ToArray()),
        _ => node.DeepClone(),
    };

    private static JsonObject CreateImageBlock(ImageContent image)
    {
        var format = image.MimeType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "jpeg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => throw new InvalidOperationException($"Unknown image type: {image.MimeType}"),
        };
        _ = DecodeBase64(image.Data) ?? throw new InvalidOperationException("Invalid image data");
        return new JsonObject
        {
            ["image"] = new JsonObject
            {
                ["format"] = format,
                ["source"] = new JsonObject { ["bytes"] = image.Data },
            },
        };
    }

    private static void AddRequiredText(JsonArray content, string text)
    {
        var sanitized = UnicodeUtilities.SanitizeSurrogates(text);
        content.Add((JsonNode?)new JsonObject
        {
            ["text"] = string.IsNullOrWhiteSpace(sanitized) ? _emptyTextPlaceholder : sanitized,
        });
    }

    private static void AddNonBlankText(JsonArray content, string text)
    {
        var sanitized = UnicodeUtilities.SanitizeSurrogates(text);
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            content.Add((JsonNode?)new JsonObject { ["text"] = sanitized });
        }
    }

    private static void AddEmptyText(JsonArray content) =>
        content.Add((JsonNode?)new JsonObject { ["text"] = _emptyTextPlaceholder });

    private static List<ContentBlock> ReplaceContent(
        IReadOnlyList<ContentBlock> content,
        int index,
        ContentBlock replacement)
    {
        var result = content.ToList();
        result[index] = replacement;
        return result;
    }

    private static string NormalizeToolCallId(string id)
    {
        var builder = new StringBuilder(Math.Min(64, id.Length));
        foreach (var character in id)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-')
            {
                builder.Append(character);
            }

            if (builder.Length == 64)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string ResolveCacheRetention(string? requested, IReadOnlyDictionary<string, string>? environment) =>
        requested ?? (string.Equals(
            ProviderEnvironmentUtilities.GetProviderEnvValue("PI_CACHE_RETENTION", environment),
            CacheRetentions.Long,
            StringComparison.Ordinal)
            ? CacheRetentions.Long
            : CacheRetentions.Short);

    private static bool SupportsImages(Model model) =>
        model.Input.Contains("image", StringComparer.OrdinalIgnoreCase);

    private static bool IsAnthropicClaudeModel(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        var name = model.Name.ToLowerInvariant();
        return id.Contains("anthropic.claude", StringComparison.Ordinal) ||
               id.Contains("anthropic/claude", StringComparison.Ordinal) ||
               name.Contains("anthropic.claude", StringComparison.Ordinal) ||
               name.Contains("anthropic/claude", StringComparison.Ordinal) ||
               name.Contains("claude", StringComparison.Ordinal);
    }

    private static bool SupportsThinkingSignature(Model model) => IsAnthropicClaudeModel(model);

    private static bool SupportsPromptCaching(Model model, IReadOnlyDictionary<string, string>? environment)
    {
        var candidates = GetModelMatchCandidates(model);
        if (!candidates.Any(candidate => candidate.Contains("claude", StringComparison.Ordinal)))
        {
            return string.Equals(
                ProviderEnvironmentUtilities.GetProviderEnvValue("AWS_BEDROCK_FORCE_CACHE", environment),
                "1",
                StringComparison.Ordinal);
        }

        return candidates.Any(candidate =>
            candidate.Contains("fable-5", StringComparison.Ordinal) ||
            candidate.Contains("opus-5", StringComparison.Ordinal) ||
            candidate.Contains("sonnet-5", StringComparison.Ordinal) ||
            candidate.Contains("-4-", StringComparison.Ordinal) ||
            candidate.Contains("claude-3-7-sonnet", StringComparison.Ordinal) ||
            candidate.Contains("claude-3-5-haiku", StringComparison.Ordinal));
    }

    private static bool SupportsAdaptiveThinking(Model model) =>
        GetModelMatchCandidates(model).Any(candidate =>
            candidate.Contains("opus-4-6", StringComparison.Ordinal) ||
            candidate.Contains("opus-4-7", StringComparison.Ordinal) ||
            candidate.Contains("opus-4-8", StringComparison.Ordinal) ||
            candidate.Contains("opus-5", StringComparison.Ordinal) ||
            candidate.Contains("sonnet-4-6", StringComparison.Ordinal) ||
            candidate.Contains("sonnet-5", StringComparison.Ordinal) ||
            candidate.Contains("fable-5", StringComparison.Ordinal));

    private static string MapThinkingLevelToEffort(Model model, string level)
    {
        if (level == ThinkingLevels.XHigh && SupportsNativeXhighEffort(model))
        {
            return ThinkingLevels.XHigh;
        }

        if (model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(level, out var mapped) &&
            !string.IsNullOrEmpty(mapped))
        {
            return mapped!;
        }

        return level switch
        {
            ThinkingLevels.Minimal or ThinkingLevels.Low => ThinkingLevels.Low,
            ThinkingLevels.Medium => ThinkingLevels.Medium,
            ThinkingLevels.High => ThinkingLevels.High,
            _ => ThinkingLevels.High,
        };
    }

    private static bool SupportsNativeXhighEffort(Model model) =>
        GetModelMatchCandidates(model).Any(candidate =>
            candidate.Contains("opus-4-7", StringComparison.Ordinal) ||
            candidate.Contains("opus-4-8", StringComparison.Ordinal) ||
            candidate.Contains("opus-5", StringComparison.Ordinal) ||
            candidate.Contains("sonnet-5", StringComparison.Ordinal) ||
            candidate.Contains("fable-5", StringComparison.Ordinal));

    private static int GetThinkingBudget(ThinkingBudgets? budgets, string level, string requestedLevel)
    {
        var custom = level switch
        {
            ThinkingLevels.Minimal => budgets?.Minimal,
            ThinkingLevels.Low => budgets?.Low,
            ThinkingLevels.Medium => budgets?.Medium,
            _ => budgets?.High,
        };
        if (custom is not null)
        {
            return custom.Value;
        }

        return requestedLevel switch
        {
            ThinkingLevels.Minimal => 1024,
            ThinkingLevels.Low => 2048,
            ThinkingLevels.Medium => 8192,
            _ => 16384,
        };
    }

    private static bool IsGovCloudBedrockTarget(Model model, BedrockOptions options)
    {
        var region = FirstNonEmpty(
            options.Region,
            GetScopedEnv("AWS_REGION", options.Environment),
            GetScopedEnv("AWS_DEFAULT_REGION", options.Environment));
        return region?.StartsWith("us-gov-", StringComparison.OrdinalIgnoreCase) == true ||
               model.Id.StartsWith("us-gov.", StringComparison.OrdinalIgnoreCase) ||
               model.Id.StartsWith("arn:aws-us-gov:", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetModelMatchCandidates(Model model)
    {
        var values = new[] { model.Id, model.Name };
        return values.SelectMany(value =>
                new[]
                {
                    value.ToLowerInvariant(),
                    value.ToLowerInvariant().Replace(' ', '-').Replace('_', '-').Replace('.', '-').Replace(':', '-'),
                })
            .ToArray();
    }

    private static (string StopReason, string? ErrorMessage) MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => (StopReasons.Stop, null),
        "max_tokens" or "model_context_window_exceeded" => (StopReasons.Length, null),
        "tool_use" => (StopReasons.ToolUse, null),
        _ => string.IsNullOrEmpty(reason)
            ? (StopReasons.Error, null)
            : (StopReasons.Error, $"Provider stopped with: {reason}"),
    };

    private static string FormatBedrockError(Exception error)
    {
        var status = GetErrorStatus(error);
        var body = error is BedrockConverseTransportException transport ? transport.ResponseBody : null;
        var core = status is not null && body is not null && !error.Message.Contains(body, StringComparison.Ordinal)
            ? $"{status}: {body}"
            : error.Message;
        var prefix = GetBedrockErrorPrefix(error);
        var retentionHint = core.Contains("data retention mode", StringComparison.OrdinalIgnoreCase)
            ? $" See {_bedrockDataRetentionDocsUrl} for supported data retention modes."
            : string.Empty;
        return prefix is null ? core + retentionHint : $"{prefix}: {core}{retentionHint}";
    }

    private static string? GetBedrockErrorPrefix(Exception error)
    {
        if (error is not BedrockConverseTransportException && error is not AmazonServiceException)
        {
            return null;
        }

        var code = GetErrorCode(error);
        return code switch
        {
            "InternalServerException" => "Internal server error",
            "ModelStreamErrorException" or "ModelStreamError" => "Model stream error",
            "ValidationException" => "Validation error",
            "ThrottlingException" => "Throttling error",
            "ServiceUnavailableException" => "Service unavailable",
            _ => code is null || code == "Unknown" ? null : code,
        };
    }

    private static void AppendFailureDiagnostic(AssistantMessage output, Exception error)
    {
        if (output.StopReason == StopReasons.Aborted)
        {
            return;
        }

        var details = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var status = GetErrorStatus(error);
        if (status is not null)
        {
            details["status"] = status.Value;
        }

        var code = GetErrorCode(error);
        if (code is { Length: > 0 } && code.Length <= _maxDiagnosticValueChars && code != "Unknown")
        {
            details["errorCode"] = code;
        }

        var requestId = GetRequestId(error);
        if (requestId is { Length: > 0 } && requestId.Length <= _maxDiagnosticValueChars)
        {
            details["requestId"] = requestId;
        }

        if (details.Count > 0)
        {
            DiagnosticUtilities.AppendAssistantMessageDiagnostic(
                output,
                new AssistantMessageDiagnostic
                {
                    Type = "bedrock_response_failure",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Details = details,
                });
        }
    }

    private static int? GetErrorStatus(Exception error) => error switch
    {
        BedrockConverseTransportException transport => transport.Status,
        AmazonServiceException service when service.StatusCode != 0 => (int)service.StatusCode,
        HttpRequestException request when request.StatusCode is not null => (int)request.StatusCode.Value,
        _ => null,
    };

    private static string? GetErrorCode(Exception error) => error switch
    {
        BedrockConverseTransportException transport => NormalizeDiagnosticValue(transport.ErrorCode),
        AmazonServiceException service => NormalizeDiagnosticValue(service.ErrorCode),
        _ when error.GetType().Name.EndsWith("Exception", StringComparison.Ordinal) =>
            NormalizeDiagnosticValue(error.GetType().Name),
        _ => null,
    };

    private static string? GetRequestId(Exception error) => error switch
    {
        BedrockConverseTransportException transport => NormalizeDiagnosticValue(transport.RequestId),
        AmazonServiceException service => NormalizeDiagnosticValue(service.RequestId),
        _ => null,
    };

    private static string? NormalizeDiagnosticValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > _maxDiagnosticValueChars)
        {
            return null;
        }

        return value.Trim();
    }

    private static bool IsReservedBedrockHeader(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.StartsWith("x-amz-", StringComparison.Ordinal) ||
               lower is "authorization" or "host";
    }

    private static string? GetScopedEnv(string name, IReadOnlyDictionary<string, string>? environment) =>
        ProviderEnvironmentUtilities.GetProviderEnvValue(name, environment);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrEmpty(value));

    private static string? GetStandardBedrockEndpointRegion(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        const string suffix = ".amazonaws.com";
        if (!host.EndsWith(suffix, StringComparison.Ordinal) &&
            !host.EndsWith(".amazonaws.com.cn", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = host.Split('.');
        if (parts.Length < 4 || !parts[0].StartsWith("bedrock-runtime", StringComparison.Ordinal))
        {
            return null;
        }

        return parts[1];
    }

    private static string? GetArnRegion(string modelId)
    {
        var parts = modelId.Split(':');
        return parts.Length > 3 && parts[0] == "arn" && parts[2] == "bedrock" ? parts[3] : null;
    }

    private static bool IsString(JsonNode? node, string value) =>
        string.Equals(StringValue(node), value, StringComparison.Ordinal);

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

    private static bool GetBool(JsonObject? node, string name, bool fallback)
    {
        try
        {
            return node?[name]?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static byte[]? DecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static BedrockOptions CopyCommonOptions(SimpleStreamOptions? options) => new()
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
    };

    private static BedrockOptions CopyWith(
        BedrockOptions source,
        int? maxTokens,
        string? reasoning,
        ThinkingBudgets? thinkingBudgets) => new()
        {
            Signal = source.Signal,
            TelemetryContext = source.TelemetryContext,
            ApiKey = source.ApiKey,
            Fetch = source.Fetch,
            Environment = source.Environment,
            OnPayload = source.OnPayload,
            OnResponse = source.OnResponse,
            Headers = source.Headers,
            TimeoutMs = source.TimeoutMs,
            MaxRetries = source.MaxRetries,
            MaxRetryDelayMs = source.MaxRetryDelayMs,
            Temperature = source.Temperature,
            SamplingParameters = source.SamplingParameters,
            MaxTokens = maxTokens,
            Transport = source.Transport,
            CacheRetention = source.CacheRetention,
            SessionId = source.SessionId,
            WebSocketConnectTimeoutMs = source.WebSocketConnectTimeoutMs,
            Metadata = source.Metadata,
            Region = source.Region,
            Profile = source.Profile,
            ToolChoice = source.ToolChoice,
            Reasoning = reasoning,
            ThinkingBudgets = thinkingBudgets,
            InterleavedThinking = source.InterleavedThinking,
            ThinkingDisplay = source.ThinkingDisplay,
            RequestMetadata = source.RequestMetadata,
            BearerToken = source.BearerToken,
        };

    private static (int MaxTokens, ThinkingBudgets Budgets) AdjustMaxTokensForThinking(
        int? baseMaxTokens,
        Model model,
        string reasoning,
        ThinkingBudgets? budgets,
        Context context)
    {
        var modelMax = Math.Max(1, model.MaxTokens);
        var maximum = baseMaxTokens is null ? modelMax : Math.Min(baseMaxTokens.Value + GetThinkingBudget(budgets, reasoning, reasoning), modelMax);
        var available = model.ContextWindow > 0
            ? Math.Max(1, model.ContextWindow - EstimateUtilities.EstimateContextTokens(context).Tokens - 4096)
            : maximum;
        maximum = Math.Min(maximum, available);
        var budget = GetThinkingBudget(budgets, reasoning, reasoning);
        if (maximum <= budget)
        {
            budget = Math.Min(budget, Math.Max(0, maximum - 1024));
        }

        var adjustedBudgets = budgets ?? new ThinkingBudgets();
        return (maximum, adjustedBudgets with
        {
            Minimal = reasoning == ThinkingLevels.Minimal ? budget : adjustedBudgets.Minimal,
            Low = reasoning == ThinkingLevels.Low ? budget : adjustedBudgets.Low,
            Medium = reasoning == ThinkingLevels.Medium ? budget : adjustedBudgets.Medium,
            High = reasoning is ThinkingLevels.High or ThinkingLevels.XHigh or ThinkingLevels.Max ? budget : adjustedBudgets.High,
        });
    }

    private static AssistantMessage CreatePendingMessage(Model model) => new()
    {
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        StopReason = StopReasons.Pending,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private enum BlockKind
    {
        Text,
        Thinking,
        Tool,
    }

    private sealed class ActiveBlock
    {
        public int ContentIndex { get; init; }

        public BlockKind Kind { get; init; }

        public string? ToolId { get; init; }

        public string? ToolName { get; init; }

        public string PartialJson { get; set; } = string.Empty;

        public List<byte[]> RedactedChunks { get; } = [];

        public bool Finalized { get; set; }
    }

    private sealed class StreamState(AssistantMessage output)
    {
        public AssistantMessage Output { get; set; } = output;

        public Dictionary<int, ActiveBlock> Blocks { get; } = [];

        public bool HasStopReason { get; set; }
    }

    private sealed class UnsupportedStrictSchemaException(string message) : Exception(message);

    private static bool ResolveJsonSchemaStrictSampling(Tool tool, bool supportsStrictMode)
    {
        if (tool.ConstrainedSampling is not JsonSchemaSampling config)
        {
            return false;
        }

        if (!supportsStrictMode)
        {
            if (config.Strict == "require")
            {
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
            }

            return false;
        }

        try
        {
            _ = MakeStrictJsonSchema(tool.Parameters);
            return true;
        }
        catch (UnsupportedStrictSchemaException error)
        {
            if (config.Strict == "require")
            {
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {error.Message}.",
                    error);
            }

            return false;
        }
    }

    private static JsonObject MakeStrictJsonSchema(JsonNode schema)
    {
        if (schema is not JsonObject root)
        {
            throw new UnsupportedStrictSchemaException("root schema must have type object");
        }

        var clone = root.DeepClone().AsObject();
        MakeStrictJsonSchemaNode(clone);
        if (StringValue(clone["type"]) != "object")
        {
            throw new UnsupportedStrictSchemaException("root schema must have type object");
        }

        return clone;
    }

    private static void MakeStrictJsonSchemaNode(JsonObject schema)
    {
        foreach (var key in new[]
                 {
                     "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties",
                     "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames",
                     "contains", "prefixItems", "not", "if", "then", "else",
                 })
        {
            if (schema.ContainsKey(key))
            {
                throw new UnsupportedStrictSchemaException($"{key} schemas are unsupported");
            }
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            if (anyOf.Count == 0)
            {
                throw new UnsupportedStrictSchemaException("anyOf must contain at least one schema");
            }

            foreach (var variant in anyOf)
            {
                if (variant is not JsonObject variantObject)
                {
                    throw new UnsupportedStrictSchemaException("boolean schemas are unsupported");
                }

                if (IsStructuredSchema(variantObject))
                {
                    throw new UnsupportedStrictSchemaException("object and array unions are unsupported");
                }

                MakeStrictJsonSchemaNode(variantObject);
            }
        }
        else if (schema.ContainsKey("anyOf"))
        {
            throw new UnsupportedStrictSchemaException("anyOf must contain at least one schema");
        }

        if (schema["items"] is JsonArray)
        {
            throw new UnsupportedStrictSchemaException("tuple schemas are unsupported");
        }

        if (schema["items"] is JsonObject items)
        {
            MakeStrictJsonSchemaNode(items);
        }

        var isObject = StringValue(schema["type"]) == "object";
        if (schema.ContainsKey("properties") && !isObject)
        {
            throw new UnsupportedStrictSchemaException("properties require type object");
        }

        if (!isObject)
        {
            return;
        }

        if (schema["additionalProperties"] is { } additional && GetBoolValue(additional) != false)
        {
            throw new UnsupportedStrictSchemaException(
                "schema-valued or true additionalProperties is unsupported");
        }

        if (schema["properties"] is { } propertiesNode && propertiesNode is not JsonObject)
        {
            throw new UnsupportedStrictSchemaException("object properties must be a schema map");
        }

        if (schema["required"] is { } requiredNode &&
            (requiredNode is not JsonArray requiredArray || requiredArray.Any(item => StringValue(item) is null)))
        {
            throw new UnsupportedStrictSchemaException("object required must be a string array");
        }

        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        var requiredNames = (schema["required"] as JsonArray)?
            .Select(StringValue)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (requiredNames.Any(name => !properties.ContainsKey(name)))
        {
            throw new UnsupportedStrictSchemaException("required contains an unknown property");
        }

        foreach (var pair in properties.ToList())
        {
            if (pair.Value is not JsonObject property)
            {
                throw new UnsupportedStrictSchemaException("boolean schemas are unsupported");
            }

            MakeStrictJsonSchemaNode(property);
            if (!requiredNames.Contains(pair.Key) && !SchemaAllowsNull(property))
            {
                properties[pair.Key] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        property.DeepClone(),
                        new JsonObject { ["type"] = "null" }),
                };
            }
        }

        schema["required"] = new JsonArray(properties.Select(pair => (JsonNode?)pair.Key).ToArray());
        schema["additionalProperties"] = false;
    }

    private static bool IsStructuredSchema(JsonObject schema)
    {
        var type = StringValue(schema["type"]);
        return type is "object" or "array" || schema.ContainsKey("properties") || schema.ContainsKey("items");
    }

    private static bool SchemaAllowsNull(JsonNode? schema)
    {
        if (schema is not JsonObject objectSchema)
        {
            return false;
        }

        if (StringValue(objectSchema["type"]) == "null")
        {
            return true;
        }

        if (objectSchema["type"] is JsonArray types && types.Any(item => StringValue(item) == "null"))
        {
            return true;
        }

        if (objectSchema["const"] is null && objectSchema.ContainsKey("const"))
        {
            return true;
        }

        if (objectSchema["enum"] is JsonArray values && values.Any(item => item is null))
        {
            return true;
        }

        return objectSchema["anyOf"] is JsonArray anyOf && anyOf.Any(SchemaAllowsNull);
    }

    private static bool? GetBoolValue(JsonNode node)
    {
        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>AWS SDK-backed Bedrock Converse transport.</summary>
public sealed class AwsBedrockConverseTransport : IBedrockConverseTransport
{
    /// <inheritdoc />
    public async Task<BedrockConverseResponse> SendAsync(
        JsonObject payload,
        BedrockTransportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        var responseHeaders = new HeaderCapture();
        var config = new AmazonBedrockRuntimeConfig
        {
            ServiceURL = options.Endpoint,
            RegionEndpoint = options.Region is null ? null : RegionEndpoint.GetBySystemName(options.Region),
            Timeout = options.TimeoutMs is null ? null : TimeSpan.FromMilliseconds(options.TimeoutMs.Value),
        };
        if (!string.IsNullOrEmpty(options.Profile))
        {
            config.Profile = new Profile(options.Profile);
        }

        if (options.BearerToken is not null)
        {
            config.AWSTokenProvider = new ServiceBearerStaticTokenProvider(options.BearerToken, null);
            config.AuthSchemePreference = ["httpBearerAuth"];
        }

        if (options.Headers.Count > 0)
        {
            config.HttpClientFactory = new HeaderCapturingHttpClientFactory(options.Headers, responseHeaders);
        }

        var credentials = CreateCredentials(options);
        AmazonBedrockRuntimeClient? client = null;
        try
        {
            client = credentials is null
                ? new AmazonBedrockRuntimeClient(config)
                : new AmazonBedrockRuntimeClient(credentials, config);
            var request = BedrockRequestBuilder.Build(payload);
            var response = await client.ConverseStreamAsync(request, cancellationToken).ConfigureAwait(false);
            var headers = responseHeaders.Snapshot();
            if (response.ResponseMetadata?.Metadata is not null)
            {
                foreach (var pair in response.ResponseMetadata.Metadata)
                {
                    headers[pair.Key] = pair.Value;
                }
            }

            var requestId = response.ResponseMetadata?.RequestId;
            if (!string.IsNullOrEmpty(requestId) && !headers.ContainsKey("x-amzn-requestid"))
            {
                headers["x-amzn-requestid"] = requestId;
            }

            return new BedrockConverseResponse
            {
                Status = (int)response.HttpStatusCode,
                Headers = headers,
                RequestId = requestId,
                Events = TranslateEvents(response, client, cancellationToken),
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            client?.Dispose();
            throw Wrap(error);
        }
    }

    private static AWSCredentials? CreateCredentials(BedrockTransportOptions options)
    {
        if (options.SkipAuth)
        {
            return new AnonymousAWSCredentials();
        }

        if (string.IsNullOrEmpty(options.AccessKeyId) || string.IsNullOrEmpty(options.SecretAccessKey))
        {
            return null;
        }

        return string.IsNullOrEmpty(options.SessionToken)
            ? new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey)
            : new SessionAWSCredentials(options.AccessKeyId, options.SecretAccessKey, options.SessionToken);
    }

    private static BedrockConverseTransportException Wrap(Exception error)
    {
        if (error is BedrockConverseTransportException typed)
        {
            return typed;
        }

        if (error is AmazonServiceException service)
        {
            return new BedrockConverseTransportException(
                service.Message,
                service.StatusCode == 0 ? null : (int)service.StatusCode,
                service.ErrorCode,
                service.RequestId,
                null,
                null,
                service);
        }

        return new BedrockConverseTransportException(error.Message, innerException: error);
    }

    private static async IAsyncEnumerable<BedrockConverseEvent> TranslateEvents(
        ConverseStreamResponse response,
        AmazonBedrockRuntimeClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            if (response.Stream is null)
            {
                yield break;
            }

            await foreach (var item in response.Stream.WithCancellation(cancellationToken))
            {
                switch (item)
                {
                    case MessageStartEvent messageStart:
                        yield return new BedrockMessageStartEvent(messageStart.Role?.Value ?? string.Empty);
                        break;
                    case ContentBlockStartEvent blockStart:
                        yield return new BedrockContentBlockStartEvent(blockStart.ContentBlockIndex ?? 0)
                        {
                            ToolUseId = blockStart.Start?.ToolUse?.ToolUseId,
                            ToolName = blockStart.Start?.ToolUse?.Name,
                        };
                        break;
                    case ContentBlockDeltaEvent blockDelta:
                        yield return TranslateDelta(blockDelta);
                        break;
                    case ContentBlockStopEvent blockStop:
                        yield return new BedrockContentBlockStopEvent(blockStop.ContentBlockIndex ?? 0);
                        break;
                    case MessageStopEvent messageStop:
                        yield return new BedrockMessageStopEvent(messageStop.StopReason?.Value);
                        break;
                    case ConverseStreamMetadataEvent metadata when metadata.Usage is not null:
                        yield return new BedrockMetadataEvent(
                            metadata.Usage.InputTokens ?? 0,
                            metadata.Usage.OutputTokens ?? 0,
                            metadata.Usage.CacheReadInputTokens ?? 0,
                            metadata.Usage.CacheWriteInputTokens ?? 0,
                            metadata.Usage.TotalTokens ?? 0);
                        break;
                }
            }
        }
        finally
        {
            response.Dispose();
            client.Dispose();
        }
    }

    private static BedrockContentBlockDeltaEvent TranslateDelta(ContentBlockDeltaEvent item)
    {
        var delta = item.Delta ?? new AwsContentBlockDelta();
        var reasoning = delta.ReasoningContent;
        return new BedrockContentBlockDeltaEvent(item.ContentBlockIndex ?? 0)
        {
            Text = delta.Text,
            ToolInput = delta.ToolUse?.Input,
            ReasoningText = reasoning?.Text,
            Signature = reasoning?.Signature,
            RedactedContent = reasoning?.RedactedContent?.ToArray(),
        };
    }

    private sealed class HeaderCapture
    {
        private readonly ConcurrentDictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public void Add(HttpResponseMessage response)
        {
            foreach (var pair in response.Headers)
            {
                _headers[pair.Key] = string.Join(", ", pair.Value);
            }

            foreach (var pair in response.Content.Headers)
            {
                _headers[pair.Key] = string.Join(", ", pair.Value);
            }
        }

        public Dictionary<string, string> Snapshot() =>
            new(_headers, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class HeaderCapturingHttpClientFactory(
        IReadOnlyDictionary<string, string> headers,
        HeaderCapture capture) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config) =>
            new(new HeaderHandler(headers, capture, new HttpClientHandler()), disposeHandler: true);

        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => true;

        private sealed class HeaderHandler(
            IReadOnlyDictionary<string, string> headers,
            HeaderCapture capture,
            HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                foreach (var pair in headers)
                {
                    if (!IsReservedHeader(pair.Key))
                    {
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                }

                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                capture.Add(response);
                return response;
            }
        }
    }

    private static bool IsReservedHeader(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.StartsWith("x-amz-", StringComparison.Ordinal) ||
               lower is "authorization" or "host";
    }
}

internal static class BedrockRequestBuilder
{
    public static ConverseStreamRequest Build(JsonObject payload)
    {
        var request = new ConverseStreamRequest
        {
            ModelId = payload["modelId"]?.GetValue<string>() ?? string.Empty,
            Messages = ConvertMessages(payload["messages"] as JsonArray),
            System = ConvertSystem(payload["system"] as JsonArray),
            InferenceConfig = ConvertInference(payload["inferenceConfig"] as JsonObject),
            ToolConfig = ConvertToolConfig(payload["toolConfig"] as JsonObject),
            AdditionalModelRequestFields = ToDocument(payload["additionalModelRequestFields"]),
            RequestMetadata = ConvertRequestMetadata(payload["requestMetadata"] as JsonObject),
        };
        return request;
    }

    private static List<AwsMessage> ConvertMessages(JsonArray? messages)
    {
        var result = new List<AwsMessage>();
        if (messages is null)
        {
            return result;
        }

        foreach (var node in messages.OfType<JsonObject>())
        {
            var message = new AwsMessage
            {
                Role = StringValue(node["role"]) == "assistant" ? ConversationRole.Assistant : ConversationRole.User,
                Content = ConvertContent(node["content"] as JsonArray),
            };
            result.Add(message);
        }

        return result;
    }

    private static List<SystemContentBlock>? ConvertSystem(JsonArray? system)
    {
        if (system is null)
        {
            return null;
        }

        return system.OfType<JsonObject>().Select(node => new SystemContentBlock
        {
            Text = StringValue(node["text"]),
            CachePoint = node["cachePoint"] is JsonObject cache
                ? new CachePointBlock
                {
                    Type = CachePointType.Default,
                    Ttl = StringValue(cache["ttl"]) == "1h" ? CacheTTL.ONE_HOUR : null,
                }
                : null,
        }).ToList();
    }

    private static InferenceConfiguration? ConvertInference(JsonObject? inference)
    {
        if (inference is null)
        {
            return null;
        }

        return new InferenceConfiguration
        {
            MaxTokens = IntValue(inference["maxTokens"]),
            Temperature = FloatValue(inference["temperature"]),
        };
    }

    private static ToolConfiguration? ConvertToolConfig(JsonObject? config)
    {
        if (config is null)
        {
            return null;
        }

        var tools = new List<AwsTool>();
        if (config["tools"] is JsonArray toolNodes)
        {
            foreach (var node in toolNodes.OfType<JsonObject>())
            {
                if (node["toolSpec"] is not JsonObject spec)
                {
                    continue;
                }

                tools.Add(new AwsTool
                {
                    ToolSpec = new ToolSpecification
                    {
                        Name = StringValue(spec["name"]),
                        Description = StringValue(spec["description"]),
                        Strict = BoolValue(spec["strict"]),
                        InputSchema = new ToolInputSchema
                        {
                            Json = ToDocument((spec["inputSchema"] as JsonObject)?["json"]),
                        },
                    },
                });
            }
        }

        return new ToolConfiguration
        {
            Tools = tools,
            ToolChoice = ConvertToolChoice(config["toolChoice"] as JsonObject),
        };
    }

    private static ToolChoice? ConvertToolChoice(JsonObject? choice)
    {
        if (choice is null)
        {
            return null;
        }

        if (choice.ContainsKey("auto"))
        {
            return new ToolChoice { Auto = new AutoToolChoice() };
        }

        if (choice.ContainsKey("any"))
        {
            return new ToolChoice { Any = new AnyToolChoice() };
        }

        if (choice["tool"] is JsonObject tool)
        {
            return new ToolChoice
            {
                Tool = new SpecificToolChoice { Name = StringValue(tool["name"]) },
            };
        }

        return null;
    }

    private static List<AwsContentBlock> ConvertContent(JsonArray? content)
    {
        var result = new List<AwsContentBlock>();
        if (content is null)
        {
            return result;
        }

        foreach (var node in content.OfType<JsonObject>())
        {
            if (StringValue(node["text"]) is { } text)
            {
                result.Add(new AwsContentBlock { Text = text });
                continue;
            }

            if (node["cachePoint"] is JsonObject cache)
            {
                result.Add(new AwsContentBlock
                {
                    CachePoint = new CachePointBlock
                    {
                        Type = CachePointType.Default,
                        Ttl = StringValue(cache["ttl"]) == "1h" ? CacheTTL.ONE_HOUR : null,
                    },
                });
                continue;
            }

            if (node["image"] is JsonObject image)
            {
                var bytes = DecodeBase64((image["source"] as JsonObject)?["bytes"]);
                if (bytes is not null)
                {
                    result.Add(new AwsContentBlock
                    {
                        Image = new ImageBlock
                        {
                            Format = ParseImageFormat(StringValue(image["format"])),
                            Source = new ImageSource { Bytes = new MemoryStream(bytes, writable: false) },
                        },
                    });
                }

                continue;
            }

            if (node["toolUse"] is JsonObject toolUse)
            {
                result.Add(new AwsContentBlock
                {
                    ToolUse = new ToolUseBlock
                    {
                        ToolUseId = StringValue(toolUse["toolUseId"]),
                        Name = StringValue(toolUse["name"]),
                        Input = ToDocument(toolUse["input"]),
                    },
                });
                continue;
            }

            if (node["toolResult"] is JsonObject toolResult)
            {
                result.Add(new AwsContentBlock
                {
                    ToolResult = new ToolResultBlock
                    {
                        ToolUseId = StringValue(toolResult["toolUseId"]),
                        Status = StringValue(toolResult["status"]) == "error"
                            ? ToolResultStatus.Error
                            : ToolResultStatus.Success,
                        Content = ConvertToolResultContent(toolResult["content"] as JsonArray),
                    },
                });
                continue;
            }

            if (node["reasoningContent"] is JsonObject reasoning)
            {
                result.Add(new AwsContentBlock
                {
                    ReasoningContent = ConvertReasoning(reasoning),
                });
            }
        }

        return result;
    }

    private static ReasoningContentBlock ConvertReasoning(JsonObject reasoning)
    {
        if (reasoning["redactedContent"] is JsonValue redacted && DecodeBase64(redacted) is { } bytes)
        {
            return new ReasoningContentBlock { RedactedContent = new MemoryStream(bytes, writable: false) };
        }

        if (reasoning["reasoningText"] is JsonObject reasoningText)
        {
            return new ReasoningContentBlock
            {
                ReasoningText = new ReasoningTextBlock
                {
                    Text = StringValue(reasoningText["text"]),
                    Signature = StringValue(reasoningText["signature"]),
                },
            };
        }

        return new ReasoningContentBlock();
    }

    private static List<ToolResultContentBlock> ConvertToolResultContent(JsonArray? content)
    {
        var result = new List<ToolResultContentBlock>();
        if (content is null)
        {
            return result;
        }

        foreach (var node in content.OfType<JsonObject>())
        {
            if (StringValue(node["text"]) is { } text)
            {
                result.Add(new ToolResultContentBlock { Text = text });
            }
            else if (node["image"] is JsonObject image)
            {
                var bytes = DecodeBase64((image["source"] as JsonObject)?["bytes"]);
                if (bytes is not null)
                {
                    result.Add(new ToolResultContentBlock
                    {
                        Image = new ImageBlock
                        {
                            Format = ParseImageFormat(StringValue(image["format"])),
                            Source = new ImageSource { Bytes = new MemoryStream(bytes, writable: false) },
                        },
                    });
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string>? ConvertRequestMetadata(JsonObject? metadata) =>
        metadata?.ToDictionary(pair => pair.Key, pair => StringValue(pair.Value) ?? string.Empty, StringComparer.Ordinal);

    private static ImageFormat ParseImageFormat(string? value) => value switch
    {
        "jpeg" => ImageFormat.Jpeg,
        "png" => ImageFormat.Png,
        "gif" => ImageFormat.Gif,
        "webp" => ImageFormat.Webp,
        _ => throw new InvalidOperationException($"Unknown image type: {value}"),
    };

    private static Document ToDocument(JsonNode? node)
    {
        if (node is null)
        {
            return new Document();
        }

        if (node is JsonObject objectNode)
        {
            var dictionary = new Dictionary<string, Document>(StringComparer.Ordinal);
            foreach (var pair in objectNode)
            {
                dictionary[pair.Key] = ToDocument(pair.Value);
            }

            return new Document(dictionary);
        }

        if (node is JsonArray array)
        {
            return new Document(array.Select(ToDocument).ToList());
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return new Document(text);
            if (value.TryGetValue<bool>(out var boolean)) return new Document(boolean);
            if (value.TryGetValue<int>(out var integer)) return new Document(integer);
            if (value.TryGetValue<long>(out var longValue)) return new Document(longValue);
            if (value.TryGetValue<double>(out var doubleValue)) return new Document(doubleValue);
        }

        return new Document();
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
            return null;
        }
    }

    private static float? FloatValue(JsonNode? node)
    {
        try
        {
            return node?.GetValue<float>();
        }
        catch
        {
            return null;
        }
    }

    private static bool? BoolValue(JsonNode? node)
    {
        try
        {
            return node?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecodeBase64(JsonNode? node) => DecodeBase64(StringValue(node));

    private static byte[]? DecodeBase64(JsonValue value) => DecodeBase64(StringValue(value));
}
