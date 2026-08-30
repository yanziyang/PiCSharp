using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;

using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

#pragma warning disable xUnit1051 // Session APIs expose cancellation; the focused tests use explicit cancellation where it is under test.

public sealed class SearchTests
{
    [Fact(DisplayName = "scans an arbitrary in-memory projected source")]
    public async Task Scans_an_arbitrary_in_memory_projected_source()
    {
        await using var rootStorage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "root", CreatedAt = 1 });
        await using var otherStorage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "other", CreatedAt = 2 });
        var root = new Session<SessionMetadata>(rootStorage);
        var other = new Session<SessionMetadata>(otherStorage);
        await root.AppendMessageAsync(SessionTestHelpers.User("fix auth flow"));
        await other.AppendMessageAsync(SessionTestHelpers.User("auth in another workspace"));

        var search = new ScanningSessionSearch(
        [
            new SessionSearchReadable<SessionMetadata>(root),
            new SessionSearchReadable<SessionMetadata>(other),
        ]);

        var hits = await CollectAsync(search.SearchAsync("auth"));

        Assert.Equal(["root", "other"], hits.Select(hit => hit.SessionId));
        Assert.Empty(await CollectAsync(search.SearchAsync("missing")));
    }

    [Fact(DisplayName = "includes labels in memory scanning projections")]
    public async Task Includes_labels_in_memory_scanning_projections()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var entryId = await session.AppendMessageAsync(SessionTestHelpers.User("plain body"));
        await session.SetLabelAsync(entryId, "important label");

        var search = new ScanningSessionSearch([new SessionSearchReadable<SessionMetadata>(session)]);
        var hits = await CollectAsync(search.SearchAsync("important"));

        var hit = Assert.Single(hits);
        Assert.Equal("session", hit.SessionId);
        Assert.Equal(entryId, hit.EntryId);
    }

    [Fact(DisplayName = "honors entry type filters and abort signals in scanning search")]
    public async Task Honors_entry_type_filters_and_abort_signals_in_scanning_search()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var messageEntryId = await session.AppendMessageAsync(SessionTestHelpers.User("auth message"));
        await session.AppendCustomEntryAsync("note", SessionTestHelpers.Object(("text", System.Text.Json.Nodes.JsonValue.Create("auth custom"))));

        var search = new ScanningSessionSearch([new SessionSearchReadable<SessionMetadata>(session)]);
        var messageHits = await CollectAsync(
            search.SearchAsync("auth", new SessionSearchOptions { EntryTypes = ["message"] }));

        var messageHit = Assert.Single(messageHits);
        Assert.Equal(messageEntryId, messageHit.EntryId);

        using var controller = new CancellationTokenSource();
        controller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(search.SearchAsync("auth", new SessionSearchOptions { CancellationToken = controller.Token })));
    }

    [Fact(DisplayName = "scans JSONL sessions from disk through the JSONL scanning source")]
    public async Task Scans_jsonl_sessions_from_disk_through_the_JSONL_scanning_source()
    {
        var root = SessionTestHelpers.CreateTempRoot();
        try
        {
            var repository = SessionTestHelpers.CreateRepository(root);
            var workspace = Path.Combine(root, "workspace");
            var otherWorkspace = Path.Combine(root, "other");
            var session = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "jsonl", Cwd = workspace });
            var entryId = await session.AppendMessageAsync(SessionTestHelpers.User("jsonl backed auth entry"));
            await session.SetLabelAsync(entryId, "disk label");
            var other = await repository.CreateAsync(new JsonlSessionCreateOptions { Id = "other", Cwd = otherWorkspace });
            var otherEntryId = await other.AppendMessageAsync(SessionTestHelpers.User("jsonl backed auth entry in another cwd"));

            var listed = await repository.ListAsync();
            var reopened = new List<ISessionSearchReadable>();
            foreach (var metadata in listed)
            {
                reopened.Add(new SessionSearchReadable<JsonlSessionMetadata>(await repository.OpenAsync(metadata)));
            }

            var search = new ScanningSessionSearch(reopened);
            var authHits = await CollectAsync(search.SearchAsync("auth"));

            Assert.Equal(2, authHits.Count);
            Assert.Contains(authHits, hit => hit.SessionId == "jsonl" && hit.EntryId == entryId);
            Assert.Contains(authHits, hit => hit.SessionId == "other" && hit.EntryId == otherEntryId);
            var diskHit = Assert.Single(await CollectAsync(search.SearchAsync("disk")));
            Assert.Equal(entryId, diskHit.EntryId);
        }
        finally
        {
            SessionTestHelpers.DeleteTempRoot(root);
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
