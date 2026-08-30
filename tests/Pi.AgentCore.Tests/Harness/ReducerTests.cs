using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class ReducerTests
{
    private static readonly MessageEntry _assistantToolsEntry = HarnessTestHelpers.Persisted(
        HarnessTestHelpers.MessageTarget(
            "assistant-tools",
            HarnessTestHelpers.Assistant(
                [new ToolCall("call-1", "tool-1", new JsonObject())],
                stopReason: StopReasons.ToolUse)),
        3);

    private static readonly MessageEntry _toolResultTarget = HarnessTestHelpers.MessageTarget(
        "tool-result-1",
        HarnessTestHelpers.ToolResult());

    private static readonly MessageEntry _assistantFinalTarget = HarnessTestHelpers.MessageTarget(
        "assistant-final",
        HarnessTestHelpers.AssistantText("done"));

    [Theory(DisplayName = "rejects {0}")]
    [MemberData(nameof(CorruptionData))]
    public void Rejects_corrupt_record_logs(string name, string reason, RecordLogSlice input)
    {
        Assert.True(name.Length > 0);
        var exception = Assert.Throws<RecordLogCorruption>(() => Reducer.ValidateRecordLog(input));
        Assert.Equal(reason, exception.Reason);
    }

    public static IEnumerable<object[]> CorruptionData()
    {
        var assistantTools = _assistantToolsEntry;
        yield return [
            "multiple operations are open",
            RecordLogCorruptionReasons.MultipleOpenOperations,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.RunStarted(2, "run-2"),
            ]),
        ];
        yield return [
            "a record references an operation that does not exist",
            RecordLogCorruptionReasons.UnknownOperation,
            HarnessTestHelpers.RecoverySlice([HarnessTestHelpers.AbortRequested(1, "missing")]),
        ];
        yield return [
            "a record follows its operation finish",
            RecordLogCorruptionReasons.RecordAfterFinish,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.OperationFinished(2),
                HarnessTestHelpers.AbortRequested(3),
            ]),
        ];
        yield return [
            "attempt numbers skip within one assistant step",
            RecordLogCorruptionReasons.NonConsecutiveAttempt,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-1"),
                HarnessTestHelpers.Attempt(3, "run-1", "assistant", 3, "assistant-2"),
            ]),
        ];
        yield return [
            "a non-compaction attempt carries compactionReason",
            RecordLogCorruptionReasons.InvalidCompactionReason,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-1", "manual"),
            ]),
        ];
        yield return [
            "a compaction attempt omits compactionReason",
            RecordLogCorruptionReasons.InvalidCompactionReason,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "compaction", 1, "compaction-1"),
            ]),
        ];
        yield return [
            "steering is enqueued after abort",
            RecordLogCorruptionReasons.QueueAfterAbort,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.AbortRequested(2),
                HarnessTestHelpers.QueueEnqueued(3),
            ]),
        ];
        yield return [
            "a queue cancellation has no enqueue",
            RecordLogCorruptionReasons.InvalidQueueCancellation,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.QueueCancelled(2),
            ]),
        ];
        yield return [
            "a queue cancellation targets an entry that exists",
            RecordLogCorruptionReasons.InvalidQueueCancellation,
            HarnessTestHelpers.RecoverySlice(
                [
                    HarnessTestHelpers.RunStarted(1),
                    HarnessTestHelpers.QueueEnqueued(2),
                    HarnessTestHelpers.QueueCancelled(4),
                ],
                [HarnessTestHelpers.Persisted(
                    HarnessTestHelpers.MessageTarget("queue-1", HarnessTestHelpers.User("queued")),
                    3)]),
        ];
        yield return [
            "structural attempts disagree on resultEntryId",
            RecordLogCorruptionReasons.InconsistentStep,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "compaction", 1, "compaction-1", "threshold"),
                HarnessTestHelpers.Attempt(3, "run-1", "compaction", 2, "compaction-2", "threshold"),
            ]),
        ];
        yield return [
            "structural attempts disagree on compactionReason",
            RecordLogCorruptionReasons.InconsistentStep,
            HarnessTestHelpers.RecoverySlice([
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "compaction", 1, "compaction-1", "threshold"),
                HarnessTestHelpers.Attempt(3, "run-1", "compaction", 2, "compaction-1", "overflow"),
            ]),
        ];
        yield return [
            "tool_started does not match the assistant tool call",
            RecordLogCorruptionReasons.ToolCallMismatch,
            HarnessTestHelpers.RecoverySlice(
                [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.ToolStarted(4, toolCallId: "different-call")],
                [assistantTools]),
        ];
        yield return [
            "two tool_started records share an invocation identity",
            RecordLogCorruptionReasons.DuplicateToolInvocation,
            HarnessTestHelpers.RecoverySlice(
                [
                    HarnessTestHelpers.RunStarted(1),
                    HarnessTestHelpers.ToolStarted(4),
                    HarnessTestHelpers.ToolStarted(5, resultEntryId: "tool-result-2"),
                ],
                [assistantTools]),
        ];
        yield return [
            "a provisioned id exists with different content",
            RecordLogCorruptionReasons.ProvisionedEntryMismatch,
            HarnessTestHelpers.RecoverySlice(
                [HarnessTestHelpers.RunStarted(
                    1,
                    initialMessages: [HarnessTestHelpers.MessageTarget("prompt-1", HarnessTestHelpers.User("expected"))])],
                [HarnessTestHelpers.Persisted(
                    HarnessTestHelpers.MessageTarget("prompt-1", HarnessTestHelpers.User("different")),
                    2)]),
        ];
        var deferred = HarnessTestHelpers.Assistant([], stopReason: StopReasons.Deferred);
        deferred.Value.Remove("deferred");
        yield return [
            "a deferred assistant message has no handle",
            RecordLogCorruptionReasons.InvalidDeferredHandle,
            HarnessTestHelpers.RecoverySlice(
                [HarnessTestHelpers.RunStarted(1)],
                [HarnessTestHelpers.Persisted(
                    HarnessTestHelpers.MessageTarget("assistant-deferred", deferred),
                    2)]),
        ];
    }

    [Fact(DisplayName = "does not mutate its bounded recovery inputs")]
    public void Does_not_mutate_its_bounded_recovery_inputs()
    {
        var target = HarnessTestHelpers.MessageTarget("prompt-1", HarnessTestHelpers.User("hello"));
        var start = HarnessTestHelpers.RunStarted(1, initialMessages: [target]);
        var entry = HarnessTestHelpers.Persisted(target, 2);
        var input = new RecordLogSlice
        {
            Lane = "main",
            OpenOperations = [start],
            Records = [start],
            Entries = [entry],
        };

        Reducer.ValidateRecordLog(input);

        Assert.Equal([start], input.Records);
        Assert.Equal([entry], input.Entries);
    }

    [Theory(DisplayName = "accepts {0}")]
    [MemberData(nameof(ValidPrefixData))]
    public void Accepts_valid_section_six_durable_prefix(string name, RecordLogSlice input)
    {
        Assert.True(name.Length > 0);
        Reducer.ValidateRecordLog(input);
    }

    public static IEnumerable<object[]> ValidPrefixData()
    {
        foreach (var prefix in ValidPrefixes("one-tool run X1-X5", [
                     Record(HarnessTestHelpers.RunStarted(1, initialMessages: [PromptTarget()])),
                     Entry(HarnessTestHelpers.Persisted(PromptTarget(), 2)),
                     Record(HarnessTestHelpers.Attempt(3, "run-1", "assistant", 1, "assistant-tools")),
                     Entry(HarnessTestHelpers.Persisted(_assistantToolsEntry, 4, "prompt-1")),
                     Record(HarnessTestHelpers.ToolStarted(5)),
                     Entry(HarnessTestHelpers.Persisted(_toolResultTarget, 6, "assistant-tools")),
                     Record(HarnessTestHelpers.Attempt(7, "run-1", "assistant", 1, "assistant-final")),
                     Entry(HarnessTestHelpers.Persisted(_assistantFinalTarget, 8, "tool-result-1")),
                     Record(HarnessTestHelpers.OperationFinished(9)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("assistant retry", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-attempt-1")),
                     Record(HarnessTestHelpers.UsageRecord(3, "assistant-attempt-1")),
                     Record(HarnessTestHelpers.Attempt(4, "run-1", "assistant", 2, "assistant-attempt-2")),
                     Record(HarnessTestHelpers.UsageRecord(5, "assistant-attempt-2", StopReasons.Stop, 2)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget("assistant-attempt-2", HarnessTestHelpers.AssistantText("ok")),
                         6)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("terminal assistant failure", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-error")),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget(
                             "assistant-error",
                             HarnessTestHelpers.Assistant([], stopReason: StopReasons.Error) with
                             {
                                 Value = new JsonObject
                                 {
                                     ["role"] = "assistant",
                                     ["content"] = new JsonArray(),
                                     ["api"] = "openai-responses",
                                     ["provider"] = "openai",
                                     ["model"] = "test-model",
                                     ["usage"] = new JsonObject
                                     {
                                         ["input"] = 1,
                                         ["output"] = 1,
                                         ["cacheRead"] = 0,
                                         ["cacheWrite"] = 0,
                                         ["totalTokens"] = 2,
                                         ["cost"] = new JsonObject
                                         {
                                             ["input"] = 0,
                                             ["output"] = 0,
                                             ["cacheRead"] = 0,
                                             ["cacheWrite"] = 0,
                                             ["total"] = 0,
                                         },
                                     },
                                     ["stopReason"] = StopReasons.Error,
                                     ["errorMessage"] = "failed",
                                     ["timestamp"] = 1,
                                 },
                             }),
                         3)),
                     Record(HarnessTestHelpers.OperationFinished(4, "run-1", "failed")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("overflow compaction and retry", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "discarded-overflow")),
                     Record(HarnessTestHelpers.UsageRecord(3, "discarded-overflow", StopReasons.Length)),
                     Record(HarnessTestHelpers.Attempt(4, "run-1", "compaction", 1, "overflow-compaction", "overflow")),
                     Entry(HarnessTestHelpers.CompactionEntry("overflow-compaction", 5)),
                     Record(HarnessTestHelpers.Attempt(6, "run-1", "assistant", 1, "assistant-after-compaction")),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget("assistant-after-compaction", HarnessTestHelpers.AssistantText("fits")),
                         7)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("steering acceptance and consumption", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.QueueEnqueued(2)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget("queue-1", HarnessTestHelpers.User("queued")),
                         3)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("queue cancellation", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.QueueEnqueued(2)),
                     Record(HarnessTestHelpers.QueueCancelled(3)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("deferred write acceptance and application", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.WriteDeferred(2)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget("write-1", HarnessTestHelpers.User("deferred write")),
                         3)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("abort during a tool", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-tools")),
                     Entry(HarnessTestHelpers.Persisted(_assistantToolsEntry, 3)),
                     Record(HarnessTestHelpers.ToolStarted(4)),
                     Record(HarnessTestHelpers.AbortRequested(5)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget(
                             "tool-result-1",
                             HarnessTestHelpers.ToolResult(text: "interrupted", isError: true)),
                         6)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("threshold auto-compaction", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "compaction", 1, "threshold-compaction", "threshold")),
                     Entry(HarnessTestHelpers.CompactionEntry("threshold-compaction", 3)),
                     Record(HarnessTestHelpers.Attempt(4, "run-1", "assistant", 1, "assistant-after-threshold")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("manual compaction", [
                     Record(HarnessTestHelpers.CompactionStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "compact-1", "compaction", 1, "compaction-1", "manual")),
                     Entry(HarnessTestHelpers.CompactionEntry("compaction-1", 3)),
                     Record(HarnessTestHelpers.OperationFinished(4, "compact-1")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("move-first navigation summary", [
                     Record(HarnessTestHelpers.NavigationStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "navigate-1", "branch_summary", 1, "summary-1")),
                     Entry(HarnessTestHelpers.BranchSummaryEntry("summary-1", 3)),
                     Record(HarnessTestHelpers.OperationFinished(4, "navigate-1")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("blocked tool without an intent record", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-tools")),
                     Entry(HarnessTestHelpers.Persisted(_assistantToolsEntry, 3)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget(
                             "blocked-result",
                             HarnessTestHelpers.ToolResult(text: "blocked", isError: true)),
                         4)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("idle next-run cancellation", [
                     Record(HarnessTestHelpers.QueueEnqueued(
                         1,
                         HarnessTestHelpers.MessageTarget("next-1", HarnessTestHelpers.User("later")),
                         "nextRun")),
                     Record(HarnessTestHelpers.QueueCancelled(2, "next-1", null)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("next-run enqueue after abort", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.AbortRequested(2)),
                     Record(HarnessTestHelpers.QueueEnqueued(
                         3,
                         HarnessTestHelpers.MessageTarget("next-1", HarnessTestHelpers.User("later")),
                         "nextRun")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("deferred write applied during abort reconciliation", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.WriteDeferred(2)),
                     Record(HarnessTestHelpers.AbortRequested(3)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget("write-1", HarnessTestHelpers.User("deferred write")),
                         4)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("accepted steering killed by abort", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.QueueEnqueued(2)),
                     Record(HarnessTestHelpers.AbortRequested(3)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("compaction retry", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "compaction", 1, "threshold-compaction", "threshold")),
                     Record(HarnessTestHelpers.Attempt(3, "run-1", "compaction", 2, "threshold-compaction", "threshold")),
                     Entry(HarnessTestHelpers.CompactionEntry("threshold-compaction", 4)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("hook-supplied manual compaction", [
                     Record(HarnessTestHelpers.CompactionStarted(1)),
                     Entry(HarnessTestHelpers.CompactionEntry("compaction-1", 2)),
                     Record(HarnessTestHelpers.OperationFinished(3, "compact-1")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("hook-supplied navigation summary", [
                     Record(HarnessTestHelpers.NavigationStarted(1)),
                     Entry(HarnessTestHelpers.BranchSummaryEntry("summary-1", 2)),
                     Record(HarnessTestHelpers.OperationFinished(3, "navigate-1")),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("deferred provider suspension and redemption", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-deferred")),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget(
                             "assistant-deferred",
                             HarnessTestHelpers.Assistant([], stopReason: StopReasons.Deferred)),
                         3)),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget(
                             "assistant-redeemed",
                             HarnessTestHelpers.AssistantText("ready")),
                         4)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }

        foreach (var prefix in ValidPrefixes("abort of a deferred provider request", [
                     Record(HarnessTestHelpers.RunStarted(1)),
                     Record(HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-deferred")),
                     Entry(HarnessTestHelpers.Persisted(
                         HarnessTestHelpers.MessageTarget(
                             "assistant-deferred",
                             HarnessTestHelpers.Assistant([], stopReason: StopReasons.Deferred)),
                         3)),
                     Record(HarnessTestHelpers.AbortRequested(4)),
                 ]))
        {
            yield return [prefix.Name, prefix.Input];
        }
    }

    [Fact(DisplayName = "reduces an idle lane to pending next-run input and default configuration")]
    public void Reduces_an_idle_lane_to_pending_next_run_input_and_default_configuration()
    {
        var pending = HarnessTestHelpers.MessageTarget("next-pending", HarnessTestHelpers.User("pending"));
        var cancelled = HarnessTestHelpers.MessageTarget("next-cancelled", HarnessTestHelpers.User("cancelled"));
        var consumed = HarnessTestHelpers.MessageTarget("next-consumed", HarnessTestHelpers.User("consumed"));
        var input = HarnessTestHelpers.ReductionInput(
            [
                HarnessTestHelpers.QueueEnqueued(1, pending, "nextRun"),
                HarnessTestHelpers.QueueEnqueued(2, cancelled, "nextRun"),
                HarnessTestHelpers.QueueCancelled(3, cancelled.Id, null),
                HarnessTestHelpers.QueueEnqueued(4, consumed, "nextRun"),
            ],
            entries: [HarnessTestHelpers.Persisted(consumed, 5)],
            leafId: "idle-leaf");

        var result = Reducer.ReduceLaneState(input);

        Assert.Equal("main", result.LaneState.Lane);
        Assert.Equal("idle-leaf", result.LaneState.LeafId);
        Assert.Null(result.LaneState.Operation);
        Assert.Single(result.LaneState.PendingNextRun);
        Assert.Equal(pending.Id, result.LaneState.PendingNextRun[0].Id);
        AssertConfiguration(result.EffectiveConfiguration, HarnessTestHelpers.Defaults);
        Assert.Null(result.TerminalFailure);
    }

    [Fact(DisplayName = "folds persisted configuration over copied defaults in sequence")]
    public void Folds_persisted_configuration_over_copied_defaults_in_sequence()
    {
        var configurationEntries = new Entry[]
        {
            new ModelChangeEntry
            {
                Id = "model-change",
                ParentId = null,
                Seq = 1,
                Timestamp = 1,
                Provider = "persisted-provider",
                ModelId = "persisted-model",
            },
            new ThinkingLevelEntry
            {
                Id = "thinking-change",
                ParentId = "model-change",
                Seq = 2,
                Timestamp = 2,
                ThinkingLevel = ThinkingLevels.High,
            },
            new ActiveToolsEntry
            {
                Id = "tools-change",
                ParentId = "thinking-change",
                Seq = 3,
                Timestamp = 3,
                ActiveToolNames = ["persisted-tool"],
            },
        };
        var input = HarnessTestHelpers.ReductionInput([], configurationEntries: configurationEntries);

        var result = Reducer.ReduceLaneState(input);

        Assert.Equal(new LaneModel { Provider = "persisted-provider", ModelId = "persisted-model" }, result.EffectiveConfiguration.Model);
        Assert.Equal(ThinkingLevels.High, result.EffectiveConfiguration.ThinkingLevel);
        Assert.Equal(["persisted-tool"], result.EffectiveConfiguration.ActiveToolNames);
        AssertConfiguration(input.Defaults, HarnessTestHelpers.Defaults);
    }

    [Fact(DisplayName = "applies committed operation-owned configuration after the anchor")]
    public void Applies_committed_operation_owned_configuration_after_the_anchor()
    {
        var assistant = HarnessTestHelpers.Persisted(
            HarnessTestHelpers.MessageTarget("assistant-config", HarnessTestHelpers.AssistantText("response")),
            2);
        assistant.Message.Value["provider"] = "response-provider";
        assistant.Message.Value["model"] = "response-model";
        var tools = new ActiveToolsEntry
        {
            Id = "operation-tools",
            ParentId = assistant.Id,
            Seq = 3,
            Timestamp = 3,
            ActiveToolNames = ["operation-tool"],
        };

        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1)],
            [assistant, tools]));

        Assert.Equal(new LaneModel { Provider = "response-provider", ModelId = "response-model" }, result.EffectiveConfiguration.Model);
        Assert.Equal(ThinkingLevels.Off, result.EffectiveConfiguration.ThinkingLevel);
        Assert.Equal(["operation-tool"], result.EffectiveConfiguration.ActiveToolNames);
    }

    [Fact(DisplayName = "keeps captured next-run input with the open run instead of pending next-run")]
    public void Keeps_captured_next_run_input_with_the_open_run_instead_of_pending_next_run()
    {
        var captured = HarnessTestHelpers.MessageTarget("next-captured", HarnessTestHelpers.User("captured"));
        var later = HarnessTestHelpers.MessageTarget("next-later", HarnessTestHelpers.User("later"));
        var start = HarnessTestHelpers.RunStarted(2, initialMessages: [captured]);

        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [
                HarnessTestHelpers.QueueEnqueued(1, captured, "nextRun"),
                start,
                HarnessTestHelpers.QueueEnqueued(3, later, "nextRun"),
            ]));

        Assert.Equal([later.Id], result.LaneState.PendingNextRun.Select(entry => entry.Id));
        Assert.Equal([captured.Id], result.LaneState.Operation!.MissingInitialMessages.Select(entry => entry.Id));
    }

    [Fact(DisplayName = "derives missing input, queues, deferred writes, and the unfinished attempt")]
    public void Derives_missing_input_queues_deferred_writes_and_the_unfinished_attempt()
    {
        var missingPrompt = HarnessTestHelpers.MessageTarget("prompt-missing", HarnessTestHelpers.User("missing"));
        var committedPrompt = HarnessTestHelpers.MessageTarget("prompt-committed", HarnessTestHelpers.User("committed"));
        var steer = HarnessTestHelpers.MessageTarget("steer-pending", HarnessTestHelpers.User("steer"));
        var consumedFollowUp = HarnessTestHelpers.MessageTarget("follow-consumed", HarnessTestHelpers.User("follow"));
        var nextRun = HarnessTestHelpers.MessageTarget("next-run", HarnessTestHelpers.User("next"));
        var pendingWrite = HarnessTestHelpers.MessageTarget("write-pending", HarnessTestHelpers.User("write"));
        var appliedWrite = HarnessTestHelpers.MessageTarget("write-applied", HarnessTestHelpers.User("applied"));
        var start = HarnessTestHelpers.RunStarted(1, initialMessages: [missingPrompt, committedPrompt]);
        var committedPromptEntry = HarnessTestHelpers.Persisted(committedPrompt, 2);
        var consumedFollowUpEntry = HarnessTestHelpers.Persisted(consumedFollowUp, 6, committedPrompt.Id);
        var appliedWriteEntry = HarnessTestHelpers.Persisted(appliedWrite, 9, consumedFollowUp.Id);

        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [
                start,
                HarnessTestHelpers.QueueEnqueued(3, steer),
                HarnessTestHelpers.QueueEnqueued(4, consumedFollowUp, "followUp"),
                HarnessTestHelpers.QueueEnqueued(5, nextRun, "nextRun"),
                HarnessTestHelpers.WriteDeferred(7, pendingWrite),
                HarnessTestHelpers.WriteDeferred(8, appliedWrite),
                HarnessTestHelpers.Attempt(10, start.Id, "assistant", 1, "assistant-pending"),
            ],
            [committedPromptEntry, consumedFollowUpEntry, appliedWriteEntry]));

        var operation = result.LaneState.Operation ?? throw new InvalidOperationException("Expected an open operation.");
        Assert.Equal([nextRun.Id], result.LaneState.PendingNextRun.Select(entry => entry.Id));
        Assert.False(operation.Aborting);
        Assert.Equal([missingPrompt.Id], operation.MissingInitialMessages.Select(entry => entry.Id));
        Assert.Equal([steer.Id], operation.PendingSteer.Select(entry => entry.Id));
        Assert.Empty(operation.PendingFollowUp);
        Assert.Equal([pendingWrite.Id], operation.PendingWrites.Select(entry => entry.Id));
        Assert.Equal("assistant", operation.Step!.Kind);
        Assert.Equal(1, operation.Step.Attempts);
        Assert.Equal("assistant-pending", operation.Step.ResultEntryId);
        Assert.Equal(appliedWrite.Id, operation.NewestOwn!.EntryId);
        Assert.Equal("message", operation.NewestOwn.Type);
        Assert.Equal("user", operation.NewestOwn.Role);
    }

    [Fact(DisplayName = "kills steer and follow-up queues on abort while preserving writes and next-run input")]
    public void Kills_steer_and_follow_up_queues_on_abort_while_preserving_writes_and_next_run_input()
    {
        var steer = HarnessTestHelpers.MessageTarget("steer-aborted", HarnessTestHelpers.User("steer"));
        var followUp = HarnessTestHelpers.MessageTarget("follow-aborted", HarnessTestHelpers.User("follow"));
        var nextRun = HarnessTestHelpers.MessageTarget("next-after-abort", HarnessTestHelpers.User("next"));
        var pendingWrite = HarnessTestHelpers.MessageTarget("write-after-abort", HarnessTestHelpers.User("write"));

        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput([
            HarnessTestHelpers.RunStarted(1),
            HarnessTestHelpers.QueueEnqueued(2, steer),
            HarnessTestHelpers.QueueEnqueued(3, followUp, "followUp"),
            HarnessTestHelpers.QueueEnqueued(4, nextRun, "nextRun"),
            HarnessTestHelpers.WriteDeferred(5, pendingWrite),
            HarnessTestHelpers.AbortRequested(6),
        ]));

        var operation = result.LaneState.Operation ?? throw new InvalidOperationException("Expected an open operation.");
        Assert.Equal([nextRun.Id], result.LaneState.PendingNextRun.Select(entry => entry.Id));
        Assert.True(operation.Aborting);
        Assert.Empty(operation.PendingSteer);
        Assert.Empty(operation.PendingFollowUp);
        Assert.Equal([pendingWrite.Id], operation.PendingWrites.Select(entry => entry.Id));
    }

    [Fact(DisplayName = "reduces an unfinished assistant, compaction, and branch summary step")]
    public void Reduces_an_unfinished_assistant_compaction_and_branch_summary_step()
    {
        var cases = new[]
        {
            (Name: "assistant", Record: HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "result"), Kind: "assistant", Reason: (string?)null),
            (Name: "compaction", Record: HarnessTestHelpers.Attempt(2, "run-1", "compaction", 1, "result", "overflow"), Kind: "compaction", Reason: (string?)"overflow"),
            (Name: "branch summary", Record: HarnessTestHelpers.Attempt(2, "run-1", "branch_summary", 1, "result"), Kind: "branch_summary", Reason: (string?)null),
        };

        foreach (var testCase in cases)
        {
            var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput([
                HarnessTestHelpers.RunStarted(1),
                testCase.Record,
            ]));
            var step = result.LaneState.Operation!.Step ?? throw new InvalidOperationException($"Expected an unfinished {testCase.Name} step.");
            Assert.Equal(testCase.Kind, step.Kind);
            Assert.Equal(1, step.Attempts);
            Assert.Equal("result", step.ResultEntryId);
            Assert.Equal(testCase.Reason, step.CompactionReason);
        }
    }

    [Fact(DisplayName = "closes the newest attempt only when its provisioned result exists")]
    public void Closes_the_newest_attempt_only_when_its_provisioned_result_exists()
    {
        var target = HarnessTestHelpers.MessageTarget("result", HarnessTestHelpers.AssistantText("done"));
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, target.Id)],
            [HarnessTestHelpers.Persisted(target, 3)]));

        Assert.Null(result.LaneState.Operation!.Step);
    }

    [Fact(DisplayName = "ignores unfulfilled result ids from earlier attempts")]
    public void Ignores_unfulfilled_result_ids_from_earlier_attempts()
    {
        var target = HarnessTestHelpers.MessageTarget("attempt-2-result", HarnessTestHelpers.AssistantText("done"));
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "attempt-1-result"),
                HarnessTestHelpers.Attempt(3, "run-1", "assistant", 2, target.Id),
            ],
            [HarnessTestHelpers.Persisted(target, 4)]));

        Assert.Null(result.LaneState.Operation!.Step);
    }

    [Fact(DisplayName = "reduces tool batch state at X1, X3, and X5")]
    public void Reduces_tool_batch_state_at_x1_x3_and_x5()
    {
        var cases = new[]
        {
            (Name: "X1", Records: new LaneRecord[]
            {
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-tools"),
            }, Result: (MessageEntry?)null),
            (Name: "X3", Records: new LaneRecord[]
            {
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-tools"),
                HarnessTestHelpers.ToolStarted(4),
            }, Result: (MessageEntry?)null),
            (Name: "X5", Records: new LaneRecord[]
            {
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-tools"),
                HarnessTestHelpers.ToolStarted(4),
            }, Result: HarnessTestHelpers.Persisted(_toolResultTarget, 5, _assistantToolsEntry.Id) with { Terminate = true }),
        };

        foreach (var testCase in cases)
        {
            var ownEntries = testCase.Result is null
                ? [_assistantToolsEntry]
                : new Entry[] { _assistantToolsEntry, testCase.Result };
            var reduction = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(testCase.Records, ownEntries));
            var batch = reduction.LaneState.Operation!.ToolBatch ?? throw new InvalidOperationException($"Expected tool batch {testCase.Name}.");
            var call = Assert.Single(batch.Calls);
            Assert.Equal(_assistantToolsEntry.Id, batch.AssistantEntryId);
            Assert.False(batch.Truncated);
            Assert.Equal(testCase.Result is null, batch.Unresolved);
            Assert.Equal(0, call.ToolIndex);
            Assert.Equal("call-1", call.ToolCall.Id);
            Assert.Equal("tool-1", call.ToolCall.Name);
            Assert.Equal(testCase.Result is not null, call.ResultExists);
            if (testCase.Result is not null)
            {
                Assert.True(call.Terminate);
            }

            Assert.Equal(
                testCase.Records.Any(record => record is ToolStartedRecord),
                call.Started is not null);
        }
    }

    [Fact(DisplayName = "does not resolve a tool batch from a deferred-write tool result")]
    public void Does_not_resolve_a_tool_batch_from_a_deferred_write_tool_result()
    {
        var assistant = HarnessTestHelpers.Persisted(_assistantToolsEntry, 3);
        var writtenResult = HarnessTestHelpers.MessageTarget("written-tool-result", HarnessTestHelpers.ToolResult());
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, assistant.Id),
                HarnessTestHelpers.WriteDeferred(4, writtenResult),
            ],
            [assistant, HarnessTestHelpers.Persisted(writtenResult, 5, assistant.Id)]));

        var call = Assert.Single(result.LaneState.Operation!.ToolBatch!.Calls);
        Assert.False(call.ResultExists);
        Assert.True(result.LaneState.Operation.ToolBatch.Unresolved);
    }

    [Fact(DisplayName = "matches blocked results without tool-start records and preserves source order")]
    public void Matches_blocked_results_without_tool_start_records_and_preserves_source_order()
    {
        var assistant = HarnessTestHelpers.Persisted(
            HarnessTestHelpers.MessageTarget(
                "assistant-two-tools",
                HarnessTestHelpers.Assistant(
                    [
                        new ToolCall("call-1", "tool-1", new JsonObject()),
                        new ToolCall("call-2", "tool-2", new JsonObject()),
                    ],
                    stopReason: StopReasons.ToolUse)),
            3);
        var blocked = HarnessTestHelpers.Persisted(
            HarnessTestHelpers.MessageTarget(
                "blocked-result",
                HarnessTestHelpers.ToolResult("call-1", "tool-1", "blocked", true)),
            4,
            assistant.Id);
        var secondStart = HarnessTestHelpers.ToolStarted(
            5,
            assistantEntryId: assistant.Id,
            toolIndex: 1,
            toolCallId: "call-2",
            toolName: "tool-2",
            resultEntryId: "call-2-result");

        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, assistant.Id), secondStart],
            [assistant, blocked]));

        var calls = result.LaneState.Operation!.ToolBatch!.Calls;
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].ResultExists);
        Assert.Equal("call-1", calls[0].ToolCall.Id);
        Assert.Equal(1, calls[1].ToolIndex);
        Assert.Equal("call-2", calls[1].ToolCall.Id);
        Assert.Equal(secondStart.Id, calls[1].Started!.Id);
        Assert.False(calls[1].ResultExists);
    }

    [Fact(DisplayName = "marks a length-stopped tool batch as truncated without resolving it")]
    public void Marks_a_length_stopped_tool_batch_as_truncated_without_resolving_it()
    {
        var truncated = HarnessTestHelpers.Persisted(
            HarnessTestHelpers.MessageTarget(
                "assistant-truncated",
                HarnessTestHelpers.Assistant(
                    [new ToolCall("call-1", "tool-1", new JsonObject())],
                    stopReason: StopReasons.Length)),
            3);
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, truncated.Id)],
            [truncated]));

        var batch = result.LaneState.Operation!.ToolBatch ?? throw new InvalidOperationException("Expected tool batch.");
        Assert.True(batch.Truncated);
        Assert.True(batch.Unresolved);
    }

    [Fact(DisplayName = "detects an unredeemed deferred handle only at the operation tail")]
    public void Detects_an_unredeemed_deferred_handle_only_at_the_operation_tail()
    {
        var deferredMessage = HarnessTestHelpers.Assistant([], stopReason: StopReasons.Deferred);
        var deferredEntry = HarnessTestHelpers.Persisted(
            HarnessTestHelpers.MessageTarget("assistant-deferred", deferredMessage),
            3);
        var pending = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, deferredEntry.Id)],
            [deferredEntry]));
        Assert.NotNull(pending.LaneState.Operation!.Deferred);
        Assert.Equal("deferred-1", pending.LaneState.Operation.Deferred!.Id);

        var successor = HarnessTestHelpers.Persisted(
            HarnessTestHelpers.MessageTarget("assistant-ready", HarnessTestHelpers.AssistantText("ready")),
            4,
            deferredEntry.Id);
        var redeemed = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, deferredEntry.Id)],
            [deferredEntry, successor]));
        Assert.Null(redeemed.LaneState.Operation!.Deferred);
    }

    [Theory(DisplayName = "derives {0} terminal-failure provenance")]
    [MemberData(nameof(TerminalFailureData))]
    public void Derives_terminal_failure_provenance(
        string name,
        IReadOnlyList<LaneRecord> records,
        IReadOnlyList<Entry> ownEntries,
        string expectedSource)
    {
        Assert.True(name.Length > 0);
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(records, ownEntries));
        Assert.NotNull(result.TerminalFailure);
        Assert.Equal(expectedSource, result.TerminalFailure!.Source);
    }

    public static IEnumerable<object[]> TerminalFailureData()
    {
        yield return [
            "step",
            (IReadOnlyList<LaneRecord>)[
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-error"),
            ],
            (IReadOnlyList<Entry>)[HarnessTestHelpers.Persisted(
                HarnessTestHelpers.MessageTarget(
                    "assistant-error",
                    HarnessTestHelpers.Assistant([], stopReason: StopReasons.Error) with
                    {
                        Value = ErrorAssistantJson("failed"),
                    }),
                3)],
            "step",
        ];
        yield return [
            "deferred fetch",
            (IReadOnlyList<LaneRecord>)[
                HarnessTestHelpers.RunStarted(1),
                HarnessTestHelpers.Attempt(2, "run-1", "assistant", 1, "assistant-deferred"),
            ],
            (IReadOnlyList<Entry>)[
                HarnessTestHelpers.Persisted(
                    HarnessTestHelpers.MessageTarget("assistant-deferred", HarnessTestHelpers.Assistant([], stopReason: StopReasons.Deferred)),
                    3),
                HarnessTestHelpers.Persisted(
                    HarnessTestHelpers.MessageTarget(
                        "deferred-error",
                        HarnessTestHelpers.Assistant([], stopReason: StopReasons.Error) with { Value = ErrorAssistantJson("expired") }),
                    4,
                    "assistant-deferred"),
            ],
            "deferred_fetch",
        ];
        yield return [
            "deferred fetch usage record",
            (IReadOnlyList<LaneRecord>)[
                HarnessTestHelpers.RunStarted(1),
                new UsageRecord
                {
                    Id = "deferred-usage",
                    Lane = "main",
                    Seq = 3,
                    Timestamp = 3,
                    Cause = "deferred_fetch",
                    RunId = "run-1",
                    EntryId = "deferred-error",
                    Attempt = 1,
                    StopReason = StopReasons.Error,
                    Usage = HarnessTestHelpers.Usage(1, 1),
                },
            ],
            (IReadOnlyList<Entry>)[HarnessTestHelpers.Persisted(
                HarnessTestHelpers.MessageTarget(
                    "deferred-error",
                    HarnessTestHelpers.Assistant([], stopReason: StopReasons.Error) with
                    {
                        Value = ErrorAssistantJson("expired"),
                    }),
                2)],
            "deferred_fetch",
        ];
    }

    [Fact(DisplayName = "does not classify an error-shaped deferred write as terminal failure")]
    public void Does_not_classify_an_error_shaped_deferred_write_as_terminal_failure()
    {
        var target = HarnessTestHelpers.MessageTarget(
            "written-error",
            HarnessTestHelpers.Assistant([], stopReason: StopReasons.Error) with { Value = ErrorAssistantJson("note") });
        var entry = HarnessTestHelpers.Persisted(target, 3);
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            [HarnessTestHelpers.RunStarted(1), HarnessTestHelpers.WriteDeferred(2, target)],
            [entry]));

        Assert.Null(result.TerminalFailure);
    }

    [Theory(DisplayName = "derives structural target state for {0}")]
    [MemberData(nameof(StructuralTargetData))]
    public void Derives_structural_target_state_for(
        string name,
        IReadOnlyList<LaneRecord> records,
        IReadOnlyList<Entry> entries,
        bool? expectedResult,
        bool? expectedSummary)
    {
        Assert.True(name.Length > 0);
        var result = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(records, entries));
        Assert.Equal(expectedResult, result.LaneState.Operation!.Targets.Result);
        Assert.Equal(expectedSummary, result.LaneState.Operation.Targets.Summary);
    }

    public static IEnumerable<object[]> StructuralTargetData()
    {
        yield return [
            "manual compaction result",
            (IReadOnlyList<LaneRecord>)[HarnessTestHelpers.CompactionStarted(1)],
            (IReadOnlyList<Entry>)[],
            false,
            null!,
        ];
        yield return [
            "completed manual compaction result",
            (IReadOnlyList<LaneRecord>)[HarnessTestHelpers.CompactionStarted(1)],
            (IReadOnlyList<Entry>)[HarnessTestHelpers.CompactionEntry("compaction-1", 2)],
            true,
            null!,
        ];
        yield return [
            "missing navigation summary",
            (IReadOnlyList<LaneRecord>)[HarnessTestHelpers.NavigationStarted(1)],
            (IReadOnlyList<Entry>)[],
            null!,
            false,
        ];
        yield return [
            "navigation summary",
            (IReadOnlyList<LaneRecord>)[HarnessTestHelpers.NavigationStarted(1)],
            (IReadOnlyList<Entry>)[HarnessTestHelpers.BranchSummaryEntry("summary-1", 2)],
            null!,
            true,
        ];
    }

    [Fact(DisplayName = "resets the overflow guard only after newer conversational input is consumed")]
    public void Resets_the_overflow_guard_only_after_newer_conversational_input_is_consumed()
    {
        var initial = HarnessTestHelpers.MessageTarget("initial", HarnessTestHelpers.User("initial"));
        var steer = HarnessTestHelpers.MessageTarget("steer", HarnessTestHelpers.User("steer"));
        var start = HarnessTestHelpers.RunStarted(1, initialMessages: [initial]);
        var initialEntry = HarnessTestHelpers.Persisted(initial, 2);
        var records = new LaneRecord[]
        {
            start,
            HarnessTestHelpers.Attempt(3, start.Id, "compaction", 1, "overflow-summary", "overflow"),
            HarnessTestHelpers.QueueEnqueued(5, steer),
        };

        var used = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(records, [initialEntry]));
        Assert.True(used.LaneState.Operation!.OverflowRecoveryUsed);

        var reset = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(
            records,
            [initialEntry, HarnessTestHelpers.Persisted(steer, 6, initial.Id)]));
        Assert.False(reset.LaneState.Operation!.OverflowRecoveryUsed);
    }

    [Fact(DisplayName = "is deterministic and does not mutate or alias its inputs")]
    public void Is_deterministic_and_does_not_mutate_or_alias_its_inputs()
    {
        var pending = HarnessTestHelpers.MessageTarget("next", HarnessTestHelpers.User("next"));
        var input = HarnessTestHelpers.ReductionInput([
            HarnessTestHelpers.QueueEnqueued(1, pending, "nextRun"),
        ]);
        var before = SnapshotInput(input);

        var first = Reducer.ReduceLaneState(input);
        var second = Reducer.ReduceLaneState(input);

        Assert.Equal(ReductionFingerprint(first), ReductionFingerprint(second));
        Assert.Equal(before, SnapshotInput(input));
        var outputEntry = Assert.IsType<MessageEntry>(first.LaneState.PendingNextRun[0]);
        outputEntry.Message.Value["content"] = "mutated-output";
        Assert.Equal("next", input.Records[0] is QueueEnqueuedRecord enqueue ? enqueue.Target.Id : null);
        Assert.Equal("next", ((QueueEnqueuedRecord)input.Records[0]).Target.Id);
        Assert.Equal(
            "next",
            HarnessMessageUtilities.ContentText(((MessageEntry)((QueueEnqueuedRecord)input.Records[0]).Target).Message.ToPiMessage()!));
    }

    [Fact(DisplayName = "replaying a prefix and its remainder equals replaying the whole mutation sequence")]
    public void Replaying_a_prefix_and_its_remainder_equals_replaying_the_whole_mutation_sequence()
    {
        var pending = HarnessTestHelpers.MessageTarget("next", HarnessTestHelpers.User("next"));
        var records = new LaneRecord[]
        {
            HarnessTestHelpers.QueueEnqueued(1, pending, "nextRun"),
            HarnessTestHelpers.RunStarted(2),
            HarnessTestHelpers.AbortRequested(3),
        };
        var whole = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(records));
        var prefix = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(records[..2]));
        Assert.NotNull(prefix.LaneState.Operation);
        var replayedRecords = records[..2].Concat(records[2..]).ToArray();
        var remainder = Reducer.ReduceLaneState(HarnessTestHelpers.ReductionInput(replayedRecords));

        Assert.Equal(ReductionFingerprint(whole), ReductionFingerprint(remainder));
    }

    private static JsonObject ErrorAssistantJson(string message) => new()
    {
        ["role"] = "assistant",
        ["content"] = new JsonArray(),
        ["api"] = "openai-responses",
        ["provider"] = "openai",
        ["model"] = "test-model",
        ["usage"] = new JsonObject
        {
            ["input"] = 1,
            ["output"] = 1,
            ["cacheRead"] = 0,
            ["cacheWrite"] = 0,
            ["totalTokens"] = 2,
            ["cost"] = new JsonObject
            {
                ["input"] = 0,
                ["output"] = 0,
                ["cacheRead"] = 0,
                ["cacheWrite"] = 0,
                ["total"] = 0,
            },
        },
        ["stopReason"] = StopReasons.Error,
        ["errorMessage"] = message,
        ["timestamp"] = 1,
    };

    private static AgentMessage PromptMessage() => HarnessTestHelpers.User("fix the bug");

    private static MessageEntry PromptTarget() => HarnessTestHelpers.MessageTarget("prompt-1", PromptMessage());

    private static RecordAction Record(LaneRecord record) => new(record);

    private static EntryAction Entry(Entry entry) => new(entry);

    private static IEnumerable<(string Name, RecordLogSlice Input)> ValidPrefixes(
        string trace,
        IReadOnlyList<DurableAction> actions)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            var prefix = actions.Take(index + 1).ToArray();
            yield return (
                $"{trace} after action {index + 1}",
                HarnessTestHelpers.RecoverySlice(
                    prefix.OfType<RecordAction>().Select(action => action.Record),
                    prefix.OfType<EntryAction>().Select(action => action.Entry)));
        }
    }

    private static string SnapshotInput(LaneReductionInput input) =>
        string.Join(
            "\n",
            input.Records.Select(record => record.Id + ":" + record.Seq)
                .Concat(input.Entries.Select(entry => entry.Id + ":" + entry.Seq)));

    private static string ReductionFingerprint(LaneReductionResult result)
    {
        var operation = result.LaneState.Operation;
        var step = operation?.Step is { } currentStep
            ? $"{currentStep.Kind}:{currentStep.Attempts}:{currentStep.ResultEntryId}:{currentStep.CompactionReason}"
            : "null";
        var toolBatch = operation?.ToolBatch is { } batch
            ? string.Join(
                ",",
                batch.Calls.Select(call =>
                    $"{call.ToolIndex}:{call.ToolCall.Id}:{call.ToolCall.Name}:{call.ResultExists}:{call.Terminate}:{call.Started?.Id}"))
            : "null";
        return string.Join(
            "|",
            result.LaneState.Lane,
            result.LaneState.LeafId,
            string.Join(",", result.LaneState.PendingNextRun.Select(EntryFingerprint)),
            operation?.Id,
            operation?.Kind,
            operation?.Aborting,
            step,
            toolBatch,
            operation?.ToolBatch?.Truncated,
            operation?.ToolBatch?.Unresolved,
            string.Join(",", operation?.MissingInitialMessages.Select(EntryFingerprint) ?? []),
            string.Join(",", operation?.PendingSteer.Select(EntryFingerprint) ?? []),
            string.Join(",", operation?.PendingFollowUp.Select(EntryFingerprint) ?? []),
            string.Join(",", operation?.PendingWrites.Select(EntryFingerprint) ?? []),
            operation?.Deferred?.Id,
            operation?.OverflowRecoveryUsed,
            operation?.NewestOwn?.EntryId,
            operation?.NewestOwn?.Type,
            operation?.NewestOwn?.Role,
            operation?.NewestOwn?.StopReason,
            operation?.Targets.Result,
            operation?.Targets.Summary,
            result.EffectiveConfiguration.Model.Provider,
            result.EffectiveConfiguration.Model.ModelId,
            result.EffectiveConfiguration.ThinkingLevel,
            string.Join(",", result.EffectiveConfiguration.ActiveToolNames),
            result.TerminalFailure?.EntryId,
            result.TerminalFailure?.Source,
            result.TerminalFailure is { } failure
                ? $"{failure.Message.Provider}:{failure.Message.Model}:{failure.Message.StopReason}:{failure.Message.ErrorMessage}:{string.Join(",", failure.Message.Content.Select(static block => block.Type))}"
                : null);
    }

    private static string EntryFingerprint(Entry entry) =>
        $"{entry.Id}:{entry.Type}:{entry.Seq}:{entry.ParentId}:{(entry is MessageEntry message ? message.Message.Value.ToJsonString() : string.Empty)}";

    private static void AssertConfiguration(EffectiveLaneConfiguration actual, EffectiveLaneConfiguration expected)
    {
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.ThinkingLevel, actual.ThinkingLevel);
        Assert.Equal(expected.ActiveToolNames, actual.ActiveToolNames);
    }

    private abstract record DurableAction;

    private sealed record RecordAction(LaneRecord Record) : DurableAction;

    private sealed record EntryAction(Entry Entry) : DurableAction;
}
