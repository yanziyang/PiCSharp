using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.AgentCore.Harness.Session.Jsonl;

/// <summary>Failure kind returned by the JSONL decoder.</summary>
public enum JsonlDecodeErrorKind
{
    /// <summary>The line is not valid JSON.</summary>
    Syntax,

    /// <summary>The JSON value does not match the session schema.</summary>
    Schema,
}

/// <summary>Typed JSONL decoding error.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name mirrors Pi's public decoder error contract.")]
public sealed class JsonlDecodeError : Exception
{
    /// <summary>Syntax or schema failure kind.</summary>
    public JsonlDecodeErrorKind Kind { get; }

    /// <summary>Creates a decoder error.</summary>
    public JsonlDecodeError(JsonlDecodeErrorKind kind, string message, Exception? cause = null)
        : base(message, cause)
    {
        Kind = kind;
    }
}

/// <summary>Result of parsing one JSONL line.</summary>
public readonly record struct JsonlParseResult<T>(T? Value, JsonlDecodeError? Error)
{
    /// <summary>Whether parsing succeeded.</summary>
    public bool IsSuccess => Error is null;
}

/// <summary>Codec for Pi harness JSONL v4 headers and mutations.</summary>
public static class JsonlCodec
{
    private static readonly HashSet<string> _entryTypes =
    [
        "message",
        "model_change",
        "thinking_level_change",
        "active_tools_change",
        "compaction",
        "branch_summary",
        "custom",
    ];

    private static readonly HashSet<string> _recordTypes =
    [
        "operation_started",
        "abort_requested",
        "operation_finished",
        "step_attempt",
        "tool_started",
        "queue_enqueued",
        "queue_cancelled",
        "write_deferred",
        "usage",
    ];

    private static readonly HashSet<string> _operationKinds = ["run", "compaction", "navigation"];

    /// <summary>Parses a header line without throwing decoder errors.</summary>
    public static JsonlParseResult<JsonlV4Header> ParseHeader(string line)
    {
        try
        {
            return new(JsonlCodec.DecodeHeader(line), null);
        }
        catch (JsonlDecodeError error)
        {
            return new(null, error);
        }
    }

    /// <summary>Parses a mutation line without throwing decoder errors.</summary>
    public static JsonlParseResult<SessionMutation> ParseMutation(string line)
    {
        try
        {
            return new(JsonlCodec.DecodeMutation(line), null);
        }
        catch (JsonlDecodeError error)
        {
            return new(null, error);
        }
    }

    /// <summary>Encodes a header with one trailing newline.</summary>
    public static string EncodeHeader(JsonlV4Header header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var value = header.RawFields is { } raw ? SessionJson.CloneObject(raw) : new JsonObject();
        Put(value, "kind", SessionJson.Value(header.Kind), header.RawFields is not null);
        Put(value, "version", SessionJson.Value(header.Version), header.RawFields is not null);
        Put(value, "id", SessionJson.Value(header.Id), header.RawFields is not null);
        Put(value, "createdAt", SessionJson.Value(header.CreatedAt), header.RawFields is not null);
        Put(value, "cwd", SessionJson.Value(header.Cwd), header.RawFields is not null);
        SetOptionalString(value, "parentSessionId", header.ParentSessionId);
        SetOptionalString(value, "legacyParentSessionPath", header.LegacyParentSessionPath);
        if (header.Metadata is not null)
        {
            value["metadata"] = SessionJson.CloneObject(header.Metadata);
        }

        return SessionJson.ToJson(value) + "\n";
    }

    /// <summary>Encodes a mutation with one trailing newline.</summary>
    public static string EncodeMutation(SessionMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        JsonObject value;
        switch (mutation)
        {
            case EntryMutation entryMutation:
                value = new JsonObject { ["kind"] = SessionJson.Value("entry") };
                if (entryMutation.Lane is not null)
                {
                    value["lane"] = SessionJson.Value(entryMutation.Lane);
                }

                CopyInto(value, SessionJson.EntryToJson(entryMutation.Entry, includeStorageFields: true));
                break;
            case RecordMutation recordMutation:
                value = new JsonObject { ["kind"] = SessionJson.Value("record") };
                CopyInto(value, SessionJson.RecordToJson(recordMutation.Record));
                break;
            case LaneMutation laneMutation:
                value = new JsonObject
                {
                    ["kind"] = SessionJson.Value("lane"),
                    ["seq"] = SessionJson.Value(laneMutation.Seq),
                    ["lane"] = SessionJson.Value(laneMutation.Lane),
                    ["leafId"] = laneMutation.LeafId is null ? null : SessionJson.Value(laneMutation.LeafId),
                };
                break;
            case FactMutation factMutation:
                value = new JsonObject
                {
                    ["kind"] = SessionJson.Value("fact"),
                    ["seq"] = SessionJson.Value(factMutation.Seq),
                    ["fact"] = SessionJson.Value(factMutation.Fact),
                };
                if (factMutation.Fact == "name")
                {
                    if (factMutation.Name is not null)
                    {
                        value["name"] = SessionJson.Value(factMutation.Name);
                    }
                }
                else
                {
                    if (factMutation.TargetId is not null)
                    {
                        value["targetId"] = SessionJson.Value(factMutation.TargetId);
                    }

                    if (factMutation.Label is not null)
                    {
                        value["label"] = SessionJson.Value(factMutation.Label);
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        return SessionJson.ToJson(value) + "\n";
    }

    /// <summary>Projects a v4 header into repository metadata.</summary>
    public static JsonlSessionMetadata MetadataFromHeader(JsonlV4Header header, string path, double modifiedAt)
    {
        return new JsonlSessionMetadata
        {
            Id = header.Id,
            CreatedAt = header.CreatedAt,
            Cwd = header.Cwd,
            Path = path,
            ModifiedAt = modifiedAt,
            SourceFormat = 4,
            ParentSessionId = header.ParentSessionId,
            LegacyParentSessionPath = header.LegacyParentSessionPath,
            Metadata = header.Metadata is null ? null : SessionJson.CloneObject(header.Metadata),
        };
    }

    internal static JsonlV4Header DecodeHeader(string line)
    {
        var value = ParseObject(line);
        if (SessionJson.GetString(value, "kind") != "header")
        {
            throw Schema("is not a header");
        }

        if (SessionJson.GetInt64(value, "version") != 4)
        {
            throw Schema("has unsupported session version");
        }

        var parentSessionId = OptionalString(value, "parentSessionId", "parentSessionId");
        var legacyParentSessionPath = OptionalString(value, "legacyParentSessionPath", "legacyParentSessionPath");
        if (parentSessionId is not null && legacyParentSessionPath is not null)
        {
            throw Schema("has both parentSessionId and legacyParentSessionPath");
        }

        JsonObject? metadata = null;
        if (value.ContainsKey("metadata"))
        {
            if (value["metadata"] is not JsonObject metadataValue)
            {
                throw Schema("has invalid metadata");
            }

            metadata = SessionJson.CloneObject(metadataValue);
        }

        return new JsonlV4Header
        {
            Kind = "header",
            Version = 4,
            Id = RequireString(value, "id"),
            CreatedAt = RequireTimestamp(value["createdAt"]),
            Cwd = RequireString(value, "cwd"),
            ParentSessionId = parentSessionId,
            LegacyParentSessionPath = legacyParentSessionPath,
            Metadata = metadata,
            RawFields = SessionJson.CloneObject(value),
        };
    }

    internal static SessionMutation DecodeMutation(string line)
    {
        var value = ParseObject(line);
        var seq = RequireSequence(value["seq"]);
        return SessionJson.GetString(value, "kind") switch
        {
            "entry" => DecodeEntryMutation(value, seq),
            "record" => DecodeRecordMutation(value, seq),
            "lane" => DecodeLaneMutation(value, seq),
            "fact" => DecodeFactMutation(value, seq),
            _ => throw Schema("has unknown mutation kind"),
        };
    }

    internal static Entry DecodeEntryObject(JsonObject value, bool requireStorage = true)
    {
        var id = RequireString(value, "id");
        var type = RequireString(value, "type", "entry type");
        if (!_entryTypes.Contains(type))
        {
            throw Schema($"has unknown entry type {type}");
        }

        var parentId = requireStorage ? RequireNullableId(value, "parentId") : GetNullableId(value, "parentId");
        var seq = requireStorage ? RequireSequence(value["seq"]) : GetLong(value, "seq") ?? 0;
        var timestamp = requireStorage ? RequireTimestamp(value["timestamp"]) : GetLong(value, "timestamp") ?? 0;
        var raw = SessionJson.CloneObject(value);
        raw.Remove("kind");
        raw.Remove("lane");

        return type switch
        {
            "message" => new MessageEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                Message = value["message"] is JsonObject message ? AgentMessage.FromJson(message) : new AgentMessage(new JsonObject()),
                Terminate = value["terminate"] is JsonValue terminate && terminate.TryGetValue<bool>(out var flag) ? flag : null,
                RawFields = raw,
            },
            "model_change" => new ModelChangeEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                Provider = SessionJson.GetString(value, "provider") ?? string.Empty,
                ModelId = SessionJson.GetString(value, "modelId") ?? string.Empty,
                RawFields = raw,
            },
            "thinking_level_change" => new ThinkingLevelEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                ThinkingLevel = SessionJson.GetString(value, "thinkingLevel") ?? string.Empty,
                RawFields = raw,
            },
            "active_tools_change" => new ActiveToolsEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                ActiveToolNames = GetStringArray(value["activeToolNames"]),
                RawFields = raw,
            },
            "compaction" => new CompactionEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                Summary = SessionJson.GetString(value, "summary") ?? string.Empty,
                RetainedTail = GetMessageArray(value["retainedTail"]),
                TokensBefore = GetLong(value, "tokensBefore") ?? 0,
                Details = value.ContainsKey("details") ? SessionJson.Clone(value["details"]) : null,
                DetailsPresent = value.ContainsKey("details"),
                Usage = value["usage"] is JsonObject usage ? SessionJson.ParseUsage(usage) : null,
                RawFields = raw,
            },
            "branch_summary" => new BranchSummaryEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                FromId = SessionJson.GetString(value, "fromId") ?? string.Empty,
                Summary = SessionJson.GetString(value, "summary") ?? string.Empty,
                Details = value.ContainsKey("details") ? SessionJson.Clone(value["details"]) : null,
                DetailsPresent = value.ContainsKey("details"),
                Usage = value["usage"] is JsonObject usage ? SessionJson.ParseUsage(usage) : null,
                RawFields = raw,
            },
            "custom" => new CustomEntry
            {
                Id = id,
                Seq = seq,
                ParentId = parentId,
                Timestamp = timestamp,
                CustomType = RequireString(value, "customType"),
                Data = value.ContainsKey("data") ? SessionJson.Clone(value["data"]) : null,
                DataPresent = value.ContainsKey("data"),
                RawFields = raw,
            },
            _ => throw Schema("has unknown entry type"),
        };
    }

    internal static LaneRecord DecodeRecordObject(JsonObject value)
    {
        var id = RequireString(value, "id");
        var lane = RequireString(value, "lane");
        var type = RequireString(value, "type", "record type");
        if (!_recordTypes.Contains(type))
        {
            throw Schema($"has unknown record type {type}");
        }

        var seq = RequireSequence(value["seq"]);
        var timestamp = RequireTimestamp(value["timestamp"]);
        var raw = SessionJson.CloneObject(value);
        raw.Remove("kind");

        return type switch
        {
            "operation_started" => DecodeOperationStarted(value, id, lane, seq, timestamp, raw),
            "abort_requested" => new AbortRequestedRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                RunId = SessionJson.GetString(value, "runId") ?? string.Empty,
                RawFields = raw,
            },
            "operation_finished" => new OperationFinishedRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                RunId = RequireString(value, "runId"),
                Outcome = SessionJson.GetString(value, "outcome") ?? string.Empty,
                Error = value["error"] is JsonObject error ? new SessionErrorInfo
                {
                    Code = SessionJson.GetString(error, "code") ?? string.Empty,
                    Message = SessionJson.GetString(error, "message") ?? string.Empty,
                } : null,
                RawFields = raw,
            },
            "step_attempt" => new StepAttemptRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                RunId = SessionJson.GetString(value, "runId") ?? string.Empty,
                Step = SessionJson.GetString(value, "step") ?? string.Empty,
                Attempt = (int)(GetLong(value, "attempt") ?? 0),
                ResultEntryId = SessionJson.GetString(value, "resultEntryId") ?? string.Empty,
                CompactionReason = SessionJson.GetString(value, "compactionReason"),
                RawFields = raw,
            },
            "tool_started" => new ToolStartedRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                RunId = SessionJson.GetString(value, "runId") ?? string.Empty,
                AssistantEntryId = SessionJson.GetString(value, "assistantEntryId") ?? string.Empty,
                ToolIndex = (int)(GetLong(value, "toolIndex") ?? 0),
                ToolCallId = SessionJson.GetString(value, "toolCallId") ?? string.Empty,
                ToolName = SessionJson.GetString(value, "toolName") ?? string.Empty,
                EffectiveArgs = value["effectiveArgs"] is JsonObject args ? SessionJson.CloneObject(args) : new JsonObject(),
                ResultEntryId = SessionJson.GetString(value, "resultEntryId") ?? string.Empty,
                Replay = SessionJson.GetString(value, "replay") ?? string.Empty,
                RawFields = raw,
            },
            "queue_enqueued" => new QueueEnqueuedRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                Queue = SessionJson.GetString(value, "queue") ?? string.Empty,
                RunId = SessionJson.GetString(value, "runId"),
                Target = value["target"] is JsonObject target ? DecodeEntryObject(target, requireStorage: false) : EmptyCustomTarget(),
                RawFields = raw,
            },
            "queue_cancelled" => new QueueCancelledRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                RunId = SessionJson.GetString(value, "runId"),
                EntryId = SessionJson.GetString(value, "entryId") ?? string.Empty,
                RawFields = raw,
            },
            "write_deferred" => new WriteDeferredRecord
            {
                Id = id,
                Lane = lane,
                Seq = seq,
                Timestamp = timestamp,
                RunId = SessionJson.GetString(value, "runId") ?? string.Empty,
                Target = value["target"] is JsonObject deferredTarget ? DecodeEntryObject(deferredTarget, requireStorage: false) : EmptyCustomTarget(),
                RawFields = raw,
            },
            "usage" => DecodeUsageRecord(value, id, lane, seq, timestamp, raw),
            _ => throw Schema("has unknown record type"),
        };
    }

    private static OperationStartedRecord DecodeOperationStarted(
        JsonObject value,
        string id,
        string lane,
        long seq,
        long timestamp,
        JsonObject raw)
    {
        if (value["intent"] is not JsonObject intentValue)
        {
            throw Schema("has invalid intent");
        }

        var kind = RequireString(intentValue, "kind", "operation kind");
        if (!_operationKinds.Contains(kind))
        {
            throw Schema($"has unknown operation kind {kind}");
        }

        OperationIntent intent = kind switch
        {
            "run" => new RunOperationIntent
            {
                OriginalPrompt = GetMessageArray(intentValue["originalPrompt"]),
                InitialMessages = GetEntryArray(intentValue["initialMessages"]),
                SystemPromptOverride = SessionJson.GetString(intentValue, "systemPromptOverride"),
                ResumeData = intentValue["resumeData"] is JsonObject resume ? SessionJson.CloneObject(resume) : null,
                RawFields = SessionJson.CloneObject(intentValue),
            },
            "compaction" => new CompactionOperationIntent
            {
                CustomInstructions = SessionJson.GetString(intentValue, "customInstructions"),
                ResultEntryId = SessionJson.GetString(intentValue, "resultEntryId") ?? string.Empty,
                RawFields = SessionJson.CloneObject(intentValue),
            },
            "navigation" => new NavigationOperationIntent
            {
                TargetId = GetNullableId(intentValue, "targetId"),
                Summarize = GetBool(intentValue, "summarize"),
                CustomInstructions = SessionJson.GetString(intentValue, "customInstructions"),
                Label = SessionJson.GetString(intentValue, "label"),
                SummaryEntryId = SessionJson.GetString(intentValue, "summaryEntryId"),
                RawFields = SessionJson.CloneObject(intentValue),
            },
            _ => throw Schema($"has unknown operation kind {kind}"),
        };

        return new OperationStartedRecord
        {
            Id = id,
            Lane = lane,
            Seq = seq,
            Timestamp = timestamp,
            SourceLeafId = GetNullableId(value, "sourceLeafId"),
            Intent = intent,
            RawFields = raw,
        };
    }

    private static UsageRecord DecodeUsageRecord(
        JsonObject value,
        string id,
        string lane,
        long seq,
        long timestamp,
        JsonObject raw)
    {
        return new UsageRecord
        {
            Id = id,
            Lane = lane,
            Seq = seq,
            Timestamp = timestamp,
            Cause = SessionJson.GetString(value, "cause") ?? string.Empty,
            Usage = value["usage"] is JsonObject usage ? SessionJson.ParseUsage(usage) : new Pi.Ai.Usage(),
            RunId = SessionJson.GetString(value, "runId"),
            EntryId = SessionJson.GetString(value, "entryId"),
            Attempt = GetLong(value, "attempt") is { } attempt ? (int)attempt : null,
            StopReason = SessionJson.GetString(value, "stopReason"),
            ToolCallId = SessionJson.GetString(value, "toolCallId"),
            Details = value.ContainsKey("details") ? SessionJson.Clone(value["details"]) : null,
            DetailsPresent = value.ContainsKey("details"),
            RawFields = raw,
        };
    }

    private static EntryMutation DecodeEntryMutation(JsonObject value, long seq)
    {
        var lane = value.ContainsKey("lane") ? RequireString(value, "lane") : null;
        var entry = DecodeEntryObject(value, requireStorage: true);
        return new EntryMutation { Seq = seq, Lane = lane, Entry = entry };
    }

    private static RecordMutation DecodeRecordMutation(JsonObject value, long seq)
    {
        var record = DecodeRecordObject(value);
        return new RecordMutation { Seq = seq, Record = record };
    }

    private static LaneMutation DecodeLaneMutation(JsonObject value, long seq)
    {
        return new LaneMutation
        {
            Seq = seq,
            Lane = RequireString(value, "lane"),
            LeafId = RequireNullableId(value, "leafId"),
        };
    }

    private static FactMutation DecodeFactMutation(JsonObject value, long seq)
    {
        var fact = SessionJson.GetString(value, "fact");
        return fact switch
        {
            "name" => new FactMutation
            {
                Seq = seq,
                Fact = fact,
                Name = OptionalFactString(value, "name"),
            },
            "label" => new FactMutation
            {
                Seq = seq,
                Fact = fact,
                TargetId = RequireString(value, "targetId"),
                Label = OptionalFactString(value, "label"),
            },
            _ => throw Schema("has unknown fact type"),
        };
    }

    private static JsonObject ParseObject(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new JsonlDecodeError(JsonlDecodeErrorKind.Syntax, "is not valid JSON", error);
        }

        return node as JsonObject ?? throw Schema("is not a JSON object");
    }

    private static string RequireString(JsonObject value, string field, string? displayField = null)
    {
        return SessionJson.GetString(value, field) ?? throw Schema($"has invalid {displayField ?? field}");
    }

    private static long RequireSequence(JsonNode? value)
    {
        var number = GetInteger(value);
        if (number is null or <= 0 || number > 9_007_199_254_740_991)
        {
            throw Schema("has invalid seq");
        }

        return number.Value;
    }

    private static long RequireTimestamp(JsonNode? value)
    {
        var number = GetInteger(value);
        if (number is null or < 0 || number > 9_007_199_254_740_991)
        {
            throw Schema("has invalid timestamp");
        }

        return number.Value;
    }

    private static string? RequireNullableId(JsonObject value, string field)
    {
        if (!value.ContainsKey(field))
        {
            throw Schema($"has invalid {field}");
        }

        return GetNullableId(value, field) ?? (value[field] is null ? null : throw Schema($"has invalid {field}"));
    }

    private static string? OptionalString(JsonObject value, string field, string displayField)
    {
        if (!value.ContainsKey(field))
        {
            return null;
        }

        return SessionJson.GetString(value, field) ?? throw Schema($"has invalid {displayField}");
    }

    private static string? OptionalFactString(JsonObject value, string field)
    {
        if (!value.ContainsKey(field))
        {
            return null;
        }

        return SessionJson.GetString(value, field) ?? throw Schema($"has invalid {field}");
    }

    private static string? GetNullableId(JsonObject value, string field)
    {
        if (!value.ContainsKey(field) || value[field] is null)
        {
            return null;
        }

        return SessionJson.GetString(value, field);
    }

    private static long? GetLong(JsonObject value, string field) => GetInteger(value[field]);

    private static long? GetInteger(JsonNode? value)
    {
        if (value is not JsonValue json)
        {
            return null;
        }

        if (!json.TryGetValue<long>(out var integer))
        {
            return null;
        }

        return integer;
    }

    private static bool GetBool(JsonObject value, string field)
    {
        return value[field] is JsonValue json && json.TryGetValue<bool>(out var result) && result;
    }

    private static string[] GetStringArray(JsonNode? value)
    {
        return value is JsonArray array
            ? array.Select(static item => item is JsonValue json && json.TryGetValue<string>(out var text) ? text : string.Empty).ToArray()
            : [];
    }

    private static AgentMessage[] GetMessageArray(JsonNode? value)
    {
        return value is JsonArray array
            ? array.OfType<JsonObject>().Select(AgentMessage.FromJson).ToArray()
            : [];
    }

    private static Entry[] GetEntryArray(JsonNode? value)
    {
        return value is JsonArray array
            ? array.OfType<JsonObject>().Select(item => DecodeEntryObject(item, requireStorage: false)).ToArray()
            : [];
    }

    private static CustomEntry EmptyCustomTarget() => new()
    {
        Id = string.Empty,
        CustomType = string.Empty,
    };

    private static JsonlDecodeError Schema(string message) => new(JsonlDecodeErrorKind.Schema, message);

    private static void CopyInto(JsonObject destination, JsonObject source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = SessionJson.Clone(pair.Value);
        }
    }

    private static void Put(JsonObject destination, string key, JsonNode value, bool preserveOrder)
    {
        if (preserveOrder || !destination.ContainsKey(key))
        {
            destination[key] = value;
        }
        else
        {
            destination.Add(key, value);
        }
    }

    private static void SetOptionalString(JsonObject destination, string key, string? value)
    {
        if (value is null)
        {
            destination.Remove(key);
        }
        else
        {
            destination[key] = SessionJson.Value(value);
        }
    }
}
