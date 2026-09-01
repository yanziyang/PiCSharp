using Pi.Client;
using Pi.Protocol;

using Xunit;

namespace Pi.Client.Tests;

public sealed class R5ClientDisposalTests
{
    [Fact(DisplayName = "connects through its ownership factory")]
    public async Task Connects_through_its_ownership_factory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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

        var client = await PiClient.ConnectAsync(
            new PiClientOptions { TransportFactory = server.Connect },
            cancellationToken);

        Assert.True(client.Connected);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "disconnects, invalidates child handles, and rejects pending requests")]
    public async Task Disconnects_invalidates_child_handles_and_rejects_pending_requests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var handle = await R5ClientTestSupport.AttachSessionAsync(
            client,
            server,
            R5ClientTestSupport.SessionSnapshot("session-1"),
            cancellationToken);
        var pending = client.ListSessionsAsync(cancellationToken);

        var firstDisposal = client.DisposeAsync(cancellationToken);
        var secondDisposal = client.DisposeAsync(cancellationToken);

        Assert.Same(firstDisposal, secondDisposal);
        Assert.True(client.Disposed);
        Assert.False(client.Connected);
        Assert.False(handle.Attached);
        await Assert.ThrowsAsync<PiClientDisposedError>(() => pending);
        await Assert.ThrowsAsync<PiClientDisposedError>(
            () => handle.PromptAsync("after disposal", cancellationToken));
        await firstDisposal;
    }

    [Fact(DisplayName = "supports explicit async disposal")]
    public async Task Supports_explicit_async_disposal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);

        await ((IAsyncDisposable)client).DisposeAsync();

        Assert.True(client.Disposed);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }
}
