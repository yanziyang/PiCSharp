using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using System.Threading.Channels;

namespace Pi.Ai;

/// <summary>Asynchronously iterable event stream with a separately awaitable final result.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "Preserves the upstream Pi event stream type name.")]
public class EventStream<TEvent, TResult> : IAsyncEnumerable<TEvent>
{
    private readonly Channel<TEvent> _channel = Channel.CreateUnbounded<TEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Func<TEvent, bool> _isComplete;
    private readonly Func<TEvent, TResult> _extractResult;
    private readonly TaskCompletionSource<TResult> _finalResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _done;

    /// <summary>Creates an event stream with a completion predicate and result extractor.</summary>
    public EventStream(Func<TEvent, bool> isComplete, Func<TEvent, TResult> extractResult)
    {
        _isComplete = isComplete ?? throw new ArgumentNullException(nameof(isComplete));
        _extractResult = extractResult ?? throw new ArgumentNullException(nameof(extractResult));
    }

    /// <summary>Pushes an event unless the stream has already reached its terminal state.</summary>
    public void Push(TEvent @event)
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }

            if (_isComplete(@event))
            {
                _done = true;
                _finalResult.TrySetResult(_extractResult(@event));
            }

            _channel.Writer.TryWrite(@event);
        }
    }

    /// <summary>Ends the stream and optionally supplies its final result.</summary>
    public void End(TResult? result = default)
    {
        lock (_gate)
        {
            _done = true;
            if (result is not null)
            {
                _finalResult.TrySetResult(result);
            }

            _channel.Writer.TryComplete();
        }
    }

    /// <summary>The final result task.</summary>
    public Task<TResult> Result => _finalResult.Task;

    /// <inheritdoc />
    public IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
}

/// <summary>Event stream specialized for assistant message events.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "Preserves the upstream Pi event stream type name.")]
public sealed class AssistantMessageEventStream : EventStream<AssistantMessageEvent, AssistantMessage>
{
    /// <summary>Creates an assistant message event stream.</summary>
    public AssistantMessageEventStream()
        : base(
            static @event => @event is StreamDoneEvent or StreamErrorEvent,
            static @event => @event switch
            {
                StreamDoneEvent done => done.Message,
                StreamErrorEvent error => error.Error,
                _ => throw new InvalidOperationException("Unexpected event type for final result."),
            })
    {
    }
}

/// <summary>Creates an assistant message event stream for extension and provider code.</summary>
public static class AssistantMessageEventStreams
{
    /// <summary>Creates a new assistant message event stream.</summary>
    public static AssistantMessageEventStream Create() => new();
}
