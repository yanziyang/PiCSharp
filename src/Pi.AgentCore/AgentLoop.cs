using System.Text.Json.Nodes;

using Pi.Ai;

namespace Pi.AgentCore;

/// <summary>Provider-independent agent-loop implementation.</summary>
public static class AgentLoop
{
    private static readonly object _defaultStreamGate = new();
    private static AgentStreamFunction? _defaultStreamFunction;

    /// <summary>Sets the process-wide fallback stream function used by legacy callers.</summary>
    public static void SetDefaultStreamFunction(AgentStreamFunction? streamFunction)
    {
        lock (_defaultStreamGate)
        {
            _defaultStreamFunction = streamFunction;
        }
    }

    /// <summary>Returns the configured process-wide fallback stream function.</summary>
    public static AgentStreamFunction? GetDefaultStreamFunction()
    {
        lock (_defaultStreamGate)
        {
            return _defaultStreamFunction;
        }
    }

    /// <summary>
    /// Starts a new loop, returning an event stream whose result contains only messages produced
    /// by this invocation, including the prompt messages.
    /// </summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Start(
        IReadOnlyList<Message> prompts,
        AgentContext context,
        AgentLoopConfig config,
        AgentStreamFunction? streamFunction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        var stream = new EventStream<AgentEvent, IReadOnlyList<Message>>(
            static @event => @event is AgentEndEvent,
            static @event => @event is AgentEndEvent end ? end.Messages : []);

        _ = CompleteStreamAsync(
            stream,
            () => RunAsync(prompts, context, config, streamFunction, null, cancellationToken));
        return stream;
    }

    /// <summary>Starts a continuation loop from an existing user or tool-result tail.</summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> StartContinuation(
        AgentContext context,
        AgentLoopConfig config,
        AgentStreamFunction? streamFunction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        if (context.Messages.Count == 0)
        {
            throw new InvalidOperationException("Cannot continue: no messages in context");
        }

        if (context.Messages[^1] is AssistantMessage)
        {
            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        var stream = new EventStream<AgentEvent, IReadOnlyList<Message>>(
            static @event => @event is AgentEndEvent,
            static @event => @event is AgentEndEvent end ? end.Messages : []);
        _ = CompleteStreamAsync(
            stream,
            () => RunContinuationAsync(context, config, streamFunction, null, cancellationToken));
        return stream;
    }

    /// <summary>Runs a new prompt and returns the messages produced by the invocation.</summary>
    public static async Task<IReadOnlyList<Message>> RunAsync(
        IReadOnlyList<Message> prompts,
        AgentContext context,
        AgentLoopConfig config,
        AgentStreamFunction? streamFunction = null,
        AgentEventSink? emit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        var newMessages = prompts.ToList();
        var currentContext = CloneContext(context, prompts);
        var sink = emit ?? (static (_, _) => ValueTask.CompletedTask);

        await sink(new AgentStartEvent(), cancellationToken).ConfigureAwait(false);
        await sink(new TurnStartEvent(), cancellationToken).ConfigureAwait(false);
        foreach (var prompt in prompts)
        {
            await sink(new MessageStartEvent(prompt), cancellationToken).ConfigureAwait(false);
            await sink(new MessageEndEvent(prompt), cancellationToken).ConfigureAwait(false);
        }

        await RunLoopAsync(
                currentContext,
                newMessages,
                config,
                sink,
                streamFunction ?? GetDefaultStreamFunction(),
                cancellationToken)
            .ConfigureAwait(false);
        return newMessages;
    }

    /// <summary>Runs a continuation without re-emitting the pre-existing transcript tail.</summary>
    public static async Task<IReadOnlyList<Message>> RunContinuationAsync(
        AgentContext context,
        AgentLoopConfig config,
        AgentStreamFunction? streamFunction = null,
        AgentEventSink? emit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        if (context.Messages.Count == 0)
        {
            throw new InvalidOperationException("Cannot continue: no messages in context");
        }

        if (context.Messages[^1] is AssistantMessage)
        {
            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        var newMessages = new List<Message>();
        var sink = emit ?? (static (_, _) => ValueTask.CompletedTask);
        await sink(new AgentStartEvent(), cancellationToken).ConfigureAwait(false);
        await sink(new TurnStartEvent(), cancellationToken).ConfigureAwait(false);
        await RunLoopAsync(
                CloneContext(context),
                newMessages,
                config,
                sink,
                streamFunction ?? GetDefaultStreamFunction(),
                cancellationToken)
            .ConfigureAwait(false);
        return newMessages;
    }

    private static async Task CompleteStreamAsync(
        EventStream<AgentEvent, IReadOnlyList<Message>> stream,
        Func<Task<IReadOnlyList<Message>>> run)
    {
        try
        {
            var result = await run().ConfigureAwait(false);
            stream.End(result);
        }
        catch
        {
            stream.End([]);
        }
    }

    private static async Task RunLoopAsync(
        AgentContext initialContext,
        List<Message> newMessages,
        AgentLoopConfig initialConfig,
        AgentEventSink emit,
        AgentStreamFunction? streamFunction,
        CancellationToken cancellationToken)
    {
        var currentContext = initialContext;
        var config = initialConfig;
        var currentModel = config.Model;
        var currentReasoning = config.Reasoning;
        ShouldStopAfterTurnContext? lastCompletedTurn = null;
        var pendingMessages = await GetMessagesAsync(config.GetSteeringMessages).ConfigureAwait(false);

        while (true)
        {
            var hasMoreToolCalls = true;
            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                if (lastCompletedTurn is not null)
                {
                    var next = config.PrepareNextTurn is null
                        ? null
                        : await config.PrepareNextTurn(lastCompletedTurn).ConfigureAwait(false);
                    if (next is not null)
                    {
                        if (next.Context is not null)
                        {
                            currentContext = CloneContext(next.Context);
                        }

                        if (next.Model is not null)
                        {
                            currentModel = next.Model;
                        }

                        if (next.ThinkingLevel is not null)
                        {
                            currentReasoning = next.ThinkingLevel == ThinkingLevels.Off
                                ? null
                                : next.ThinkingLevel;
                        }
                    }

                    if (pendingMessages.Count == 0)
                    {
                        pendingMessages = await GetMessagesAsync(config.GetSteeringMessages).ConfigureAwait(false);
                    }

                    await emit(new TurnStartEvent(), cancellationToken).ConfigureAwait(false);
                }

                if (pendingMessages.Count > 0)
                {
                    foreach (var message in pendingMessages)
                    {
                        await emit(new MessageStartEvent(message), cancellationToken).ConfigureAwait(false);
                        await emit(new MessageEndEvent(message), cancellationToken).ConfigureAwait(false);
                        currentContext.MessagesInternal.Add(message);
                        newMessages.Add(message);
                    }

                    pendingMessages = [];
                }

                var messageResult = await StreamAssistantResponseAsync(
                currentContext,
                config,
                currentModel,
                currentReasoning,
                emit,
                streamFunction,
                cancellationToken)
                    .ConfigureAwait(false);
                newMessages.Add(messageResult);

                if (messageResult.StopReason is StopReasons.Error or StopReasons.Aborted)
                {
                    await emit(new TurnEndEvent(messageResult, []), cancellationToken).ConfigureAwait(false);
                    await emit(new AgentEndEvent(newMessages.ToArray()), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var toolCalls = messageResult.Content.OfType<ToolCall>().ToArray();
                IReadOnlyList<ToolResultMessage> toolResults = [];
                hasMoreToolCalls = false;
                if (toolCalls.Length > 0)
                {
                    var executed = messageResult.StopReason == StopReasons.Length
                        ? await FailTruncatedToolCallsAsync(toolCalls, emit, cancellationToken).ConfigureAwait(false)
                        : await ExecuteToolCallsAsync(
                                currentContext,
                                messageResult,
                                toolCalls,
                                config,
                                emit,
                                cancellationToken)
                            .ConfigureAwait(false);
                    toolResults = executed.Messages;
                    hasMoreToolCalls = !executed.Terminate;
                    currentContext.MessagesInternal.AddRange(toolResults);
                    newMessages.AddRange(toolResults);
                }

                await emit(new TurnEndEvent(messageResult, toolResults), cancellationToken).ConfigureAwait(false);
                lastCompletedTurn = new ShouldStopAfterTurnContext
                {
                    Message = messageResult,
                    ToolResults = toolResults,
                    Context = currentContext,
                    NewMessages = newMessages.ToArray(),
                };

                if (config.ShouldStopAfterTurn is not null &&
                    await config.ShouldStopAfterTurn(lastCompletedTurn).ConfigureAwait(false))
                {
                    await emit(new AgentEndEvent(newMessages.ToArray()), cancellationToken).ConfigureAwait(false);
                    return;
                }

                pendingMessages = await GetMessagesAsync(config.GetSteeringMessages).ConfigureAwait(false);
            }

            var followUps = await GetMessagesAsync(config.GetFollowUpMessages).ConfigureAwait(false);
            if (followUps.Count > 0)
            {
                pendingMessages = followUps;
                continue;
            }

            break;
        }

        await emit(new AgentEndEvent(newMessages.ToArray()), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<Message>> GetMessagesAsync(
        Func<ValueTask<IReadOnlyList<Message>>>? getMessages)
    {
        if (getMessages is null)
        {
            return [];
        }

        return await getMessages().ConfigureAwait(false) ?? [];
    }

    private static async Task<AssistantMessage> StreamAssistantResponseAsync(
        AgentContext context,
        AgentLoopConfig config,
        Model model,
        string? reasoning,
        AgentEventSink emit,
        AgentStreamFunction? streamFunction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Message> messages = context.MessagesInternal.ToArray();
        if (config.TransformContext is not null)
        {
            messages = await config.TransformContext(messages, cancellationToken).ConfigureAwait(false);
        }

        var llmMessages = await config.ConvertToLlm(messages).ConfigureAwait(false);
        var llmContext = new Context
        {
            SystemPrompt = context.SystemPrompt,
            Messages = llmMessages,
            Tools = context.ToolsInternal
                .Select(static tool => new Tool
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.Parameters.DeepClone(),
                    ConstrainedSampling = tool.ConstrainedSampling,
                })
                .ToArray(),
        };
        var apiKey = config.GetApiKey is null
            ? config.ApiKey
            : await config.GetApiKey(model.Provider).ConfigureAwait(false) ?? config.ApiKey;
        var options = CreateStreamOptions(config, apiKey, reasoning, cancellationToken);
        var function = streamFunction ?? throw new InvalidOperationException("No default stream function has been configured.");
        var response = function(model, llmContext, options)
            ?? throw new InvalidOperationException("The stream function returned no stream.");

        AssistantMessage? partialMessage = null;
        var addedPartial = false;
        await foreach (var @event in response.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (@event)
            {
                case StreamStartEvent start:
                    partialMessage = start.Partial;
                    context.MessagesInternal.Add(partialMessage);
                    addedPartial = true;
                    await emit(new MessageStartEvent(partialMessage), cancellationToken).ConfigureAwait(false);
                    break;

                case TextStartEvent textStart:
                    partialMessage = await UpdatePartialAsync(textStart.Partial, textStart, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case TextDeltaEvent textDelta:
                    partialMessage = await UpdatePartialAsync(textDelta.Partial, textDelta, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case TextEndEvent textEnd:
                    partialMessage = await UpdatePartialAsync(textEnd.Partial, textEnd, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case ThinkingStartEvent thinkingStart:
                    partialMessage = await UpdatePartialAsync(thinkingStart.Partial, thinkingStart, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case ThinkingDeltaEvent thinkingDelta:
                    partialMessage = await UpdatePartialAsync(thinkingDelta.Partial, thinkingDelta, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case ThinkingEndEvent thinkingEnd:
                    partialMessage = await UpdatePartialAsync(thinkingEnd.Partial, thinkingEnd, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case ToolCallStartEvent toolStart:
                    partialMessage = await UpdatePartialAsync(toolStart.Partial, toolStart, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case ToolCallDeltaEvent toolDelta:
                    partialMessage = await UpdatePartialAsync(toolDelta.Partial, toolDelta, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case ToolCallEndEvent toolEnd:
                    partialMessage = await UpdatePartialAsync(toolEnd.Partial, toolEnd, context, partialMessage, addedPartial, emit, cancellationToken).ConfigureAwait(false);
                    break;
                case StreamDoneEvent:
                case StreamErrorEvent:
                    {
                        var finalMessage = await response.Result.ConfigureAwait(false);
                        ReplaceOrAppendAssistant(context, finalMessage, addedPartial);
                        if (!addedPartial)
                        {
                            await emit(new MessageStartEvent(finalMessage), cancellationToken).ConfigureAwait(false);
                        }

                        await emit(new MessageEndEvent(finalMessage), cancellationToken).ConfigureAwait(false);
                        return finalMessage;
                    }
            }
        }

        var result = await response.Result.ConfigureAwait(false);
        ReplaceOrAppendAssistant(context, result, addedPartial);
        if (!addedPartial)
        {
            await emit(new MessageStartEvent(result), cancellationToken).ConfigureAwait(false);
        }

        await emit(new MessageEndEvent(result), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<AssistantMessage?> UpdatePartialAsync(
        AssistantMessage partial,
        AssistantMessageEvent @event,
        AgentContext context,
        AssistantMessage? current,
        bool addedPartial,
        AgentEventSink emit,
        CancellationToken cancellationToken)
    {
        if (!addedPartial)
        {
            return current;
        }

        current = partial;
        context.MessagesInternal[^1] = partial;
        await emit(new MessageUpdateEvent(partial, @event), cancellationToken).ConfigureAwait(false);
        return current;
    }

    private static void ReplaceOrAppendAssistant(AgentContext context, AssistantMessage message, bool addedPartial)
    {
        if (addedPartial)
        {
            context.MessagesInternal[^1] = message;
        }
        else
        {
            context.MessagesInternal.Add(message);
        }
    }

    private static SimpleStreamOptions CreateStreamOptions(
        AgentLoopConfig config,
        string? apiKey,
        string? reasoning,
        CancellationToken cancellationToken) => new()
        {
            Signal = cancellationToken,
            TelemetryContext = config.TelemetryContext,
            ApiKey = apiKey,
            Fetch = config.Fetch,
            Environment = config.Environment,
            OnPayload = config.OnPayload,
            OnResponse = config.OnResponse,
            Headers = config.Headers,
            TimeoutMs = config.TimeoutMs,
            MaxRetries = config.MaxRetries,
            MaxRetryDelayMs = config.MaxRetryDelayMs,
            Temperature = config.Temperature,
            SamplingParameters = config.SamplingParameters,
            MaxTokens = config.MaxTokens,
            Transport = config.Transport,
            CacheRetention = config.CacheRetention,
            SessionId = config.SessionId,
            WebSocketConnectTimeoutMs = config.WebSocketConnectTimeoutMs,
            Metadata = config.Metadata,
            ToolChoice = config.ToolChoice,
            Reasoning = reasoning,
            Deferred = config.Deferred,
            DeferredWindow = config.DeferredWindow,
            ThinkingBudgets = config.ThinkingBudgets,
        };

    private static async Task<ExecutedToolCallBatch> FailTruncatedToolCallsAsync(
        ToolCall[] toolCalls,
        AgentEventSink emit,
        CancellationToken cancellationToken)
    {
        var messages = new List<ToolResultMessage>(toolCalls.Length);
        foreach (var toolCall in toolCalls)
        {
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments), cancellationToken).ConfigureAwait(false);
            var finalized = new FinalizedToolCall(
                toolCall,
                CreateErrorToolResult(
                    $"Tool call \"{toolCall.Name}\" was not executed: the response hit the output token limit, so its arguments may be truncated. Re-issue the tool call with complete arguments."),
                true);
            await EmitToolExecutionEndAsync(finalized, emit, cancellationToken).ConfigureAwait(false);
            var message = CreateToolResultMessage(finalized);
            await EmitToolResultMessageAsync(message, emit, cancellationToken).ConfigureAwait(false);
            messages.Add(message);
        }

        return new ExecutedToolCallBatch(messages, false);
    }

    private static async Task<ExecutedToolCallBatch> ExecuteToolCallsAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCall> toolCalls,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken cancellationToken)
    {
        var forceSequential = config.ToolExecution == ToolExecutionMode.Sequential ||
                              toolCalls.Any(call => context.ToolsInternal.FirstOrDefault(tool => tool.Name == call.Name)?.ExecutionMode == ToolExecutionMode.Sequential);
        return forceSequential
            ? await ExecuteSequentialAsync(context, assistantMessage, toolCalls, config, emit, cancellationToken).ConfigureAwait(false)
            : await ExecuteParallelAsync(context, assistantMessage, toolCalls, config, emit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExecutedToolCallBatch> ExecuteSequentialAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCall> toolCalls,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken cancellationToken)
    {
        var finalizedCalls = new List<FinalizedToolCall>();
        var messages = new List<ToolResultMessage>();
        foreach (var toolCall in toolCalls)
        {
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments), cancellationToken).ConfigureAwait(false);
            var preparation = await PrepareToolCallAsync(context, assistantMessage, toolCall, config, cancellationToken).ConfigureAwait(false);
            var finalized = preparation.Immediate
                ? new FinalizedToolCall(toolCall, preparation.Result!, preparation.IsError)
                : await ExecutePreparedToolCallAsync(context, assistantMessage, preparation, config, emit, false, cancellationToken).ConfigureAwait(false);
            await EmitToolExecutionEndAsync(finalized, emit, cancellationToken).ConfigureAwait(false);
            var message = CreateToolResultMessage(finalized);
            await EmitToolResultMessageAsync(message, emit, cancellationToken).ConfigureAwait(false);
            finalizedCalls.Add(finalized);
            messages.Add(message);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return new ExecutedToolCallBatch(messages, ShouldTerminate(finalizedCalls));
    }

    private static async Task<ExecutedToolCallBatch> ExecuteParallelAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCall> toolCalls,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task<FinalizedToolCall>>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments), cancellationToken).ConfigureAwait(false);
            var preparation = await PrepareToolCallAsync(context, assistantMessage, toolCall, config, cancellationToken).ConfigureAwait(false);
            if (preparation.Immediate)
            {
                var finalized = new FinalizedToolCall(toolCall, preparation.Result!, preparation.IsError);
                await EmitToolExecutionEndAsync(finalized, emit, cancellationToken).ConfigureAwait(false);
                tasks.Add(Task.FromResult(finalized));
            }
            else
            {
                tasks.Add(ExecutePreparedToolCallAsync(context, assistantMessage, preparation, config, emit, true, cancellationToken));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        var finalizedCalls = await Task.WhenAll(tasks).ConfigureAwait(false);
        var messages = new List<ToolResultMessage>(finalizedCalls.Length);
        foreach (var finalized in finalizedCalls)
        {
            var message = CreateToolResultMessage(finalized);
            await EmitToolResultMessageAsync(message, emit, cancellationToken).ConfigureAwait(false);
            messages.Add(message);
        }

        return new ExecutedToolCallBatch(messages, ShouldTerminate(finalizedCalls));
    }

    private sealed record ToolCallPreparation(
        ToolCall ToolCall,
        AgentTool? Tool,
        JsonObject? Arguments,
        AgentToolResult? Result,
        bool IsError)
    {
        public bool Immediate => Result is not null;
    }

    private static async ValueTask<ToolCallPreparation> PrepareToolCallAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCall toolCall,
        AgentLoopConfig config,
        CancellationToken cancellationToken)
    {
        var tool = context.ToolsInternal.FirstOrDefault(candidate => candidate.Name == toolCall.Name);
        if (tool is null)
        {
            return new ToolCallPreparation(toolCall, null, null, CreateErrorToolResult($"Tool {toolCall.Name} not found"), true);
        }

        try
        {
            var rawArguments = toolCall.Arguments.DeepClone().AsObject();
            var preparedArguments = tool.PrepareArguments?.Invoke(rawArguments) ?? rawArguments;
            var arguments = ToolArguments.Validate(
                tool,
                new ToolArguments.ToolCallLike(toolCall.Name, preparedArguments));
            if (config.BeforeToolCall is not null)
            {
                var before = await config.BeforeToolCall(
                        new BeforeToolCallContext
                        {
                            AssistantMessage = assistantMessage,
                            ToolCall = toolCall,
                            Arguments = arguments,
                            Context = context,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ToolCallPreparation(toolCall, null, null, CreateErrorToolResult("Operation aborted"), true);
                }

                if (before?.Block == true)
                {
                    return new ToolCallPreparation(
                        toolCall,
                        null,
                        null,
                        CreateErrorToolResult(before.Reason ?? "Tool execution was blocked") with { Terminate = before.Terminate },
                        true);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new ToolCallPreparation(toolCall, null, null, CreateErrorToolResult("Operation aborted"), true);
            }

            return new ToolCallPreparation(toolCall, tool, arguments, null, false);
        }
        catch (Exception error)
        {
            return new ToolCallPreparation(toolCall, null, null, CreateErrorToolResult(error.Message), true);
        }
    }

    private static async Task<FinalizedToolCall> ExecutePreparedToolCallAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCallPreparation preparation,
        AgentLoopConfig config,
        AgentEventSink emit,
        bool emitEnd,
        CancellationToken cancellationToken)
    {
        var updateTasks = new List<Task>();
        var acceptingUpdates = true;
        AgentToolResult result;
        var isError = false;
        try
        {
            result = await preparation.Tool!.Execute(
                    preparation.ToolCall.Id,
                    preparation.Arguments!,
                    cancellationToken,
                    partialResult =>
                    {
                        if (!acceptingUpdates)
                        {
                            return;
                        }

                        updateTasks.Add(
                            EmitToolUpdateAsync(
                                preparation.ToolCall,
                                preparation.Arguments!,
                                partialResult,
                                emit,
                                cancellationToken));
                    })
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            result = CreateErrorToolResult(error.Message);
            isError = true;
        }
        finally
        {
            acceptingUpdates = false;
            await Task.WhenAll(updateTasks).ConfigureAwait(false);
        }

        if (config.AfterToolCall is not null)
        {
            try
            {
                var after = await config.AfterToolCall(
                        new AfterToolCallContext
                        {
                            AssistantMessage = assistantMessage,
                            ToolCall = preparation.ToolCall,
                            Arguments = preparation.Arguments!,
                            Result = result,
                            IsError = isError,
                            Context = context,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (after is not null)
                {
                    result = result with
                    {
                        Content = after.Content ?? result.Content,
                        Details = after.Details ?? result.Details,
                        Usage = after.Usage ?? result.Usage,
                        Terminate = after.Terminate ?? result.Terminate,
                    };
                    isError = after.IsError ?? isError;
                }
            }
            catch (Exception error)
            {
                result = CreateErrorToolResult(error.Message);
                isError = true;
            }
        }

        var finalized = new FinalizedToolCall(preparation.ToolCall, result, isError);
        if (emitEnd)
        {
            await EmitToolExecutionEndAsync(finalized, emit, cancellationToken).ConfigureAwait(false);
        }

        return finalized;
    }

    private static async Task EmitToolUpdateAsync(
        ToolCall toolCall,
        JsonObject arguments,
        AgentToolResult partialResult,
        AgentEventSink emit,
        CancellationToken cancellationToken) =>
        await emit(
                new ToolExecutionUpdateEvent(toolCall.Id, toolCall.Name, arguments, partialResult),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task EmitToolExecutionEndAsync(
        FinalizedToolCall finalized,
        AgentEventSink emit,
        CancellationToken cancellationToken) =>
        await emit(
                new ToolExecutionEndEvent(
                    finalized.ToolCall.Id,
                    finalized.ToolCall.Name,
                    finalized.Result,
                    finalized.IsError),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task EmitToolResultMessageAsync(
        ToolResultMessage message,
        AgentEventSink emit,
        CancellationToken cancellationToken)
    {
        await emit(new MessageStartEvent(message), cancellationToken).ConfigureAwait(false);
        await emit(new MessageEndEvent(message), cancellationToken).ConfigureAwait(false);
    }

    private static ToolResultMessage CreateToolResultMessage(FinalizedToolCall finalized) => new()
    {
        ToolCallId = finalized.ToolCall.Id,
        ToolName = finalized.ToolCall.Name,
        Content = finalized.Result.Content,
        Details = finalized.Result.Details,
        Usage = finalized.Result.Usage,
        AddedToolNames = finalized.Result.AddedToolNames is { Count: > 0 } ? finalized.Result.AddedToolNames : null,
        IsError = finalized.IsError,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static AgentToolResult CreateErrorToolResult(string message) => new()
    {
        Content = [new TextContent(message)],
        Details = new JsonObject(),
    };

    private static bool ShouldTerminate(IEnumerable<FinalizedToolCall> finalizedCalls)
    {
        var calls = finalizedCalls.ToArray();
        return calls.Length > 0 && calls.All(call => call.Result.Terminate);
    }

    private sealed record FinalizedToolCall(ToolCall ToolCall, AgentToolResult Result, bool IsError);

    private sealed record ExecutedToolCallBatch(IReadOnlyList<ToolResultMessage> Messages, bool Terminate);

    private static AgentContext CloneContext(AgentContext source, IReadOnlyList<Message>? appended = null)
    {
        var sourceMessages = source.MessagesInternal.Count > 0 || source.Messages.Count == 0
            ? source.MessagesInternal
            : source.Messages;
        var sourceTools = source.ToolsInternal.Count > 0 || source.Tools.Count == 0
            ? source.ToolsInternal
            : source.Tools;
        var messages = sourceMessages.ToList();
        if (appended is not null)
        {
            messages.AddRange(appended);
        }

        var tools = sourceTools.ToList();

        return new AgentContext
        {
            SystemPrompt = source.SystemPrompt,
            Messages = messages,
            Tools = tools,
            MessagesInternal = messages,
            ToolsInternal = tools,
        };
    }

    private static AgentContext CloneContext(AgentContext source) => CloneContext(source, null);
}
