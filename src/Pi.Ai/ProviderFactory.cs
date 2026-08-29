using System.Diagnostics.CodeAnalysis;

namespace Pi.Ai;

/// <summary>Concrete provider runtime unit used by <see cref="ModelsRuntime"/>.</summary>
public sealed class Provider
{
    private readonly Func<IReadOnlyList<Model>> _getModels;
    private readonly Func<Model, Context, StreamOptions?, AssistantMessageEventStream> _stream;
    private readonly Func<Model, Context, SimpleStreamOptions?, AssistantMessageEventStream> _streamSimple;
    private readonly Func<Model, DeferredHandle, DeferredFetchOptions?, AssistantMessageEventStream?>? _fetchDeferred;
    private readonly Func<Model, DeferredHandle, DeferredCancelOptions?, Task>? _cancelDeferred;

    /// <summary>Creates a provider backed by one API implementation.</summary>
    public Provider(
        string id,
        string name,
        ProviderAuth auth,
        Func<IReadOnlyList<Model>> getModels,
        ProviderStreams streams,
        string? baseUrl = null,
        ProviderHeaders? headers = null,
        Func<RefreshModelsContext, Task>? refreshModels = null,
        Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? filterModels = null)
        : this(
            id,
            name,
            auth,
            getModels,
            streams.Stream,
            streams.StreamSimple,
            baseUrl,
            headers,
            refreshModels,
            filterModels,
            streams is DeferredProviderStreams
                ? (model, handle, options) => streams.FetchDeferred(model, handle, options)
                : null,
            streams is DeferredProviderStreams ? streams.CancelDeferredAsync : null)
    {
    }

    internal Provider(
        string id,
        string name,
        ProviderAuth auth,
        Func<IReadOnlyList<Model>> getModels,
        Func<Model, Context, StreamOptions?, AssistantMessageEventStream> stream,
        Func<Model, Context, SimpleStreamOptions?, AssistantMessageEventStream> streamSimple,
        string? baseUrl,
        ProviderHeaders? headers,
        Func<RefreshModelsContext, Task>? refreshModels,
        Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? filterModels,
        Func<Model, DeferredHandle, DeferredFetchOptions?, AssistantMessageEventStream?>? fetchDeferred,
        Func<Model, DeferredHandle, DeferredCancelOptions?, Task>? cancelDeferred)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(getModels);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(streamSimple);
        Id = id;
        Name = name;
        Auth = auth;
        BaseUrl = baseUrl;
        Headers = headers;
        _getModels = getModels;
        _stream = stream;
        _streamSimple = streamSimple;
        RefreshModels = refreshModels;
        FilterModels = filterModels;
        _fetchDeferred = fetchDeferred;
        _cancelDeferred = cancelDeferred;
    }

    /// <summary>Provider identifier.</summary>
    public string Id { get; }

    /// <summary>Provider display name.</summary>
    public string Name { get; }

    /// <summary>Provider base URL override.</summary>
    public string? BaseUrl { get; }

    /// <summary>Provider-wide static headers.</summary>
    public ProviderHeaders? Headers { get; }

    /// <summary>Provider authentication implementations.</summary>
    public ProviderAuth Auth { get; }

    /// <summary>Dynamic model refresh implementation, when present.</summary>
    public Func<RefreshModelsContext, Task>? RefreshModels { get; }

    /// <summary>Credential-specific model filtering policy.</summary>
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels { get; }

    /// <summary>Whether this provider exposes deferred response fetching.</summary>
    public bool SupportsDeferredFetch => _fetchDeferred is not null;

    /// <summary>Whether this provider exposes deferred response cancellation.</summary>
    public bool SupportsDeferredCancel => _cancelDeferred is not null;

    /// <summary>Returns the provider's current model catalog.</summary>
    public IReadOnlyList<Model> GetModels() => _getModels();

    /// <summary>Starts a provider stream.</summary>
    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        _stream(model, context, options);

    /// <summary>Starts a provider-neutral simple stream.</summary>
    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
        _streamSimple(model, context, options);

    /// <summary>Starts a deferred-response fetch, when supported.</summary>
    public AssistantMessageEventStream? FetchDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null) =>
        _fetchDeferred?.Invoke(model, handle, options);

    /// <summary>Cancels a deferred response, when supported.</summary>
    public Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options = null) =>
        _cancelDeferred?.Invoke(model, handle, options) ?? Task.CompletedTask;
}

/// <summary>Marker for provider streams that implement deferred operations.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "Preserves the upstream provider capability concept.")]
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream provider capability concept.")]
public interface DeferredProviderStreams : ProviderStreams
{
}

/// <summary>Parts used to construct a provider with one or more API adapters.</summary>
public sealed class CreateProviderOptions
{
    /// <summary>Provider identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display name; defaults to <see cref="Id"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Provider base URL.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Provider-wide static headers.</summary>
    public ProviderHeaders? Headers { get; init; }

    /// <summary>Provider authentication implementations.</summary>
    public required ProviderAuth Auth { get; init; }

    /// <summary>Static baseline model list.</summary>
    public IReadOnlyList<Model> Models { get; init; } = [];

    /// <summary>Optional dynamic model overlay loader.</summary>
    public Func<RefreshModelsContext, Task<IReadOnlyList<Model>>>? FetchModels { get; init; }

    /// <summary>Optional credential-specific model filter.</summary>
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels { get; init; }

    /// <summary>One API implementation for every model.</summary>
    public ProviderStreams? Api { get; init; }

    /// <summary>API implementation map keyed by model API identifier.</summary>
    public IReadOnlyDictionary<string, ProviderStreams>? ApiBy { get; init; }
}

/// <summary>Builds providers from static/dynamic model data and API stream implementations.</summary>
public static class ProviderFactory
{
    /// <summary>
    /// Builds a provider. A single <see cref="CreateProviderOptions.Api"/> or a keyed
    /// <see cref="CreateProviderOptions.ApiBy"/> map must be supplied.
    /// </summary>
    public static Provider CreateProvider(CreateProviderOptions input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if ((input.Api is null) == (input.ApiBy is null))
        {
            throw new ArgumentException("Exactly one provider API implementation or API map is required.", nameof(input));
        }

        var baselineModels = input.Models;
        var dynamicModels = Array.Empty<Model>();
        var modelsGate = new object();

        IReadOnlyList<Model> CurrentModels()
        {
            lock (modelsGate)
            {
                var merged = baselineModels.ToList();
                foreach (var model in dynamicModels)
                {
                    var index = merged.FindIndex(entry => string.Equals(entry.Id, model.Id, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        merged[index] = model;
                    }
                    else
                    {
                        merged.Add(model);
                    }
                }

                return merged;
            }
        }

        ProviderStreams? SingleApi() => input.Api;
        ProviderStreams? ApiFor(Model model) => SingleApi() ??
            (input.ApiBy is not null && input.ApiBy.TryGetValue(model.Api, out var streams) ? streams : null);

        AssistantMessageEventStream MissingApi(Model model) => LazyStreams.Create(
            model,
            () => Task.FromException<AssistantMessageEventStream>(
                new ModelsError(
                    ModelsErrorCodes.Stream,
                    $"Provider {input.Id} has no API implementation for \"{model.Api}\"")));

        AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options) =>
            ApiFor(model)?.Stream(model, context, options) ?? MissingApi(model);

        AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options) =>
            ApiFor(model)?.StreamSimple(model, context, options) ?? MissingApi(model);

        Func<RefreshModelsContext, Task>? refreshModels = null;
        if (input.FetchModels is not null)
        {
            refreshModels = async context =>
            {
                if (context.Stored is not null)
                {
                    var restored = context.Stored.Models
                        .Where(model => string.Equals(model.Provider, input.Id, StringComparison.Ordinal))
                        .ToArray();
                    var restoredPublished = await context.Publish(
                            new ModelsPublication
                            {
                                PersistSpecified = false,
                                Update = () =>
                                {
                                    lock (modelsGate)
                                    {
                                        dynamicModels = restored;
                                    }
                                },
                            })
                        .ConfigureAwait(false);
                    if (!restoredPublished)
                    {
                        return;
                    }
                }

                if (!context.AllowNetwork || context.Signal.IsCancellationRequested)
                {
                    return;
                }

                var refreshed = await input.FetchModels(context).ConfigureAwait(false);
                if (context.Signal.IsCancellationRequested)
                {
                    return;
                }

                await context.Publish(
                        new ModelsPublication
                        {
                            PersistSpecified = true,
                            Persist = new ModelsStoreEntry
                            {
                                Models = refreshed,
                                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            },
                            Update = () =>
                            {
                                lock (modelsGate)
                                {
                                    dynamicModels = refreshed.ToArray();
                                }
                            },
                        })
                    .ConfigureAwait(false);
            };
        }

        var allStreams = input.Api is not null
            ? [input.Api]
            : input.ApiBy?.Values.Where(static value => value is not null).ToArray() ?? [];
        var deferredStreams = allStreams.OfType<DeferredProviderStreams>().ToArray();
        Func<Model, DeferredHandle, DeferredFetchOptions?, AssistantMessageEventStream>? fetchDeferred = null;
        Func<Model, DeferredHandle, DeferredCancelOptions?, Task>? cancelDeferred = null;
        if (deferredStreams.Length > 0)
        {
            fetchDeferred = (model, handle, options) =>
                ApiFor(model) is DeferredProviderStreams streams
                    ? streams.FetchDeferred(model, handle, options)
                        ?? LazyStreams.Create(
                            model,
                            () => Task.FromException<AssistantMessageEventStream>(
                                new ModelsError(
                                    ModelsErrorCodes.Provider,
                                    $"Provider {input.Id} does not support deferred responses for \"{model.Api}\"")))
                    : LazyStreams.Create(
                        model,
                        () => Task.FromException<AssistantMessageEventStream>(
                            new ModelsError(
                                ModelsErrorCodes.Provider,
                                $"Provider {input.Id} does not support deferred responses for \"{model.Api}\"")));
            cancelDeferred = (model, handle, options) =>
                ApiFor(model) is DeferredProviderStreams streams
                    ? streams.CancelDeferredAsync(model, handle, options)
                    : Task.FromException(
                        new ModelsError(
                            ModelsErrorCodes.Provider,
                            $"Provider {input.Id} cannot cancel deferred responses for \"{model.Api}\""));
        }

        return new Provider(
            input.Id,
            input.Name ?? input.Id,
            input.Auth,
            CurrentModels,
            Stream,
            StreamSimple,
            input.BaseUrl,
            input.Headers,
            refreshModels,
            input.FilterModels,
            fetchDeferred,
            cancelDeferred);
    }
}
