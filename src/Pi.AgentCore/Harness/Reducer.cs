using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;
using Pi.Ai;
using SessionAgentMessage = Pi.AgentCore.Harness.Session.AgentMessage;

namespace Pi.AgentCore.Harness;

/// <summary>Machine-readable reasons for a contradictory lane recovery slice.</summary>
public static class RecordLogCorruptionReasons
{
    /// <summary>More than one operation is open.</summary>
    public const string MultipleOpenOperations = "multiple_open_operations";

    /// <summary>A record references an unknown operation.</summary>
    public const string UnknownOperation = "unknown_operation";

    /// <summary>A record follows its operation finish.</summary>
    public const string RecordAfterFinish = "record_after_finish";

    /// <summary>Attempt numbers are not consecutive.</summary>
    public const string NonConsecutiveAttempt = "non_consecutive_attempt";

    /// <summary>A compaction reason is invalid for the step.</summary>
    public const string InvalidCompactionReason = "invalid_compaction_reason";

    /// <summary>A queue item was added after abort.</summary>
    public const string QueueAfterAbort = "queue_after_abort";

    /// <summary>A queue cancellation does not match an enqueue.</summary>
    public const string InvalidQueueCancellation = "invalid_queue_cancellation";

    /// <summary>A structural step is internally inconsistent.</summary>
    public const string InconsistentStep = "inconsistent_step";

    /// <summary>A tool-start record does not match its assistant call.</summary>
    public const string ToolCallMismatch = "tool_call_mismatch";

    /// <summary>A tool invocation identity is duplicated.</summary>
    public const string DuplicateToolInvocation = "duplicate_tool_invocation";

    /// <summary>A provisioned entry differs from the durable entry.</summary>
    public const string ProvisionedEntryMismatch = "provisioned_entry_mismatch";

    /// <summary>A deferred assistant result has an invalid handle.</summary>
    public const string InvalidDeferredHandle = "invalid_deferred_handle";
}

/// <summary>Exception raised when durable lane records contradict the single-writer protocol.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name preserves the upstream record-log corruption error contract.")]
public sealed class RecordLogCorruption : Exception
{
    /// <summary>Machine-readable corruption reason.</summary>
    public string Reason { get; }

    /// <summary>Creates a corruption exception.</summary>
    public RecordLogCorruption(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}

/// <summary>Bounded lane recovery data supplied to the reducer.</summary>
public record RecordLogSlice
{
    /// <summary>Lane being recovered.</summary>
    public required string Lane { get; init; }

    /// <summary>Unfinished operation starts, newest first.</summary>
    public IReadOnlyList<OperationStartedRecord> OpenOperations { get; init; } = [];

    /// <summary>Lane records in any order.</summary>
    public IReadOnlyList<LaneRecord> Records { get; init; } = [];

    /// <summary>Entries needed to validate records and provisioned targets.</summary>
    public IReadOnlyList<Entry> Entries { get; init; } = [];
}

/// <summary>Effective model and tool configuration for a lane.</summary>
public sealed record EffectiveLaneConfiguration
{
    /// <summary>Provider/model pair.</summary>
    public required LaneModel Model { get; init; }

    /// <summary>Effective thinking level.</summary>
    public required string ThinkingLevel { get; init; }

    /// <summary>Active tool names.</summary>
    public IReadOnlyList<string> ActiveToolNames { get; init; } = [];
}

/// <summary>Provider/model pair projected by recovery.</summary>
public sealed record LaneModel
{
    /// <summary>Provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Model identifier.</summary>
    public required string ModelId { get; init; }
}

/// <summary>Assistant failure and its durable provenance.</summary>
public sealed record TerminalFailureState
{
    /// <summary>Failure entry identifier.</summary>
    public required string EntryId { get; init; }

    /// <summary>Failure source.</summary>
    public required string Source { get; init; }

    /// <summary>Assistant failure message.</summary>
    public required AssistantMessage Message { get; init; }
}

/// <summary>One tool invocation projected from an unfinished batch.</summary>
public sealed record ToolCallState
{
    /// <summary>Assistant-content ordinal.</summary>
    public int ToolIndex { get; init; }

    /// <summary>Tool call payload.</summary>
    public required ToolCall ToolCall { get; init; }

    /// <summary>Durable tool-start record, when present.</summary>
    public ToolStartedRecord? Started { get; init; }

    /// <summary>Whether a matching result exists.</summary>
    public bool ResultExists { get; init; }

    /// <summary>Termination hint from the matching result entry.</summary>
    public bool? Terminate { get; init; }
}

/// <summary>Unfinished assistant tool batch.</summary>
public sealed record ToolBatchState
{
    /// <summary>Assistant entry containing the calls.</summary>
    public required string AssistantEntryId { get; init; }

    /// <summary>Calls in assistant source order.</summary>
    public IReadOnlyList<ToolCallState> Calls { get; init; } = [];

    /// <summary>Whether the assistant stopped because of a length limit.</summary>
    public bool Truncated { get; init; }

    /// <summary>Whether at least one call still lacks a result.</summary>
    public bool Unresolved { get; init; }
}

/// <summary>Newest entry owned by an open operation.</summary>
public sealed record NewestOwnState
{
    /// <summary>Entry identifier.</summary>
    public required string EntryId { get; init; }

    /// <summary>Entry discriminator.</summary>
    public required string Type { get; init; }

    /// <summary>Message role, when the entry is a message.</summary>
    public string? Role { get; init; }

    /// <summary>Assistant stop reason, when available.</summary>
    public string? StopReason { get; init; }
}

/// <summary>Whether an operation's structural target exists.</summary>
public sealed record LaneTargets
{
    /// <summary>Manual-compaction result exists.</summary>
    public bool? Result { get; init; }

    /// <summary>Navigation summary exists.</summary>
    public bool? Summary { get; init; }
}

/// <summary>Open operation state reconstructed from durable entries and records.</summary>
public sealed record LaneOperationState
{
    /// <summary>Operation identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Operation kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Operation intent.</summary>
    public required OperationIntent Intent { get; init; }

    /// <summary>Whether abort has been requested.</summary>
    public bool Aborting { get; init; }

    /// <summary>Unfinished structural step.</summary>
    public LaneStepState? Step { get; init; }

    /// <summary>Unfinished tool batch.</summary>
    public ToolBatchState? ToolBatch { get; init; }

    /// <summary>Initial messages not yet durable.</summary>
    public IReadOnlyList<Entry> MissingInitialMessages { get; init; } = [];

    /// <summary>Pending steering messages.</summary>
    public IReadOnlyList<Entry> PendingSteer { get; init; } = [];

    /// <summary>Pending follow-up messages.</summary>
    public IReadOnlyList<Entry> PendingFollowUp { get; init; } = [];

    /// <summary>Deferred writes not yet durable.</summary>
    public IReadOnlyList<Entry> PendingWrites { get; init; } = [];

    /// <summary>Unredeemed deferred provider handle.</summary>
    public DeferredHandle? Deferred { get; init; }

    /// <summary>Whether overflow recovery has already been used for current input.</summary>
    public bool OverflowRecoveryUsed { get; init; }

    /// <summary>Newest operation-owned entry.</summary>
    public NewestOwnState? NewestOwn { get; init; }

    /// <summary>Structural result targets.</summary>
    public LaneTargets Targets { get; init; } = new();
}

/// <summary>Unfinished structural step state.</summary>
public sealed record LaneStepState
{
    /// <summary>Step discriminator.</summary>
    public required string Kind { get; init; }

    /// <summary>Attempt count.</summary>
    public int Attempts { get; init; }

    /// <summary>Provisioned result identifier.</summary>
    public required string ResultEntryId { get; init; }

    /// <summary>Compaction reason, when the step is compaction.</summary>
    public string? CompactionReason { get; init; }
}

/// <summary>Complete lane recovery result.</summary>
public sealed record LaneState
{
    /// <summary>Lane identifier.</summary>
    public required string Lane { get; init; }

    /// <summary>Current lane leaf.</summary>
    public string? LeafId { get; init; }

    /// <summary>Open operation, or null while idle.</summary>
    public LaneOperationState? Operation { get; init; }

    /// <summary>Unconsumed next-run inputs.</summary>
    public IReadOnlyList<Entry> PendingNextRun { get; init; } = [];
}

/// <summary>Result of projecting one lane recovery slice.</summary>
public sealed record LaneReductionResult
{
    /// <summary>Projected lane state.</summary>
    public required LaneState LaneState { get; init; }

    /// <summary>Effective model/thinking/tools configuration.</summary>
    public required EffectiveLaneConfiguration EffectiveConfiguration { get; init; }

    /// <summary>Terminal assistant failure, when one is present.</summary>
    public TerminalFailureState? TerminalFailure { get; init; }
}

/// <summary>Inputs required for pure lane reduction.</summary>
public sealed record LaneReductionInput : RecordLogSlice
{
    /// <summary>Current lane leaf.</summary>
    public string? LeafId { get; init; }

    /// <summary>Entries appended by the open operation.</summary>
    public IReadOnlyList<Entry> OwnEntries { get; init; } = [];

    /// <summary>Entries used to derive effective configuration.</summary>
    public IReadOnlyList<Entry> ConfigurationEntries { get; init; } = [];

    /// <summary>Fallback configuration.</summary>
    public required EffectiveLaneConfiguration Defaults { get; init; }
}

/// <summary>Validates a bounded durable lane slice.</summary>
public static class Reducer
{
    /// <summary>Rejects contradictions without mutating the supplied collections.</summary>
    public static void ValidateRecordLog(RecordLogSlice input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.OpenOperations.Count > 1)
        {
            Corrupt(RecordLogCorruptionReasons.MultipleOpenOperations, $"Lane {input.Lane} has at least two open operations");
        }

        var entriesById = input.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        ValidateDeferredHandles(entriesById.Values);
        var starts = new Dictionary<string, OperationStartedRecord>(StringComparer.Ordinal);
        var finishedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        var abortedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        var queueEnqueues = new Dictionary<string, QueueEnqueuedRecord>(StringComparer.Ordinal);
        var latestAttempt = new Dictionary<string, StepAttemptRecord>(StringComparer.Ordinal);
        var toolInvocations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in input.Records.OrderBy(static record => record.Seq))
        {
            if (record is OperationStartedRecord started)
            {
                starts[started.Id] = started;
                ValidateOperationResult(entriesById, started);
                continue;
            }

            var runId = GetRunId(record);
            if (runId is not null)
            {
                if (!starts.ContainsKey(runId))
                {
                    Corrupt(RecordLogCorruptionReasons.UnknownOperation, $"Record {record.Id} references unknown operation {runId}");
                }

                if (finishedAt.TryGetValue(runId, out var finishSeq) && record.Seq > finishSeq)
                {
                    Corrupt(RecordLogCorruptionReasons.RecordAfterFinish, $"Record {record.Id} follows the finish of operation {runId}");
                }
            }

            switch (record)
            {
                case OperationFinishedRecord finished:
                    finishedAt[finished.RunId] = finished.Seq;
                    break;
                case AbortRequestedRecord abort:
                    abortedAt[abort.RunId] = abort.Seq;
                    break;
                case StepAttemptRecord attempt:
                    ValidateAttemptReason(attempt);
                    ValidateAttemptSequence(attempt, latestAttempt.GetValueOrDefault(attempt.RunId), entriesById);
                    ValidateAttemptResult(entriesById, attempt);
                    latestAttempt[attempt.RunId] = attempt;
                    break;
                case ToolStartedRecord tool:
                    ValidateToolStart(tool, entriesById, toolInvocations);
                    break;
                case QueueEnqueuedRecord enqueue:
                    if (enqueue.Queue != "nextRun" && enqueue.RunId is not null &&
                        abortedAt.TryGetValue(enqueue.RunId, out var abortSeq) && enqueue.Seq > abortSeq)
                    {
                        Corrupt(RecordLogCorruptionReasons.QueueAfterAbort, $"{enqueue.Queue} item {enqueue.Target.Id} was enqueued after abort");
                    }

                    queueEnqueues[enqueue.Target.Id] = enqueue;
                    ValidateExactProvisionedEntry(entriesById, enqueue.Target);
                    break;
                case QueueCancelledRecord cancelled:
                    if (!queueEnqueues.TryGetValue(cancelled.EntryId, out var enqueueRecord) ||
                        enqueueRecord.Seq >= cancelled.Seq ||
                        enqueueRecord.RunId != cancelled.RunId ||
                        entriesById.ContainsKey(cancelled.EntryId))
                    {
                        Corrupt(RecordLogCorruptionReasons.InvalidQueueCancellation, $"Queue cancellation {cancelled.Id} has no pending matching enqueue");
                    }

                    break;
                case WriteDeferredRecord deferred:
                    ValidateExactProvisionedEntry(entriesById, deferred.Target);
                    break;
                case UsageRecord:
                    break;
            }
        }
    }

    /// <summary>Purely projects one lane's orchestration state.</summary>
    public static LaneReductionResult ReduceLaneState(LaneReductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRecordLog(input);

        var records = input.Records.OrderBy(static record => record.Seq).ToArray();
        var ownEntries = input.OwnEntries.OrderBy(static entry => entry.Seq).ToArray();
        var entriesById = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in input.Entries.Concat(ownEntries))
        {
            entriesById[entry.Id] = entry;
        }

        var cancelledQueueIds = records
            .OfType<QueueCancelledRecord>()
            .Select(static record => record.EntryId)
            .ToHashSet(StringComparer.Ordinal);
        var pendingQueueRecords = records
            .OfType<QueueEnqueuedRecord>()
            .Where(record => !entriesById.ContainsKey(record.Target.Id) && !cancelledQueueIds.Contains(record.Target.Id))
            .ToArray();
        var started = input.OpenOperations.Count == 0 ? null : input.OpenOperations[0];
        var capturedInitialMessageIds = started?.Intent is RunOperationIntent run
            ? run.InitialMessages.Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var pendingNextRun = pendingQueueRecords
            .Where(record => record.Queue == "nextRun" && !capturedInitialMessageIds.Contains(record.Target.Id))
            .Select(record => CloneProvisionedEntry(record.Target))
            .ToArray();
        var effectiveConfiguration = DeriveEffectiveConfiguration(input);

        if (started is null)
        {
            return new LaneReductionResult
            {
                LaneState = new LaneState
                {
                    Lane = input.Lane,
                    LeafId = input.LeafId,
                    Operation = null,
                    PendingNextRun = pendingNextRun,
                },
                EffectiveConfiguration = effectiveConfiguration,
                TerminalFailure = null,
            };
        }

        var operationRecords = records
            .Where(record => record is OperationStartedRecord operationStarted
                ? operationStarted.Id == started.Id
                : GetRunId(record) == started.Id)
            .ToArray();
        var aborting = operationRecords.OfType<AbortRequestedRecord>().Any();
        var pendingSteer = aborting
            ? []
            : pendingQueueRecords
                .Where(record => record.Queue == "steer" && record.RunId == started.Id)
                .Select(record => CloneProvisionedEntry(record.Target))
                .ToArray();
        var pendingFollowUp = aborting
            ? []
            : pendingQueueRecords
                .Where(record => record.Queue == "followUp" && record.RunId == started.Id)
                .Select(record => CloneProvisionedEntry(record.Target))
                .ToArray();
        var pendingWrites = operationRecords
            .OfType<WriteDeferredRecord>()
            .Where(record => !entriesById.ContainsKey(record.Target.Id))
            .Select(record => CloneProvisionedEntry(record.Target))
            .ToArray();
        var missingInitialMessages = started.Intent is RunOperationIntent startedRun
            ? startedRun.InitialMessages
                .Where(entry => !entriesById.ContainsKey(entry.Id))
                .Select(CloneProvisionedEntry)
                .ToArray()
            : [];

        var newestAttempt = operationRecords.OfType<StepAttemptRecord>().LastOrDefault();
        LaneStepState? step = newestAttempt is not null && !entriesById.ContainsKey(newestAttempt.ResultEntryId)
            ? new LaneStepState
            {
                Kind = newestAttempt.Step,
                Attempts = newestAttempt.Attempt,
                ResultEntryId = newestAttempt.ResultEntryId,
                CompactionReason = newestAttempt.Step == "compaction" ? newestAttempt.CompactionReason : null,
            }
            : null;

        var consumedInputIds = new HashSet<string>(StringComparer.Ordinal);
        if (started.Intent is RunOperationIntent runIntent)
        {
            foreach (var entry in runIntent.InitialMessages)
            {
                consumedInputIds.Add(entry.Id);
            }
        }

        foreach (var enqueue in operationRecords.OfType<QueueEnqueuedRecord>().Where(static record => record.Queue != "nextRun"))
        {
            consumedInputIds.Add(enqueue.Target.Id);
        }

        var newestConsumedInputSequence = long.MinValue;
        foreach (var id in consumedInputIds)
        {
            if (entriesById.TryGetValue(id, out var entry) && entry is MessageEntry)
            {
                newestConsumedInputSequence = Math.Max(newestConsumedInputSequence, entry.Seq);
            }
        }

        var overflowRecoveryUsed = operationRecords
            .OfType<StepAttemptRecord>()
            .Any(record => record.Step == "compaction" && record.CompactionReason == "overflow" && record.Seq > newestConsumedInputSequence);

        var newestOwnEntry = ownEntries.LastOrDefault();
        var newestOwn = DeriveNewestOwn(newestOwnEntry);
        var deferred = newestOwnEntry is MessageEntry newestMessage &&
                       newestMessage.Message.Role == "assistant" &&
                       StringValue(newestMessage.Message.Value, "stopReason") == StopReasons.Deferred
            ? HarnessMessageUtilities.TryGetDeferredHandle(newestMessage.Message)
            : null;
        var targets = new LaneTargets();
        if (started.Intent is CompactionOperationIntent compaction)
        {
            targets = targets with { Result = entriesById.ContainsKey(compaction.ResultEntryId) };
        }
        else if (started.Intent is NavigationOperationIntent navigation && navigation.SummaryEntryId is not null)
        {
            targets = targets with { Summary = entriesById.ContainsKey(navigation.SummaryEntryId) };
        }

        var deferredWriteIds = operationRecords
            .OfType<WriteDeferredRecord>()
            .Select(static record => record.Target.Id)
            .ToHashSet(StringComparer.Ordinal);
        TerminalFailureState? terminalFailure = null;
        if (newestOwnEntry is MessageEntry newestAssistantEntry &&
            newestAssistantEntry.Message.Role == "assistant" &&
            StringValue(newestAssistantEntry.Message.Value, "stopReason") == StopReasons.Error &&
            !deferredWriteIds.Contains(newestAssistantEntry.Id))
        {
            var producedByStep = operationRecords
                .OfType<StepAttemptRecord>()
                .Any(record => record.ResultEntryId == newestAssistantEntry.Id);
            var previousOwnEntry = ownEntries.Length > 1 ? ownEntries[^2] : null;
            var producedByDeferredFetch = operationRecords
                .OfType<UsageRecord>()
                .Any(record => record.Cause == "deferred_fetch" && record.EntryId == newestAssistantEntry.Id) ||
                (previousOwnEntry is MessageEntry previousMessage &&
                 previousMessage.Message.Role == "assistant" &&
                 StringValue(previousMessage.Message.Value, "stopReason") == StopReasons.Deferred);
            if (producedByStep || producedByDeferredFetch)
            {
                terminalFailure = new TerminalFailureState
                {
                    EntryId = newestAssistantEntry.Id,
                    Source = producedByStep ? "step" : "deferred_fetch",
                    Message = HarnessMessageUtilities.TryGetAssistant(newestAssistantEntry.Message)
                        ?? throw new InvalidOperationException($"Assistant entry {newestAssistantEntry.Id} could not be decoded."),
                };
            }
        }

        return new LaneReductionResult
        {
            LaneState = new LaneState
            {
                Lane = input.Lane,
                LeafId = input.LeafId,
                Operation = new LaneOperationState
                {
                    Id = started.Id,
                    Kind = started.Intent.Kind,
                    Intent = CloneIntent(started.Intent),
                    Aborting = aborting,
                    Step = step,
                    ToolBatch = DeriveToolBatch(started.Id, operationRecords, ownEntries, entriesById, deferredWriteIds),
                    MissingInitialMessages = missingInitialMessages,
                    PendingSteer = pendingSteer,
                    PendingFollowUp = pendingFollowUp,
                    PendingWrites = pendingWrites,
                    Deferred = deferred,
                    OverflowRecoveryUsed = overflowRecoveryUsed,
                    NewestOwn = newestOwn,
                    Targets = targets,
                },
                PendingNextRun = pendingNextRun,
            },
            EffectiveConfiguration = effectiveConfiguration,
            TerminalFailure = terminalFailure,
        };
    }

    private static void ValidateOperationResult(
        Dictionary<string, Entry> entriesById,
        OperationStartedRecord record)
    {
        switch (record.Intent)
        {
            case RunOperationIntent run:
                foreach (var target in run.InitialMessages)
                {
                    ValidateExactProvisionedEntry(entriesById, target);
                }

                break;
            case CompactionOperationIntent compaction:
                ValidateResultEntry(entriesById, compaction.ResultEntryId, static entry => entry is CompactionEntry, "manual compaction");
                break;
            case NavigationOperationIntent navigation when navigation.SummaryEntryId is not null:
                ValidateResultEntry(entriesById, navigation.SummaryEntryId, static entry => entry is BranchSummaryEntry, "navigation summary");
                break;
        }
    }

    private static void ValidateAttemptReason(StepAttemptRecord record)
    {
        if (record.Step == "compaction")
        {
            if (record.CompactionReason is not ("manual" or "threshold" or "overflow"))
            {
                Corrupt(RecordLogCorruptionReasons.InvalidCompactionReason, $"Compaction attempt {record.Id} has no valid compaction reason");
            }
        }
        else if (record.CompactionReason is not null)
        {
            Corrupt(RecordLogCorruptionReasons.InvalidCompactionReason, $"{record.Step} attempt {record.Id} has a compaction reason");
        }
    }

    private static void ValidateAttemptSequence(
        StepAttemptRecord record,
        StepAttemptRecord? previous,
        Dictionary<string, Entry> entriesById)
    {
        var previousResult = previous is not null && entriesById.TryGetValue(previous.ResultEntryId, out var previousEntry)
            ? previousEntry
            : null;
        var continuesSeries = previous is not null && previous.Step == record.Step &&
                              (previousResult is null || previousResult.Seq >= record.Seq);
        var expectedAttempt = continuesSeries && previous is not null ? previous.Attempt + 1 : 1;
        if (record.Attempt != expectedAttempt)
        {
            Corrupt(
                RecordLogCorruptionReasons.NonConsecutiveAttempt,
                $"{record.Step} attempt {record.Id} is {record.Attempt}; expected {expectedAttempt}");
        }

        if (!continuesSeries || record.Step == "assistant" || previous is null)
        {
            return;
        }

        if (record.ResultEntryId != previous.ResultEntryId)
        {
            Corrupt(RecordLogCorruptionReasons.InconsistentStep, $"{record.Step} attempts disagree on their result entry id");
        }

        if (record.CompactionReason != previous.CompactionReason)
        {
            Corrupt(RecordLogCorruptionReasons.InconsistentStep, $"{record.Step} attempts disagree on their compaction reason");
        }
    }

    private static void ValidateAttemptResult(
        Dictionary<string, Entry> entriesById,
        StepAttemptRecord record)
    {
        switch (record.Step)
        {
            case "assistant":
                ValidateResultEntry(
                    entriesById,
                    record.ResultEntryId,
                    static entry => entry is MessageEntry message && message.Message.Role == "assistant",
                    "assistant result");
                break;
            case "compaction":
                ValidateResultEntry(entriesById, record.ResultEntryId, static entry => entry is CompactionEntry, "compaction result");
                break;
            case "branch_summary":
                ValidateResultEntry(entriesById, record.ResultEntryId, static entry => entry is BranchSummaryEntry, "branch-summary result");
                break;
        }
    }

    private static void ValidateToolStart(
        ToolStartedRecord record,
        Dictionary<string, Entry> entriesById,
        HashSet<string> invocations)
    {
        var invocation = $"{record.AssistantEntryId}\u0000{record.ToolIndex}";
        if (!invocations.Add(invocation))
        {
            Corrupt(RecordLogCorruptionReasons.DuplicateToolInvocation, $"Tool invocation {record.AssistantEntryId}:{record.ToolIndex} is duplicated");
        }

        if (!entriesById.TryGetValue(record.AssistantEntryId, out var assistantEntry) ||
            assistantEntry is not MessageEntry assistantMessage ||
            assistantMessage.Message.Role != "assistant")
        {
            Corrupt(RecordLogCorruptionReasons.ToolCallMismatch, $"Tool start {record.Id} does not reference an assistant entry");
        }

        var toolCalls = HarnessMessageUtilities.GetToolCalls(((MessageEntry)assistantEntry!).Message);
        var toolCall = record.ToolIndex >= 0 && record.ToolIndex < toolCalls.Count ? toolCalls[record.ToolIndex] : null;
        if (toolCall is null || toolCall.Id != record.ToolCallId || toolCall.Name != record.ToolName)
        {
            Corrupt(RecordLogCorruptionReasons.ToolCallMismatch, $"Tool start {record.Id} does not match its assistant tool-call ordinal");
        }

        ValidateResultEntry(
            entriesById,
            record.ResultEntryId,
            entry => entry is MessageEntry message &&
                     message.Message.Role == "toolResult" &&
                     StringValue(message.Message.Value, "toolCallId") == record.ToolCallId &&
                     StringValue(message.Message.Value, "toolName") == record.ToolName,
            "tool result");
    }

    private static void ValidateDeferredHandles(IEnumerable<Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry is MessageEntry message &&
                message.Message.Role == "assistant" &&
                StringValue(message.Message.Value, "stopReason") == StopReasons.Deferred &&
                HarnessMessageUtilities.TryGetDeferredHandle(message.Message) is null)
            {
                Corrupt(RecordLogCorruptionReasons.InvalidDeferredHandle, $"Deferred assistant entry {entry.Id} does not carry a handle");
            }
        }
    }

    private static void ValidateExactProvisionedEntry(
        Dictionary<string, Entry> entriesById,
        Entry target)
    {
        if (entriesById.TryGetValue(target.Id, out var entry) && !MatchesProvisionedEntry(entry, target))
        {
            Corrupt(
                RecordLogCorruptionReasons.ProvisionedEntryMismatch,
                $"Provisioned entry {target.Id} exists with content different from its intent");
        }
    }

    private static bool MatchesProvisionedEntry(Entry entry, Entry target)
    {
        var left = SessionJson.EntryToJson(entry, includeStorageFields: false);
        var right = SessionJson.EntryToJson(target, includeStorageFields: false);
        return JsonNode.DeepEquals(left, right);
    }

    private static void ValidateResultEntry(
        Dictionary<string, Entry> entriesById,
        string resultEntryId,
        Func<Entry, bool> matches,
        string description)
    {
        if (entriesById.TryGetValue(resultEntryId, out var entry) && !matches(entry))
        {
            Corrupt(
                RecordLogCorruptionReasons.ProvisionedEntryMismatch,
                $"Provisioned {description} entry {resultEntryId} exists with different content");
        }
    }

    private static EffectiveLaneConfiguration DeriveEffectiveConfiguration(LaneReductionInput input)
    {
        var configuration = input.Defaults with { ActiveToolNames = input.Defaults.ActiveToolNames.ToArray(), Model = input.Defaults.Model with { } };
        var entriesById = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in input.ConfigurationEntries.Concat(input.OwnEntries))
        {
            entriesById[entry.Id] = entry;
        }

        foreach (var entry in entriesById.Values.OrderBy(static entry => entry.Seq))
        {
            configuration = entry switch
            {
                ModelChangeEntry model => configuration with
                {
                    Model = new LaneModel { Provider = model.Provider, ModelId = model.ModelId },
                },
                ThinkingLevelEntry thinking => configuration with { ThinkingLevel = thinking.ThinkingLevel },
                ActiveToolsEntry tools => configuration with { ActiveToolNames = tools.ActiveToolNames.ToArray() },
                MessageEntry message when message.Message.Role == "assistant" &&
                    StringValue(message.Message.Value, "provider") is { Length: > 0 } provider &&
                    StringValue(message.Message.Value, "model") is { Length: > 0 } modelId => configuration with
                    {
                        Model = new LaneModel { Provider = provider, ModelId = modelId },
                    },
                _ => configuration,
            };
        }

        return configuration;
    }

    private static NewestOwnState? DeriveNewestOwn(Entry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        if (entry is not MessageEntry message)
        {
            return new NewestOwnState { EntryId = entry.Id, Type = entry.Type };
        }

        var role = message.Message.Role;
        return new NewestOwnState
        {
            EntryId = entry.Id,
            Type = entry.Type,
            Role = role,
            StopReason = role == "assistant" ? StringValue(message.Message.Value, "stopReason") : null,
        };
    }

    private static ToolBatchState? DeriveToolBatch(
        string operationId,
        IReadOnlyList<LaneRecord> records,
        IReadOnlyList<Entry> ownEntries,
        Dictionary<string, Entry> entriesById,
        HashSet<string> deferredWriteIds)
    {
        var assistantEntry = ownEntries
            .Reverse()
            .OfType<MessageEntry>()
            .FirstOrDefault(entry => entry.Message.Role == "assistant" && HarnessMessageUtilities.GetToolCalls(entry.Message).Count > 0);
        if (assistantEntry is null)
        {
            return null;
        }

        var toolCalls = HarnessMessageUtilities.GetToolCalls(assistantEntry.Message);
        var starts = records
            .OfType<ToolStartedRecord>()
            .Where(record => record.RunId == operationId && record.AssistantEntryId == assistantEntry.Id)
            .ToDictionary(static record => record.ToolIndex);
        var calls = toolCalls.Select((toolCall, toolIndex) =>
        {
            starts.TryGetValue(toolIndex, out var started);
            var startedResult = started is not null && entriesById.TryGetValue(started.ResultEntryId, out var durableResult)
                ? durableResult
                : null;
            var blockedResult = ownEntries.FirstOrDefault(entry =>
                entry.Seq > assistantEntry.Seq &&
                !deferredWriteIds.Contains(entry.Id) &&
                entry is MessageEntry message &&
                message.Message.Role == "toolResult" &&
                StringValue(message.Message.Value, "toolCallId") == toolCall.Id);
            var result = startedResult ?? blockedResult;
            return new ToolCallState
            {
                ToolIndex = toolIndex,
                ToolCall = CloneToolCall(toolCall),
                Started = started is null ? null : CloneRecord(started),
                ResultExists = result is not null,
                Terminate = result is MessageEntry resultMessage ? resultMessage.Terminate : null,
            };
        }).ToArray();

        return new ToolBatchState
        {
            AssistantEntryId = assistantEntry.Id,
            Calls = calls,
            Truncated = StringValue(assistantEntry.Message.Value, "stopReason") == StopReasons.Length,
            Unresolved = calls.Any(static call => !call.ResultExists),
        };
    }

    private static string? GetRunId(LaneRecord record) => record switch
    {
        AbortRequestedRecord abort => abort.RunId,
        OperationFinishedRecord finished => finished.RunId,
        StepAttemptRecord attempt => attempt.RunId,
        ToolStartedRecord tool => tool.RunId,
        QueueEnqueuedRecord enqueue => enqueue.RunId,
        QueueCancelledRecord cancelled => cancelled.RunId,
        WriteDeferredRecord deferred => deferred.RunId,
        UsageRecord usage => usage.RunId,
        _ => null,
    };

    private static OperationIntent CloneIntent(OperationIntent intent)
    {
        var start = new OperationStartedRecord
        {
            Id = "clone",
            Lane = "clone",
            Seq = 1,
            Timestamp = 1,
            Intent = intent,
        };
        return ((OperationStartedRecord)CloneRecord(start)).Intent;
    }

    private static TRecord CloneRecord<TRecord>(TRecord record) where TRecord : LaneRecord =>
        (TRecord)JsonlCodec.DecodeRecordObject(SessionJson.RecordToJson(record));

    private static Entry CloneEntry(Entry entry) =>
        JsonlCodec.DecodeEntryObject(SessionJson.EntryToJson(entry, includeStorageFields: true));

    private static Entry CloneProvisionedEntry(Entry entry) =>
        JsonlCodec.DecodeEntryObject(
            SessionJson.EntryToJson(entry, includeStorageFields: false),
            requireStorage: false);

    private static ToolCall CloneToolCall(ToolCall call) => new(
        call.Id,
        call.Name,
        (JsonObject)call.Arguments.DeepClone(),
        call.ThoughtSignature,
        call.Namespace);

    private static string? StringValue(JsonObject value, string name) => SessionJson.GetString(value, name);

    private static void Corrupt(string reason, string message) => throw new RecordLogCorruption(reason, message);
}

/// <summary>Convenience forwarding methods matching the TypeScript reducer exports.</summary>
public static class ReducerFunctions
{
    /// <summary>Validates a bounded record log.</summary>
    public static void ValidateRecordLog(RecordLogSlice input) => Reducer.ValidateRecordLog(input);

    /// <summary>Reduces one lane from durable recovery inputs.</summary>
    public static LaneReductionResult ReduceLaneState(LaneReductionInput input) => Reducer.ReduceLaneState(input);
}
