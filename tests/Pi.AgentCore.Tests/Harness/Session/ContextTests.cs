using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

public sealed class ContextTests
{
    [Fact(DisplayName = "starts at the latest compaction and materializes its retained tail")]
    public void Starts_at_the_latest_compaction_and_materializes_its_retained_tail()
    {
        var entries = new Entry[]
        {
            Entry(new MessageEntry { Id = "old", ParentId = null, Message = new AgentMessage(SessionTestHelpers.User("old")) }, 1),
            Entry(
                new CompactionEntry
                {
                    Id = "compact",
                    ParentId = "old",
                    Summary = "summary",
                    RetainedTail =
                    [
                        new AgentMessage(SessionTestHelpers.User("retained")),
                        new AgentMessage(SessionTestHelpers.Assistant("answer")),
                    ],
                    TokensBefore = 100,
                },
                2),
            Entry(new ModelChangeEntry { Id = "model", ParentId = "compact", Provider = "openai", ModelId = "gpt-5" }, 3),
            Entry(new ThinkingLevelEntry { Id = "thinking", ParentId = "model", ThinkingLevel = "high" }, 4),
            Entry(new MessageEntry { Id = "tail", ParentId = "thinking", Message = new AgentMessage(SessionTestHelpers.User("tail")) }, 5),
        };

        var context = SessionContextBuilder.BuildSessionContext(entries);

        Assert.Equal(["compactionSummary", "user", "assistant", "user"], context.Messages.Select(message => message.Role));
        Assert.Equal(new SessionModel { Provider = "openai", ModelId = "gpt-5" }, context.Model);
        Assert.Equal("high", context.ThinkingLevel);
    }

    [Fact(DisplayName = "applies caller transforms after the compaction boundary")]
    public void Applies_caller_transforms_after_the_compaction_boundary()
    {
        var entries = new Entry[]
        {
            Entry(new MessageEntry { Id = "old", ParentId = null, Message = new AgentMessage(SessionTestHelpers.User("old")) }, 1),
            Entry(
                new CompactionEntry
                {
                    Id = "compact",
                    ParentId = "old",
                    Summary = "summary",
                    RetainedTail = [],
                    TokensBefore = 100,
                },
                2),
            Entry(
                new BranchSummaryEntry
                {
                    Id = "branch",
                    ParentId = "compact",
                    FromId = "abandoned",
                    Summary = "branch summary",
                },
                3),
            Entry(new MessageEntry { Id = "tail", ParentId = "branch", Message = new AgentMessage(SessionTestHelpers.User("tail")) }, 4),
        };

        var context = SessionContextBuilder.BuildSessionContext(
            entries,
            new SessionContextOptions
            {
                EntryTransforms =
                [
                    candidates => candidates.Where(candidate => candidate is not CompactionEntry).ToArray(),
                ],
            });

        Assert.Equal(["branchSummary", "user"], context.Messages.Select(message => message.Role));
    }

    [Fact(DisplayName = "projects custom entries and omits deferred assistant handles")]
    public void Projects_custom_entries_and_omits_deferred_assistant_handles()
    {
        var deferred = SessionTestHelpers.Assistant("", stopReason: "deferred") with
        {
            Content = [],
            Deferred = new DeferredHandle
            {
                Provider = "openai",
                ModelId = "gpt-5",
                Api = "openai-responses",
                Id = "response-1",
            },
        };
        var entries = new Entry[]
        {
            Entry(new MessageEntry { Id = "user", ParentId = null, Message = new AgentMessage(SessionTestHelpers.User("hello")) }, 1),
            Entry(new MessageEntry { Id = "deferred", ParentId = "user", Message = new AgentMessage(deferred) }, 2),
            Entry(
                new CustomEntry
                {
                    Id = "custom",
                    ParentId = "deferred",
                    CustomType = "note",
                    Data = JsonValue.Create("project me"),
                    DataPresent = true,
                },
                3),
        };

        var context = SessionContextBuilder.BuildSessionContext(
            entries,
            new SessionContextOptions
            {
                EntryProjectors = new Dictionary<string, SessionEntryProjector>
                {
                    ["note"] = custom =>
                    [
                        new AgentMessage(SessionTestHelpers.User($"note: {custom.Data?.GetValue<string>()}")),
                    ],
                },
            });

        Assert.Equal(["user", "user"], context.Messages.Select(message => message.Role));
        Assert.Equal("note: project me", context.Messages[1].Value["content"]![0]!["text"]!.GetValue<string>());
    }

    private static T Entry<T>(T entry, long seq)
        where T : Entry => entry with { Seq = seq, Timestamp = seq };
}
