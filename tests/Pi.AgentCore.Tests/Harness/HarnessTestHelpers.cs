using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using Pi.Ai.Testing;

namespace Pi.AgentCore.Tests.Harness;

internal static class HarnessTestHelpers
{
    public static readonly EffectiveLaneConfiguration Defaults = new()
    {
        Model = new LaneModel { Provider = "default-provider", ModelId = "default-model" },
        ThinkingLevel = ThinkingLevels.Off,
        ActiveToolNames = ["default-tool"],
    };

    public static Usage Usage(int input, int output, int cacheRead = 0, int cacheWrite = 0) => new()
    {
        Input = input,
        Output = output,
        CacheRead = cacheRead,
        CacheWrite = cacheWrite,
        TotalTokens = input + output + cacheRead + cacheWrite,
        Cost = new UsageCost(),
    };

    public static AgentMessage User(string text, long timestamp = 1) =>
        new(Pi.Ai.UserMessage.Blocks([new TextContent(text)], timestamp));

    public static AgentMessage UserPlain(string text, long timestamp = 1) =>
        new(new JsonObject
        {
            ["role"] = "user",
            ["content"] = text,
            ["timestamp"] = timestamp,
        });

    public static AgentMessage Assistant(
        IReadOnlyList<ContentBlock>? content = null,
        string text = "assistant",
        string stopReason = StopReasons.Stop,
        Usage? usage = null,
        long timestamp = 1)
    {
        var message = new AssistantMessage
        {
            Content = content ?? [new TextContent(text)],
            Api = "openai-responses",
            Provider = "openai",
            Model = "test-model",
            Usage = usage ?? Usage(1, 1),
            StopReason = stopReason,
            Timestamp = timestamp,
            Deferred = stopReason == StopReasons.Deferred
                ? new DeferredHandle
                {
                    Provider = "openai",
                    ModelId = "test-model",
                    Api = "openai-responses",
                    Id = "deferred-1",
                }
                : null,
        };
        return new AgentMessage(message);
    }

    public static AgentMessage AssistantText(
        string text,
        Usage? usage = null,
        string stopReason = StopReasons.Stop,
        long timestamp = 1) =>
        Assistant([new TextContent(text)], text, stopReason, usage, timestamp);

    public static AgentMessage ToolResult(
        string callId = "call-1",
        string toolName = "tool-1",
        string text = "result",
        bool isError = false,
        long timestamp = 1) =>
        new(new ToolResultMessage
        {
            ToolCallId = callId,
            ToolName = toolName,
            Content = [new TextContent(text)],
            IsError = isError,
            Timestamp = timestamp,
        });

    public static MessageEntry MessageTarget(string id, AgentMessage message) => new()
    {
        Id = id,
        Message = message,
    };

    public static T Persisted<T>(T entry, long seq, string? parentId = null)
        where T : Entry => entry with { Seq = seq, Timestamp = seq, ParentId = parentId };

    public static OperationStartedRecord RunStarted(
        long seq = 1,
        string id = "run-1",
        IReadOnlyList<Entry>? initialMessages = null) => new()
        {
            Id = id,
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            SourceLeafId = null,
            Intent = new RunOperationIntent
            {
                OriginalPrompt = [],
                InitialMessages = initialMessages ?? [],
            },
        };

    public static OperationStartedRecord CompactionStarted(
        long seq,
        string resultEntryId = "compaction-1") => new()
        {
            Id = "compact-1",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            SourceLeafId = "source",
            Intent = new CompactionOperationIntent { ResultEntryId = resultEntryId },
        };

    public static OperationStartedRecord NavigationStarted(
        long seq,
        string summaryEntryId = "summary-1") => new()
        {
            Id = "navigate-1",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            SourceLeafId = "source",
            Intent = new NavigationOperationIntent
            {
                TargetId = "target",
                Summarize = true,
                SummaryEntryId = summaryEntryId,
            },
        };

    public static StepAttemptRecord Attempt(
        long seq,
        string runId,
        string step,
        int attempt,
        string resultEntryId,
        string? compactionReason = null) => new()
        {
            Id = $"attempt-{seq}",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            RunId = runId,
            Step = step,
            Attempt = attempt,
            ResultEntryId = resultEntryId,
            CompactionReason = compactionReason,
        };

    public static AbortRequestedRecord AbortRequested(long seq, string runId = "run-1") => new()
    {
        Id = $"abort-{seq}",
        Lane = "main",
        Seq = seq,
        Timestamp = seq,
        RunId = runId,
    };

    public static OperationFinishedRecord OperationFinished(
        long seq,
        string runId = "run-1",
        string outcome = "completed") => new()
        {
            Id = $"finish-{seq}",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            RunId = runId,
            Outcome = outcome,
        };

    public static ToolStartedRecord ToolStarted(
        long seq,
        string assistantEntryId = "assistant-tools",
        int toolIndex = 0,
        string toolCallId = "call-1",
        string toolName = "tool-1",
        string resultEntryId = "tool-result-1") => new()
        {
            Id = $"tool-start-{seq}",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            RunId = "run-1",
            AssistantEntryId = assistantEntryId,
            ToolIndex = toolIndex,
            ToolCallId = toolCallId,
            ToolName = toolName,
            EffectiveArgs = new JsonObject(),
            ResultEntryId = resultEntryId,
            Replay = "never",
        };

    public static QueueEnqueuedRecord QueueEnqueued(
        long seq,
        Entry? target = null,
        string queue = "steer") => new()
        {
            Id = $"queue-{seq}",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            Queue = queue,
            RunId = queue == "nextRun" ? null : "run-1",
            Target = target ?? MessageTarget("queue-1", User("queued")),
        };

    public static QueueCancelledRecord QueueCancelled(
        long seq,
        string entryId = "queue-1",
        string? runId = "run-1") => new()
        {
            Id = $"cancel-{seq}",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            EntryId = entryId,
            RunId = runId,
        };

    public static WriteDeferredRecord WriteDeferred(long seq, Entry? target = null) => new()
    {
        Id = $"write-{seq}",
        Lane = "main",
        Seq = seq,
        Timestamp = seq,
        RunId = "run-1",
        Target = target ?? MessageTarget("write-1", User("deferred write")),
    };

    public static UsageRecord UsageRecord(
        long seq,
        string resultEntryId,
        string stopReason = StopReasons.Error,
        int attempt = 1) => new()
        {
            Id = $"usage-{seq}",
            Lane = "main",
            Seq = seq,
            Timestamp = seq,
            Cause = "assistant",
            RunId = "run-1",
            EntryId = resultEntryId,
            Attempt = attempt,
            StopReason = stopReason,
            Usage = Usage(1, 1),
        };

    public static CompactionEntry CompactionEntry(string id, long seq) => new()
    {
        Id = id,
        ParentId = null,
        Seq = seq,
        Timestamp = seq,
        Summary = "summary",
        RetainedTail = [],
        TokensBefore = 10,
    };

    public static BranchSummaryEntry BranchSummaryEntry(string id, long seq) => new()
    {
        Id = id,
        ParentId = "target",
        Seq = seq,
        Timestamp = seq,
        FromId = "source",
        Summary = "summary",
    };

    public static RecordLogSlice RecoverySlice(
        IEnumerable<LaneRecord> records,
        IEnumerable<Entry>? entries = null)
    {
        var materialized = records.ToArray();
        var finished = materialized
            .OfType<OperationFinishedRecord>()
            .Select(static record => record.RunId)
            .ToHashSet(StringComparer.Ordinal);
        return new RecordLogSlice
        {
            Lane = "main",
            OpenOperations = materialized
                .OfType<OperationStartedRecord>()
                .Where(record => !finished.Contains(record.Id))
                .OrderByDescending(static record => record.Seq)
                .ToArray(),
            Records = materialized,
            Entries = (entries ?? []).ToArray(),
        };
    }

    public static LaneReductionInput ReductionInput(
        IEnumerable<LaneRecord> records,
        IEnumerable<Entry>? ownEntries = null,
        IEnumerable<Entry>? entries = null,
        IEnumerable<Entry>? configurationEntries = null,
        string? leafId = null,
        EffectiveLaneConfiguration? defaults = null)
    {
        var own = (ownEntries ?? []).ToArray();
        var extra = (entries ?? []).ToArray();
        var slice = RecoverySlice(records, own.Concat(extra));
        return new LaneReductionInput
        {
            Lane = slice.Lane,
            OpenOperations = slice.OpenOperations,
            Records = slice.Records,
            Entries = slice.Entries,
            LeafId = leafId ?? own.LastOrDefault()?.Id,
            OwnEntries = own,
            ConfigurationEntries = (configurationEntries ?? []).ToArray(),
            Defaults = defaults ?? Defaults,
        };
    }

    public static JsonObject JsonObject(params (string Key, JsonNode? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }

    public static FauxHarnessModel CreateFauxModel(bool reasoning, int maxTokens = 8192)
    {
        var registration = FauxProviderFactory.RegisterFauxProvider(new RegisterFauxProviderOptions
        {
            Provider = $"harness-faux-{Guid.NewGuid():N}",
            Models =
            [
                new FauxModelDefinition
                {
                    Id = reasoning ? "reasoning-model" : "non-reasoning-model",
                    Reasoning = reasoning,
                    ContextWindow = 200000,
                    MaxTokens = maxTokens,
                },
            ],
        });
        var provider = ProviderFactory.CreateProvider(new CreateProviderOptions
        {
            Id = registration.Provider.Provider,
            Name = registration.Provider.Provider,
            Auth = new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "Harness faux",
                    Resolve = _ => Task.FromResult<AuthResult?>(new AuthResult { Auth = new ModelAuth() }),
                },
            },
            Models = registration.Models,
            Api = new FauxHarnessStreams(registration.Provider),
        });
        var models = ModelsFactory.CreateModels();
        models.SetProvider(provider);
        return new FauxHarnessModel(registration, models, models.GetModel(provider.Id, registration.Models[0].Id)!);
    }

    public static AssistantMessage AssistantWithUsage(
        string text,
        Usage usage,
        string stopReason = StopReasons.Stop) =>
        new()
        {
            Content = [new TextContent(text)],
            Api = "harness",
            Provider = "harness",
            Model = "harness-model",
            Usage = usage,
            StopReason = stopReason,
            ErrorMessage = stopReason is StopReasons.Error or StopReasons.Aborted ? text : null,
            Timestamp = 1,
        };

    public static AssistantMessageEventStream CompletedStream(AssistantMessage message)
    {
        var stream = new AssistantMessageEventStream();
        stream.Push(new StreamStartEvent(message));
        if (message.StopReason == StopReasons.Error || message.StopReason == StopReasons.Aborted)
        {
            stream.Push(new StreamErrorEvent(message.StopReason, message));
        }
        else
        {
            stream.Push(new StreamDoneEvent(message.StopReason, message));
        }

        stream.End(message);
        return stream;
    }

    public sealed record FauxHarnessModel(
        FauxProviderRegistration Registration,
        MutableModels Models,
        Model Model) : IDisposable
    {
        public FauxProvider Provider => Registration.Provider;

        public void Dispose() => Registration.Dispose();
    }

    public static ScriptedHarnessModel CreateScriptedModel(
        IEnumerable<AssistantMessage> responses,
        bool reasoning = false,
        int maxTokens = 8192)
    {
        var providerId = $"harness-scripted-{Guid.NewGuid():N}";
        var model = new Model
        {
            Id = "scripted-model",
            Name = "Scripted Model",
            Api = "scripted",
            Provider = providerId,
            BaseUrl = "http://localhost:0",
            Reasoning = reasoning,
            Input = ["text"],
            Cost = new ModelCost(),
            ContextWindow = 200000,
            MaxTokens = maxTokens,
        };
        var provider = ProviderFactory.CreateProvider(new CreateProviderOptions
        {
            Id = providerId,
            Name = providerId,
            Auth = HarnessAuth(),
            Models = [model],
            Api = new ScriptedHarnessStreams(responses),
        });
        var models = ModelsFactory.CreateModels();
        models.SetProvider(provider);
        return new ScriptedHarnessModel(models, models.GetModel(providerId, model.Id)!);
    }

    private static ProviderAuth HarnessAuth() => new()
    {
        ApiKey = new ApiKeyAuth
        {
            Name = "Harness faux",
            Resolve = _ => Task.FromResult<AuthResult?>(new AuthResult { Auth = new ModelAuth() }),
        },
    };

    public sealed record ScriptedHarnessModel(MutableModels Models, Model Model);

    private sealed class FauxHarnessStreams(FauxProvider provider) : ProviderStreams
    {
        public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
            provider.Stream(model, context, ToSimple(options));

        public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
            provider.Stream(model, context, options);

        private static SimpleStreamOptions ToSimple(StreamOptions? options) => options is SimpleStreamOptions simple
            ? simple
            : new SimpleStreamOptions
            {
                Signal = options?.Signal ?? default,
                TelemetryContext = options?.TelemetryContext,
                ApiKey = options?.ApiKey,
                Fetch = options?.Fetch,
                Environment = options?.Environment,
                OnPayload = options?.OnPayload,
                OnResponse = options?.OnResponse,
                Headers = options?.Headers,
                TimeoutMs = options?.TimeoutMs,
                MaxRetries = options?.MaxRetries,
                MaxRetryDelayMs = options?.MaxRetryDelayMs,
                Temperature = options?.Temperature,
                SamplingParameters = options?.SamplingParameters,
                MaxTokens = options?.MaxTokens,
                Transport = options?.Transport,
                CacheRetention = options?.CacheRetention,
                SessionId = options?.SessionId,
                WebSocketConnectTimeoutMs = options?.WebSocketConnectTimeoutMs,
                Metadata = options?.Metadata,
            };
    }

    private sealed class ScriptedHarnessStreams(IEnumerable<AssistantMessage> responses) : ProviderStreams
    {
        private readonly Queue<AssistantMessage> _responses = new(responses);

        public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
            Next();

        public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
            Next();

        private AssistantMessageEventStream Next()
        {
            lock (_responses)
            {
                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException("No scripted harness response queued.");
                }

                return CompletedStream(_responses.Dequeue());
            }
        }
    }
}
