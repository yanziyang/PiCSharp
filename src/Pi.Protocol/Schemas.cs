using System.Text.Json;

namespace Pi.Protocol;

/// <summary>The protocol version negotiated by the client and server.</summary>
public static class ProtocolConstants
{
    /// <summary>The current Pi protocol version.</summary>
    public const int ProtocolVersion = 1;
}

/// <summary>Thinking levels supported by a model.</summary>
public enum ThinkingLevel
{
    /// <summary>No thinking.</summary>
    Off,
    /// <summary>Minimal thinking.</summary>
    Minimal,
    /// <summary>Low thinking.</summary>
    Low,
    /// <summary>Medium thinking.</summary>
    Medium,
    /// <summary>High thinking.</summary>
    High,
    /// <summary>Extra-high thinking.</summary>
    Xhigh,
    /// <summary>Maximum thinking.</summary>
    Max,
}

/// <summary>Lifecycle phase of an acquired session.</summary>
public enum SessionPhase
{
    /// <summary>The session is idle.</summary>
    Idle,
    /// <summary>The session is processing a turn.</summary>
    Turn,
    /// <summary>The session is compacting context.</summary>
    Compaction,
    /// <summary>The session is producing a branch summary.</summary>
    BranchSummary,
    /// <summary>The session is retrying a request.</summary>
    Retry,
}

/// <summary>Input modalities accepted by a model.</summary>
public enum ModelInputKind
{
    /// <summary>Text input.</summary>
    Text,
    /// <summary>Image input.</summary>
    Image,
}

/// <summary>The content kind used by an incremental assistant delta.</summary>
public enum ContentKind
{
    /// <summary>Text content.</summary>
    Text,
    /// <summary>Thinking content.</summary>
    Thinking,
    /// <summary>Tool-call content.</summary>
    ToolCall,
}

/// <summary>Terminal reason for an assistant transcript item.</summary>
public enum TranscriptStopReason
{
    /// <summary>The assistant stopped normally.</summary>
    Stop,
    /// <summary>The model reached its output limit.</summary>
    Length,
    /// <summary>The assistant requested a tool.</summary>
    ToolUse,
    /// <summary>The assistant failed.</summary>
    Error,
    /// <summary>The assistant was aborted.</summary>
    Aborted,
}

/// <summary>Protocol error codes returned by the server.</summary>
public enum ProtocolErrorCode
{
    /// <summary>Protocol version negotiation failed.</summary>
    Version,
    /// <summary>The session is busy.</summary>
    Busy,
    /// <summary>The session is locked.</summary>
    SessionLocked,
    /// <summary>The requested resource was not found.</summary>
    NotFound,
    /// <summary>The request is invalid.</summary>
    InvalidRequest,
    /// <summary>The request is not implemented.</summary>
    NotImplemented,
    /// <summary>An internal server error occurred.</summary>
    InternalError,
}

/// <summary>A recursive JSON-compatible value used in protocol details and tool inputs.</summary>
public abstract record JsonValue
{
    /// <summary>The JSON null value.</summary>
    public sealed record JsonNull : JsonValue;

    /// <summary>A JSON boolean value.</summary>
    public sealed record JsonBoolean(bool Value) : JsonValue;

    /// <summary>A JSON number value.</summary>
    public sealed record JsonNumber(double Value) : JsonValue;

    /// <summary>A JSON string value.</summary>
    public sealed record JsonString(string Value) : JsonValue;

    /// <summary>A JSON array value.</summary>
    public sealed record JsonArray(IReadOnlyList<JsonValue> Values) : JsonValue;

    /// <summary>A JSON object value.</summary>
    public sealed record JsonObject(IReadOnlyDictionary<string, JsonValue> Properties) : JsonValue;

    /// <summary>Converts a supported CLR value to a recursive JSON value.</summary>
    public static JsonValue From(object? value)
    {
        return value switch
        {
            null => new JsonNull(),
            JsonValue json => json,
            bool boolean => new JsonBoolean(boolean),
            string text => new JsonString(text),
            byte number => new JsonNumber(number),
            sbyte number => new JsonNumber(number),
            short number => new JsonNumber(number),
            ushort number => new JsonNumber(number),
            int number => new JsonNumber(number),
            uint number => new JsonNumber(number),
            long number => new JsonNumber(number),
            ulong number => new JsonNumber(number),
            float number => new JsonNumber(number),
            double number => new JsonNumber(number),
            decimal number => new JsonNumber((double)number),
            JsonElement element => FromJsonElement(element),
            IDictionary<string, object?> dictionary => new JsonObject(
                dictionary.ToDictionary(static pair => pair.Key, static pair => From(pair.Value), StringComparer.Ordinal)),
            IReadOnlyDictionary<string, object?> dictionary => new JsonObject(
                dictionary.ToDictionary(static pair => pair.Key, static pair => From(pair.Value), StringComparer.Ordinal)),
            IList<object?> list => new JsonArray(list.Select(From).ToArray()),
            Array array => new JsonArray(array.Cast<object?>().Select(From).ToArray()),
            _ => throw new ArgumentException($"Unsupported JSON value type: {value.GetType().Name}", nameof(value)),
        };
    }

    /// <summary>Returns the CLR value consumed by the protocol CBOR encoder.</summary>
    internal object? ToWireValue() => this switch
    {
        JsonNull => null,
        JsonBoolean boolean => boolean.Value,
        JsonNumber number => number.Value,
        JsonString text => text.Value,
        JsonArray array => array.Values.Select(static item => item.ToWireValue()).ToArray(),
        JsonObject map => ToOrderedMap(map.Properties),
        _ => throw new InvalidOperationException($"Unknown JSON value type {GetType().Name}"),
    };

    /// <summary>Converts a Boolean to a JSON value.</summary>
    public static implicit operator JsonValue(bool value) => new JsonBoolean(value);

    /// <summary>Converts a string to a JSON value.</summary>
    public static implicit operator JsonValue(string value) => new JsonString(value);

    /// <summary>Converts a 32-bit integer to a JSON value.</summary>
    public static implicit operator JsonValue(int value) => new JsonNumber(value);

    /// <summary>Converts a 64-bit integer to a JSON value.</summary>
    public static implicit operator JsonValue(long value) => new JsonNumber(value);

    /// <summary>Converts a double-precision number to a JSON value.</summary>
    public static implicit operator JsonValue(double value) => new JsonNumber(value);

    /// <summary>Converts a decimal number to a JSON value.</summary>
    public static implicit operator JsonValue(decimal value) => new JsonNumber((double)value);

    private static JsonValue FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => new JsonNull(),
            JsonValueKind.False => new JsonBoolean(false),
            JsonValueKind.True => new JsonBoolean(true),
            JsonValueKind.Number when element.TryGetDouble(out double number) => new JsonNumber(number),
            JsonValueKind.String => new JsonString(element.GetString() ?? string.Empty),
            JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(FromJsonElement).ToArray()),
            JsonValueKind.Object => new JsonObject(
                element.EnumerateObject().ToDictionary(static property => property.Name, static property => FromJsonElement(property.Value), StringComparer.Ordinal)),
            _ => throw new ArgumentException("Unsupported JSON element", nameof(element)),
        };
    }

    private static OrderedMap ToOrderedMap(IReadOnlyDictionary<string, JsonValue> properties)
    {
        OrderedMap map = new();
        foreach (KeyValuePair<string, JsonValue> property in properties)
        {
            map.Add(property.Key, property.Value.ToWireValue());
        }

        return map;
    }
}

/// <summary>A model provider and model identifier.</summary>
public sealed record ModelRef(string Provider, string Id);

/// <summary>Per-token model pricing.</summary>
public sealed record ModelCost(double Input, double Output, double CacheRead, double CacheWrite);

/// <summary>Metadata advertised for a model.</summary>
public sealed record ModelMetadata(
    string Provider,
    string Id,
    string Name,
    string Api,
    bool Reasoning,
    IReadOnlyList<ModelInputKind> Input,
    long ContextWindow,
    long MaxTokens,
    ModelCost Cost,
    IReadOnlyList<ThinkingLevel> SupportedThinkingLevels,
    bool Authenticated);

/// <summary>Token and cost usage for a model call.</summary>
public sealed record Usage(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long? Reasoning,
    long TotalTokens,
    UsageCost Cost);

/// <summary>Cost totals inside a usage record.</summary>
public sealed record UsageCost(double Input, double Output, double CacheRead, double CacheWrite, double Total);

/// <summary>Base type for user, assistant, and tool content blocks.</summary>
public abstract record Content
{
    /// <summary>The exact protocol discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>A text content block.</summary>
public sealed record TextContent(string Text) : Content
{
    /// <inheritdoc />
    public override string Type => "text";
}

/// <summary>Assistant thinking content.</summary>
public sealed record ThinkingContent(string Thinking, bool? Redacted = null) : Content
{
    /// <inheritdoc />
    public override string Type => "thinking";
}

/// <summary>An image content block.</summary>
public sealed record ImageContent(string Data, string MimeType) : Content
{
    /// <inheritdoc />
    public override string Type => "image";
}

/// <summary>An assistant tool-call content block.</summary>
public sealed record ToolCallContent(string ToolCallId, string ToolName, JsonValue Input) : Content
{
    /// <inheritdoc />
    public override string Type => "toolCall";
}

/// <summary>A user transcript item.</summary>
public sealed record UserTranscriptItem(
    string Id,
    IReadOnlyList<Content> Content,
    long Timestamp) : TranscriptItem(Id, "user", Timestamp);

/// <summary>Base type for assistant transcript states.</summary>
public abstract record AssistantTranscriptItem(
    string Id,
    IReadOnlyList<Content> Content,
    ModelRef Model,
    string? ResponseModel,
    Usage? Usage,
    long Timestamp) : TranscriptItem(Id, "assistant", Timestamp)
{
    /// <summary>The exact assistant status discriminator.</summary>
    public abstract string Status { get; }
}

/// <summary>An assistant item that is still streaming.</summary>
public sealed record StreamingAssistantTranscriptItem(
    string Id,
    IReadOnlyList<Content> Content,
    ModelRef Model,
    string? ResponseModel,
    Usage? Usage,
    long Timestamp) : AssistantTranscriptItem(Id, Content, Model, ResponseModel, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "streaming";
}

/// <summary>A completed assistant item.</summary>
public sealed record CompleteAssistantTranscriptItem(
    string Id,
    IReadOnlyList<Content> Content,
    ModelRef Model,
    string? ResponseModel,
    Usage? Usage,
    long Timestamp,
    TranscriptStopReason StopReason) : AssistantTranscriptItem(Id, Content, Model, ResponseModel, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "complete";
}

/// <summary>An assistant item that ended with an error.</summary>
public sealed record ErrorAssistantTranscriptItem(
    string Id,
    IReadOnlyList<Content> Content,
    ModelRef Model,
    string? ResponseModel,
    Usage? Usage,
    long Timestamp,
    string? ErrorMessage) : AssistantTranscriptItem(Id, Content, Model, ResponseModel, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "error";
}

/// <summary>An assistant item aborted before completion.</summary>
public sealed record AbortedAssistantTranscriptItem(
    string Id,
    IReadOnlyList<Content> Content,
    ModelRef Model,
    string? ResponseModel,
    Usage? Usage,
    long Timestamp,
    string? ErrorMessage) : AssistantTranscriptItem(Id, Content, Model, ResponseModel, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "aborted";
}

/// <summary>Base type for tool transcript states.</summary>
public abstract record ToolTranscriptItem(
    string Id,
    string ToolCallId,
    string ToolName,
    JsonValue Input,
    IReadOnlyList<Content> Content,
    JsonValue? Details,
    Usage? Usage,
    long Timestamp) : TranscriptItem(Id, "tool", Timestamp)
{
    /// <summary>The exact tool status discriminator.</summary>
    public abstract string Status { get; }

    /// <summary>Whether the tool result is an error.</summary>
    public abstract bool IsError { get; }
}

/// <summary>A tool that is still running.</summary>
public sealed record RunningToolTranscriptItem(
    string Id,
    string ToolCallId,
    string ToolName,
    JsonValue Input,
    IReadOnlyList<Content> Content,
    JsonValue? Details,
    Usage? Usage,
    long Timestamp) : ToolTranscriptItem(Id, ToolCallId, ToolName, Input, Content, Details, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "running";

    /// <inheritdoc />
    public override bool IsError => false;
}

/// <summary>A tool that completed successfully.</summary>
public sealed record CompleteToolTranscriptItem(
    string Id,
    string ToolCallId,
    string ToolName,
    JsonValue Input,
    IReadOnlyList<Content> Content,
    JsonValue? Details,
    Usage? Usage,
    long Timestamp) : ToolTranscriptItem(Id, ToolCallId, ToolName, Input, Content, Details, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "complete";

    /// <inheritdoc />
    public override bool IsError => false;
}

/// <summary>A tool that completed with an error.</summary>
public sealed record ErrorToolTranscriptItem(
    string Id,
    string ToolCallId,
    string ToolName,
    JsonValue Input,
    IReadOnlyList<Content> Content,
    JsonValue? Details,
    Usage? Usage,
    long Timestamp) : ToolTranscriptItem(Id, ToolCallId, ToolName, Input, Content, Details, Usage, Timestamp)
{
    /// <inheritdoc />
    public override string Status => "error";

    /// <inheritdoc />
    public override bool IsError => true;
}

/// <summary>Base type for transcript items.</summary>
public abstract record TranscriptItem(string Id, string Role, long Timestamp);

/// <summary>Base type for incremental session progress.</summary>
public abstract record TranscriptProgress
{
    /// <summary>The exact progress discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Signals that a transcript item started.</summary>
public sealed record ItemStartedProgress(TranscriptItem Item) : TranscriptProgress
{
    /// <inheritdoc />
    public override string Type => "item_started";
}

/// <summary>Signals a streamed assistant content delta.</summary>
public sealed record AssistantDeltaProgress(
    string MessageId,
    long ContentIndex,
    ContentKind Kind,
    string Delta) : TranscriptProgress
{
    /// <inheritdoc />
    public override string Type => "assistant_delta";
}

/// <summary>Signals that an assistant or tool item changed.</summary>
public sealed record ItemUpdatedProgress(TranscriptItem Item) : TranscriptProgress
{
    /// <inheritdoc />
    public override string Type => "item_updated";
}

/// <summary>Signals that an assistant or tool item reached a terminal state.</summary>
public sealed record ItemFinishedProgress(TranscriptItem Item) : TranscriptProgress
{
    /// <inheritdoc />
    public override string Type => "item_finished";
}

/// <summary>Durable metadata for a listed session.</summary>
public sealed record SessionMetadata(
    string Id,
    long CreatedAt,
    long? UpdatedAt = null,
    string? ParentSessionId = null,
    string? SessionName = null,
    string? Cwd = null);

/// <summary>Authoritative state for an acquired session.</summary>
public sealed record SessionSnapshot(
    string Id,
    string? Name,
    string Cwd,
    long CreatedAt,
    long UpdatedAt,
    SessionPhase Phase,
    ModelRef Model,
    ThinkingLevel ThinkingLevel,
    bool Attached,
    bool Locked,
    long Revision,
    IReadOnlyList<TranscriptItem> Transcript,
    IReadOnlyList<UserTranscriptItem> QueuedSteer,
    long QueuedSteerCount);

/// <summary>Authoritative server state advertised in a handshake or event.</summary>
public sealed record ServerSnapshot(
    string ServerId,
    int ProtocolVersion,
    long Revision,
    IReadOnlyList<SessionMetadata> Sessions,
    IReadOnlyList<ModelMetadata> Models);

/// <summary>A protocol error returned by a server.</summary>
public sealed record ProtocolError(ProtocolErrorCode Code, string Message, JsonValue? Details = null);

/// <summary>Base type for client commands.</summary>
public abstract record Command
{
    /// <summary>The exact command discriminator.</summary>
    public abstract string CommandName { get; }
}

/// <summary>Lists durable sessions.</summary>
public sealed record ListCommand : Command
{
    /// <inheritdoc />
    public override string CommandName => "list";
}

/// <summary>Creates a new session.</summary>
public sealed record CreateCommand(
    string? Cwd = null,
    string? Name = null,
    ModelRef? Model = null,
    ThinkingLevel? ThinkingLevel = null) : Command
{
    /// <inheritdoc />
    public override string CommandName => "create";
}

/// <summary>Attaches to a session.</summary>
public sealed record AttachCommand(string SessionId) : Command
{
    /// <inheritdoc />
    public override string CommandName => "attach";
}

/// <summary>Detaches from a session.</summary>
public sealed record DetachCommand(string SessionId) : Command
{
    /// <inheritdoc />
    public override string CommandName => "detach";
}

/// <summary>Sends a prompt to a session.</summary>
public sealed record PromptCommand(string SessionId, string Text) : Command
{
    /// <inheritdoc />
    public override string CommandName => "prompt";
}

/// <summary>Steers an active session.</summary>
public sealed record SteerCommand(string SessionId, string Text) : Command
{
    /// <inheritdoc />
    public override string CommandName => "steer";
}

/// <summary>Aborts a session.</summary>
public sealed record AbortCommand(string SessionId) : Command
{
    /// <inheritdoc />
    public override string CommandName => "abort";
}

/// <summary>Changes the current model.</summary>
public sealed record SetModelCommand(string SessionId, ModelRef Model) : Command
{
    /// <inheritdoc />
    public override string CommandName => "set_model";
}

/// <summary>Changes the current thinking level.</summary>
public sealed record SetThinkingCommand(string SessionId, ThinkingLevel ThinkingLevel) : Command
{
    /// <inheritdoc />
    public override string CommandName => "set_thinking";
}

/// <summary>Creates a session request result.</summary>
public sealed record CreateResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "create";
}

/// <summary>Attaches to a session result.</summary>
public sealed record AttachResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "attach";
}

/// <summary>Prompt request result.</summary>
public sealed record PromptResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "prompt";
}

/// <summary>Steer request result.</summary>
public sealed record SteerResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "steer";
}

/// <summary>Abort request result.</summary>
public sealed record AbortResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "abort";
}

/// <summary>Set-model request result.</summary>
public sealed record SetModelResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "set_model";
}

/// <summary>Set-thinking request result.</summary>
public sealed record SetThinkingResult(SessionSnapshot Session) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "set_thinking";
}

/// <summary>List request result.</summary>
public sealed record ListResult(IReadOnlyList<SessionMetadata> Sessions) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "list";
}

/// <summary>Detach request result.</summary>
public sealed record DetachResult(string SessionId) : CommandResult
{
    /// <inheritdoc />
    public override string CommandName => "detach";
}

/// <summary>Base type for command results.</summary>
public abstract record CommandResult
{
    /// <summary>The exact command discriminator.</summary>
    public abstract string CommandName { get; }
}

/// <summary>Client hello message.</summary>
public sealed record ClientHello(long Version) : ClientMessage
{
    /// <inheritdoc />
    public override string Type => "hello";
}

/// <summary>A correlated client request envelope.</summary>
public sealed record RequestEnvelope(string Id, Command Request) : ClientMessage
{
    /// <inheritdoc />
    public override string Type => "request";
}

/// <summary>Base type for client messages.</summary>
public abstract record ClientMessage
{
    /// <summary>The exact client message discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Server hello message.</summary>
public sealed record ServerHello(int Version, string ConnectionId, ServerSnapshot Snapshot) : ServerMessage
{
    /// <inheritdoc />
    public override string Type => "hello";
}

/// <summary>Server hello error message.</summary>
public sealed record ServerHelloError(ProtocolError Error) : ServerMessage
{
    /// <inheritdoc />
    public override string Type => "hello_error";
}

/// <summary>A successful or failed response envelope.</summary>
public sealed record ResponseEnvelope(string Id, bool Ok, CommandResult? Result = null, ProtocolError? Error = null) : ServerMessage
{
    /// <inheritdoc />
    public override string Type => "response";
}

/// <summary>A server event envelope.</summary>
public sealed record EventEnvelope(ServerEvent Event) : ServerMessage
{
    /// <inheritdoc />
    public override string Type => "event";
}

/// <summary>Base type for server messages.</summary>
public abstract record ServerMessage
{
    /// <summary>The exact server message discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Base type for server events.</summary>
public abstract record ServerEvent
{
    /// <summary>The exact server event discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Authoritative server snapshot event.</summary>
public sealed record ServerSnapshotEvent(ServerSnapshot Snapshot) : ServerEvent
{
    /// <inheritdoc />
    public override string Type => "server_snapshot";
}

/// <summary>Authoritative session snapshot event.</summary>
public sealed record SessionSnapshotEvent(SessionSnapshot Snapshot) : ServerEvent
{
    /// <inheritdoc />
    public override string Type => "session_snapshot";
}

/// <summary>Incremental session progress event.</summary>
public sealed record SessionProgressEvent(string SessionId, TranscriptProgress Progress) : ServerEvent
{
    /// <inheritdoc />
    public override string Type => "session_progress";
}

/// <summary>Session removal event.</summary>
public sealed record SessionRemovedEvent(string SessionId) : ServerEvent
{
    /// <inheritdoc />
    public override string Type => "session_removed";
}
