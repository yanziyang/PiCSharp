using System.Diagnostics.CodeAnalysis;

using Pi.Telemetry;

using Xunit;

namespace Pi.Telemetry.Tests;

[SuppressMessage("Usage", "xUnit1051", Justification = "These tests intentionally exercise telemetry's default token overload.")]
public sealed class TypedSpanStarterTests
{
    [Fact]
    public async Task Rejects_unknown_span_names()
    {
        var starter = CreateStarter();

        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => starter.StartSpan<int>(
            "missing",
            new Dictionary<string, object?>(),
            (_, _) => 0)));
    }

    [Fact]
    public async Task Rejects_missing_required_start_attributes()
    {
        var starter = CreateStarter();

        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => starter.StartSpan<int>(
            "operation",
            new Dictionary<string, object?>(),
            (_, _) => 0)));
    }

    [Fact]
    public async Task Rejects_wrong_types_and_values()
    {
        var starter = CreateStarter();

        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => starter.StartSpan<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = 1 },
            (_, _) => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => starter.StartSpan<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "other" },
            (_, _) => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => starter.StartSpan<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "read", ["unknown"] = true },
            (_, _) => 0)));
    }

    [Fact]
    public async Task Validates_end_attributes_and_declared_events()
    {
        var telemetry = new InMemoryTelemetryContext();
        var starter = CreateStarter(telemetry);

        await starter.StartSpanAsync<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "read" },
            (span, _) =>
            {
                span.SetAttributes(new Dictionary<string, object?> { ["outcome"] = "ok" });
                span.AddEvent("finished", new Dictionary<string, object?> { ["count"] = 1 });
                return Task.FromResult(0);
            });

        await Assert.ThrowsAsync<ArgumentException>(() => starter.StartSpanAsync<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "read" },
            (span, _) =>
            {
                span.SetAttributes(new Dictionary<string, object?> { ["unknown"] = true });
                return Task.FromResult(0);
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => starter.StartSpanAsync<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "read" },
            (span, _) =>
            {
                span.AddEvent("finished", new Dictionary<string, object?> { ["count"] = "one" });
                return Task.FromResult(0);
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => starter.StartSpanAsync<int>(
            "operation",
            new Dictionary<string, object?> { ["kind"] = "read" },
            (span, _) =>
            {
                span.AddEvent("missing");
                return Task.FromResult(0);
            }));
    }

    private static TypedSpanStarter CreateStarter(TelemetryContext? telemetry = null) =>
        TelemetrySchema.CreateTypedSpanStarter(telemetry ?? new InMemoryTelemetryContext(), [CreateSchema()]);

    private static TelemetrySchemaDefinition CreateSchema() => new(
        1,
        new Dictionary<string, TelemetrySpanDefinition>
        {
            ["operation"] = new(
                "Operation",
                new TelemetryParentDefinition.RootOrExternal(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>
                {
                    ["kind"] = new(
                        new TelemetryAttributeDefinition("string", "Kind", values: ["read", "write"]),
                        true),
                },
                new Dictionary<string, TelemetryAttributeDefinition>
                {
                    ["outcome"] = new("string", "Outcome", values: ["ok", "error"]),
                },
                new Dictionary<string, TelemetryEventDefinition>
                {
                    ["finished"] = new(
                        "Finished",
                        new Dictionary<string, TelemetryRequiredAttributeDefinition>
                        {
                            ["count"] = new(new TelemetryAttributeDefinition("number", "Count"), true),
                        }),
                },
                "The operation fails"),
        });
}
