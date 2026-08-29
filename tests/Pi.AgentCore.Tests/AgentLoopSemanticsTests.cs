using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

using static Pi.AgentCore.Tests.AgentTestFixtures;

namespace Pi.AgentCore.Tests;

/// <summary>
/// Ported from <c>reference/pi/packages/agent/test/agent-loop.test.ts</c>. Case names follow
/// upstream so a failure maps back to the upstream expectation.
/// </summary>
public sealed class AgentLoopSemanticsTests
{
    // ---------------------------------------------------------------- execution mode

    [Fact]
    public async Task Should_force_sequential_execution_when_a_tool_has_executionMode_sequential()
    {
        var firstResolved = false;
        var parallelObserved = false;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = Tool("slow", async (_, arguments, _, _) =>
        {
            var value = arguments["value"]!.GetValue<string>();
            if (value == "first")
            {
                await releaseFirst.Task.ConfigureAwait(false);
                firstResolved = true;
            }

            if (value == "second" && !firstResolved)
            {
                parallelObserved = true;
            }

            return new AgentToolResult { Content = [new TextContent($"slow: {value}")] };
        }, ToolExecutionMode.Sequential);

        var events = new List<AgentEvent>();
        var stream = Replay(Assistant(
            string.Empty,
            StopReasons.ToolUse,
            Call("tool-1", "slow", "first"),
            Call("tool-2", "slow", "second")));

        // Config is parallel by default; the tool must still force sequential scheduling.
        var run = AgentLoop.RunAsync(
            [UserMessage.Text("run both", 1)],
            new AgentContext { Tools = [slow] },
            Config(),
            stream,
            Collect(events),
            TestContext.Current.CancellationToken);

        releaseFirst.SetResult();
        await run;

        Assert.False(parallelObserved);
        Assert.Equal(["tool-1", "tool-2"], ToolResultIds(events));
    }

    [Fact]
    public async Task Should_force_sequential_execution_when_one_of_multiple_tools_is_sequential()
    {
        var order = new List<string>();
        var gate = new object();
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = Tool("slow", async (_, _, _, _) =>
        {
            await releaseSlow.Task.ConfigureAwait(false);
            lock (gate) { order.Add("slow"); }
            return new AgentToolResult { Content = [new TextContent("slow")] };
        }, ToolExecutionMode.Sequential);

        var fast = Tool("fast", (_, _, _, _) =>
        {
            lock (gate) { order.Add("fast"); }
            return Task.FromResult(new AgentToolResult { Content = [new TextContent("fast")] });
        });

        var events = new List<AgentEvent>();
        var run = AgentLoop.RunAsync(
            [UserMessage.Text("run", 1)],
            new AgentContext { Tools = [slow, fast] },
            Config(),
            Replay(Assistant(
                string.Empty,
                StopReasons.ToolUse,
                Call("tool-1", "slow", "a"),
                Call("tool-2", "fast", "b"))),
            Collect(events),
            TestContext.Current.CancellationToken);

        releaseSlow.SetResult();
        await run;

        // One sequential tool in the batch serialises the whole batch.
        Assert.Equal(["slow", "fast"], order);
        Assert.Equal(["tool-1", "tool-2"], ToolResultIds(events));
    }

    [Fact]
    public async Task Should_allow_parallel_execution_when_all_tools_are_parallel()
    {
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tool = Tool("par", async (_, _, _, _) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            // Neither call can finish until both have started; this deadlocks if the
            // loop serialises a batch of parallel tools.
            await bothStarted.Task.ConfigureAwait(false);
            return new AgentToolResult { Content = [new TextContent("ok")] };
        }, ToolExecutionMode.Parallel);

        var events = new List<AgentEvent>();
        await AgentLoop.RunAsync(
            [UserMessage.Text("run", 1)],
            new AgentContext { Tools = [tool] },
            Config(),
            Replay(Assistant(
                string.Empty,
                StopReasons.ToolUse,
                Call("tool-1", "par", "a"),
                Call("tool-2", "par", "b"))),
            Collect(events),
            TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, started);
        Assert.Equal(["tool-1", "tool-2"], ToolResultIds(events));
    }

    // ---------------------------------------------------------------- termination

    [Fact]
    public async Task Should_stop_after_the_current_turn_when_shouldStopAfterTurn_returns_true()
    {
        var turns = 0;
        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(Assistant($"turn {turns}"));
        });

        var result = await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext(),
            Config(
                shouldStopAfterTurn: static _ => new ValueTask<bool>(true),
                getFollowUpMessages: static () =>
                    new ValueTask<IReadOnlyList<Message>>(new List<Message> { UserMessage.Text("more", 2) })),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        // The follow-up queue would normally continue the loop; the stop signal wins.
        Assert.Equal(1, turns);
        Assert.Equal(["user", "assistant"], result.Select(static m => m.Role));
    }

    [Fact]
    public async Task Should_stop_after_a_tool_batch_when_every_tool_result_sets_terminate()
    {
        var turns = 0;
        var tool = Tool("t", (_, _, _, _) => Task.FromResult(
            new AgentToolResult { Content = [new TextContent("done")], Terminate = true }));

        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(turns == 1
                ? Assistant(string.Empty, StopReasons.ToolUse, Call("c1", "t", "a"), Call("c2", "t", "b"))
                : Assistant("should not be reached"));
        });

        await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext { Tools = [tool] },
            Config(),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, turns);
    }

    [Fact]
    public async Task Should_stop_after_a_blocked_tool_call_when_beforeToolCall_sets_terminate()
    {
        var turns = 0;
        var executed = false;
        var tool = Tool("t", (_, _, _, _) =>
        {
            executed = true;
            return Task.FromResult(new AgentToolResult());
        });

        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(turns == 1
                ? Assistant(string.Empty, StopReasons.ToolUse, Call("c1", "t", "a"))
                : Assistant("should not be reached"));
        });

        await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext { Tools = [tool] },
            Config(beforeToolCall: static (_, _) => new ValueTask<BeforeToolCallResult?>(
                new BeforeToolCallResult { Block = true, Reason = "no", Terminate = true })),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.False(executed);
        Assert.Equal(1, turns);
    }

    [Fact]
    public async Task Should_continue_after_a_mixed_batch_with_one_terminating_blocked_call()
    {
        var turns = 0;
        var tool = Tool("t", (_, _, _, _) => Task.FromResult(
            new AgentToolResult { Content = [new TextContent("ok")] }));

        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(turns == 1
                ? Assistant(string.Empty, StopReasons.ToolUse, Call("c1", "t", "a"), Call("c2", "t", "b"))
                : Assistant("second turn"));
        });

        // Only the first call is blocked-and-terminating; the batch as a whole must not terminate.
        await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext { Tools = [tool] },
            Config(beforeToolCall: static (context, _) => new ValueTask<BeforeToolCallResult?>(
                context.ToolCall.Id == "c1"
                    ? new BeforeToolCallResult { Block = true, Reason = "no", Terminate = true }
                    : null)),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, turns);
    }

    [Fact]
    public async Task Should_continue_after_parallel_tool_calls_when_not_all_tool_results_terminate()
    {
        var turns = 0;
        var tool = Tool("t", (_, arguments, _, _) => Task.FromResult(new AgentToolResult
        {
            Content = [new TextContent("ok")],
            Terminate = arguments["value"]!.GetValue<string>() == "a",
        }));

        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(turns == 1
                ? Assistant(string.Empty, StopReasons.ToolUse, Call("c1", "t", "a"), Call("c2", "t", "b"))
                : Assistant("second turn"));
        });

        await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext { Tools = [tool] },
            Config(),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, turns);
    }

    // ---------------------------------------------------------------- context handling

    // Upstream's agentLoopContinue maps to AgentLoop.StartContinuation (stream) and
    // AgentLoop.RunContinuationAsync (task). Both guards are present with upstream's
    // exact messages.
    [Fact]
    public void Should_throw_when_context_has_no_messages()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AgentLoop.StartContinuation(
            new AgentContext { SystemPrompt = "You are helpful." },
            Config(),
            Replay(Assistant("unused")),
            TestContext.Current.CancellationToken));

        Assert.Equal("Cannot continue: no messages in context", error.Message);
    }

    /// <summary>
    /// Additional coverage beyond upstream's suite: the second guard in agentLoopContinue
    /// is implemented but has no upstream test of its own.
    /// </summary>
    [Fact]
    public void Should_throw_when_continuing_from_an_assistant_tail()
    {
        var messages = new List<Message> { UserMessage.Text("hi", 1), Assistant("reply") };

        var error = Assert.Throws<InvalidOperationException>(() => AgentLoop.StartContinuation(
            new AgentContext { Messages = messages },
            Config(),
            Replay(Assistant("unused")),
            TestContext.Current.CancellationToken));

        Assert.Equal("Cannot continue from message role: assistant", error.Message);
    }

    [Fact]
    public async Task Should_continue_from_existing_context_without_emitting_user_message_events()
    {
        var events = new List<AgentEvent>();
        var existing = new List<Message> { UserMessage.Text("Hello", 1) };

        var result = await AgentLoop.RunContinuationAsync(
            new AgentContext { SystemPrompt = "You are helpful.", Messages = existing },
            Config(),
            Replay(Assistant("Response")),
            Collect(events),
            TestContext.Current.CancellationToken);

        // Only the new assistant message is returned; the existing user message is not replayed.
        Assert.Single(result);
        Assert.Equal("assistant", result[0].Role);

        var ends = events.OfType<MessageEndEvent>().ToList();
        Assert.Single(ends);
        Assert.Equal("assistant", ends[0].Message.Role);
    }

    [Fact]
    public async Task Should_apply_transformContext_before_convertToLlm()
    {
        IReadOnlyList<Message>? seenByConverter = null;
        var config = new AgentLoopConfig
        {
            Model = TestModel(),
            TransformContext = static (messages, _) => new ValueTask<IReadOnlyList<Message>>(
                messages.Append(UserMessage.Text("injected", 2)).ToList()),
            ConvertToLlm = messages =>
            {
                seenByConverter = messages;
                return new ValueTask<IReadOnlyList<Message>>(messages);
            },
        };

        await AgentLoop.RunAsync(
            [UserMessage.Text("original", 1)],
            new AgentContext(),
            config,
            Replay(Assistant("ok")),
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(seenByConverter);
        Assert.Contains(seenByConverter!, static m => UserText(m) == "injected");
    }

    [Fact]
    public async Task Should_use_prepareNextTurn_snapshot_before_continuing()
    {
        var modelsSeen = new List<string>();
        var replacement = TestModel() with { Id = "replacement-model" };
        var turns = 0;

        var streamFunction = new AgentStreamFunction((model, _, _) =>
        {
            modelsSeen.Add(model.Id);
            turns++;
            return Completed(Assistant($"turn {turns}"));
        });

        var applied = false;
        await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext(),
            Config(
                prepareNextTurn: _ =>
                {
                    if (applied)
                    {
                        return new ValueTask<AgentLoopTurnUpdate?>((AgentLoopTurnUpdate?)null);
                    }

                    applied = true;
                    return new ValueTask<AgentLoopTurnUpdate?>(new AgentLoopTurnUpdate { Model = replacement });
                },
                getFollowUpMessages: () => new ValueTask<IReadOnlyList<Message>>(
                    turns == 1 ? new List<Message> { UserMessage.Text("again", 2) } : [])),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, modelsSeen.Count);
        Assert.Equal("test-model", modelsSeen[0]);
        Assert.Equal("replacement-model", modelsSeen[1]);
    }

    [Fact]
    public async Task Should_inject_queued_messages_after_all_tool_calls_complete()
    {
        var executed = new List<string>();
        var gate = new object();

        var tool = Tool("echo", async (_, arguments, _, _) =>
        {
            var value = arguments["value"]!.GetValue<string>();
            await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(false);
            lock (gate) { executed.Add(value); }
            return new AgentToolResult { Content = [new TextContent($"ok:{value}")] };
        }, ToolExecutionMode.Sequential);

        var drained = false;
        var turns = 0;
        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            turns++;
            return Completed(turns == 1
                ? Assistant(
                    string.Empty,
                    StopReasons.ToolUse,
                    Call("tool-1", "echo", "first"),
                    Call("tool-2", "echo", "second"))
                : Assistant("after steering"));
        });

        var result = await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext { Tools = [tool] },
            Config(getSteeringMessages: () =>
            {
                if (drained)
                {
                    return new ValueTask<IReadOnlyList<Message>>(Array.Empty<Message>());
                }

                drained = true;
                return new ValueTask<IReadOnlyList<Message>>(
                    new List<Message> { UserMessage.Text("interrupt", 2) });
            }),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        // Upstream asserts both tools run to completion before steering is injected.
        Assert.Equal(["first", "second"], executed);

        var transcript = result.Select(static m => UserText(m) ?? m.Role).ToList();
        var interruptAt = transcript.IndexOf("interrupt");
        Assert.True(interruptAt >= 0, "steering message was not injected into the transcript");
        Assert.Equal(2, result.OfType<ToolResultMessage>().Count());
    }

    [Fact]
    public async Task Should_forward_sessionId_to_stream_function_options()
    {
        string? seen = null;
        var streamFunction = new AgentStreamFunction((_, _, options) =>
        {
            seen = options?.SessionId;
            return Completed(Assistant("ok"));
        });

        await AgentLoop.RunAsync(
            [UserMessage.Text("go", 1)],
            new AgentContext(),
            Config(sessionId: "session-42"),
            streamFunction,
            Collect([]),
            TestContext.Current.CancellationToken);

        Assert.Equal("session-42", seen);
    }

    private static List<string> ToolResultIds(List<AgentEvent> events) =>
        [.. events.OfType<MessageEndEvent>()
            .Select(static e => e.Message)
            .OfType<ToolResultMessage>()
            .Select(static m => m.ToolCallId)];
}
