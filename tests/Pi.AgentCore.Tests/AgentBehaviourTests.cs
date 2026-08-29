using Pi.Ai;

using Xunit;

using static Pi.AgentCore.Tests.AgentTestFixtures;

namespace Pi.AgentCore.Tests;

/// <summary>
/// Ported from <c>reference/pi/packages/agent/test/agent.test.ts</c>. Case names follow
/// upstream so a failure maps back to the upstream expectation.
/// </summary>
public sealed class AgentBehaviourTests
{
    // ---------------------------------------------------------------- construction

    [Fact]
    public void Should_create_an_agent_instance_with_default_state()
    {
        // Upstream passes `streamFn: unusedStreamFunction`; its constructor resolves
        // `streamFn ?? getDefaultStreamFn()` and throws when neither is available.
        var agent = NewAgent(UnusedStreamFunction);

        Assert.Equal(string.Empty, agent.State.SystemPrompt);
        Assert.Equal(ThinkingLevels.Off, agent.State.ThinkingLevel);
        Assert.False(agent.State.IsStreaming);
        Assert.Empty(agent.State.Messages);
        Assert.Empty(agent.State.Tools);
        Assert.Null(agent.State.StreamingMessage);
        Assert.Empty(agent.State.PendingToolCalls);
        Assert.Null(agent.State.ErrorMessage);
    }

    [Fact]
    public void Should_create_an_agent_instance_with_custom_initial_state()
    {
        var agent = new Agent(new AgentOptions
        {
            StreamFunction = UnusedStreamFunction,
            InitialState = new AgentState
            {
                SystemPrompt = "You are a helpful assistant.",
                Model = TestModel(),
                ThinkingLevel = ThinkingLevels.Low,
            },
        });

        Assert.Equal("You are a helpful assistant.", agent.State.SystemPrompt);
        Assert.Equal("test-model", agent.State.Model.Id);
        Assert.Equal(ThinkingLevels.Low, agent.State.ThinkingLevel);
    }

    // ---------------------------------------------------------------- subscribers

    [Fact]
    public async Task Should_subscribe_to_events()
    {
        var events = new List<AgentEvent>();
        var agent = NewAgent(Replay(Assistant("ok")));
        using var subscription = agent.Subscribe(events.Add);

        await agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(events, static e => e is AgentStartEvent);
        Assert.Contains(events, static e => e is AgentEndEvent);
    }

    [Fact]
    public async Task Should_await_async_subscribers_before_prompt_resolves()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerFinished = false;
        var agent = NewAgent(Replay(Assistant("ok")));

        using var subscription = agent.Subscribe(async (@event, _) =>
        {
            if (@event is AgentEndEvent)
            {
                await barrier.Task.ConfigureAwait(false);
                listenerFinished = true;
            }
        });

        var prompt = agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // The run must not be considered complete while a listener is still awaiting.
        Assert.False(prompt.IsCompleted);
        Assert.False(listenerFinished);

        barrier.SetResult();
        await prompt;

        Assert.True(listenerFinished);
        Assert.False(agent.State.IsStreaming);
    }

    [Fact]
    public async Task WaitForIdle_should_wait_for_async_subscribers()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerFinished = false;
        var agent = NewAgent(Replay(Assistant("ok")));

        using var subscription = agent.Subscribe(async (@event, _) =>
        {
            if (@event is AgentEndEvent)
            {
                await barrier.Task.ConfigureAwait(false);
                listenerFinished = true;
            }
        });

        var prompt = agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var idle = agent.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);

        barrier.SetResult();
        await prompt;
        await idle;

        Assert.True(listenerFinished);
    }

    [Fact]
    public async Task Should_pass_the_active_abort_signal_to_subscribers()
    {
        CancellationToken? seen = null;
        var agent = NewAgent(Replay(Assistant("ok")));

        using var subscription = agent.Subscribe((@event, _) =>
        {
            if (@event is AgentStartEvent)
            {
                seen = agent.Signal;
            }

            return ValueTask.CompletedTask;
        });

        await agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.False(seen!.Value.IsCancellationRequested);
    }

    // ---------------------------------------------------------------- state

    [Fact]
    public async Task Should_update_state_with_mutators()
    {
        var agent = NewAgent(Replay(Assistant("ok")));
        agent.State.SystemPrompt = "mutated";
        agent.State.ThinkingLevel = ThinkingLevels.Off;

        await agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("mutated", agent.State.SystemPrompt);
        Assert.Equal(["user", "assistant"], agent.State.Messages.Select(static m => m.Role));
    }

    // ---------------------------------------------------------------- queues

    [Fact]
    public void Should_support_steering_message_queue()
    {
        var agent = NewAgent(UnusedStreamFunction);
        Assert.False(agent.HasQueuedMessages());

        agent.Steer(UserMessage.Text("steer", 1));
        Assert.True(agent.HasQueuedMessages());

        agent.ClearSteeringQueue();
        Assert.False(agent.HasQueuedMessages());
    }

    [Fact]
    public void Should_support_follow_up_message_queue()
    {
        var agent = NewAgent(UnusedStreamFunction);
        agent.FollowUp(UserMessage.Text("follow", 1));
        Assert.True(agent.HasQueuedMessages());

        agent.ClearAllQueues();
        Assert.False(agent.HasQueuedMessages());
    }

    [Fact]
    public async Task Continue_should_process_queued_follow_up_messages_after_an_assistant_turn()
    {
        var turns = 0;
        var agent = NewAgent(new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(Assistant($"turn {turns}"));
        }));

        await agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, turns);

        agent.FollowUp(UserMessage.Text("more", 2));
        await agent.ContinueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, turns);
        Assert.Contains(agent.State.Messages, static m => UserText(m) == "more");
    }

    // ---------------------------------------------------------------- guards

    [Fact]
    public async Task Should_throw_when_prompt_called_while_streaming()
    {
        var (agent, started, release) = HeldAgent();
        var prompt = agent.PromptAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await started.Task;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.PromptAsync("second", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("already processing", error.Message, StringComparison.Ordinal);

        release.SetResult();
        await prompt;
    }

    [Fact]
    public async Task Should_throw_when_continue_called_while_streaming()
    {
        var (agent, started, release) = HeldAgent();
        var prompt = agent.PromptAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await started.Task;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ContinueAsync(TestContext.Current.CancellationToken));
        Assert.Contains("already processing", error.Message, StringComparison.Ordinal);

        release.SetResult();
        await prompt;
    }

    [Fact]
    public async Task Should_reject_reset_while_processing_without_corrupting_the_transcript()
    {
        var (agent, started, release) = HeldAgent();
        var prompt = agent.PromptAsync("Hello", cancellationToken: TestContext.Current.CancellationToken);
        await started.Task;

        try
        {
            Assert.True(agent.State.IsStreaming);
            Assert.Equal(["user"], agent.State.Messages.Select(static m => m.Role));

            var error = Assert.Throws<InvalidOperationException>(agent.Reset);
            Assert.Equal(
                "Agent is already processing. Wait for completion before resetting.",
                error.Message);

            // The rejected reset must leave both the streaming flag and the transcript intact.
            Assert.True(agent.State.IsStreaming);
            Assert.Equal(["user"], agent.State.Messages.Select(static m => m.Role));
        }
        finally
        {
            release.SetResult();
            await prompt;
        }

        Assert.False(agent.State.IsStreaming);
        Assert.Equal(["user", "assistant"], agent.State.Messages.Select(static m => m.Role));
    }

    [Fact]
    public void Should_handle_abort_controller()
    {
        var agent = NewAgent(UnusedStreamFunction);

        // Upstream asserts only that aborting with nothing running does not throw.
        // Abort during a live run is covered by AgentLoopTests.
        agent.Abort();
        Assert.False(agent.State.IsStreaming);
    }

    // ---------------------------------------------------------------- forwarding

    [Fact]
    public async Task Should_forward_sessionId_to_streamFunction_options()
    {
        string? seen = null;
        var agent = new Agent(new AgentOptions
        {
            SessionId = "session-7",
            StreamFunction = (_, _, options) =>
            {
                seen = options?.SessionId;
                return Completed(Assistant("ok"));
            },
            InitialState = new AgentState { Model = TestModel() },
        });

        await agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("session-7", seen);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Mirrors upstream's <c>unusedStreamFunction</c>: present, never invoked.</summary>
    private static AgentStreamFunction UnusedStreamFunction =>
        static (_, _, _) => throw new InvalidOperationException("Unexpected stream call");

    private static Agent NewAgent(AgentStreamFunction streamFunction) => new(new AgentOptions
    {
        StreamFunction = streamFunction,
        InitialState = new AgentState { Model = TestModel() },
    });

    /// <summary>
    /// An agent whose single response is held open until the returned release source is
    /// completed, so the test can observe mid-run state. Mirrors upstream's
    /// <c>streamStarted</c> / <c>releaseResponse</c> deferred pair.
    /// </summary>
    private static (Agent Agent, TaskCompletionSource Started, TaskCompletionSource Release) HeldAgent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var agent = NewAgent((_, _, _) =>
        {
            var stream = new AssistantMessageEventStream();
            _ = Task.Run(async () =>
            {
                stream.Push(new StreamStartEvent(Assistant(string.Empty)));
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                var done = Assistant("Done");
                stream.Push(new StreamDoneEvent(done.StopReason, done));
                stream.End(done);
            });
            return stream;
        });

        return (agent, started, release);
    }
}
