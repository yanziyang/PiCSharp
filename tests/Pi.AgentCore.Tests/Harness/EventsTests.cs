using Pi.AgentCore.Harness;

using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class EventsTests
{
    private static readonly RunStartEvent _runStartEvent = new()
    {
        Lane = "main",
        RunId = "run-1",
    };

    private static readonly RunEndEvent _runEndEvent = new()
    {
        Lane = "main",
        RunId = "run-1",
        Outcome = "completed",
        LeafId = "entry-1",
    };

    [Fact(DisplayName = "delivers matching events to direct listeners and watchers")]
    public void Delivers_matching_events_to_direct_listeners_and_watchers()
    {
        var events = new HarnessEventBus();
        var direct = new List<RunStartEvent>();
        var watched = new List<HarnessEvent>();
        var off = events.On(HarnessEventTypes.RunStart, @event => direct.Add((RunStartEvent)@event));
        var watch = events.Watch<object?>(() => null);
        watch.Start(watched.Add);

        events.Emit(_runStartEvent);
        events.Emit(_runEndEvent);
        off();
        events.Emit(_runStartEvent);

        Assert.Equal([_runStartEvent], direct);
        Assert.Equal([_runStartEvent, _runEndEvent, _runStartEvent], watched);
    }

    [Fact(DisplayName = "captures a snapshot without an event gap, then flushes and delivers live events")]
    public void Captures_a_snapshot_without_an_event_gap_then_flushes_and_delivers_live_events()
    {
        var events = new HarnessEventBus();
        var expectedSnapshot = new { LeafId = (string?)null };
        var watch = events.Watch(() =>
        {
            var snapshot = expectedSnapshot;
            events.Emit(_runStartEvent);
            return snapshot;
        });
        var received = new List<HarnessEvent>();

        Assert.Same(expectedSnapshot, watch.Snapshot);
        Assert.Empty(received);

        watch.Start(received.Add);
        Assert.Equal([_runStartEvent], received);

        events.Emit(_runEndEvent);
        Assert.Equal([_runStartEvent, _runEndEvent], received);

        watch.Unsubscribe();
        events.Emit(_runStartEvent);
        Assert.Equal([_runStartEvent, _runEndEvent], received);
    }
}
