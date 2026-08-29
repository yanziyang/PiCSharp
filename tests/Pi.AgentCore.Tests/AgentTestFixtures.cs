using System.Text.Json.Nodes;

using Pi.Ai;

namespace Pi.AgentCore.Tests;

/// <summary>
/// Shared builders for agent-loop tests, mirroring the helpers upstream defines at the
/// top of <c>packages/agent/test/agent-loop.test.ts</c>.
/// </summary>
internal static class AgentTestFixtures
{
    public static Model TestModel() => new()
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

    public static AgentLoopConfig Config(
        Func<ShouldStopAfterTurnContext, ValueTask<bool>>? shouldStopAfterTurn = null,
        Func<ShouldStopAfterTurnContext, ValueTask<AgentLoopTurnUpdate?>>? prepareNextTurn = null,
        Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? beforeToolCall = null,
        Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? afterToolCall = null,
        Func<ValueTask<IReadOnlyList<Message>>>? getSteeringMessages = null,
        Func<ValueTask<IReadOnlyList<Message>>>? getFollowUpMessages = null,
        AgentContextTransformer? transformContext = null,
        ToolExecutionMode toolExecution = ToolExecutionMode.Parallel,
        string? sessionId = null) => new()
        {
            Model = TestModel(),
            ConvertToLlm = static messages => new(messages),
            ToolExecution = toolExecution,
            SessionId = sessionId,
            TransformContext = transformContext,
            ShouldStopAfterTurn = shouldStopAfterTurn,
            PrepareNextTurn = prepareNextTurn,
            GetSteeringMessages = getSteeringMessages,
            GetFollowUpMessages = getFollowUpMessages,
            BeforeToolCall = beforeToolCall,
            AfterToolCall = afterToolCall,
        };

    public static AgentEventSink Collect(List<AgentEvent> events) => (@event, _) =>
    {
        events.Add(@event);
        return ValueTask.CompletedTask;
    };

    public static AgentTool Tool(
        string name,
        AgentToolExecutor execute,
        ToolExecutionMode? executionMode = null) => new()
        {
            Name = name,
            Label = name,
            Description = name,
            Parameters = StringArgSchema(),
            ExecutionMode = executionMode,
            Execute = execute,
        };

    /// <summary>Mirrors upstream's <c>Type.Object({ value: Type.String() })</c>.</summary>
    public static JsonObject StringArgSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray { "value" },
    };

    public static AssistantMessage Assistant(
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

    public static AssistantMessageEventStream Completed(AssistantMessage message)
    {
        var stream = new AssistantMessageEventStream();
        stream.Push(new StreamDoneEvent(message.StopReason, message));
        stream.End(message);
        return stream;
    }

    /// <summary>
    /// Reads the text of a user message. Upstream models user content as the union
    /// <c>string | ContentBlock[]</c>, which the port carries as <see cref="object"/>.
    /// </summary>
    public static string? UserText(Message message) => message switch
    {
        UserMessage { Content: string text } => text,
        UserMessage { Content: IReadOnlyList<ContentBlock> blocks } =>
            string.Concat(blocks.OfType<TextContent>().Select(static block => block.Text)),
        _ => null,
    };

    public static ToolCall Call(string id, string name, string value) =>
        new(id, name, new JsonObject { ["value"] = value });

    /// <summary>
    /// A stream function that replays <paramref name="responses"/> in order, then answers
    /// "done" for any further turn. Mirrors upstream's <c>callIndex</c> pattern.
    /// </summary>
    public static AgentStreamFunction Replay(params AssistantMessage[] responses)
    {
        var index = 0;
        return (_, _, _) =>
        {
            var message = index < responses.Length ? responses[index] : Assistant("done");
            index++;
            return Completed(message);
        };
    }
}
