using Pi.Protocol;

namespace Pi.Server;

internal sealed class LiveSessionManager
{
    private sealed class LiveSession
    {
        internal required string Id { get; init; }
        internal required IPiSessionRuntime Runtime { get; init; }
        internal HashSet<ConnectionState> Connections { get; } = [];
        internal Unsubscribe Unsubscribe { get; set; } = static () => { };
        internal int OperationCount { get; set; }
        internal bool Ready { get; set; }
        internal bool Terminal { get; set; }
        internal Task? Disposing { get; set; }
    }

    private readonly IPiServerService _service;
    private readonly Func<bool> _isClosing;
    private readonly Func<ConnectionState, EventEnvelope, Task<bool>> _sendMessage;
    private readonly Func<IByteConnection, Task> _closeConnection;
    private readonly Func<ConnectionState, Task> _disconnect;
    private readonly Action _broadcastServerSnapshot;
    private readonly Action<Exception> _reportError;
    private readonly object _gate = new();
    private readonly Dictionary<string, LiveSession> _liveSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LiveSession>> _openingSessions = new(StringComparer.Ordinal);

    internal LiveSessionManager(
        IPiServerService service,
        Func<bool> isClosing,
        Func<ConnectionState, EventEnvelope, Task<bool>> sendMessage,
        Func<IByteConnection, Task> closeConnection,
        Func<ConnectionState, Task> disconnect,
        Action broadcastServerSnapshot,
        Action<Exception> reportError)
    {
        _service = service;
        _isClosing = isClosing;
        _sendMessage = sendMessage;
        _closeConnection = closeConnection;
        _disconnect = disconnect;
        _broadcastServerSnapshot = broadcastServerSnapshot;
        _reportError = reportError;
    }

    internal async Task<CommandResult> ExecuteCommandAsync(
        ConnectionState connection,
        Command command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            ListCommand => new ListResult(await ListMetadataAsync(cancellationToken).ConfigureAwait(false)),
            CreateCommand create => await CreateAsync(connection, create, cancellationToken).ConfigureAwait(false),
            AttachCommand attach => await AttachExistingAsync(connection, attach, cancellationToken).ConfigureAwait(false),
            DetachCommand detach => await DetachAsync(connection, detach, cancellationToken).ConfigureAwait(false),
            PromptCommand prompt => await PromptAsync(connection, prompt, cancellationToken).ConfigureAwait(false),
            SteerCommand steer => await SteerAsync(connection, steer, cancellationToken).ConfigureAwait(false),
            AbortCommand abort => await AbortAsync(connection, abort, cancellationToken).ConfigureAwait(false),
            SetModelCommand setModel => await SetModelAsync(connection, setModel, cancellationToken).ConfigureAwait(false),
            SetThinkingCommand setThinking => await SetThinkingAsync(connection, setThinking, cancellationToken).ConfigureAwait(false),
            _ => throw new ProtocolValidationError("Invalid client protocol command"),
        };
    }

    internal async Task DisconnectAsync(ConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        LiveSession[] sessions;
        lock (_gate)
        {
            sessions = connection.SessionIdsInternal
                .Select(id => _liveSessions.GetValueOrDefault(id))
                .Where(static session => session is not null)
                .Cast<LiveSession>()
                .ToArray();
            connection.SessionIdsInternal.Clear();
            foreach (var session in sessions)
            {
                session.Connections.Remove(connection);
            }
        }

        await Task.WhenAll(sessions.Select(async session =>
        {
            try
            {
                await MaybeDisposeAsync(session).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _reportError(error);
            }
        })).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<SessionMetadata>> ListMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await _service.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        LiveSession[] liveSessions;
        lock (_gate)
        {
            liveSessions = _liveSessions.Values.Where(static session => session.Disposing is null).ToArray();
        }

        var liveSnapshots = await Task.WhenAll(
            liveSessions.Select(async live =>
                (live.Id, Snapshot: await NormalizedSnapshotAsync(live, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);
        var liveById = liveSnapshots.ToDictionary(static value => value.Id, StringComparer.Ordinal);
        var metadata = new List<SessionMetadata>(stored.Count + liveById.Count);
        foreach (var item in stored)
        {
            if (!liveById.Remove(item.Id, out var live))
            {
                metadata.Add(item);
                continue;
            }

            metadata.Add(item with
            {
                UpdatedAt = live.Snapshot.UpdatedAt,
                SessionName = live.Snapshot.Name,
                Cwd = live.Snapshot.Cwd,
            });
        }

        metadata.AddRange(liveById.Values.Select(static value => ToMetadata(value.Snapshot)));
        return metadata;
    }

    internal async Task CloseAsync()
    {
        Task<LiveSession>[] openings;
        LiveSession[] sessions;
        lock (_gate)
        {
            openings = _openingSessions.Values.ToArray();
        }

        var openingResults = await Task.WhenAll(openings.Select(ObserveOpeningAsync)).ConfigureAwait(false);
        _ = openingResults;
        lock (_gate)
        {
            sessions = _liveSessions.Values.ToArray();
            _liveSessions.Clear();
        }
        await Task.WhenAll(sessions.Select(async live =>
        {
            if (live.Disposing is not null)
            {
                await live.Disposing.ConfigureAwait(false);
                return;
            }

            live.Unsubscribe();
            await live.Runtime.DisposeAsync().ConfigureAwait(false);
        })).ConfigureAwait(false);
    }

    private async Task<CommandResult> CreateAsync(
        ConnectionState connection,
        CreateCommand command,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString();
        var options = new CreateSessionOptions(id, command.Cwd, command.Name, command.Model, command.ThinkingLevel);
        var live = await AcquireAsync(
            id,
            () => _service.CreateSessionAsync(options, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await AttachAsync(connection, live).ConfigureAwait(false);
        var session = ForConnection(
            await BroadcastSnapshotAsync(live, cancellationToken).ConfigureAwait(false),
            connection);
        _broadcastServerSnapshot();
        return new CreateResult(session);
    }

    private async Task<CommandResult> AttachExistingAsync(
        ConnectionState connection,
        AttachCommand command,
        CancellationToken cancellationToken)
    {
        var live = await AcquireAsync(
            command.SessionId,
            () => _service.OpenSessionAsync(command.SessionId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await AttachAsync(connection, live).ConfigureAwait(false);
        var session = ForConnection(
            await BroadcastSnapshotAsync(live, cancellationToken).ConfigureAwait(false),
            connection);
        _broadcastServerSnapshot();
        return new AttachResult(session);
    }

    private async Task<CommandResult> DetachAsync(
        ConnectionState connection,
        DetachCommand command,
        CancellationToken cancellationToken)
    {
        LiveSession? live = null;
        var detached = connection.SessionIdsInternal.Remove(command.SessionId);
        if (detached)
        {
            lock (_gate)
            {
                live = _liveSessions.GetValueOrDefault(command.SessionId);
                live?.Connections.Remove(connection);
            }

            if (live is not null)
            {
                if (live.Connections.Count > 0 && !live.Terminal && live.Disposing is null)
                {
                    await BroadcastSnapshotAsync(live, cancellationToken).ConfigureAwait(false);
                }

                await MaybeDisposeAsync(live).ConfigureAwait(false);
            }

            _broadcastServerSnapshot();
        }

        return new DetachResult(command.SessionId);
    }

    private async Task<CommandResult> PromptAsync(
        ConnectionState connection,
        PromptCommand command,
        CancellationToken cancellationToken)
    {
        var live = RequireAttached(connection, command.SessionId);
        var session = await RunOperationAsync(
            connection,
            live,
            () => live.Runtime.PromptAsync(new PromptInput(command.Text), cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new PromptResult(session);
    }

    private async Task<CommandResult> SteerAsync(
        ConnectionState connection,
        SteerCommand command,
        CancellationToken cancellationToken)
    {
        var live = RequireAttached(connection, command.SessionId);
        var session = await RunOperationAsync(
            connection,
            live,
            () => live.Runtime.SteerAsync(new SteerInput(command.Text), cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new SteerResult(session);
    }

    private async Task<CommandResult> AbortAsync(
        ConnectionState connection,
        AbortCommand command,
        CancellationToken cancellationToken)
    {
        var live = RequireAttached(connection, command.SessionId);
        var session = await RunOperationAsync(
            connection,
            live,
            () => live.Runtime.AbortAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new AbortResult(session);
    }

    private async Task<CommandResult> SetModelAsync(
        ConnectionState connection,
        SetModelCommand command,
        CancellationToken cancellationToken)
    {
        var live = RequireAttached(connection, command.SessionId);
        var session = await RunOperationAsync(
            connection,
            live,
            () => live.Runtime.SetModelAsync(command.Model, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new SetModelResult(session);
    }

    private async Task<CommandResult> SetThinkingAsync(
        ConnectionState connection,
        SetThinkingCommand command,
        CancellationToken cancellationToken)
    {
        var live = RequireAttached(connection, command.SessionId);
        var session = await RunOperationAsync(
            connection,
            live,
            () => live.Runtime.SetThinkingAsync(command.ThinkingLevel, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new SetThinkingResult(session);
    }

    private async Task<SessionSnapshot> RunOperationAsync(
        ConnectionState connection,
        LiveSession live,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        live.OperationCount++;
        try
        {
            await operation().ConfigureAwait(false);
            return ForConnection(
                await BroadcastSnapshotAsync(live, cancellationToken).ConfigureAwait(false),
                connection);
        }
        finally
        {
            live.OperationCount--;
            ScheduleMaybeDispose(live);
        }
    }

    private async Task<LiveSession> AcquireAsync(
        string id,
        Func<Task<IPiSessionRuntime>> acquireRuntime,
        CancellationToken cancellationToken)
    {
        for (; ; )
        {
            LiveSession? existing;
            Task<LiveSession>? opening;
            Task<LiveSession>? pending = null;
            lock (_gate)
            {
                existing = _liveSessions.GetValueOrDefault(id);
                opening = _openingSessions.GetValueOrDefault(id);
                if (existing is null && opening is null)
                {
                    pending = CreateAsync(id, acquireRuntime);
                    _openingSessions[id] = pending;
                    opening = pending;
                }
            }

            if (existing is not null)
            {
                if (existing.Terminal)
                {
                    throw new PiServerError(
                        ProtocolErrorCode.SessionLocked,
                        $"Session runtime is terminating: {id}");
                }

                if (existing.Disposing is not null)
                {
                    await existing.Disposing.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return existing;
            }

            try
            {
                return await opening!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_openingSessions.GetValueOrDefault(id) == opening)
                    {
                        _openingSessions.Remove(id);
                    }
                }
            }
        }
    }

    private async Task<LiveSession> CreateAsync(string id, Func<Task<IPiSessionRuntime>> acquireRuntime)
    {
        var runtime = await acquireRuntime().ConfigureAwait(false);
        if (_isClosing())
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("PiServer closed while acquiring a session runtime");
        }

        LiveSession? live = null;
        try
        {
            var snapshot = await runtime.GetSnapshotAsync().ConfigureAwait(false);
            if (snapshot.Id != id)
            {
                throw new PiServerError(
                    ProtocolErrorCode.InvalidRequest,
                    $"Service returned session {snapshot.Id} for server-assigned session {id}");
            }

            live = new LiveSession { Id = id, Runtime = runtime };
            live.Unsubscribe = runtime.Subscribe(runtimeEvent => HandleRuntimeEvent(live, runtimeEvent));
            lock (_gate)
            {
                _liveSessions[id] = live;
                live.Ready = true;
            }

            return live;
        }
        catch
        {
            live?.Unsubscribe();
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeError)
            {
                _reportError(disposeError);
            }

            throw;
        }
    }

    private void HandleRuntimeEvent(LiveSession live, PiSessionRuntimeEvent runtimeEvent)
    {
        if (runtimeEvent is PiSessionRuntimeEvent.Error error)
        {
            _ = TerminateAsync(live, error.Value).ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted && completed.Exception is not null)
                    {
                        _reportError(completed.Exception.GetBaseException());
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        if (runtimeEvent is PiSessionRuntimeEvent.Progress progress)
        {
            var envelope = new EventEnvelope(new SessionProgressEvent(live.Id, progress.Value));
            foreach (var connection in live.Connections.ToArray())
            {
                FireAndReport(_sendMessage(connection, envelope));
            }
        }
        else
        {
            FireAndReport(BroadcastSnapshotAsync(live));
        }

        ScheduleMaybeDispose(live);
    }

    private async Task TerminateAsync(LiveSession live, PiServerError error)
    {
        if (live.Terminal)
        {
            return;
        }

        live.Terminal = true;
        _reportError(error);
        live.Unsubscribe();
        var connections = live.Connections.ToArray();
        await Task.WhenAll(connections.Select(connection => _closeConnection(connection.Connection))).ConfigureAwait(false);
        await Task.WhenAll(connections.Select(_disconnect)).ConfigureAwait(false);
        await MaybeDisposeAsync(live).ConfigureAwait(false);
    }

    private static async Task<SessionSnapshot> NormalizedSnapshotAsync(
        LiveSession live,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await live.Runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Id != live.Id)
        {
            throw new PiServerError(
                ProtocolErrorCode.InvalidRequest,
                $"Runtime session ID changed from {live.Id} to {snapshot.Id}");
        }

        return snapshot with
        {
            Phase = live.Runtime.GetPhase(),
            Attached = live.Connections.Count > 0,
            Locked = true,
        };
    }

    private static SessionSnapshot ForConnection(SessionSnapshot snapshot, ConnectionState connection) =>
        snapshot with { Attached = connection.SessionIdsInternal.Contains(snapshot.Id) };

    private async Task<SessionSnapshot> BroadcastSnapshotAsync(
        LiveSession live,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await NormalizedSnapshotAsync(live, cancellationToken).ConfigureAwait(false);
        var envelope = new EventEnvelope(new SessionSnapshotEvent(snapshot));
        foreach (var connection in live.Connections.ToArray())
        {
            FireAndReport(_sendMessage(connection, envelope));
        }

        return snapshot;
    }

    private async Task AttachAsync(ConnectionState connection, LiveSession live)
    {
        if (connection.Disconnected ||
            connection.Stage != ConnectionStage.Ready ||
            connection.Connection.Closed)
        {
            await MaybeDisposeAsync(live).ConfigureAwait(false);
            throw new PiServerError(
                ProtocolErrorCode.InvalidRequest,
                "Connection closed while attaching to a session");
        }

        connection.SessionIdsInternal.Add(live.Id);
        live.Connections.Add(connection);
    }

    private LiveSession RequireAttached(ConnectionState connection, string sessionId)
    {
        if (!connection.SessionIdsInternal.Contains(sessionId))
        {
            throw new PiServerError(
                ProtocolErrorCode.InvalidRequest,
                $"Connection is not attached to session {sessionId}");
        }

        LiveSession? live;
        lock (_gate)
        {
            live = _liveSessions.GetValueOrDefault(sessionId);
        }

        if (live is null || live.Terminal || live.Disposing is not null)
        {
            throw new PiServerError(ProtocolErrorCode.NotFound, $"Session is not live: {sessionId}");
        }

        return live;
    }

    private void ScheduleMaybeDispose(LiveSession live)
    {
        FireAndReport(MaybeDisposeAsync(live));
    }

    private async Task MaybeDisposeAsync(LiveSession live)
    {
        if (_isClosing() ||
            !live.Ready ||
            live.Disposing is not null ||
            live.Connections.Count > 0 ||
            live.OperationCount > 0 ||
            (!live.Terminal && live.Runtime.GetPhase() != SessionPhase.Idle))
        {
            return;
        }

        lock (_gate)
        {
            if (live.Disposing is not null)
            {
                return;
            }

            live.Unsubscribe();
            live.Disposing = DisposeLiveAsync(live);
        }

        var disposing = live.Disposing;
        if (disposing is not null)
        {
            await disposing.ConfigureAwait(false);
        }
        if (!_isClosing())
        {
            _broadcastServerSnapshot();
        }
    }

    private async Task DisposeLiveAsync(LiveSession live)
    {
        try
        {
            await live.Runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_liveSessions.GetValueOrDefault(live.Id) == live)
                {
                    _liveSessions.Remove(live.Id);
                }
            }
        }
    }

    private async Task<LiveSession?> ObserveOpeningAsync(Task<LiveSession> opening)
    {
        try
        {
            await opening.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError(error);
        }

        return null;
    }

    private void FireAndReport(Task task)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    _reportError(completed.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static SessionMetadata ToMetadata(SessionSnapshot snapshot) => new(
        snapshot.Id,
        snapshot.CreatedAt,
        snapshot.UpdatedAt,
        SessionName: snapshot.Name,
        Cwd: snapshot.Cwd);
}
