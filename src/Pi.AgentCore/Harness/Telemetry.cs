using Pi.Telemetry;

namespace Pi.AgentCore.Harness;

/// <summary>Agent-owned telemetry schemas and schema-validated span helpers.</summary>
public static class HarnessTelemetry
{
    private static readonly string[] _hookNames =
    [
        "before_run",
        "before_resume",
        "before_run_end",
        "transform_context",
        "before_request",
        "before_payload",
        "after_response",
        "before_tool",
        "after_tool",
        "before_compaction",
        "before_navigation",
    ];

    private static readonly string[] _eventTypes =
    [
        "run_start",
        "run_resume",
        "run_suspend",
        "run_abort",
        "run_end",
        "fault",
        "handler_error",
        "turn_start",
        "turn_end",
        "retry_scheduled",
        "retry_start",
        "retry_end",
        "message_start",
        "message_update",
        "message_end",
        "tool_start",
        "tool_update",
        "tool_end",
        "entry_added",
        "write_pending",
        "queue_update",
        "fact_update",
        "config_update",
        "compaction_start",
        "compaction_end",
        "navigation_start",
        "navigation_end",
        "lane_created",
        "usage",
    ];

    /// <summary>Schema for one logical request to an AI provider.</summary>
    public static TelemetrySchemaDefinition AiTelemetrySchema { get; } = CreateAiSchema();

    /// <summary>Schema for agent harness and session operations.</summary>
    public static TelemetrySchemaDefinition HarnessTelemetrySchema { get; } = CreateHarnessSchema();

    /// <summary>Combined AI and harness schemas used by the agent-owned starter.</summary>
    public static IReadOnlyList<TelemetrySchemaDefinition> AgentTelemetrySchemas { get; } =
        [AiTelemetrySchema, HarnessTelemetrySchema];

    /// <summary>Starts an AI-request span with runtime schema validation.</summary>
    public static Task<T> StartAiSpanAsync<T>(
        TelemetryContext telemetryContext,
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken = default) =>
        StartAsync(telemetryContext, AiTelemetrySchema, name, attributes, callback, cancellationToken);

    /// <summary>Starts an AI-request span with runtime schema validation.</summary>
    public static Task<T> StartAiSpan<T>(
        TelemetryContext telemetryContext,
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken = default) =>
        Start(telemetryContext, AiTelemetrySchema, name, attributes, callback, cancellationToken);

    /// <summary>Starts a harness span with runtime schema validation.</summary>
    public static Task<T> StartHarnessSpanAsync<T>(
        TelemetryContext telemetryContext,
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken = default) =>
        StartAsync(telemetryContext, HarnessTelemetrySchema, name, attributes, callback, cancellationToken);

    /// <summary>Starts a harness span with runtime schema validation.</summary>
    public static Task<T> StartHarnessSpan<T>(
        TelemetryContext telemetryContext,
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken = default) =>
        Start(telemetryContext, HarnessTelemetrySchema, name, attributes, callback, cancellationToken);

    private static Task<T> Start<T>(
        TelemetryContext telemetryContext,
        TelemetrySchemaDefinition schema,
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken)
    {
        var starter = TelemetrySchema.CreateTypedSpanStarter(telemetryContext, [schema]);
        return starter.StartSpan(name, attributes, (span, _) => callback(span), cancellationToken);
    }

    private static Task<T> StartAsync<T>(
        TelemetryContext telemetryContext,
        TelemetrySchemaDefinition schema,
        string name,
        IReadOnlyDictionary<string, object?> attributes,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken)
    {
        var starter = TelemetrySchema.CreateTypedSpanStarter(telemetryContext, [schema]);
        return starter.StartSpanAsync(name, attributes, (span, _) => callback(span), cancellationToken);
    }

    private static TelemetrySchemaDefinition CreateAiSchema() => new(
        1,
        new Dictionary<string, TelemetrySpanDefinition>(StringComparer.Ordinal)
        {
            ["pi.ai.request"] = Span(
                "One logical request to an AI provider",
                new TelemetryParentDefinition.Any(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.ai.operation"] = Required(Attribute(
                        "string",
                        "Logical provider operation",
                        values: ["stream", "fetch_deferred", "cancel_deferred", "generate_images"])),
                    ["pi.ai.provider"] = Required(Attribute("string", "Selected provider id")),
                    ["pi.ai.model"] = Required(Attribute("string", "Requested model id")),
                    ["pi.ai.api"] = Required(Attribute("string", "Provider API id")),
                    ["pi.ai.streaming"] = Required(Attribute("boolean", "Whether this operation returns a stream")),
                    ["pi.ai.deferred"] = Required(
                        Attribute("boolean", "Whether the operation requests or participates in deferred execution"),
                        required: false),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.ai.response.model"] = Attribute("string", "Concrete response model"),
                    ["pi.ai.response.id"] = Attribute("string", "Provider response id", "high"),
                    ["pi.ai.response.stop_reason"] = Attribute(
                        "string",
                        "Normalized terminal response reason",
                        values: ["stop", "length", "tool_use", "error", "aborted", "deferred"]),
                    ["pi.ai.http.status_code"] = Attribute("number", "Final HTTP status"),
                    ["pi.ai.usage.input_tokens"] = Attribute("number", "Reported input tokens"),
                    ["pi.ai.usage.output_tokens"] = Attribute("number", "Reported output tokens"),
                    ["pi.ai.usage.cache_read_tokens"] = Attribute("number", "Reported cache-read tokens"),
                    ["pi.ai.usage.cache_write_tokens"] = Attribute("number", "Reported cache-write tokens"),
                    ["pi.ai.usage.reasoning_tokens"] = Attribute("number", "Reported reasoning tokens"),
                    ["pi.ai.usage.total_tokens"] = Attribute("number", "Reported total tokens"),
                    ["pi.ai.usage.cost"] = Attribute("number", "Reported total cost"),
                    ["pi.ai.stream.chunk_count"] = Attribute("number", "Streamed update chunk count"),
                    ["pi.ai.stream.time_to_first_chunk_ms"] = Attribute(
                        "number",
                        "Elapsed milliseconds to first update chunk"),
                    ["pi.ai.error.type"] = Attribute("string", "Provider or transport error class", "low"),
                },
                errorWhen: "The operation throws or returns an error result"),
        });

    private static TelemetrySchemaDefinition CreateHarnessSchema()
    {
        var operationErrors = new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["pi.error.code"] = Attribute("string", "Stable operation error code", "low"),
            ["pi.error.type"] = Attribute("string", "Low-cardinality operation error class", "low"),
        };

        var spans = new Dictionary<string, TelemetrySpanDefinition>(StringComparer.Ordinal)
        {
            ["pi.harness.run"] = Span(
                "One admitted in-process run invocation",
                new TelemetryParentDefinition.RootOrExternal(),
                OperationStart("run"),
                MergeEnd(
                    new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                    {
                        ["pi.operation.outcome"] = Attribute(
                            "string",
                            "Run invocation outcome",
                            values: ["completed", "aborted", "failed", "suspended"]),
                    },
                    operationErrors),
                "The run fails or throws"),
            ["pi.harness.compaction"] = Span(
                "One admitted in-process manual compaction invocation",
                new TelemetryParentDefinition.RootOrExternal(),
                OperationStart("compaction"),
                MergeEnd(
                    new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                    {
                        ["pi.operation.outcome"] = Attribute(
                            "string",
                            "Compaction invocation outcome",
                            values: ["completed", "declined", "aborted", "failed"]),
                    },
                    operationErrors),
                "The compaction fails or throws"),
            ["pi.harness.navigation"] = Span(
                "One admitted in-process navigation invocation",
                new TelemetryParentDefinition.RootOrExternal(),
                OperationStart("navigation"),
                MergeEnd(
                    new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                    {
                        ["pi.operation.outcome"] = Attribute(
                            "string",
                            "Navigation invocation outcome",
                            values: ["completed", "declined", "aborted", "failed"]),
                    },
                    operationErrors),
                "The navigation fails or throws"),
            ["pi.harness.checkpoint"] = Span(
                "One run checkpoint",
                new TelemetryParentDefinition.Spans(["pi.harness.run"]),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
                    ["pi.operation.id"] = Required(Attribute("string", "Durable operation id", "high")),
                    ["pi.checkpoint.kind"] = Required(Attribute(
                        "string",
                        "Checkpoint purpose",
                        values: ["normal", "failure_drain", "abort_reconcile"])),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal),
                "Checkpoint work throws"),
            ["pi.harness.turn"] = Span(
                "One assistant response and its tool batch",
                new TelemetryParentDefinition.Spans(["pi.harness.run"]),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
                    ["pi.operation.id"] = Required(Attribute("string", "Durable operation id", "high")),
                    ["pi.turn.id"] = Required(Attribute("string", "Invocation-local turn id")),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal),
                "Turn work throws"),
            ["pi.harness.step"] = Span(
                "One durable retry attempt",
                new TelemetryParentDefinition.Spans(
                    ["pi.harness.turn", "pi.harness.checkpoint", "pi.harness.compaction", "pi.harness.navigation"]),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
                    ["pi.operation.id"] = Required(Attribute("string", "Durable operation id", "high")),
                    ["pi.step.kind"] = Required(Attribute(
                        "string",
                        "Retryable step kind",
                        values: ["assistant", "compaction", "branch_summary"])),
                    ["pi.step.attempt"] = Required(Attribute("number", "One-based durable attempt number")),
                    ["pi.compaction.reason"] = Required(Attribute(
                        "string",
                        "Compaction trigger",
                        values: ["manual", "threshold", "overflow"]),
                        required: false),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.step.outcome"] = Attribute(
                        "string",
                        "Attempt outcome",
                        values: ["succeeded", "retry", "failed", "aborted", "deferred", "overflow"]),
                },
                "The attempt retries, fails, or throws"),
            ["pi.harness.tool"] = Span(
                "One raw phase-2 tool execution",
                new TelemetryParentDefinition.Spans(["pi.harness.turn", "pi.harness.run"]),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
                    ["pi.operation.id"] = Required(Attribute("string", "Durable operation id", "high")),
                    ["pi.turn.id"] = Required(Attribute("string", "Invocation-local live turn id", "high"), required: false),
                    ["pi.tool.name"] = Required(Attribute("string", "Tool name")),
                    ["pi.tool.call_id"] = Required(Attribute("string", "Tool call id", "high")),
                    ["pi.tool.replay"] = Required(Attribute("string", "Declared replay policy", values: ["never", "safe"])),
                    ["pi.tool.recovery"] = Required(Attribute("boolean", "Whether this is recovery execution")),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.tool.is_error"] = Attribute("boolean", "Whether raw phase-2 execution returned an error"),
                },
                "Raw phase-2 execution returns an error"),
            ["pi.harness.hook"] = Span(
                "One registered hook handler invocation",
                new TelemetryParentDefinition.Any(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
                    ["pi.operation.id"] = Required(
                        Attribute("string", "Durable operation id when accepted", "high"),
                        required: false),
                    ["pi.hook.name"] = Required(Attribute(
                        "string",
                        "Hook name",
                        values: _hookNames.Cast<object?>().ToArray())),
                    ["pi.hook.registration_id"] = Required(
                        Attribute("string", "Stable hook registration id"),
                        required: false),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.hook.outcome"] = Attribute(
                        "string",
                        "Handler outcome",
                        values: ["completed", "skipped", "blocked", "failed"]),
                },
                "The handler throws"),
            ["pi.harness.sleep"] = Span(
                "One retry delay",
                new TelemetryParentDefinition.Spans(["pi.harness.step", "pi.harness.run"]),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.operation.id"] = Required(Attribute("string", "Durable operation id", "high")),
                    ["pi.sleep.delay_ms"] = Required(Attribute("number", "Requested delay in milliseconds")),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.sleep.outcome"] = Attribute("string", "Delay outcome", values: ["elapsed", "aborted"]),
                },
                "Sleep work throws"),
            ["pi.harness.event_handler"] = Span(
                "One passive event listener invocation",
                new TelemetryParentDefinition.Any(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.event.type"] = Required(Attribute(
                        "string",
                        "Delivered harness event type",
                        "low",
                        _eventTypes.Cast<object?>().ToArray())),
                    ["pi.lane.name"] = Required(
                        Attribute("string", "Lane name for lane-scoped events", "high"),
                        required: false),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal),
                "The listener throws"),
            ["pi.session.write"] = Span(
                "One committed session mutation",
                new TelemetryParentDefinition.Any(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
                    ["pi.operation.id"] = Required(
                        Attribute("string", "Durable operation id when accepted", "high"),
                        required: false),
                    ["pi.session.mutation"] = Required(Attribute(
                        "string",
                        "Session mutation kind",
                        values: ["entry", "record", "lane", "fact"])),
                    ["pi.session.item_type"] = Required(
                        Attribute("string", "Entry, record, lane, or fact subtype"),
                        required: false),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["pi.session.seq"] = Attribute("number", "Committed session sequence when exposed"),
                },
                "Storage rejects the mutation"),
        };

        return new TelemetrySchemaDefinition(1, spans);
    }

    private static Dictionary<string, TelemetryRequiredAttributeDefinition> OperationStart(string operationKind) =>
        new(StringComparer.Ordinal)
        {
            ["pi.session.id"] = Required(Attribute("string", "Session id", "high")),
            ["pi.lane.name"] = Required(Attribute("string", "Lane name", "high")),
            ["pi.operation.id"] = Required(Attribute("string", "Durable operation id", "high")),
            ["pi.operation.recovery"] = Required(Attribute("boolean", "Whether this invocation resumes durable work")),
            ["pi.operation.kind"] = Required(Attribute("string", "Run operation kind", values: [operationKind])),
        };

    private static Dictionary<string, TelemetryAttributeDefinition> MergeEnd(
        Dictionary<string, TelemetryAttributeDefinition> primary,
        IReadOnlyDictionary<string, TelemetryAttributeDefinition> secondary)
    {
        foreach (var pair in secondary)
        {
            primary.Add(pair.Key, pair.Value);
        }

        return primary;
    }

    private static TelemetrySpanDefinition Span(
        string description,
        TelemetryParentDefinition parents,
        IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> startAttributes,
        IReadOnlyDictionary<string, TelemetryAttributeDefinition> endAttributes,
        string errorWhen) =>
        new(description, parents, startAttributes, endAttributes, Events: null, errorWhen);

    private static TelemetryAttributeDefinition Attribute(
        string type,
        string description,
        string? cardinality = null,
        IReadOnlyList<object?>? values = null) =>
        new(type, description, cardinality: cardinality, values: values);

    private static TelemetryRequiredAttributeDefinition Required(
        TelemetryAttributeDefinition definition,
        bool required = true) =>
        new(definition, required);
}
