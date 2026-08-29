using System.Diagnostics.CodeAnalysis;

using Pi.Telemetry;

using Xunit;

namespace Pi.Telemetry.Tests;

[SuppressMessage("Usage", "xUnit1051", Justification = "These tests intentionally exercise telemetry's default token overload.")]
public sealed class TelemetryTests
{
    private static readonly string[] _initialTags = ["initial"];

    [Fact]
    public async Task Define_preserves_schema_and_runtime_starter_binds_children()
    {
        var operationSchema = new TelemetrySchemaDefinition(
            1,
            new Dictionary<string, TelemetrySpanDefinition>
            {
                ["operation"] = new(
                    "Operation",
                    new TelemetryParentDefinition.RootOrExternal(),
                    new Dictionary<string, TelemetryRequiredAttributeDefinition>
                    {
                        ["kind"] = new(new TelemetryAttributeDefinition("string", "Kind"), true),
                    },
                    new Dictionary<string, TelemetryAttributeDefinition>(),
                    null,
                    "The operation fails"),
            });
        var requestSchema = new TelemetrySchemaDefinition(
            3,
            new Dictionary<string, TelemetrySpanDefinition>
            {
                ["request"] = new(
                    "Request",
                    new TelemetryParentDefinition.Spans(["operation"]),
                    new Dictionary<string, TelemetryRequiredAttributeDefinition>
                    {
                        ["provider"] = new(new TelemetryAttributeDefinition("string", "Provider"), true),
                    },
                    new Dictionary<string, TelemetryAttributeDefinition>
                    {
                        ["response"] = new("string", "Response kind"),
                    },
                    null,
                    "The request fails"),
            });

        Assert.Same(operationSchema, TelemetrySchema.Define(operationSchema));

        var telemetry = new InMemoryTelemetryContext();
        var starter = TelemetrySchema.CreateTypedSpanStarter(telemetry, [operationSchema, requestSchema]);
        var result = starter.StartSpanAsync(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "read" },
            async (_, child) => await child.StartSpan(
                "request",
                new Dictionary<string, object?> { ["provider"] = "example" },
                (requestSpan, _) =>
                {
                    requestSpan.SetAttributes(new Dictionary<string, object?> { ["response"] = "cached" });
                    return 42;
                }));

        Assert.Equal(42, await result);
        var spans = telemetry.GetSpans();
        var operation = Assert.Single(spans, span => span.Name == "operation");
        var request = Assert.Single(spans, span => span.Name == "request");
        Assert.Null(operation.ParentId);
        Assert.Equal(operation.Id, request.ParentId);
        Assert.Equal("cached", request.Attributes["response"]);
    }

    [Fact]
    public async Task Noop_admits_callbacks_synchronously_and_reuses_one_inert_span()
    {
        var admitted = false;
        TelemetrySpan? firstSpan = null;
        var result = NoopTelemetryContext.Instance.StartSpanAsync(
            new SpanOptions("first"),
            async span =>
            {
                admitted = true;
                firstSpan = span;
                var child = span.StartSpan(new SpanOptions("child"), childSpan => childSpan);
                Assert.Same(span, await child);
                return 42;
            });

        Assert.True(admitted);
        Assert.Equal(42, await result);
        Assert.Same(NoopTelemetryContext.Instance, firstSpan);
    }

    [Fact]
    public async Task Noop_preserves_sync_and_async_rejection_values()
    {
        var syncError = new InvalidOperationException("sync");
        var sync = NoopTelemetryContext.Instance.StartSpan<int>(
            new SpanOptions("sync"),
            _ => throw syncError);
        var syncObserved = await Assert.ThrowsAsync<InvalidOperationException>(() => sync);
        Assert.Same(syncError, syncObserved);

        var asyncError = new InvalidOperationException("async");
        var asynchronous = NoopTelemetryContext.Instance.StartSpanAsync<int>(
            new SpanOptions("async"),
            _ => Task.FromException<int>(asyncError));
        var asyncObserved = await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous);
        Assert.Same(asyncError, asyncObserved);
    }

    [Fact]
    public async Task Noop_does_not_inspect_telemetry_payloads()
    {
        var options = new SpanOptions("operation", new ThrowingAttributes());
        await NoopTelemetryContext.Instance.StartSpan(options, span =>
        {
            var attributes = new ThrowingAttributes();
            span.AddEvent("event", attributes);
            span.SetAttributes(attributes);
            span.SetStatus(new SpanStatus.Ok());
            return 9;
        });
    }

    [Fact]
    public async Task InMemory_records_lifecycle_attributes_events_and_status()
    {
        var telemetry = new InMemoryTelemetryContext();
        await telemetry.StartSpan(
            new SpanOptions(
                "recording",
                new Dictionary<string, object?>
                {
                    ["start"] = "value",
                    ["overwrite"] = "start",
                    ["ignored"] = null,
                }),
            span =>
            {
                span.SetAttributes(new Dictionary<string, object?>
                {
                    ["count"] = 1,
                    ["overwrite"] = "middle",
                });
                span.SetAttributes(new Dictionary<string, object?>
                {
                    ["count"] = null,
                    ["overwrite"] = "end",
                });
                span.AddEvent("first", new Dictionary<string, object?> { ["index"] = 1, ["ignored"] = null });
                span.AddEvent("second", new Dictionary<string, object?> { ["index"] = 2 });
                span.SetStatus(new SpanStatus.Error(new SpanError("Expected", "failure")));
                return Task.CompletedTask;
            });

        var spanSnapshot = Assert.Single(telemetry.GetSpans());
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["start"] = "value",
                ["overwrite"] = "end",
                ["count"] = 1,
            },
            spanSnapshot.Attributes);
        Assert.Collection(
            spanSnapshot.Events,
            firstEvent =>
            {
                Assert.Equal("first", firstEvent.Name);
                Assert.Equal(new Dictionary<string, object?> { ["index"] = 1 }, firstEvent.Attributes);
            },
            secondEvent =>
            {
                Assert.Equal("second", secondEvent.Name);
                Assert.Equal(new Dictionary<string, object?> { ["index"] = 2 }, secondEvent.Attributes);
            });
        Assert.Equal(new SpanStatus.Error(new SpanError("Expected", "failure")), spanSnapshot.Status);
        Assert.True(spanSnapshot.Settled);
        Assert.Equal(1, spanSnapshot.EndSequence);
    }

    [Fact]
    public async Task InMemory_automatically_marks_failed_callbacks()
    {
        var telemetry = new InMemoryTelemetryContext();
        var error = new InvalidOperationException("failed");
        var operation = telemetry.StartSpan<int>(new SpanOptions("failure"), _ => throw error);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Same(error, observed);
        var status = Assert.Single(telemetry.GetSpans()).Status;
        var errorStatus = Assert.IsType<SpanStatus.Error>(status);
        Assert.Equal(nameof(InvalidOperationException), errorStatus.Details?.Name);
        Assert.Equal("failed", errorStatus.Details?.Message);
    }

    [Fact]
    public async Task Explicit_status_is_not_overwritten_by_failure()
    {
        var telemetry = new InMemoryTelemetryContext();
        var error = new InvalidOperationException("after explicit status");
        var operation = telemetry.StartSpan<int>(new SpanOptions("explicit"), span =>
        {
            span.SetStatus(new SpanStatus.Ok());
            throw error;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal(new SpanStatus.Ok(), Assert.Single(telemetry.GetSpans()).Status);
    }

    [Fact]
    public async Task Calls_after_settlement_are_inert_and_do_not_record_children()
    {
        var telemetry = new InMemoryTelemetryContext();
        TelemetrySpan? settledSpan = null;
        await telemetry.StartSpan(new SpanOptions("settled", new Dictionary<string, object?> { ["value"] = "initial" }), span =>
        {
            settledSpan = span;
            return 7;
        });

        Assert.NotNull(settledSpan);
        settledSpan!.SetAttributes(new Dictionary<string, object?> { ["value"] = "late" });
        settledSpan.AddEvent("late", new Dictionary<string, object?> { ["value"] = true });
        settledSpan.SetStatus(new SpanStatus.Error());
        var childAdmitted = false;
        var child = settledSpan.StartSpan(new SpanOptions("late-child"), _ =>
        {
            childAdmitted = true;
            return 7;
        });

        Assert.True(childAdmitted);
        Assert.Equal(7, await child);
        var snapshot = Assert.Single(telemetry.GetSpans());
        Assert.Equal(new Dictionary<string, object?> { ["value"] = "initial" }, snapshot.Attributes);
        Assert.Empty(snapshot.Events);
        Assert.Equal(new SpanStatus.Ok(), snapshot.Status);
    }

    [Fact]
    public async Task Nested_and_concurrent_children_have_stable_parentage_and_end_order()
    {
        var telemetry = new InMemoryTelemetryContext();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await telemetry.StartSpanAsync(new SpanOptions("parent"), async parent =>
        {
            var first = parent.StartSpanAsync(new SpanOptions("first-child"), async _ =>
            {
                await releaseFirst.Task;
                return 1;
            });
            var second = parent.StartSpan(new SpanOptions("second-child"), _ => "done");
            Assert.Equal("done", await second);
            releaseFirst.SetResult();
            Assert.Equal(1, await first);
            return 0;
        });

        var spans = telemetry.GetSpans();
        var parentSnapshot = Assert.Single(spans, span => span.Name == "parent");
        var firstSnapshot = Assert.Single(spans, span => span.Name == "first-child");
        var secondSnapshot = Assert.Single(spans, span => span.Name == "second-child");
        Assert.Null(parentSnapshot.ParentId);
        Assert.Equal(parentSnapshot.Id, firstSnapshot.ParentId);
        Assert.Equal(parentSnapshot.Id, secondSnapshot.ParentId);
        Assert.True(secondSnapshot.EndSequence < firstSnapshot.EndSequence);
        Assert.True(firstSnapshot.EndSequence < parentSnapshot.EndSequence);
    }

    [Fact]
    public async Task Unreadable_recording_payloads_are_ignored_atomically()
    {
        var telemetry = new InMemoryTelemetryContext();
        await telemetry.StartSpan(new SpanOptions("atomic", new Dictionary<string, object?> { ["retained"] = "value" }), span =>
        {
            span.SetAttributes(new ThrowingAttributes());
            span.AddEvent("unreadable-event", new ThrowingAttributes());
            return 0;
        });

        var snapshot = Assert.Single(telemetry.GetSpans());
        Assert.Equal(new Dictionary<string, object?> { ["retained"] = "value" }, snapshot.Attributes);
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public async Task Snapshots_are_detached_and_open_spans_are_visible()
    {
        var telemetry = new InMemoryTelemetryContext();
        bool? openSettled = null;
        int? openEndSequence = null;
        await telemetry.StartSpan(new SpanOptions("snapshot", new Dictionary<string, object?> { ["tags"] = _initialTags }), span =>
        {
            span.AddEvent("event", new Dictionary<string, object?> { ["value"] = 1 });
            var open = Assert.Single(telemetry.GetSpans());
            openSettled = open.Settled;
            openEndSequence = open.EndSequence;
            return 0;
        });

        Assert.False(openSettled);
        Assert.Null(openEndSequence);
        var first = Assert.Single(telemetry.GetSpans());
        Assert.True(first.Settled);
        Assert.Equal(1, first.EndSequence);
        var firstAttributes = Assert.IsType<string[]>(first.Attributes["tags"]);
        firstAttributes[0] = "mutated";
        var firstEventAttributes = Assert.IsType<int>(first.Events[0].Attributes["value"]);
        Assert.Equal(1, firstEventAttributes);

        var second = Assert.Single(telemetry.GetSpans());
        Assert.Equal(_initialTags, second.Attributes["tags"]);
        Assert.Equal(1, second.Events[0].Attributes["value"]);
    }

    private sealed class ThrowingAttributes : IReadOnlyDictionary<string, object?>
    {
        public IEnumerable<string> Keys => throw new InvalidOperationException("enumerate");

        public IEnumerable<object?> Values => throw new InvalidOperationException("enumerate");

        public int Count => throw new InvalidOperationException("inspect");

        public object? this[string key] => throw new InvalidOperationException("read");

        public bool ContainsKey(string key) => throw new InvalidOperationException("read");

        public bool TryGetValue(string key, out object? value)
        {
            throw new InvalidOperationException("read");
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            throw new InvalidOperationException("enumerate");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
