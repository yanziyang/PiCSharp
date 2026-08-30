using Pi.AgentCore.Harness.Session;

using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

#pragma warning disable xUnit1051 // Session APIs expose cancellation; this deterministic test has no cancellation branch.

public sealed class MemoryTests
{
    [Fact(DisplayName = "uses one injectable id generator across lane views")]
    public async Task Uses_one_injectable_id_generator_across_lane_views()
    {
        var next = 0;
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(
            storage,
            new DelegateIdGenerator(() => "generated-" + ++next));

        var mainId = await session.AppendCustomEntryAsync("note");
        await session.CreateLaneAsync("thread", mainId);
        var threadId = await session.View("thread").AppendCustomEntryAsync("note");

        Assert.Equal("generated-1", mainId);
        Assert.Equal("generated-2", threadId);
    }

    private sealed class DelegateIdGenerator(Func<string> next) : IIdGenerator
    {
        public string Next() => next();
    }
}
