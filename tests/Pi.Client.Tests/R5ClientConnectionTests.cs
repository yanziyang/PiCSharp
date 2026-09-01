using Pi.Client;
using Pi.Protocol;

using Xunit;

namespace Pi.Client.Tests;

public sealed class R5ClientConnectionTests
{
    [Fact(DisplayName = "isolates subscriber failures from handshake and transport state")]
    public async Task Isolates_subscriber_failures_from_handshake_and_transport_state()
    {
        var server = new R5MemoryByteServer();
        server.OnMessage(message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(
                    ProtocolConstants.ProtocolVersion,
                    "connection-1",
                    R5ClientTestSupport.BaseServerSnapshot));
            }
        });
        var client = R5ClientTestSupport.CreateClient(server);
        client.Subscribe(_ => throw new InvalidOperationException("consumer failure"));

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact(DisplayName = "does not restore a connection after a snapshot listener disconnects during handshake")]
    public async Task Does_not_restore_a_connection_after_a_snapshot_listener_disconnects_during_handshake()
    {
        var server = new R5MemoryByteServer();
        server.OnMessage(message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(
                    ProtocolConstants.ProtocolVersion,
                    "connection-1",
                    R5ClientTestSupport.BaseServerSnapshot));
            }
        });
        var client = R5ClientTestSupport.CreateClient(server);
        client.Subscribe(_ => client.Disconnect());

        await Assert.ThrowsAsync<PiDisconnectedError>(
            () => client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(1, server.ClientCloseCount);
    }

    [Fact(DisplayName = "does not restore a stale connection when a snapshot listener reconnects during handshake")]
    public async Task Does_not_restore_a_stale_connection_when_a_snapshot_listener_reconnects_during_handshake()
    {
        var first = new R5MemoryByteServer();
        var second = new R5MemoryByteServer();
        var connection = 0;
        foreach (var server in new[] { first, second })
        {
            server.OnMessage(message =>
            {
                if (message is ClientHello)
                {
                    server.Send(new ServerHello(
                        ProtocolConstants.ProtocolVersion,
                        $"connection-{connection}",
                        R5ClientTestSupport.BaseServerSnapshot with { Revision = connection }));
                }
            });
        }

        var client = new PiClient(new PiClientOptions
        {
            TransportFactory = handlers =>
            {
                var selected = connection++ == 0 ? first : second;
                return selected.Connect(handlers);
            },
        });
        Task<ServerSnapshot>? reconnect = null;
        var reconnectRequested = false;
        client.Subscribe(_ =>
        {
            if (reconnectRequested)
            {
                return;
            }

            reconnectRequested = true;
            client.Disconnect();
            reconnect = client.ReconnectAsync();
        });

        await Assert.ThrowsAsync<PiDisconnectedError>(
            () => client.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(reconnect);
        var snapshot = await reconnect!;

        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.Equal(1, first.ClientCloseCount);
    }

    [Fact(DisplayName = "rejects a typed handshake version error")]
    public async Task Rejects_a_typed_handshake_version_error()
    {
        var server = new R5MemoryByteServer();
        server.OnMessage(_ => server.Send(new ServerHelloError(
            new ProtocolError(ProtocolErrorCode.Version, "Unsupported protocol version"))));
        var client = R5ClientTestSupport.CreateClient(server);

        var error = await Assert.ThrowsAsync<PiServerError>(
            () => client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ProtocolErrorCode.Version, error.Code);
        Assert.Equal("Unsupported protocol version", error.Message);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(1, server.ClientCloseCount);
    }

    [Fact(DisplayName = "supports synchronous reconnect from a disconnection listener")]
    public async Task Supports_synchronous_reconnect_from_a_disconnection_listener()
    {
        var first = new R5MemoryByteServer();
        var second = new R5MemoryByteServer();
        var connection = 0;
        foreach (var server in new[] { first, second })
        {
            server.OnMessage(message =>
            {
                if (message is ClientHello)
                {
                    server.Send(new ServerHello(
                        ProtocolConstants.ProtocolVersion,
                        $"connection-{connection}",
                        R5ClientTestSupport.BaseServerSnapshot with { Revision = connection }));
                }
            });
        }

        var client = new PiClient(new PiClientOptions
        {
            TransportFactory = handlers =>
            {
                var selected = connection++ == 0 ? first : second;
                return selected.Connect(handlers);
            },
        });
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        Task<ServerSnapshot>? reconnect = null;
        client.OnConnectionStateChange(change =>
        {
            if (change.State == ConnectionState.Disconnected)
            {
                reconnect = client.ReconnectAsync();
            }
        });

        first.Close();

        Assert.NotNull(reconnect);
        var snapshot = await reconnect!;
        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact(DisplayName = "rejects pending requests on transport errors")]
    public async Task Rejects_pending_requests_on_transport_errors()
    {
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(
            server,
            TestContext.Current.CancellationToken);
        var pending = client.ListSessionsAsync(TestContext.Current.CancellationToken);

        server.Error(new InvalidOperationException("read failed"));

        var error = await Assert.ThrowsAsync<PiDisconnectedError>(() => pending);
        Assert.Equal("read failed", error.Message);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact(DisplayName = "enforces the configured frame limit for outbound and inbound messages")]
    public async Task Enforces_the_configured_frame_limit_for_outbound_and_inbound_messages()
    {
        var server = new R5MemoryByteServer();
        server.OnMessage(message =>
        {
            if (message is ClientHello)
            {
                server.Send(new ServerHello(
                    ProtocolConstants.ProtocolVersion,
                    "connection-1",
                    R5ClientTestSupport.BaseServerSnapshot));
            }
        });
        var client = R5ClientTestSupport.CreateClient(server, maxFrameLength: 512);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        var handle = await R5ClientTestSupport.AttachSessionAsync(
            client,
            server,
            R5ClientTestSupport.SessionSnapshot("session-1"),
            TestContext.Current.CancellationToken);
        var sentBefore = server.SentByClient.Count;

        await Assert.ThrowsAsync<ProtocolValidationError>(
            () => handle.PromptAsync(
                new string('x', 1_000),
                TestContext.Current.CancellationToken));
        Assert.Equal(sentBefore, server.SentByClient.Count);

        server.SendRaw(new byte[] { 0, 0, 2, 1 });
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact(DisplayName = "disconnects on invalid protocol data")]
    public async Task Disconnects_on_invalid_protocol_data()
    {
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(
            server,
            TestContext.Current.CancellationToken);
        var raw = R5ClientTestSupport.EncodeRawServerValue(new Dictionary<string, object?>
        {
            ["type"] = "event",
            ["event"] = new Dictionary<string, object?>
            {
                ["type"] = "session_removed",
                ["sessionId"] = 1,
            },
        });

        server.SendRaw(raw);

        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact(DisplayName = "reports truncated framing when the transport closes")]
    public async Task Reports_truncated_framing_when_the_transport_closes()
    {
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(
            server,
            TestContext.Current.CancellationToken);
        var pending = client.ListSessionsAsync(TestContext.Current.CancellationToken);
        server.SendRaw(new byte[] { 0, 0, 0, 2, 1 });
        server.Close();

        var error = await Assert.ThrowsAsync<ProtocolValidationError>(() => pending);
        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact(DisplayName = "rejects frame limits outside the unsigned 32-bit range")]
    public async Task Rejects_frame_limits_outside_the_unsigned_32_bit_range()
    {
        var server = new R5MemoryByteServer();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            R5ClientTestSupport.CreateClient(server, maxFrameLength: 0));

        // MaxFrameLength is uint in the C# contract, so values above uint.MaxValue
        // are rejected by the type system rather than by PiClient at runtime.
        var maximum = R5ClientTestSupport.CreateClient(server, maxFrameLength: uint.MaxValue);
        await maximum.DisposeAsync(TestContext.Current.CancellationToken);
    }
}
