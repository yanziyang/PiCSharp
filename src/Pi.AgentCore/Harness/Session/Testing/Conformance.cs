using System.Text.Json.Nodes;

using Pi.Ai;

using PiSession = Pi.AgentCore.Harness.Session;

namespace Pi.AgentCore.Harness.Session.Testing;

/// <summary>
/// Creates the runner-independent session cases shipped by Pi. Each returned case owns a fresh
/// fixture, which lets the same suite run against memory, JSONL, or a future backend.
/// </summary>
public static class SessionBackendConformance
{
    /// <summary>Creates all upstream session conformance cases.</summary>
    public static IReadOnlyList<SessionBackendConformanceCase<TMetadata>> Create<TMetadata>(
        Func<Task<ISessionBackendFixture<TMetadata>>> fixtureFactory)
        where TMetadata : SessionMetadata
    {
        ArgumentNullException.ThrowIfNull(fixtureFactory);
        var cases = new List<SessionBackendConformanceCase<TMetadata>>();

        void AddCase(string group, string name, Func<ISessionBackend<TMetadata>, Task> test)
        {
            cases.Add(new SessionBackendConformanceCase<TMetadata>
            {
                Group = group,
                Name = name,
                RunAsync = async () =>
                {
                    await using var fixture = await fixtureFactory().ConfigureAwait(false);
                    await test(fixture.Repository).ConfigureAwait(false);
                },
            });
        }

        AddCase("entries and lanes", "assigns parents and one sequence across every mutation", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var root = await session.AppendEntryAsync(new MessageEntry { Id = "root", Message = UserMessage("root") });
            await session.CreateLaneAsync("thread", root.Id);
            var child = await session.AppendEntryAsync(new CustomEntry { Id = "child", CustomType = "note", Data = JsonValue.Create(1), DataPresent = true }, "thread");
            var record = await session.AppendRecordAsync(OperationStarted("run", "thread", "run"));
            await session.SetNameAsync("Example");
            await session.SetLabelAsync(root.Id, "checkpoint");
            await session.MoveLaneAsync("main", child.Id);

            Expect(root.ParentId is null && root.Seq == 1, "root parent/sequence mismatch");
            Expect(child.ParentId == "root" && child.Seq == 3, "child parent/sequence mismatch");
            Expect(record.Seq == 4, "record sequence mismatch");
            Expect((await session.GetLogAsync()).Select(item => item.Seq).SequenceEqual([1, 2, 3, 4, 5, 6, 7]), "log sequence mismatch");
            var lanes = await session.GetLanesAsync();
            Expect(
                lanes.Count == 2 && lanes[0].Lane == "main" && lanes[0].LeafId == "child" && lanes[1].Lane == "thread" && lanes[1].LeafId == "child",
                "lane pointers mismatch");
        });

        AddCase("records and log", "commits records and lane moves as separate mutations", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var root = await session.AppendEntryAsync(new MessageEntry { Id = "root", Message = UserMessage("root") });
            var finished = await session.AppendRecordAsync(new OperationFinishedRecord
            {
                Id = "finish",
                Lane = "main",
                RunId = "run",
                Outcome = "completed",
            });
            Expect(finished.Seq == 2, "finished record sequence mismatch");
            await session.MoveLaneAsync("main", null);
            var lanes = await session.GetLanesAsync();
            Expect(lanes.Count == 1 && lanes[0].LeafId is null, "lane move was not persisted");
            Expect((await session.GetLogAsync()).Count == 3 && root.Id == "root", "log entry count mismatch");
            await ExpectCode(() => session.MoveLaneAsync("main", "missing"), SessionErrorCode.NotFound);
            Expect((await session.FindRecordsAsync()).Count == 1, "record query mismatch");
        });

        AddCase("entries and lanes", "rejects duplicate ids without changing state", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendEntryAsync(new MessageEntry { Id = "shared", Message = UserMessage("root") });
            await ExpectCode(() => session.AppendRecordAsync(OperationStarted("shared", "main", "run")), SessionErrorCode.AlreadyExists);
            await session.AppendRecordAsync(OperationStarted("run", "main", "run"));
            await ExpectCode(() => session.AppendEntryAsync(new CustomEntry { Id = "run", CustomType = "note" }), SessionErrorCode.AlreadyExists);
            Expect((await session.GetLogAsync()).Count == 2, "duplicate changed state");
        });

        AddCase("entries and lanes", "isolates lanes while sharing the tree", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendEntryAsync(new MessageEntry { Id = "root", Message = UserMessage("root") });
            await session.CreateLaneAsync("thread", "root");
            await session.AppendEntryAsync(new MessageEntry { Id = "main-child", Message = UserMessage("main") });
            await session.View("thread").AppendMessageAsync(UserMessage("thread"));
            var lanes = await session.GetLanesAsync();
            Expect(lanes[0].LeafId == "main-child" && lanes[1].LeafId is not null, "lanes did not diverge");
            Expect((await session.FindEntriesOnBranchAsync(bounds: new BranchBounds { Start = "main-child" }, query: new EntryQuery { Order = EntryOrder.OldestFirst })).Count == 2, "main branch mismatch");
        });

        AddCase("queries and facts", "rejects invalid queries before empty reads", async repository =>
        {
            var session = await repository.CreateAsync("invalid-queries");
            await session.CreateLaneAsync("thread", null);
            await ExpectCode(() => session.FindEntriesAsync(new EntryQuery { Limit = 0 }), SessionErrorCode.InvalidQuery);
            await ExpectCode(() => session.FindEntryAsync(new EntryQuery { Limit = 0 }), SessionErrorCode.InvalidQuery);
            await ExpectCode(() => session.FindEntriesOnBranchAsync(new EntryQuery(), new BranchBounds { Start = "missing" }, CancellationToken.None), SessionErrorCode.NotFound);
            await ExpectCode(() => session.FindRecordsAsync(new RecordQuery { OperationKind = "run" }), SessionErrorCode.InvalidQuery);
            await ExpectCode(() => session.FindRecordsAsync(new RecordQuery { Type = "step_attempt", OperationKind = "run" }), SessionErrorCode.InvalidQuery);
            await ExpectCode(() => session.FindOpenOperationsAsync("main", 0), SessionErrorCode.InvalidQuery);
            await ExpectCode(() => session.GetLogAsync(new LogOptions { AfterSeq = -1 }), SessionErrorCode.InvalidQuery);
        });

        AddCase("queries and facts", "supports bounded filtered and cursor-based queries", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendEntryAsync(new MessageEntry { Id = "root", Message = UserMessage("root") });
            await session.AppendEntryAsync(new CustomEntry { Id = "old-note", CustomType = "note", Data = JsonValue.Create(1), DataPresent = true });
            await session.AppendEntryAsync(new CompactionEntry { Id = "compact", Summary = "summary", TokensBefore = 10 });
            await session.AppendEntryAsync(new CustomEntry { Id = "new-note", CustomType = "note", Data = JsonValue.Create(2), DataPresent = true });
            await session.AppendEntryAsync(new MessageEntry { Id = "tail", Message = AssistantMessage("tail") });
            Expect((await session.FindEntriesAsync()).Select(entry => entry.Id).SequenceEqual(["tail", "new-note", "compact", "old-note", "root"]), "newest entry order mismatch");
            var bounded = await session.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst, Cursor = new EntryCursor { AfterSeq = 2 }, Limit = 2 });
            Expect(bounded.Select(entry => entry.Id).SequenceEqual(["compact", "new-note"]), "cursor query mismatch");
            Expect((await session.FindEntriesAsync(new EntryQuery { CustomType = "note" })).Select(entry => entry.Id).SequenceEqual(["new-note", "old-note"]), "custom filter mismatch");
            var branch = await session.FindEntriesOnBranchAsync(new EntryQuery { CustomType = "note", Limit = 1 }, new BranchBounds { Start = "tail" });
            Expect(branch.Select(entry => entry.Id).SequenceEqual(["new-note"]), "branch filter mismatch");
            await ExpectCode(() => session.FindEntriesOnBranchAsync(bounds: new BranchBounds { Start = "missing" }), SessionErrorCode.NotFound);
        });

        AddCase("records and log", "keeps lane names permanent with their recovery records", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.CreateLaneAsync("thread", null);
            await session.AppendRecordAsync(OperationStarted("old-run", "thread", "run"));
            await session.AppendRecordAsync(new QueueEnqueuedRecord
            {
                Id = "old-next-run",
                Lane = "thread",
                Queue = "nextRun",
                Target = new MessageEntry { Id = "queued-message", Message = UserMessage("queued") },
            });
            Expect((await session.FindRecordsAsync(new RecordQuery { Lane = "thread" })).Select(record => record.Id).SequenceEqual(["old-next-run", "old-run"]), "lane record order mismatch");
            await ExpectCode(() => session.CreateLaneAsync("thread", null), SessionErrorCode.AlreadyExists);
        });

        AddCase("records and log", "persists queue cancellation without consuming its target", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var enqueued = await session.AppendRecordAsync(new QueueEnqueuedRecord
            {
                Id = "enqueue",
                Lane = "main",
                Queue = "nextRun",
                Target = new MessageEntry { Id = "queued-message", Message = UserMessage("queued") },
            });
            var cancelled = await session.AppendRecordAsync(new QueueCancelledRecord { Id = "cancel", Lane = "main", EntryId = "queued-message" });
            Expect(enqueued.Seq == 1 && cancelled.Seq == 2 && cancelled.RunId is null, "queue sequence mismatch");
            Expect(await session.GetEntryAsync("queued-message") is null, "queue target became an entry");
            Expect((await session.FindRecordsAsync(new RecordQuery { Type = "queue_cancelled" })).Count == 1, "queue cancellation missing");
        });

        AddCase("records and log", "filters records by lane type run sequence and order", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendRecordAsync(OperationStarted("run-1", "main", "run"));
            await session.AppendRecordAsync(new StepAttemptRecord { Id = "attempt-1", Lane = "main", RunId = "run-1", Step = "assistant", Attempt = 1, ResultEntryId = "assistant-1" });
            await session.CreateLaneAsync("thread", null);
            await session.AppendRecordAsync(OperationStarted("run-2", "thread", "run"));
            await session.AppendRecordAsync(new StepAttemptRecord { Id = "attempt-2", Lane = "thread", RunId = "run-2", Step = "assistant", Attempt = 1, ResultEntryId = "assistant-2" });
            Expect((await session.FindRecordsAsync(new RecordQuery { Lane = "thread" })).Select(item => item.Id).SequenceEqual(["attempt-2", "run-2"]), "record lane filter mismatch");
            Expect((await session.FindRecordsAsync(new RecordQuery { Type = "step_attempt", Order = EntryOrder.OldestFirst })).Select(item => item.Id).SequenceEqual(["attempt-1", "attempt-2"]), "record type filter mismatch");
            Expect((await session.FindRecordsAsync(new RecordQuery { RunId = "run-1", AfterSeq = 1 })).Single().Id == "attempt-1", "record run cursor mismatch");
        });

        AddCase("records and log", "filters operation starts by operation kind", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendRecordAsync(OperationStarted("run-old", "main", "run"));
            await session.AppendRecordAsync(new OperationFinishedRecord { Id = "run-old-finished", Lane = "main", RunId = "run-old", Outcome = "completed" });
            await session.AppendRecordAsync(OperationStarted("compaction", "main", "compaction"));
            await session.AppendRecordAsync(new OperationFinishedRecord { Id = "compaction-finished", Lane = "main", RunId = "compaction", Outcome = "completed" });
            await session.AppendRecordAsync(OperationStarted("navigation", "main", "navigation"));
            await session.AppendRecordAsync(new OperationFinishedRecord { Id = "navigation-finished", Lane = "main", RunId = "navigation", Outcome = "completed" });
            await session.AppendRecordAsync(OperationStarted("run-new", "main", "run"));
            Expect((await session.FindRecordsAsync(new RecordQuery { Type = "operation_started", OperationKind = "run", Order = EntryOrder.OldestFirst })).Select(item => item.Id).SequenceEqual(["run-old", "run-new"]), "run operation filter mismatch");
            Expect((await session.FindRecordsAsync(new RecordQuery { Type = "operation_started", OperationKind = "compaction" })).Single().Id == "compaction", "compaction filter mismatch");
        });

        AddCase("records and log", "tracks and enforces one open operation per lane", async repository =>
        {
            var session = await repository.CreateAsync("session");
            Expect((await session.FindOpenOperationsAsync("main", 2)).Count == 0, "new lane has an open operation");
            var first = await session.AppendRecordAsync(OperationStarted("first", "main", "run"));
            Expect((await session.FindOpenOperationsAsync("main", 2)).Single().Id == first.Id, "open operation missing");
            await ExpectCode(() => session.AppendRecordAsync(OperationStarted("second", "main", "run")), SessionErrorCode.Storage);
            await session.AppendRecordAsync(new OperationFinishedRecord { Id = "finish-first", Lane = "main", RunId = first.Id, Outcome = "completed" });
            Expect((await session.FindOpenOperationsAsync("main")).Count == 0, "finished operation remained open");
        });

        AddCase("records and log", "does not let an earlier finish close a later start", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendRecordAsync(new OperationFinishedRecord { Id = "finish-before-start", Lane = "main", RunId = "run", Outcome = "completed" });
            var started = await session.AppendRecordAsync(OperationStarted("run", "main", "run"));
            Expect((await session.FindOpenOperationsAsync("main")).Single().Id == started.Id, "earlier finish closed later operation");
        });

        AddCase("records and log", "scopes open operations by lane and limit", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.CreateLaneAsync("thread", null);
            var main = await session.AppendRecordAsync(OperationStarted("main-run", "main", "run"));
            var thread = await session.AppendRecordAsync(OperationStarted("thread-navigation", "thread", "navigation"));
            Expect((await session.FindOpenOperationsAsync("main")).Single().Id == main.Id, "main operation scope mismatch");
            Expect((await session.FindOpenOperationsAsync("thread", 2)).Single().Id == thread.Id, "thread operation scope mismatch");
        });

        AddCase("validation and immutability", "returns immutable open-operation records", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var committed = await session.AppendRecordAsync(OperationStarted("run", "main", "run"));
            var read = (await session.FindOpenOperationsAsync("main")).Single();
            var intent = (RunOperationIntent)read.Intent;
            var mutated = intent.OriginalPrompt.ToList();
            mutated.Add(UserMessage("mutated"));
            var again = (RunOperationIntent)(await session.FindOpenOperationsAsync("main")).Single().Intent;
            Expect(committed.Id == "run" && again.OriginalPrompt.Count == 0, "open operation read was mutable");
        });

        AddCase("queries and facts", "keeps latest-value facts and computes ledger statistics across lanes", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendEntryAsync(new MessageEntry { Id = "user", Message = UserMessage("question") });
            await session.AppendEntryAsync(new MessageEntry { Id = "assistant", Message = AssistantMessage("answer") });
            await session.AppendRecordAsync(new UsageRecord { Id = "assistant-usage", Lane = "main", Cause = "assistant", RunId = "run", EntryId = "assistant", Attempt = 1, StopReason = "stop", Usage = Usage(10, 5, 3, 2, 20, 10) });
            await session.AppendRecordAsync(new UsageRecord { Id = "deferred-usage", Lane = "main", Cause = "deferred_fetch", RunId = "run", EntryId = "deferred-result", Attempt = 1, StopReason = "deferred", Usage = Usage(0, 0, 0, 0, 0, 0) });
            await session.CreateLaneAsync("thread", "assistant");
            await session.AppendRecordAsync(new UsageRecord { Id = "correction", Lane = "thread", Cause = "adjustment", Details = new JsonObject { ["reason"] = "provider correction" }, DetailsPresent = true, Usage = Usage(-2, 0, 0, 0, -2, -0.5) });
            await session.SetNameAsync("First");
            await session.SetNameAsync("Second");
            await session.SetLabelAsync("user", "keep");
            await session.SetLabelAsync("user", null);
            await ExpectCode(() => session.SetLabelAsync("missing", "checkpoint"), SessionErrorCode.NotFound);
            var stats = await session.GetStatsAsync();
            Expect(stats.MessageCount == 2 && stats.CachedTokens == 3 && stats.UncachedTokens == 10 && stats.TotalTokens == 18 && Math.Abs(stats.CostTotal - 9.5) < 0.0001, "usage statistics mismatch");
            Expect(await session.GetNameAsync() == "Second" && await session.GetLabelAsync("user") is null, "facts mismatch");
        });

        AddCase("queries and facts", "clears session names durably", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.SetNameAsync("Temporary");
            await session.SetNameAsync(null);
            Expect(await session.GetNameAsync() is null, "name was not cleared");
            var metadata = await session.GetMetadataAsync();
            var reopened = await repository.OpenAsync(metadata);
            Expect(await reopened.GetNameAsync() is null && (await reopened.GetLogAsync()).Count == 2, "cleared name did not survive reopen");
            var fork = await repository.ForkAsync(metadata, new ForkOptions(), "fork");
            Expect(await fork.GetNameAsync() is null, "cleared name reappeared in fork");
        });

        AddCase("validation and immutability", "returns immutable copies from reads", async repository =>
        {
            var session = await repository.CreateAsync("immutable");
            await session.AppendEntryAsync(new CustomEntry
            {
                Id = "custom",
                CustomType = "note",
                Data = new JsonObject { ["nested"] = new JsonObject { ["value"] = 1 } },
                DataPresent = true,
            });
            var read = (CustomEntry)(await session.GetEntryAsync("custom"))!;
            ((JsonObject)read.Data!["nested"]!)["value"] = 99;
            var second = (CustomEntry)(await session.GetEntryAsync("custom"))!;
            Expect(((JsonObject)second.Data!["nested"]!)["value"]!.GetValue<int>() == 1, "entry read was mutable");
        });

        AddCase("entries and lanes", "validates lane lifecycle and targets", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await ExpectCode(() => session.CreateLaneAsync("main", null), SessionErrorCode.AlreadyExists);
            await ExpectCode(() => session.CreateLaneAsync("thread", "missing"), SessionErrorCode.NotFound);
            await ExpectCode(() => session.MoveLaneAsync("missing", null), SessionErrorCode.InvalidLane);
        });

        AddCase("entries and lanes", "binds lane views without caching leaves", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var root = await session.AppendMessageAsync(UserMessage("root"));
            await session.CreateLaneAsync("thread", root);
            var thread = session.View("thread");
            var writes = await Task.WhenAll(session.AppendMessageAsync(UserMessage("main")), thread.AppendMessageAsync(UserMessage("thread")));
            Expect(await session.GetLeafIdAsync() == writes[0] && await thread.GetLeafIdAsync() == writes[1], "lane view leaf was cached");
            var empty = await repository.CreateAsync("empty");
            Expect((await empty.FindEntriesOnBranchAsync()).Count == 0, "empty branch was not empty");
        });

        AddCase("entries and lanes", "appends provisioned entries with their existing ids", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var entry = await session.AppendEntryAsync(new CustomEntry { Id = "provisioned", CustomType = "note", Data = JsonValue.Create(1), DataPresent = true });
            Expect(entry.Id == "provisioned" && entry.ParentId is null && entry.Seq == 1 && await session.GetLeafIdAsync() == "provisioned", "provisioned entry mismatch");
        });

        AddCase("entries and lanes", "persists tool-result termination decisions", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var entry = await session.AppendEntryAsync(new MessageEntry { Id = "tool-result", Message = ToolMessage("call-1", "example"), Terminate = true });
            var stored = (MessageEntry)(await session.GetEntryAsync(entry.Id))!;
            Expect(stored.Terminate == true && (await session.FindEntriesAsync()).Count == 1, "termination decision was not persisted");
        });

        AddCase("validation and immutability", "rejects non-JSON entries before storage mutation", async repository =>
        {
            var session = await repository.CreateAsync("session");
            var invalid = new JsonObject { ["value"] = JsonValue.Create(double.NaN) };
            await ExpectCode(() => session.AppendCustomEntryAsync("invalid", invalid), SessionErrorCode.InvalidPayload);
            Expect((await session.GetLogAsync()).Count == 0, "invalid custom entry changed state");
            var valid = await session.AppendCustomEntryAsync("valid", new JsonObject { ["value"] = 1 });
            Expect((await session.GetEntryAsync(valid)) is not null, "valid custom entry was rejected");
        });

        AddCase("validation and immutability", "rejects non-JSON records before storage mutation", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await ExpectCode(() => session.AppendRecordAsync(new ToolStartedRecord
            {
                Id = "invalid-record",
                Lane = "main",
                RunId = "run",
                AssistantEntryId = "assistant",
                ToolIndex = 0,
                ToolCallId = "call",
                ToolName = "example",
                EffectiveArgs = new JsonObject { ["value"] = JsonValue.Create(double.NaN) },
                ResultEntryId = "result",
                Replay = "never",
            }), SessionErrorCode.InvalidPayload);
            Expect((await session.FindRecordsAsync()).Count == 0, "invalid record changed state");
            Expect((await session.AppendRecordAsync(OperationStarted("valid-record", "main", "run"))).Seq == 1, "valid record sequence mismatch");
        });

        AddCase("entries and lanes", "linearizes concurrent writes across two lanes", async repository =>
        {
            var session = await repository.CreateAsync("session");
            await session.AppendEntryAsync(new MessageEntry { Id = "root", Message = UserMessage("root") });
            await session.CreateLaneAsync("thread", "root");
            var mainWrite = session.AppendEntryAsync(new CustomEntry { Id = "main-1", CustomType = "note" });
            var threadWrite = session.View("thread").AppendCustomEntryAsync("note");
            await Task.WhenAll(mainWrite, threadWrite);
            var entries = new Entry[]
            {
                await mainWrite,
                (await session.GetEntryAsync(await threadWrite))!,
            };
            Expect(entries.Select(item => item.Seq).Distinct().Count() == entries.Length, "concurrent sequence collision");
            Expect((await session.GetLogAsync()).Where(item => item.Kind == "entry").Count() == 3, "concurrent log count mismatch");
        });

        AddCase("repository and forks", "creates lists and opens sessions", async repository =>
        {
            var session = await repository.CreateAsync("one");
            var id = await session.AppendMessageAsync(UserMessage("persisted"));
            var metadata = await session.GetMetadataAsync();
            var listed = await repository.ListAsync();
            Expect(listed.Count == 1 && listed[0].Id == metadata.Id, "repository list mismatch");
            Expect((await (await repository.OpenAsync(metadata)).FindEntriesAsync()).Single().Id == id, "reopened entry missing");
            await ExpectCode(() => repository.CreateAsync("one"), SessionErrorCode.AlreadyExists);
        });

        AddCase("repository and forks", "deletes sessions idempotently", async repository =>
        {
            var session = await repository.CreateAsync("one");
            var metadata = await session.GetMetadataAsync();
            await repository.DeleteAsync(metadata);
            await ExpectCode(() => repository.OpenAsync(metadata), SessionErrorCode.NotFound);
            await repository.DeleteAsync(metadata);
        });

        AddCase("repository and forks", "forks one branch with selected facts and no records", async repository =>
        {
            var source = await repository.CreateAsync("source");
            var root = await source.AppendMessageAsync(UserMessage("root"));
            var shared = await source.AppendMessageAsync(AssistantMessage("shared"));
            await source.CreateLaneAsync("thread", shared);
            var threadChild = await source.View("thread").AppendMessageAsync(UserMessage("thread"));
            var mainChild = await source.AppendMessageAsync(UserMessage("main"));
            await source.SetNameAsync("Source");
            await source.SetLabelAsync(shared, "copied");
            await source.SetLabelAsync(threadChild, "excluded");
            await source.AppendRecordAsync(OperationStarted("run", "main", "run"));
            await source.AppendRecordAsync(new UsageRecord { Id = "source-usage", Lane = "main", Cause = "adjustment", Usage = Usage(10, 5, 3, 2, 20, 10) });
            var fork = await repository.ForkAsync(await source.GetMetadataAsync(), new ForkOptions { Scope = "branch", EntryId = mainChild, Position = "at" }, "branch-fork");
            Expect((await fork.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst })).Select(entry => entry.Id).SequenceEqual([root, shared, mainChild]), "branch fork entries mismatch");
            Expect(await fork.GetNameAsync() == "Source" && await fork.GetLabelAsync(shared) == "copied" && await fork.GetLabelAsync(threadChild) is null, "branch fork facts mismatch");
            Expect((await fork.FindRecordsAsync()).Count == 0 && (await fork.GetStatsAsync()).MessageCount == 3, "branch fork records/stats mismatch");
            Expect((await fork.GetMetadataAsync()).ParentSessionId == "source", "fork parent mismatch");
        });

        AddCase("repository and forks", "forks a complete tree with lanes and facts", async repository =>
        {
            var source = await repository.CreateAsync("source");
            var root = await source.AppendMessageAsync(UserMessage("root"));
            await source.CreateLaneAsync("thread", root);
            var mainChild = await source.AppendMessageAsync(UserMessage("main"));
            var threadChild = await source.View("thread").AppendMessageAsync(UserMessage("thread"));
            await source.SetLabelAsync(threadChild, "thread-tip");
            var fork = await repository.ForkAsync(await source.GetMetadataAsync(), new ForkOptions { Scope = "tree" }, "tree-fork");
            Expect((await fork.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst })).Select(entry => entry.Id).SequenceEqual([root, mainChild, threadChild]), "tree fork entries mismatch");
            var lanes = await fork.GetLanesAsync();
            Expect(lanes.Count == 2 && lanes[0].LeafId == mainChild && lanes[1].LeafId == threadChild && await fork.GetLabelAsync(threadChild) == "thread-tip", "tree fork lanes/facts mismatch");
        });

        AddCase("repository and forks", "forks before an entry without modifying the source", async repository =>
        {
            var source = await repository.CreateAsync("source");
            var root = await source.AppendMessageAsync(UserMessage("root"));
            var tail = await source.AppendMessageAsync(UserMessage("tail"));
            var fork = await repository.ForkAsync(await source.GetMetadataAsync(), new ForkOptions { EntryId = tail }, "fork");
            Expect((await fork.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst })).Single().Id == root && await source.GetLeafIdAsync() == tail, "before fork changed source or target");
            var before = await repository.ForkAsync(await source.GetMetadataAsync(), new ForkOptions { Position = "before" }, "before-default-target");
            Expect((await before.FindEntriesAsync()).Count == 1, "before default fork mismatch");
            var at = await repository.ForkAsync(await source.GetMetadataAsync(), new ForkOptions { Position = "at" }, "at-default-target");
            Expect((await at.FindEntriesAsync()).Count == 2, "at default fork mismatch");
            var sourceMetadata = await source.GetMetadataAsync();
            await ExpectCode(() => repository.ForkAsync(sourceMetadata, new ForkOptions { EntryId = "missing" }, "missing"), SessionErrorCode.InvalidForkTarget);
        });

        AddCase("repository and forks", "validates the default fork target", async repository =>
        {
            var source = await repository.CreateAsync("source-with-custom-leaf");
            await source.AppendCustomEntryAsync("not-a-message");
            var sourceMetadata = await source.GetMetadataAsync();
            await ExpectCode(() => repository.ForkAsync(sourceMetadata, new ForkOptions(), "fork"), SessionErrorCode.InvalidForkTarget);
        });

        return cases;
    }

    /// <summary>Runs every case against one fixture factory.</summary>
    public static async Task RunAllAsync<TMetadata>(Func<Task<ISessionBackendFixture<TMetadata>>> fixtureFactory)
        where TMetadata : SessionMetadata
    {
        foreach (var testCase in Create(fixtureFactory))
        {
            try
            {
                await testCase.RunAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"{testCase.Group}: {testCase.Name}", error);
            }
        }
    }

    private static AgentMessage UserMessage(string text) => AgentMessage.FromJson(new JsonObject
    {
        ["role"] = "user",
        ["content"] = TextContent(text),
        ["timestamp"] = 1,
    });

    private static AgentMessage AssistantMessage(string text) => AgentMessage.FromJson(new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = TextContent(text),
        ["api"] = "anthropic-messages",
        ["provider"] = "anthropic",
        ["model"] = "claude-sonnet-4-5",
        ["usage"] = new JsonObject
        {
            ["input"] = 0,
            ["output"] = 0,
            ["cacheRead"] = 0,
            ["cacheWrite"] = 0,
            ["totalTokens"] = 0,
            ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 },
        },
        ["stopReason"] = "stop",
        ["timestamp"] = 1,
    });

    private static AgentMessage ToolMessage(string callId, string toolName) => AgentMessage.FromJson(new JsonObject
    {
        ["role"] = "toolResult",
        ["toolCallId"] = callId,
        ["toolName"] = toolName,
        ["content"] = TextContent("done"),
        ["isError"] = false,
        ["timestamp"] = 1,
    });

    private static OperationStartedRecord OperationStarted(string id, string lane, string kind) => new()
    {
        Id = id,
        Lane = lane,
        SourceLeafId = null,
        Intent = kind switch
        {
            "run" => new RunOperationIntent(),
            "compaction" => new CompactionOperationIntent { ResultEntryId = id + "-result" },
            "navigation" => new NavigationOperationIntent { TargetId = null, Summarize = false },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        },
    };

    private static Usage Usage(int input, int output, int cacheRead, int cacheWrite, int totalTokens, double totalCost) => new()
    {
        Input = input,
        Output = output,
        CacheRead = cacheRead,
        CacheWrite = cacheWrite,
        TotalTokens = totalTokens,
        Cost = new UsageCost { Total = totalCost },
    };

    private static JsonArray TextContent(string text)
    {
        var content = new JsonArray();
        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        return content;
    }

    private static async Task ExpectCode(Func<Task> operation, string code)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (SessionError error) when (error.Code == code)
        {
            return;
        }

        throw new InvalidOperationException($"Expected SessionError with code {code}");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
