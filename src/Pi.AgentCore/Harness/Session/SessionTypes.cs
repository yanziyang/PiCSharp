using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

using Pi.Ai;

namespace Pi.AgentCore.Harness.Session;

/// <summary>String constants used by session errors.</summary>
public static class SessionErrorCode
{
    /// <summary>The requested session or entry does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>The requested identifier is already in use.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>A persisted entry or mutation is invalid.</summary>
    public const string InvalidEntry = "invalid_entry";

    /// <summary>A caller supplied a non-durable payload.</summary>
    public const string InvalidPayload = "invalid_payload";

    /// <summary>A lane is missing or invalid.</summary>
    public const string InvalidLane = "invalid_lane";

    /// <summary>A query is invalid.</summary>
    public const string InvalidQuery = "invalid_query";

    /// <summary>A fork target is invalid.</summary>
    public const string InvalidForkTarget = "invalid_fork_target";

    /// <summary>The backend failed to complete an operation.</summary>
    public const string Storage = "storage";
}

/// <summary>Error raised by a session backend.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name is the upstream public session error contract.")]
public sealed class SessionError : Exception
{
    /// <summary>The stable upstream-compatible error code.</summary>
    public string Code { get; }

    /// <summary>Creates a session error.</summary>
    public SessionError(string code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}

/// <summary>Generator used for provisioned entry identifiers.</summary>
public interface IIdGenerator
{
    /// <summary>Returns the next identifier.</summary>
    string Next();
}

/// <summary>
/// Extensible agent message. The JSON object is retained so custom harness messages and fields
/// survive a read/write cycle without requiring a C# type for every application extension.
/// </summary>
public sealed record AgentMessage
{
    /// <summary>Message JSON, including its role discriminator.</summary>
    public JsonObject Value { get; init; }

    /// <summary>Creates a message from a JSON object.</summary>
    public AgentMessage(JsonObject value)
    {
        Value = (JsonObject)value.DeepClone();
    }

    /// <summary>Creates a message from one of the shared Pi AI message types.</summary>
    public AgentMessage(Message value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = SessionJson.MessageToJson(value);
    }

    /// <summary>Creates a message from a JSON object.</summary>
    public static AgentMessage FromJson(JsonObject value) => new(value);

    /// <summary>Creates a message from a shared Pi AI message.</summary>
    public static AgentMessage FromPiMessage(Message value) => new(value);

    /// <summary>Message role, when present.</summary>
    public string? Role => SessionJson.GetString(Value, "role");

    /// <summary>Converts a standard Pi AI message; custom roles return null.</summary>
    public Message? ToPiMessage() => SessionJson.JsonToMessage(Value);

    /// <summary>Implicitly wraps a standard Pi AI message.</summary>
    public static implicit operator AgentMessage(Message value) => new(value);
}

/// <summary>Base for storage-assigned session entries.</summary>
public abstract record Entry
{
    /// <summary>Entry discriminator.</summary>
    public abstract string Type { get; }

    /// <summary>Stable entry identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Shared storage sequence.</summary>
    public long Seq { get; init; }

    /// <summary>Parent entry on the appending lane.</summary>
    public string? ParentId { get; init; }

    /// <summary>Unix timestamp in milliseconds assigned by storage.</summary>
    public long Timestamp { get; init; }

    // The parsed source object lets a newer producer's unknown fields survive a fork.
    internal JsonObject? RawFields { get; init; }
}

/// <summary>Message entry.</summary>
public sealed record MessageEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "message";

    /// <summary>Agent message payload.</summary>
    public required AgentMessage Message { get; init; }

    /// <summary>Whether the tool result should terminate the operation.</summary>
    public bool? Terminate { get; init; }
}

/// <summary>Model selection change entry.</summary>
public sealed record ModelChangeEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "model_change";

    /// <summary>Provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Model identifier.</summary>
    public required string ModelId { get; init; }
}

/// <summary>Thinking-level change entry.</summary>
public sealed record ThinkingLevelEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "thinking_level_change";

    /// <summary>Requested thinking level.</summary>
    public required string ThinkingLevel { get; init; }
}

/// <summary>Active-tool set change entry.</summary>
public sealed record ActiveToolsEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "active_tools_change";

    /// <summary>Names of active tools.</summary>
    public IReadOnlyList<string> ActiveToolNames { get; init; } = [];
}

/// <summary>Compaction summary entry.</summary>
public sealed record CompactionEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "compaction";

    /// <summary>Conversation summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Messages retained after compaction.</summary>
    public IReadOnlyList<AgentMessage> RetainedTail { get; init; } = [];

    /// <summary>Token count before compaction.</summary>
    public long TokensBefore { get; init; }

    /// <summary>Optional compaction details.</summary>
    public JsonNode? Details { get; init; }

    /// <summary>Whether details was explicitly supplied, including a JSON null.</summary>
    public bool DetailsPresent { get; init; }

    /// <summary>Optional compaction usage.</summary>
    public Usage? Usage { get; init; }
}

/// <summary>Branch-navigation summary entry.</summary>
public sealed record BranchSummaryEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "branch_summary";

    /// <summary>Entry from which the summarized branch came.</summary>
    public required string FromId { get; init; }

    /// <summary>Branch summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Optional branch details.</summary>
    public JsonNode? Details { get; init; }

    /// <summary>Whether details was explicitly supplied, including a JSON null.</summary>
    public bool DetailsPresent { get; init; }

    /// <summary>Optional branch-summary usage.</summary>
    public Usage? Usage { get; init; }
}

/// <summary>Application-defined entry.</summary>
public sealed record CustomEntry : Entry
{
    /// <inheritdoc />
    public override string Type => "custom";

    /// <summary>Application-defined custom type.</summary>
    public required string CustomType { get; init; }

    /// <summary>Application-defined JSON data.</summary>
    public JsonNode? Data { get; init; }

    /// <summary>Whether data was explicitly supplied, including a JSON null.</summary>
    public bool DataPresent { get; init; }
}

/// <summary>Base for storage-assigned lane records.</summary>
public abstract record LaneRecord
{
    /// <summary>Record discriminator.</summary>
    public abstract string Type { get; }

    /// <summary>Stable record identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Shared storage sequence.</summary>
    public long Seq { get; init; }

    /// <summary>Lane owning the record.</summary>
    public required string Lane { get; init; }

    /// <summary>Unix timestamp in milliseconds assigned by storage.</summary>
    public long Timestamp { get; init; }

    // The parsed source object lets a newer producer's unknown fields survive a fork.
    internal JsonObject? RawFields { get; init; }
}

/// <summary>Base for operation intents.</summary>
public abstract record OperationIntent
{
    /// <summary>Operation kind.</summary>
    public abstract string Kind { get; }

    internal JsonObject? RawFields { get; init; }
}

/// <summary>Run operation intent.</summary>
public sealed record RunOperationIntent : OperationIntent
{
    /// <inheritdoc />
    public override string Kind => "run";

    /// <summary>Normalized original prompt.</summary>
    public IReadOnlyList<AgentMessage> OriginalPrompt { get; init; } = [];

    /// <summary>Provisioned initial entries.</summary>
    public IReadOnlyList<Entry> InitialMessages { get; init; } = [];

    /// <summary>Optional system prompt override.</summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>Optional extension resume state.</summary>
    public JsonObject? ResumeData { get; init; }
}

/// <summary>Compaction operation intent.</summary>
public sealed record CompactionOperationIntent : OperationIntent
{
    /// <inheritdoc />
    public override string Kind => "compaction";

    /// <summary>Optional caller instructions.</summary>
    public string? CustomInstructions { get; init; }

    /// <summary>Entry that will contain the result.</summary>
    public required string ResultEntryId { get; init; }
}

/// <summary>Navigation operation intent.</summary>
public sealed record NavigationOperationIntent : OperationIntent
{
    /// <inheritdoc />
    public override string Kind => "navigation";

    /// <summary>Navigation target, or null for the current branch root.</summary>
    public string? TargetId { get; init; }

    /// <summary>Whether a summary should be generated.</summary>
    public bool Summarize { get; init; }

    /// <summary>Optional caller instructions.</summary>
    public string? CustomInstructions { get; init; }

    /// <summary>Optional label for the navigation result.</summary>
    public string? Label { get; init; }

    /// <summary>Optional summary entry identifier.</summary>
    public string? SummaryEntryId { get; init; }
}

/// <summary>Operation-started record.</summary>
public sealed record OperationStartedRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "operation_started";

    /// <summary>Lane leaf at operation start.</summary>
    public string? SourceLeafId { get; init; }

    /// <summary>Operation intent.</summary>
    public required OperationIntent Intent { get; init; }
}

/// <summary>Abort-requested record.</summary>
public sealed record AbortRequestedRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "abort_requested";

    /// <summary>Operation being aborted.</summary>
    public required string RunId { get; init; }
}

/// <summary>Operation-finished record.</summary>
public sealed record OperationFinishedRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "operation_finished";

    /// <summary>Completed operation.</summary>
    public required string RunId { get; init; }

    /// <summary>Operation outcome.</summary>
    public required string Outcome { get; init; }

    /// <summary>Optional failure information.</summary>
    public SessionErrorInfo? Error { get; init; }
}

/// <summary>Failure information retained on a finished operation.</summary>
public sealed record SessionErrorInfo
{
    /// <summary>Stable application error code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable error message.</summary>
    public required string Message { get; init; }
}

/// <summary>Step-attempt record.</summary>
public sealed record StepAttemptRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "step_attempt";

    /// <summary>Owning operation.</summary>
    public required string RunId { get; init; }

    /// <summary>Step kind.</summary>
    public required string Step { get; init; }

    /// <summary>Attempt number.</summary>
    public int Attempt { get; init; }

    /// <summary>Result entry identifier.</summary>
    public required string ResultEntryId { get; init; }

    /// <summary>Compaction reason when the step is compaction.</summary>
    public string? CompactionReason { get; init; }
}

/// <summary>Tool-started record.</summary>
public sealed record ToolStartedRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "tool_started";

    /// <summary>Owning operation.</summary>
    public required string RunId { get; init; }

    /// <summary>Assistant entry containing the tool call.</summary>
    public required string AssistantEntryId { get; init; }

    /// <summary>Tool-call content index.</summary>
    public int ToolIndex { get; init; }

    /// <summary>Tool-call identifier.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>Effective tool arguments.</summary>
    public JsonObject EffectiveArgs { get; init; } = new();

    /// <summary>Result entry identifier.</summary>
    public required string ResultEntryId { get; init; }

    /// <summary>Whether replay is allowed.</summary>
    public required string Replay { get; init; }
}

/// <summary>Queue-enqueued record.</summary>
public sealed record QueueEnqueuedRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "queue_enqueued";

    /// <summary>Queue kind.</summary>
    public required string Queue { get; init; }

    /// <summary>Owning run for steer/follow-up queues.</summary>
    public string? RunId { get; init; }

    /// <summary>Provisioned queued entry.</summary>
    public required Entry Target { get; init; }
}

/// <summary>Queue-cancelled record.</summary>
public sealed record QueueCancelledRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "queue_cancelled";

    /// <summary>Owning run when cancellation is run-scoped.</summary>
    public string? RunId { get; init; }

    /// <summary>Queued entry identifier.</summary>
    public required string EntryId { get; init; }
}

/// <summary>Deferred-write record.</summary>
public sealed record WriteDeferredRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "write_deferred";

    /// <summary>Owning operation.</summary>
    public required string RunId { get; init; }

    /// <summary>Provisioned target entry.</summary>
    public required Entry Target { get; init; }
}

/// <summary>Usage record.</summary>
public sealed record UsageRecord : LaneRecord
{
    /// <inheritdoc />
    public override string Type => "usage";

    /// <summary>Usage cause.</summary>
    public required string Cause { get; init; }

    /// <summary>Usage and cost values.</summary>
    public Usage Usage { get; init; } = new();

    /// <summary>Owning run for operation-owned usage.</summary>
    public string? RunId { get; init; }

    /// <summary>Related entry.</summary>
    public string? EntryId { get; init; }

    /// <summary>Attempt number.</summary>
    public int? Attempt { get; init; }

    /// <summary>Assistant/deferred stop reason.</summary>
    public string? StopReason { get; init; }

    /// <summary>Tool-call identifier.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Optional adjustment details.</summary>
    public JsonNode? Details { get; init; }

    /// <summary>Whether details was explicitly supplied, including a JSON null.</summary>
    public bool DetailsPresent { get; init; }
}

/// <summary>Base for append-only state mutations.</summary>
public abstract record SessionMutation
{
    /// <summary>Shared mutation sequence.</summary>
    public long Seq { get; init; }
}

/// <summary>Entry append mutation.</summary>
public sealed record EntryMutation : SessionMutation
{
    /// <summary>Optional lane that owns the appended entry.</summary>
    public string? Lane { get; init; }

    /// <summary>Entry payload.</summary>
    public required Entry Entry { get; init; }
}

/// <summary>Record append mutation.</summary>
public sealed record RecordMutation : SessionMutation
{
    /// <summary>Record payload.</summary>
    public required LaneRecord Record { get; init; }
}

/// <summary>Lane pointer mutation.</summary>
public sealed record LaneMutation : SessionMutation
{
    /// <summary>Lane name.</summary>
    public required string Lane { get; init; }

    /// <summary>New leaf entry, or null.</summary>
    public string? LeafId { get; init; }
}

/// <summary>Latest-value fact mutation.</summary>
public sealed record FactMutation : SessionMutation
{
    /// <summary>Fact discriminator.</summary>
    public required string Fact { get; init; }

    /// <summary>Label target for label facts.</summary>
    public string? TargetId { get; init; }

    /// <summary>Name value for name facts.</summary>
    public string? Name { get; init; }

    /// <summary>Label value for label facts.</summary>
    public string? Label { get; init; }
}

/// <summary>Pointer to a lane's current leaf.</summary>
public sealed record LanePointer
{
    /// <summary>Lane name.</summary>
    public required string Lane { get; init; }

    /// <summary>Leaf entry, or null when empty.</summary>
    public string? LeafId { get; set; }
}

/// <summary>Statistics accumulated by a session ledger.</summary>
public sealed record SessionStats
{
    /// <summary>Number of message entries.</summary>
    public long MessageCount { get; init; }

    /// <summary>Cached input tokens.</summary>
    public long CachedTokens { get; init; }

    /// <summary>Non-cached input and written-cache tokens.</summary>
    public long UncachedTokens { get; init; }

    /// <summary>Total provider-reported tokens.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Total recorded cost.</summary>
    public double CostTotal { get; init; }
}

/// <summary>Newest-first or oldest-first order.</summary>
public enum EntryOrder
{
    /// <summary>Newest sequence first.</summary>
    NewestFirst,

    /// <summary>Oldest sequence first.</summary>
    OldestFirst,
}

/// <summary>Cursor for a session query.</summary>
public sealed record EntryCursor
{
    /// <summary>Exclusive sequence boundary.</summary>
    public long AfterSeq { get; init; }
}

/// <summary>Entry query.</summary>
public record EntryQuery
{
    /// <summary>Exact entry type filter.</summary>
    public string? Type { get; init; }

    /// <summary>Exact custom type filter.</summary>
    public string? CustomType { get; init; }

    /// <summary>Result ordering.</summary>
    public EntryOrder Order { get; init; } = EntryOrder.NewestFirst;

    /// <summary>Maximum number of results.</summary>
    public int? Limit { get; init; }

    /// <summary>Exclusive sequence cursor.</summary>
    public EntryCursor? Cursor { get; init; }
}

/// <summary>Optional bounds for a branch query.</summary>
public sealed record BranchBounds
{
    /// <summary>Starting leaf. Null means the view's current leaf.</summary>
    public string? Start { get; init; }

    /// <summary>Inclusive stop type.</summary>
    public string? StopAtType { get; init; }

    /// <summary>Inclusive stop identifier.</summary>
    public string? StopAtId { get; init; }
}

/// <summary>Record query.</summary>
public sealed record RecordQuery
{
    /// <summary>Exact lane filter.</summary>
    public string? Lane { get; init; }

    /// <summary>Exact record type filter.</summary>
    public string? Type { get; init; }

    /// <summary>Operation identity filter.</summary>
    public string? RunId { get; init; }

    /// <summary>Operation kind filter.</summary>
    public string? OperationKind { get; init; }

    /// <summary>Exclusive lower sequence bound.</summary>
    public long? AfterSeq { get; init; }

    /// <summary>Result ordering.</summary>
    public EntryOrder Order { get; init; } = EntryOrder.NewestFirst;

    /// <summary>Maximum number of results.</summary>
    public int? Limit { get; init; }
}

/// <summary>Log query options.</summary>
public sealed record LogOptions
{
    /// <summary>Exclusive sequence boundary.</summary>
    public long? AfterSeq { get; init; }

    /// <summary>Maximum number of results.</summary>
    public int? Limit { get; init; }
}

/// <summary>Base log item.</summary>
public abstract record LogItem
{
    /// <summary>Log item kind.</summary>
    public abstract string Kind { get; }

    /// <summary>Shared sequence.</summary>
    public long Seq { get; init; }
}

/// <summary>Entry log item.</summary>
public sealed record EntryLogItem : LogItem
{
    /// <inheritdoc />
    public override string Kind => "entry";

    /// <summary>Entry payload.</summary>
    public required Entry Entry { get; init; }
}

/// <summary>Record log item.</summary>
public sealed record RecordLogItem : LogItem
{
    /// <inheritdoc />
    public override string Kind => "record";

    /// <summary>Record payload.</summary>
    public required LaneRecord Record { get; init; }
}

/// <summary>Lane-pointer log item.</summary>
public sealed record LaneLogItem : LogItem
{
    /// <inheritdoc />
    public override string Kind => "lane";

    /// <summary>Lane name.</summary>
    public required string Lane { get; init; }

    /// <summary>Lane leaf.</summary>
    public string? LeafId { get; init; }
}

/// <summary>Name-fact log item.</summary>
public sealed record NameFactLogItem : LogItem
{
    /// <inheritdoc />
    public override string Kind => "fact";

    /// <summary>Fact discriminator.</summary>
    public const string Fact = "name";

    /// <summary>Current name, or null when cleared.</summary>
    public string? Name { get; init; }
}

/// <summary>Label-fact log item.</summary>
public sealed record LabelFactLogItem : LogItem
{
    /// <inheritdoc />
    public override string Kind => "fact";

    /// <summary>Fact discriminator.</summary>
    public const string Fact = "label";

    /// <summary>Labeled entry identifier.</summary>
    public required string TargetId { get; init; }

    /// <summary>Current label, or null when cleared.</summary>
    public string? Label { get; init; }
}

/// <summary>Session metadata shared by all backends.</summary>
public record SessionMetadata
{
    /// <summary>Logical session identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Creation time in Unix milliseconds.</summary>
    public long CreatedAt { get; init; }

    /// <summary>Optional parent session identifier.</summary>
    public string? ParentSessionId { get; init; }
}

/// <summary>Session creation options.</summary>
public record SessionCreateOptions
{
    /// <summary>Optional explicit identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Optional parent session identifier.</summary>
    public string? ParentSessionId { get; init; }
}

/// <summary>Branch/tree fork options.</summary>
public sealed record ForkOptions
{
    /// <summary>Fork scope; branch is the default.</summary>
    public string Scope { get; init; } = "branch";

    /// <summary>Optional branch entry target.</summary>
    public string? EntryId { get; init; }

    /// <summary>Whether the target itself is included.</summary>
    public string? Position { get; init; }
}

/// <summary>Session repository abstraction.</summary>
public interface ISessionRepository<TMetadata, in TCreateOptions>
    where TMetadata : SessionMetadata
    where TCreateOptions : SessionCreateOptions
{
    /// <summary>Creates a session.</summary>
    Task<Session<TMetadata>> CreateAsync(TCreateOptions options, CancellationToken cancellationToken = default);

    /// <summary>Opens an existing session.</summary>
    Task<Session<TMetadata>> OpenAsync(TMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Lists session metadata without opening sessions.</summary>
    Task<IReadOnlyList<TMetadata>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a session idempotently.</summary>
    Task DeleteAsync(TMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Forks a session.</summary>
    Task<Session<TMetadata>> ForkAsync(
        TMetadata source,
        ForkOptions options,
        TCreateOptions createOptions,
        CancellationToken cancellationToken = default);
}

/// <summary>Storage implementation used by a session.</summary>
public interface ISessionStorage<TMetadata>
    where TMetadata : SessionMetadata
{
    /// <summary>Gets metadata.</summary>
    Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets lane pointers in insertion order.</summary>
    Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a lane.</summary>
    Task CreateLaneAsync(string lane, string? at, CancellationToken cancellationToken = default);

    /// <summary>Moves a lane.</summary>
    Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default);

    /// <summary>Appends an entry to a lane.</summary>
    Task<Entry> AppendEntryAsync(Entry entry, string lane, CancellationToken cancellationToken = default);

    /// <summary>Appends a record.</summary>
    Task<LaneRecord> AppendRecordAsync(LaneRecord record, CancellationToken cancellationToken = default);

    /// <summary>Gets an entry.</summary>
    Task<Entry?> GetEntryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Finds entries.</summary>
    Task<IReadOnlyList<Entry>> FindEntriesAsync(EntryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Finds entries on an explicit branch.</summary>
    Task<IReadOnlyList<Entry>> FindEntriesOnBranchAsync(
        EntryQuery query,
        string start,
        BranchBounds bounds,
        CancellationToken cancellationToken = default);

    /// <summary>Finds records.</summary>
    Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(RecordQuery query, CancellationToken cancellationToken = default);

    /// <summary>Finds open operation starts for a lane.</summary>
    Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the mutation log.</summary>
    Task<IReadOnlyList<LogItem>> GetLogAsync(LogOptions options, CancellationToken cancellationToken = default);

    /// <summary>Gets the current session name.</summary>
    Task<string?> GetNameAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets or clears the current session name.</summary>
    Task SetNameAsync(string? name, CancellationToken cancellationToken = default);

    /// <summary>Gets an entry label.</summary>
    Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Sets or clears an entry label.</summary>
    Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default);

    /// <summary>Gets accumulated statistics.</summary>
    Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
