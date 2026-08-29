using System.Text.Json.Nodes;

using Pi.Ai;

namespace Pi.AgentCore;

/// <summary>Construction options for the stateful agent wrapper.</summary>
public sealed class AgentOptions
{
    /// <summary>Initial state copied into the new agent.</summary>
    public AgentState? InitialState { get; init; }

    /// <summary>Transcript-to-provider conversion callback.</summary>
    public AgentMessageConverter? ConvertToLlm { get; init; }

    /// <summary>Optional context transform before each provider call.</summary>
    public AgentContextTransformer? TransformContext { get; init; }

    /// <summary>Provider-neutral stream function.</summary>
    public AgentStreamFunction? StreamFunction { get; init; }

    /// <summary>Dynamic API-key resolver.</summary>
    public Func<string, ValueTask<string?>>? GetApiKey { get; init; }

    /// <summary>Payload callback forwarded to provider requests.</summary>
    public Func<JsonNode?, Model, ValueTask<JsonNode?>>? OnPayload { get; init; }

    /// <summary>Response callback forwarded to provider requests.</summary>
    public Func<ProviderResponse, Model, ValueTask>? OnResponse { get; init; }

    /// <summary>Before-tool-call hook.</summary>
    public Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

    /// <summary>After-tool-call hook.</summary>
    public Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? AfterToolCall { get; init; }

    /// <summary>Stop-after-turn hook.</summary>
    public Func<ShouldStopAfterTurnContext, CancellationToken, ValueTask<bool>>? ShouldStopAfterTurn { get; init; }

    /// <summary>Context-aware prepare-next-turn hook.</summary>
    public Func<ShouldStopAfterTurnContext, CancellationToken, ValueTask<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }

    /// <summary>Queue behavior for steering messages.</summary>
    public QueueMode SteeringMode { get; init; } = QueueMode.OneAtATime;

    /// <summary>Queue behavior for follow-up messages.</summary>
    public QueueMode FollowUpMode { get; init; } = QueueMode.OneAtATime;

    /// <summary>Provider cache/session identifier.</summary>
    public string? SessionId { get; init; }

    /// <summary>Optional reasoning token budgets.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }

    /// <summary>Preferred provider transport.</summary>
    public string? Transport { get; init; }

    /// <summary>Maximum provider-requested retry delay.</summary>
    public int? MaxRetryDelayMs { get; init; }

    /// <summary>Default tool scheduling mode.</summary>
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
}

/// <summary>Stateful wrapper around the low-level Pi agent loop.</summary>
public sealed class Agent
{
    private readonly object _gate = new();
    private readonly List<AgentEventSink> _listeners = [];
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private readonly AgentMessageConverter _convertToLlm;
    private readonly AgentContextTransformer? _transformContext;
    private readonly AgentStreamFunction _streamFunction;
    private readonly Func<string, ValueTask<string?>>? _getApiKey;
    private readonly Func<JsonNode?, Model, ValueTask<JsonNode?>>? _onPayload;
    private readonly Func<ProviderResponse, Model, ValueTask>? _onResponse;
    private readonly Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? _beforeToolCall;
    private readonly Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? _afterToolCall;
    private readonly Func<ShouldStopAfterTurnContext, CancellationToken, ValueTask<bool>>? _shouldStopAfterTurn;
    private readonly Func<ShouldStopAfterTurnContext, CancellationToken, ValueTask<AgentLoopTurnUpdate?>>? _prepareNextTurn;
    private ActiveRun? _activeRun;

    /// <summary>Creates an agent with optional initial state and callbacks.</summary>
    public Agent(AgentOptions? options = null)
    {
        options ??= new AgentOptions();
        _state = CopyState(options.InitialState);
        _convertToLlm = options.ConvertToLlm ?? DefaultConvertToLlm;
        _transformContext = options.TransformContext;
        _streamFunction = options.StreamFunction ?? AgentLoop.GetDefaultStreamFunction()
            ?? throw new InvalidOperationException("No default stream function has been configured.");
        _getApiKey = options.GetApiKey;
        _onPayload = options.OnPayload;
        _onResponse = options.OnResponse;
        _beforeToolCall = options.BeforeToolCall;
        _afterToolCall = options.AfterToolCall;
        _shouldStopAfterTurn = options.ShouldStopAfterTurn;
        _prepareNextTurn = options.PrepareNextTurn;
        _steeringQueue = new PendingMessageQueue(options.SteeringMode);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode);
        SessionId = options.SessionId;
        ThinkingBudgets = options.ThinkingBudgets;
        Transport = options.Transport;
        MaxRetryDelayMs = options.MaxRetryDelayMs;
        ToolExecution = options.ToolExecution;
    }

    private readonly AgentState _state;

    /// <summary>Current mutable agent state.</summary>
    public AgentState State => _state;

    /// <summary>Session identifier forwarded to providers.</summary>
    public string? SessionId { get; set; }

    /// <summary>Per-level thinking budgets forwarded to providers.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; set; }

    /// <summary>Preferred provider transport forwarded to providers.</summary>
    public string? Transport { get; set; }

    /// <summary>Maximum provider-requested retry delay.</summary>
    public int? MaxRetryDelayMs { get; set; }

    /// <summary>Tool scheduling strategy for multi-call assistant messages.</summary>
    public ToolExecutionMode ToolExecution { get; set; }

    /// <summary>Active cancellation token, if a run is in progress.</summary>
    public CancellationToken? Signal => _activeRun?.Controller.Token;

    /// <summary>Subscribes to lifecycle events. Listener callbacks are awaited in order.</summary>
    public IDisposable Subscribe(AgentEventSink listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>Subscribes a synchronous event listener.</summary>
    public IDisposable Subscribe(Action<AgentEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Subscribe((@event, _) =>
        {
            listener(@event);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Queues a steering message for the next turn boundary.</summary>
    public void Steer(Message message) => _steeringQueue.Enqueue(message);

    /// <summary>Queues a follow-up message after the agent would otherwise stop.</summary>
    public void FollowUp(Message message) => _followUpQueue.Enqueue(message);

    /// <summary>Controls how steering messages are drained.</summary>
    public QueueMode SteeringMode
    {
        get => _steeringQueue.Mode;
        set => _steeringQueue.Mode = value;
    }

    /// <summary>Controls how follow-up messages are drained.</summary>
    public QueueMode FollowUpMode
    {
        get => _followUpQueue.Mode;
        set => _followUpQueue.Mode = value;
    }

    /// <summary>Removes all steering messages.</summary>
    public void ClearSteeringQueue() => _steeringQueue.Clear();

    /// <summary>Removes all follow-up messages.</summary>
    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    /// <summary>Removes all queued messages.</summary>
    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    /// <summary>Returns true when either queue contains a message.</summary>
    public bool HasQueuedMessages() => _steeringQueue.HasItems || _followUpQueue.HasItems;

    /// <summary>Aborts the active run.</summary>
    public void Abort() => _activeRun?.Controller.Cancel();

    /// <summary>Waits until the active run and all agent-end listeners settle.</summary>
    public Task WaitForIdleAsync() => _activeRun?.Completion.Task ?? Task.CompletedTask;

    /// <summary>Clears transcript/runtime state and queues.</summary>
    public void Reset()
    {
        EnsureIdle("Agent is already processing. Wait for completion before resetting.");
        _state.Messages = [];
        _state.IsStreaming = false;
        _state.StreamingMessage = null;
        _state.PendingToolCalls = new HashSet<string>(StringComparer.Ordinal);
        _state.ErrorMessage = null;
        ClearAllQueues();
    }

    /// <summary>Starts a text prompt.</summary>
    public Task PromptAsync(string input, IReadOnlyList<ImageContent>? images = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var content = new List<ContentBlock> { new TextContent(input) };
        if (images is not null)
        {
            content.AddRange(images);
        }

        var prompt = images is { Count: > 0 }
            ? new UserMessage(content, Now())
            : UserMessage.Text(input, Now());
        return PromptAsync(prompt, cancellationToken);
    }

    /// <summary>Starts a single-message prompt.</summary>
    public Task PromptAsync(Message message, CancellationToken cancellationToken = default) =>
        PromptAsync([message], cancellationToken);

    /// <summary>Starts a batch prompt.</summary>
    public async Task PromptAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        EnsureIdle("Agent is already processing a prompt. Use Steer() or FollowUp() to queue messages, or wait for completion.");
        await RunWithLifecycleAsync(
                signal => AgentLoop.RunAsync(
                    messages,
                    CreateContextSnapshot(),
                    CreateLoopConfig(),
                    _streamFunction,
                    ProcessEventAsync,
                    signal),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Continues from a user or tool-result tail.</summary>
    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdle("Agent is already processing. Wait for completion before continuing.");
        if (_state.Messages.Count == 0)
        {
            throw new InvalidOperationException("No messages to continue from");
        }

        var last = _state.Messages[_state.Messages.Count - 1];

        if (last is AssistantMessage)
        {
            var steering = _steeringQueue.Drain();
            if (steering.Count > 0)
            {
                await RunPromptBatchAsync(steering, true, cancellationToken).ConfigureAwait(false);
                return;
            }

            var followUps = _followUpQueue.Drain();
            if (followUps.Count > 0)
            {
                await RunPromptBatchAsync(followUps, false, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        await RunWithLifecycleAsync(
                signal => AgentLoop.RunContinuationAsync(
                    CreateContextSnapshot(),
                    CreateLoopConfig(),
                    _streamFunction,
                    ProcessEventAsync,
                    signal),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunPromptBatchAsync(
        IReadOnlyList<Message> messages,
        bool skipInitialSteeringPoll,
        CancellationToken cancellationToken)
    {
        await RunWithLifecycleAsync(
                signal => AgentLoop.RunAsync(
                    messages,
                    CreateContextSnapshot(),
                    CreateLoopConfig(skipInitialSteeringPoll),
                    _streamFunction,
                    ProcessEventAsync,
                    signal),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunWithLifecycleAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken)
    {
        EnsureIdle("Agent is already processing.");
        using var controller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveRun(controller);
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Agent is already processing.");
            }

            _activeRun = active;
        }

        _state.IsStreaming = true;
        _state.StreamingMessage = null;
        _state.ErrorMessage = null;
        try
        {
            await run(controller.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await HandleRunFailureAsync(error, controller.IsCancellationRequested, controller.Token).ConfigureAwait(false);
        }
        finally
        {
            _state.IsStreaming = false;
            _state.StreamingMessage = null;
            _state.PendingToolCalls = new HashSet<string>(StringComparer.Ordinal);
            active.Completion.TrySetResult();
            lock (_gate)
            {
                if (ReferenceEquals(_activeRun, active))
                {
                    _activeRun = null;
                }
            }
        }
    }

    private async Task HandleRunFailureAsync(Exception error, bool aborted, CancellationToken cancellationToken)
    {
        var failure = new AssistantMessage
        {
            Content = [new TextContent(string.Empty)],
            Api = _state.Model.Api,
            Provider = _state.Model.Provider,
            Model = _state.Model.Id,
            Usage = new Usage(),
            StopReason = aborted ? StopReasons.Aborted : StopReasons.Error,
            ErrorMessage = error.Message,
            Timestamp = Now(),
        };
        await ProcessEventAsync(new MessageStartEvent(failure), cancellationToken).ConfigureAwait(false);
        await ProcessEventAsync(new MessageEndEvent(failure), cancellationToken).ConfigureAwait(false);
        await ProcessEventAsync(new TurnEndEvent(failure, []), cancellationToken).ConfigureAwait(false);
        await ProcessEventAsync(new AgentEndEvent([failure]), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProcessEventAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case MessageStartEvent messageStart:
                _state.StreamingMessage = messageStart.Message;
                break;
            case MessageUpdateEvent messageUpdate:
                _state.StreamingMessage = messageUpdate.Message;
                break;
            case MessageEndEvent messageEnd:
                _state.StreamingMessage = null;
                _state.Messages = [.. _state.Messages, messageEnd.Message];
                break;
            case ToolExecutionStartEvent toolStart:
                _state.PendingToolCalls = new HashSet<string>(_state.PendingToolCalls, StringComparer.Ordinal)
                {
                    toolStart.ToolCallId,
                };
                break;
            case ToolExecutionEndEvent toolEnd:
                var pending = new HashSet<string>(_state.PendingToolCalls, StringComparer.Ordinal);
                pending.Remove(toolEnd.ToolCallId);
                _state.PendingToolCalls = pending;
                break;
            case TurnEndEvent turnEnd when turnEnd.Message.ErrorMessage is not null:
                _state.ErrorMessage = turnEnd.Message.ErrorMessage;
                break;
            case AgentEndEvent:
                _state.StreamingMessage = null;
                break;
        }

        var listeners = ListenerSnapshot();
        foreach (var listener in listeners)
        {
            await listener(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private AgentLoopConfig CreateLoopConfig(bool skipInitialSteeringPoll = false)
    {
        var skip = skipInitialSteeringPoll;
        return new AgentLoopConfig
        {
            Model = _state.Model,
            Reasoning = _state.ThinkingLevel == ThinkingLevels.Off ? null : _state.ThinkingLevel,
            SessionId = SessionId,
            OnPayload = _onPayload,
            OnResponse = _onResponse,
            ThinkingBudgets = ThinkingBudgets,
            Transport = Transport,
            MaxRetryDelayMs = MaxRetryDelayMs,
            ToolExecution = ToolExecution,
            ConvertToLlm = _convertToLlm,
            TransformContext = _transformContext,
            GetApiKey = _getApiKey,
            BeforeToolCall = _beforeToolCall,
            AfterToolCall = _afterToolCall,
            ShouldStopAfterTurn = _shouldStopAfterTurn is null
                ? null
                : async context => await _shouldStopAfterTurn(context, Signal ?? default).ConfigureAwait(false),
            PrepareNextTurn = _prepareNextTurn is null
                ? null
                : async context => await _prepareNextTurn(context, Signal ?? default).ConfigureAwait(false),
            GetSteeringMessages = () =>
            {
                if (skip)
                {
                    skip = false;
                    return new ValueTask<IReadOnlyList<Message>>([]);
                }

                return new ValueTask<IReadOnlyList<Message>>(_steeringQueue.Drain());
            },
            GetFollowUpMessages = () => new ValueTask<IReadOnlyList<Message>>(_followUpQueue.Drain()),
        };
    }

    private AgentContext CreateContextSnapshot() => new()
    {
        SystemPrompt = _state.SystemPrompt,
        Messages = _state.Messages.ToArray(),
        Tools = _state.Tools.ToArray(),
    };

    private void EnsureIdle(string message)
    {
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private AgentEventSink[] ListenerSnapshot()
    {
        lock (_gate)
        {
            return _listeners.ToArray();
        }
    }

    private static AgentState CopyState(AgentState? initial)
    {
        if (initial is null)
        {
            return new AgentState();
        }

        return new AgentState
        {
            SystemPrompt = initial.SystemPrompt,
            Model = initial.Model,
            ThinkingLevel = initial.ThinkingLevel,
            Tools = initial.Tools.ToArray(),
            Messages = initial.Messages.ToArray(),
        };
    }

    private static ValueTask<IReadOnlyList<Message>> DefaultConvertToLlm(IReadOnlyList<Message> messages) =>
        new(messages.Where(static message => message is UserMessage or AssistantMessage or ToolResultMessage).ToArray());

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class ActiveRun(CancellationTokenSource controller)
    {
        public CancellationTokenSource Controller { get; } = controller;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }

    private sealed class PendingMessageQueue(QueueMode mode)
    {
        private readonly List<Message> _messages = [];

        public QueueMode Mode { get; set; } = mode;

        public bool HasItems => _messages.Count > 0;

        public void Enqueue(Message message)
        {
            ArgumentNullException.ThrowIfNull(message);
            _messages.Add(message);
        }

        public List<Message> Drain()
        {
            if (Mode == QueueMode.All)
            {
                var result = _messages.ToList();
                _messages.Clear();
                return result;
            }

            if (_messages.Count == 0)
            {
                return [];
            }

            var first = _messages[0];
            _messages.RemoveAt(0);
            return [first];
        }

        public void Clear() => _messages.Clear();
    }
}
