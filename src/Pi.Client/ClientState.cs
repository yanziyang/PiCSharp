using Pi.Protocol;

namespace Pi.Client;

/// <summary>Materialized snapshots and listener state maintained by <see cref="PiClient"/>.</summary>
public sealed class ClientState
{
    private readonly Dictionary<string, SessionSnapshot> _sessionSnapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attachedSessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<Action<ServerSnapshot>> _snapshotListeners = [];
    private readonly HashSet<Action<ServerEvent>> _eventListeners = [];
    private readonly Dictionary<string, HashSet<Action<SessionSnapshot>>> _sessionSnapshotListeners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Action<ServerEvent>>> _sessionEventListeners = new(StringComparer.Ordinal);
    private readonly Action<Exception>? _onListenerError;
    private ServerSnapshot? _snapshot;

    /// <summary>Initializes client state with an optional subscriber-error callback.</summary>
    public ClientState(Action<Exception>? onListenerError = null)
    {
        _onListenerError = onListenerError;
    }

    /// <summary>Most recent authoritative server snapshot, if a handshake completed.</summary>
    public ServerSnapshot? Snapshot => _snapshot;

    /// <summary>Clears snapshots and attachment state for a fresh connection.</summary>
    public void Reset()
    {
        _snapshot = null;
        _sessionSnapshots.Clear();
        _attachedSessionIds.Clear();
    }

    /// <summary>Clears only attachment flags after a connection terminates.</summary>
    public void ClearAttachments() => _attachedSessionIds.Clear();

    /// <summary>Releases all snapshot and listener state.</summary>
    public void Dispose()
    {
        Reset();
        _snapshotListeners.Clear();
        _eventListeners.Clear();
        _sessionSnapshotListeners.Clear();
        _sessionEventListeners.Clear();
    }

    /// <summary>Returns the latest snapshot for a session.</summary>
    public SessionSnapshot? GetSessionSnapshot(string sessionId) =>
        _sessionSnapshots.GetValueOrDefault(sessionId);

    /// <summary>Returns whether the server currently considers a session attached.</summary>
    public bool IsSessionAttached(string sessionId) => _attachedSessionIds.Contains(sessionId);

    /// <summary>Removes a cached session snapshot and returns the previous value.</summary>
    public SessionSnapshot? ForgetSessionSnapshot(string sessionId)
    {
        if (!_sessionSnapshots.Remove(sessionId, out var previous))
        {
            return null;
        }

        return previous;
    }

    /// <summary>Restores a snapshot only when no newer local snapshot is known.</summary>
    public void RestoreSessionSnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_sessionSnapshots.ContainsKey(snapshot.Id))
        {
            _sessionSnapshots[snapshot.Id] = snapshot;
        }
    }

    /// <summary>Subscribes to authoritative server snapshots.</summary>
    public Unsubscribe Subscribe(Action<ServerSnapshot> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _snapshotListeners.Add(listener);
        return () => _snapshotListeners.Remove(listener);
    }

    /// <summary>Subscribes to all server events.</summary>
    public Unsubscribe OnEvent(Action<ServerEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _eventListeners.Add(listener);
        return () => _eventListeners.Remove(listener);
    }

    /// <summary>Subscribes to snapshots for one session.</summary>
    public Unsubscribe SubscribeSession(string sessionId, Action<SessionSnapshot> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!_sessionSnapshotListeners.TryGetValue(sessionId, out var listeners))
        {
            listeners = [];
            _sessionSnapshotListeners.Add(sessionId, listeners);
        }

        listeners.Add(listener);
        return () =>
        {
            if (!_sessionSnapshotListeners.TryGetValue(sessionId, out var current))
            {
                return;
            }

            current.Remove(listener);
            if (current.Count == 0)
            {
                _sessionSnapshotListeners.Remove(sessionId);
            }
        };
    }

    /// <summary>Subscribes to events associated with one session.</summary>
    public Unsubscribe OnSessionEvent(string sessionId, Action<ServerEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!_sessionEventListeners.TryGetValue(sessionId, out var listeners))
        {
            listeners = [];
            _sessionEventListeners.Add(sessionId, listeners);
        }

        listeners.Add(listener);
        return () =>
        {
            if (!_sessionEventListeners.TryGetValue(sessionId, out var current))
            {
                return;
            }

            current.Remove(listener);
            if (current.Count == 0)
            {
                _sessionEventListeners.Remove(sessionId);
            }
        };
    }

    /// <summary>Applies a successful command result to the cached authoritative state.</summary>
    public void ApplyResult(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result)
        {
            case ListResult:
                return;
            case DetachResult detach:
                _attachedSessionIds.Remove(detach.SessionId);
                if (_sessionSnapshots.TryGetValue(detach.SessionId, out var snapshot))
                {
                    ApplySessionSnapshot(snapshot with { Attached = false }, force: true);
                }

                return;
            case CreateResult create:
                ApplySessionSnapshot(create.Session);
                return;
            case AttachResult attach:
                ApplySessionSnapshot(attach.Session);
                return;
            case PromptResult prompt:
                ApplySessionSnapshot(prompt.Session);
                return;
            case SteerResult steer:
                ApplySessionSnapshot(steer.Session);
                return;
            case AbortResult abort:
                ApplySessionSnapshot(abort.Session);
                return;
            case SetModelResult model:
                ApplySessionSnapshot(model.Session);
                return;
            case SetThinkingResult thinking:
                ApplySessionSnapshot(thinking.Session);
                return;
            default:
                throw new ArgumentException("Unknown command result", nameof(result));
        }
    }

    /// <summary>Applies a server event and notifies global and session listeners.</summary>
    public void ApplyEvent(ServerEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        switch (@event)
        {
            case ServerSnapshotEvent server:
                ApplyServerSnapshot(server.Snapshot);
                break;
            case SessionSnapshotEvent session:
                ApplySessionSnapshot(session.Snapshot);
                break;
            case SessionRemovedEvent removed:
                _sessionSnapshots.Remove(removed.SessionId);
                _attachedSessionIds.Remove(removed.SessionId);
                break;
        }

        Notify(_eventListeners, @event);
        var sessionId = GetEventSessionId(@event);
        if (sessionId is not null && _sessionEventListeners.TryGetValue(sessionId, out var listeners))
        {
            Notify(listeners, @event);
        }
    }

    /// <summary>Applies a server snapshot when its revision is not stale.</summary>
    public void ApplyServerSnapshot(ServerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_snapshot is not null && snapshot.Revision < _snapshot.Revision)
        {
            return;
        }

        _snapshot = snapshot;
        Notify(_snapshotListeners, snapshot);
    }

    private void ApplySessionSnapshot(SessionSnapshot snapshot, bool force = false)
    {
        var current = _sessionSnapshots.GetValueOrDefault(snapshot.Id);
        if (!force && current is not null && snapshot.Revision < current.Revision)
        {
            return;
        }

        _sessionSnapshots[snapshot.Id] = snapshot;
        if (snapshot.Attached)
        {
            _attachedSessionIds.Add(snapshot.Id);
        }
        else
        {
            _attachedSessionIds.Remove(snapshot.Id);
        }

        if (_sessionSnapshotListeners.TryGetValue(snapshot.Id, out var listeners))
        {
            Notify(listeners, snapshot);
        }
    }

    private void Notify<T>(IEnumerable<Action<T>> listeners, T value)
    {
        foreach (var listener in listeners.ToArray())
        {
            try
            {
                listener(value);
            }
            catch (Exception error)
            {
                ReportListenerError(error);
            }
        }
    }

    private void ReportListenerError(Exception error)
    {
        if (_onListenerError is null)
        {
            return;
        }

        try
        {
            _onListenerError(error);
        }
        catch
        {
            // Diagnostics must not affect protocol or client state.
        }
    }

    private static string? GetEventSessionId(ServerEvent @event) => @event switch
    {
        SessionSnapshotEvent session => session.Snapshot.Id,
        SessionProgressEvent progress => progress.SessionId,
        SessionRemovedEvent removed => removed.SessionId,
        _ => null,
    };
}
