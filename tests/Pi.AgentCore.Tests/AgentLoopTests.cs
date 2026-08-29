using System.Globalization;
using System.Text.Json.Nodes;
using Pi.AgentCore;
using Pi.Ai;
using Xunit;

namespace Pi.AgentCore.Tests;

public sealed class AgentLoopTests
{
    [Fact]
    public async Task Emits_prompt_and_assistant_lifecycle_in_order()
    {
        var events = new List<AgentEvent>();
        var result = await AgentLoop.RunAsync(
            [UserMessage.Text("hello", 1)],
            new AgentContext { SystemPrompt = "help" },
            Config(),
            static (_, _, _) => Completed(Assistant("world")),
            Collect(events),
            TestContext.Current.CancellationToken);

        Assert.Equal(["user", "assistant"], result.Select(static message => message.Role));
        Assert.Equal(
            [
                "agent_start", "turn_start", "message_start", "message_end", "message_start",
                "message_end", "turn_end", "agent_end",
            ],
            events.Select(static @event => @event.Type));
    }

    [Fact]
    public async Task Executes_tool_calls_and_applies_after_hook_overrides()
    {
        var executed = new List<string>();
        var observedUsage = (Usage?)null;
        var callNumber = 0;
        var tool = new AgentTool
        {
            Name = "echo",
            Label = "Echo",
            Description = "Echoes a value",
            Parameters = ObjectSchema(("value", new JsonObject { ["type"] = "string" }), "value"),
            Execute = (id, arguments, _, onUpdate) =>
            {
                executed.Add($"{id}:{arguments["value"]!.GetValue<string>()}");
                onUpdate?.Invoke(new AgentToolResult
                {
                    Content = [new TextContent("running")],
                    Details = new JsonObject { ["stage"] = "running" },
                });
                return Task.FromResult(new AgentToolResult
                {
                    Content = [new TextContent("done")],
                    Details = new JsonObject { ["stage"] = "done" },
                    Usage = new Usage { Input = 1 },
                });
            },
        };
        var events = new List<AgentEvent>();
        var config = Config(
            new[] { tool },
            afterToolCall: (context, _) =>
            {
                observedUsage = context.Result.Usage;
                return new ValueTask<AfterToolCallResult?>(new AfterToolCallResult
                {
                    Content = [new TextContent("patched")],
                    Usage = new Usage { Output = 2 },
                    Terminate = true,
                });
            });
        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            callNumber++;
            var response = callNumber == 1
                ? Assistant(
                    string.Empty,
                    StopReasons.ToolUse,
                    new ToolCall("call-1", "echo", new JsonObject { ["value"] = "hello" }))
                : Assistant("finished");
            return Completed(response);
        });

        var result = await AgentLoop.RunAsync(
            [UserMessage.Text("run", 1)],
            new AgentContext { Tools = [tool] },
            config,
            streamFunction,
            Collect(events),
            TestContext.Current.CancellationToken);

        Assert.Equal(["call-1:hello"], executed);
        Assert.Equal(1, observedUsage!.Input);
        Assert.Contains(events, static @event => @event is ToolExecutionUpdateEvent);
        var resultMessage = Assert.Single(result.OfType<ToolResultMessage>());
        Assert.Equal("patched", Assert.IsType<TextContent>(resultMessage.Content[0]).Text);
        Assert.Equal(2, resultMessage.Usage!.Output);
        Assert.Equal(1, callNumber);
    }

    [Fact]
    public async Task Preparation_runs_before_validation_and_before_hook_mutation_is_not_revalidated()
    {
        string? executedValue = null;
        var tool = new AgentTool
        {
            Name = "prepare",
            Label = "Prepare",
            Description = "Prepares a value",
            Parameters = ObjectSchema(("value", new JsonObject { ["type"] = "string" }), "value"),
            PrepareArguments = arguments =>
            {
                arguments["value"] = arguments["value"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture);
                return arguments;
            },
            Execute = (_, arguments, _, _) =>
            {
                executedValue = arguments["value"]!.ToJsonString();
                return Task.FromResult(new AgentToolResult
                {
                    Content = [new TextContent("ok")],
                });
            },
        };
        var calls = 0;
        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            calls++;
            return Completed(calls == 1
                ? Assistant(
                    string.Empty,
                    StopReasons.ToolUse,
                    new ToolCall("call-1", "prepare", new JsonObject { ["value"] = 42 }))
                : Assistant("finished"));
        });

        await AgentLoop.RunAsync(
            [UserMessage.Text("run", 1)],
            new AgentContext { Tools = [tool] },
            Config(
                [tool],
                beforeToolCall: (context, _) =>
                {
                    context.Arguments["value"] = 123;
                    return new ValueTask<BeforeToolCallResult?>((BeforeToolCallResult?)null);
                }),
            streamFunction,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("123", executedValue);
    }

    [Fact]
    public async Task Does_not_execute_tool_calls_from_length_truncated_response()
    {
        var executed = 0;
        var tool = new AgentTool
        {
            Name = "unsafe",
            Label = "Unsafe",
            Description = "Must not execute on truncation",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, _, _, _) =>
            {
                executed++;
                return Task.FromResult(new AgentToolResult { Content = [new TextContent("bad")] });
            },
        };
        var calls = 0;
        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            calls++;
            return Completed(calls == 1
                ? Assistant(string.Empty, StopReasons.Length, new ToolCall("call-1", "unsafe", new JsonObject()))
                : Assistant("recovered"));
        });

        var result = await AgentLoop.RunAsync(
            [UserMessage.Text("run", 1)],
            new AgentContext { Tools = [tool] },
            Config([tool]),
            streamFunction,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, executed);
        Assert.Equal(2, calls);
        Assert.Contains(result.OfType<ToolResultMessage>(), message => message.IsError);
    }

    [Fact]
    public async Task Parallel_execution_emits_completion_order_but_persists_source_order()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionEnds = new List<string>();
        var tools = new[]
        {
            MakeTool("first", async (_, _, _, _) =>
            {
                await releaseFirst.Task.ConfigureAwait(false);
                return new AgentToolResult { Content = [new TextContent("first")] };
            }),
            MakeTool("second", (_, _, _, _) =>
            {
                secondFinished.SetResult();
                return Task.FromResult(new AgentToolResult { Content = [new TextContent("second")] });
            }),
        };
        var calls = 0;
        var streamFunction = new AgentStreamFunction((_, _, _) =>
        {
            calls++;
            return Completed(calls == 1
                ? Assistant(
                    string.Empty,
                    StopReasons.ToolUse,
                    new ToolCall("first-id", "first", new JsonObject()),
                    new ToolCall("second-id", "second", new JsonObject()))
                : Assistant("finished"));
        });
        var events = new List<AgentEvent>();
        var run = AgentLoop.RunAsync(
            [UserMessage.Text("run", 1)],
            new AgentContext { Tools = tools },
            Config(tools),
            streamFunction,
            Collect(events),
            TestContext.Current.CancellationToken);

        await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        releaseFirst.SetResult();
        var result = await run;

        executionEnds.AddRange(
            events.OfType<ToolExecutionEndEvent>().Select(static @event => @event.ToolName));
        Assert.Equal(["second", "first"], executionEnds);
        Assert.Equal(["first", "second"], result.OfType<ToolResultMessage>().Select(static message => message.ToolName));
    }

    [Fact]
    public async Task Steering_and_follow_up_messages_continue_the_loop()
    {
        var callCount = 0;
        var streamFunction = new AgentStreamFunction((_, context, _) =>
        {
            callCount++;
            var text = context.Messages[^1] is UserMessage user && user.Content is string value
                ? value
                : "unknown";
            return Completed(Assistant(text));
        });
        var steering = new Queue<Message>([UserMessage.Text("steer", 2)]);
        var followUp = new Queue<Message>([UserMessage.Text("follow", 3)]);
        var config = Config(
            getSteeringMessages: () => new ValueTask<IReadOnlyList<Message>>(steering.Count == 0 ? [] : [steering.Dequeue()]),
            getFollowUpMessages: () => new ValueTask<IReadOnlyList<Message>>(followUp.Count == 0 ? [] : [followUp.Dequeue()]));

        var result = await AgentLoop.RunAsync(
            [UserMessage.Text("initial", 1)],
            new AgentContext(),
            config,
            streamFunction,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Equal(["initial", "steer", "follow"], result.OfType<UserMessage>().Select(static user => (string)user.Content));
    }

    [Fact]
    public async Task Agent_tracks_state_awaits_listeners_and_rejects_concurrent_prompt()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentState { Model = TestModel() },
            StreamFunction = static (_, _, _) => Completed(Assistant("ok")),
        });
        var listenerFinished = false;
        agent.Subscribe(async (@event, _) =>
        {
            if (@event is AgentEndEvent)
            {
                await barrier.Task.ConfigureAwait(false);
                listenerFinished = true;
            }
        });

        var prompt = agent.PromptAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(agent.State.IsStreaming);
        Assert.False(listenerFinished);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.PromptAsync("second", cancellationToken: TestContext.Current.CancellationToken));
        barrier.SetResult();
        await prompt;
        Assert.False(agent.State.IsStreaming);
        Assert.True(listenerFinished);
        Assert.Equal(2, agent.State.Messages.Count);
    }

    [Fact]
    public async Task Agent_abort_surfaces_an_aborted_assistant_message()
    {
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentState { Model = TestModel() },
            StreamFunction = static (_, _, options) =>
            {
                var stream = new AssistantMessageEventStream();
                _ = Task.Run(async () =>
                {
                    while (options?.Signal.IsCancellationRequested != true)
                    {
                        await Task.Delay(5).ConfigureAwait(false);
                    }

                    var message = Assistant("", StopReasons.Aborted);
                    stream.Push(new StreamErrorEvent(StopReasons.Aborted, message));
                    stream.End(message);
                });
                return stream;
            },
        });

        var prompt = agent.PromptAsync("cancel", cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        agent.Abort();
        await prompt;

        var message = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReasons.Aborted, message.StopReason);
    }

    private static AgentLoopConfig Config(
        IReadOnlyList<AgentTool>? tools = null,
        Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? afterToolCall = null,
        Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? beforeToolCall = null,
        Func<ValueTask<IReadOnlyList<Message>>>? getSteeringMessages = null,
        Func<ValueTask<IReadOnlyList<Message>>>? getFollowUpMessages = null) => new()
        {
            Model = TestModel(),
            ConvertToLlm = static messages => new(messages),
            ToolExecution = ToolExecutionMode.Parallel,
            GetSteeringMessages = getSteeringMessages,
            GetFollowUpMessages = getFollowUpMessages,
            BeforeToolCall = beforeToolCall,
            AfterToolCall = afterToolCall,
        };

    private static AgentEventSink Collect(List<AgentEvent> events) => (@event, _) =>
    {
        events.Add(@event);
        return ValueTask.CompletedTask;
    };

    private static AgentTool MakeTool(string name, AgentToolExecutor execute) => new()
    {
        Name = name,
        Label = name,
        Description = name,
        Parameters = new JsonObject { ["type"] = "object" },
        Execute = execute,
    };

    private static JsonObject ObjectSchema((string Name, JsonObject Schema) property, string required) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { [property.Name] = property.Schema },
        ["required"] = new JsonArray { required },
    };

    private static AssistantMessageEventStream Completed(AssistantMessage message)
    {
        var stream = new AssistantMessageEventStream();
        stream.Push(new StreamDoneEvent(message.StopReason, message));
        stream.End(message);
        return stream;
    }

    private static AssistantMessage Assistant(
        string text,
        string stopReason = StopReasons.Stop,
        params ToolCall[] toolCalls)
    {
        var content = new List<ContentBlock>();
        if (!string.IsNullOrEmpty(text))
        {
            content.Add(new TextContent(text));
        }

        content.AddRange(toolCalls);
        return new AssistantMessage
        {
            Content = content,
            Api = ApiNames.OpenAiResponses,
            Provider = "test",
            Model = "test-model",
            Usage = new Usage(),
            StopReason = stopReason,
            Timestamp = 1,
        };
    }

    private static Model TestModel() => new()
    {
        Id = "test-model",
        Name = "test-model",
        Api = ApiNames.OpenAiResponses,
        Provider = "test",
        BaseUrl = "https://example.invalid",
        Input = ["text"],
        ContextWindow = 8192,
        MaxTokens = 2048,
    };
}
