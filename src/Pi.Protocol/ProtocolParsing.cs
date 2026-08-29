using System.Collections;
using System.Globalization;

namespace Pi.Protocol;

internal static class ProtocolParsing
{
    public static ClientMessage ParseClientMessage(object? value)
    {
        RequireProtocolValue(value);
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string type = RequiredString(map, "type");
        return type switch
        {
            "hello" => ParseClientHello(map),
            "request" => ParseRequestEnvelope(map),
            _ => Invalid<ClientMessage>("Invalid client protocol message"),
        };
    }

    public static ServerMessage ParseServerMessage(object? value)
    {
        RequireProtocolValue(value);
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string type = RequiredString(map, "type");
        return type switch
        {
            "hello" => ParseServerHello(map),
            "hello_error" => ParseServerHelloError(map),
            "response" => ParseResponseEnvelope(map),
            "event" => ParseEventEnvelope(map),
            _ => Invalid<ServerMessage>("Invalid server protocol message"),
        };
    }

    private static ClientHello ParseClientHello(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "version");
        return new ClientHello(RequiredInteger(map, "version"));
    }

    private static RequestEnvelope ParseRequestEnvelope(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "id", "request");
        return new RequestEnvelope(RequiredId(map, "id"), ParseCommand(RequiredValue(map, "request")));
    }

    private static ServerHello ParseServerHello(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "version", "connectionId", "snapshot");
        int version = checked((int)RequiredInteger(map, "version"));
        if (version != ProtocolConstants.ProtocolVersion)
        {
            throw Invalid("Invalid server protocol message");
        }

        return new ServerHello(version, RequiredId(map, "connectionId"), ParseServerSnapshot(RequiredValue(map, "snapshot")));
    }

    private static ServerHelloError ParseServerHelloError(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "error");
        return new ServerHelloError(ParseProtocolError(RequiredValue(map, "error")));
    }

    private static ResponseEnvelope ParseResponseEnvelope(IReadOnlyDictionary<string, object?> map)
    {
        string[] common = ["type", "id", "ok", "result", "error"];
        EnsureKeys(map, common);
        string id = RequiredId(map, "id");
        bool ok = RequiredBoolean(map, "ok");
        if (ok)
        {
            if (!map.TryGetValue("result", out object? result) || result is null)
            {
                throw Invalid("Invalid server protocol message");
            }

            if (map.ContainsKey("error"))
            {
                throw Invalid("Invalid server protocol message");
            }

            return new ResponseEnvelope(id, true, ParseCommandResult(result));
        }

        if (!map.TryGetValue("error", out object? error) || error is null)
        {
            throw Invalid("Invalid server protocol message");
        }

        if (map.ContainsKey("result"))
        {
            throw Invalid("Invalid server protocol message");
        }

        return new ResponseEnvelope(id, false, Error: ParseProtocolError(error));
    }

    private static EventEnvelope ParseEventEnvelope(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "event");
        return new EventEnvelope(ParseServerEvent(RequiredValue(map, "event")));
    }

    private static Command ParseCommand(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string command = RequiredString(map, "command");
        return command switch
        {
            "list" => ParseListCommand(map),
            "create" => ParseCreateCommand(map),
            "attach" => ParseAttachCommand(map),
            "detach" => ParseDetachCommand(map),
            "prompt" => ParsePromptCommand(map),
            "steer" => ParseSteerCommand(map),
            "abort" => ParseAbortCommand(map),
            "set_model" => ParseSetModelCommand(map),
            "set_thinking" => ParseSetThinkingCommand(map),
            _ => Invalid<Command>("Invalid client protocol message"),
        };
    }

    private static ListCommand ParseListCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command");
        return new ListCommand();
    }

    private static CreateCommand ParseCreateCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "cwd", "name", "model", "thinkingLevel");
        return new CreateCommand(
            OptionalString(map, "cwd", minimumLength: 1),
            OptionalString(map, "name"),
            Optional(map, "model", ParseModelRef),
            OptionalStruct(map, "thinkingLevel", ParseThinkingLevel));
    }

    private static AttachCommand ParseAttachCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId");
        return new AttachCommand(RequiredId(map, "sessionId"));
    }

    private static DetachCommand ParseDetachCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId");
        return new DetachCommand(RequiredId(map, "sessionId"));
    }

    private static PromptCommand ParsePromptCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId", "text");
        return new PromptCommand(RequiredId(map, "sessionId"), RequiredString(map, "text"));
    }

    private static SteerCommand ParseSteerCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId", "text");
        return new SteerCommand(RequiredId(map, "sessionId"), RequiredString(map, "text"));
    }

    private static AbortCommand ParseAbortCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId");
        return new AbortCommand(RequiredId(map, "sessionId"));
    }

    private static SetModelCommand ParseSetModelCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId", "model");
        return new SetModelCommand(RequiredId(map, "sessionId"), ParseModelRef(RequiredValue(map, "model")));
    }

    private static SetThinkingCommand ParseSetThinkingCommand(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId", "thinkingLevel");
        return new SetThinkingCommand(RequiredId(map, "sessionId"), ParseThinkingLevel(RequiredValue(map, "thinkingLevel")));
    }

    private static CommandResult ParseCommandResult(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string command = RequiredString(map, "command");
        return command switch
        {
            "list" => ParseListResult(map),
            "create" => ParseCreateResult(map),
            "attach" => ParseAttachResult(map),
            "detach" => ParseDetachResult(map),
            "prompt" => ParsePromptResult(map),
            "steer" => ParseSteerResult(map),
            "abort" => ParseAbortResult(map),
            "set_model" => ParseSetModelResult(map),
            "set_thinking" => ParseSetThinkingResult(map),
            _ => Invalid<CommandResult>("Invalid server protocol message"),
        };
    }

    private static ListResult ParseListResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessions");
        return new ListResult(ParseList(map, "sessions", ParseSessionMetadata));
    }

    private static CreateResult ParseCreateResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new CreateResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static AttachResult ParseAttachResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new AttachResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static DetachResult ParseDetachResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "sessionId");
        return new DetachResult(RequiredId(map, "sessionId"));
    }

    private static PromptResult ParsePromptResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new PromptResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static SteerResult ParseSteerResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new SteerResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static AbortResult ParseAbortResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new AbortResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static SetModelResult ParseSetModelResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new SetModelResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static SetThinkingResult ParseSetThinkingResult(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "command", "session");
        return new SetThinkingResult(ParseSessionSnapshot(RequiredValue(map, "session")));
    }

    private static ModelRef ParseModelRef(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "provider", "id");
        return new ModelRef(RequiredId(map, "provider"), RequiredId(map, "id"));
    }

    private static ModelCost ParseModelCost(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "input", "output", "cacheRead", "cacheWrite");
        return new ModelCost(
            RequiredNumber(map, "input", minimum: 0),
            RequiredNumber(map, "output", minimum: 0),
            RequiredNumber(map, "cacheRead", minimum: 0),
            RequiredNumber(map, "cacheWrite", minimum: 0));
    }

    private static ModelMetadata ParseModelMetadata(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "provider", "id", "name", "api", "reasoning", "input", "contextWindow", "maxTokens", "cost", "supportedThinkingLevels", "authenticated");
        return new ModelMetadata(
            RequiredId(map, "provider"),
            RequiredId(map, "id"),
            RequiredString(map, "name", minimumLength: 1),
            RequiredId(map, "api"),
            RequiredBoolean(map, "reasoning"),
            ParseList(map, "input", ParseModelInputKind),
            RequiredInteger(map, "contextWindow", minimum: 1),
            RequiredInteger(map, "maxTokens", minimum: 1),
            ParseModelCost(RequiredValue(map, "cost")),
            ParseList(map, "supportedThinkingLevels", ParseThinkingLevel, minimumCount: 1),
            RequiredBoolean(map, "authenticated"));
    }

    private static Usage ParseUsage(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "input", "output", "cacheRead", "cacheWrite", "reasoning", "totalTokens", "cost");
        return new Usage(
            RequiredInteger(map, "input", minimum: 0),
            RequiredInteger(map, "output", minimum: 0),
            RequiredInteger(map, "cacheRead", minimum: 0),
            RequiredInteger(map, "cacheWrite", minimum: 0),
            OptionalStruct(map, "reasoning", valueToParse => ParseInteger(valueToParse, "reasoning", 0)),
            RequiredInteger(map, "totalTokens", minimum: 0),
            ParseUsageCost(RequiredValue(map, "cost")));
    }

    private static UsageCost ParseUsageCost(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "input", "output", "cacheRead", "cacheWrite", "total");
        return new UsageCost(
            RequiredNumber(map, "input", minimum: 0),
            RequiredNumber(map, "output", minimum: 0),
            RequiredNumber(map, "cacheRead", minimum: 0),
            RequiredNumber(map, "cacheWrite", minimum: 0),
            RequiredNumber(map, "total", minimum: 0));
    }

    private static Content ParseContent(object? value, ContentSet allowed)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string type = RequiredString(map, "type");
        return type switch
        {
            "text" when allowed.HasFlag(ContentSet.Text) => ParseTextContent(map),
            "thinking" when allowed.HasFlag(ContentSet.Thinking) => ParseThinkingContent(map),
            "image" when allowed.HasFlag(ContentSet.Image) => ParseImageContent(map),
            "toolCall" when allowed.HasFlag(ContentSet.ToolCall) => ParseToolCallContent(map),
            _ => Invalid<Content>("Invalid protocol content"),
        };
    }

    private static TextContent ParseTextContent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "text");
        return new TextContent(RequiredString(map, "text"));
    }

    private static ThinkingContent ParseThinkingContent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "thinking", "redacted");
        return new ThinkingContent(RequiredString(map, "thinking"), OptionalBoolean(map, "redacted"));
    }

    private static ImageContent ParseImageContent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "data", "mimeType");
        return new ImageContent(RequiredString(map, "data"), RequiredString(map, "mimeType", minimumLength: 1));
    }

    private static ToolCallContent ParseToolCallContent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "toolCallId", "toolName", "input");
        return new ToolCallContent(
            RequiredId(map, "toolCallId"),
            RequiredId(map, "toolName"),
            ParseRequiredJsonValue(map, "input"));
    }

    private static UserTranscriptItem ParseUserTranscriptItem(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "id", "role", "content", "timestamp");
        RequireLiteral(map, "role", "user");
        return new UserTranscriptItem(
            RequiredId(map, "id"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Image)),
            RequiredInteger(map, "timestamp", minimum: 0));
    }

    private static AssistantTranscriptItem ParseAssistantTranscriptItem(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string status = RequiredString(map, "status");
        return status switch
        {
            "streaming" => ParseStreamingAssistant(map),
            "complete" => ParseCompleteAssistant(map),
            "error" => ParseErrorAssistant(map),
            "aborted" => ParseAbortedAssistant(map),
            _ => Invalid<AssistantTranscriptItem>("Invalid assistant transcript item"),
        };
    }

    private static StreamingAssistantTranscriptItem ParseStreamingAssistant(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "content", "model", "responseModel", "usage", "timestamp", "status");
        RequireLiteral(map, "role", "assistant");
        return new StreamingAssistantTranscriptItem(
            RequiredId(map, "id"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Thinking | ContentSet.ToolCall)),
            ParseModelRef(RequiredValue(map, "model")),
            OptionalString(map, "responseModel", minimumLength: 1),
            Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0));
    }

    private static CompleteAssistantTranscriptItem ParseCompleteAssistant(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "content", "model", "responseModel", "usage", "timestamp", "status", "stopReason");
        RequireLiteral(map, "role", "assistant");
        return new CompleteAssistantTranscriptItem(
            RequiredId(map, "id"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Thinking | ContentSet.ToolCall)),
            ParseModelRef(RequiredValue(map, "model")),
            OptionalString(map, "responseModel", minimumLength: 1),
            Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0),
            ParseCompleteStopReason(RequiredValue(map, "stopReason")));
    }

    private static ErrorAssistantTranscriptItem ParseErrorAssistant(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "content", "model", "responseModel", "usage", "timestamp", "status", "stopReason", "errorMessage");
        RequireLiteral(map, "role", "assistant");
        RequireLiteral(map, "stopReason", "error");
        return new ErrorAssistantTranscriptItem(
            RequiredId(map, "id"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Thinking | ContentSet.ToolCall)),
            ParseModelRef(RequiredValue(map, "model")),
            OptionalString(map, "responseModel", minimumLength: 1),
            Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0),
            OptionalString(map, "errorMessage", minimumLength: 1));
    }

    private static AbortedAssistantTranscriptItem ParseAbortedAssistant(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "content", "model", "responseModel", "usage", "timestamp", "status", "stopReason", "errorMessage");
        RequireLiteral(map, "role", "assistant");
        RequireLiteral(map, "stopReason", "aborted");
        return new AbortedAssistantTranscriptItem(
            RequiredId(map, "id"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Thinking | ContentSet.ToolCall)),
            ParseModelRef(RequiredValue(map, "model")),
            OptionalString(map, "responseModel", minimumLength: 1),
            Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0),
            OptionalString(map, "errorMessage"));
    }

    private static ToolTranscriptItem ParseToolTranscriptItem(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string status = RequiredString(map, "status");
        return status switch
        {
            "running" => ParseRunningTool(map),
            "complete" => ParseCompleteTool(map),
            "error" => ParseErrorTool(map),
            _ => Invalid<ToolTranscriptItem>("Invalid tool transcript item"),
        };
    }

    private static RunningToolTranscriptItem ParseRunningTool(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "toolCallId", "toolName", "input", "content", "details", "usage", "timestamp", "status", "isError");
        RequireLiteral(map, "role", "tool");
        RequireLiteral(map, "isError", false);
        return new RunningToolTranscriptItem(
            RequiredId(map, "id"), RequiredId(map, "toolCallId"), RequiredId(map, "toolName"),
            ParseRequiredJsonValue(map, "input"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Image)),
            Optional(map, "details", ParseJsonValue), Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0));
    }

    private static CompleteToolTranscriptItem ParseCompleteTool(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "toolCallId", "toolName", "input", "content", "details", "usage", "timestamp", "status", "isError");
        RequireLiteral(map, "role", "tool");
        RequireLiteral(map, "isError", false);
        return new CompleteToolTranscriptItem(
            RequiredId(map, "id"), RequiredId(map, "toolCallId"), RequiredId(map, "toolName"),
            ParseRequiredJsonValue(map, "input"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Image)),
            Optional(map, "details", ParseJsonValue), Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0));
    }

    private static ErrorToolTranscriptItem ParseErrorTool(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "id", "role", "toolCallId", "toolName", "input", "content", "details", "usage", "timestamp", "status", "isError");
        RequireLiteral(map, "role", "tool");
        RequireLiteral(map, "isError", true);
        return new ErrorToolTranscriptItem(
            RequiredId(map, "id"), RequiredId(map, "toolCallId"), RequiredId(map, "toolName"),
            ParseRequiredJsonValue(map, "input"),
            ParseList(map, "content", item => ParseContent(item, ContentSet.Text | ContentSet.Image)),
            Optional(map, "details", ParseJsonValue), Optional(map, "usage", ParseUsage),
            RequiredInteger(map, "timestamp", minimum: 0));
    }

    private static TranscriptItem ParseTranscriptItem(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string role = RequiredString(map, "role");
        return role switch
        {
            "user" => ParseUserTranscriptItem(map),
            "assistant" => ParseAssistantTranscriptItem(map),
            "tool" => ParseToolTranscriptItem(map),
            _ => Invalid<TranscriptItem>("Invalid transcript item"),
        };
    }

    private static TranscriptProgress ParseTranscriptProgress(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string type = RequiredString(map, "type");
        return type switch
        {
            "item_started" => ParseItemStarted(map),
            "assistant_delta" => ParseAssistantDelta(map),
            "item_updated" => ParseItemUpdated(map),
            "item_finished" => ParseItemFinished(map),
            _ => Invalid<TranscriptProgress>("Invalid transcript progress"),
        };
    }

    private static ItemStartedProgress ParseItemStarted(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "item");
        return new ItemStartedProgress(ParseTranscriptItem(RequiredValue(map, "item")));
    }

    private static AssistantDeltaProgress ParseAssistantDelta(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "messageId", "contentIndex", "kind", "delta");
        return new AssistantDeltaProgress(
            RequiredId(map, "messageId"), RequiredInteger(map, "contentIndex", minimum: 0),
            ParseContentKind(RequiredValue(map, "kind")), RequiredString(map, "delta"));
    }

    private static ItemUpdatedProgress ParseItemUpdated(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "item");
        TranscriptItem item = ParseTranscriptItem(RequiredValue(map, "item"));
        if (item is not AssistantTranscriptItem and not ToolTranscriptItem)
        {
            throw Invalid("Invalid transcript progress");
        }

        return new ItemUpdatedProgress(item);
    }

    private static ItemFinishedProgress ParseItemFinished(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "item");
        TranscriptItem item = ParseTranscriptItem(RequiredValue(map, "item"));
        if (item is StreamingAssistantTranscriptItem or RunningToolTranscriptItem or UserTranscriptItem)
        {
            throw Invalid("Invalid transcript progress");
        }

        return new ItemFinishedProgress(item);
    }

    private static SessionMetadata ParseSessionMetadata(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "id", "createdAt", "updatedAt", "parentSessionId", "sessionName", "cwd");
        return new SessionMetadata(
            RequiredId(map, "id"),
            RequiredInteger(map, "createdAt", minimum: 0),
            OptionalStruct(map, "updatedAt", valueToParse => ParseInteger(valueToParse, "updatedAt", 0)),
            OptionalString(map, "parentSessionId", minimumLength: 1),
            OptionalString(map, "sessionName"),
            OptionalString(map, "cwd", minimumLength: 1));
    }

    private static SessionSnapshot ParseSessionSnapshot(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "id", "name", "cwd", "createdAt", "updatedAt", "phase", "model", "thinkingLevel", "attached", "locked", "revision", "transcript", "queuedSteer", "queuedSteerCount");
        return new SessionSnapshot(
            RequiredId(map, "id"),
            OptionalString(map, "name"),
            RequiredString(map, "cwd", minimumLength: 1),
            RequiredInteger(map, "createdAt", minimum: 0),
            RequiredInteger(map, "updatedAt", minimum: 0),
            ParseSessionPhase(RequiredValue(map, "phase")),
            ParseModelRef(RequiredValue(map, "model")),
            ParseThinkingLevel(RequiredValue(map, "thinkingLevel")),
            RequiredBoolean(map, "attached"),
            RequiredBoolean(map, "locked"),
            RequiredInteger(map, "revision", minimum: 0),
            ParseList(map, "transcript", ParseTranscriptItem),
            ParseList(map, "queuedSteer", ParseUserTranscriptItem),
            RequiredInteger(map, "queuedSteerCount", minimum: 0));
    }

    private static ServerSnapshot ParseServerSnapshot(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "serverId", "protocolVersion", "revision", "sessions", "models");
        int protocolVersion = checked((int)RequiredInteger(map, "protocolVersion"));
        if (protocolVersion != ProtocolConstants.ProtocolVersion)
        {
            throw Invalid("Invalid server protocol message");
        }

        return new ServerSnapshot(
            RequiredId(map, "serverId"), protocolVersion,
            RequiredInteger(map, "revision", minimum: 0),
            ParseList(map, "sessions", ParseSessionMetadata),
            ParseList(map, "models", ParseModelMetadata));
    }

    private static ProtocolError ParseProtocolError(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        EnsureKeys(map, "code", "message", "details");
        return new ProtocolError(
            ParseProtocolErrorCode(RequiredValue(map, "code")),
            RequiredString(map, "message"),
            Optional(map, "details", ParseJsonValue));
    }

    private static ServerEvent ParseServerEvent(object? value)
    {
        IReadOnlyDictionary<string, object?> map = RequireMap(value);
        string type = RequiredString(map, "type");
        return type switch
        {
            "server_snapshot" => ParseServerSnapshotEvent(map),
            "session_snapshot" => ParseSessionSnapshotEvent(map),
            "session_progress" => ParseSessionProgressEvent(map),
            "session_removed" => ParseSessionRemovedEvent(map),
            _ => Invalid<ServerEvent>("Invalid server event"),
        };
    }

    private static ServerSnapshotEvent ParseServerSnapshotEvent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "snapshot");
        return new ServerSnapshotEvent(ParseServerSnapshot(RequiredValue(map, "snapshot")));
    }

    private static SessionSnapshotEvent ParseSessionSnapshotEvent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "snapshot");
        return new SessionSnapshotEvent(ParseSessionSnapshot(RequiredValue(map, "snapshot")));
    }

    private static SessionProgressEvent ParseSessionProgressEvent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "sessionId", "progress");
        return new SessionProgressEvent(RequiredId(map, "sessionId"), ParseTranscriptProgress(RequiredValue(map, "progress")));
    }

    private static SessionRemovedEvent ParseSessionRemovedEvent(IReadOnlyDictionary<string, object?> map)
    {
        EnsureKeys(map, "type", "sessionId");
        return new SessionRemovedEvent(RequiredId(map, "sessionId"));
    }

    private static JsonValue ParseJsonValue(object? value)
    {
        switch (value)
        {
            case null:
                return new JsonValue.JsonNull();
            case JsonValue json:
                return json;
            case bool boolean:
                return new JsonValue.JsonBoolean(boolean);
            case string text:
                return new JsonValue.JsonString(text);
            case byte[]:
                throw Invalid("Invalid JSON value");
        }

        if (TryNumber(value, out double number))
        {
            if (!double.IsFinite(number))
            {
                throw Invalid("Invalid JSON value");
            }

            return new JsonValue.JsonNumber(number);
        }

        if (TryGetList(value, out IReadOnlyList<object?>? list))
        {
            return new JsonValue.JsonArray(list!.Select(ParseJsonValue).ToArray());
        }

        if (TryGetMap(value, out IReadOnlyDictionary<string, object?>? map))
        {
            Dictionary<string, JsonValue> result = new(StringComparer.Ordinal);
            foreach ((string key, object? item) in map!)
            {
                result.Add(key, ParseJsonValue(item));
            }

            return new JsonValue.JsonObject(result);
        }

        throw Invalid("Invalid JSON value");
    }

    private static ThinkingLevel ParseThinkingLevel(object? value) => RequiredEnum(value, "thinking level", new Dictionary<string, ThinkingLevel>(StringComparer.Ordinal)
    {
        ["off"] = ThinkingLevel.Off,
        ["minimal"] = ThinkingLevel.Minimal,
        ["low"] = ThinkingLevel.Low,
        ["medium"] = ThinkingLevel.Medium,
        ["high"] = ThinkingLevel.High,
        ["xhigh"] = ThinkingLevel.Xhigh,
        ["max"] = ThinkingLevel.Max,
    });

    private static SessionPhase ParseSessionPhase(object? value) => RequiredEnum(value, "session phase", new Dictionary<string, SessionPhase>(StringComparer.Ordinal)
    {
        ["idle"] = SessionPhase.Idle,
        ["turn"] = SessionPhase.Turn,
        ["compaction"] = SessionPhase.Compaction,
        ["branch_summary"] = SessionPhase.BranchSummary,
        ["retry"] = SessionPhase.Retry,
    });

    private static ModelInputKind ParseModelInputKind(object? value) => RequiredEnum(value, "model input kind", new Dictionary<string, ModelInputKind>(StringComparer.Ordinal)
    {
        ["text"] = ModelInputKind.Text,
        ["image"] = ModelInputKind.Image,
    });

    private static ContentKind ParseContentKind(object? value) => RequiredEnum(value, "content kind", new Dictionary<string, ContentKind>(StringComparer.Ordinal)
    {
        ["text"] = ContentKind.Text,
        ["thinking"] = ContentKind.Thinking,
        ["toolCall"] = ContentKind.ToolCall,
    });

    private static TranscriptStopReason ParseCompleteStopReason(object? value) => RequiredEnum(value, "stop reason", new Dictionary<string, TranscriptStopReason>(StringComparer.Ordinal)
    {
        ["stop"] = TranscriptStopReason.Stop,
        ["length"] = TranscriptStopReason.Length,
        ["toolUse"] = TranscriptStopReason.ToolUse,
    });

    private static ProtocolErrorCode ParseProtocolErrorCode(object? value) => RequiredEnum(value, "protocol error code", new Dictionary<string, ProtocolErrorCode>(StringComparer.Ordinal)
    {
        ["version"] = ProtocolErrorCode.Version,
        ["busy"] = ProtocolErrorCode.Busy,
        ["session_locked"] = ProtocolErrorCode.SessionLocked,
        ["not_found"] = ProtocolErrorCode.NotFound,
        ["invalid_request"] = ProtocolErrorCode.InvalidRequest,
        ["not_implemented"] = ProtocolErrorCode.NotImplemented,
        ["internal_error"] = ProtocolErrorCode.InternalError,
    });

    private static TEnum RequiredEnum<TEnum>(object? value, string name, IReadOnlyDictionary<string, TEnum> values)
    {
        if (value is string text && values.ContainsKey(text))
        {
            return values[text];
        }

        throw Invalid($"Invalid {name}");
    }

    private static long ParseInteger(object? value, string name, long minimum)
    {
        if (!TryNumber(value, out double number) || !double.IsFinite(number) || Math.Truncate(number) != number || number < minimum || number > 9_007_199_254_740_991d)
        {
            throw Invalid($"Invalid {name}");
        }

        return checked((long)number);
    }

    private static T[] ParseList<T>(IReadOnlyDictionary<string, object?> map, string key, Func<object?, T> parser, int minimumCount = 0)
    {
        if (!map.TryGetValue(key, out object? value) || !TryGetList(value, out IReadOnlyList<object?>? values))
        {
            throw Invalid("Invalid protocol array");
        }

        if (values!.Count < minimumCount)
        {
            throw Invalid("Invalid protocol array");
        }

        return values.Select(parser).ToArray();
    }

    private static T? Optional<T>(IReadOnlyDictionary<string, object?> map, string key, Func<object?, T> parser) where T : class
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        return parser(value);
    }

    private static T? OptionalStruct<T>(IReadOnlyDictionary<string, object?> map, string key, Func<object?, T> parser) where T : struct
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        return parser(value);
    }

    private static bool? OptionalBoolean(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        throw Invalid("Invalid protocol boolean");
    }

    private static string? OptionalString(IReadOnlyDictionary<string, object?> map, string key, int minimumLength = 0)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (value is string text && text.Length >= minimumLength)
        {
            return text;
        }

        throw Invalid("Invalid protocol string");
    }

    private static string RequiredId(IReadOnlyDictionary<string, object?> map, string key) => RequiredString(map, key, minimumLength: 1);

    private static string RequiredString(IReadOnlyDictionary<string, object?> map, string key, int minimumLength = 0)
    {
        if (map.TryGetValue(key, out object? value) && value is string text && text.Length >= minimumLength)
        {
            return text;
        }

        throw Invalid("Invalid protocol string");
    }

    private static long RequiredInteger(IReadOnlyDictionary<string, object?> map, string key, long minimum = long.MinValue) =>
        ParseInteger(RequiredValue(map, key), key, minimum);

    private static double RequiredNumber(IReadOnlyDictionary<string, object?> map, string key, double minimum)
    {
        if (!TryNumber(RequiredValue(map, key), out double value) || !double.IsFinite(value) || value < minimum)
        {
            throw Invalid("Invalid protocol number");
        }

        return value;
    }

    private static bool RequiredBoolean(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (map.TryGetValue(key, out object? value) && value is bool boolean)
        {
            return boolean;
        }

        throw Invalid("Invalid protocol boolean");
    }

    private static object? RequiredValue(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (map.TryGetValue(key, out object? value) && value is not null)
        {
            return value;
        }

        throw Invalid("Invalid protocol message");
    }

    private static void RequireLiteral(IReadOnlyDictionary<string, object?> map, string key, object expected)
    {
        if (!map.TryGetValue(key, out object? value) || !Equals(value, expected))
        {
            throw Invalid("Invalid protocol message");
        }
    }

    private static void EnsureKeys(IReadOnlyDictionary<string, object?> map, params string[] allowed)
    {
        HashSet<string> keys = allowed.ToHashSet(StringComparer.Ordinal);
        if (map.Keys.Any(key => !keys.Contains(key)))
        {
            throw Invalid("Invalid protocol message");
        }
    }

    private static void RequireProtocolValue(object? value)
    {
        HashSet<object> ancestors = new(ReferenceEqualityComparer.Instance);
        if (!IsProtocolValue(value, false, ancestors))
        {
            throw Invalid("Invalid protocol message");
        }
    }

    private static bool IsProtocolValue(object? value, bool optionalProperty, HashSet<object> ancestors)
    {
        if (value is null || value is bool or string)
        {
            return true;
        }

        if (value is byte[] || value is ReadOnlyMemory<byte> || value is JsonValue)
        {
            return false;
        }

        if (TryNumber(value, out double number))
        {
            return double.IsFinite(number);
        }

        if (TryGetList(value, out IReadOnlyList<object?>? list))
        {
            if (!ancestors.Add(value))
            {
                return false;
            }

            try
            {
                return list!.All(item => IsProtocolValue(item, false, ancestors));
            }
            finally
            {
                ancestors.Remove(value);
            }
        }

        if (TryGetMap(value, out IReadOnlyDictionary<string, object?>? map))
        {
            if (!ancestors.Add(value))
            {
                return false;
            }

            try
            {
                return map!.Values.All(item => IsProtocolValue(item, true, ancestors));
            }
            finally
            {
                ancestors.Remove(value);
            }
        }

        return false;
    }

    private static JsonValue ParseRequiredJsonValue(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            throw Invalid($"Missing required property '{key}'.");
        }

        return ParseJsonValue(value);
    }

    private static IReadOnlyDictionary<string, object?> RequireMap(object? value)
    {
        if (TryGetMap(value, out IReadOnlyDictionary<string, object?>? map))
        {
            return map!;
        }

        throw Invalid("Invalid protocol object");
    }

    private static bool TryGetMap(object? value, out IReadOnlyDictionary<string, object?>? map)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> readOnly:
                map = readOnly;
                return true;
            case IDictionary<string, object?> dictionary:
                map = new Dictionary<string, object?>(dictionary, StringComparer.Ordinal);
                return true;
            case OrderedMap ordered:
                Dictionary<string, object?> result = new(StringComparer.Ordinal);
                foreach ((string key, object? item) in ordered)
                {
                    if (!result.TryAdd(key, item))
                    {
                        map = null;
                        return false;
                    }
                }

                map = result;
                return true;
            default:
                map = null;
                return false;
        }
    }

    private static bool TryGetList(object? value, out IReadOnlyList<object?>? list)
    {
        switch (value)
        {
            case IReadOnlyList<object?> readOnly:
                list = readOnly;
                return true;
            case IList nonGeneric:
                object?[] values = new object?[nonGeneric.Count];
                for (int index = 0; index < nonGeneric.Count; index++)
                {
                    values[index] = nonGeneric[index];
                }

                list = values;
                return true;
            default:
                list = null;
                return false;
        }
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case byte v:
                number = v;
                return true;
            case sbyte v:
                number = v;
                return true;
            case ushort v:
                number = v;
                return true;
            case short v:
                number = v;
                return true;
            case uint v:
                number = v;
                return true;
            case int v:
                number = v;
                return true;
            case ulong v:
                number = v;
                return true;
            case long v:
                number = v;
                return true;
            case float v:
                number = v;
                return true;
            case double v:
                number = v;
                return true;
            case decimal v:
                number = (double)v;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static T Invalid<T>(string message) => throw new ProtocolValidationError(message);

    private static ProtocolValidationError Invalid(string message) => new(message);

    [Flags]
    private enum ContentSet
    {
        Text = 1,
        Thinking = 2,
        Image = 4,
        ToolCall = 8,
    }
}
