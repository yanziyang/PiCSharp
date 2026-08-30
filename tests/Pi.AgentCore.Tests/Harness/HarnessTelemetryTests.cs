using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Pi.AgentCore.Harness;
using Pi.Telemetry;

using Xunit;

namespace Pi.AgentCore.Tests.Harness;

[SuppressMessage("Usage", "xUnit1051", Justification = "Telemetry tests intentionally exercise default-token overloads.")]
public sealed class HarnessTelemetryTests
{
    [Fact(DisplayName = "serializes both schemas and generates the checked-in reference")]
    public void Serializes_both_schemas_and_exposes_the_checked_in_span_vocabulary()
    {
        var aiJson = JsonSerializer.Serialize(HarnessTelemetry.AiTelemetrySchema);
        var harnessJson = JsonSerializer.Serialize(HarnessTelemetry.HarnessTelemetrySchema);

        Assert.False(string.IsNullOrEmpty(aiJson));
        Assert.False(string.IsNullOrEmpty(harnessJson));
        Assert.Same(HarnessTelemetry.AiTelemetrySchema, HarnessTelemetry.AgentTelemetrySchemas[0]);
        Assert.Same(HarnessTelemetry.HarnessTelemetrySchema, HarnessTelemetry.AgentTelemetrySchemas[1]);
        Assert.Equal(
            [
                "pi.harness.run",
                "pi.harness.compaction",
                "pi.harness.navigation",
                "pi.harness.checkpoint",
                "pi.harness.turn",
                "pi.harness.step",
                "pi.harness.tool",
                "pi.harness.hook",
                "pi.harness.sleep",
                "pi.harness.event_handler",
                "pi.session.write",
            ],
            HarnessTelemetry.HarnessTelemetrySchema.Spans.Keys);
    }

    [Fact(DisplayName = "starts AI-request and harness spans through one composed typed starter")]
    public async Task Starts_ai_request_and_harness_spans_through_one_composed_typed_starter()
    {
        var telemetry = new InMemoryTelemetryContext();
        var starter = TelemetrySchema.CreateTypedSpanStarter(telemetry, HarnessTelemetry.AgentTelemetrySchemas);

        await starter.StartSpanAsync<int>(
            "pi.harness.step",
            new Dictionary<string, object?>
            {
                ["pi.lane.name"] = "main",
                ["pi.operation.id"] = "operation",
                ["pi.step.kind"] = "assistant",
                ["pi.step.attempt"] = 1,
            },
            async (stepSpan, startChildSpan) =>
            {
                stepSpan.SetAttributes(new Dictionary<string, object?> { ["pi.step.outcome"] = "succeeded" });
                await startChildSpan.StartSpanAsync<int>(
                    "pi.ai.request",
                    new Dictionary<string, object?>
                    {
                        ["pi.ai.operation"] = "stream",
                        ["pi.ai.provider"] = "provider",
                        ["pi.ai.model"] = "model",
                        ["pi.ai.api"] = "api",
                        ["pi.ai.streaming"] = true,
                    },
                    (requestSpan, _) =>
                    {
                        requestSpan.SetAttributes(new Dictionary<string, object?>
                        {
                            ["pi.ai.response.stop_reason"] = "stop",
                        });
                        return Task.FromResult(0);
                    });
                return 0;
            });

        var spans = telemetry.GetSpans();
        var step = Assert.Single(spans, span => span.Name == "pi.harness.step");
        var request = Assert.Single(spans, span => span.Name == "pi.ai.request");
        Assert.Null(step.ParentId);
        Assert.Equal(step.Id, request.ParentId);
        Assert.Equal("succeeded", step.Attributes["pi.step.outcome"]);
        Assert.Equal("stop", request.Attributes["pi.ai.response.stop_reason"]);
    }

    [Fact(DisplayName = "infers exact AI start and optional end attributes")]
    public async Task Infers_exact_ai_start_and_optional_end_attributes()
    {
        var telemetry = new InMemoryTelemetryContext();
        var valid = new Dictionary<string, object?>
        {
            ["pi.ai.operation"] = "stream",
            ["pi.ai.provider"] = "provider",
            ["pi.ai.model"] = "model",
            ["pi.ai.api"] = "api",
            ["pi.ai.streaming"] = true,
        };

        await HarnessTelemetry.StartAiSpanAsync<int>(
            telemetry,
            "pi.ai.request",
            valid,
            span =>
            {
                span.SetAttributes(new Dictionary<string, object?>
                {
                    ["pi.ai.response.stop_reason"] = "tool_use",
                });
                return Task.FromResult(0);
            });

        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => HarnessTelemetry.StartAiSpan<int>(
            telemetry,
            "pi.ai.request",
            new Dictionary<string, object?>(valid)
            {
                ["pi.ai.unknown"] = true,
            },
            _ => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => HarnessTelemetry.StartAiSpan<int>(
            telemetry,
            "pi.ai.request",
            new Dictionary<string, object?>
            {
                ["pi.ai.operation"] = "stream",
            },
            _ => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => HarnessTelemetry.StartAiSpan<int>(
            telemetry,
            "pi.ai.request",
            new Dictionary<string, object?>(valid)
            {
                ["pi.ai.streaming"] = "true",
            },
            _ => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessTelemetry.StartAiSpanAsync<int>(
            telemetry,
            "pi.ai.request",
            valid,
            span =>
            {
                span.SetAttributes(new Dictionary<string, object?> { ["pi.ai.unknown"] = true });
                return Task.FromResult(0);
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessTelemetry.StartAiSpanAsync<int>(
            telemetry,
            "pi.ai.request",
            valid,
            span =>
            {
                span.AddEvent("chunk");
                return Task.FromResult(0);
            }));
    }

    [Fact(DisplayName = "infers per-span harness literals and optional completion enrichment")]
    public async Task Infers_per_span_harness_literals_and_optional_completion_enrichment()
    {
        var telemetry = new InMemoryTelemetryContext();
        var valid = new Dictionary<string, object?>
        {
            ["pi.session.id"] = "session",
            ["pi.lane.name"] = "main",
            ["pi.operation.id"] = "operation",
            ["pi.operation.kind"] = "run",
            ["pi.operation.recovery"] = false,
        };

        await HarnessTelemetry.StartHarnessSpanAsync<int>(
            telemetry,
            "pi.harness.run",
            valid,
            span =>
            {
                span.SetAttributes(new Dictionary<string, object?> { ["pi.operation.outcome"] = "completed" });
                span.SetAttributes(new Dictionary<string, object?>());
                return Task.FromResult(0);
            });

        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => HarnessTelemetry.StartHarnessSpan<int>(
            telemetry,
            "pi.harness.run",
            new Dictionary<string, object?>(valid)
            {
                ["pi.unknown"] = true,
            },
            _ => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => HarnessTelemetry.StartHarnessSpan<int>(
            telemetry,
            "pi.harness.run",
            new Dictionary<string, object?>(valid)
            {
                ["pi.operation.kind"] = "navigation",
            },
            _ => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => HarnessTelemetry.StartHarnessSpan<int>(
            telemetry,
            "pi.harness.run",
            new Dictionary<string, object?>(),
            _ => 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessTelemetry.StartHarnessSpanAsync<int>(
            telemetry,
            "pi.harness.checkpoint",
            new Dictionary<string, object?>
            {
                ["pi.lane.name"] = "main",
                ["pi.operation.id"] = "operation",
                ["pi.checkpoint.kind"] = "normal",
            },
            span =>
            {
                span.SetAttributes(new Dictionary<string, object?> { ["pi.unknown"] = true });
                return Task.FromResult(0);
            }));
    }
}
