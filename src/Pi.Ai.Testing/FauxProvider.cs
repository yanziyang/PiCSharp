using System.Text.Json.Nodes;

using Pi.Ai;

namespace Pi.Ai.Testing;

/// <summary>Optional model definition used to configure a faux provider.</summary>
public sealed record FauxModelDefinition
{
    /// <summary>Model identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Whether the model supports reasoning.</summary>
    public bool Reasoning { get; init; }

    /// <summary>Accepted input modalities.</summary>
    public IReadOnlyList<string>? Input { get; init; }

    /// <summary>Pricing metadata.</summary>
    public ModelCost? Cost { get; init; }

    /// <summary>Context window size.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>Maximum output token count.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>Options for deferred faux responses.</summary>
public sealed record FauxDeferredOptions
{
    /// <summary>Number of polls that remain deferred before the scripted response is ready.</summary>
    public int PendingFetches { get; init; }

    /// <summary>Suggested polling interval in milliseconds.</summary>
    public int? PollAfterMs { get; init; }
}

/// <summary>Faux provider configuration.</summary>
public sealed record RegisterFauxProviderOptions
{
    /// <summary>API identifier. A faux identifier is generated when omitted.</summary>
    public string? Api { get; init; }

    /// <summary>Provider identifier.</summary>
    public string? Provider { get; init; }

    /// <summary>Configured models.</summary>
    public IReadOnlyList<FauxModelDefinition>? Models { get; init; }

    /// <summary>Deferred response behavior.</summary>
    public FauxDeferredOptions? Deferred { get; init; }

    /// <summary>Simulated output rate in tokens per second.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>Randomized chunk-size bounds.</summary>
    public FauxTokenSizeOptions? TokenSize { get; init; }
}

/// <summary>Chunk-size bounds for faux stream deltas.</summary>
public sealed record FauxTokenSizeOptions
{
    /// <summary>Minimum simulated token size.</summary>
    public int? Min { get; init; }

    /// <summary>Maximum simulated token size.</summary>
    public int? Max { get; init; }
}

/// <summary>Mutable counters and deferred-cancellation observations for a faux provider.</summary>
public sealed class FauxProviderState
{
    /// <summary>Number of submitted stream calls.</summary>
    public int CallCount { get; internal set; }

    /// <summary>Number of deferred fetch calls.</summary>
    public int DeferredFetchCount { get; internal set; }

    /// <summary>Handles passed to deferred cancellation.</summary>
    public List<DeferredHandle> CancelledDeferred { get; } = [];
}

/// <summary>Factory invoked to produce one scripted faux assistant response.</summary>
public delegate Task<AssistantMessage> FauxResponseFactory(
    Context context,
    SimpleStreamOptions? options,
    FauxProviderState state,
    Model model);

/// <summary>A queued faux response, either a fixed assistant message or an asynchronous factory.</summary>
public sealed class FauxResponseStep
{
    private readonly AssistantMessage? _message;
    private readonly FauxResponseFactory? _factory;

    private FauxResponseStep(AssistantMessage message)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
    }

    private FauxResponseStep(FauxResponseFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Creates a fixed-message response step.</summary>
    public static FauxResponseStep FromMessage(AssistantMessage message) => new(message);

    /// <summary>Creates a factory-backed response step.</summary>
    public static FauxResponseStep FromFactory(FauxResponseFactory factory) => new(factory);

    internal Task<AssistantMessage> ResolveAsync(
        Context context,
        SimpleStreamOptions? options,
        FauxProviderState state,
        Model model) =>
        _message is not null
            ? Task.FromResult(_message)
            : _factory!(context, options, state, model);
}

/// <summary>Faux content/message construction helpers.</summary>
public static class FauxMessages
{
    private static readonly Usage _defaultUsage = new()
    {
        Cost = new UsageCost(),
    };

    /// <summary>Creates a text content block.</summary>
    public static TextContent FauxText(string text) => new(text);

    /// <summary>Creates a reasoning content block.</summary>
    public static ThinkingContent FauxThinking(string thinking) => new(thinking);

    /// <summary>Creates a tool call content block.</summary>
    public static ToolCall FauxToolCall(
        string name,
        JsonObject arguments,
        string? id = null) =>
        new(id ?? RandomId("tool"), name, arguments.DeepClone().AsObject());

    /// <summary>Creates a tool call content block from a JSON property dictionary.</summary>
    public static ToolCall FauxToolCall(
        string name,
        IReadOnlyDictionary<string, JsonNode?> arguments,
        string? id = null)
    {
        var json = new JsonObject();
        foreach (var pair in arguments)
        {
            json[pair.Key] = pair.Value?.DeepClone();
        }

        return FauxToolCall(name, json, id);
    }

    /// <summary>Creates a faux assistant message with Pi's default metadata.</summary>
    public static AssistantMessage FauxAssistantMessage(
        string content,
        string? stopReason = null,
        DeferredHandle? deferred = null,
        string? errorMessage = null,
        string? responseId = null,
        long? timestamp = null) =>
        FauxAssistantMessage(
            [FauxText(content)],
            stopReason,
            deferred,
            errorMessage,
            responseId,
            timestamp);

    /// <summary>Creates a faux assistant message from content blocks.</summary>
    public static AssistantMessage FauxAssistantMessage(
        IReadOnlyList<ContentBlock> content,
        string? stopReason = null,
        DeferredHandle? deferred = null,
        string? errorMessage = null,
        string? responseId = null,
        long? timestamp = null) => new()
        {
            Content = content.Select(CloneContentBlock).ToArray(),
            Api = "faux",
            Provider = ProviderNames.Faux,
            Model = "faux-1",
            Usage = CloneUsage(_defaultUsage),
            StopReason = stopReason ?? StopReasons.Stop,
            Deferred = deferred,
            ErrorMessage = errorMessage,
            ResponseId = responseId,
            Timestamp = timestamp ?? Now(),
        };

    internal static ContentBlock CloneContentBlock(ContentBlock block) => block switch
    {
        TextContent text => text with { },
        ThinkingContent thinking => thinking with { },
        ImageContent image => image with { },
        ToolCall toolCall => toolCall with { Arguments = toolCall.Arguments.DeepClone().AsObject() },
        _ => throw new InvalidOperationException($"Unsupported faux content block: {block.GetType().Name}"),
    };

    internal static AssistantMessage CloneMessage(AssistantMessage message) => message with
    {
        Content = message.Content.Select(CloneContentBlock).ToArray(),
        Usage = CloneUsage(message.Usage),
        Deferred = message.Deferred is null ? null : CloneHandle(message.Deferred),
        Diagnostics = message.Diagnostics?.ToArray(),
    };

    internal static Usage CloneUsage(Usage usage) => usage with
    {
        Cost = usage.Cost with { },
    };

    internal static DeferredHandle CloneHandle(DeferredHandle handle) => handle with
    {
        Data = handle.Data?.DeepClone(),
    };

    internal static string RandomId(string prefix) =>
        $"{prefix}:{Now()}:{Guid.NewGuid():N}";

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Deterministic faux provider implementation.</summary>
public sealed class FauxProvider
{
    private const string _defaultModelId = "faux-1";
    private const string _defaultModelName = "Faux Model";
    private const string _defaultBaseUrl = "http://localhost:0";
    private const int _defaultMinTokenSize = 3;
    private const int _defaultMaxTokenSize = 5;

    private readonly List<FauxResponseStep> _pendingResponses = [];
    private readonly Dictionary<string, string> _promptCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeferredEntry> _deferredResponses = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _minTokenSize;
    private readonly int _maxTokenSize;
    private readonly double? _tokensPerSecond;
    private readonly RegisterFauxProviderOptions _options;
    private readonly Model[] _models;

    /// <summary>Creates a configured faux provider.</summary>
    public FauxProvider(RegisterFauxProviderOptions? options = null)
    {
        _options = options ?? new RegisterFauxProviderOptions();
        Api = _options.Api ?? FauxMessages.RandomId("faux");
        Provider = _options.Provider ?? ProviderNames.Faux;
        var configuredMin = _options.TokenSize?.Min ?? _defaultMinTokenSize;
        var configuredMax = _options.TokenSize?.Max ?? _defaultMaxTokenSize;
        _minTokenSize = Math.Max(1, Math.Min(configuredMin, configuredMax));
        _maxTokenSize = Math.Max(_minTokenSize, configuredMax);
        _tokensPerSecond = _options.TokensPerSecond;
        State = new FauxProviderState();

        var definitions = _options.Models is { Count: > 0 }
            ? _options.Models
            : [new FauxModelDefinition
            {
                Id = _defaultModelId,
                Name = _defaultModelName,
                Input = ["text", "image"],
                Cost = new ModelCost(),
                ContextWindow = 128000,
                MaxTokens = 16384,
            }];
        _models = definitions
            .Select(definition => new Model
            {
                Id = definition.Id,
                Name = definition.Name ?? definition.Id,
                Api = Api,
                Provider = Provider,
                BaseUrl = _defaultBaseUrl,
                Reasoning = definition.Reasoning,
                Input = definition.Input ?? ["text", "image"],
                Cost = definition.Cost ?? new ModelCost(),
                ContextWindow = definition.ContextWindow ?? 128000,
                MaxTokens = definition.MaxTokens ?? 16384,
            })
            .ToArray();
    }

    /// <summary>Faux API identifier.</summary>
    public string Api { get; }

    /// <summary>Faux provider identifier.</summary>
    public string Provider { get; }

    /// <summary>Configured faux models.</summary>
    public IReadOnlyList<Model> Models => _models;

    /// <summary>Mutable faux provider state.</summary>
    public FauxProviderState State { get; }

    /// <summary>Returns the first configured model.</summary>
    public Model GetModel() => _models[0];

    /// <summary>Looks up a configured model by identifier.</summary>
    public Model? GetModel(string modelId) => _models.FirstOrDefault(model => model.Id == modelId);

    /// <summary>Replaces queued responses.</summary>
    public void SetResponses(IEnumerable<FauxResponseStep> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        lock (_gate)
        {
            _pendingResponses.Clear();
            _pendingResponses.AddRange(responses);
        }
    }

    /// <summary>Appends responses to the queue.</summary>
    public void AppendResponses(IEnumerable<FauxResponseStep> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        lock (_gate)
        {
            _pendingResponses.AddRange(responses);
        }
    }

    /// <summary>Returns the number of queued responses.</summary>
    public int GetPendingResponseCount()
    {
        lock (_gate)
        {
            return _pendingResponses.Count;
        }
    }

    /// <summary>Starts a faux response stream.</summary>
    public AssistantMessageEventStream Stream(
        Model model,
        Context context,
        SimpleStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var outer = new AssistantMessageEventStream();
        FauxResponseStep? step;
        lock (_gate)
        {
            step = _pendingResponses.Count > 0 ? _pendingResponses[0] : null;
            if (step is not null)
            {
                _pendingResponses.RemoveAt(0);
            }
        }

        State.CallCount++;
        _ = ProcessStreamAsync(outer, step, model, context, options);
        return outer;
    }

    /// <summary>Completes one faux stream and returns its terminal assistant message.</summary>
    public Task<AssistantMessage> CompleteAsync(
        Model model,
        Context context,
        SimpleStreamOptions? options = null) =>
        Stream(model, context, options).Result;

    /// <summary>Fetches a deferred faux response.</summary>
    public AssistantMessageEventStream FetchDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        var outer = new AssistantMessageEventStream();
        State.DeferredFetchCount++;
        _ = ProcessDeferredFetchAsync(outer, model, handle, options);
        return outer;
    }

    /// <summary>Cancels a deferred faux response.</summary>
    public async Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        State.CancelledDeferred.Add(FauxMessages.CloneHandle(handle));
        lock (_gate)
        {
            if (_deferredResponses.TryGetValue(handle.Id, out var entry))
            {
                entry.Cancelled = true;
            }
        }

        if (options?.OnResponse is { } onResponse)
        {
            await onResponse(new ProviderResponse(200, new Dictionary<string, string>()), model).ConfigureAwait(false);
        }
    }

    private async Task ProcessStreamAsync(
        AssistantMessageEventStream outer,
        FauxResponseStep? step,
        Model model,
        Context context,
        SimpleStreamOptions? options)
    {
        try
        {
            if (options?.OnResponse is { } onResponse)
            {
                await onResponse(new ProviderResponse(200, new Dictionary<string, string>()), model).ConfigureAwait(false);
            }

            if (step is null)
            {
                var error = CreateErrorMessage(
                    new InvalidOperationException("No more faux responses queued"),
                    model);
                error = WithUsageEstimate(error, context, options);
                outer.Push(new StreamErrorEvent(StopReasons.Error, error));
                outer.End(error);
                return;
            }

            if (options?.Deferred == true)
            {
                var handle = new DeferredHandle
                {
                    Provider = model.Provider,
                    ModelId = model.Id,
                    Api = model.Api,
                    Id = FauxMessages.RandomId("deferred"),
                    PollAfterMs = _options.Deferred?.PollAfterMs,
                };
                lock (_gate)
                {
                    _deferredResponses[handle.Id] = new DeferredEntry
                    {
                        Handle = FauxMessages.CloneHandle(handle),
                        Step = step,
                        Context = context,
                        Options = options,
                        Model = model,
                        PendingFetches = Math.Max(0, _options.Deferred?.PendingFetches ?? 0),
                    };
                }

                await StreamWithDeltasAsync(
                    outer,
                    CreateDeferredMessage(model, handle),
                    options.Signal).ConfigureAwait(false);
                return;
            }

            var message = await ResolveResponseAsync(step, context, options, model).ConfigureAwait(false);
            await StreamWithDeltasAsync(outer, message, options?.Signal ?? default).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var message = CreateErrorMessage(error, model);
            outer.Push(new StreamErrorEvent(StopReasons.Error, message));
            outer.End(message);
        }
    }

    private async Task ProcessDeferredFetchAsync(
        AssistantMessageEventStream outer,
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options)
    {
        try
        {
            if (options?.OnResponse is { } onResponse)
            {
                await onResponse(new ProviderResponse(200, new Dictionary<string, string>()), model).ConfigureAwait(false);
            }

            DeferredEntry entry;
            bool wasPending;
            lock (_gate)
            {
                if (!_deferredResponses.TryGetValue(handle.Id, out entry!) ||
                    entry.Handle.Provider != handle.Provider ||
                    entry.Handle.ModelId != handle.ModelId ||
                    entry.Handle.Api != handle.Api)
                {
                    throw new InvalidOperationException($"Unknown faux deferred response: {handle.Id}");
                }

                if (entry.Cancelled)
                {
                    throw new InvalidOperationException($"Faux deferred response was cancelled: {handle.Id}");
                }

                wasPending = entry.PendingFetches > 0;
                if (wasPending)
                {
                    entry.PendingFetches--;
                }
            }

            if (wasPending)
            {
                await StreamWithDeltasAsync(
                    outer,
                    CreateDeferredMessage(model, entry.Handle),
                    options?.Signal ?? default).ConfigureAwait(false);
                return;
            }

            if (entry.Final is null)
            {
                try
                {
                    var submissionOptions = WithoutSubmissionOnlyOptions(entry.Options);
                    entry.Final = await ResolveResponseAsync(
                        entry.Step,
                        entry.Context,
                        submissionOptions,
                        entry.Model).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    entry.Final = CreateErrorMessage(error, Api, Provider, entry.Model.Id);
                }
            }

            await StreamWithDeltasAsync(outer, entry.Final, options?.Signal ?? default).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var message = CreateErrorMessage(error, model);
            outer.Push(new StreamErrorEvent(StopReasons.Error, message));
            outer.End(message);
        }
    }

    private async Task<AssistantMessage> ResolveResponseAsync(
        FauxResponseStep step,
        Context context,
        SimpleStreamOptions? options,
        Model model)
    {
        var resolved = await step.ResolveAsync(context, options, State, model).ConfigureAwait(false);
        var cloned = FauxMessages.CloneMessage(resolved) with
        {
            Api = Api,
            Provider = Provider,
            Model = model.Id,
            Timestamp = resolved.Timestamp == 0 ? FauxMessages.Now() : resolved.Timestamp,
        };
        return WithUsageEstimate(cloned, context, options);
    }

    private AssistantMessage WithUsageEstimate(
        AssistantMessage message,
        Context context,
        StreamOptions? options)
    {
        var promptText = SerializeContext(context);
        var promptTokens = EstimateTokens(promptText);
        var outputTokens = EstimateTokens(AssistantContentToText(message.Content));
        var input = promptTokens;
        var cacheRead = 0;
        var cacheWrite = 0;
        var sessionId = options is SimpleStreamOptions simple ? simple.SessionId : null;
        var cacheRetention = options is SimpleStreamOptions simpleOptions ? simpleOptions.CacheRetention : null;

        if (!string.IsNullOrEmpty(sessionId) && cacheRetention != CacheRetentions.None)
        {
            lock (_gate)
            {
                if (_promptCache.TryGetValue(sessionId, out var previousPrompt))
                {
                    var cachedCharacters = CommonPrefixLength(previousPrompt, promptText);
                    cacheRead = EstimateTokens(previousPrompt[..cachedCharacters]);
                    cacheWrite = EstimateTokens(promptText[cachedCharacters..]);
                    input = Math.Max(0, promptTokens - cacheRead);
                }
                else
                {
                    cacheWrite = promptTokens;
                }

                _promptCache[sessionId] = promptText;
            }
        }

        return message with
        {
            Usage = new Usage
            {
                Input = input,
                Output = outputTokens,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                TotalTokens = input + outputTokens + cacheRead + cacheWrite,
                Cost = new UsageCost(),
            },
        };
    }

    private static string SerializeContext(Context context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            parts.Add($"system:{context.SystemPrompt}");
        }

        foreach (var message in context.Messages)
        {
            parts.Add($"{message.Role}:{MessageToText(message)}");
        }

        if (context.Tools.Count > 0)
        {
            parts.Add($"tools:{SerializeTools(context.Tools)}");
        }

        return string.Join("\n\n", parts);
    }

    private static string SerializeTools(IEnumerable<Tool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var serialized = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
            };
            if (tool.ConstrainedSampling is not null)
            {
                serialized["constrainedSampling"] = SerializeConstrainedSampling(tool.ConstrainedSampling);
            }

            array.Add((JsonNode)serialized);
        }

        return array.ToJsonString();
    }

    private static JsonObject SerializeConstrainedSampling(ConstrainedSamplingConfig sampling) => sampling switch
    {
        JsonSchemaSampling jsonSchema => new JsonObject
        {
            ["type"] = "json_schema",
            ["strict"] = jsonSchema.Strict,
        },
        GrammarSampling grammar => new JsonObject
        {
            ["type"] = "grammar",
            ["variants"] = new JsonObject(grammar.Variants.ToDictionary(
                static pair => pair.Key,
                static pair => (JsonNode?)pair.Value,
                StringComparer.Ordinal)),
        },
        _ => throw new InvalidOperationException($"Unsupported constrained sampling type: {sampling.GetType().Name}"),
    };

    private static string MessageToText(Message message) => message switch
    {
        UserMessage user => ContentToText(user.Content),
        AssistantMessage assistant => AssistantContentToText(assistant.Content),
        ToolResultMessage toolResult => string.Join(
            "\n",
            new[] { toolResult.ToolName, ContentToText(toolResult.Content) }),
        _ => string.Empty,
    };

    private static string ContentToText(object content) => content switch
    {
        string text => text,
        IEnumerable<ContentBlock> blocks => string.Join(
            "\n",
            blocks.Select(block => block switch
            {
                TextContent textBlock => textBlock.Text,
                ImageContent imageBlock => $"[image:{imageBlock.MimeType}:{imageBlock.Data.Length}]",
                _ => string.Empty,
            })),
        _ => string.Empty,
    };

    private static string AssistantContentToText(IEnumerable<ContentBlock> content) =>
        string.Join(
            "\n",
            content.Select(block => block switch
            {
                TextContent text => text.Text,
                ThinkingContent thinking => thinking.Thinking,
                ToolCall toolCall => $"{toolCall.Name}:{toolCall.Arguments.ToJsonString()}",
                _ => string.Empty,
            }));

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4d);

    private static int CommonPrefixLength(string first, string second)
    {
        var length = Math.Min(first.Length, second.Length);
        var index = 0;
        while (index < length && first[index] == second[index])
        {
            index++;
        }

        return index;
    }

    private async Task StreamWithDeltasAsync(
        AssistantMessageEventStream stream,
        AssistantMessage message,
        CancellationToken cancellationToken)
    {
        var partial = message with
        {
            Content = [],
            StopReason = StopReasons.Pending,
        };
        if (cancellationToken.IsCancellationRequested)
        {
            var aborted = CreateAbortedMessage(partial);
            stream.Push(new StreamErrorEvent(StopReasons.Aborted, aborted));
            stream.End(aborted);
            return;
        }

        stream.Push(new StreamStartEvent(partial));
        for (var index = 0; index < message.Content.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var aborted = CreateAbortedMessage(partial);
                stream.Push(new StreamErrorEvent(StopReasons.Aborted, aborted));
                stream.End(aborted);
                return;
            }

            var block = message.Content[index];
            var content = partial.Content.ToList();
            switch (block)
            {
                case ThinkingContent thinking:
                    content.Add(new ThinkingContent(string.Empty));
                    partial = partial with { Content = content.ToArray() };
                    stream.Push(new ThinkingStartEvent(index, partial));
                    foreach (var chunk in SplitStringByTokenSize(thinking.Thinking))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            var aborted = CreateAbortedMessage(partial);
                            stream.Push(new StreamErrorEvent(StopReasons.Aborted, aborted));
                            stream.End(aborted);
                            return;
                        }

                        var current = ((ThinkingContent)partial.Content[index]).Thinking + chunk;
                        content = partial.Content.ToList();
                        content[index] = new ThinkingContent(current);
                        partial = partial with { Content = content.ToArray() };
                        stream.Push(new ThinkingDeltaEvent(index, chunk, partial));
                    }

                    stream.Push(new ThinkingEndEvent(index, thinking.Thinking, partial));
                    break;

                case TextContent text:
                    content.Add(new TextContent(string.Empty));
                    partial = partial with { Content = content.ToArray() };
                    stream.Push(new TextStartEvent(index, partial));
                    foreach (var chunk in SplitStringByTokenSize(text.Text))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            var aborted = CreateAbortedMessage(partial);
                            stream.Push(new StreamErrorEvent(StopReasons.Aborted, aborted));
                            stream.End(aborted);
                            return;
                        }

                        var current = ((TextContent)partial.Content[index]).Text + chunk;
                        content = partial.Content.ToList();
                        content[index] = new TextContent(current);
                        partial = partial with { Content = content.ToArray() };
                        stream.Push(new TextDeltaEvent(index, chunk, partial));
                    }

                    stream.Push(new TextEndEvent(index, text.Text, partial));
                    break;

                case ToolCall toolCall:
                    content.Add(new ToolCall(toolCall.Id, toolCall.Name, new JsonObject()));
                    partial = partial with { Content = content.ToArray() };
                    stream.Push(new ToolCallStartEvent(index, partial));
                    foreach (var chunk in SplitStringByTokenSize(toolCall.Arguments.ToJsonString()))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            var aborted = CreateAbortedMessage(partial);
                            stream.Push(new StreamErrorEvent(StopReasons.Aborted, aborted));
                            stream.End(aborted);
                            return;
                        }

                        stream.Push(new ToolCallDeltaEvent(index, chunk, partial));
                    }

                    content = partial.Content.ToList();
                    content[index] = toolCall with { Arguments = toolCall.Arguments.DeepClone().AsObject() };
                    partial = partial with { Content = content.ToArray() };
                    stream.Push(new ToolCallEndEvent(index, toolCall, partial));
                    break;

                case ImageContent:
                    content.Add(FauxMessages.CloneContentBlock(block));
                    partial = partial with { Content = content.ToArray() };
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported faux content block: {block.GetType().Name}");
            }
        }

        if (message.StopReason == StopReasons.Pending)
        {
            throw new InvalidOperationException("Faux response ended without a stop reason");
        }

        if (message.StopReason is StopReasons.Error or StopReasons.Aborted)
        {
            stream.Push(new StreamErrorEvent(message.StopReason, message));
            stream.End(message);
            return;
        }

        stream.Push(new StreamDoneEvent(message.StopReason, message));
        stream.End(message);
    }

    private List<string> SplitStringByTokenSize(string text)
    {
        var chunks = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var tokenSize = Random.Shared.Next(_minTokenSize, _maxTokenSize + 1);
            var characterSize = Math.Max(1, tokenSize * 4);
            chunks.Add(text.Substring(index, Math.Min(characterSize, text.Length - index)));
            index += characterSize;
        }

        return chunks.Count > 0 ? chunks : [string.Empty];
    }

    private async Task ScheduleChunkAsync(string chunk)
    {
        if (!_tokensPerSecond.HasValue || _tokensPerSecond <= 0)
        {
            await Task.Yield();
            return;
        }

        var delay = TimeSpan.FromSeconds(EstimateTokens(chunk) / _tokensPerSecond.Value);
        await Task.Delay(delay).ConfigureAwait(false);
    }

    private static AssistantMessage CreateDeferredMessage(Model model, DeferredHandle handle) => new()
    {
        Content = [],
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Usage = new Usage { Cost = new UsageCost() },
        StopReason = StopReasons.Deferred,
        Deferred = FauxMessages.CloneHandle(handle),
        Timestamp = FauxMessages.Now(),
    };

    private static AssistantMessage CreateErrorMessage(Exception error, Model model) =>
        CreateErrorMessage(error, model.Api, model.Provider, model.Id);

    private static AssistantMessage CreateErrorMessage(
        Exception error,
        string api,
        string provider,
        string modelId) => new()
        {
            Content = [],
            Api = api,
            Provider = provider,
            Model = modelId,
            Usage = new Usage { Cost = new UsageCost() },
            StopReason = StopReasons.Error,
            ErrorMessage = error.Message,
            Timestamp = FauxMessages.Now(),
        };

    private static AssistantMessage CreateAbortedMessage(AssistantMessage partial) => partial with
    {
        StopReason = StopReasons.Aborted,
        ErrorMessage = "Request was aborted",
        Timestamp = FauxMessages.Now(),
    };

    private static SimpleStreamOptions WithoutSubmissionOnlyOptions(SimpleStreamOptions options) => new()
    {
        ApiKey = options.ApiKey,
        Fetch = options.Fetch,
        Environment = options.Environment,
        OnPayload = options.OnPayload,
        OnResponse = null,
        Headers = options.Headers,
        TimeoutMs = options.TimeoutMs,
        MaxRetries = options.MaxRetries,
        MaxRetryDelayMs = options.MaxRetryDelayMs,
        TelemetryContext = options.TelemetryContext,
        Temperature = options.Temperature,
        SamplingParameters = options.SamplingParameters,
        MaxTokens = options.MaxTokens,
        Transport = options.Transport,
        CacheRetention = options.CacheRetention,
        SessionId = options.SessionId,
        WebSocketConnectTimeoutMs = options.WebSocketConnectTimeoutMs,
        Metadata = options.Metadata,
        ToolChoice = options.ToolChoice,
        Reasoning = options.Reasoning,
        Deferred = false,
        DeferredWindow = options.DeferredWindow,
        ThinkingBudgets = options.ThinkingBudgets,
    };

    private sealed class DeferredEntry
    {
        public required DeferredHandle Handle { get; init; }

        public required FauxResponseStep Step { get; init; }

        public required Context Context { get; init; }

        public required SimpleStreamOptions Options { get; init; }

        public required Model Model { get; init; }

        public int PendingFetches { get; set; }

        public bool Cancelled { get; set; }

        public AssistantMessage? Final { get; set; }
    }
}

/// <summary>Registration handle for a faux provider.</summary>
public sealed class FauxProviderRegistration : IDisposable
{
    private readonly Action _unregister;
    private bool _isRegistered = true;

    internal FauxProviderRegistration(FauxProvider provider, Action unregister)
    {
        Provider = provider;
        _unregister = unregister;
    }

    /// <summary>The registered faux provider.</summary>
    public FauxProvider Provider { get; }

    /// <summary>Provider API identifier.</summary>
    public string Api => Provider.Api;

    /// <summary>Configured models.</summary>
    public IReadOnlyList<Model> Models => Provider.Models;

    /// <summary>Mutable provider state.</summary>
    public FauxProviderState State => Provider.State;

    /// <summary>Returns the first configured model.</summary>
    public Model GetModel() => Provider.GetModel();

    /// <summary>Looks up a model by identifier.</summary>
    public Model? GetModel(string modelId) => Provider.GetModel(modelId);

    /// <summary>Replaces queued responses.</summary>
    public void SetResponses(IEnumerable<FauxResponseStep> responses) => Provider.SetResponses(responses);

    /// <summary>Appends queued responses.</summary>
    public void AppendResponses(IEnumerable<FauxResponseStep> responses) => Provider.AppendResponses(responses);

    /// <summary>Returns the number of pending responses.</summary>
    public int GetPendingResponseCount() => Provider.GetPendingResponseCount();

    /// <summary>Stops exposing this provider through its registration.</summary>
    public void Unregister()
    {
        if (_isRegistered)
        {
            _isRegistered = false;
            _unregister();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Unregister();
}

/// <summary>Creates and tracks deterministic faux provider registrations.</summary>
public static class FauxProviderFactory
{
    private static readonly Dictionary<string, FauxProviderRegistration> _registrations = new(StringComparer.Ordinal);
    private static readonly object _gate = new();

    /// <summary>Registers a faux provider.</summary>
    public static FauxProviderRegistration RegisterFauxProvider(RegisterFauxProviderOptions? options = null)
    {
        var provider = new FauxProvider(options);
        FauxProviderRegistration? registration = null;
        registration = new FauxProviderRegistration(provider, () =>
        {
            lock (_gate)
            {
                if (_registrations.TryGetValue(provider.Api, out var current) && ReferenceEquals(current, registration))
                {
                    _registrations.Remove(provider.Api);
                }
            }
        });
        lock (_gate)
        {
            _registrations[provider.Api] = registration;
        }

        return registration;
    }

    /// <summary>Returns a currently registered faux provider by API identifier.</summary>
    public static FauxProviderRegistration? GetRegistration(string api)
    {
        lock (_gate)
        {
            return _registrations.TryGetValue(api, out var registration) ? registration : null;
        }
    }
}
