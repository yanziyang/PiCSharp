using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Compaction;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using Pi.Telemetry;

namespace Pi.AgentCore.Harness;

/// <summary>Indicates that a lane already owns an operation.</summary>
public sealed class LaneBusy : TaggedErrorValue
{
    /// <summary>Creates a lane-busy error.</summary>
    public LaneBusy(string lane, string operationId, string operationKind, string message)
        : base(nameof(LaneBusy), message)
    {
        Lane = lane;
        OperationId = operationId;
        OperationKind = operationKind;
    }

    /// <summary>Busy lane.</summary>
    public string Lane { get; }

    /// <summary>Active operation identifier.</summary>
    public string OperationId { get; }

    /// <summary>Active operation kind.</summary>
    public string OperationKind { get; }
}

/// <summary>Indicates that durable recovery cannot resolve required identities.</summary>
public sealed class MissingIdentities : TaggedErrorValue
{
    /// <summary>Creates a missing-identities error.</summary>
    public MissingIdentities(string lane, IReadOnlyList<string> tools, IReadOnlyList<string> models, string message)
        : base(nameof(MissingIdentities), message)
    {
        Lane = lane;
        Tools = tools.ToArray();
        Models = models.ToArray();
    }

    /// <summary>Affected lane.</summary>
    public string Lane { get; }

    /// <summary>Missing tool names.</summary>
    public IReadOnlyList<string> Tools { get; }

    /// <summary>Missing model identifiers.</summary>
    public IReadOnlyList<string> Models { get; }
}

/// <summary>Indicates that no run is active on a lane.</summary>
public sealed class NoActiveRun : TaggedErrorValue
{
    /// <summary>Creates a no-active-run error.</summary>
    public NoActiveRun(string lane, string message) : base(nameof(NoActiveRun), message) => Lane = lane;

    /// <summary>Affected lane.</summary>
    public string Lane { get; }
}

/// <summary>Indicates that no operation is active on a lane.</summary>
public sealed class NoActiveOperation : TaggedErrorValue
{
    /// <summary>Creates a no-active-operation error.</summary>
    public NoActiveOperation(string lane, string message) : base(nameof(NoActiveOperation), message) => Lane = lane;

    /// <summary>Affected lane.</summary>
    public string Lane { get; }
}

/// <summary>Indicates that no suspended operation can be resumed.</summary>
public sealed class NothingToResume : TaggedErrorValue
{
    /// <summary>Creates a nothing-to-resume error.</summary>
    public NothingToResume(string lane, string message) : base(nameof(NothingToResume), message) => Lane = lane;

    /// <summary>Affected lane.</summary>
    public string Lane { get; }
}

/// <summary>Indicates that a supplied message is invalid for the operation.</summary>
public sealed class InvalidMessage : TaggedErrorValue
{
    /// <summary>Creates an invalid-message error.</summary>
    public InvalidMessage(string lane, string reason, string message) : base(nameof(InvalidMessage), message)
    {
        Lane = lane;
        Reason = reason;
    }

    /// <summary>Affected lane.</summary>
    public string Lane { get; }

    /// <summary>Stable rejection reason.</summary>
    public string Reason { get; }
}

/// <summary>Indicates that a requested skill is unavailable.</summary>
public sealed class UnknownSkill : TaggedErrorValue
{
    /// <summary>Creates an unknown-skill error.</summary>
    public UnknownSkill(string name, string message) : base(nameof(UnknownSkill), message) => Name = name;

    /// <summary>Requested skill name.</summary>
    public string Name { get; }
}

/// <summary>Indicates that a requested prompt template is unavailable.</summary>
public sealed class UnknownTemplate : TaggedErrorValue
{
    /// <summary>Creates an unknown-template error.</summary>
    public UnknownTemplate(string name, string message) : base(nameof(UnknownTemplate), message) => Name = name;

    /// <summary>Requested template name.</summary>
    public string Name { get; }
}

/// <summary>Indicates that a requested branch target is unavailable.</summary>
public sealed class UnknownTarget : TaggedErrorValue
{
    /// <summary>Creates an unknown-target error.</summary>
    public UnknownTarget(string targetId, string message) : base(nameof(UnknownTarget), message) => TargetId = targetId;

    /// <summary>Requested target identifier.</summary>
    public string TargetId { get; }
}

/// <summary>Indicates that a queued item is unavailable.</summary>
public sealed class UnknownQueueItem : TaggedErrorValue
{
    /// <summary>Creates an unknown-queue-item error.</summary>
    public UnknownQueueItem(string lane, string entryId, string message) : base(nameof(UnknownQueueItem), message)
    {
        Lane = lane;
        EntryId = entryId;
    }

    /// <summary>Affected lane.</summary>
    public string Lane { get; }

    /// <summary>Requested queued entry identifier.</summary>
    public string EntryId { get; }
}

/// <summary>Indicates that a lane name is already in use.</summary>
public sealed class LaneExists : TaggedErrorValue
{
    /// <summary>Creates a lane-exists error.</summary>
    public LaneExists(string lane, string message) : base(nameof(LaneExists), message) => Lane = lane;

    /// <summary>Conflicting lane name.</summary>
    public string Lane { get; }
}

/// <summary>Indicates that a lane name or target is invalid.</summary>
public sealed class InvalidLane : TaggedErrorValue
{
    /// <summary>Creates an invalid-lane error.</summary>
    public InvalidLane(string lane, string reason, string message) : base(nameof(InvalidLane), message)
    {
        Lane = lane;
        Reason = reason;
    }

    /// <summary>Invalid lane name.</summary>
    public string Lane { get; }

    /// <summary>Stable rejection reason.</summary>
    public string Reason { get; }
}

/// <summary>Indicates that the session has no compactable content.</summary>
public sealed class NothingToCompact : TaggedErrorValue
{
    /// <summary>Creates a nothing-to-compact error.</summary>
    public NothingToCompact(string lane, string message) : base(nameof(NothingToCompact), message) => Lane = lane;

    /// <summary>Affected lane.</summary>
    public string Lane { get; }
}

/// <summary>Indicates that the harness has been closed.</summary>
public sealed class Closed : TaggedErrorValue
{
    /// <summary>Creates a closed-harness error.</summary>
    public Closed(string message) : base(nameof(Closed), message)
    {
    }
}

/// <summary>Wraps a fault raised while driving the harness.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The exception name is part of the upstream Pi harness API.")]
public sealed class HarnessFault : Exception
{
    /// <summary>Creates a harness fault.</summary>
    public HarnessFault(string message, object? cause)
        : base(message, cause as Exception)
    {
        Cause = cause;
    }

    /// <summary>Original fault value.</summary>
    public object? Cause { get; }
}

/// <summary>Raised when the harness closes during an active operation.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The exception name is part of the upstream Pi harness API.")]
public sealed class HarnessClosed : Exception
{
    /// <summary>Creates the standard closed-harness exception.</summary>
    public HarnessClosed()
        : base("AgentHarness was closed while the operation was active")
    {
    }
}

/// <summary>Raised for scaffold operations that are intentionally not implemented yet.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The exception name is part of the upstream Pi harness API.")]
public sealed class HarnessNotImplemented : Exception
{
    /// <summary>Creates a not-implemented exception for one operation.</summary>
    public HarnessNotImplemented(string operation)
        : base($"AgentHarness.{operation} is not implemented yet") => Operation = operation;

    /// <summary>Operation that is not implemented.</summary>
    public string Operation { get; }
}

/// <summary>Stable error information carried by a failed operation outcome.</summary>
public sealed record OperationError
{
    /// <summary>Stable error code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable error message.</summary>
    public required string Message { get; init; }
}

/// <summary>Outcome of one run invocation.</summary>
public abstract record RunOutcome
{
    /// <summary>Outcome discriminator.</summary>
    public abstract string Kind { get; }
}

/// <summary>Completed run outcome.</summary>
public sealed record CompletedRunOutcome(string LeafId, string FinalEntryId, AssistantMessage FinalMessage) : RunOutcome
{
    /// <inheritdoc />
    public override string Kind => "completed";
}

/// <summary>Aborted run outcome.</summary>
public sealed record AbortedRunOutcome(string LeafId, string FinalEntryId, AssistantMessage FinalMessage) : RunOutcome
{
    /// <inheritdoc />
    public override string Kind => "aborted";
}

/// <summary>Failed run outcome.</summary>
public sealed record FailedRunOutcome(
    string LeafId,
    OperationError Error,
    string? FinalEntryId = null,
    AssistantMessage? FinalMessage = null) : RunOutcome
{
    /// <inheritdoc />
    public override string Kind => "failed";
}

/// <summary>Suspended run outcome.</summary>
public sealed record SuspendedRunOutcome(
    string LeafId,
    string FinalEntryId,
    DeferredHandle Deferred) : RunOutcome
{
    /// <inheritdoc />
    public override string Kind => "suspended";
}

/// <summary>Outcome of one compaction invocation.</summary>
public abstract record CompactionOutcome
{
    /// <summary>Outcome discriminator.</summary>
    public abstract string Kind { get; }
}

/// <summary>Completed compaction outcome.</summary>
public sealed record CompletedCompactionOutcome(string LeafId, CompactionEntry Entry) : CompactionOutcome
{
    /// <inheritdoc />
    public override string Kind => "completed";
}

/// <summary>Declined compaction outcome.</summary>
public sealed record DeclinedCompactionOutcome(string LeafId) : CompactionOutcome
{
    /// <inheritdoc />
    public override string Kind => "declined";
}

/// <summary>Aborted compaction outcome.</summary>
public sealed record AbortedCompactionOutcome(string LeafId) : CompactionOutcome
{
    /// <inheritdoc />
    public override string Kind => "aborted";
}

/// <summary>Failed compaction outcome.</summary>
public sealed record FailedCompactionOutcome(string LeafId, OperationError Error) : CompactionOutcome
{
    /// <inheritdoc />
    public override string Kind => "failed";
}

/// <summary>Outcome of one navigation invocation.</summary>
public abstract record NavigationOutcome
{
    /// <summary>Outcome discriminator.</summary>
    public abstract string Kind { get; }
}

/// <summary>Completed navigation outcome.</summary>
public sealed record CompletedNavigationOutcome(string? NewLeafId, BranchSummaryEntry? SummaryEntry = null) : NavigationOutcome
{
    /// <inheritdoc />
    public override string Kind => "completed";
}

/// <summary>Declined navigation outcome.</summary>
public sealed record DeclinedNavigationOutcome(string? LeafId) : NavigationOutcome
{
    /// <inheritdoc />
    public override string Kind => "declined";
}

/// <summary>Aborted navigation outcome.</summary>
public sealed record AbortedNavigationOutcome(string? LeafId) : NavigationOutcome
{
    /// <inheritdoc />
    public override string Kind => "aborted";
}

/// <summary>Failed navigation outcome.</summary>
public sealed record FailedNavigationOutcome(string? LeafId, OperationError Error) : NavigationOutcome
{
    /// <inheritdoc />
    public override string Kind => "failed";
}

/// <summary>Value returned by a run operation.</summary>
public sealed record RunResultValue(string RunId, RunOutcome Outcome);

/// <summary>Value returned by a compaction operation.</summary>
public sealed record CompactionResultValue(string RunId, CompactionOutcome Outcome);

/// <summary>Value returned by a navigation operation.</summary>
public sealed record NavigationResultValue(string RunId, NavigationOutcome Outcome);

/// <summary>Value returned when a queue item is accepted.</summary>
public sealed record QueueResultValue(string EntryId);

/// <summary>Value returned when queued cancellation completes.</summary>
public sealed record CancelQueuedResultValue(string Outcome);

/// <summary>Value returned when abort processing completes.</summary>
public sealed record AbortResultValue(string RunId, IReadOnlyList<AgentMessage> Steer, IReadOnlyList<AgentMessage> FollowUp);

/// <summary>Value returned when an operation resumes.</summary>
public sealed record ResumeResultValue(string Operation, string RunId, object Outcome);

/// <summary>Outcome of a suspended-operation inspection.</summary>
public sealed record AbortingOperation(IReadOnlyList<AgentMessage> Steer, IReadOnlyList<AgentMessage> FollowUp);

/// <summary>Missing identities reported by recovery.</summary>
public sealed record MissingIdentitySet(IReadOnlyList<string> Tools, IReadOnlyList<string> Models);

/// <summary>Operation suspended in a lane.</summary>
public sealed record SuspendedOperation
{
    /// <summary>Owning lane.</summary>
    public required string Lane { get; init; }

    /// <summary>Operation kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Durable operation identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Start timestamp in Unix milliseconds.</summary>
    public long StartedAt { get; init; }

    /// <summary>Suspension reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Original prompt, when retained.</summary>
    public IReadOnlyList<AgentMessage>? Prompt { get; init; }

    /// <summary>Deferred provider handle, when suspended on a deferred response.</summary>
    public DeferredHandle? Deferred { get; init; }

    /// <summary>Abort messages retained during reconciliation.</summary>
    public AbortingOperation? Aborting { get; init; }

    /// <summary>Identities unavailable during recovery.</summary>
    public required MissingIdentitySet Missing { get; init; }
}

/// <summary>Current operation state for a lane.</summary>
public sealed record LaneOperation
{
    /// <summary>Operation identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Operation kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Operation status.</summary>
    public required string Status { get; init; }
}

/// <summary>Public lane information.</summary>
public sealed record LaneInfo
{
    /// <summary>Lane name.</summary>
    public required string Name { get; init; }

    /// <summary>Current leaf identifier.</summary>
    public string? LeafId { get; init; }

    /// <summary>Active operation, if any.</summary>
    public LaneOperation? Operation { get; init; }
}

/// <summary>Queued message and its durable entry identifier.</summary>
public sealed record QueuedItem(string EntryId, AgentMessage Message);

/// <summary>Snapshot of one lane.</summary>
public sealed record LaneSnapshot
{
    /// <summary>Lane name.</summary>
    public required string Lane { get; init; }

    /// <summary>Current transcript.</summary>
    public IReadOnlyList<Entry> Transcript { get; init; } = [];

    /// <summary>Current leaf identifier.</summary>
    public string? LeafId { get; init; }

    /// <summary>Current operation.</summary>
    public LaneOperation? Operation { get; init; }

    /// <summary>Pending steering, follow-up and next-run queues.</summary>
    public QueueSnapshot Queues { get; init; } = new();

    /// <summary>Pending session writes.</summary>
    public IReadOnlyList<PendingWrite> PendingWrites { get; init; } = [];

    /// <summary>Whether recovery has faulted this lane.</summary>
    public bool Faulted { get; init; }
}

/// <summary>Snapshot of lane queues.</summary>
public sealed record QueueSnapshot
{
    /// <summary>Steering queue.</summary>
    public IReadOnlyList<QueuedItem> Steer { get; init; } = [];

    /// <summary>Follow-up queue.</summary>
    public IReadOnlyList<QueuedItem> FollowUp { get; init; } = [];

    /// <summary>Next-run queue.</summary>
    public IReadOnlyList<QueuedItem> NextRun { get; init; } = [];
}

/// <summary>Pending provisioned session write.</summary>
public sealed record PendingWrite(string Id, Entry Entry);

/// <summary>Snapshot of all lanes and harness fault state.</summary>
public sealed record SessionSnapshot
{
    /// <summary>Known lanes.</summary>
    public IReadOnlyList<LaneInfo> Lanes { get; init; } = [];

    /// <summary>Whether recovery has faulted the session.</summary>
    public bool Faulted { get; init; }
}

/// <summary>Base for one action exposed by a manual harness driver.</summary>
public abstract record ActionInfo
{
    /// <summary>Action discriminator.</summary>
    public abstract string Kind { get; }
}

/// <summary>Append-entry action.</summary>
public sealed record AppendEntryAction(string EntryType, string EntryId) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "append_entry";
}

/// <summary>Append-record action.</summary>
public sealed record AppendRecordAction(string RecordType) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "append_record";
}

/// <summary>Move-lane action.</summary>
public sealed record MoveLaneAction(string? To) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "move_lane";
}

/// <summary>Set-fact action.</summary>
public sealed record SetFactAction(string Fact) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "set_fact";
}

/// <summary>Try-finish-run action.</summary>
public sealed record TryFinishRunAction(string Outcome) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "try_finish_run";
}

/// <summary>Finish-operation action.</summary>
public sealed record FinishOperationAction(string Outcome) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "finish_operation";
}

/// <summary>Commit-follow-up action.</summary>
public sealed record CommitFollowUpAction : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "commit_follow_up";
}

/// <summary>Consume-queue-item action.</summary>
public sealed record ConsumeQueueItemAction(string Queue, string EntryId) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "consume_queue_item";
}

/// <summary>Apply-pending-write action.</summary>
public sealed record ApplyPendingWriteAction(string EntryId) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "apply_pending_write";
}

/// <summary>Stream-assistant action.</summary>
public sealed record StreamAssistantAction(string Step, int Attempt) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "stream_assistant";
}

/// <summary>Execute-tool action.</summary>
public sealed record ExecuteToolAction(string ToolCallId, string ToolName) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "execute_tool";
}

/// <summary>Fetch-deferred or cancel-deferred action.</summary>
public sealed record DeferredAction(string Operation, string Provider, string Id) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => Operation;
}

/// <summary>Hook invocation action.</summary>
public sealed record HookAction(string Name) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "hook";
}

/// <summary>Sleep action.</summary>
public sealed record SleepAction(double DelayMs) : ActionInfo
{
    /// <inheritdoc />
    public override string Kind => "sleep";
}

/// <summary>Passive hook registry contract.</summary>
public interface IHooks
{
    /// <summary>Registers a handler for a hook name.</summary>
    Action On(string name, Action handler, string? id = null);

    /// <summary>Registers an asynchronous handler for a hook name.</summary>
    Action On(string name, Func<object?, Task> handler, string? id = null);
}

/// <summary>Options for branch navigation.</summary>
public sealed record NavigateOptions
{
    /// <summary>Whether to summarize the abandoned branch.</summary>
    public bool Summarize { get; init; }

    /// <summary>Additional summarization instructions.</summary>
    public string? CustomInstructions { get; init; }

    /// <summary>Optional label for the navigation result.</summary>
    public string? Label { get; init; }
}

/// <summary>Harness tool definition with an optional replay policy.</summary>
public sealed record HarnessTool
{
    /// <summary>Stable tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display label.</summary>
    public required string Label { get; init; }

    /// <summary>Model-visible description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>JSON schema for tool arguments.</summary>
    public JsonNode Parameters { get; init; } = new JsonObject();

    /// <summary>Declared replay policy.</summary>
    public string? Replay { get; init; }

    /// <summary>Tool execution callback.</summary>
    public Func<string, JsonObject, CancellationToken, Action<AgentToolResult>?, Task<AgentToolResult>>? Execute { get; init; }
}

/// <summary>Resources and provider configuration used to create a harness.</summary>
public sealed class AgentHarnessOptions
{
    /// <summary>Durable session backing the harness.</summary>
    public required Session<SessionMetadata> Session { get; init; }

    /// <summary>Provider/model registry.</summary>
    public required Models Models { get; init; }

    /// <summary>Initial model.</summary>
    public required Model Model { get; init; }

    /// <summary>Initial reasoning level.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>Initially active tool names.</summary>
    public IReadOnlyList<string>? ActiveToolNames { get; init; }

    /// <summary>Tools available to the harness.</summary>
    public IReadOnlyList<HarnessTool>? Tools { get; init; }

    /// <summary>Static tool context.</summary>
    public object? ToolContext { get; init; }

    /// <summary>Per-turn tool context provider.</summary>
    public Func<ValueTask<object?>>? ToolContextFactory { get; init; }

    /// <summary>Static system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Asynchronous system prompt provider.</summary>
    public Func<ValueTask<string>>? SystemPromptFactory { get; init; }

    /// <summary>Initial skills and prompt templates.</summary>
    public AgentHarnessResources? Resources { get; init; }

    /// <summary>Initial simple-stream options.</summary>
    public SimpleStreamOptions? StreamOptions { get; init; }

    /// <summary>Assistant retry policy.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>Compaction settings.</summary>
    public CompactionSettings? Compaction { get; init; }

    /// <summary>Steering queue mode.</summary>
    public QueueMode? SteeringMode { get; init; }

    /// <summary>Follow-up queue mode.</summary>
    public QueueMode? FollowUpMode { get; init; }

    /// <summary>Tool execution mode.</summary>
    public ToolExecutionMode? ToolExecution { get; init; }

    /// <summary>Automatic or manual driver mode.</summary>
    public string? Drive { get; init; }

    /// <summary>Optional conversion to provider messages.</summary>
    public Func<IReadOnlyList<AgentMessage>, ValueTask<IReadOnlyList<Message>>>? ToProviderMessages { get; init; }

    /// <summary>Application-defined entry projectors.</summary>
    public IReadOnlyDictionary<string, Func<Entry, ValueTask<IReadOnlyList<AgentMessage>>>>? EntryProjectors { get; init; }

    /// <summary>Parent telemetry context.</summary>
    public TelemetryContext? Context { get; init; }
}

/// <summary>Result of opening an agent harness.</summary>
public sealed record AgentHarnessCreateResult(
    AgentHarness Harness,
    IReadOnlyList<SuspendedOperation> Suspended);

/// <summary>Public operations available on one harness lane.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "The interface name preserves the upstream Pi harness API.")]
public interface AgentLane
{
    /// <summary>Lane name.</summary>
    string Name { get; }

    /// <summary>Returns the current leaf identifier.</summary>
    Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Submits text input.</summary>
    Task<Result<RunResultValue, TaggedErrorValue>> PromptAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default);

    /// <summary>Submits one extensible message.</summary>
    Task<Result<RunResultValue, TaggedErrorValue>> PromptAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Submits multiple extensible messages.</summary>
    Task<Result<RunResultValue, TaggedErrorValue>> PromptAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a named skill.</summary>
    Task<Result<RunResultValue, TaggedErrorValue>> SkillAsync(
        string name,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a named prompt template.</summary>
    Task<Result<RunResultValue, TaggedErrorValue>> PromptFromTemplateAsync(
        string name,
        IReadOnlyList<string>? args = null,
        CancellationToken cancellationToken = default);

    /// <summary>Requests manual compaction.</summary>
    Task<Result<CompactionResultValue, TaggedErrorValue>> CompactAsync(
        string? customInstructions = null,
        CancellationToken cancellationToken = default);

    /// <summary>Navigates to a session tree target.</summary>
    Task<Result<NavigationResultValue, TaggedErrorValue>> NavigateTreeAsync(
        string? targetId,
        NavigateOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a suspended operation.</summary>
    Task<Result<ResumeResultValue, TaggedErrorValue>> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Aborts the active operation.</summary>
    Task<Result<AbortResultValue, TaggedErrorValue>> AbortAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues a steering text message.</summary>
    Task<Result<QueueResultValue, TaggedErrorValue>> SteerAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a steering message.</summary>
    Task<Result<QueueResultValue, TaggedErrorValue>> SteerAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a follow-up text message.</summary>
    Task<Result<QueueResultValue, TaggedErrorValue>> FollowUpAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a follow-up message.</summary>
    Task<Result<QueueResultValue, TaggedErrorValue>> FollowUpAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a message for the next run.</summary>
    Task<Result<QueueResultValue, TaggedErrorValue>> NextRunAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a message for the next run.</summary>
    Task<Result<QueueResultValue, TaggedErrorValue>> NextRunAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels one queued item.</summary>
    Task<Result<CancelQueuedResultValue, TaggedErrorValue>> CancelQueuedAsync(
        string entryId,
        CancellationToken cancellationToken = default);

    /// <summary>Records provider usage.</summary>
    Task<Result<object?, TaggedErrorValue>> RecordUsageAsync(
        Usage usage,
        string? entryId = null,
        JsonNode? details = null,
        CancellationToken cancellationToken = default);

    /// <summary>Waits for the lane to become idle.</summary>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a callback when the lane is idle.</summary>
    Task RunWhenIdleAsync(Func<Task> callback, CancellationToken cancellationToken = default);

    /// <summary>Peeks at the next manual action.</summary>
    Task<ActionInfo?> PeekActionAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes the next manual action.</summary>
    Task<ActionInfo?> ExecuteActionAsync(CancellationToken cancellationToken = default);

    /// <summary>Drives the lane until it reaches completion.</summary>
    Task RunToCompletionAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current model.</summary>
    Task<Model> GetModelAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the current model.</summary>
    Task SetModelAsync(Model model, CancellationToken cancellationToken = default);

    /// <summary>Returns the current reasoning level.</summary>
    Task<string> GetThinkingLevelAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the current reasoning level.</summary>
    Task SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default);

    /// <summary>Returns active tool names.</summary>
    Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes active tool names.</summary>
    Task SetActiveToolsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default);

    /// <summary>Session view associated with this lane.</summary>
    Session<SessionMetadata> Session { get; }

    /// <summary>Creates a lane watch.</summary>
    Task<WatchHandle<LaneSnapshot>> WatchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Initial scaffold of the durable agent harness.</summary>
public sealed class AgentHarness : AgentLane
{
    private readonly Session<SessionMetadata> _durableSession;
    private Model _model;
    private string _thinkingLevel;
    private IReadOnlyList<string> _activeToolNames;
    private IReadOnlyList<HarnessTool> _tools;
    private AgentHarnessResources _resources;
    private SimpleStreamOptions _streamOptions;
    private RetryPolicy _retryPolicy;
    private CompactionSettings _compactionSettings;
    private QueueMode _steeringMode;
    private QueueMode _followUpMode;
    private bool _closed;

    private AgentHarness(AgentHarnessOptions options)
    {
        _durableSession = options.Session;
        Session = options.Session;
        _model = options.Model;
        _thinkingLevel = options.ThinkingLevel ?? ThinkingLevels.Off;
        _tools = options.Tools?.ToArray() ?? [];
        _activeToolNames = options.ActiveToolNames?.ToArray() ?? _tools.Select(static tool => tool.Name).ToArray();
        _resources = CloneResources(options.Resources);
        _streamOptions = CloneStreamOptions(options.StreamOptions);
        _retryPolicy = options.Retry ?? new RetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 1000 };
        _compactionSettings = options.Compaction ?? Pi.AgentCore.Harness.Compaction.Compaction.DefaultCompactionSettings;
        _steeringMode = options.SteeringMode ?? QueueMode.OneAtATime;
        _followUpMode = options.FollowUpMode ?? QueueMode.OneAtATime;
        Hooks = new UnavailableRegistry("hooks.on", () => _closed);
        Events = new UnavailableRegistry("events.on", () => _closed);
    }

    /// <inheritdoc />
    public string Name => "main";

    /// <inheritdoc />
    public Session<SessionMetadata> Session { get; }

    /// <summary>Hook registry exposed by the harness.</summary>
    public IHooks Hooks { get; }

    /// <summary>Passive event registry exposed by the harness.</summary>
    public IEvents Events { get; }

    /// <summary>Opens a record-free session or reports the unimplemented restore path.</summary>
    public static async Task<AgentHarnessCreateResult> CreateAsync(
        AgentHarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var records = await options.Session.FindRecordsAsync(
                new RecordQuery { Limit = 1 },
                cancellationToken)
            .ConfigureAwait(false);
        if (records.Count > 0)
        {
            throw new HarnessNotImplemented("create.restore");
        }

        return new AgentHarnessCreateResult(new AgentHarness(options), []);
    }

    /// <summary>Compatibility alias for the asynchronous create operation.</summary>
    public static Task<AgentHarnessCreateResult> Create(
        AgentHarnessOptions options,
        CancellationToken cancellationToken = default) =>
        CreateAsync(options, cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) =>
        _durableSession.GetLeafIdAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Result<RunResultValue, TaggedErrorValue>> PromptAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<RunResultValue, TaggedErrorValue>>("prompt", cancellationToken);

    /// <inheritdoc />
    public Task<Result<RunResultValue, TaggedErrorValue>> PromptAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<RunResultValue, TaggedErrorValue>>("prompt", cancellationToken);

    /// <inheritdoc />
    public Task<Result<RunResultValue, TaggedErrorValue>> PromptAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<RunResultValue, TaggedErrorValue>>("prompt", cancellationToken);

    /// <inheritdoc />
    public Task<Result<RunResultValue, TaggedErrorValue>> SkillAsync(
        string name,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<RunResultValue, TaggedErrorValue>>("skill", cancellationToken);

    /// <inheritdoc />
    public Task<Result<RunResultValue, TaggedErrorValue>> PromptFromTemplateAsync(
        string name,
        IReadOnlyList<string>? args = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<RunResultValue, TaggedErrorValue>>("promptFromTemplate", cancellationToken);

    /// <inheritdoc />
    public Task<Result<CompactionResultValue, TaggedErrorValue>> CompactAsync(
        string? customInstructions = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<CompactionResultValue, TaggedErrorValue>>("compact", cancellationToken);

    /// <inheritdoc />
    public Task<Result<NavigationResultValue, TaggedErrorValue>> NavigateTreeAsync(
        string? targetId,
        NavigateOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<NavigationResultValue, TaggedErrorValue>>("navigateTree", cancellationToken);

    /// <inheritdoc />
    public Task<Result<ResumeResultValue, TaggedErrorValue>> ResumeAsync(CancellationToken cancellationToken = default) =>
        Unavailable<Result<ResumeResultValue, TaggedErrorValue>>("resume", cancellationToken);

    /// <inheritdoc />
    public Task<Result<AbortResultValue, TaggedErrorValue>> AbortAsync(CancellationToken cancellationToken = default) =>
        Unavailable<Result<AbortResultValue, TaggedErrorValue>>("abort", cancellationToken);

    /// <inheritdoc />
    public Task<Result<QueueResultValue, TaggedErrorValue>> SteerAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<QueueResultValue, TaggedErrorValue>>("steer", cancellationToken);

    /// <inheritdoc />
    public Task<Result<QueueResultValue, TaggedErrorValue>> SteerAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<QueueResultValue, TaggedErrorValue>>("steer", cancellationToken);

    /// <inheritdoc />
    public Task<Result<QueueResultValue, TaggedErrorValue>> FollowUpAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<QueueResultValue, TaggedErrorValue>>("followUp", cancellationToken);

    /// <inheritdoc />
    public Task<Result<QueueResultValue, TaggedErrorValue>> FollowUpAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<QueueResultValue, TaggedErrorValue>>("followUp", cancellationToken);

    /// <inheritdoc />
    public Task<Result<QueueResultValue, TaggedErrorValue>> NextRunAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<QueueResultValue, TaggedErrorValue>>("nextRun", cancellationToken);

    /// <inheritdoc />
    public Task<Result<QueueResultValue, TaggedErrorValue>> NextRunAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<QueueResultValue, TaggedErrorValue>>("nextRun", cancellationToken);

    /// <inheritdoc />
    public Task<Result<CancelQueuedResultValue, TaggedErrorValue>> CancelQueuedAsync(
        string entryId,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<CancelQueuedResultValue, TaggedErrorValue>>("cancelQueued", cancellationToken);

    /// <inheritdoc />
    public Task<Result<object?, TaggedErrorValue>> RecordUsageAsync(
        Usage usage,
        string? entryId = null,
        JsonNode? details = null,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<object?, TaggedErrorValue>>("recordUsage", cancellationToken);

    /// <inheritdoc />
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) =>
        Unavailable("waitForIdle", cancellationToken);

    /// <inheritdoc />
    public Task RunWhenIdleAsync(Func<Task> callback, CancellationToken cancellationToken = default) =>
        Unavailable("runWhenIdle", cancellationToken);

    /// <inheritdoc />
    public Task<ActionInfo?> PeekActionAsync(CancellationToken cancellationToken = default) =>
        Unavailable<ActionInfo?>("peekAction", cancellationToken);

    /// <inheritdoc />
    public Task<ActionInfo?> ExecuteActionAsync(CancellationToken cancellationToken = default) =>
        Unavailable<ActionInfo?>("executeAction", cancellationToken);

    /// <inheritdoc />
    public Task RunToCompletionAsync(CancellationToken cancellationToken = default) =>
        Unavailable("runToCompletion", cancellationToken);

    /// <inheritdoc />
    public Task<Model> GetModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_model);
    }

    /// <inheritdoc />
    public Task SetModelAsync(Model model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _model = model ?? throw new ArgumentNullException(nameof(model));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> GetThinkingLevelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_thinkingLevel);
    }

    /// <inheritdoc />
    public Task SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _thinkingLevel = level ?? throw new ArgumentNullException(nameof(level));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(_activeToolNames.ToArray());
    }

    /// <inheritdoc />
    public Task SetActiveToolsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _activeToolNames = names?.ToArray() ?? throw new ArgumentNullException(nameof(names));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WatchHandle<LaneSnapshot>> WatchAsync(CancellationToken cancellationToken = default) =>
        Unavailable<WatchHandle<LaneSnapshot>>("watch", cancellationToken);

    /// <summary>Returns a lane view when lane support is implemented.</summary>
    public Task<AgentLane?> LaneAsync(string name, CancellationToken cancellationToken = default) =>
        Unavailable<AgentLane?>("lane", cancellationToken);

    /// <summary>Creates a lane when lane support is implemented.</summary>
    public Task<Result<AgentLane, TaggedErrorValue>> CreateLaneAsync(
        string name,
        string? at,
        CancellationToken cancellationToken = default) =>
        Unavailable<Result<AgentLane, TaggedErrorValue>>("createLane", cancellationToken);

    /// <summary>Lists lanes when lane support is implemented.</summary>
    public Task<IReadOnlyList<LaneInfo>> LanesAsync(CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyList<LaneInfo>>("lanes", cancellationToken);

    /// <summary>Returns the current tool definitions.</summary>
    public Task<IReadOnlyList<HarnessTool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<HarnessTool>>(_tools.ToArray());
    }

    /// <summary>Replaces tool definitions and optionally the active set.</summary>
    public Task SetToolsAsync(
        IReadOnlyList<HarnessTool> tools,
        IReadOnlyList<string>? activeNames = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tools = tools?.ToArray() ?? throw new ArgumentNullException(nameof(tools));
        _activeToolNames = activeNames?.ToArray() ?? _tools.Select(static tool => tool.Name).ToArray();
        return Task.CompletedTask;
    }

    /// <summary>Returns defensive copies of resources.</summary>
    public Task<AgentHarnessResources> GetResourcesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CloneResources(_resources));
    }

    /// <summary>Replaces skill and prompt-template resources.</summary>
    public Task SetResourcesAsync(AgentHarnessResources resources, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resources = CloneResources(resources);
        return Task.CompletedTask;
    }

    /// <summary>Returns a defensive copy of stream options.</summary>
    public Task<SimpleStreamOptions> GetStreamOptionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CloneStreamOptions(_streamOptions));
    }

    /// <summary>Replaces stream options.</summary>
    public Task SetStreamOptionsAsync(SimpleStreamOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _streamOptions = CloneStreamOptions(options ?? throw new ArgumentNullException(nameof(options)));
        return Task.CompletedTask;
    }

    /// <summary>Returns the current retry policy.</summary>
    public Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_retryPolicy with { });
    }

    /// <summary>Replaces the retry policy.</summary>
    public Task SetRetryPolicyAsync(RetryPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _retryPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return Task.CompletedTask;
    }

    /// <summary>Returns the current compaction settings.</summary>
    public Task<CompactionSettings> GetCompactionSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_compactionSettings with { });
    }

    /// <summary>Replaces compaction settings.</summary>
    public Task SetCompactionSettingsAsync(CompactionSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _compactionSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        return Task.CompletedTask;
    }

    /// <summary>Returns the steering queue mode.</summary>
    public Task<QueueMode> GetSteeringModeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_steeringMode);
    }

    /// <summary>Changes the steering queue mode.</summary>
    public Task SetSteeringModeAsync(QueueMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _steeringMode = mode;
        return Task.CompletedTask;
    }

    /// <summary>Returns the follow-up queue mode.</summary>
    public Task<QueueMode> GetFollowUpModeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_followUpMode);
    }

    /// <summary>Changes the follow-up queue mode.</summary>
    public Task SetFollowUpModeAsync(QueueMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _followUpMode = mode;
        return Task.CompletedTask;
    }

    /// <summary>Returns a session watch when session-watch support is implemented.</summary>
    public Task<WatchHandle<SessionSnapshot>> WatchSessionAsync(CancellationToken cancellationToken = default) =>
        Unavailable<WatchHandle<SessionSnapshot>>("watchSession", cancellationToken);

    /// <summary>Closes the harness and rejects unfinished operations thereafter.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _closed = true;
        return Task.CompletedTask;
    }

    private Task Unavailable(string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(CreateUnavailable(operation));
    }

    private Task<T> Unavailable<T>(string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<T>(CreateUnavailable(operation));
    }

    private Exception CreateUnavailable(string operation) =>
        _closed ? new HarnessClosed() : new HarnessNotImplemented(operation);

    private static AgentHarnessResources CloneResources(AgentHarnessResources? resources) => new()
    {
        Skills = resources?.Skills?.ToArray() ?? [],
        PromptTemplates = resources?.PromptTemplates?.ToArray() ?? [],
    };

    private static SimpleStreamOptions CloneStreamOptions(SimpleStreamOptions? options)
    {
        options ??= new SimpleStreamOptions();
        return new SimpleStreamOptions
        {
            Signal = options.Signal,
            TelemetryContext = options.TelemetryContext,
            ApiKey = options.ApiKey,
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
            MaxTokens = options.MaxTokens,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            WebSocketConnectTimeoutMs = options.WebSocketConnectTimeoutMs,
            Metadata = options.Metadata,
            ToolChoice = options.ToolChoice,
            Reasoning = options.Reasoning,
            Deferred = options.Deferred,
            DeferredWindow = options.DeferredWindow,
            ThinkingBudgets = options.ThinkingBudgets,
        };
    }

    private sealed class UnavailableRegistry(string operation, Func<bool> isClosed) : IHooks, IEvents
    {
        public Action On(string name, Action handler, string? id = null) => throw Create();

        public Action On(string name, Func<object?, Task> handler, string? id = null) => throw Create();

        public Action On(string type, Action<HarnessEvent> listener) => throw Create();

        public Action On(string type, Func<HarnessEvent, Task> listener) => throw Create();

        private Exception Create() => isClosed() ? new HarnessClosed() : new HarnessNotImplemented(operation);
    }
}
