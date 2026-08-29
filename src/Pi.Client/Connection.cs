using Pi.Protocol;

namespace Pi.Client;

internal sealed class Connection
{
    private readonly ByteTransportFactory _transportFactory;
    private readonly uint _maxFrameLength;
    private readonly Action<ServerSnapshot> _onHandshake;
    private readonly Action<ServerMessage> _onMessage;
    private readonly Action<ConnectionStateChange> _onStateChange;
    private ConnectionState _state = ConnectionState.Disconnected;
    private IByteTransport? _transport;
    private ServerMessageDecoder? _decoder;
    private TaskCompletionSource<ServerSnapshot>? _handshake;
    private long _sequence;
    private long _activeSequence;

    public Connection(
        ByteTransportFactory transportFactory,
        uint? maxFrameLength,
        Action<ServerSnapshot> onHandshake,
        Action<ServerMessage> onMessage,
        Action<ConnectionStateChange> onStateChange)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _maxFrameLength = maxFrameLength ?? Framing.DefaultMaxFrameLength;
        if (_maxFrameLength == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameLength), "PiClient maxFrameLength must be greater than zero");
        }

        _onHandshake = onHandshake ?? throw new ArgumentNullException(nameof(onHandshake));
        _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
        _onStateChange = onStateChange ?? throw new ArgumentNullException(nameof(onStateChange));
    }

    public ConnectionState State => _state;

    public uint MaxFrameLength => _maxFrameLength;

    public Task<ServerSnapshot> ConnectAsync()
    {
        if (_state != ConnectionState.Disconnected)
        {
            return Task.FromException<ServerSnapshot>(
                new PiDisconnectedError($"PiClient is already {StateText(_state)}"));
        }

        var sequence = ++_sequence;
        _activeSequence = sequence;
        _decoder = new ServerMessageDecoder(new FrameDecoderOptions { MaxFrameLength = _maxFrameLength });
        var handshake = new TaskCompletionSource<ServerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshake = handshake;
        _transport = null;
        SetState(ConnectionState.Connecting);

        var handlers = new ByteTransportHandlers
        {
            OnData = chunk => HandleData(sequence, chunk),
            OnClose = () =>
            {
                if (IsCurrent(sequence))
                {
                    HandleClose();
                }
            },
            OnError = error =>
            {
                if (IsCurrent(sequence))
                {
                    FailAndClose(ClientErrorUtilities.ToDisconnectedError(error));
                }
            },
        };
        _ = OpenTransportAsync(sequence, handlers);
        return handshake.Task;
    }

    public void Disconnect(Exception? reason = null)
    {
        if (_state == ConnectionState.Disconnected)
        {
            return;
        }

        FailAndClose(reason ?? new PiDisconnectedError("Client disconnected"));
    }

    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        FailAndClose(error);
    }

    public void Send(ReadOnlyMemory<byte> frame)
    {
        if (_state != ConnectionState.Connected || _transport is null)
        {
            throw new PiDisconnectedError();
        }

        var transport = _transport;
        try
        {
            _ = ObserveSendAsync(transport, frame);
        }
        catch (Exception error)
        {
            FailAndClose(ClientErrorUtilities.ToDisconnectedError(error));
        }
    }

    private async Task OpenTransportAsync(long sequence, ByteTransportHandlers handlers)
    {
        IByteTransport transport;
        try
        {
            transport = await _transportFactory(handlers).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (IsCurrent(sequence))
            {
                FailAndClose(ClientErrorUtilities.ToDisconnectedError(error));
            }

            return;
        }

        if (!IsCurrent(sequence) || _state != ConnectionState.Connecting)
        {
            transport.Close();
            return;
        }

        _transport = transport;
        try
        {
            var hello = ProtocolCodec.EncodeClientMessage(
                new ClientHello(ProtocolConstants.ProtocolVersion),
                new FrameDecoderOptions { MaxFrameLength = _maxFrameLength });
            await transport.SendAsync(hello).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (IsCurrent(sequence))
            {
                FailAndClose(ClientErrorUtilities.ToDisconnectedError(error));
            }
        }
    }

    private async Task ObserveSendAsync(IByteTransport transport, ReadOnlyMemory<byte> frame)
    {
        try
        {
            await transport.SendAsync(frame).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_state != ConnectionState.Disconnected && ReferenceEquals(_transport, transport))
            {
                FailAndClose(ClientErrorUtilities.ToDisconnectedError(error));
            }
        }
    }

    private void HandleData(long sequence, ReadOnlyMemory<byte> chunk)
    {
        if (!IsCurrent(sequence))
        {
            return;
        }

        if (_state == ConnectionState.Connecting && _transport is null)
        {
            FailAndClose(new ProtocolValidationError("Received server data before the client hello was sent"));
            return;
        }

        IReadOnlyList<ServerMessage> messages;
        try
        {
            messages = _decoder!.Push(chunk.Span);
        }
        catch (Exception error)
        {
            FailAndClose(ClientErrorUtilities.ToException(error));
            return;
        }

        foreach (var message in messages)
        {
            if (_state == ConnectionState.Disconnected)
            {
                return;
            }

            HandleMessage(sequence, message);
        }
    }

    private void HandleMessage(long sequence, ServerMessage message)
    {
        if (_state == ConnectionState.Connecting)
        {
            if (message is ServerHelloError helloError)
            {
                FailAndClose(new PiServerError(helloError.Error));
                return;
            }

            if (message is not ServerHello hello)
            {
                FailAndClose(new ProtocolValidationError("Expected server hello as first message"));
                return;
            }

            if (_transport is null)
            {
                FailAndClose(new ProtocolValidationError("Received server hello before the client hello was sent"));
                return;
            }

            var handshake = _handshake!;
            _state = ConnectionState.Connected;
            try
            {
                _onHandshake(hello.Snapshot);
            }
            catch (Exception error)
            {
                if (IsCurrent(sequence))
                {
                    FailAndClose(ClientErrorUtilities.ToException(error));
                }

                return;
            }

            if (!IsCurrent(sequence) || _state != ConnectionState.Connected)
            {
                return;
            }

            _onStateChange(new ConnectionStateChange(ConnectionState.Connected));
            if (!IsCurrent(sequence) || _state != ConnectionState.Connected)
            {
                return;
            }

            handshake.TrySetResult(hello.Snapshot);
            _handshake = null;
            return;
        }

        if (_state != ConnectionState.Connected)
        {
            return;
        }

        if (message is ServerHello or ServerHelloError)
        {
            FailAndClose(new ProtocolValidationError("Unexpected handshake message"));
            return;
        }

        _onMessage(message);
    }

    private void HandleClose()
    {
        if (_state == ConnectionState.Disconnected)
        {
            return;
        }

        Exception error = new PiDisconnectedError("Byte transport closed");
        try
        {
            _decoder?.End();
        }
        catch (Exception decoderError)
        {
            error = ClientErrorUtilities.ToException(decoderError);
        }

        TransitionToDisconnected(error);
    }

    private void FailAndClose(Exception error)
    {
        var transport = _transport;
        TransitionToDisconnected(error);
        transport?.Close();
    }

    private void TransitionToDisconnected(Exception error)
    {
        if (_state == ConnectionState.Disconnected)
        {
            return;
        }

        _state = ConnectionState.Disconnected;
        _transport = null;
        _decoder = null;
        _activeSequence = 0;
        _handshake?.TrySetException(error);
        _handshake = null;
        _onStateChange(new ConnectionStateChange(ConnectionState.Disconnected, error));
    }

    private void SetState(ConnectionState state)
    {
        _state = state;
        _onStateChange(new ConnectionStateChange(state));
    }

    private bool IsCurrent(long sequence) => _state != ConnectionState.Disconnected && _activeSequence == sequence;

    private static string StateText(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => "connecting",
        ConnectionState.Connected => "connected",
        _ => "disconnected",
    };
}
