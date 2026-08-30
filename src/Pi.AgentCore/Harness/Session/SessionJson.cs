using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Pi.Ai;

namespace Pi.AgentCore.Harness.Session;

internal static class SessionJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

    internal static string ToJson(JsonNode node) => node.ToJsonString(Options);

    internal static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    internal static JsonObject CloneObject(JsonObject value) => (JsonObject)value.DeepClone();

    internal static TMetadata CloneMetadata<TMetadata>(TMetadata metadata)
        where TMetadata : SessionMetadata
    {
        if (metadata is Jsonl.JsonlSessionMetadata jsonlMetadata)
        {
            return (TMetadata)(SessionMetadata)(jsonlMetadata with
            {
                Metadata = jsonlMetadata.Metadata is null ? null : CloneObject(jsonlMetadata.Metadata),
            });
        }

        return metadata;
    }

    internal static string? GetString(JsonObject value, string propertyName)
    {
        return value[propertyName] is JsonValue json && json.TryGetValue<string>(out var result) ? result : null;
    }

    internal static long? GetInt64(JsonObject value, string propertyName)
    {
        if (value[propertyName] is not JsonValue json)
        {
            return null;
        }

        return json.TryGetValue<long>(out var result) ? result :
            json.TryGetValue<int>(out var integer) ? integer : null;
    }

    internal static double? GetDouble(JsonNode? node)
    {
        if (node is not JsonValue json)
        {
            return null;
        }

        return json.TryGetValue<double>(out var result) && double.IsFinite(result) ? result :
            json.TryGetValue<long>(out var integer) ? integer : null;
    }

    internal static JsonObject? GetObject(JsonObject value, string propertyName) => value[propertyName] as JsonObject;

    internal static JsonArray? GetArray(JsonObject value, string propertyName) => value[propertyName] as JsonArray;

    internal static bool Has(JsonObject value, string propertyName) => value.ContainsKey(propertyName);

    internal static JsonObject Object(params (string Name, JsonNode? Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (name, value) in fields)
        {
            result[name] = value;
        }

        return result;
    }

    internal static JsonArray Array<T>(IEnumerable<T> values, Func<T, JsonNode?> projector)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(projector(value));
        }

        return result;
    }

    internal static JsonNode Value(string value) => JsonValue.Create(value)!;

    internal static JsonNode Value(long value) => JsonValue.Create(value)!;

    internal static JsonNode Value(int value) => JsonValue.Create(value)!;

    internal static JsonNode Value(double value) => JsonValue.Create(value)!;

    internal static JsonNode Value(bool value) => JsonValue.Create(value)!;

    internal static JsonNode? Optional(JsonNode? value, bool present) => present ? Clone(value) : null;

    internal static JsonObject MessageToJson(Message message)
    {
        return message switch
        {
            UserMessage user => Object(
                ("role", Value("user")),
                ("content", SerializeContentValue(user.Content)),
                ("timestamp", Value(user.Timestamp))),
            AssistantMessage assistant => AssistantToJson(assistant),
            ToolResultMessage toolResult => ToolResultToJson(toolResult),
            _ => throw new ArgumentException($"Unsupported Pi message type: {message.GetType().Name}", nameof(message)),
        };
    }

    private static JsonObject AssistantToJson(AssistantMessage message)
    {
        var result = Object(
            ("role", Value("assistant")),
            ("content", Array(message.Content, ContentToJson)),
            ("api", Value(message.Api)),
            ("provider", Value(message.Provider)),
            ("model", Value(message.Model)),
            ("usage", UsageToJson(message.Usage)),
            ("stopReason", Value(message.StopReason)),
            ("timestamp", Value(message.Timestamp)));
        AddIfNotNull(result, "responseModel", message.ResponseModel);
        AddIfNotNull(result, "responseId", message.ResponseId);
        if (message.Diagnostics is not null)
        {
            result["diagnostics"] = Array(message.Diagnostics, DiagnosticToJson);
        }

        if (message.Deferred is not null)
        {
            result["deferred"] = DeferredToJson(message.Deferred);
        }

        AddIfNotNull(result, "errorMessage", message.ErrorMessage);
        AddIfNotNull(result, "rawStopReason", message.RawStopReason);
        if (message.EndTurn is not null)
        {
            result["endTurn"] = Value(message.EndTurn.Value);
        }

        return result;
    }

    private static JsonObject ToolResultToJson(ToolResultMessage message)
    {
        var result = Object(
            ("role", Value("toolResult")),
            ("toolCallId", Value(message.ToolCallId)),
            ("toolName", Value(message.ToolName)),
            ("content", Array(message.Content, ContentToJson)),
            ("isError", Value(message.IsError)),
            ("timestamp", Value(message.Timestamp)));
        if (message.Details is not null)
        {
            result["details"] = Clone(message.Details);
        }

        if (message.Usage is not null)
        {
            result["usage"] = UsageToJson(message.Usage);
        }

        if (message.AddedToolNames is not null)
        {
            result["addedToolNames"] = Array(message.AddedToolNames, Value);
        }

        return result;
    }

    private static JsonNode SerializeContentValue(object content)
    {
        if (content is JsonNode node)
        {
            return Clone(node)!;
        }

        if (content is string text)
        {
            return Value(text);
        }

        if (content is IEnumerable<ContentBlock> blocks)
        {
            return Array(blocks, ContentToJson);
        }

        throw new ArgumentException("Content must be a JSON node, string, or content-block collection.", nameof(content));
    }

    internal static JsonObject ContentToJson(ContentBlock block)
    {
        return block switch
        {
            TextContent text => Object(
                ("type", Value("text")),
                ("text", Value(text.Text))),
            ThinkingContent thinking => Object(
                ("type", Value("thinking")),
                ("thinking", Value(thinking.Thinking))),
            ImageContent image => Object(
                ("type", Value("image")),
                ("data", Value(image.Data)),
                ("mimeType", Value(image.MimeType))),
            ToolCall toolCall => Object(
                ("type", Value("toolCall")),
                ("id", Value(toolCall.Id)),
                ("name", Value(toolCall.Name)),
                ("arguments", CloneObject(toolCall.Arguments))),
            _ => throw new ArgumentException($"Unsupported content block type: {block.GetType().Name}", nameof(block)),
        };
    }

    private static JsonObject DiagnosticToJson(AssistantMessageDiagnostic diagnostic)
    {
        var result = Object(
            ("type", Value(diagnostic.Type)),
            ("timestamp", Value(diagnostic.Timestamp)));
        if (diagnostic.Error is not null)
        {
            var error = new JsonObject();
            AddIfNotNull(error, "name", diagnostic.Error.Name);
            error["message"] = Value(diagnostic.Error.Message);
            AddIfNotNull(error, "stack", diagnostic.Error.Stack);
            if (diagnostic.Error.Code is not null)
            {
                error["code"] = Clone(diagnostic.Error.Code);
            }

            result["error"] = error;
        }

        if (diagnostic.Details is not null)
        {
            var details = new JsonObject();
            foreach (var pair in diagnostic.Details)
            {
                details[pair.Key] = Clone(pair.Value);
            }

            result["details"] = details;
        }

        return result;
    }

    private static JsonObject DeferredToJson(DeferredHandle handle)
    {
        var result = Object(
            ("provider", Value(handle.Provider)),
            ("modelId", Value(handle.ModelId)),
            ("api", Value(handle.Api)),
            ("id", Value(handle.Id)));
        if (handle.ExpiresAt is not null)
        {
            result["expiresAt"] = Value(handle.ExpiresAt.Value);
        }

        if (handle.PollAfterMs is not null)
        {
            result["pollAfterMs"] = Value(handle.PollAfterMs.Value);
        }

        if (handle.Data is not null)
        {
            result["data"] = Clone(handle.Data);
        }

        return result;
    }

    internal static Usage ParseUsage(JsonObject value)
    {
        var usage = new Usage
        {
            Input = GetInt32(value, "input"),
            Output = GetInt32(value, "output"),
            CacheRead = GetInt32(value, "cacheRead"),
            CacheWrite = GetInt32(value, "cacheWrite"),
            CacheWrite1h = GetNullableInt32(value, "cacheWrite1h"),
            Reasoning = GetNullableInt32(value, "reasoning"),
            TotalTokens = GetInt32(value, "totalTokens"),
        };

        if (value["cost"] is JsonObject cost)
        {
            usage = usage with
            {
                Cost = new UsageCost
                {
                    Input = GetDouble(cost["input"]) ?? 0,
                    Output = GetDouble(cost["output"]) ?? 0,
                    CacheRead = GetDouble(cost["cacheRead"]) ?? 0,
                    CacheWrite = GetDouble(cost["cacheWrite"]) ?? 0,
                    Total = GetDouble(cost["total"]) ?? 0,
                },
            };
        }

        return usage;
    }

    internal static JsonObject UsageToJson(Usage usage)
    {
        var cost = Object(
            ("input", Value(usage.Cost.Input)),
            ("output", Value(usage.Cost.Output)),
            ("cacheRead", Value(usage.Cost.CacheRead)),
            ("cacheWrite", Value(usage.Cost.CacheWrite)),
            ("total", Value(usage.Cost.Total)));
        var result = Object(
            ("input", Value(usage.Input)),
            ("output", Value(usage.Output)),
            ("cacheRead", Value(usage.CacheRead)),
            ("cacheWrite", Value(usage.CacheWrite)));
        if (usage.CacheWrite1h is not null)
        {
            result["cacheWrite1h"] = Value(usage.CacheWrite1h.Value);
        }

        if (usage.Reasoning is not null)
        {
            result["reasoning"] = Value(usage.Reasoning.Value);
        }

        result["totalTokens"] = Value(usage.TotalTokens);
        result["cost"] = cost;
        return result;
    }

    internal static JsonObject EntryToJson(Entry entry, bool includeStorageFields)
    {
        var result = entry.RawFields is { } raw ? CloneObject(raw) : new JsonObject();
        var hasRaw = entry.RawFields is not null;
        SetField(result, "type", Value(entry.Type));
        SetField(result, "id", Value(entry.Id));

        switch (entry)
        {
            case MessageEntry message:
                SetField(result, "message", CloneObject(message.Message.Value));
                if (message.Terminate is not null)
                {
                    SetField(result, "terminate", Value(message.Terminate.Value));
                }

                break;
            case ModelChangeEntry model:
                SetField(result, "provider", Value(model.Provider));
                SetField(result, "modelId", Value(model.ModelId));
                break;
            case ThinkingLevelEntry thinking:
                SetField(result, "thinkingLevel", Value(thinking.ThinkingLevel));
                break;
            case ActiveToolsEntry tools:
                SetField(result, "activeToolNames", Array(tools.ActiveToolNames, Value));
                break;
            case CompactionEntry compaction:
                SetField(result, "summary", Value(compaction.Summary));
                SetField(result, "retainedTail", Array(compaction.RetainedTail, static item => CloneObject(item.Value)));
                SetField(result, "tokensBefore", Value(compaction.TokensBefore));
                if (compaction.DetailsPresent || compaction.Details is not null)
                {
                    SetField(result, "details", Clone(compaction.Details));
                }

                if (compaction.Usage is not null)
                {
                    SetField(result, "usage", UsageToJson(compaction.Usage));
                }

                break;
            case BranchSummaryEntry branch:
                SetField(result, "fromId", Value(branch.FromId));
                SetField(result, "summary", Value(branch.Summary));
                if (branch.DetailsPresent || branch.Details is not null)
                {
                    SetField(result, "details", Clone(branch.Details));
                }

                if (branch.Usage is not null)
                {
                    SetField(result, "usage", UsageToJson(branch.Usage));
                }

                break;
            case CustomEntry custom:
                SetField(result, "customType", Value(custom.CustomType));
                if (custom.DataPresent || custom.Data is not null)
                {
                    SetField(result, "data", Clone(custom.Data));
                }

                break;
        }

        if (includeStorageFields)
        {
            SetField(result, "parentId", entry.ParentId is null ? null : Value(entry.ParentId));
            SetField(result, "seq", Value(entry.Seq));
            SetField(result, "timestamp", Value(entry.Timestamp));
        }
        else
        {
            result.Remove("parentId");
            result.Remove("seq");
            result.Remove("timestamp");
        }

        _ = hasRaw;
        return result;
    }

    internal static JsonObject RecordToJson(LaneRecord record)
    {
        var result = record.RawFields is { } raw ? CloneObject(raw) : new JsonObject();
        SetField(result, "type", Value(record.Type));
        SetField(result, "id", Value(record.Id));
        SetField(result, "lane", Value(record.Lane));

        switch (record)
        {
            case OperationStartedRecord started:
                SetField(result, "sourceLeafId", started.SourceLeafId is null ? null : Value(started.SourceLeafId));
                SetField(result, "intent", IntentToJson(started.Intent));
                break;
            case AbortRequestedRecord abort:
                SetField(result, "runId", Value(abort.RunId));
                break;
            case OperationFinishedRecord finished:
                SetField(result, "runId", Value(finished.RunId));
                SetField(result, "outcome", Value(finished.Outcome));
                if (finished.Error is not null)
                {
                    SetField(result, "error", Object(
                        ("code", Value(finished.Error.Code)),
                        ("message", Value(finished.Error.Message))));
                }

                break;
            case StepAttemptRecord attempt:
                SetField(result, "runId", Value(attempt.RunId));
                SetField(result, "step", Value(attempt.Step));
                SetField(result, "attempt", Value(attempt.Attempt));
                SetField(result, "resultEntryId", Value(attempt.ResultEntryId));
                if (attempt.CompactionReason is not null)
                {
                    SetField(result, "compactionReason", Value(attempt.CompactionReason));
                }

                break;
            case ToolStartedRecord tool:
                SetField(result, "runId", Value(tool.RunId));
                SetField(result, "assistantEntryId", Value(tool.AssistantEntryId));
                SetField(result, "toolIndex", Value(tool.ToolIndex));
                SetField(result, "toolCallId", Value(tool.ToolCallId));
                SetField(result, "toolName", Value(tool.ToolName));
                SetField(result, "effectiveArgs", CloneObject(tool.EffectiveArgs));
                SetField(result, "resultEntryId", Value(tool.ResultEntryId));
                SetField(result, "replay", Value(tool.Replay));
                break;
            case QueueEnqueuedRecord queue:
                SetField(result, "queue", Value(queue.Queue));
                if (queue.RunId is not null)
                {
                    SetField(result, "runId", Value(queue.RunId));
                }

                SetField(result, "target", EntryToJson(queue.Target, includeStorageFields: false));
                break;
            case QueueCancelledRecord cancelled:
                if (cancelled.RunId is not null)
                {
                    SetField(result, "runId", Value(cancelled.RunId));
                }

                SetField(result, "entryId", Value(cancelled.EntryId));
                break;
            case WriteDeferredRecord deferred:
                SetField(result, "runId", Value(deferred.RunId));
                SetField(result, "target", EntryToJson(deferred.Target, includeStorageFields: false));
                break;
            case UsageRecord usage:
                SetField(result, "cause", Value(usage.Cause));
                if (usage.Cause == "adjustment" && (usage.DetailsPresent || usage.Details is not null))
                {
                    SetField(result, "details", Clone(usage.Details));
                }

                if (usage.RunId is not null)
                {
                    SetField(result, "runId", Value(usage.RunId));
                }

                if (usage.EntryId is not null)
                {
                    SetField(result, "entryId", Value(usage.EntryId));
                }

                if (usage.Attempt is not null)
                {
                    SetField(result, "attempt", Value(usage.Attempt.Value));
                }

                if (usage.StopReason is not null)
                {
                    SetField(result, "stopReason", Value(usage.StopReason));
                }

                if (usage.ToolCallId is not null)
                {
                    SetField(result, "toolCallId", Value(usage.ToolCallId));
                }

                SetField(result, "usage", UsageToJson(usage.Usage));
                break;
        }

        SetField(result, "seq", Value(record.Seq));
        SetField(result, "timestamp", Value(record.Timestamp));
        return result;
    }

    private static JsonObject IntentToJson(OperationIntent intent)
    {
        var result = intent.RawFields is { } raw ? CloneObject(raw) : new JsonObject();
        SetField(result, "kind", Value(intent.Kind));
        switch (intent)
        {
            case RunOperationIntent run:
                SetField(result, "originalPrompt", Array(run.OriginalPrompt, static item => CloneObject(item.Value)));
                SetField(result, "initialMessages", Array(run.InitialMessages, static item => EntryToJson(item, includeStorageFields: false)));
                AddOptionalString(result, "systemPromptOverride", run.SystemPromptOverride);
                if (run.ResumeData is not null)
                {
                    SetField(result, "resumeData", CloneObject(run.ResumeData));
                }

                break;
            case CompactionOperationIntent compaction:
                AddOptionalString(result, "customInstructions", compaction.CustomInstructions);
                SetField(result, "resultEntryId", Value(compaction.ResultEntryId));
                break;
            case NavigationOperationIntent navigation:
                SetField(result, "targetId", navigation.TargetId is null ? null : Value(navigation.TargetId));
                SetField(result, "summarize", Value(navigation.Summarize));
                AddOptionalString(result, "customInstructions", navigation.CustomInstructions);
                AddOptionalString(result, "label", navigation.Label);
                AddOptionalString(result, "summaryEntryId", navigation.SummaryEntryId);
                break;
        }

        return result;
    }

    private static void SetField(JsonObject result, string name, JsonNode? value)
    {
        result[name] = value;
    }

    private static void AddOptionalString(JsonObject result, string name, string? value)
    {
        if (value is not null)
        {
            SetField(result, name, Value(value));
        }
    }

    internal static Message? JsonToMessage(JsonObject value)
    {
        var role = GetString(value, "role");
        var timestamp = GetInt64(value, "timestamp") ?? 0;
        switch (role)
        {
            case "user":
                return new UserMessage(Clone(value["content"]) ?? new JsonArray(), timestamp);
            case "assistant":
                {
                    var content = value["content"] is JsonArray array
                        ? array.Select(ParseContent).Where(static item => item is not null).Cast<ContentBlock>().ToArray()
                        : [];
                    return new AssistantMessage
                    {
                        Content = content,
                        Api = GetString(value, "api") ?? string.Empty,
                        Provider = GetString(value, "provider") ?? string.Empty,
                        Model = GetString(value, "model") ?? string.Empty,
                        ResponseModel = GetString(value, "responseModel"),
                        ResponseId = GetString(value, "responseId"),
                        Usage = value["usage"] is JsonObject usage ? ParseUsage(usage) : new Usage(),
                        StopReason = GetString(value, "stopReason") ?? string.Empty,
                        Deferred = null,
                        ErrorMessage = GetString(value, "errorMessage"),
                        RawStopReason = GetString(value, "rawStopReason"),
                        EndTurn = value["endTurn"] is JsonValue endTurn && endTurn.TryGetValue<bool>(out var turn) ? turn : null,
                        Timestamp = timestamp,
                    };
                }
            case "toolResult":
                {
                    var content = value["content"] is JsonArray array
                        ? array.Select(ParseContent).Where(static item => item is not null).Cast<ContentBlock>().ToArray()
                        : [];
                    return new ToolResultMessage
                    {
                        ToolCallId = GetString(value, "toolCallId") ?? string.Empty,
                        ToolName = GetString(value, "toolName") ?? string.Empty,
                        Content = content,
                        Details = Clone(value["details"]),
                        Usage = value["usage"] is JsonObject usage ? ParseUsage(usage) : null,
                        IsError = value["isError"] is JsonValue error && error.TryGetValue<bool>(out var isError) && isError,
                        Timestamp = timestamp,
                    };
                }
            default:
                return null;
        }
    }

    private static ContentBlock? ParseContent(JsonNode? value)
    {
        if (value is not JsonObject block)
        {
            return null;
        }

        return GetString(block, "type") switch
        {
            "text" => new TextContent(GetString(block, "text") ?? string.Empty),
            "thinking" => new ThinkingContent(GetString(block, "thinking") ?? string.Empty),
            "image" => new ImageContent(GetString(block, "data") ?? string.Empty, GetString(block, "mimeType") ?? string.Empty),
            "toolCall" => new ToolCall(
                GetString(block, "id") ?? string.Empty,
                GetString(block, "name") ?? string.Empty,
                GetObject(block, "arguments") is { } arguments ? CloneObject(arguments) : new JsonObject()),
            _ => null,
        };
    }

    private static void AddIfNotNull(JsonObject result, string name, string? value)
    {
        if (value is not null)
        {
            result[name] = Value(value);
        }
    }

    private static int GetInt32(JsonObject value, string propertyName)
    {
        var number = GetInt64(value, propertyName) ?? 0;
        return checked((int)number);
    }

    private static int? GetNullableInt32(JsonObject value, string propertyName)
    {
        return value.ContainsKey(propertyName) && value[propertyName] is not null ? GetInt32(value, propertyName) : null;
    }
}
