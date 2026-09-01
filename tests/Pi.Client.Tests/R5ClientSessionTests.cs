using Pi.Client;
using Pi.Protocol;

using Xunit;

namespace Pi.Client.Tests;

public sealed class R5ClientSessionTests
{
    [Fact(DisplayName = "keeps multiple session handles independent and enforces detach")]
    public async Task Keeps_multiple_session_handles_independent_and_enforces_detach()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        server.OnMessage(message =>
        {
            if (message is not RequestEnvelope request)
            {
                return;
            }

            switch (request.Request)
            {
                case AttachCommand attach:
                    server.Send(new ResponseEnvelope(
                        request.Id,
                        true,
                        new AttachResult(R5ClientTestSupport.SessionSnapshot(attach.SessionId))));
                    break;
                case DetachCommand detach:
                    server.Send(new ResponseEnvelope(
                        request.Id,
                        true,
                        new DetachResult(detach.SessionId)));
                    break;
            }
        });

        var first = await client.AttachSessionAsync("session-1", cancellationToken);
        var second = await client.AttachSessionAsync("session-2", cancellationToken);

        Assert.True(first.Attached);
        Assert.True(second.Attached);
        await first.DetachAsync(cancellationToken);
        Assert.False(first.Attached);
        Assert.True(second.Attached);
        await Assert.ThrowsAsync<PiSessionDetachedError>(
            () => first.AbortAsync(cancellationToken));

        await second.DisposeAsync(cancellationToken);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "enforces exclusive and shared lease modes")]
    public async Task Enforces_exclusive_and_shared_lease_modes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        server.OnMessage(message =>
        {
            if (message is not RequestEnvelope request)
            {
                return;
            }

            switch (request.Request)
            {
                case AttachCommand attach:
                    server.Send(new ResponseEnvelope(
                        request.Id,
                        true,
                        new AttachResult(R5ClientTestSupport.SessionSnapshot(attach.SessionId))));
                    break;
                case DetachCommand detach:
                    server.Send(new ResponseEnvelope(
                        request.Id,
                        true,
                        new DetachResult(detach.SessionId)));
                    break;
            }
        });

        var shared = await client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Shared),
            cancellationToken);
        await Assert.ThrowsAsync<PiSessionOwnershipError>(() => client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Exclusive),
            cancellationToken));
        await shared.DisposeAsync(cancellationToken);

        var exclusive = await client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Exclusive),
            cancellationToken);
        await Assert.ThrowsAsync<PiSessionOwnershipError>(() => client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Shared),
            cancellationToken));
        await exclusive.DisposeAsync(cancellationToken);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "invalidated leases dispose without protocol cleanup")]
    public async Task Invalidated_leases_dispose_without_protocol_cleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        server.OnMessage(message =>
        {
            if (message is RequestEnvelope { Request: AttachCommand attach } request)
            {
                server.Send(new ResponseEnvelope(
                    request.Id,
                    true,
                    new AttachResult(R5ClientTestSupport.SessionSnapshot(attach.SessionId))));
            }
        });

        var lease = await client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Exclusive),
            cancellationToken);
        client.Disconnect();

        await lease.DisposeAsync(cancellationToken);
        Assert.False(lease.Active);
    }

    [Fact(DisplayName = "rejects commands while releasing and restores an explicit detach after failure")]
    public async Task Rejects_commands_while_releasing_and_restores_an_explicit_detach_after_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var acquiring = client.AcquireSessionAsync(
            "session-1",
            new AcquireSessionOptions(SessionLeaseMode.Exclusive),
            cancellationToken);
        var attachRequest = await server.WaitForRequestAsync("attach", cancellationToken);
        server.Send(new ResponseEnvelope(
            attachRequest.Id,
            true,
            new AttachResult(R5ClientTestSupport.SessionSnapshot("session-1"))));
        var lease = await acquiring;

        var firstDetach = lease.DetachAsync(cancellationToken);
        var failedDetachRequest = await server.WaitForRequestAsync("detach", cancellationToken);
        await Assert.ThrowsAsync<PiSessionDetachedError>(
            () => lease.AbortAsync(cancellationToken));
        server.Send(new ResponseEnvelope(
            failedDetachRequest.Id,
            false,
            Error: new ProtocolError(ProtocolErrorCode.InvalidRequest, "retry")));
        var detachError = await Assert.ThrowsAsync<PiServerError>(() => firstDetach);
        Assert.Equal("retry", detachError.Message);
        Assert.True(lease.Active);

        var secondDetach = lease.DetachAsync(cancellationToken);
        var successfulDetachRequest = await server.WaitForRequestAsync(
            "detach",
            cancellationToken,
            occurrence: 1);
        server.Send(new ResponseEnvelope(
            successfulDetachRequest.Id,
            true,
            new DetachResult("session-1")));
        await secondDetach;
        Assert.False(lease.Active);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "serializes reacquisition behind final lease detachment")]
    public async Task Serializes_reacquisition_behind_final_lease_detachment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var firstAttachment = client.AttachSessionAsync("session-1", cancellationToken);
        var firstAttachRequest = await server.WaitForRequestAsync("attach", cancellationToken);
        server.Send(new ResponseEnvelope(
            firstAttachRequest.Id,
            true,
            new AttachResult(R5ClientTestSupport.SessionSnapshot("session-1"))));
        var first = await firstAttachment;

        var detaching = first.DetachAsync(cancellationToken);
        var detachRequest = await server.WaitForRequestAsync("detach", cancellationToken);
        var reacquiring = client.AttachSessionAsync("session-1", cancellationToken);
        Assert.Equal(
            ["attach", "detach"],
            server.Received.OfType<RequestEnvelope>().Select(request => request.Request.CommandName));

        server.Send(new ResponseEnvelope(
            detachRequest.Id,
            true,
            new DetachResult("session-1")));
        await detaching;
        var secondAttachRequest = await server.WaitForRequestAsync(
            "attach",
            cancellationToken,
            occurrence: 1);
        server.Send(new ResponseEnvelope(
            secondAttachRequest.Id,
            true,
            new AttachResult(R5ClientTestSupport.SessionSnapshot("session-1", revision: 2))));

        var second = await reacquiring;
        Assert.True(second.Attached);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "accepts a lower revision after detaching and reacquiring the same session")]
    public async Task Accepts_a_lower_revision_after_detaching_and_reacquiring_the_same_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var attachCount = 0;
        server.OnMessage(message =>
        {
            if (message is not RequestEnvelope request)
            {
                return;
            }

            switch (request.Request)
            {
                case AttachCommand attach:
                    var revision = attachCount++ == 0 ? 10 : 0;
                    server.Send(new ResponseEnvelope(
                        request.Id,
                        true,
                        new AttachResult(R5ClientTestSupport.SessionSnapshot(attach.SessionId, revision))));
                    break;
                case DetachCommand detach:
                    server.Send(new ResponseEnvelope(
                        request.Id,
                        true,
                        new DetachResult(detach.SessionId)));
                    break;
            }
        });

        var first = await client.AttachSessionAsync("session-1", cancellationToken);
        Assert.Equal(10, first.Snapshot?.Revision);
        await first.DetachAsync(cancellationToken);
        var reopened = await client.AttachSessionAsync("session-1", cancellationToken);

        Assert.NotSame(first, reopened);
        Assert.Equal(0, reopened.Snapshot?.Revision);
        await reopened.DisposeAsync(cancellationToken);
        await client.DisposeAsync(cancellationToken);
    }
}
