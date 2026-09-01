using Pi.Client;
using Pi.Protocol;

using Xunit;

namespace Pi.Client.Tests;

public sealed class R5ClientRequestTests
{
    [Fact(DisplayName = "correlates coalesced out-of-order responses")]
    public async Task Correlates_coalesced_out_of_order_responses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);

        var listed = client.ListSessionsAsync(cancellationToken);
        var attached = client.AttachSessionAsync("session-1", cancellationToken);
        var attachRequest = await server.WaitForRequestAsync("attach", cancellationToken);
        var listRequest = await server.WaitForRequestAsync("list", cancellationToken);
        server.SendTogether([
            new ResponseEnvelope(
                attachRequest.Id,
                true,
                new AttachResult(R5ClientTestSupport.SessionSnapshot("session-1"))),
            new ResponseEnvelope(listRequest.Id, true, new ListResult([])),
        ]);

        Assert.Empty(await listed);
        var handle = await attached;
        Assert.Equal("session-1", handle.Id);
        Assert.True(handle.Attached);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "surfaces typed request errors")]
    public async Task Surfaces_typed_request_errors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var attaching = client.AttachSessionAsync("locked", cancellationToken);
        var request = await server.WaitForRequestAsync("attach", cancellationToken);
        server.Send(new ResponseEnvelope(
            request.Id,
            false,
            Error: new ProtocolError(ProtocolErrorCode.SessionLocked, "Already attached")));

        var error = await Assert.ThrowsAsync<PiServerError>(() => attaching);
        Assert.Equal(ProtocolErrorCode.SessionLocked, error.Code);
        Assert.Equal("Already attached", error.Message);
        await client.DisposeAsync(cancellationToken);
    }
}
