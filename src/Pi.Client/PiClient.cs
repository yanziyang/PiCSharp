using Pi.Protocol;

namespace Pi.Client;

/// <summary>Client for the framed Pi protocol and its session lease API.</summary>
public sealed class PiClient : IAsyncDisposable
{
    private readonly PiClientOptions _options;
    private readonly Connection _connection;
    private readonly ClientState _state;
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sessionLeaseCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionLeaseToken> _exclusiveSessionLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sessionLeaseGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sessionAttachments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sessionDetachments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionCleanupRequired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sessionReconciliations = new(StringComparer.Ordinal);
    private readonly HashSet<Action<ConnectionStateChange>> _connectionStateListeners = [];
    private int _requestSequence;
    private bool _disposed;
    private Task? _disposeTask;

    /// <summary>Initializes a Pi client without opening a transport.</summary>
    public PiClient(PiClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = new ClientState(options.OnListenerError);
        _connection = new Connection(
            options.TransportFactory,
            options.MaxFrameLength,
            snapshot => _state.ApplyServerSnapshot(snapshot),
            message => HandleMessage(message),
            change => HandleConnectionStateChange(change));
    }

    /// <summary>Whether disposal has begun.</summary>
    public bool Disposed => _disposed;

    /// <summary>Current byte-transport and protocol-handshake state.</summary>
    public ConnectionState ConnectionState => _connection.State;

    /// <summary>Whether the protocol handshake is complete.</summary>
    public bool Connected => _connection.State == ConnectionState.Connected;

    /// <summary>Most recent authoritative server snapshot.</summary>
    public ServerSnapshot? Snapshot => _state.Snapshot;

    /// <summary>Initializes and connects a new client.</summary>
    public static async Task<PiClient> ConnectAsync(
        PiClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var client = new PiClient(options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a transport and completes after the server hello.</summary>
    public Task<ServerSnapshot> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_connection.State == ConnectionState.Disconnected)
        {
            _state.Reset();
        }

        return _connection.ConnectAsync().WaitAsync(cancellationToken);
    }

    /// <summary>Reconnects through a fresh transport factory result.</summary>
    public Task<ServerSnapshot> ReconnectAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(cancellationToken);

    /// <summary>Closes the active transport.</summary>
    public void Disconnect(string reason = "Client disconnected")
    {
        _connection.Disconnect(new PiDisconnectedError(reason));
    }

    /// <summary>Subscribes to authoritative server snapshots.</summary>
    public Unsubscribe Subscribe(Action<ServerSnapshot> listener)
    {
        ThrowIfDisposed();
        return _state.Subscribe(listener);
    }

    /// <summary>Subscribes to all server events.</summary>
    public Unsubscribe OnEvent(Action<ServerEvent> listener)
    {
        ThrowIfDisposed();
        return _state.OnEvent(listener);
    }

    /// <summary>Subscribes to connection state changes.</summary>
    public Unsubscribe OnConnectionStateChange(Action<ConnectionStateChange> listener)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(listener);
        _connectionStateListeners.Add(listener);
        return () => _connectionStateListeners.Remove(listener);
    }

    /// <summary>Lists durable sessions visible to the server.</summary>
    public async Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ListCommand(), cancellationToken).ConfigureAwait(false);
        return result is ListResult list
            ? list.Sessions
            : throw UnexpectedResult(result, "list");
    }

    /// <summary>Creates a session and acquires an exclusive lease for it.</summary>
    public async Task<SessionHandle> CreateSessionAsync(
        CreateSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var create = new CreateCommand(options?.Cwd, options?.Name, options?.Model, options?.ThinkingLevel);
        var result = await RequestAsync(create, cancellationToken).ConfigureAwait(false);
        if (result is not CreateResult created)
        {
            throw UnexpectedResult(result, "create");
        }

        var token = ReserveSessionLease(created.Session.Id, SessionLeaseMode.Exclusive);
        return CreateSessionHandle(created.Session.Id, token);
    }

    /// <summary>Attaches to an existing session with a shared lease.</summary>
    public Task<SessionHandle> AttachSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        AcquireSessionAsync(sessionId, new AcquireSessionOptions(SessionLeaseMode.Shared), cancellationToken);

    /// <summary>Acquires a shared or exclusive lease for an existing session.</summary>
    public async Task<SessionHandle> AcquireSessionAsync(
        string sessionId,
        AcquireSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        var token = ReserveSessionLease(sessionId, options.Mode);
        try
        {
            if (_sessionDetachments.TryGetValue(sessionId, out var detachment))
            {
                await detachment.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var reconciled = _sessionCleanupRequired.Contains(sessionId)
                ? await ReconcileSessionCleanupAsync(sessionId, cancellationToken).ConfigureAwait(false)
                : false;
            if (reconciled || !_state.IsSessionAttached(sessionId))
            {
                if (!_sessionAttachments.TryGetValue(sessionId, out var attachment))
                {
                    attachment = AttachSessionCoreAsync(sessionId);
                    _sessionAttachments.Add(sessionId, attachment);
                }

                try
                {
                    await attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (_sessionAttachments.TryGetValue(sessionId, out var current) && ReferenceEquals(current, attachment))
                    {
                        _sessionAttachments.Remove(sessionId);
                    }
                }
            }

            return CreateSessionHandle(sessionId, token);
        }
        catch
        {
            ReleaseSessionLease(sessionId, token);
            throw;
        }
    }

    /// <summary>Releases the client and closes any active transport.</summary>
    public Task DisposeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposeTask is not null)
        {
            return _disposeTask.WaitAsync(cancellationToken);
        }

        _disposed = true;
        var error = new PiClientDisposedError();
        RejectPendingRequests(error);
        _connection.Disconnect(error);
        _state.Dispose();
        InvalidateAllSessionLeases();
        _connectionStateListeners.Clear();
        _disposeTask = Task.CompletedTask;
        return _disposeTask.WaitAsync(cancellationToken);
    }

    /// <summary>Releases the client through <see cref="IAsyncDisposable"/>.</summary>
    public ValueTask DisposeAsync() => new(DisposeAsync(CancellationToken.None));

    private Task<CommandResult> RequestAsync(Command command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!Connected)
        {
            return Task.FromException<CommandResult>(new PiDisconnectedError());
        }

        return RequestCoreAsync(command).WaitAsync(cancellationToken);
    }

    private Task<CommandResult> RequestCoreAsync(Command command)
    {
        var id = $"request-{++_requestSequence}";
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests.Add(id, new PendingRequest(command, completion));
        try
        {
            var frame = ProtocolCodec.EncodeClientMessage(
                new RequestEnvelope(id, command),
                new FrameDecoderOptions { MaxFrameLength = _connection.MaxFrameLength });
            _connection.Send(frame);
        }
        catch (Exception error)
        {
            if (_pendingRequests.Remove(id))
            {
                completion.TrySetException(ClientErrorUtilities.ToException(error));
            }
        }

        return completion.Task;
    }

    private async Task AttachSessionCoreAsync(string sessionId)
    {
        var previous = _state.ForgetSessionSnapshot(sessionId);
        try
        {
            var result = await RequestCoreAsync(new AttachCommand(sessionId)).ConfigureAwait(false);
            if (result is not AttachResult)
            {
                throw UnexpectedResult(result, "attach");
            }
        }
        catch
        {
            if (previous is not null)
            {
                _state.RestoreSessionSnapshot(previous);
            }

            throw;
        }
    }

    private SessionHandle CreateSessionHandle(string sessionId, SessionLeaseToken token)
    {
        var generation = _sessionLeaseGenerations.GetValueOrDefault(sessionId);
        var state = LeaseState.Active;
        Task? releaseTask = null;

        void RefreshState()
        {
            if ((state is LeaseState.Active or LeaseState.Releasing) &&
                _sessionLeaseGenerations.GetValueOrDefault(sessionId) != generation)
            {
                state = LeaseState.Invalidated;
            }
        }

        bool IsActive()
        {
            RefreshState();
            return state == LeaseState.Active && _state.IsSessionAttached(sessionId);
        }

        void AssertActive()
        {
            ThrowIfDisposed();
            if (!Connected)
            {
                throw new PiDisconnectedError();
            }

            if (!IsActive())
            {
                throw new PiSessionDetachedError(sessionId);
            }
        }

        async Task ReleaseAsync(bool relinquishOnFailure, CancellationToken cancellationToken)
        {
            RefreshState();
            if (state is LeaseState.Released or LeaseState.Invalidated)
            {
                return;
            }

            if (releaseTask is not null)
            {
                await releaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            AssertActive();
            state = LeaseState.Releasing;
            releaseTask = ReleaseCoreAsync(relinquishOnFailure);
            await releaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task ReleaseCoreAsync(bool relinquishOnFailure)
        {
            try
            {
                var count = _sessionLeaseCounts.GetValueOrDefault(sessionId);
                if (count <= 1)
                {
                    var detachment = RequestCoreAsync(new DetachCommand(sessionId));
                    _sessionDetachments[sessionId] = detachment;
                    try
                    {
                        var result = await detachment.ConfigureAwait(false);
                        if (result is not DetachResult)
                        {
                            throw UnexpectedResult(result, "detach");
                        }

                        ReleaseSessionLease(sessionId, token);
                    }
                    finally
                    {
                        if (_sessionDetachments.TryGetValue(sessionId, out var current) && ReferenceEquals(current, detachment))
                        {
                            _sessionDetachments.Remove(sessionId);
                        }
                    }
                }
                else
                {
                    ReleaseSessionLease(sessionId, token);
                }

                state = LeaseState.Released;
            }
            catch
            {
                RefreshState();
                if (state == LeaseState.Invalidated)
                {
                    return;
                }

                if (relinquishOnFailure)
                {
                    ReleaseSessionLease(sessionId, token);
                    _sessionCleanupRequired.Add(sessionId);
                    state = LeaseState.Released;
                }
                else
                {
                    state = LeaseState.Active;
                    releaseTask = null;
                }

                throw;
            }
        }

        return new SessionHandle(
            sessionId,
            new SessionHandleCallbacks
            {
                IsAttached = IsActive,
                GetSnapshot = () => IsActive() ? _state.GetSessionSnapshot(sessionId) : null,
                Subscribe = listener =>
                {
                    AssertActive();
                    return _state.SubscribeSession(
                        sessionId,
                        snapshot =>
                        {
                            if (IsActive())
                            {
                                listener(snapshot);
                            }
                        });
                },
                OnEvent = listener =>
                {
                    AssertActive();
                    return _state.OnSessionEvent(
                        sessionId,
                        @event =>
                        {
                            if (IsActive() || @event is SessionRemovedEvent)
                            {
                                listener(@event);
                            }
                        });
                },
                DetachAsync = cancellationToken => ReleaseAsync(relinquishOnFailure: false, cancellationToken),
                DisposeAsync = cancellationToken => ReleaseAsync(relinquishOnFailure: true, cancellationToken),
                RequestAsync = (command, cancellationToken) =>
                {
                    AssertActive();
                    return RequestAsync(command, cancellationToken);
                },
            });
    }

    private SessionLeaseToken ReserveSessionLease(string sessionId, SessionLeaseMode mode)
    {
        var count = _sessionLeaseCounts.GetValueOrDefault(sessionId);
        if (mode == SessionLeaseMode.Exclusive && count > 0)
        {
            throw new PiSessionOwnershipError(sessionId, $"Session {sessionId} already has an active lease");
        }

        if (mode == SessionLeaseMode.Shared && _exclusiveSessionLeases.ContainsKey(sessionId))
        {
            throw new PiSessionOwnershipError(sessionId, $"Session {sessionId} has an exclusive lease");
        }

        var token = new SessionLeaseToken(mode);
        _sessionLeaseCounts[sessionId] = count + 1;
        if (mode == SessionLeaseMode.Exclusive)
        {
            _exclusiveSessionLeases[sessionId] = token;
        }

        return token;
    }

    private void ReleaseSessionLease(string sessionId, SessionLeaseToken token)
    {
        var count = _sessionLeaseCounts.GetValueOrDefault(sessionId);
        if (count <= 1)
        {
            _sessionLeaseCounts.Remove(sessionId);
        }
        else
        {
            _sessionLeaseCounts[sessionId] = count - 1;
        }

        if (_exclusiveSessionLeases.TryGetValue(sessionId, out var current) && ReferenceEquals(current, token))
        {
            _exclusiveSessionLeases.Remove(sessionId);
        }
    }

    private async Task<bool> ReconcileSessionCleanupAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessionCleanupRequired.Contains(sessionId))
        {
            return false;
        }

        if (!_sessionReconciliations.TryGetValue(sessionId, out var reconciliation))
        {
            reconciliation = ReconcileSessionCleanupCoreAsync(sessionId);
            _sessionReconciliations[sessionId] = reconciliation;
        }

        await reconciliation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ReconcileSessionCleanupCoreAsync(string sessionId)
    {
        try
        {
            var result = await RequestCoreAsync(new DetachCommand(sessionId)).ConfigureAwait(false);
            if (result is not DetachResult)
            {
                throw UnexpectedResult(result, "detach");
            }

            _sessionCleanupRequired.Remove(sessionId);
        }
        finally
        {
            _sessionReconciliations.Remove(sessionId);
        }
    }

    private void HandleMessage(ServerMessage message)
    {
        if (message is EventEnvelope envelope)
        {
            if (envelope.Event is SessionRemovedEvent removed)
            {
                InvalidateSessionLeases(removed.SessionId);
            }

            _state.ApplyEvent(envelope.Event);
            return;
        }

        if (message is not ResponseEnvelope)
        {
            _connection.Fail(new ProtocolValidationError("Unexpected server message"));
            return;
        }

        var response = (ResponseEnvelope)message;

        if (!_pendingRequests.Remove(response.Id, out var pending))
        {
            _connection.Fail(new ProtocolValidationError("Response has no matching request"));
            return;
        }

        if (!response.Ok)
        {
            pending.Completion.TrySetException(new PiServerError(response.Error!));
            return;
        }

        if (response.Result is null)
        {
            var error = new ProtocolValidationError("Successful response has no result");
            pending.Completion.TrySetException(error);
            _connection.Fail(error);
            return;
        }

        if (!string.Equals(response.Result.CommandName, pending.Command.CommandName, StringComparison.Ordinal))
        {
            var error = new ProtocolValidationError(
                $"Response command {response.Result.CommandName} does not match {pending.Command.CommandName}");
            pending.Completion.TrySetException(error);
            _connection.Fail(error);
            return;
        }

        _state.ApplyResult(response.Result);
        pending.Completion.TrySetResult(response.Result);
    }

    private void HandleConnectionStateChange(ConnectionStateChange change)
    {
        if (change.State == ConnectionState.Disconnected)
        {
            _state.ClearAttachments();
            InvalidateAllSessionLeases();
            RejectPendingRequests(change.Error ?? new PiDisconnectedError());
        }

        foreach (var listener in _connectionStateListeners.ToArray())
        {
            try
            {
                listener(change);
            }
            catch (Exception error)
            {
                try
                {
                    _options.OnListenerError?.Invoke(error);
                }
                catch
                {
                    // Diagnostics must not affect protocol or client state.
                }
            }
        }
    }

    private void RejectPendingRequests(Exception error)
    {
        var requests = _pendingRequests.Values.ToArray();
        _pendingRequests.Clear();
        foreach (var request in requests)
        {
            request.Completion.TrySetException(error);
        }
    }

    private void InvalidateSessionLeases(string sessionId)
    {
        _sessionLeaseCounts.Remove(sessionId);
        _exclusiveSessionLeases.Remove(sessionId);
        _sessionCleanupRequired.Remove(sessionId);
        _sessionLeaseGenerations[sessionId] = _sessionLeaseGenerations.GetValueOrDefault(sessionId) + 1;
    }

    private void InvalidateAllSessionLeases()
    {
        foreach (var sessionId in _sessionLeaseCounts.Keys.ToArray())
        {
            InvalidateSessionLeases(sessionId);
        }

        _sessionCleanupRequired.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new PiClientDisposedError();
        }
    }

    private static InvalidOperationException UnexpectedResult(CommandResult result, string command) =>
        new($"Command {command} returned unexpected result {result.CommandName}");

    private sealed record PendingRequest(Command Command, TaskCompletionSource<CommandResult> Completion);

    private sealed record SessionLeaseToken(SessionLeaseMode Mode);

    private enum LeaseState
    {
        Active,
        Releasing,
        Released,
        Invalidated,
    }
}
