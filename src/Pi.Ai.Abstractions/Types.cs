using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Pi.Telemetry;

namespace Pi.Ai;

/// <summary>Known provider API identifiers. Custom identifiers remain valid.</summary>
public static class ApiNames
{
    /// <summary>OpenAI chat-completions API.</summary>
    public const string OpenAiCompletions = "openai-completions";

    /// <summary>Mistral conversations API.</summary>
    public const string MistralConversations = "mistral-conversations";

    /// <summary>OpenAI Responses API.</summary>
    public const string OpenAiResponses = "openai-responses";

    /// <summary>Azure OpenAI Responses API.</summary>
    public const string AzureOpenAiResponses = "azure-openai-responses";

    /// <summary>OpenAI Codex Responses API.</summary>
    public const string OpenAiCodexResponses = "openai-codex-responses";

    /// <summary>Anthropic Messages API.</summary>
    public const string AnthropicMessages = "anthropic-messages";

    /// <summary>Amazon Bedrock Converse stream API.</summary>
    public const string BedrockConverseStream = "bedrock-converse-stream";

    /// <summary>Google Generative AI API.</summary>
    public const string GoogleGenerativeAi = "google-generative-ai";

    /// <summary>Google Vertex AI API.</summary>
    public const string GoogleVertex = "google-vertex";

    /// <summary>Pi Messages API.</summary>
    public const string PiMessages = "pi-messages";

    /// <summary>OpenRouter image-generation API.</summary>
    public const string OpenRouterImages = "openrouter-images";
}

/// <summary>String constants for Pi's open-ended provider identifiers.</summary>
public static class ProviderNames
{
    /// <summary>The faux provider used by deterministic tests.</summary>
    public const string Faux = "faux";
}

/// <summary>String constants for model thinking levels.</summary>
public static class ThinkingLevels
{
    /// <summary>Disables reasoning.</summary>
    public const string Off = "off";

    /// <summary>Minimal reasoning.</summary>
    public const string Minimal = "minimal";

    /// <summary>Low reasoning.</summary>
    public const string Low = "low";

    /// <summary>Medium reasoning.</summary>
    public const string Medium = "medium";

    /// <summary>High reasoning.</summary>
    public const string High = "high";

    /// <summary>Extra-high reasoning.</summary>
    public const string XHigh = "xhigh";

    /// <summary>Maximum reasoning.</summary>
    public const string Max = "max";
}

/// <summary>String constants for assistant completion termination reasons.</summary>
public static class StopReasons
{
    /// <summary>A stream is still producing output.</summary>
    public const string Pending = "pending";

    /// <summary>The assistant ended normally.</summary>
    public const string Stop = "stop";

    /// <summary>The provider reached a token limit.</summary>
    public const string Length = "length";

    /// <summary>The assistant requested a tool call.</summary>
    public const string ToolUse = "toolUse";

    /// <summary>The provider returned an error.</summary>
    public const string Error = "error";

    /// <summary>The request was aborted.</summary>
    public const string Aborted = "aborted";

    /// <summary>The provider returned a deferred response handle.</summary>
    public const string Deferred = "deferred";
}

/// <summary>String constants for provider cache retention preferences.</summary>
[SuppressMessage("Naming", "CA1720", Justification = "The values are protocol strings, not CLR type names.")]
public static class CacheRetentions
{
    /// <summary>Do not use prompt caching.</summary>
    public const string None = "none";

    /// <summary>Use short-lived prompt caching.</summary>
    public const string Short = "short";

    /// <summary>Use long-lived prompt caching.</summary>
    public const string Long = "long";
}

/// <summary>HTTP response metadata supplied to provider lifecycle callbacks.</summary>
public sealed record ProviderResponse(int Status, IReadOnlyDictionary<string, string> Headers);

/// <summary>Provider-scoped environment overrides.</summary>
public sealed class ProviderEnvironment : Dictionary<string, string>
{
    /// <summary>Creates an ordinal-keyed environment collection.</summary>
    public ProviderEnvironment() : base(StringComparer.Ordinal)
    {
    }
}

/// <summary>Provider-scoped custom headers. A null value suppresses a default header.</summary>
public sealed class ProviderHeaders : Dictionary<string, string?>
{
    /// <summary>Creates a case-insensitive header collection.</summary>
    public ProviderHeaders() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

/// <summary>Common authentication, transport, and lifecycle callbacks for a provider request.</summary>
public class ProviderRequestOptions
{
    /// <summary>Cancellation requested by the caller.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Explicit parent context for telemetry produced by this request.</summary>
    public TelemetryContext? TelemetryContext { get; init; }

    /// <summary>Explicit provider API key.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Optional injected HTTP request function.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Fetch { get; init; }

    /// <summary>Provider-scoped environment overrides.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Callback that may inspect or replace a provider payload.</summary>
    public Func<JsonNode?, Model, ValueTask<JsonNode?>>? OnPayload { get; init; }

    /// <summary>Callback invoked after an HTTP response is received.</summary>
    public Func<ProviderResponse, Model, ValueTask>? OnResponse { get; init; }

    /// <summary>Custom HTTP headers.</summary>
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    /// <summary>HTTP request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Maximum provider retry attempts.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>Maximum server-requested retry delay in milliseconds.</summary>
    public int? MaxRetryDelayMs { get; init; }
}

/// <summary>Provider streaming options shared by all API adapters.</summary>
public class StreamOptions : ProviderRequestOptions
{
    /// <summary>Sampling temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Additional sampling parameters merged into compatible requests.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? SamplingParameters { get; init; }

    /// <summary>Maximum output token count.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Preferred provider transport.</summary>
    public string? Transport { get; init; }

    /// <summary>Prompt cache retention preference.</summary>
    public string? CacheRetention { get; init; }

    /// <summary>Logical session identifier used for provider cache affinity.</summary>
    public string? SessionId { get; init; }

    /// <summary>WebSocket connection timeout in milliseconds.</summary>
    public int? WebSocketConnectTimeoutMs { get; init; }

    /// <summary>Provider-neutral request metadata.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Metadata { get; init; }
}

/// <summary>Provider options with arbitrary API-specific extension values.</summary>
public sealed class ProviderStreamOptions : StreamOptions
{
    /// <summary>API-specific extension values.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Extensions { get; init; } =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
}

/// <summary>Options for polling a deferred provider response.</summary>
public sealed class DeferredFetchOptions : ProviderRequestOptions
{
    /// <summary>Maximum provider long-poll duration in milliseconds.</summary>
    public int Wait { get; init; }
}

/// <summary>Options for best-effort deferred-response cancellation.</summary>
public sealed class DeferredCancelOptions : ProviderRequestOptions
{
}

/// <summary>Token budgets for provider reasoning levels.</summary>
public sealed record ThinkingBudgets
{
    /// <summary>Minimal-level budget.</summary>
    public int? Minimal { get; init; }

    /// <summary>Low-level budget.</summary>
    public int? Low { get; init; }

    /// <summary>Medium-level budget.</summary>
    public int? Medium { get; init; }

    /// <summary>High-level budget.</summary>
    public int? High { get; init; }
}

/// <summary>Provider-neutral options for simple stream requests.</summary>
public sealed class SimpleStreamOptions : StreamOptions
{
    /// <summary>Provider-neutral tool selection.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Requested reasoning level.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Whether to request a deferred response.</summary>
    public bool Deferred { get; init; }

    /// <summary>Deferred response window, when requested.</summary>
    public string? DeferredWindow { get; init; }

    /// <summary>Custom reasoning token budgets.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}

/// <summary>Text signature metadata preserved by provider adapters.</summary>
public sealed record TextSignatureV1(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("phase"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase = null);

/// <summary>A content block returned by a model.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ToolCall), "toolCall")]
public abstract record ContentBlock
{
    /// <summary>The upstream content discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>Text content returned by a model.</summary>
public sealed record TextContent(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("textSignature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextSignature = null)
    : ContentBlock
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text";
}

/// <summary>Reasoning content returned by a model.</summary>
public sealed record ThinkingContent(
    [property: JsonPropertyName("thinking")] string Thinking,
    [property: JsonPropertyName("thinkingSignature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingSignature = null,
    [property: JsonPropertyName("redacted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Redacted = null)
    : ContentBlock
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking";
}

/// <summary>Base64-encoded image content.</summary>
public sealed record ImageContent(
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("mimeType")] string MimeType)
    : ContentBlock
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "image";
}

/// <summary>Tool call content returned by a model.</summary>
public sealed record ToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonObject Arguments,
    [property: JsonPropertyName("thoughtSignature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThoughtSignature = null,
    [property: JsonPropertyName("namespace"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Namespace = null)
    : ContentBlock
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolCall";
}

/// <summary>Token usage and provider cost for one assistant response.</summary>
public sealed record Usage
{
    /// <summary>Non-cached input tokens.</summary>
    public int Input { get; init; }

    /// <summary>Output tokens.</summary>
    public int Output { get; init; }

    /// <summary>Cached input tokens read.</summary>
    public int CacheRead { get; init; }

    /// <summary>Cached input tokens written.</summary>
    public int CacheWrite { get; init; }

    /// <summary>Subset of cache writes using one-hour retention.</summary>
    public int? CacheWrite1h { get; init; }

    /// <summary>Reasoning tokens, when reported by the provider.</summary>
    public int? Reasoning { get; init; }

    /// <summary>Total tokens reported by the provider.</summary>
    public int TotalTokens { get; init; }

    /// <summary>Cost breakdown.</summary>
    public UsageCost Cost { get; init; } = new();
}

/// <summary>Cost breakdown for one response.</summary>
public sealed record UsageCost
{
    /// <summary>Input cost.</summary>
    public double Input { get; set; }

    /// <summary>Output cost.</summary>
    public double Output { get; set; }

    /// <summary>Cached-read cost.</summary>
    public double CacheRead { get; set; }

    /// <summary>Cached-write cost.</summary>
    public double CacheWrite { get; set; }

    /// <summary>Total cost.</summary>
    public double Total { get; set; }
}

/// <summary>Provider-owned deferred response handle.</summary>
public sealed record DeferredHandle
{
    /// <summary>Provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Requested model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>API identifier.</summary>
    public required string Api { get; init; }

    /// <summary>Provider token.</summary>
    public required string Id { get; init; }

    /// <summary>Expiration timestamp in Unix milliseconds.</summary>
    public long? ExpiresAt { get; init; }

    /// <summary>Suggested polling interval in milliseconds.</summary>
    public int? PollAfterMs { get; init; }

    /// <summary>Provider conversion data.</summary>
    public JsonNode? Data { get; init; }
}

/// <summary>Base message in a model context.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
public abstract record Message
{
    /// <summary>The upstream role discriminator.</summary>
    [JsonIgnore]
    public abstract string Role { get; }
}

/// <summary>User input message.</summary>
public sealed record UserMessage(
    [property: JsonPropertyName("content")] object Content,
    [property: JsonPropertyName("timestamp")] long Timestamp)
    : Message
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Role => "user";

    /// <summary>Creates a text user message.</summary>
    public static UserMessage Text(string content, long timestamp) => new(content, timestamp);

    /// <summary>Creates a multimodal user message.</summary>
    public static UserMessage Blocks(IReadOnlyList<ContentBlock> content, long timestamp) => new(content, timestamp);
}

/// <summary>Assistant response message.</summary>
public sealed record AssistantMessage : Message
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Role => "assistant";

    /// <summary>Response content blocks.</summary>
    [JsonPropertyName("content")]
    public IReadOnlyList<ContentBlock> Content { get; init; } = [];

    /// <summary>API identifier.</summary>
    [JsonPropertyName("api")]
    public required string Api { get; init; }

    /// <summary>Provider identifier.</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>Requested model identifier.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Concrete response model when it differs from the request model.</summary>
    [JsonPropertyName("responseModel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseModel { get; init; }

    /// <summary>Provider response identifier.</summary>
    [JsonPropertyName("responseId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; init; }

    /// <summary>Redacted provider/runtime diagnostics.</summary>
    [JsonPropertyName("diagnostics"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AssistantMessageDiagnostic>? Diagnostics { get; set; }

    /// <summary>Usage and cost.</summary>
    [JsonPropertyName("usage")]
    public Usage Usage { get; init; } = new();

    /// <summary>Completion stop reason.</summary>
    [JsonPropertyName("stopReason")]
    public required string StopReason { get; init; }

    /// <summary>Deferred response handle.</summary>
    [JsonPropertyName("deferred"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeferredHandle? Deferred { get; init; }

    /// <summary>Error message for an unsuccessful response.</summary>
    [JsonPropertyName("errorMessage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>Raw provider stop reason.</summary>
    [JsonPropertyName("rawStopReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawStopReason { get; init; }

    /// <summary>Whether the provider explicitly ended its turn.</summary>
    [JsonPropertyName("endTurn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EndTurn { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}

/// <summary>Tool execution result message.</summary>
public sealed record ToolResultMessage : Message
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Role => "toolResult";

    /// <summary>Tool call identifier.</summary>
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    /// <summary>Tool name.</summary>
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    /// <summary>Tool output blocks.</summary>
    [JsonPropertyName("content")]
    public IReadOnlyList<ContentBlock> Content { get; init; } = [];

    /// <summary>Tool-specific details.</summary>
    [JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Details { get; init; }

    /// <summary>Optional tool execution usage.</summary>
    [JsonPropertyName("usage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? Usage { get; init; }

    /// <summary>Tools loaded by this result.</summary>
    [JsonPropertyName("addedToolNames"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AddedToolNames { get; init; }

    /// <summary>Whether execution failed.</summary>
    [JsonPropertyName("isError")]
    public bool IsError { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}

/// <summary>Input context supplied to a provider.</summary>
public sealed record Context
{
    /// <summary>Optional system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Ordered conversation messages.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];

    /// <summary>Available tool definitions.</summary>
    public IReadOnlyList<Tool> Tools { get; init; } = [];
}

/// <summary>OpenAI grammar format.</summary>
public static class GrammarFormats
{
    /// <summary>OpenAI Lark grammar.</summary>
    public const string OpenAiLark = "openai_lark";

    /// <summary>OpenAI regular-expression grammar.</summary>
    public const string OpenAiRegex = "openai_regex";
}

/// <summary>Provider-side constrained sampling configuration.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JsonSchemaSampling), "json_schema")]
[JsonDerivedType(typeof(GrammarSampling), "grammar")]
public abstract record ConstrainedSamplingConfig
{
}

/// <summary>JSON-schema constrained sampling.</summary>
public sealed record JsonSchemaSampling(
    [property: JsonPropertyName("strict")] string Strict) : ConstrainedSamplingConfig;

/// <summary>Grammar constrained sampling.</summary>
public sealed record GrammarSampling(
    [property: JsonPropertyName("variants")] IReadOnlyDictionary<string, string> Variants) : ConstrainedSamplingConfig;

/// <summary>Provider tool definition.</summary>
public sealed record Tool
{
    /// <summary>Tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Tool description.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema for tool parameters.</summary>
    public JsonNode Parameters { get; init; } = new JsonObject();

    /// <summary>Optional provider-side constrained sampling configuration.</summary>
    public ConstrainedSamplingConfig? ConstrainedSampling { get; init; }
}

/// <summary>One assistant stream event.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StreamStartEvent), "start")]
[JsonDerivedType(typeof(TextStartEvent), "text_start")]
[JsonDerivedType(typeof(TextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(TextEndEvent), "text_end")]
[JsonDerivedType(typeof(ThinkingStartEvent), "thinking_start")]
[JsonDerivedType(typeof(ThinkingDeltaEvent), "thinking_delta")]
[JsonDerivedType(typeof(ThinkingEndEvent), "thinking_end")]
[JsonDerivedType(typeof(ToolCallStartEvent), "toolcall_start")]
[JsonDerivedType(typeof(ToolCallDeltaEvent), "toolcall_delta")]
[JsonDerivedType(typeof(ToolCallEndEvent), "toolcall_end")]
[JsonDerivedType(typeof(StreamDoneEvent), "done")]
[JsonDerivedType(typeof(StreamErrorEvent), "error")]
public abstract record AssistantMessageEvent
{
    /// <summary>The upstream event discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>Initial assistant stream event.</summary>
public sealed record StreamStartEvent(
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "start";
}

/// <summary>Start of a text content block.</summary>
public sealed record TextStartEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text_start";
}

/// <summary>Incremental text content.</summary>
public sealed record TextDeltaEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("delta")] string Delta,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text_delta";
}

/// <summary>End of a text content block.</summary>
public sealed record TextEndEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text_end";
}

/// <summary>Start of a reasoning content block.</summary>
public sealed record ThinkingStartEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking_start";
}

/// <summary>Incremental reasoning content.</summary>
public sealed record ThinkingDeltaEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("delta")] string Delta,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking_delta";
}

/// <summary>End of a reasoning content block.</summary>
public sealed record ThinkingEndEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "thinking_end";
}

/// <summary>Start of a tool call content block.</summary>
public sealed record ToolCallStartEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolcall_start";
}

/// <summary>Incremental tool call arguments.</summary>
public sealed record ToolCallDeltaEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("delta")] string Delta,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolcall_delta";
}

/// <summary>End of a tool call content block.</summary>
public sealed record ToolCallEndEvent(
    [property: JsonPropertyName("contentIndex")] int ContentIndex,
    [property: JsonPropertyName("toolCall")] ToolCall ToolCall,
    [property: JsonPropertyName("partial")] AssistantMessage Partial) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "toolcall_end";
}

/// <summary>Successful terminal assistant stream event.</summary>
public sealed record StreamDoneEvent(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] AssistantMessage Message) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "done";
}

/// <summary>Unsuccessful terminal assistant stream event.</summary>
public sealed record StreamErrorEvent(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("error")] AssistantMessage Error) : AssistantMessageEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "error";
}

/// <summary>Function signature for a provider stream implementation.</summary>
public delegate AssistantMessageEventStream StreamFunction(
    Model model,
    Context context,
    StreamOptions? options = null);

/// <summary>Function signature for image generation.</summary>
public delegate Task<AssistantImages> ImagesFunction(
    ImagesModel model,
    ImagesContext context,
    ProviderRequestOptions? options = null);

/// <summary>Uniform API implementation surface used by provider factories.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi provider contract name.")]
public interface ProviderStreams
{
    /// <summary>Starts a full provider stream.</summary>
    AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null);

    /// <summary>Starts a provider-neutral simple stream.</summary>
    AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null);

    /// <summary>Fetches a deferred response when supported.</summary>
    AssistantMessageEventStream? FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions? options = null) => null;

    /// <summary>Cancels a deferred response when supported.</summary>
    Task CancelDeferredAsync(Model model, DeferredHandle handle, DeferredCancelOptions? options = null) => Task.CompletedTask;
}

/// <summary>Uniform image API implementation surface.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi provider contract name.")]
public interface ProviderImages
{
    /// <summary>Generates images from an image context.</summary>
    Task<AssistantImages> GenerateImagesAsync(ImagesModel model, ImagesContext context, ProviderRequestOptions? options = null);
}

/// <summary>Image-generation input context.</summary>
public sealed record ImagesContext(IReadOnlyList<ContentBlock> Input);

/// <summary>Image-generation result.</summary>
public sealed record AssistantImages
{
    /// <summary>API identifier.</summary>
    public required string Api { get; init; }

    /// <summary>Provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Model identifier.</summary>
    public required string Model { get; init; }

    /// <summary>Generated output blocks.</summary>
    public IReadOnlyList<ContentBlock> Output { get; init; } = [];

    /// <summary>Provider response identifier.</summary>
    public string? ResponseId { get; init; }

    /// <summary>Optional usage.</summary>
    public Usage? Usage { get; init; }

    /// <summary>Stop reason.</summary>
    public required string StopReason { get; init; }

    /// <summary>Error message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; init; }
}

/// <summary>Model cost rates in dollars per million tokens.</summary>
public record ModelCostRates
{
    /// <summary>Input-token rate.</summary>
    public double Input { get; init; }

    /// <summary>Output-token rate.</summary>
    public double Output { get; init; }

    /// <summary>Cache-read rate.</summary>
    public double CacheRead { get; init; }

    /// <summary>Cache-write rate.</summary>
    public double CacheWrite { get; init; }
}

/// <summary>One threshold in a model's tiered cost table.</summary>
public sealed record ModelCostTier : ModelCostRates
{
    /// <summary>Use this tier above this input-token count.</summary>
    public int InputTokensAbove { get; init; }
}

/// <summary>Model pricing metadata.</summary>
public sealed record ModelCost : ModelCostRates
{
    /// <summary>Optional request-wide pricing tiers.</summary>
    public IReadOnlyList<ModelCostTier> Tiers { get; init; } = [];
}

/// <summary>Unified provider model description.</summary>
public sealed record Model
{
    /// <summary>Stable model identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display model name.</summary>
    public required string Name { get; init; }

    /// <summary>API adapter identifier.</summary>
    public required string Api { get; init; }

    /// <summary>Owning provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider base URL.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Whether the model supports reasoning.</summary>
    public bool Reasoning { get; init; }

    /// <summary>Provider-specific thinking-level mappings.</summary>
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }

    /// <summary>Accepted input modalities.</summary>
    public IReadOnlyList<string> Input { get; init; } = ["text"];

    /// <summary>Pricing metadata.</summary>
    public ModelCost Cost { get; init; } = new();

    /// <summary>Context window size.</summary>
    public int ContextWindow { get; init; }

    /// <summary>Maximum output token count.</summary>
    public int MaxTokens { get; init; }

    /// <summary>Default sampling parameters.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? SamplingParameters { get; init; }

    /// <summary>Static provider headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Provider compatibility overrides.</summary>
    public JsonObject? Compatibility { get; init; }
}

/// <summary>Image model description.</summary>
public sealed record ImagesModel
{
    /// <summary>Stable model identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display model name.</summary>
    public required string Name { get; init; }

    /// <summary>Image API identifier.</summary>
    public required string Api { get; init; }

    /// <summary>Owning provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider base URL.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Accepted output modalities.</summary>
    public IReadOnlyList<string> Output { get; init; } = ["image"];

    /// <summary>Pricing metadata.</summary>
    public ModelCost Cost { get; init; } = new();
}
