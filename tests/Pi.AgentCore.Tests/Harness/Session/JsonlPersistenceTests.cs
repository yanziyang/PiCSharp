using System.Text.Json.Nodes;

using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;

using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

#pragma warning disable xUnit1051 // Session APIs expose cancellation; focused tests exercise cancellation explicitly where relevant.

public sealed class JsonlPersistenceTests
{
    [Fact(DisplayName = "exposes the complete metadata contract")]
    public async Task Exposes_the_complete_metadata_contract()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var cwd = Path.Combine(root, "workspace", "project");
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions
            {
                Id = "metadata",
                Cwd = cwd,
                ParentSessionId = "parent",
                Metadata = new JsonObject
                {
                    ["owner"] = "agent",
                    ["nested"] = new JsonObject { ["enabled"] = true },
                },
            });
            var metadata = await session.GetMetadataAsync();

            Assert.Equal("metadata", metadata.Id);
            Assert.True(metadata.CreatedAt > 0);
            Assert.Equal(Path.GetFullPath(cwd), metadata.Cwd);
            Assert.Equal("parent", metadata.ParentSessionId);
            Assert.Equal(Path.GetFullPath(metadata.Path), metadata.Path);
            Assert.Equal(4, metadata.SourceFormat);
            Assert.True(File.Exists(metadata.Path));
            Assert.Equal("agent", metadata.Metadata!["owner"]!.GetValue<string>());
            Assert.Single(await repository.ListAsync(new JsonlSessionListOptions { Cwd = cwd }));
            Assert.Empty(await repository.ListAsync(new JsonlSessionListOptions { Cwd = Path.Combine(root, "other", "project") }));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects a malformed JSON header on open and skips it when listing")]
    public async Task Rejects_a_malformed_JSON_header_on_open_and_skips_it_when_listing()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "valid", Cwd = root });
            var malformedSession = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "malformed-header", Cwd = root });
            var metadata = await malformedSession.GetMetadataAsync();
            File.WriteAllText(metadata.Path, "not json\n");

            var error = await Assert.ThrowsAsync<SessionError>(() => repository.OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Equal(["valid"], (await repository.ListAsync(new JsonlSessionListOptions { Cwd = root })).Select(item => item.Id));
            Assert.Equal("not json\n", await File.ReadAllTextAsync(metadata.Path));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects non-object header metadata on open and skips it when listing")]
    public async Task Rejects_non_object_header_metadata_on_open_and_skips_it_when_listing()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "valid", Cwd = root });
            var malformedSession = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "invalid-header-metadata", Cwd = root });
            var metadata = await malformedSession.GetMetadataAsync();
            var malformed = new JsonObject
            {
                ["kind"] = "header",
                ["version"] = 4,
                ["id"] = metadata.Id,
                ["createdAt"] = metadata.CreatedAt,
                ["cwd"] = metadata.Cwd,
                ["metadata"] = "invalid",
            }.ToJsonString() + "\n";
            File.WriteAllText(metadata.Path, malformed);

            var error = await Assert.ThrowsAsync<SessionError>(() => repository.OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Equal(["valid"], (await repository.ListAsync(new JsonlSessionListOptions { Cwd = root })).Select(item => item.Id));
            Assert.Equal(malformed, await File.ReadAllTextAsync(metadata.Path));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects session ids that cannot be used in coding-agent filenames")]
    public async Task Rejects_session_ids_that_cannot_be_used_in_coding_agent_filenames()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var error = await Assert.ThrowsAsync<SessionError>(() => SessionTestHelpers.CreateRepository(root).CreateAsync(
                new JsonlSessionCreateOptions { Id = "../escape", Cwd = root }));
            Assert.Equal(SessionErrorCode.InvalidPayload, error.Code);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "allows the same explicit session id in different working directories")]
    public async Task Allows_the_same_explicit_session_id_in_different_working_directories()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var firstCwd = Path.Combine(root, "workspaces", "first");
            var secondCwd = Path.Combine(root, "workspaces", "second");
            var first = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "shared", Cwd = firstCwd });
            var second = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "shared", Cwd = secondCwd });

            Assert.Equal(Path.GetFullPath(firstCwd), (await first.GetMetadataAsync()).Cwd);
            Assert.Equal(Path.GetFullPath(secondCwd), (await second.GetMetadataAsync()).Cwd);
            Assert.Equal(["shared", "shared"], (await repository.ListAsync()).Select(item => item.Id));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects concurrent create and fork calls for the same destination")]
    public async Task Rejects_concurrent_create_and_fork_calls_for_the_same_destination()
    {
        foreach (var pair in new[] { (First: "create", Second: "create"), (First: "create", Second: "fork"), (First: "fork", Second: "fork") })
        {
            var root = SessionTestHelpers.CreateTempRoot();
            try
            {
                var repository = SessionTestHelpers.CreateRepository(root);
                var source = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "source", Cwd = root });
                var sourceMetadata = await source.GetMetadataAsync();

                async Task Run(string kind)
                {
                    if (kind == "create")
                    {
                        await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "same", Cwd = root });
                    }
                    else
                    {
                        await repository.ForkAsync(
                            sourceMetadata,
                            new ForkOptions(),
                            new JsonlSessionCreateOptions { Id = "same", Cwd = root });
                    }
                }

                var outcomes = await Task.WhenAll(CaptureAsync(() => Run(pair.First)), CaptureAsync(() => Run(pair.Second)));
                Assert.Single(outcomes, outcome => outcome is null);
                var failure = Assert.IsType<SessionError>(Assert.Single(outcomes, outcome => outcome is not null));
                Assert.Equal(SessionErrorCode.AlreadyExists, failure.Code);
                Assert.Single(await repository.ListAsync(new JsonlSessionListOptions { Cwd = root }), item => item.Id == "same");
            }
            finally
            {
                SessionTestHelpers.DeleteTempRoot(root);
            }
        }
    }

    [Fact(DisplayName = "releases a destination reservation after a failed create or fork")]
    public async Task Releases_a_destination_reservation_after_a_failed_create_or_fork()
    {
        foreach (var kind in new[] { "create", "fork" })
        {
            var root = SessionTestHelpers.CreateTempRoot();
            try
            {
                var fileSystem = new ControlledFileSystem();
                var repository = new JsonlSessionRepo(fileSystem, root);
                var source = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "source", Cwd = root });
                var sourceMetadata = await source.GetMetadataAsync();
                if (kind == "create")
                {
                    fileSystem.FailNextWrite = true;
                }
                else
                {
                    fileSystem.FailNextRename = true;
                }

                async Task<Session<JsonlSessionMetadata>> RunAsync() => kind == "create"
                    ? await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "retry", Cwd = root })
                    : await repository.ForkAsync(sourceMetadata, new ForkOptions(), new JsonlSessionCreateOptions { Id = "retry", Cwd = root });

                var failure = await Assert.ThrowsAsync<SessionError>(RunAsync);
                Assert.Equal(SessionErrorCode.Storage, failure.Code);
                Assert.NotNull(await RunAsync());
                Assert.Single(await repository.ListAsync(new JsonlSessionListOptions { Cwd = root }), item => item.Id == "retry");
            }
            finally
            {
                SessionTestHelpers.DeleteTempRoot(root);
            }
        }
    }

    [Fact(DisplayName = "sorts listed sessions by current filesystem modification time")]
    public async Task Sorts_listed_sessions_by_current_filesystem_modification_time()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var newest = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "newest", Cwd = Path.Combine(root, "newest") });
            var newestMetadata = await newest.GetMetadataAsync();
            var oldest = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "oldest", Cwd = Path.Combine(root, "oldest") });
            var oldestMetadata = await oldest.GetMetadataAsync();
            File.SetLastWriteTimeUtc(newestMetadata.Path, DateTime.UnixEpoch.AddMilliseconds(1_700_000_002_000));
            File.SetLastWriteTimeUtc(oldestMetadata.Path, DateTime.UnixEpoch.AddMilliseconds(1_700_000_001_000));

            var listed = await repository.ListAsync();
            Assert.Equal(["newest", "oldest"], listed.Select(item => item.Id));
            Assert.Equal(["newest"], (await repository.ListAsync(new JsonlSessionListOptions { Cwd = newestMetadata.Cwd })).Select(item => item.Id));
            Assert.True(listed[0].ModifiedAt > listed[1].ModifiedAt);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "writes one line per mutation and restores the shared sequence")]
    public async Task Writes_one_line_per_mutation_and_restores_the_shared_sequence()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "session", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            var entryId = await session.AppendCustomEntryAsync("note", new JsonObject { ["value"] = 1 });
            await session.CreateLaneAsync("thread", entryId);
            await session.AppendRecordAsync(SessionTestHelpers.RunStarted("run", "thread"));
            await session.SetNameAsync("Example");
            await session.SetLabelAsync(entryId, "checkpoint");
            await session.MoveLaneAsync("main", null);

            var lines = File.ReadAllLines(metadata.Path).Select(SessionTestHelpers.ParseObject).ToArray();
            Assert.Equal(["header", "entry", "lane", "record", "fact", "fact", "lane"], lines.Select(line => line["kind"]!.GetValue<string>()));
            Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], lines.Skip(1).Select(line => line["seq"]!.GetValue<long>()));

            var reopened = await repository.OpenAsync(metadata);
            Assert.Null((await reopened.GetLanesAsync())[0].LeafId);
            Assert.Equal(entryId, (await reopened.GetLanesAsync())[1].LeafId);
            Assert.Equal("Example", await reopened.GetNameAsync());
            Assert.Equal("checkpoint", await reopened.GetLabelAsync(entryId));
            Assert.Equal(["run"], (await reopened.FindRecordsAsync()).Select(record => record.Id));
            Assert.Equal(["run"], (await reopened.FindOpenOperationsAsync("thread", 2)).Select(record => record.Id));
            Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], (await reopened.GetLogAsync()).Select(item => item.Seq));
            var finished = await reopened.AppendRecordAsync(new OperationFinishedRecord
            {
                Id = "finish",
                Lane = "thread",
                RunId = "run",
                Outcome = "completed",
            });
            Assert.Equal(7, finished.Seq);
            Assert.Empty(await reopened.FindOpenOperationsAsync("thread", 2));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "recomputes fork message counts when reopening")]
    public async Task Recomputes_fork_message_counts_when_reopening()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var source = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "source", Cwd = root });
            await source.AppendMessageAsync(SessionTestHelpers.User("one"));
            await source.AppendMessageAsync(SessionTestHelpers.User("two", 2));
            var fork = await repository.ForkAsync(
                await source.GetMetadataAsync(),
                new ForkOptions(),
                new JsonlSessionCreateOptions { Id = "fork", Cwd = root });
            var forkMetadata = await fork.GetMetadataAsync();

            var reopened = await repository.OpenAsync(forkMetadata);
            Assert.Equal(2, (await reopened.GetStatsAsync()).MessageCount);
            await reopened.AppendMessageAsync(SessionTestHelpers.User("three", 3));
            Assert.Equal(3, (await reopened.GetStatsAsync()).MessageCount);
            var verified = await repository.OpenAsync(forkMetadata);
            Assert.Equal(3, (await verified.GetStatsAsync()).MessageCount);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "reopens a tree fork with its lanes and facts")]
    public async Task Reopens_a_tree_fork_with_its_lanes_and_facts()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var source = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "source", Cwd = root });
            var rootId = await source.AppendCustomEntryAsync("root");
            await source.CreateLaneAsync("thread", rootId);
            var mainId = await source.AppendCustomEntryAsync("main");
            var threadId = await source.View("thread").AppendCustomEntryAsync("thread");
            await source.SetNameAsync("Source");
            await source.SetLabelAsync(threadId, "tip");

            var fork = await repository.ForkAsync(
                await source.GetMetadataAsync(),
                new ForkOptions { Scope = "tree" },
                new JsonlSessionCreateOptions { Id = "fork", Cwd = root });
            var metadata = await fork.GetMetadataAsync();
            var importedEntryLines = File.ReadAllLines(metadata.Path).Select(SessionTestHelpers.ParseObject)
                .Where(line => line["kind"]?.GetValue<string>() == "entry").ToArray();
            Assert.All(importedEntryLines, line => Assert.False(line.ContainsKey("lane")));

            var reopened = await repository.OpenAsync(metadata);
            Assert.Equal([rootId, mainId, threadId], (await reopened.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst })).Select(entry => entry.Id));
            var lanes = await reopened.GetLanesAsync();
            Assert.Equal(mainId, lanes[0].LeafId);
            Assert.Equal(threadId, lanes[1].LeafId);
            Assert.Equal("Source", await reopened.GetNameAsync());
            Assert.Equal("tip", await reopened.GetLabelAsync(threadId));
            Assert.Empty(await reopened.FindRecordsAsync());
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "does not publish a partial fork when staging fails")]
    public async Task Does_not_publish_a_partial_fork_when_staging_fails()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var fileSystem = new ControlledFileSystem();
            var repository = new JsonlSessionRepo(fileSystem, root);
            var source = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "source", Cwd = root });
            await source.AppendMessageAsync(SessionTestHelpers.User("one"));
            await source.AppendMessageAsync(SessionTestHelpers.User("two", 2));
            var sourceMetadata = await source.GetMetadataAsync();
            fileSystem.FailNextAppend = true;

            var error = await Assert.ThrowsAsync<SessionError>(() => repository.ForkAsync(
                sourceMetadata,
                new ForkOptions(),
                new JsonlSessionCreateOptions { Id = "fork", Cwd = root }));
            Assert.Equal(SessionErrorCode.Storage, error.Code);
            Assert.Equal(["source"], (await repository.ListAsync()).Select(item => item.Id));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(sourceMetadata.Path)!, "*.tmp"));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "does not publish a fork when atomic rename fails")]
    public async Task Does_not_publish_a_fork_when_atomic_rename_fails()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var fileSystem = new ControlledFileSystem();
            var repository = new JsonlSessionRepo(fileSystem, root);
            var source = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "source", Cwd = root });
            await source.AppendMessageAsync(SessionTestHelpers.User("one"));
            var sourceMetadata = await source.GetMetadataAsync();
            fileSystem.FailNextRename = true;

            var error = await Assert.ThrowsAsync<SessionError>(() => repository.ForkAsync(
                sourceMetadata,
                new ForkOptions(),
                new JsonlSessionCreateOptions { Id = "fork", Cwd = root }));
            Assert.Equal(SessionErrorCode.Storage, error.Code);
            Assert.Equal(["source"], (await repository.ListAsync()).Select(item => item.Id));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(sourceMetadata.Path)!, "*.tmp"));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "repairs a valid final line missing its newline")]
    public async Task Repairs_a_valid_final_line_missing_its_newline()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "session", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            var firstId = await session.AppendCustomEntryAsync("first");
            var unterminated = (await File.ReadAllTextAsync(metadata.Path)).TrimEnd('\r', '\n');
            await File.WriteAllTextAsync(metadata.Path, unterminated);

            var reopened = await repository.OpenAsync(metadata);
            Assert.Equal(unterminated + "\n", await File.ReadAllTextAsync(metadata.Path));
            var secondId = await reopened.AppendCustomEntryAsync("second");
            var verified = await repository.OpenAsync(metadata);
            Assert.Equal([firstId, secondId], (await verified.FindEntriesAsync(new EntryQuery { Order = EntryOrder.OldestFirst })).Select(entry => entry.Id));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "fails to open when repairing a missing final newline fails")]
    public async Task Fails_to_open_when_repairing_a_missing_final_newline_fails()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "session", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            await session.AppendCustomEntryAsync("first");
            var unterminated = (await File.ReadAllTextAsync(metadata.Path)).TrimEnd('\r', '\n');
            await File.WriteAllTextAsync(metadata.Path, unterminated);
            var fileSystem = new ControlledFileSystem { FailNextAppend = true };

            var error = await Assert.ThrowsAsync<SessionError>(() => new JsonlSessionRepo(fileSystem, root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.Storage, error.Code);
            Assert.Equal(unterminated, await File.ReadAllTextAsync(metadata.Path));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "truncates a malformed final line")]
    public async Task Truncates_a_malformed_final_line()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "session", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            await session.AppendCustomEntryAsync("note", new JsonObject { ["value"] = "kept" });
            var validPrefix = await File.ReadAllTextAsync(metadata.Path);
            await File.AppendAllTextAsync(metadata.Path, "{\"kind\":\"entry\"");

            var reopened = await repository.OpenAsync(metadata);
            Assert.Single(await reopened.FindEntriesAsync());
            Assert.Equal(validPrefix, await File.ReadAllTextAsync(metadata.Path));
            var appendedId = await reopened.AppendCustomEntryAsync("after-recovery");
            Assert.Equal(2, (await reopened.GetEntryAsync(appendedId))!.Seq);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects a complete invalid final mutation without modifying the file")]
    public async Task Rejects_a_complete_invalid_final_mutation_without_modifying_the_file()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var metadata = WriteRawSession(root, "invalid-final-mutation", new JsonObject
            {
                ["kind"] = "unknown",
                ["seq"] = 1,
            });
            var corrupted = await File.ReadAllTextAsync(metadata.Path);
            var error = await Assert.ThrowsAsync<SessionError>(() => SessionTestHelpers.CreateRepository(root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Equal(corrupted, await File.ReadAllTextAsync(metadata.Path));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects a malformed middle line without modifying the file")]
    public async Task Rejects_a_malformed_middle_line_without_modifying_the_file()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "session", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            await session.AppendCustomEntryAsync("first");
            await session.AppendCustomEntryAsync("second");
            var lines = File.ReadAllLines(metadata.Path);
            var corrupted = string.Join('\n', [lines[0], lines[1], "not-json", lines[2]]) + "\n";
            await File.WriteAllTextAsync(metadata.Path, corrupted);

            var error = await Assert.ThrowsAsync<SessionError>(() => repository.OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Equal(corrupted, await File.ReadAllTextAsync(metadata.Path));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects an imported entry that references a missing parent")]
    public async Task Rejects_an_imported_entry_that_references_a_missing_parent()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var metadata = WriteRawSession(root, "missing-parent", new JsonObject
            {
                ["kind"] = "entry",
                ["type"] = "custom",
                ["id"] = "orphan",
                ["customType"] = "note",
                ["parentId"] = "missing",
                ["seq"] = 1,
                ["timestamp"] = 1,
            });
            var error = await Assert.ThrowsAsync<SessionError>(() => SessionTestHelpers.CreateRepository(root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Contains("references missing parent missing", error.Message);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects a lane-bound entry that does not chain to the lane leaf")]
    public async Task Rejects_a_lane_bound_entry_that_does_not_chain_to_the_lane_leaf()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "session", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            await session.AppendCustomEntryAsync("first");
            await session.AppendCustomEntryAsync("second");
            var lines = File.ReadAllLines(metadata.Path).Select(SessionTestHelpers.ParseObject).ToArray();
            lines[2]["parentId"] = null;
            await File.WriteAllTextAsync(metadata.Path, string.Join('\n', lines.Select(line => line.ToJsonString())) + "\n");

            var error = await Assert.ThrowsAsync<SessionError>(() => repository.OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Contains("does not chain to the lane leaf", error.Message);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "does not move a lane for an imported entry without lane metadata")]
    public async Task Does_not_move_a_lane_for_an_imported_entry_without_lane_metadata()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var metadata = WriteRawSession(root, "import", new JsonObject
            {
                ["kind"] = "entry",
                ["type"] = "custom",
                ["id"] = "imported",
                ["customType"] = "note",
                ["parentId"] = null,
                ["seq"] = 1,
                ["timestamp"] = 1,
            });
            var repository = SessionTestHelpers.CreateRepository(root);
            var imported = await repository.OpenAsync(metadata);
            Assert.Null(await imported.GetLeafIdAsync());
            Assert.Equal(["imported"], (await imported.FindEntriesAsync()).Select(entry => entry.Id));

            await File.AppendAllTextAsync(metadata.Path, new JsonObject
            {
                ["kind"] = "lane",
                ["seq"] = 2,
                ["lane"] = "main",
                ["leafId"] = "imported",
            }.ToJsonString() + "\n");
            var moved = await repository.OpenAsync(metadata);
            Assert.Equal("imported", await moved.GetLeafIdAsync());
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "rejects a non-consecutive sequence during replay")]
    public Task Rejects_a_non_consecutive_sequence_during_replay() =>
        AssertReplayRejectsAsync(
            "non-consecutive-sequence",
            "non-consecutive seq",
            new JsonObject
            {
                ["kind"] = "entry",
                ["type"] = "custom",
                ["id"] = "entry",
                ["customType"] = "note",
                ["parentId"] = null,
                ["seq"] = 2,
                ["timestamp"] = 1,
            });

    [Fact(DisplayName = "rejects a duplicate entry/record id during replay")]
    public Task Rejects_a_duplicate_entry_record_id_during_replay() =>
        AssertReplayRejectsAsync(
            "duplicate-id",
            "duplicate id",
            new JsonObject
            {
                ["kind"] = "entry",
                ["type"] = "custom",
                ["id"] = "duplicate",
                ["customType"] = "note",
                ["parentId"] = null,
                ["seq"] = 1,
                ["timestamp"] = 1,
            },
            RecordRun("duplicate", 2));

    [Fact(DisplayName = "rejects an entry with a missing parent during replay")]
    public Task Rejects_an_entry_with_a_missing_parent_during_replay() =>
        AssertReplayRejectsAsync(
            "missing-parent-replay",
            "missing parent",
            new JsonObject
            {
                ["kind"] = "entry",
                ["type"] = "custom",
                ["id"] = "entry",
                ["customType"] = "note",
                ["parentId"] = "missing",
                ["seq"] = 1,
                ["timestamp"] = 1,
            });

    [Fact(DisplayName = "rejects an entry referencing a missing lane during replay")]
    public Task Rejects_an_entry_referencing_a_missing_lane_during_replay() =>
        AssertReplayRejectsAsync(
            "missing-lane-entry",
            "missing lane",
            new JsonObject
            {
                ["kind"] = "entry",
                ["lane"] = "thread",
                ["type"] = "custom",
                ["id"] = "entry",
                ["customType"] = "note",
                ["parentId"] = null,
                ["seq"] = 1,
                ["timestamp"] = 1,
            });

    [Fact(DisplayName = "rejects a record referencing a missing lane during replay")]
    public Task Rejects_a_record_referencing_a_missing_lane_during_replay() =>
        AssertReplayRejectsAsync("missing-lane-record", "missing lane", RecordRun("run", 1, "thread"));

    [Fact(DisplayName = "rejects a lane move referencing a missing entry during replay")]
    public Task Rejects_a_lane_move_referencing_a_missing_entry_during_replay() =>
        AssertReplayRejectsAsync(
            "missing-lane-target",
            "missing lane target",
            new JsonObject { ["kind"] = "lane", ["lane"] = "thread", ["leafId"] = "missing", ["seq"] = 1 });

    [Fact(DisplayName = "rejects a label referencing a missing entry during replay")]
    public Task Rejects_a_label_referencing_a_missing_entry_during_replay() =>
        AssertReplayRejectsAsync(
            "missing-label-target",
            "missing label target",
            new JsonObject
            {
                ["kind"] = "fact",
                ["fact"] = "label",
                ["targetId"] = "missing",
                ["label"] = "checkpoint",
                ["seq"] = 1,
            });

    [Fact(DisplayName = "rejects a complete malformed interior mutation without modifying the file")]
    public async Task Rejects_a_complete_malformed_interior_mutation_without_modifying_the_file()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var metadata = WriteRawSession(root, "malformed-interior", new JsonObject
            {
                ["kind"] = "record",
                ["type"] = "operation_started",
                ["id"] = "run",
                ["lane"] = "main",
                ["seq"] = 1,
                ["timestamp"] = 1,
                ["sourceLeafId"] = null,
            }, new JsonObject { ["kind"] = "fact", ["fact"] = "name", ["name"] = "after", ["seq"] = 2 });
            var corrupted = await File.ReadAllTextAsync(metadata.Path);
            var error = await Assert.ThrowsAsync<SessionError>(() => SessionTestHelpers.CreateRepository(root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Equal(corrupted, await File.ReadAllTextAsync(metadata.Path));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "preserves the session when staging torn-tail repair fails")]
    public async Task Preserves_the_session_when_staging_torn_tail_repair_fails()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "repair-failure", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            await session.AppendCustomEntryAsync("kept");
            await File.AppendAllTextAsync(metadata.Path, "{\"kind\":\"entry\"");
            var original = await File.ReadAllTextAsync(metadata.Path);
            var fileSystem = new ControlledFileSystem { FailNextWrite = true };

            var error = await Assert.ThrowsAsync<SessionError>(() => new JsonlSessionRepo(fileSystem, root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.Storage, error.Code);
            Assert.Equal(original, await File.ReadAllTextAsync(metadata.Path));
            Assert.False(File.Exists(metadata.Path + ".tmp"));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    [Fact(DisplayName = "preserves the session when torn-tail repair cannot be published")]
    public async Task Preserves_the_session_when_torn_tail_repair_cannot_be_published()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "repair-rename-failure", Cwd = root });
            var metadata = await session.GetMetadataAsync();
            await session.AppendCustomEntryAsync("kept");
            await File.AppendAllTextAsync(metadata.Path, "{\"kind\":\"entry\"");
            var original = await File.ReadAllTextAsync(metadata.Path);
            var fileSystem = new ControlledFileSystem { FailNextRename = true };

            var error = await Assert.ThrowsAsync<SessionError>(() => new JsonlSessionRepo(fileSystem, root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.Storage, error.Code);
            Assert.Equal(original, await File.ReadAllTextAsync(metadata.Path));
            Assert.False(File.Exists(metadata.Path + ".tmp"));
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    private static JsonlSessionMetadata WriteRawSession(string root, string id, params JsonObject[] mutations)
    {
        var path = Path.Combine(root, id + ".jsonl");
        var header = new JsonObject
        {
            ["kind"] = "header",
            ["version"] = 4,
            ["id"] = id,
            ["createdAt"] = 1,
            ["cwd"] = root,
        };
        File.WriteAllText(path, string.Join('\n', new[] { header }.Concat(mutations).Select(line => line.ToJsonString())) + "\n");
        var modifiedAt = (File.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).TotalMilliseconds;
        return new JsonlSessionMetadata
        {
            Id = id,
            CreatedAt = 1,
            Cwd = root,
            Path = path,
            ModifiedAt = modifiedAt,
            SourceFormat = 4,
        };
    }

    private static JsonObject RecordRun(string id, long seq, string lane = "main") => new()
    {
        ["kind"] = "record",
        ["type"] = "operation_started",
        ["id"] = id,
        ["lane"] = lane,
        ["seq"] = seq,
        ["timestamp"] = seq,
        ["sourceLeafId"] = null,
        ["intent"] = new JsonObject { ["kind"] = "run", ["originalPrompt"] = new JsonArray(), ["initialMessages"] = new JsonArray() },
    };

    private static async Task AssertReplayRejectsAsync(string id, string expectedMessage, params JsonObject[] mutations)
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var metadata = WriteRawSession(root, id, mutations);
            var error = await Assert.ThrowsAsync<SessionError>(() => SessionTestHelpers.CreateRepository(root).OpenAsync(metadata));
            Assert.Equal(SessionErrorCode.InvalidEntry, error.Code);
            Assert.Contains(expectedMessage, error.Message);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class ControlledFileSystem : IJsonlFileSystem
    {
        private readonly LocalJsonlFileSystem _inner = new();

        public bool FailNextWrite { get; set; }
        public bool FailNextRename { get; set; }
        public bool FailNextAppend { get; set; }

        public string AbsolutePath(string path) => _inner.AbsolutePath(path);
        public string JoinPath(params string[] paths) => _inner.JoinPath(paths);
        public Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadTextFileAsync(path, cancellationToken);
        public Task<IReadOnlyList<string>> ReadTextLinesAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadTextLinesAsync(path, cancellationToken);

        public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("injected write failure");
            }

            return _inner.WriteFileAsync(path, content, cancellationToken);
        }

        public Task AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("injected append failure");
            }

            return _inner.AppendFileAsync(path, content, cancellationToken);
        }

        public Task RenameFileAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            if (FailNextRename)
            {
                FailNextRename = false;
                throw new IOException("injected rename failure");
            }

            return _inner.RenameFileAsync(source, destination, cancellationToken);
        }

        public Task<JsonlFileInfo> FileInfoAsync(string path, CancellationToken cancellationToken = default) => _inner.FileInfoAsync(path, cancellationToken);
        public Task<IReadOnlyList<JsonlDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default) => _inner.ListDirectoryAsync(path, cancellationToken);
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => _inner.ExistsAsync(path, cancellationToken);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => _inner.CreateDirectoryAsync(path, cancellationToken);
        public Task RemoveAsync(string path, bool force = false, CancellationToken cancellationToken = default) => _inner.RemoveAsync(path, force, cancellationToken);
    }
}
