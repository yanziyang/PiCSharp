using Pi.AgentCore.Harness.Compaction;
using Pi.AgentCore.Harness.Session;

using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class BranchSummarizationTests
{
    [Fact(DisplayName = "collects the abandoned side of a branch in chronological order")]
    public async Task Collects_the_abandoned_side_of_a_branch_in_chronological_order()
    {
        var nextId = 0;
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(
            storage,
            new DelegateIdGenerator(() => $"entry-{++nextId}"));
        var cancellationToken = TestContext.Current.CancellationToken;

        var rootId = await session.AppendMessageAsync(HarnessTestHelpers.User("root"), cancellationToken);
        var commonId = await session.AppendMessageAsync(HarnessTestHelpers.User("common"), cancellationToken);
        var abandonedIds = new[]
        {
            await session.AppendMessageAsync(HarnessTestHelpers.User("abandoned 1"), cancellationToken),
            await session.AppendMessageAsync(HarnessTestHelpers.User("abandoned 2"), cancellationToken),
        };
        await session.CreateLaneAsync("target", commonId, cancellationToken);
        var targetId = await session.View("target").AppendMessageAsync(
            HarnessTestHelpers.User("target"),
            cancellationToken);

        var result = await BranchSummarization.CollectEntriesForBranchSummaryAsync(
            session,
            abandonedIds[1],
            targetId,
            cancellationToken);

        Assert.Equal(commonId, result.CommonAncestorId);
        Assert.Equal(abandonedIds, result.Entries.Select(entry => entry.Id));
        Assert.DoesNotContain(result.Entries, entry => entry.Id == rootId);
    }

    [Fact(DisplayName = "returns no entries when there was no previous leaf")]
    public async Task Returns_no_entries_when_there_was_no_previous_leaf()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var cancellationToken = TestContext.Current.CancellationToken;
        var targetId = await session.AppendMessageAsync(HarnessTestHelpers.User("target"), cancellationToken);

        var result = await BranchSummarization.CollectEntriesForBranchSummaryAsync(
            session,
            null,
            targetId,
            cancellationToken);

        Assert.Empty(result.Entries);
        Assert.Null(result.CommonAncestorId);
    }

    private sealed class DelegateIdGenerator(Func<string> next) : IIdGenerator
    {
        public string Next() => next();
    }
}
