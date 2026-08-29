using Pi.Client;
using Pi.Protocol;

using Xunit;

namespace Pi.Client.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task Sends_hello_before_accepting_fragmented_server_hello()
    {
        var server = new MemoryByteServer();
        server.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                server.Send(
                    new ServerHello(
                        ProtocolConstants.ProtocolVersion,
                        "connection-1",
                        BaseServerSnapshot()),
                    fragmentSize: 3);
            }
        };
        var client = new PiClient(new PiClientOptions { TransportFactory = server.Connect });
        var states = new List<ConnectionState>();
        client.OnConnectionStateChange(change => states.Add(change.State));

        var snapshot = await client.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BaseServerSnapshot().ServerId, snapshot.ServerId);
        Assert.Equal(BaseServerSnapshot().ProtocolVersion, snapshot.ProtocolVersion);
        Assert.Equal(BaseServerSnapshot().Revision, snapshot.Revision);
        Assert.Single(snapshot.Models);
        Assert.IsType<ClientHello>(Assert.Single(server.Received));
        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Connected],
            states);
    }

    [Fact]
    public async Task Rejects_data_delivered_before_client_hello()
    {
        var server = new MemoryByteServer();
        var client = new PiClient(new PiClientOptions
        {
            TransportFactory = handlers =>
            {
                handlers.OnData(ProtocolCodec.EncodeServerMessage(
                    new ServerHello(
                        ProtocolConstants.ProtocolVersion,
                        "connection-1",
                        BaseServerSnapshot())));
                return server.Connect(handlers);
            },
        });

        var error = await Assert.ThrowsAsync<ProtocolValidationError>(
            () => client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Received server data before the client hello was sent", error.Message);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(0, server.Transport?.SendCount ?? 0);
        Assert.Equal(1, server.Transport?.CloseCount ?? 0);
    }

    [Fact]
    public async Task Isolates_listener_failures_and_reports_them()
    {
        var server = CreateHandshakeServer();
        var listenerErrors = new List<Exception>();
        var client = new PiClient(new PiClientOptions
        {
            TransportFactory = server.Connect,
            OnListenerError = listenerErrors.Add,
        });
        client.Subscribe(_ => throw new InvalidOperationException("consumer failure"));

        await client.ConnectAsync(TestContext.Current.CancellationToken);
        server.Send(new EventEnvelope(new ServerSnapshotEvent(BaseServerSnapshot(revision: 2))));

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.Equal(2, listenerErrors.Count);
        Assert.All(listenerErrors, error => Assert.Equal("consumer failure", error.Message));
    }

    [Fact]
    public async Task Correlates_requests_and_updates_session_snapshot_monotonically()
    {
        var server = CreateHandshakeServer();
        var initial = SessionSnapshotFor("session-1", revision: 1);
        server.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(ProtocolConstants.ProtocolVersion, "connection-1", BaseServerSnapshot()));
            }
            else if (message is RequestEnvelope { Request: AttachCommand attach } request)
            {
                server.Send(new ResponseEnvelope(request.Id, true, new AttachResult(initial)));
            }
            else if (message is RequestEnvelope { Request: DetachCommand detach } detachRequest)
            {
                server.Send(new ResponseEnvelope(detachRequest.Id, true, new DetachResult(detach.SessionId)));
            }
        };
        var client = await PiClient.ConnectAsync(
            new PiClientOptions { TransportFactory = server.Connect },
            TestContext.Current.CancellationToken);
        var handle = await client.AttachSessionAsync("session-1", TestContext.Current.CancellationToken);

        server.Send(new EventEnvelope(new SessionSnapshotEvent(initial with { Revision = 3, ThinkingLevel = ThinkingLevel.High })));
        server.Send(new EventEnvelope(new SessionSnapshotEvent(initial with { Revision = 2, ThinkingLevel = ThinkingLevel.Low })));

        Assert.Equal(3, handle.Snapshot!.Revision);
        Assert.Equal(ThinkingLevel.High, handle.Snapshot.ThinkingLevel);
        await handle.DisposeAsync(TestContext.Current.CancellationToken);
        await client.DisposeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_command_mismatch_and_disconnects()
    {
        var server = CreateHandshakeServer();
        server.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(ProtocolConstants.ProtocolVersion, "connection-1", BaseServerSnapshot()));
            }
            else if (message is RequestEnvelope request)
            {
                server.Send(new ResponseEnvelope(request.Id, true, new ListResult([])));
            }
        };
        var client = await PiClient.ConnectAsync(
            new PiClientOptions { TransportFactory = server.Connect },
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<ProtocolValidationError>(
            () => client.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not match create", error.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact]
    public async Task Enforces_shared_and_exclusive_session_leases_with_reference_counted_detach()
    {
        var server = CreateHandshakeServer();
        var session = SessionSnapshotFor("session-1", revision: 1);
        server.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(ProtocolConstants.ProtocolVersion, "connection-1", BaseServerSnapshot()));
            }
            else if (message is RequestEnvelope request)
            {
                switch (request.Request)
                {
                    case AttachCommand:
                        server.Send(new ResponseEnvelope(request.Id, true, new AttachResult(session)));
                        break;
                    case DetachCommand detach:
                        server.Send(new ResponseEnvelope(request.Id, true, new DetachResult(detach.SessionId)));
                        break;
                }
            }
        };
        var client = await PiClient.ConnectAsync(
            new PiClientOptions { TransportFactory = server.Connect },
            TestContext.Current.CancellationToken);
        var first = await client.AttachSessionAsync("session-1", TestContext.Current.CancellationToken);
        var second = await client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Shared),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PiSessionOwnershipError>(
            () => client.AcquireSessionAsync(
                "session-1",
                new AcquireSessionOptions(SessionLeaseMode.Exclusive),
                TestContext.Current.CancellationToken));

        await first.DetachAsync(TestContext.Current.CancellationToken);
        Assert.True(second.Attached);
        Assert.DoesNotContain(server.Received, message =>
            message is RequestEnvelope { Request: DetachCommand });

        await second.DisposeAsync(TestContext.Current.CancellationToken);
        Assert.Contains(server.Received, message => message is RequestEnvelope { Request: DetachCommand });
        Assert.False(second.Attached);
        await client.DisposeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_pending_request_on_transport_close_and_allows_reconnect()
    {
        var first = CreateHandshakeServer();
        var second = CreateHandshakeServer();
        var connection = 0;
        first.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                first.Send(new ServerHello(ProtocolConstants.ProtocolVersion, "first", BaseServerSnapshot(revision: 1)));
            }
        };
        second.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                second.Send(new ServerHello(ProtocolConstants.ProtocolVersion, "second", BaseServerSnapshot(revision: 2)));
            }
            else if (message is RequestEnvelope request && request.Request is ListCommand)
            {
                second.Send(new ResponseEnvelope(request.Id, true, new ListResult([])));
            }
        };
        var client = new PiClient(new PiClientOptions
        {
            TransportFactory = handlers =>
            {
                connection++;
                return (connection == 1 ? first : second).Connect(handlers);
            },
        });
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        var pending = client.ListSessionsAsync(TestContext.Current.CancellationToken);
        first.Transport!.Close();

        await Assert.ThrowsAsync<PiDisconnectedError>(() => pending);
        var snapshot = await client.ReconnectAsync(TestContext.Current.CancellationToken);
        var sessions = await client.ListSessionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Revision);
        Assert.Empty(sessions);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    private static MemoryByteServer CreateHandshakeServer()
    {
        var server = new MemoryByteServer();
        server.OnClientMessage = message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(ProtocolConstants.ProtocolVersion, "connection-1", BaseServerSnapshot()));
            }
        };
        return server;
    }

    private static ServerSnapshot BaseServerSnapshot(long revision = 1) => new(
        "server-1",
        ProtocolConstants.ProtocolVersion,
        revision,
        [],
        [new ModelMetadata(
            "test",
            "model",
            "Test model",
            "test-api",
            false,
            [ModelInputKind.Text],
            8_192,
            2_048,
            new ModelCost(0, 0, 0, 0),
            [ThinkingLevel.Off],
            true)]);

    private static SessionSnapshot SessionSnapshotFor(
        string id,
        long revision,
        bool attached = true) => new(
            id,
            null,
            "C:\\work",
            1,
            1,
            SessionPhase.Idle,
            new ModelRef("test", "model"),
            ThinkingLevel.Off,
            attached,
            false,
            revision,
            [],
            [],
            0);

    private sealed class MemoryByteServer
    {
        public Action<ClientMessage>? OnClientMessage { get; set; }

        public List<ClientMessage> Received { get; } = [];

        public MemoryByteTransport? Transport { get; private set; }

        public ValueTask<IByteTransport> Connect(ByteTransportHandlers handlers)
        {
            Transport = new MemoryByteTransport(this, handlers);
            return ValueTask.FromResult<IByteTransport>(Transport);
        }

        public void Send(ServerMessage message, int? fragmentSize = null)
        {
            Transport?.Deliver(message, fragmentSize);
        }

        public sealed class MemoryByteTransport : IByteTransport
        {
            private readonly MemoryByteServer _server;
            private readonly ByteTransportHandlers _handlers;
            private readonly ClientMessageDecoder _decoder = new();
            private bool _closed;

            public MemoryByteTransport(MemoryByteServer server, ByteTransportHandlers handlers)
            {
                _server = server;
                _handlers = handlers;
            }

            public int SendCount { get; private set; }

            public int CloseCount { get; private set; }

            public ValueTask SendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SendCount++;
                foreach (var message in _decoder.Push(chunk.Span))
                {
                    _server.Received.Add(message);
                    _server.OnClientMessage?.Invoke(message);
                }

                return ValueTask.CompletedTask;
            }

            public void Close()
            {
                CloseCount++;
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _handlers.OnClose();
            }

            public void Deliver(ServerMessage message, int? fragmentSize)
            {
                if (_closed)
                {
                    return;
                }

                var bytes = ProtocolCodec.EncodeServerMessage(message);
                var size = fragmentSize.GetValueOrDefault(bytes.Length);
                for (var offset = 0; offset < bytes.Length; offset += size)
                {
                    _handlers.OnData(bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset)));
                }
            }
        }
    }
}
