using Pi.Client;
using Pi.Protocol;

using Xunit;

namespace Pi.Client.Tests;

public sealed class R5ClientStateTests
{
    [Fact(DisplayName = "reduces only authoritative snapshots and supports unsubscribe")]
    public async Task Reduces_only_authoritative_snapshots_and_supports_unsubscribe()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var initial = R5ClientTestSupport.SessionSnapshot("session-1");
        var handle = await R5ClientTestSupport.AttachSessionAsync(
            client,
            server,
            initial,
            cancellationToken);
        var observed = new List<long>();
        var progressTypes = new List<string>();
        var unsubscribe = handle.Subscribe(snapshot => observed.Add(snapshot.Revision));
        var unsubscribeEvents = handle.OnEvent(@event => progressTypes.Add(@event.Type));

        server.Send(new EventEnvelope(new SessionProgressEvent(
            "session-1",
            new AssistantDeltaProgress("assistant-1", 0, ContentKind.Text, "hi"))));
        Assert.Equal(["session_progress"], progressTypes);
        Assert.Equal(initial, handle.Snapshot);

        var prompting = handle.PromptAsync("hello", cancellationToken);
        var promptRequest = await server.WaitForRequestAsync("prompt", cancellationToken);
        var updated = initial with { Revision = 2, Phase = SessionPhase.Turn };
        server.Send(new ResponseEnvelope(promptRequest.Id, true, new PromptResult(updated)));

        Assert.Equal(updated, await prompting);
        Assert.Equal(updated, handle.Snapshot);
        Assert.Equal([2L], observed);

        unsubscribe();
        unsubscribeEvents();
        server.Send(new EventEnvelope(new SessionSnapshotEvent(initial with { Revision = 3 })));
        Assert.Equal([2L], observed);

        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "keeps session leases attached across server metadata snapshots")]
    public async Task Keeps_session_leases_attached_across_server_metadata_snapshots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        var handle = await R5ClientTestSupport.AttachSessionAsync(
            client,
            server,
            R5ClientTestSupport.SessionSnapshot("session-1"),
            cancellationToken);

        server.Send(new EventEnvelope(new ServerSnapshotEvent(
            R5ClientTestSupport.BaseServerSnapshot with
            {
                Revision = 2,
                Sessions = [new SessionMetadata("session-1", 1, SessionName: "Named session")],
            })));

        Assert.True(handle.Attached);
        await client.DisposeAsync(cancellationToken);
    }

    [Fact(DisplayName = "does not let an attach response replace a newer snapshot from the reacquired runtime")]
    public async Task Does_not_let_an_attach_response_replace_a_newer_snapshot_from_the_reacquired_runtime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = new R5MemoryByteServer();
        var client = await R5ClientTestSupport.ConnectClientAsync(server, cancellationToken);
        server.Send(new EventEnvelope(new SessionSnapshotEvent(
            R5ClientTestSupport.SessionSnapshot("session-1", revision: 10, attached: false))));
        server.OnMessage(message =>
        {
            if (message is not RequestEnvelope { Request: AttachCommand } request)
            {
                return;
            }

            server.Send(new EventEnvelope(new SessionSnapshotEvent(
                R5ClientTestSupport.SessionSnapshot(
                    "session-1",
                    revision: 3,
                    thinkingLevel: ThinkingLevel.High))));
            server.Send(new ResponseEnvelope(
                request.Id,
                true,
                new AttachResult(R5ClientTestSupport.SessionSnapshot(
                    "session-1",
                    revision: 2,
                    thinkingLevel: ThinkingLevel.Medium))));
        });

        var handle = await client.AttachSessionAsync("session-1", cancellationToken);

        Assert.Equal(3, handle.Snapshot?.Revision);
        Assert.Equal(ThinkingLevel.High, handle.Snapshot?.ThinkingLevel);
        await client.DisposeAsync(cancellationToken);
    }
}
