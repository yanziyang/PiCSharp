namespace Pi.AgentCore.Harness;

/// <summary>Known harness lifecycle event discriminators.</summary>
public static class HarnessEventTypes
{
    /// <summary>Operation started.</summary>
    public const string RunStart = "run_start";

    /// <summary>Operation ended.</summary>
    public const string RunEnd = "run_end";
}

/// <summary>Base type for harness lifecycle events.</summary>
public abstract record HarnessEvent
{
    /// <summary>Event discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Event emitted when a lane operation starts.</summary>
public sealed record RunStartEvent : HarnessEvent
{
    /// <summary>Event discriminator.</summary>
    public override string Type => HarnessEventTypes.RunStart;

    /// <summary>Owning lane.</summary>
    public required string Lane { get; init; }

    /// <summary>Operation identifier.</summary>
    public required string RunId { get; init; }
}

/// <summary>Event emitted when a lane operation ends.</summary>
public sealed record RunEndEvent : HarnessEvent
{
    /// <summary>Event discriminator.</summary>
    public override string Type => HarnessEventTypes.RunEnd;

    /// <summary>Owning lane.</summary>
    public required string Lane { get; init; }

    /// <summary>Operation identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Operation outcome.</summary>
    public required string Outcome { get; init; }

    /// <summary>Leaf entry after the operation.</summary>
    public required string LeafId { get; init; }
}

/// <summary>Subscription interface for passive future-event listeners.</summary>
public interface IEvents
{
    /// <summary>Registers a listener for one event type and returns its unsubscribe action.</summary>
    Action On(string type, Action<HarnessEvent> listener);

    /// <summary>Registers an asynchronous listener for one event type.</summary>
    Action On(string type, Func<HarnessEvent, Task> listener);
}

/// <summary>Snapshot plus buffered event stream used to observe a harness.</summary>
public sealed class WatchHandle<TSnapshot>
{
    private readonly Action<Action<HarnessEvent>> _start;
    private readonly Action _unsubscribe;

    internal WatchHandle(
        TSnapshot snapshot,
        Action<Action<HarnessEvent>> start,
        Action _unsubscribe)
    {
        Snapshot = snapshot;
        _start = start;
        this._unsubscribe = _unsubscribe;
    }

    /// <summary>State captured when the watch was created.</summary>
    public TSnapshot Snapshot { get; }

    /// <summary>Starts receiving buffered and future events in order.</summary>
    public void Start(Action<HarnessEvent> listener) => _start(listener);

    /// <summary>Unsubscribes and discards buffered events.</summary>
    public void Unsubscribe() => _unsubscribe();
}

/// <summary>In-process event bus with passive subscriptions and snapshot watches.</summary>
public sealed class HarnessEventBus : IEvents
{
    private readonly Dictionary<string, List<Action<HarnessEvent>>> _listeners = new(StringComparer.Ordinal);
    private readonly List<Action<HarnessEvent>> _watchListeners = [];

    /// <inheritdoc />
    public Action On(string type, Action<HarnessEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(listener);
        if (!_listeners.TryGetValue(type, out var listeners))
        {
            listeners = [];
            _listeners[type] = listeners;
        }

        listeners.Add(listener);
        return () =>
        {
            if (!_listeners.TryGetValue(type, out var current))
            {
                return;
            }

            current.Remove(listener);
            if (current.Count == 0)
            {
                _listeners.Remove(type);
            }
        };
    }

    /// <inheritdoc />
    public Action On(string type, Func<HarnessEvent, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return On(type, eventValue => _ = listener(eventValue));
    }

    /// <summary>Registers a strongly typed synchronous listener.</summary>
    public Action On<TEvent>(string type, Action<TEvent> listener) where TEvent : HarnessEvent =>
        On(type, eventValue =>
        {
            if (eventValue is TEvent typed)
            {
                listener(typed);
            }
        });

    /// <summary>Registers a strongly typed asynchronous listener.</summary>
    public Action On<TEvent>(string type, Func<TEvent, Task> listener) where TEvent : HarnessEvent =>
        On(type, eventValue =>
        {
            if (eventValue is TEvent typed)
            {
                _ = listener(typed);
            }
        });

    /// <summary>Publishes an event to matching listeners and active watches.</summary>
    public void Emit(HarnessEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        foreach (var listener in _listeners.TryGetValue(@event.Type, out var listeners) ? listeners.ToArray() : [])
        {
            listener(@event);
        }

        foreach (var listener in _watchListeners.ToArray())
        {
            listener(@event);
        }
    }

    /// <summary>Creates a watch whose snapshot is captured before later events are delivered.</summary>
    public WatchHandle<TSnapshot> Watch<TSnapshot>(Func<TSnapshot> captureSnapshot)
    {
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        Action<HarnessEvent>? listener = null;
        var buffered = new List<HarnessEvent>();
        var active = true;
        Action<HarnessEvent> receive = @event =>
        {
            if (!active)
            {
                return;
            }

            if (listener is null)
            {
                buffered.Add(@event);
            }
            else
            {
                listener(@event);
            }
        };

        _watchListeners.Add(receive);
        var snapshot = captureSnapshot();
        return new WatchHandle<TSnapshot>(
            snapshot,
            nextListener =>
            {
                ArgumentNullException.ThrowIfNull(nextListener);
                while (active && buffered.Count > 0)
                {
                    var pending = buffered.ToArray();
                    buffered.Clear();
                    foreach (var @event in pending)
                    {
                        nextListener(@event);
                    }
                }

                if (active)
                {
                    listener = nextListener;
                }
            },
            () =>
            {
                active = false;
                _watchListeners.Remove(receive);
                buffered.Clear();
            });
    }
}
