namespace Pi.Protocol;

internal static class ProtocolWire
{
    public static OrderedMap ToWire(ClientMessage message) => message switch
    {
        ClientHello hello => Map(("type", "hello"), ("version", hello.Version)),
        RequestEnvelope request => Map(
            ("type", "request"),
            ("id", request.Id),
            ("request", ToWire(request.Request))),
        _ => throw new ProtocolValidationError("Invalid client protocol message"),
    };

    public static OrderedMap ToWire(ServerMessage message) => message switch
    {
        ServerHello hello => Map(
            ("type", "hello"),
            ("version", hello.Version),
            ("connectionId", hello.ConnectionId),
            ("snapshot", ToWire(hello.Snapshot))),
        ServerHelloError error => Map(("type", "hello_error"), ("error", ToWire(error.Error))),
        ResponseEnvelope response => ToWire(response),
        EventEnvelope envelope => Map(("type", "event"), ("event", ToWire(envelope.Event))),
        _ => throw new ProtocolValidationError("Invalid server protocol message"),
    };

    private static OrderedMap ToWire(ResponseEnvelope response)
    {
        OrderedMap map = Map(("type", "response"), ("id", response.Id), ("ok", response.Ok));
        if (response.Ok)
        {
            map.Add("result", response.Result is null ? null : ToWire(response.Result));
            if (response.Error is not null)
            {
                map.Add("error", ToWire(response.Error));
            }
        }
        else
        {
            map.Add("error", response.Error is null ? null : ToWire(response.Error));
            if (response.Result is not null)
            {
                map.Add("result", ToWire(response.Result));
            }
        }

        return map;
    }

    private static OrderedMap ToWire(Command command)
    {
        return command switch
        {
            ListCommand => Map(("command", "list")),
            CreateCommand create => MapOptional(
                ("command", "create"),
                ("cwd", create.Cwd),
                ("name", create.Name),
                ("model", create.Model is null ? null : ToWire(create.Model)),
                ("thinkingLevel", create.ThinkingLevel is null ? null : ToWire(create.ThinkingLevel.Value))),
            AttachCommand attach => Map(("command", "attach"), ("sessionId", attach.SessionId)),
            DetachCommand detach => Map(("command", "detach"), ("sessionId", detach.SessionId)),
            PromptCommand prompt => Map(("command", "prompt"), ("sessionId", prompt.SessionId), ("text", prompt.Text)),
            SteerCommand steer => Map(("command", "steer"), ("sessionId", steer.SessionId), ("text", steer.Text)),
            AbortCommand abort => Map(("command", "abort"), ("sessionId", abort.SessionId)),
            SetModelCommand setModel => Map(
                ("command", "set_model"),
                ("sessionId", setModel.SessionId),
                ("model", ToWire(setModel.Model))),
            SetThinkingCommand setThinking => Map(
                ("command", "set_thinking"),
                ("sessionId", setThinking.SessionId),
                ("thinkingLevel", ToWire(setThinking.ThinkingLevel))),
            _ => throw new ProtocolValidationError("Invalid client protocol command"),
        };
    }

    private static OrderedMap ToWire(CommandResult result)
    {
        return result switch
        {
            ListResult list => Map(("command", "list"), ("sessions", ToWire(list.Sessions))),
            CreateResult create => Map(("command", "create"), ("session", ToWire(create.Session))),
            AttachResult attach => Map(("command", "attach"), ("session", ToWire(attach.Session))),
            DetachResult detach => Map(("command", "detach"), ("sessionId", detach.SessionId)),
            PromptResult prompt => Map(("command", "prompt"), ("session", ToWire(prompt.Session))),
            SteerResult steer => Map(("command", "steer"), ("session", ToWire(steer.Session))),
            AbortResult abort => Map(("command", "abort"), ("session", ToWire(abort.Session))),
            SetModelResult setModel => Map(("command", "set_model"), ("session", ToWire(setModel.Session))),
            SetThinkingResult setThinking => Map(("command", "set_thinking"), ("session", ToWire(setThinking.Session))),
            _ => throw new ProtocolValidationError("Invalid server command result"),
        };
    }

    private static OrderedMap ToWire(ModelRef model) => Map(("provider", model.Provider), ("id", model.Id));

    private static OrderedMap ToWire(ModelCost cost) => Map(
        ("input", cost.Input),
        ("output", cost.Output),
        ("cacheRead", cost.CacheRead),
        ("cacheWrite", cost.CacheWrite));

    private static OrderedMap ToWire(ModelMetadata model) => Map(
        ("provider", model.Provider),
        ("id", model.Id),
        ("name", model.Name),
        ("api", model.Api),
        ("reasoning", model.Reasoning),
        ("input", ToWire(model.Input)),
        ("contextWindow", model.ContextWindow),
        ("maxTokens", model.MaxTokens),
        ("cost", ToWire(model.Cost)),
        ("supportedThinkingLevels", ToWire(model.SupportedThinkingLevels)),
        ("authenticated", model.Authenticated));

    private static OrderedMap ToWire(Usage usage)
    {
        OrderedMap map = Map(
            ("input", usage.Input),
            ("output", usage.Output),
            ("cacheRead", usage.CacheRead),
            ("cacheWrite", usage.CacheWrite));
        if (usage.Reasoning.HasValue)
        {
            map.Add("reasoning", usage.Reasoning.Value);
        }

        map.Add("totalTokens", usage.TotalTokens);
        map.Add("cost", Map(
            ("input", usage.Cost.Input),
            ("output", usage.Cost.Output),
            ("cacheRead", usage.Cost.CacheRead),
            ("cacheWrite", usage.Cost.CacheWrite),
            ("total", usage.Cost.Total)));
        return map;
    }

    private static object[] ToWire(IReadOnlyList<Content> content) => content.Select(ToWire).ToArray();

    private static object[] ToWire(IReadOnlyList<TranscriptItem> items) => items.Select(ToWire).ToArray();

    private static object[] ToWire(IReadOnlyList<UserTranscriptItem> items) => items.Select(ToWire).ToArray();

    private static object[] ToWire(IReadOnlyList<SessionMetadata> sessions) => sessions.Select(ToWire).ToArray();

    private static object[] ToWire(IReadOnlyList<ModelMetadata> models) => models.Select(ToWire).ToArray();

    private static object[] ToWire(IReadOnlyList<ModelInputKind> input) => input.Select(static value => value switch
    {
        ModelInputKind.Text => (object)"text",
        ModelInputKind.Image => "image",
        _ => throw new ProtocolValidationError("Invalid model input kind"),
    }).ToArray();

    private static object[] ToWire(IReadOnlyList<ThinkingLevel> levels) => levels.Select(static value => (object)ToWire(value)).ToArray();

    private static OrderedMap ToWire(Content content) => content switch
    {
        TextContent text => Map(("type", "text"), ("text", text.Text)),
        ThinkingContent thinking => MapOptional(
            ("type", "thinking"),
            ("thinking", thinking.Thinking),
            ("redacted", thinking.Redacted)),
        ImageContent image => Map(("type", "image"), ("data", image.Data), ("mimeType", image.MimeType)),
        ToolCallContent toolCall => Map(
            ("type", "toolCall"),
            ("toolCallId", toolCall.ToolCallId),
            ("toolName", toolCall.ToolName),
            ("input", toolCall.Input.ToWireValue())),
        _ => throw new ProtocolValidationError("Invalid protocol content"),
    };

    private static OrderedMap ToWire(TranscriptItem item) => item switch
    {
        UserTranscriptItem user => Map(
            ("id", user.Id),
            ("role", "user"),
            ("content", ToWire(user.Content)),
            ("timestamp", user.Timestamp)),
        StreamingAssistantTranscriptItem streaming => ToWire(streaming),
        CompleteAssistantTranscriptItem complete => ToWire(complete),
        ErrorAssistantTranscriptItem error => ToWire(error),
        AbortedAssistantTranscriptItem aborted => ToWire(aborted),
        RunningToolTranscriptItem running => ToWire(running),
        CompleteToolTranscriptItem completeTool => ToWire(completeTool),
        ErrorToolTranscriptItem errorTool => ToWire(errorTool),
        _ => throw new ProtocolValidationError("Invalid protocol transcript item"),
    };

    private static OrderedMap ToWire(AssistantTranscriptItem item)
    {
        OrderedMap map = Map(
            ("id", item.Id),
            ("role", "assistant"),
            ("content", ToWire(item.Content)),
            ("model", ToWire(item.Model)));
        if (item.ResponseModel is not null)
        {
            map.Add("responseModel", item.ResponseModel);
        }

        if (item.Usage is not null)
        {
            map.Add("usage", ToWire(item.Usage));
        }

        map.Add("timestamp", item.Timestamp);
        map.Add("status", item.Status);
        switch (item)
        {
            case CompleteAssistantTranscriptItem complete:
                map.Add("stopReason", ToWire(complete.StopReason));
                break;
            case ErrorAssistantTranscriptItem error:
                map.Add("stopReason", "error");
                if (error.ErrorMessage is not null)
                {
                    map.Add("errorMessage", error.ErrorMessage);
                }

                break;
            case AbortedAssistantTranscriptItem aborted:
                map.Add("stopReason", "aborted");
                if (aborted.ErrorMessage is not null)
                {
                    map.Add("errorMessage", aborted.ErrorMessage);
                }

                break;
        }

        return map;
    }

    private static OrderedMap ToWire(ToolTranscriptItem item)
    {
        OrderedMap map = Map(
            ("id", item.Id),
            ("role", "tool"),
            ("toolCallId", item.ToolCallId),
            ("toolName", item.ToolName),
            ("input", item.Input.ToWireValue()),
            ("content", ToWire(item.Content)));
        if (item.Details is not null)
        {
            map.Add("details", item.Details.ToWireValue());
        }

        if (item.Usage is not null)
        {
            map.Add("usage", ToWire(item.Usage));
        }

        map.Add("timestamp", item.Timestamp);
        map.Add("status", item.Status);
        map.Add("isError", item.IsError);
        return map;
    }

    private static OrderedMap ToWire(TranscriptProgress progress) => progress switch
    {
        ItemStartedProgress started => Map(("type", "item_started"), ("item", ToWire(started.Item))),
        AssistantDeltaProgress delta => Map(
            ("type", "assistant_delta"),
            ("messageId", delta.MessageId),
            ("contentIndex", delta.ContentIndex),
            ("kind", ToWire(delta.Kind)),
            ("delta", delta.Delta)),
        ItemUpdatedProgress updated => Map(("type", "item_updated"), ("item", ToWire(updated.Item))),
        ItemFinishedProgress finished => Map(("type", "item_finished"), ("item", ToWire(finished.Item))),
        _ => throw new ProtocolValidationError("Invalid transcript progress"),
    };

    private static OrderedMap ToWire(SessionMetadata metadata)
    {
        return MapOptional(
            ("id", metadata.Id),
            ("createdAt", metadata.CreatedAt),
            ("updatedAt", metadata.UpdatedAt),
            ("parentSessionId", metadata.ParentSessionId),
            ("sessionName", metadata.SessionName),
            ("cwd", metadata.Cwd));
    }

    private static OrderedMap ToWire(SessionSnapshot snapshot) => MapOptional(
        ("id", snapshot.Id),
        ("name", snapshot.Name),
        ("cwd", snapshot.Cwd),
        ("createdAt", snapshot.CreatedAt),
        ("updatedAt", snapshot.UpdatedAt),
        ("phase", ToWire(snapshot.Phase)),
        ("model", ToWire(snapshot.Model)),
        ("thinkingLevel", ToWire(snapshot.ThinkingLevel)),
        ("attached", snapshot.Attached),
        ("locked", snapshot.Locked),
        ("revision", snapshot.Revision),
        ("transcript", ToWire(snapshot.Transcript)),
        ("queuedSteer", ToWire(snapshot.QueuedSteer)),
        ("queuedSteerCount", snapshot.QueuedSteerCount));

    private static OrderedMap ToWire(ServerSnapshot snapshot) => Map(
        ("serverId", snapshot.ServerId),
        ("protocolVersion", snapshot.ProtocolVersion),
        ("revision", snapshot.Revision),
        ("sessions", ToWire(snapshot.Sessions)),
        ("models", ToWire(snapshot.Models)));

    private static OrderedMap ToWire(ProtocolError error) => MapOptional(
        ("code", ToWire(error.Code)),
        ("message", error.Message),
        ("details", error.Details is null ? null : error.Details.ToWireValue()));

    private static OrderedMap ToWire(ServerEvent serverEvent) => serverEvent switch
    {
        ServerSnapshotEvent snapshot => Map(("type", "server_snapshot"), ("snapshot", ToWire(snapshot.Snapshot))),
        SessionSnapshotEvent snapshot => Map(("type", "session_snapshot"), ("snapshot", ToWire(snapshot.Snapshot))),
        SessionProgressEvent progress => Map(
            ("type", "session_progress"),
            ("sessionId", progress.SessionId),
            ("progress", ToWire(progress.Progress))),
        SessionRemovedEvent removed => Map(("type", "session_removed"), ("sessionId", removed.SessionId)),
        _ => throw new ProtocolValidationError("Invalid server event"),
    };

    private static OrderedMap Map(params (string Key, object? Value)[] values)
    {
        OrderedMap map = new();
        foreach ((string key, object? value) in values)
        {
            map.Add(key, value);
        }

        return map;
    }

    private static OrderedMap MapOptional(params (string Key, object? Value)[] values)
    {
        OrderedMap map = new();
        foreach ((string key, object? value) in values)
        {
            if (value is not null)
            {
                map.Add(key, value);
            }
        }

        return map;
    }

    private static string ToWire(ThinkingLevel value) => value switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.Xhigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => throw new ProtocolValidationError("Invalid thinking level"),
    };

    private static string ToWire(SessionPhase value) => value switch
    {
        SessionPhase.Idle => "idle",
        SessionPhase.Turn => "turn",
        SessionPhase.Compaction => "compaction",
        SessionPhase.BranchSummary => "branch_summary",
        SessionPhase.Retry => "retry",
        _ => throw new ProtocolValidationError("Invalid session phase"),
    };

    private static string ToWire(ContentKind value) => value switch
    {
        ContentKind.Text => "text",
        ContentKind.Thinking => "thinking",
        ContentKind.ToolCall => "toolCall",
        _ => throw new ProtocolValidationError("Invalid content kind"),
    };

    private static string ToWire(TranscriptStopReason value) => value switch
    {
        TranscriptStopReason.Stop => "stop",
        TranscriptStopReason.Length => "length",
        TranscriptStopReason.ToolUse => "toolUse",
        TranscriptStopReason.Error => "error",
        TranscriptStopReason.Aborted => "aborted",
        _ => throw new ProtocolValidationError("Invalid transcript stop reason"),
    };

    private static string ToWire(ProtocolErrorCode value) => value switch
    {
        ProtocolErrorCode.Version => "version",
        ProtocolErrorCode.Busy => "busy",
        ProtocolErrorCode.SessionLocked => "session_locked",
        ProtocolErrorCode.NotFound => "not_found",
        ProtocolErrorCode.InvalidRequest => "invalid_request",
        ProtocolErrorCode.NotImplemented => "not_implemented",
        ProtocolErrorCode.InternalError => "internal_error",
        _ => throw new ProtocolValidationError("Invalid protocol error code"),
    };
}
