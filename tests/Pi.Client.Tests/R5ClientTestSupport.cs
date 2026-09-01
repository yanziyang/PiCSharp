using Pi.Client;
using Pi.Protocol;

namespace Pi.Client.Tests;

internal sealed class R5MemoryByteServer
{
    private readonly object _gate = new();
    private readonly List<Action<ClientMessage>> _messageListeners = [];
    private ClientMessageDecoder _decoder = new();
    private TaskCompletionSource<bool> _messageChanged = NewSignal();
    private ByteTransportHandlers? _handlers;

    public List<ClientMessage> Received { get; } = [];

    public List<byte[]> SentByClient { get; } = [];

    public R5MemoryByteTransport? Transport { get; private set; }

    public int ClientCloseCount { get; private set; }

    public ValueTask<IByteTransport> Connect(ByteTransportHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
        _decoder = new ClientMessageDecoder();
        Transport = new R5MemoryByteTransport(this, handlers);
        return ValueTask.FromResult<IByteTransport>(Transport);
    }

    public Unsubscribe OnMessage(Action<ClientMessage> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            _messageListeners.Add(listener);
        }

        return () =>
        {
            lock (_gate)
            {
                _messageListeners.Remove(listener);
            }
        };
    }

    public void Send(ServerMessage message, int? splitAt = null)
    {
        var frame = ProtocolCodec.EncodeServerMessage(message);
        if (splitAt is not { } split)
        {
            SendRaw(frame);
            return;
        }

        SendRaw(frame.AsMemory(0, split));
        SendRaw(frame.AsMemory(split));
    }

    public void SendTogether(IReadOnlyList<ServerMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var frames = messages.Select(static message => ProtocolCodec.EncodeServerMessage(message)).ToArray();
        var length = frames.Sum(static frame => frame.Length);
        var combined = new byte[length];
        var offset = 0;
        foreach (var frame in frames)
        {
            frame.CopyTo(combined, offset);
            offset += frame.Length;
        }

        SendRaw(combined);
    }

    public void SendRaw(ReadOnlyMemory<byte> chunk) => _handlers?.OnData(chunk);

    public void Close() => _handlers?.OnClose();

    public void Error(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _handlers?.OnError(error);
    }

    public async Task<ClientMessage> WaitForMessageAsync(
        Func<ClientMessage, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        for (; ; )
        {
            Task wait;
            lock (_gate)
            {
                var message = Received.FirstOrDefault(predicate);
                if (message is not null)
                {
                    return message;
                }

                wait = _messageChanged.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RequestEnvelope> WaitForRequestAsync(
        string command,
        CancellationToken cancellationToken,
        int occurrence = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        for (; ; )
        {
            Task wait;
            lock (_gate)
            {
                var request = Received
                    .OfType<RequestEnvelope>()
                    .Where(item => item.Request.CommandName == command)
                    .Skip(occurrence)
                    .FirstOrDefault();
                if (request is not null)
                {
                    return request;
                }

                wait = _messageChanged.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Receive(ClientMessage message)
    {
        Action<ClientMessage>[] listeners;
        lock (_gate)
        {
            Received.Add(message);
            _messageChanged.TrySetResult(true);
            _messageChanged = NewSignal();
            listeners = _messageListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            listener(message);
        }
    }

    private void RecordClientClose()
    {
        ClientCloseCount++;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class R5MemoryByteTransport : IByteTransport
    {
        private readonly R5MemoryByteServer _server;
        private readonly ByteTransportHandlers _handlers;
        private bool _closed;

        public R5MemoryByteTransport(R5MemoryByteServer server, ByteTransportHandlers handlers)
        {
            _server = server;
            _handlers = handlers;
        }

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed)
            {
                return ValueTask.FromException(new InvalidOperationException("Transport is closed"));
            }

            SendCount++;
            _server.SentByClient.Add(chunk.ToArray());
            foreach (var message in _server._decoder.Push(chunk.Span))
            {
                _server.Receive(message);
            }

            return ValueTask.CompletedTask;
        }

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            CloseCount++;
            _server.RecordClientClose();
        }
    }
}

internal static class R5ClientTestSupport
{
    public static ServerSnapshot BaseServerSnapshot { get; } = new(
        "server-1",
        ProtocolConstants.ProtocolVersion,
        1,
        [],
        []);

    public static SessionSnapshot SessionSnapshot(
        string id,
        long revision = 1,
        ThinkingLevel thinkingLevel = ThinkingLevel.Off,
        SessionPhase phase = SessionPhase.Idle,
        bool attached = true,
        bool locked = true) => new(
            id,
            "Session " + id,
            "/workspace",
            1,
            1,
            phase,
            new ModelRef("faux", "model"),
            thinkingLevel,
            attached,
            locked,
            revision,
            [],
            [],
            0);

    public static PiClient CreateClient(
        R5MemoryByteServer server,
        Action<Exception>? onListenerError = null,
        uint? maxFrameLength = null) =>
        new(new PiClientOptions
        {
            TransportFactory = server.Connect,
            OnListenerError = onListenerError,
            MaxFrameLength = maxFrameLength,
        });

    public static async Task<PiClient> ConnectClientAsync(
        R5MemoryByteServer server,
        CancellationToken cancellationToken)
    {
        server.OnMessage(message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(
                    ProtocolConstants.ProtocolVersion,
                    "connection-1",
                    BaseServerSnapshot));
            }
        });
        var client = CreateClient(server);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    public static async Task<SessionHandle> AttachSessionAsync(
        PiClient client,
        R5MemoryByteServer server,
        SessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var occurrence = server.Received
            .OfType<RequestEnvelope>()
            .Count(item => item.Request is AttachCommand);
        var attaching = client.AttachSessionAsync(snapshot.Id, cancellationToken);
        var request = await server.WaitForRequestAsync("attach", cancellationToken, occurrence).ConfigureAwait(false);
        server.Send(new ResponseEnvelope(request.Id, true, new AttachResult(snapshot)));
        return await attaching.ConfigureAwait(false);
    }

    public static byte[] EncodeRawServerValue(IReadOnlyDictionary<string, object?> value) =>
        Framing.EncodeFrame(CborEncoder.EncodeCbor(value));
}
