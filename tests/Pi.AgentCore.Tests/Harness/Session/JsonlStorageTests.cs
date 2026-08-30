using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;
using Pi.Ai;
using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

#pragma warning disable xUnit1051 // Session APIs expose cancellation; focused tests exercise cancellation separately where relevant.

public sealed class JsonlStorageTests
{
    [Fact(DisplayName = "round trips every entry type and bounded branch queries")]
    public async Task Round_trips_every_entry_type_and_bounded_branch_queries()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var session = await SessionTestHelpers.CreateRepository(root).CreateAsync(
                new JsonlSessionCreateOptions { Id = "entries", Cwd = root });
            var committed = new List<Entry>();
            committed.Add(await session.AppendEntryAsync(new MessageEntry
            {
                Id = "message",
                Message = new AgentMessage(SessionTestHelpers.User("question")),
            }));
            committed.Add(await session.AppendEntryAsync(new MessageEntry
            {
                Id = "assistant-tool-call",
                Message = new AgentMessage(new AssistantMessage
                {
                    Content =
                    [
                        new TextContent("I'll inspect it."),
                        new ToolCall("call-1", "read", new JsonObject { ["path"] = "README.md" }),
                    ],
                    Api = "anthropic-messages",
                    Provider = "anthropic",
                    Model = "claude-sonnet-4-5",
                    Usage = SessionTestHelpers.Usage(1),
                    StopReason = "toolUse",
                    Timestamp = 2,
                }),
            }));
            committed.Add(await session.AppendEntryAsync(new MessageEntry
            {
                Id = "tool-result",
                Message = new AgentMessage(SessionTestHelpers.ToolResult("contents", 3)),
                Terminate = true,
            }));
            committed.Add(await session.AppendEntryAsync(new ModelChangeEntry
            {
                Id = "model",
                Provider = "anthropic",
                ModelId = "claude-sonnet-4-5",
            }));
            committed.Add(await session.AppendEntryAsync(new ThinkingLevelEntry
            {
                Id = "thinking",
                ThinkingLevel = "high",
            }));
            committed.Add(await session.AppendEntryAsync(new ActiveToolsEntry
            {
                Id = "tools",
                ActiveToolNames = ["read", "bash"],
            }));
            committed.Add(await session.AppendEntryAsync(new CompactionEntry
            {
                Id = "compaction",
                Summary = "summary",
                RetainedTail = [new AgentMessage(SessionTestHelpers.User("retained"))],
                TokensBefore = 123,
                Details = new JsonObject { ["source"] = "test" },
                DetailsPresent = true,
                Usage = SessionTestHelpers.Usage(1),
            }));
            committed.Add(await session.AppendEntryAsync(new BranchSummaryEntry
            {
                Id = "branch-summary",
                FromId = "message",
                Summary = "branch",
                Details = new JsonObject { ["reason"] = "navigation" },
                DetailsPresent = true,
                Usage = SessionTestHelpers.Usage(2),
            }));
            committed.Add(await session.AppendEntryAsync(new CustomEntry
            {
                Id = "custom",
                CustomType = "note",
                Data = new JsonObject { ["nested"] = new JsonObject { ["value"] = 1 } },
                DataPresent = true,
            }));

            var restored = await SessionTestHelpers.ReopenAsync(root, session);
            var restoredEntries = await restored.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst });

            Assert.Equal(committed.Select(entry => entry.Id), restoredEntries.Select(entry => entry.Id));
            Assert.Equal(committed.Select(entry => entry.Type), restoredEntries.Select(entry => entry.Type));
            Assert.Equal(
                ["custom", "branch-summary", "compaction"],
                (await restored.FindEntriesOnBranchAsync(new EntryQuery(), new BranchBounds { StopAtType = "compaction" }))
                    .Select(entry => entry.Id));
            Assert.Equal(
                ["compaction", "branch-summary"],
                (await restored.FindEntriesAsync(new EntryQuery
                {
                    Order = EntryOrder.OldestFirst,
                    Cursor = new EntryCursor { AfterSeq = committed[5].Seq },
                    Limit = 2,
                })).Select(entry => entry.Id));
            Assert.Equal(["custom"], (await restored.FindEntriesAsync(new EntryQuery { CustomType = "note" })).Select(entry => entry.Id));
            Assert.Equal(3, (await restored.GetStatsAsync()).MessageCount);

            var custom = (CustomEntry)(await restored.GetEntryAsync("custom"))!;
            custom.Data!.AsObject()["nested"]!.AsObject()["value"] = 99;
            var logCustom = (CustomEntry)((EntryLogItem)(await restored.GetLogAsync()).Single(item => item is EntryLogItem log && log.Entry.Id == "custom")).Entry;
            logCustom.Data!.AsObject()["nested"]!.AsObject()["value"] = 100;

            var restoredAgain = Assert.IsType<CustomEntry>(await restored.GetEntryAsync("custom"));
            Assert.Equal(1, restoredAgain.Data!.AsObject()["nested"]!.AsObject()["value"]!.GetValue<int>());
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "round trips every record type, recovery projection, and ledger statistics")]
    public async Task Round_trips_every_record_type_recovery_projection_and_ledger_statistics()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var session = await SessionTestHelpers.CreateRepository(root).CreateAsync(
                new JsonlSessionCreateOptions { Id = "records", Cwd = root });
            await session.AppendCustomEntryAsync("anchor");
            var records = new List<LaneRecord>();

            async Task Append(LaneRecord record) => records.Add(await session.AppendRecordAsync(record));

            await Append(SessionTestHelpers.RunStarted("run", "main", "anchor") with
            {
                Intent = new RunOperationIntent
                {
                    OriginalPrompt = [new AgentMessage(SessionTestHelpers.User("prompt"))],
                    InitialMessages =
                    [
                        new MessageEntry
                        {
                            Id = "initial",
                            Message = new AgentMessage(SessionTestHelpers.User("initial")),
                        },
                    ],
                    SystemPromptOverride = "system",
                    ResumeData = new JsonObject { ["extension"] = new JsonObject { ["version"] = 1 } },
                },
            });
            await Append(new QueueEnqueuedRecord
            {
                Id = "steer",
                Lane = "main",
                Queue = "steer",
                RunId = "run",
                Target = new MessageEntry { Id = "steer-message", Message = new AgentMessage(SessionTestHelpers.User("steer")) },
            });
            await Append(new QueueEnqueuedRecord
            {
                Id = "follow-up",
                Lane = "main",
                Queue = "followUp",
                Target = new MessageEntry { Id = "follow-up-message", Message = new AgentMessage(SessionTestHelpers.User("follow up")) },
            });
            await Append(new StepAttemptRecord
            {
                Id = "assistant-attempt",
                Lane = "main",
                RunId = "run",
                Step = "assistant",
                Attempt = 1,
                ResultEntryId = "assistant-result",
            });
            await Append(new ToolStartedRecord
            {
                Id = "tool",
                Lane = "main",
                RunId = "run",
                AssistantEntryId = "assistant-result",
                ToolIndex = 0,
                ToolCallId = "call-1",
                ToolName = "read",
                EffectiveArgs = new JsonObject { ["path"] = "README.md" },
                ResultEntryId = "tool-result",
                Replay = "safe",
            });
            await Append(new WriteDeferredRecord
            {
                Id = "deferred-write",
                Lane = "main",
                RunId = "run",
                Target = new CustomEntry
                {
                    Id = "deferred-entry",
                    CustomType = "fact",
                    Data = new JsonObject { ["value"] = true },
                    DataPresent = true,
                },
            });
            await Append(new UsageRecord
            {
                Id = "assistant-usage",
                Lane = "main",
                Cause = "assistant",
                RunId = "run",
                EntryId = "assistant-result",
                Attempt = 1,
                StopReason = "stop",
                Usage = SessionTestHelpers.Usage(1),
            });
            await Append(new UsageRecord
            {
                Id = "deferred-usage",
                Lane = "main",
                Cause = "deferred_fetch",
                RunId = "run",
                EntryId = "deferred-result",
                Attempt = 1,
                StopReason = "deferred",
                Usage = SessionTestHelpers.Usage(2),
            });
            await Append(new UsageRecord
            {
                Id = "tool-usage",
                Lane = "main",
                Cause = "tool",
                RunId = "run",
                EntryId = "tool-result",
                ToolCallId = "call-1",
                Usage = SessionTestHelpers.Usage(3),
            });
            await Append(new UsageRecord
            {
                Id = "hook-usage",
                Lane = "main",
                Cause = "hook",
                RunId = "run",
                EntryId = "hook-result",
                Usage = SessionTestHelpers.Usage(4),
            });
            await Append(new UsageRecord
            {
                Id = "adjustment",
                Lane = "main",
                Cause = "adjustment",
                Details = new JsonObject { ["reason"] = "correction" },
                DetailsPresent = true,
                Usage = SessionTestHelpers.Usage(5),
            });
            await Append(new AbortRequestedRecord { Id = "abort", Lane = "main", RunId = "run" });
            await Append(new OperationFinishedRecord
            {
                Id = "run-finished",
                Lane = "main",
                RunId = "run",
                Outcome = "aborted",
            });
            await Append(new QueueEnqueuedRecord
            {
                Id = "next-run",
                Lane = "main",
                Queue = "nextRun",
                Target = new MessageEntry { Id = "next-message", Message = new AgentMessage(SessionTestHelpers.User("next")) },
            });
            await Append(new QueueCancelledRecord { Id = "queue-cancelled", Lane = "main", EntryId = "next-message" });
            await Append(new OperationStartedRecord
            {
                Id = "compaction",
                Lane = "main",
                SourceLeafId = "anchor",
                Intent = new CompactionOperationIntent { CustomInstructions = "short", ResultEntryId = "compaction-result" },
            });
            await Append(new StepAttemptRecord
            {
                Id = "compaction-attempt",
                Lane = "main",
                RunId = "compaction",
                Step = "compaction",
                Attempt = 1,
                ResultEntryId = "compaction-result",
                CompactionReason = "manual",
            });
            await Append(new OperationFinishedRecord
            {
                Id = "compaction-finished",
                Lane = "main",
                RunId = "compaction",
                Outcome = "completed",
            });
            await Append(new OperationStartedRecord
            {
                Id = "navigation",
                Lane = "main",
                SourceLeafId = "anchor",
                Intent = new NavigationOperationIntent
                {
                    TargetId = null,
                    Summarize = true,
                    CustomInstructions = "summarize",
                    Label = "checkpoint",
                    SummaryEntryId = "navigation-summary",
                },
            });
            await Append(new StepAttemptRecord
            {
                Id = "branch-attempt",
                Lane = "main",
                RunId = "navigation",
                Step = "branch_summary",
                Attempt = 1,
                ResultEntryId = "navigation-summary",
            });

            var restored = await SessionTestHelpers.ReopenAsync(root, session);
            Assert.Equal(records.Select(record => record.Id), (await restored.FindRecordsAsync(new RecordQuery { Order = EntryOrder.OldestFirst })).Select(record => record.Id));
            Assert.Equal(["run"], (await restored.FindRecordsAsync(new RecordQuery { Type = "operation_started", OperationKind = "run", Limit = 1 })).Select(record => record.Id));
            Assert.Equal(
                ["compaction", "compaction-attempt", "compaction-finished"],
                (await restored.FindRecordsAsync(new RecordQuery { RunId = "compaction", Order = EntryOrder.OldestFirst })).Select(record => record.Id));
            Assert.Equal(
                ["adjustment", "hook-usage"],
                (await restored.FindRecordsAsync(new RecordQuery { Type = "usage", AfterSeq = records[6].Seq, Limit = 2 })).Select(record => record.Id));
            Assert.Equal(["navigation"], (await restored.FindOpenOperationsAsync("main", 2)).Select(record => record.Id));
            Assert.Equal(
                new SessionStats { MessageCount = 0, CachedTokens = 45, UncachedTokens = 75, TotalTokens = 150, CostTotal = 15 },
                await restored.GetStatsAsync());

            var started = (OperationStartedRecord)(await restored.FindRecordsAsync(new RecordQuery { Type = "operation_started", OperationKind = "run" })).Single();
            ((RunOperationIntent)started.Intent).OriginalPrompt.ToList().Add(new AgentMessage(SessionTestHelpers.User("mutated")));
            Assert.Equal(records.Select(record => record.Id), (await restored.FindRecordsAsync(new RecordQuery { Order = EntryOrder.OldestFirst })).Select(record => record.Id));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "persists concurrent cross-lane writes in shared sequence order")]
    public async Task Persists_concurrent_cross_lane_writes_in_shared_sequence_order()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var session = await SessionTestHelpers.CreateRepository(root).CreateAsync(
                new JsonlSessionCreateOptions { Id = "concurrent", Cwd = root });
            var rootEntry = await session.AppendCustomEntryAsync("root");
            await session.CreateLaneAsync("thread", rootEntry);
            var writes = await Task.WhenAll(
                session.AppendEntryAsync(new CustomEntry { Id = "main-1", CustomType = "note" }, "main"),
                session.AppendEntryAsync(new CustomEntry { Id = "thread-1", CustomType = "note" }, "thread"),
                session.AppendEntryAsync(new CustomEntry { Id = "main-2", CustomType = "note" }, "main"),
                session.AppendEntryAsync(new CustomEntry { Id = "thread-2", CustomType = "note" }, "thread"));

            var restored = await SessionTestHelpers.ReopenAsync(root, session);
            var order = (await restored.GetLogAsync()).OfType<EntryLogItem>()
                .Where(item => item.Entry.Id != rootEntry)
                .Select(item => item.Entry.Id)
                .ToArray();
            Assert.Equal(writes.Length, order.Length);
            Assert.Equal(writes.OrderBy(entry => entry.Seq).Select(entry => entry.Id), order);
            Assert.Equal(6, (await restored.GetLogAsync()).Count);
            Assert.Equal(6, (await restored.GetLogAsync()).Select(item => item.Seq).Distinct().Count());
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects non-JSON payloads without changing the durable prefix")]
    public async Task Rejects_non_JSON_payloads_without_changing_the_durable_prefix()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var session = await SessionTestHelpers.CreateRepository(root).CreateAsync(
                new JsonlSessionCreateOptions { Id = "validation", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            var prefix = await File.ReadAllTextAsync(metadata.Path);
            var cyclic = new Dictionary<string, object?>();
            cyclic["self"] = cyclic;

            var error = await Assert.ThrowsAsync<SessionError>(() => session.AppendCustomEntryAsync("invalid", cyclic));
            Assert.Equal(SessionErrorCode.InvalidPayload, error.Code);
            var recordError = await Assert.ThrowsAsync<SessionError>(() => session.AppendRecordAsync(new ToolStartedRecord
            {
                Id = "invalid-record",
                Lane = "main",
                RunId = "run",
                AssistantEntryId = "assistant",
                ToolIndex = 0,
                ToolCallId = "call",
                ToolName = "read",
                EffectiveArgs = new JsonObject { ["value"] = JsonValue.Create(double.NaN) },
                ResultEntryId = "result",
                Replay = "never",
            }));
            Assert.Equal(SessionErrorCode.InvalidPayload, recordError.Code);
            Assert.Equal(prefix, await File.ReadAllTextAsync(metadata.Path));
            Assert.Empty(await session.GetLogAsync());

            var valid = await session.View("main").AppendCustomEntryAsync("valid", new JsonObject { ["value"] = 1 });
            Assert.Equal(1, (await session.GetEntryAsync(valid))!.Seq);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "does not advance state or poison the write queue after an append failure")]
    public async Task Does_not_advance_state_or_poison_the_write_queue_after_an_append_failure()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var fileSystem = new FaultingFileSystem { FailNextAppend = true };
            var repository = new JsonlSessionRepo(fileSystem, root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "append-failure", Cwd = root });

            var error = await Assert.ThrowsAsync<SessionError>(() => session.AppendCustomEntryAsync("rejected"));
            Assert.Equal(SessionErrorCode.Storage, error.Code);
            Assert.Empty(await session.GetLogAsync());
            var committed = await session.AppendCustomEntryAsync("committed");
            Assert.Equal(1, (await session.GetEntryAsync(committed))!.Seq);

            var reopened = await new JsonlSessionRepo(root).OpenAsync(await session.GetMetadataAsync());
            Assert.Equal(committed, Assert.Single((await reopened.GetLogAsync()).OfType<EntryLogItem>()).Entry.Id);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    private sealed class FaultingFileSystem : IJsonlFileSystem
    {
        private readonly LocalJsonlFileSystem _inner = new();

        public bool FailNextAppend { get; set; }

        public string AbsolutePath(string path) => _inner.AbsolutePath(path);
        public string JoinPath(params string[] paths) => _inner.JoinPath(paths);
        public Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadTextFileAsync(path, cancellationToken);
        public Task<IReadOnlyList<string>> ReadTextLinesAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadTextLinesAsync(path, cancellationToken);
        public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) => _inner.WriteFileAsync(path, content, cancellationToken);

        public Task AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("injected append failure");
            }

            return _inner.AppendFileAsync(path, content, cancellationToken);
        }

        public Task RenameFileAsync(string source, string destination, CancellationToken cancellationToken = default) => _inner.RenameFileAsync(source, destination, cancellationToken);
        public Task<JsonlFileInfo> FileInfoAsync(string path, CancellationToken cancellationToken = default) => _inner.FileInfoAsync(path, cancellationToken);
        public Task<IReadOnlyList<JsonlDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default) => _inner.ListDirectoryAsync(path, cancellationToken);
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => _inner.ExistsAsync(path, cancellationToken);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => _inner.CreateDirectoryAsync(path, cancellationToken);
        public Task RemoveAsync(string path, bool force = false, CancellationToken cancellationToken = default) => _inner.RemoveAsync(path, force, cancellationToken);
    }
}
