using System.Text.Json.Nodes;

using Pi.Ai;
using Pi.Ai.Testing;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class FauxProviderTests
{
    [Fact]
    public async Task Registers_provider_and_estimates_usage()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("hello world"))]);

        var response = await registration.Provider.CompleteAsync(registration.GetModel(), UserContext("hi there"));

        Assert.Equal([new TextContent("hello world")], response.Content);
        Assert.True(response.Usage.Input > 0);
        Assert.True(response.Usage.Output > 0);
        Assert.Equal(response.Usage.Input + response.Usage.Output, response.Usage.TotalTokens);
        Assert.Equal(1, registration.State.CallCount);
    }

    [Fact]
    public async Task Supports_text_thinking_and_tool_call_helpers()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
                new ContentBlock[]
                {
                    FauxMessages.FauxThinking("think"),
                    FauxMessages.FauxToolCall("echo", new JsonObject { ["text"] = "hi" }),
                    FauxMessages.FauxText("done"),
                },
                StopReasons.ToolUse)),
        ]);

        var response = await registration.Provider.CompleteAsync(registration.GetModel(), UserContext());

        Assert.Equal(
            ["thinking", "toolCall", "text"],
            response.Content.Select(static block => block.Type));
        Assert.Equal(StopReasons.ToolUse, response.StopReason);
    }

    [Fact]
    public async Task Supports_multiple_models_and_model_aware_factories()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            Models =
            [
                new FauxModelDefinition { Id = "faux-fast", Name = "Faux Fast" },
                new FauxModelDefinition { Id = "faux-thinker", Name = "Faux Thinker", Reasoning = true },
            ],
        });
        registration.SetResponses(
        [
            FauxResponseStep.FromFactory((_, _, _, model) =>
                Task.FromResult(FauxMessages.FauxAssistantMessage($"{model.Id}:{model.Reasoning}"))),
            FauxResponseStep.FromFactory((_, _, _, model) =>
                Task.FromResult(FauxMessages.FauxAssistantMessage($"{model.Id}:{model.Reasoning}"))),
        ]);

        Assert.Equal(["faux-fast", "faux-thinker"], registration.Models.Select(static model => model.Id));
        Assert.Same(registration.Models[0], registration.GetModel());
        Assert.False(registration.GetModel("faux-fast")!.Reasoning);
        Assert.True(registration.GetModel("faux-thinker")!.Reasoning);

        var fast = await registration.Provider.CompleteAsync(registration.GetModel("faux-fast")!, UserContext());
        var thinker = await registration.Provider.CompleteAsync(registration.GetModel("faux-thinker")!, UserContext());

        Assert.Equal([new TextContent("faux-fast:False")], fast.Content);
        Assert.Equal([new TextContent("faux-thinker:True")], thinker.Content);
    }

    [Fact]
    public async Task Rewrites_api_provider_and_model_on_returned_messages()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            Api = "faux:test",
            Provider = "faux-provider",
            Models = [new FauxModelDefinition { Id = "faux-model" }],
        });
        registration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("hello"))]);

        var response = await registration.Provider.CompleteAsync(registration.GetModel(), UserContext());

        Assert.Equal("faux:test", response.Api);
        Assert.Equal("faux-provider", response.Provider);
        Assert.Equal("faux-model", response.Model);
    }

    [Fact]
    public async Task Consumes_queued_responses_in_order_and_errors_when_exhausted()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("first")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("second")),
        ]);

        var context = UserContext();
        var first = await registration.Provider.CompleteAsync(registration.GetModel(), context);
        var second = await registration.Provider.CompleteAsync(registration.GetModel(), context);
        var exhausted = await registration.Provider.CompleteAsync(registration.GetModel(), context);

        Assert.Equal([new TextContent("first")], first.Content);
        Assert.Equal([new TextContent("second")], second.Content);
        Assert.Equal(StopReasons.Error, exhausted.StopReason);
        Assert.Equal("No more faux responses queued", exhausted.ErrorMessage);
        Assert.Equal(0, registration.GetPendingResponseCount());
        Assert.Equal(3, registration.State.CallCount);
    }

    [Fact]
    public async Task Can_replace_and_append_queued_responses()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("first"))]);
        var context = UserContext();

        Assert.Equal([new TextContent("first")],
            (await registration.Provider.CompleteAsync(registration.GetModel(), context)).Content);
        Assert.Equal(0, registration.GetPendingResponseCount());

        registration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("second"))]);
        Assert.Equal(1, registration.GetPendingResponseCount());
        Assert.Equal([new TextContent("second")],
            (await registration.Provider.CompleteAsync(registration.GetModel(), context)).Content);

        registration.AppendResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("third")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("fourth")),
        ]);
        Assert.Equal(2, registration.GetPendingResponseCount());
        Assert.Equal([new TextContent("third")],
            (await registration.Provider.CompleteAsync(registration.GetModel(), context)).Content);
        Assert.Equal([new TextContent("fourth")],
            (await registration.Provider.CompleteAsync(registration.GetModel(), context)).Content);
        Assert.Equal(0, registration.GetPendingResponseCount());
    }

    [Fact]
    public async Task Supports_async_factories_and_reports_factory_errors()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses(
        [
            FauxResponseStep.FromFactory((context, _, state, _) =>
                Task.FromResult(FauxMessages.FauxAssistantMessage($"{context.Messages.Count}:{state.CallCount}"))),
            FauxResponseStep.FromFactory((_, _, _, _) =>
                Task.FromException<AssistantMessage>(new InvalidOperationException("boom"))),
        ]);

        var first = await registration.Provider.CompleteAsync(registration.GetModel(), UserContext());
        var events = await CollectAsync(registration.Provider.Stream(registration.GetModel(), UserContext()));

        Assert.Equal([new TextContent("1:1")], first.Content);
        var terminal = Assert.IsType<StreamErrorEvent>(Assert.Single(events));
        Assert.Equal(StopReasons.Error, terminal.Error.StopReason);
        Assert.Equal("boom", terminal.Error.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_a_response_without_a_terminal_stop_reason()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
                "partial",
                stopReason: StopReasons.Pending)),
        ]);

        var events = await CollectAsync(registration.Provider.Stream(registration.GetModel(), UserContext()));

        Assert.DoesNotContain(events, static @event => @event is StreamDoneEvent);
        var terminal = Assert.IsType<StreamErrorEvent>(events[^1]);
        Assert.Equal(StopReasons.Error, terminal.Error.StopReason);
        Assert.Equal("Faux response ended without a stop reason", terminal.Error.ErrorMessage);
    }

    [Fact]
    public async Task Estimates_prompt_and_output_tokens_from_serialized_context()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("done"))]);
        var tool = new Tool
        {
            Name = "echo",
            Description = "Echo back text",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("text"),
                ["additionalProperties"] = false,
            },
        };
        var context = new Context
        {
            SystemPrompt = "sys",
            Messages =
            [
                UserMessage.Blocks(
                    [new TextContent("hello"), new ImageContent("abcd", "image/png")],
                    1),
                FauxMessages.FauxAssistantMessage("prior"),
                new ToolResultMessage
                {
                    ToolCallId = "tool-1",
                    ToolName = "echo",
                    Content = [new TextContent("tool out")],
                    IsError = false,
                    Timestamp = 2,
                },
            ],
            Tools = [tool],
        };

        var response = await registration.Provider.CompleteAsync(registration.GetModel(), context);
        var promptText = string.Join(
            "\n\n",
            "system:sys",
            "user:hello\n[image:image/png:4]",
            "assistant:prior",
            "toolResult:echo\ntool out",
            "tools:[{\"name\":\"echo\",\"description\":\"Echo back text\",\"parameters\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}}]");

        var expectedPromptTokens = (int)Math.Ceiling(promptText.Length / 4d);
        var expectedOutputTokens = (int)Math.Ceiling("done".Length / 4d);
        Assert.Equal(expectedPromptTokens, response.Usage.Input);
        Assert.Equal(expectedOutputTokens, response.Usage.Output);
        Assert.Equal(0, response.Usage.CacheRead);
        Assert.Equal(0, response.Usage.CacheWrite);
        Assert.Equal(expectedPromptTokens + expectedOutputTokens, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Simulates_prompt_caching_per_session_and_respects_none()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider();
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("first")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("second")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("third")),
        ]);
        var context = new Context
        {
            SystemPrompt = "Be concise.",
            Messages = [UserMessage.Text("hello", 1)],
        };

        var first = await registration.Provider.CompleteAsync(
            registration.GetModel(),
            context,
            new SimpleStreamOptions { SessionId = "session-1", CacheRetention = CacheRetentions.Short });
        Assert.Equal(0, first.Usage.CacheRead);
        Assert.True(first.Usage.CacheWrite > 0);

        context = context with
        {
            Messages = [.. context.Messages, first, UserMessage.Text("follow up", 2)],
        };
        var second = await registration.Provider.CompleteAsync(
            registration.GetModel(),
            context,
            new SimpleStreamOptions { SessionId = "session-1", CacheRetention = CacheRetentions.Short });
        Assert.True(second.Usage.CacheRead > 0);
        Assert.True(second.Usage.Input + second.Usage.CacheRead > second.Usage.Input);

        var third = await registration.Provider.CompleteAsync(
            registration.GetModel(),
            context,
            new SimpleStreamOptions { SessionId = "session-1", CacheRetention = CacheRetentions.None });
        Assert.Equal(0, third.Usage.CacheRead);
        Assert.Equal(0, third.Usage.CacheWrite);
    }

    [Fact]
    public async Task Streams_exact_event_order_for_fixed_size_chunks()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            TokenSize = new FauxTokenSizeOptions { Min = 1, Max = 1 },
        });
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
                new ContentBlock[]
                {
                    FauxMessages.FauxThinking("go"),
                    FauxMessages.FauxText("ok"),
                    FauxMessages.FauxToolCall("echo", new JsonObject(), "tool-1"),
                },
                StopReasons.ToolUse)),
        ]);

        var events = await CollectAsync(registration.Provider.Stream(registration.GetModel(), UserContext()));

        Assert.Equal(
            [
                "start",
                "thinking_start",
                "thinking_delta",
                "thinking_end",
                "text_start",
                "text_delta",
                "text_end",
                "toolcall_start",
                "toolcall_delta",
                "toolcall_end",
                "done",
            ],
            events.Select(static @event => @event.Type));
        Assert.Equal(StopReasons.Pending, Assert.IsType<StreamStartEvent>(events[0]).Partial.StopReason);
    }

    [Fact]
    public async Task Streams_tool_call_deltas_that_reassemble_to_json()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            TokenSize = new FauxTokenSizeOptions { Min = 1, Max = 1 },
        });
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
                [FauxMessages.FauxToolCall("echo", new JsonObject { ["text"] = "hi", ["count"] = 12 }, "tool-1")],
                StopReasons.ToolUse)),
        ]);

        var events = await CollectAsync(registration.Provider.Stream(registration.GetModel(), UserContext()));
        var deltas = events
            .OfType<ToolCallDeltaEvent>()
            .Select(static @event => @event.Delta);

        Assert.Equal("{\"text\":\"hi\",\"count\":12}", string.Concat(deltas));
        Assert.Equal(2, events.Count(static @event => @event is ToolCallStartEvent or ToolCallEndEvent));
    }

    [Fact]
    public async Task Streams_explicit_error_and_aborted_messages_as_terminal_errors()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            TokenSize = new FauxTokenSizeOptions { Min = 2, Max = 2 },
        });
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
                "partial",
                stopReason: StopReasons.Error,
                errorMessage: "upstream failed")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
                "partial",
                stopReason: StopReasons.Aborted,
                errorMessage: "Request was aborted")),
        ]);

        var errorEvents = await CollectAsync(registration.Provider.Stream(registration.GetModel(), UserContext()));
        var abortedEvents = await CollectAsync(registration.Provider.Stream(registration.GetModel(), UserContext()));
        Assert.Equal(["start", "text_start", "text_delta", "text_end", "error"],
            errorEvents.Select(static @event => @event.Type));
        Assert.Equal(StopReasons.Error, Assert.IsType<StreamErrorEvent>(errorEvents[^1]).Reason);
        Assert.Equal("upstream failed", Assert.IsType<StreamErrorEvent>(errorEvents[^1]).Error.ErrorMessage);
        Assert.Equal(StopReasons.Aborted, Assert.IsType<StreamErrorEvent>(abortedEvents[^1]).Reason);
    }

    [Fact]
    public async Task Supports_abort_before_and_during_a_paced_stream()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            TokensPerSecond = 100,
            TokenSize = new FauxTokenSizeOptions { Min = 3, Max = 3 },
        });
        registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("abcdefghijklmnopqrstuvwxyz")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("abcdefghijklmnopqrstuvwxyz")),
        ]);

        using var before = new CancellationTokenSource();
        before.Cancel();
        var beforeEvents = await CollectAsync(registration.Provider.Stream(
            registration.GetModel(),
            UserContext(),
            new SimpleStreamOptions { Signal = before.Token }));
        Assert.Single(beforeEvents);
        Assert.Equal(StopReasons.Aborted, Assert.IsType<StreamErrorEvent>(beforeEvents[0]).Reason);

        using var during = new CancellationTokenSource();
        var duringEvents = new List<AssistantMessageEvent>();
        await foreach (var @event in registration.Provider.Stream(
                           registration.GetModel(),
                           UserContext(),
                           new SimpleStreamOptions { Signal = during.Token }))
        {
            duringEvents.Add(@event);
            if (@event is TextDeltaEvent)
            {
                during.Cancel();
            }
        }

        Assert.Contains(duringEvents, static @event => @event is TextDeltaEvent);
        Assert.IsType<StreamErrorEvent>(duringEvents[^1]);
        Assert.DoesNotContain(duringEvents, static @event => @event is TextEndEvent);
    }

    [Fact]
    public async Task Supports_deferred_polling_and_cancellation()
    {
        using var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            Deferred = new FauxDeferredOptions { PendingFetches = 1, PollAfterMs = 25 },
        });
        registration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("ready"))]);
        var model = registration.GetModel();
        var initial = await registration.Provider.CompleteAsync(
            model,
            UserContext(),
            new SimpleStreamOptions { Deferred = true });
        var handle = Assert.IsType<DeferredHandle>(initial.Deferred);
        Assert.Equal(StopReasons.Deferred, initial.StopReason);
        Assert.Equal(0, registration.State.DeferredFetchCount);

        var pending = await registration.Provider.FetchDeferred(model, handle).Result;
        Assert.Equal(StopReasons.Deferred, pending.StopReason);
        Assert.Equal(1, registration.State.DeferredFetchCount);

        var final = await registration.Provider.FetchDeferred(model, handle).Result;
        Assert.Equal([new TextContent("ready")], final.Content);
        Assert.Equal(StopReasons.Stop, final.StopReason);
        Assert.Equal(2, registration.State.DeferredFetchCount);

        using var cancelledRegistration = FauxProviderFactory.RegisterFauxProvider();
        cancelledRegistration.SetResponses([FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("never"))]);
        var cancelled = await cancelledRegistration.Provider.CompleteAsync(
            cancelledRegistration.GetModel(),
            UserContext(),
            new SimpleStreamOptions { Deferred = true });
        var cancelledHandle = Assert.IsType<DeferredHandle>(cancelled.Deferred);
        await cancelledRegistration.Provider.CancelDeferredAsync(cancelledRegistration.GetModel(), cancelledHandle);
        var cancelledEvents = await CollectAsync(
            cancelledRegistration.Provider.FetchDeferred(cancelledRegistration.GetModel(), cancelledHandle));
        var error = Assert.IsType<StreamErrorEvent>(cancelledEvents[^1]);
        Assert.Equal($"Faux deferred response was cancelled: {cancelledHandle.Id}", error.Error.ErrorMessage);
        Assert.Single(cancelledRegistration.State.CancelledDeferred);
    }

    [Fact]
    public void Unregister_removes_the_faux_registration()
    {
        var registration = FauxProviderFactory.RegisterFauxProvider();
        var api = registration.Api;
        Assert.Same(registration, FauxProviderFactory.GetRegistration(api));
        registration.Unregister();
        Assert.Null(FauxProviderFactory.GetRegistration(api));
        registration.Unregister();
    }

    private static Context UserContext(string content = "hi") => new()
    {
        Messages = [UserMessage.Text(content, 1)],
    };

    private static async Task<List<AssistantMessageEvent>> CollectAsync(AssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        return events;
    }
}
