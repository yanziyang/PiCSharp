using Pi.Protocol;

namespace Pi.Server;

/// <summary>Transport-agnostic Pi protocol server with live-session dispatch.</summary>
public sealed class PiServer
{
    private const int _defaultHandshakeTimeoutMilliseconds = 5_000;
    private const int _maximumTimerDelayMilliseconds = 2_147_483_647;

    private readonly IPiServerListener[] _listeners;
    private readonly uint _maxFrameLength;
    private readonly TimeSpan _handshakeTimeout;
    private readonly Action<Exception>? _onError;
    private readonly HashSet<ConnectionState> _connections = [];
    private readonly object _gate = new();
    private readonly LiveSessionManager _sessions;
    private readonly ServerSnapshotPublisher _snapshots;
    private bool _closing;
    private bool _started;
    private Task<PiServer>? _startTask;
    private Task? _closeTask;

    /// <summary>Initializes a server over already-authorized byte listeners.</summary>
    public PiServer(IPiServerService service, PiServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);
        _listeners = ResolveListeners(options.Listeners);
        _maxFrameLength = ResolveMaxFrameLength(options.MaxFrameLength);
        _handshakeTimeout = ResolveHandshakeTimeout(options.HandshakeTimeout);
        if (options.ServerId is { Length: 0 })
        {
            throw new ArgumentException("PiServer serverId must not be empty", nameof(options));
        }

        Id = options.ServerId ?? Guid.NewGuid().ToString();
        _onError = options.OnError;
        _sessions = new LiveSessionManager(
            service,
            () => _closing,
            (connection, message) => SendMessageAsync(connection, message),
            connection => CloseConnectionAsync(connection),
            connection => DisconnectAsync(connection),
            BroadcastServerSnapshot,
            ReportError);
        _snapshots = new ServerSnapshotPublisher(
            Id,
            service,
            _connections,
            () => _closing,
            cancellationToken => _sessions.ListMetadataAsync(cancellationToken),
            (connection, message) => SendMessageAsync(connection, message),
            ReportError);
    }

    /// <summary>Stable identifier advertised in the server hello.</summary>
    public string Id { get; }

    /// <summary>Human-readable listener addresses currently exposed by the server.</summary>
    public IReadOnlyList<string> Addresses =>
        _listeners.Where(static listener => listener.Address is not null)
            .Select(static listener => listener.Address!)
            .ToArray();

    /// <summary>Starts all configured listeners and completes after they are bound.</summary>
    public Task<PiServer> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started)
            {
                return Task.FromException<PiServer>(new InvalidOperationException("PiServer is already started"));
            }

            if (_startTask is not null)
            {
                return Task.FromException<PiServer>(new InvalidOperationException("PiServer is already starting"));
            }

            if (_closing)
            {
                return Task.FromException<PiServer>(new InvalidOperationException("PiServer is closing or closed"));
            }

            _startTask = StartInternalAsync(cancellationToken);
            return _startTask;
        }
    }

    /// <summary>Accepts one authorized byte connection and returns its callbacks.</summary>
    public ByteConnectionHandler Accept(IByteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_closing)
        {
            Observe(CloseConnectionAsync(connection));
            return new ByteConnectionHandler
            {
                OnData = static _ => { },
                OnClose = static () => { },
                OnError = ReportError,
            };
        }

        var handshakeTimeout = new CancellationTokenSource();
        var state = ServerConnection.CreateState(
            Guid.NewGuid().ToString(),
            connection,
            _maxFrameLength,
            handshakeTimeout);
        lock (_gate)
        {
            if (_closing)
            {
                handshakeTimeout.Cancel();
                Observe(CloseConnectionAsync(connection));
                return new ByteConnectionHandler
                {
                    OnData = static _ => { },
                    OnClose = static () => { },
                    OnError = ReportError,
                };
            }

            _connections.Add(state);
        }

        Observe(WatchHandshakeTimeoutAsync(state, handshakeTimeout.Token));
        return new ByteConnectionHandler
        {
            OnData = chunk => Receive(state, chunk),
            OnClose = () => TransportClosed(state),
            OnError = error =>
            {
                ReportError(error);
                Observe(CloseConnectionAndDisconnectAsync(state));
            },
        };
    }

    /// <summary>Stops listeners, disconnects clients, and disposes live runtimes.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task closeTask;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _closing = true;
                _closeTask = CloseInternalAsync();
            }

            closeTask = _closeTask;
        }

        return closeTask.WaitAsync(cancellationToken);
    }

    private async Task<PiServer> StartInternalAsync(CancellationToken cancellationToken)
    {
        var startedListeners = new List<IPiServerListener>();
        try
        {
            foreach (var listener in _listeners)
            {
                await listener.StartAsync(Accept, cancellationToken).ConfigureAwait(false);
                startedListeners.Add(listener);
            }

            lock (_gate)
            {
                _started = true;
            }

            return this;
        }
        catch
        {
            lock (_gate)
            {
                _closing = true;
            }

            await Task.WhenAll(startedListeners.Select(CloseListenerSafelyAsync)).ConfigureAwait(false);
            await CloseServerStateAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _startTask = null;
            }
        }
    }

    private async Task CloseInternalAsync()
    {
        Task<PiServer>? starting;
        lock (_gate)
        {
            starting = _startTask;
        }

        if (starting is not null)
        {
            await starting.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_listeners.Select(listener => listener.CloseAsync())).ConfigureAwait(false);
        }
        finally
        {
            await CloseServerStateAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _started = false;
            }
        }
    }

    private void Receive(ConnectionState state, ReadOnlyMemory<byte> chunk)
    {
        if (ConnectionState.IsTerminal(state))
        {
            return;
        }

        IReadOnlyList<ClientMessage> messages;
        try
        {
            messages = state.Decoder.Push(chunk.Span);
        }
        catch (Exception error)
        {
            Observe(FailProtocolAsync(state, ServerProtocol.ToProtocolError(error, ReportError)));
            return;
        }

        foreach (var message in messages)
        {
            if (ConnectionState.IsTerminal(state))
            {
                return;
            }

            DispatchMessage(state, message);
        }
    }

    private void DispatchMessage(ConnectionState state, ClientMessage message)
    {
        if (state.Stage == ConnectionStage.AwaitingHello)
        {
            if (message is not ClientHello hello)
            {
                Observe(FailProtocolAsync(
                    state,
                    new ProtocolError(
                        ProtocolErrorCode.InvalidRequest,
                        "The first client message must be hello")));
                return;
            }

            state.Stage = ConnectionStage.Handshaking;
            state.HandshakeTask = RunHandshakeAsync(state, hello);
            return;
        }

        if (message is ClientHello)
        {
            Observe(FailProtocolAsync(
                state,
                new ProtocolError(
                    ProtocolErrorCode.InvalidRequest,
                    "hello may only be sent as the first message")));
            return;
        }

        if (message is not RequestEnvelope request)
        {
            Observe(FailProtocolAsync(
                state,
                new ProtocolError(ProtocolErrorCode.InvalidRequest, "Invalid client protocol message")));
            return;
        }

        if (state.Stage == ConnectionStage.Ready)
        {
            Observe(HandleRequestAsync(state, request));
            return;
        }

        if (state.Stage != ConnectionStage.Handshaking || state.HandshakeTask is null)
        {
            return;
        }

        Observe(HandleAfterHandshakeAsync(state, request, state.HandshakeTask));
    }

    private async Task HandleAfterHandshakeAsync(
        ConnectionState state,
        RequestEnvelope request,
        Task handshake)
    {
        await handshake.ConfigureAwait(false);
        if (state.Stage == ConnectionStage.Ready && !state.Disconnected)
        {
            await HandleRequestAsync(state, request).ConfigureAwait(false);
        }
    }

    private async Task RunHandshakeAsync(ConnectionState state, ClientHello hello)
    {
        try
        {
            if (!Pi.Protocol.Protocol.IsSupportedProtocolVersion(hello.Version))
            {
                await FailProtocolAsync(
                    state,
                    new ProtocolError(
                        ProtocolErrorCode.Version,
                        $"Unsupported protocol version {hello.Version}; expected {ProtocolConstants.ProtocolVersion}"))
                    .ConfigureAwait(false);
                return;
            }

            var snapshot = await _snapshots.GetAsync().ConfigureAwait(false);
            if (_closing || state.Disconnected || state.Stage != ConnectionStage.Handshaking || state.Connection.Closed)
            {
                return;
            }

            var sent = await SendMessageAsync(
                state,
                new ServerHello(ProtocolConstants.ProtocolVersion, state.Id, snapshot)).ConfigureAwait(false);
            if (!sent || state.Disconnected || state.Stage != ConnectionStage.Handshaking)
            {
                return;
            }

            state.HandshakeComplete = true;
            state.Stage = ConnectionStage.Ready;
            state.HandshakeTimeout.Cancel();
            if (snapshot.Revision != _snapshots.CurrentRevision)
            {
                var current = await _snapshots.GetAsync().ConfigureAwait(false);
                await SendMessageAsync(
                    state,
                    new EventEnvelope(new ServerSnapshotEvent(current))).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            await FailProtocolAsync(state, ServerProtocol.ToProtocolError(error, ReportError)).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(ConnectionState state, RequestEnvelope request)
    {
        try
        {
            var result = await _sessions.ExecuteCommandAsync(state, request.Request).ConfigureAwait(false);
            await SendMessageAsync(state, new ResponseEnvelope(request.Id, true, result)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await SendMessageAsync(
                state,
                new ResponseEnvelope(
                    request.Id,
                    false,
                    Error: ServerProtocol.ToProtocolError(error, ReportError))).ConfigureAwait(false);
        }
    }

    private void TransportClosed(ConnectionState state)
    {
        if (!state.Disconnected && state.Stage != ConnectionStage.Closing)
        {
            try
            {
                state.Decoder.End();
            }
            catch (Exception error)
            {
                ReportError(error);
            }
        }

        Observe(DisconnectAsync(state));
    }

    private async Task DisconnectAsync(ConnectionState state)
    {
        if (state.Disconnected)
        {
            return;
        }

        var handshakeComplete = state.HandshakeComplete;
        ServerConnection.MarkClosed(state);
        lock (_gate)
        {
            _connections.Remove(state);
        }

        try
        {
            await _sessions.DisconnectAsync(state).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportError(error);
        }

        if (!_closing && handshakeComplete)
        {
            Observe(_snapshots.BroadcastAsync());
        }
    }

    private async Task<bool> SendMessageAsync(ConnectionState state, ServerMessage message)
    {
        if (state.Disconnected || state.Connection.Closed)
        {
            return false;
        }

        byte[] frame;
        try
        {
            frame = ProtocolCodec.EncodeServerMessage(
                message,
                new FrameDecoderOptions { MaxFrameLength = _maxFrameLength });
        }
        catch (Exception error)
        {
            ReportError(error);
            await CloseConnectionAsync(state.Connection).ConfigureAwait(false);
            await DisconnectAsync(state).ConfigureAwait(false);
            return false;
        }

        try
        {
            await state.Connection.SendAsync(frame).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            ReportError(error);
            await CloseConnectionAsync(state.Connection).ConfigureAwait(false);
            await DisconnectAsync(state).ConfigureAwait(false);
            return false;
        }
    }

    private async Task FailProtocolAsync(ConnectionState state, ProtocolError error)
    {
        if (state.Disconnected || state.Stage is ConnectionStage.Closing or ConnectionStage.Closed)
        {
            return;
        }

        ServerConnection.MarkClosing(state);
        byte[]? finalFrame = null;
        try
        {
            finalFrame = ProtocolCodec.EncodeServerMessage(
                new ServerHelloError(error),
                new FrameDecoderOptions { MaxFrameLength = _maxFrameLength });
        }
        catch (Exception encodeError)
        {
            ReportError(encodeError);
        }

        await CloseConnectionAsync(state.Connection, finalFrame).ConfigureAwait(false);
        await DisconnectAsync(state).ConfigureAwait(false);
    }

    private async Task CloseServerStateAsync()
    {
        ConnectionState[] connections;
        lock (_gate)
        {
            connections = _connections.ToArray();
            foreach (var connection in connections)
            {
                ServerConnection.MarkClosing(connection);
            }
        }

        await Task.WhenAll(connections.Select(connection => CloseConnectionAsync(connection.Connection))).ConfigureAwait(false);
        await Task.WhenAll(connections.Select(DisconnectAsync)).ConfigureAwait(false);
        await _sessions.CloseAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _connections.Clear();
        }
    }

    private async Task CloseConnectionAndDisconnectAsync(ConnectionState state)
    {
        await CloseConnectionAsync(state.Connection).ConfigureAwait(false);
        await DisconnectAsync(state).ConfigureAwait(false);
    }

    private async Task CloseConnectionAsync(IByteConnection connection, byte[]? finalFrame = null)
    {
        try
        {
            ReadOnlyMemory<byte>? finalChunk = finalFrame is null ? null : finalFrame;
            await connection.CloseAsync(finalChunk).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private async Task CloseListenerSafelyAsync(IPiServerListener listener)
    {
        try
        {
            await listener.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private async Task WatchHandshakeTimeoutAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_handshakeTimeout, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && !state.HandshakeComplete)
            {
                await FailProtocolAsync(
                    state,
                    new ProtocolError(ProtocolErrorCode.InvalidRequest, "Handshake timeout"))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private void ReportError(Exception error)
    {
        try
        {
            _onError?.Invoke(error);
        }
        catch
        {
            // Error observers cannot affect server state.
        }
    }

    private void BroadcastServerSnapshot() => Observe(_snapshots.BroadcastAsync());

    private void Observe(Task task)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    ReportError(completed.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static IPiServerListener[] ResolveListeners(IReadOnlyList<IPiServerListener>? listeners)
    {
        if (listeners is null)
        {
            throw new ArgumentException("PiServer listeners must be an array", nameof(listeners));
        }

        return listeners.ToArray();
    }

    private static uint ResolveMaxFrameLength(uint? maxFrameLength)
    {
        var value = maxFrameLength ?? Framing.DefaultMaxFrameLength;
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFrameLength),
                value,
                $"PiServer maxFrameLength must be an integer between 1 and {uint.MaxValue}");
        }

        return value;
    }

    private static TimeSpan ResolveHandshakeTimeout(TimeSpan? timeout)
    {
        if (timeout is null)
        {
            return TimeSpan.FromMilliseconds(_defaultHandshakeTimeoutMilliseconds);
        }

        var milliseconds = timeout.Value.TotalMilliseconds;
        if (!double.IsFinite(milliseconds) ||
            milliseconds <= 0 ||
            milliseconds > _maximumTimerDelayMilliseconds ||
            Math.Truncate(milliseconds) != milliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"PiServer handshakeTimeout must be an integer between 1 and {_maximumTimerDelayMilliseconds} milliseconds");
        }

        return timeout.Value;
    }
}
