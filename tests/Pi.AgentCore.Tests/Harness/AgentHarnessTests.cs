using System.Diagnostics.CodeAnalysis;

using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Compaction;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;

using Xunit;

namespace Pi.AgentCore.Tests.Harness;

[SuppressMessage("Usage", "xUnit1051", Justification = "Harness scaffold tests intentionally exercise default-token overloads.")]
public sealed class AgentHarnessTests
{
    [Fact(DisplayName = "opens only record-free sessions before restore is implemented")]
    public async Task Opens_only_record_free_sessions_before_restore_is_implemented()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var created = await AgentHarness.CreateAsync(CreateOptions(session));

        Assert.Empty(created.Suspended);
        Assert.Equal("main", created.Harness.Name);
        Assert.Same(session, created.Harness.Session);
        Assert.Null(await created.Harness.GetLeafIdAsync());
        Assert.Null(await created.Harness.Session.GetLeafIdAsync());

        await created.Harness.CloseAsync();

        await using var recordedStorage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "recorded", CreatedAt = 1 });
        var recorded = new Session<SessionMetadata>(recordedStorage);
        await recorded.AppendRecordAsync(new OperationStartedRecord
        {
            Id = "run",
            Lane = "main",
            SourceLeafId = null,
            Intent = new RunOperationIntent
            {
                OriginalPrompt = [],
                InitialMessages = [],
            },
        });

        var error = await Assert.ThrowsAsync<HarnessNotImplemented>(() => AgentHarness.CreateAsync(CreateOptions(recorded)));
        Assert.Equal("create.restore", error.Operation);
    }

    [Fact(DisplayName = "keeps scaffold-safe configuration as defensive copies")]
    public async Task Keeps_scaffold_safe_configuration_as_defensive_copies()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var harness = (await AgentHarness.CreateAsync(CreateOptions(session))).Harness;

        var model = AgentTestFixtures.TestModel() with { Id = "replacement-model" };
        await harness.SetModelAsync(model);
        Assert.Same(model, await harness.GetModelAsync());

        await harness.SetThinkingLevelAsync(ThinkingLevels.High);
        Assert.Equal(ThinkingLevels.High, await harness.GetThinkingLevelAsync());

        var activeTools = new List<string> { "one" };
        await harness.SetActiveToolsAsync(activeTools);
        activeTools.Add("mutated");
        Assert.Equal(["one"], await harness.GetActiveToolsAsync());
        var readActiveTools = Assert.IsType<string[]>(await harness.GetActiveToolsAsync());
        readActiveTools[0] = "mutated";
        Assert.Equal(["one"], await harness.GetActiveToolsAsync());

        var tool = new HarnessTool { Name = "tool", Label = "Tool" };
        var tools = new List<HarnessTool> { tool };
        await harness.SetToolsAsync(tools);
        tools.Add(new HarnessTool { Name = "mutated", Label = "Mutated" });
        Assert.Equal(["tool"], (await harness.GetToolsAsync()).Select(static item => item.Name));
        var readTools = Assert.IsType<HarnessTool[]>(await harness.GetToolsAsync());
        readTools[0] = new HarnessTool { Name = "mutated", Label = "Mutated" };
        Assert.Equal(["tool"], (await harness.GetToolsAsync()).Select(static item => item.Name));

        var sourceSkills = new List<Skill>
        {
            new Skill
            {
                Name = "skill",
                Description = "desc",
                Content = "body",
                FilePath = "/tmp/SKILL.md",
            },
        };
        var resources = new AgentHarnessResources
        {
            Skills = sourceSkills,
            PromptTemplates = [new PromptTemplate { Name = "template", Content = "body" }],
        };
        await harness.SetResourcesAsync(resources);
        sourceSkills.Add(new Skill
        {
            Name = "mutated",
            Description = "desc",
            Content = "body",
            FilePath = "/tmp/OTHER.md",
        });
        Assert.Equal(["skill"], (await harness.GetResourcesAsync()).Skills.Select(static skill => skill.Name));
        var readResources = await harness.GetResourcesAsync();
        var readResourceSkills = Assert.IsType<Skill[]>(readResources.Skills);
        readResourceSkills[0] = new Skill
        {
            Name = "mutated",
            Description = "desc",
            Content = "body",
            FilePath = "/tmp/OTHER.md",
        };
        Assert.Equal(["skill"], (await harness.GetResourcesAsync()).Skills.Select(static skill => skill.Name));

        var streamOptions = new SimpleStreamOptions { MaxTokens = 10, Reasoning = ThinkingLevels.Low };
        await harness.SetStreamOptionsAsync(streamOptions);
        var readStreamOptions = await harness.GetStreamOptionsAsync();
        Assert.NotSame(streamOptions, readStreamOptions);
        Assert.Equal(10, readStreamOptions.MaxTokens);
        Assert.Equal(ThinkingLevels.Low, readStreamOptions.Reasoning);

        var retryPolicy = new RetryPolicy { Enabled = true, MaxRetries = 2, BaseDelayMs = 10 };
        await harness.SetRetryPolicyAsync(retryPolicy);
        var readRetryPolicy = await harness.GetRetryPolicyAsync();
        Assert.NotSame(retryPolicy, readRetryPolicy);
        Assert.Equal(retryPolicy, readRetryPolicy);

        var compactionSettings = new CompactionSettings { Enabled = false, ReserveTokens = 1, KeepRecentTokens = 2 };
        await harness.SetCompactionSettingsAsync(compactionSettings);
        var readCompactionSettings = await harness.GetCompactionSettingsAsync();
        Assert.NotSame(compactionSettings, readCompactionSettings);
        Assert.Equal(compactionSettings, readCompactionSettings);

        await harness.SetSteeringModeAsync(QueueMode.All);
        Assert.Equal(QueueMode.All, await harness.GetSteeringModeAsync());
        await harness.SetFollowUpModeAsync(QueueMode.All);
        Assert.Equal(QueueMode.All, await harness.GetFollowUpModeAsync());
    }

    [Fact(DisplayName = "rejects every unfinished public operation explicitly")]
    public async Task Rejects_every_unfinished_public_operation_explicitly()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var harness = (await AgentHarness.CreateAsync(CreateOptions(session))).Harness;
        var userMessage = HarnessTestHelpers.User("hello");
        var usage = HarnessTestHelpers.Usage(1, 2);
        var callbackCalled = false;

        await AssertNotImplemented("prompt", () => harness.PromptAsync("hello"));
        await AssertNotImplemented("skill", () => harness.SkillAsync("skill"));
        await AssertNotImplemented("promptFromTemplate", () => harness.PromptFromTemplateAsync("template"));
        await AssertNotImplemented("compact", () => harness.CompactAsync());
        await AssertNotImplemented("navigateTree", () => harness.NavigateTreeAsync(null));
        await AssertNotImplemented("resume", () => harness.ResumeAsync());
        await AssertNotImplemented("abort", () => harness.AbortAsync());
        await AssertNotImplemented("steer", () => harness.SteerAsync(userMessage));
        await AssertNotImplemented("followUp", () => harness.FollowUpAsync(userMessage));
        await AssertNotImplemented("nextRun", () => harness.NextRunAsync(userMessage));
        await AssertNotImplemented("cancelQueued", () => harness.CancelQueuedAsync("queued"));
        await AssertNotImplemented("recordUsage", () => harness.RecordUsageAsync(usage));
        await AssertNotImplemented("waitForIdle", () => harness.WaitForIdleAsync());
        await AssertNotImplemented(
            "runWhenIdle",
            () => harness.RunWhenIdleAsync(() =>
            {
                callbackCalled = true;
                return Task.CompletedTask;
            }));
        await AssertNotImplemented("peekAction", () => harness.PeekActionAsync());
        await AssertNotImplemented("executeAction", () => harness.ExecuteActionAsync());
        await AssertNotImplemented("runToCompletion", () => harness.RunToCompletionAsync());
        await AssertNotImplemented("watch", () => harness.WatchAsync());
        await AssertNotImplemented("lane", () => harness.LaneAsync("main"));
        await AssertNotImplemented("createLane", () => harness.CreateLaneAsync("thread", null));
        await AssertNotImplemented("lanes", () => harness.LanesAsync());
        await AssertNotImplemented("watchSession", () => harness.WatchSessionAsync());

        Assert.False(callbackCalled);
        Assert.Throws<HarnessNotImplemented>(() => harness.Hooks.On("before_run", () => { }));
        Assert.Throws<HarnessNotImplemented>(() => harness.Events.On("event", _ => { }));
    }

    [Fact(DisplayName = "reports HarnessClosed for unfinished operations after close")]
    public async Task Reports_harness_closed_for_unfinished_operations_after_close()
    {
        await using var storage = new InMemorySessionStorage<SessionMetadata>(
            new SessionMetadata { Id = "session", CreatedAt = 1 });
        var session = new Session<SessionMetadata>(storage);
        var harness = (await AgentHarness.CreateAsync(CreateOptions(session))).Harness;
        await harness.CloseAsync();

        await Assert.ThrowsAsync<HarnessClosed>(() => harness.PromptAsync("hello"));
        await Assert.ThrowsAsync<HarnessClosed>(() => harness.WaitForIdleAsync());
        Assert.Throws<HarnessClosed>(() => harness.Hooks.On("before_run", () => { }));
        Assert.Throws<HarnessClosed>(() => harness.Events.On("event", _ => { }));
    }

    private static AgentHarnessOptions CreateOptions(Session<SessionMetadata> session) => new()
    {
        Session = session,
        Models = ModelsFactory.CreateModels(),
        Model = AgentTestFixtures.TestModel(),
    };

    private static async Task AssertNotImplemented(string operation, Func<Task> invoke)
    {
        var error = await Assert.ThrowsAsync<HarnessNotImplemented>(invoke);
        Assert.Equal(operation, error.Operation);
    }
}
