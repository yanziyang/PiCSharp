using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Pi.Ai;

/// <summary>Provider-owned publication of a dynamic model catalog.</summary>
public sealed class ModelsPublication
{
    /// <summary>Whether persistence was explicitly requested for this publication.</summary>
    public bool PersistSpecified { get; init; }

    /// <summary>Catalog to persist; null with <see cref="PersistSpecified"/> deletes the catalog.</summary>
    public ModelsStoreEntry? Persist { get; init; }

    /// <summary>Synchronous update of provider-private in-memory state after persistence.</summary>
    public Action? Update { get; init; }
}

/// <summary>Context supplied to a provider dynamic-model refresh callback.</summary>
public sealed class RefreshModelsContext
{
    /// <summary>Effective configured credential, when network access is enabled.</summary>
    public Credential? Credential { get; init; }

    /// <summary>Provider-scoped persisted catalog captured before this phase.</summary>
    public ModelsStoreEntry? Stored { get; init; }

    /// <summary>Generation-checked publication operation.</summary>
    public required Func<ModelsPublication, Task<bool>> Publish { get; init; }

    /// <summary>False during offline/cache-only initialization.</summary>
    public bool AllowNetwork { get; init; }

    /// <summary>Whether provider freshness checks should be bypassed.</summary>
    public bool? Force { get; init; }

    /// <summary>Shared provider refresh cancellation token.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>Options for refreshing dynamic provider model catalogs.</summary>
public sealed class ModelsRefreshOptions
{
    /// <summary>Whether network refresh is allowed; defaults to true.</summary>
    public bool? AllowNetwork { get; init; }

    /// <summary>Provider ids to refresh; null selects all dynamic providers.</summary>
    public IReadOnlyList<string>? Providers { get; init; }

    /// <summary>Whether provider freshness checks should be bypassed.</summary>
    public bool? Force { get; init; }

    /// <summary>Optional operation cancellation.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>Result of a best-effort dynamic model refresh.</summary>
public sealed record ModelsRefreshResult
{
    /// <summary>Whether the caller's refresh operation was aborted.</summary>
    public bool Aborted { get; init; }

    /// <summary>Provider failures that were not caused by cancellation.</summary>
    public IReadOnlyDictionary<string, Exception> Errors { get; init; } =
        new Dictionary<string, Exception>(StringComparer.Ordinal);
}

/// <summary>Request-header transformation hook used by the Models facade.</summary>
public sealed class ModelsRequestTransforms
{
    /// <summary>Transforms fully assembled headers immediately before provider dispatch.</summary>
    public Func<ProviderHeaders, Task<ProviderHeaders>>? TransformHeaders { get; init; }
}

/// <summary>API stream options accepted by the Models facade.</summary>
public sealed class ModelsApiStreamOptions : StreamOptions
{
    /// <summary>Transforms fully assembled request headers.</summary>
    public Func<ProviderHeaders, Task<ProviderHeaders>>? TransformHeaders { get; init; }
}

/// <summary>Simple-stream options accepted by the Models facade.</summary>
public sealed class ModelsSimpleStreamOptions : StreamOptions
{
    /// <summary>Transforms fully assembled request headers.</summary>
    public Func<ProviderHeaders, Task<ProviderHeaders>>? TransformHeaders { get; init; }

    /// <summary>Provider-neutral tool selection.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Requested reasoning level.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Whether to request a deferred response.</summary>
    public bool Deferred { get; init; }

    /// <summary>Deferred response window.</summary>
    public string? DeferredWindow { get; init; }

    /// <summary>Custom reasoning token budgets.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}

/// <summary>Deferred-fetch options accepted by the Models facade.</summary>
public sealed class ModelsDeferredFetchOptions : ProviderRequestOptions
{
    /// <summary>Transforms fully assembled request headers.</summary>
    public Func<ProviderHeaders, Task<ProviderHeaders>>? TransformHeaders { get; init; }

    /// <summary>Maximum provider long-poll duration in milliseconds.</summary>
    public int Wait { get; init; }
}

/// <summary>Deferred-cancel options accepted by the Models facade.</summary>
public sealed class ModelsDeferredCancelOptions : ProviderRequestOptions
{
    /// <summary>Transforms fully assembled request headers.</summary>
    public Func<ProviderHeaders, Task<ProviderHeaders>>? TransformHeaders { get; init; }
}

/// <summary>Construction dependencies for the Models runtime.</summary>
public sealed class CreateModelsOptions
{
    /// <summary>Credential store; defaults to an in-memory store.</summary>
    public CredentialStore? Credentials { get; init; }

    /// <summary>Dynamic model store; defaults to an in-memory store.</summary>
    public ModelsStore? ModelsStore { get; init; }

    /// <summary>Auth environment; defaults to process environment/local filesystem.</summary>
    public AuthContext? AuthContext { get; init; }
}

/// <summary>Runtime collection of providers with auth and stream convenience methods.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi Models contract name.")]
public interface Models
{
    /// <summary>Returns providers in registration order.</summary>
    IReadOnlyList<Provider> GetProviders();

    /// <summary>Looks up a provider by id.</summary>
    Provider? GetProvider(string id);

    /// <summary>Returns last-known models from one provider or all providers.</summary>
    IReadOnlyList<Model> GetModels(string? providerId = null);

    /// <summary>Looks up a model in one provider's last-known catalog.</summary>
    Model? GetModel(string providerId, string modelId);

    /// <summary>Refreshes selected dynamic provider catalogs.</summary>
    Task<ModelsRefreshResult> RefreshAsync(
        ModelsRefreshOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Checks provider auth without refreshing OAuth credentials.</summary>
    Task<AuthCheck?> CheckAuthAsync(
        string providerId,
        AuthOperationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns models whose providers have configured auth.</summary>
    Task<IReadOnlyList<Model>> GetAvailableAsync(
        string? providerId = null,
        AuthOperationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves auth for a provider id.</summary>
    Task<AuthResult?> GetAuthAsync(
        string providerId,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves auth for a model and merges model-scoped headers.</summary>
    Task<AuthResult?> GetAuthAsync(
        Model model,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs provider login and persists its returned credential.</summary>
    Task<Credential> LoginAsync(
        string providerId,
        string type,
        AuthInteraction interaction,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a provider credential.</summary>
    Task LogoutAsync(
        string providerId,
        AuthOperationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an authenticated API stream.</summary>
    AssistantMessageEventStream Stream(Model model, Context context, ModelsApiStreamOptions? options = null);

    /// <summary>Completes an authenticated API request.</summary>
    Task<AssistantMessage> CompleteAsync(
        Model model,
        Context context,
        ModelsApiStreamOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an authenticated simple stream.</summary>
    AssistantMessageEventStream StreamSimple(Model model, Context context, ModelsSimpleStreamOptions? options = null);

    /// <summary>Completes an authenticated simple request.</summary>
    Task<AssistantMessage> CompleteSimpleAsync(
        Model model,
        Context context,
        ModelsSimpleStreamOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a deferred provider response.</summary>
    Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        ModelsDeferredFetchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a deferred provider response.</summary>
    Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        ModelsDeferredCancelOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Mutable provider collection used while registering built-in adapters.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi Models contract name.")]
public interface MutableModels : Models
{
    /// <summary>Adds or replaces a provider by id.</summary>
    void SetProvider(Provider provider);

    /// <summary>Deletes a provider by id.</summary>
    void DeleteProvider(string id);

    /// <summary>Stops refreshes and removes all providers.</summary>
    void ClearProviders();
}

/// <summary>Default Models runtime implementation.</summary>
public sealed class ModelsRuntime : MutableModels
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _refreshGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, CancellationTokenSource Controller)> _refreshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _publicationChains = new(StringComparer.Ordinal);
    private readonly CredentialStore _credentials;
    private readonly ModelsStore _modelsStore;
    private readonly AuthContext _authContext;

    /// <summary>Creates a Models runtime with optional application-owned dependencies.</summary>
    public ModelsRuntime(CreateModelsOptions? options = null)
    {
        _credentials = options?.Credentials ?? new InMemoryCredentialStore();
        _modelsStore = options?.ModelsStore ?? new InMemoryModelsStore();
        _authContext = options?.AuthContext ?? AuthContextFactory.CreateDefaultProviderContext();
    }

    /// <inheritdoc />
    public void SetProvider(Provider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        SupersedeProviderRefresh(provider.Id);
        lock (_gate)
        {
            _providers[provider.Id] = provider;
        }
    }

    /// <inheritdoc />
    public void DeleteProvider(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        SupersedeProviderRefresh(id);
        lock (_gate)
        {
            _providers.Remove(id);
        }
    }

    /// <inheritdoc />
    public void ClearProviders()
    {
        string[] ids;
        lock (_gate)
        {
            ids = _providers.Keys.Concat(_refreshes.Keys).Distinct(StringComparer.Ordinal).ToArray();
        }

        foreach (var id in ids)
        {
            SupersedeProviderRefresh(id);
        }

        lock (_gate)
        {
            _providers.Clear();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Provider> GetProviders()
    {
        lock (_gate)
        {
            return _providers.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public Provider? GetProvider(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_gate)
        {
            return _providers.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Model> GetModels(string? providerId = null)
    {
        if (providerId is not null)
        {
            var provider = GetProvider(providerId);
            if (provider is null)
            {
                return [];
            }

            try
            {
                return provider.GetModels();
            }
            catch
            {
                return [];
            }
        }

        var models = new List<Model>();
        foreach (var provider in GetProviders())
        {
            try
            {
                models.AddRange(provider.GetModels());
            }
            catch
            {
                // Best-effort catalog listing matches the upstream runtime.
            }
        }

        return models;
    }

    /// <inheritdoc />
    public Model? GetModel(string providerId, string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        return GetModels(providerId).FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<ModelsRefreshResult> RefreshAsync(
        ModelsRefreshOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ModelsRefreshOptions();
        using var signalSource = Link(options.Signal, cancellationToken);
        var callerSignal = signalSource.Token;
        var errors = new ConcurrentDictionary<string, Exception>(StringComparer.Ordinal);
        if (callerSignal.IsCancellationRequested)
        {
            return new ModelsRefreshResult { Aborted = true, Errors = errors };
        }

        var selected = options.Providers is null
            ? null
            : options.Providers.ToHashSet(StringComparer.Ordinal);
        var providers = GetProviders()
            .Where(provider => provider.RefreshModels is not null &&
                (selected is null || selected.Contains(provider.Id)))
            .ToArray();
        var operations = providers
            .Select(provider => RefreshProviderAsync(provider, options, errors, callerSignal))
            .ToArray();
        var all = Task.WhenAll(operations);
        try
        {
            await all.WaitAsync(callerSignal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerSignal.IsCancellationRequested)
        {
            _ = all.Exception;
        }

        return new ModelsRefreshResult
        {
            Aborted = callerSignal.IsCancellationRequested,
            Errors = new Dictionary<string, Exception>(errors, StringComparer.Ordinal),
        };
    }

    private async Task RefreshProviderAsync(
        Provider provider,
        ModelsRefreshOptions options,
        ConcurrentDictionary<string, Exception> errors,
        CancellationToken callerSignal)
    {
        var refresh = BeginProviderRefresh(provider.Id);
        using var signalSource = Link(callerSignal, refresh.Controller.Token);
        var signal = signalSource.Token;
        try
        {
            Credential? storedCredential = null;
            Exception? credentialError = null;
            try
            {
                storedCredential = await ReadCredentialAsync(provider.Id, signal).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                credentialError = error;
            }

            await RunProviderRefreshPhaseAsync(
                    provider,
                    storedCredential,
                    allowNetwork: false,
                    force: null,
                    refresh.Generation,
                    signal)
                .ConfigureAwait(false);
            if (credentialError is not null)
            {
                throw credentialError;
            }

            if (options.AllowNetwork == false || signal.IsCancellationRequested)
            {
                return;
            }

            var credential = await ResolveRefreshCredentialAsync(provider, storedCredential, signal).ConfigureAwait(false);
            if (credential is null || signal.IsCancellationRequested)
            {
                return;
            }

            await RunProviderRefreshPhaseAsync(
                    provider,
                    credential,
                    allowNetwork: true,
                    options.Force,
                    refresh.Generation,
                    signal)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (signal.IsCancellationRequested)
        {
            // Cancellation is represented by ModelsRefreshResult.Aborted, not a provider error.
        }
        catch (Exception error)
        {
            if (!signal.IsCancellationRequested)
            {
                errors[provider.Id] = error;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_refreshes.TryGetValue(provider.Id, out var current) &&
                    current.Generation == refresh.Generation &&
                    ReferenceEquals(current.Controller, refresh.Controller))
                {
                    _refreshes.Remove(provider.Id);
                }
            }
        }
    }

    private async Task RunProviderRefreshPhaseAsync(
        Provider provider,
        Credential? credential,
        bool allowNetwork,
        bool? force,
        long generation,
        CancellationToken signal)
    {
        var stored = await _modelsStore.ReadAsync(provider.Id, signal).ConfigureAwait(false);
        var refresh = provider.RefreshModels ?? throw new InvalidOperationException("Provider is not dynamic.");
        await refresh(
                new RefreshModelsContext
                {
                    Credential = credential,
                    Stored = stored?.DeepCopy(),
                    Publish = publication => PublishProviderModelsAsync(provider.Id, generation, publication, signal),
                    AllowNetwork = allowNetwork,
                    Force = allowNetwork ? force : null,
                    Signal = signal,
                })
            .ConfigureAwait(false);
    }

    private Task<bool> PublishProviderModelsAsync(
        string providerId,
        long generation,
        ModelsPublication publication,
        CancellationToken signal)
    {
        Task previous;
        Task<bool> queued;
        Task? tail = null;
        lock (_gate)
        {
            previous = _publicationChains.GetValueOrDefault(providerId) ?? Task.CompletedTask;
            queued = PublishAfterAsync(previous, providerId, generation, publication, signal);
            tail = queued.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        if (_publicationChains.TryGetValue(providerId, out var current) && ReferenceEquals(current, tail))
                        {
                            _publicationChains.Remove(providerId);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            _publicationChains[providerId] = tail;
        }

        return queued.WaitAsync(signal);
    }

    private async Task<bool> PublishAfterAsync(
        Task previous,
        string providerId,
        long generation,
        ModelsPublication publication,
        CancellationToken signal)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed publication must not poison subsequent provider publications.
        }

        if (signal.IsCancellationRequested || !IsCurrentGeneration(providerId, generation))
        {
            return false;
        }

        if (publication.PersistSpecified)
        {
            if (publication.Persist is null)
            {
                await _modelsStore.DeleteAsync(providerId, signal).ConfigureAwait(false);
            }
            else
            {
                await _modelsStore.WriteAsync(providerId, publication.Persist.DeepCopy(), signal).ConfigureAwait(false);
            }
        }

        if (signal.IsCancellationRequested || !IsCurrentGeneration(providerId, generation))
        {
            return false;
        }

        publication.Update?.Invoke();
        return true;
    }

    private bool IsCurrentGeneration(string providerId, long generation)
    {
        lock (_gate)
        {
            return _refreshGenerations.TryGetValue(providerId, out var current) && current == generation;
        }
    }

    private (long Generation, CancellationTokenSource Controller) BeginProviderRefresh(string providerId)
    {
        lock (_gate)
        {
            var generation = SupersedeProviderRefreshLocked(providerId);
            var controller = new CancellationTokenSource();
            _refreshes[providerId] = (generation, controller);
            return (generation, controller);
        }
    }

    private long SupersedeProviderRefresh(string providerId)
    {
        lock (_gate)
        {
            return SupersedeProviderRefreshLocked(providerId);
        }
    }

    private long SupersedeProviderRefreshLocked(string providerId)
    {
        var generation = _refreshGenerations.GetValueOrDefault(providerId) + 1;
        _refreshGenerations[providerId] = generation;
        if (_refreshes.TryGetValue(providerId, out var current))
        {
            current.Controller.Cancel();
            _refreshes.Remove(providerId);
        }

        return generation;
    }

    private async Task<Credential?> ResolveRefreshCredentialAsync(
        Provider provider,
        Credential? stored,
        CancellationToken signal)
    {
        if (stored is OAuthCredential oauth)
        {
            var oauthAuth = provider.Auth.OAuth;
            if (oauthAuth is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < oauth.Expires)
            {
                return oauth;
            }

            var post = await _credentials
                .ModifyAsync(
                    provider.Id,
                    async current =>
                    {
                        if (current is not OAuthCredential currentOAuth ||
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < currentOAuth.Expires)
                        {
                            return null;
                        }

                        return await oauthAuth.Refresh(new OAuthRefreshInput { Credential = currentOAuth, Signal = signal })
                            .ConfigureAwait(false);
                    },
                    signal)
                .ConfigureAwait(false);
            return post is OAuthCredential refreshed ? refreshed : null;
        }

        var apiKey = provider.Auth.ApiKey;
        if (apiKey is null)
        {
            return null;
        }

        var credential = stored as ApiKeyCredential;
        var result = await apiKey.Resolve(
                new ApiKeyAuthResolveInput
                {
                    Context = _authContext,
                    Credential = credential,
                    Signal = signal,
                })
            .ConfigureAwait(false);
        return result is null
            ? null
            : new ApiKeyCredential { Key = result.Auth.ApiKey, Env = result.Env };
    }

    private async Task<Credential?> ReadCredentialAsync(string providerId, CancellationToken signal)
    {
        try
        {
            return await _credentials.ReadAsync(providerId, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(
                ModelsErrorCodes.Auth,
                $"Credential store read failed for {providerId}",
                error);
        }
    }

    /// <inheritdoc />
    public async Task<AuthCheck?> CheckAuthAsync(
        string providerId,
        AuthOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        using var signalSource = Link(options?.Signal ?? default, cancellationToken);
        var signal = signalSource.Token;
        signal.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId);
        if (provider is null)
        {
            return null;
        }

        var credential = await ReadCredentialAsync(providerId, signal).ConfigureAwait(false);
        if (credential is OAuthCredential)
        {
            return provider.Auth.OAuth is null
                ? null
                : new AuthCheck { Source = "OAuth", Type = AuthTypes.OAuth };
        }

        var apiKey = provider.Auth.ApiKey;
        if (apiKey is null)
        {
            return null;
        }

        if (apiKey.Check is not null)
        {
            try
            {
                return await apiKey.Check(
                        new ApiKeyAuthCheckInput
                        {
                            Context = _authContext,
                            Credential = credential as ApiKeyCredential,
                            Signal = signal,
                        })
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ModelsError(
                    ModelsErrorCodes.Auth,
                    $"API key auth check failed for provider {provider.Id}",
                    error);
            }
        }

        var resolution = await ProviderAuthResolver.ResolveAsync(
                new AuthProviderDescriptor { Id = provider.Id, Auth = provider.Auth },
                _credentials,
                _authContext,
                new AuthResolutionOverrides { Signal = signal },
                signal)
            .ConfigureAwait(false);
        return resolution is null
            ? null
            : new AuthCheck { Source = resolution.Source, Type = AuthTypes.ApiKey };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetAvailableAsync(
        string? providerId = null,
        AuthOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var signalSource = Link(options?.Signal ?? default, cancellationToken);
        var signal = signalSource.Token;
        signal.ThrowIfCancellationRequested();
        var providers = providerId is null
            ? GetProviders()
            : GetProvider(providerId) is { } entry ? [entry] : [];
        var available = new List<Model>();
        foreach (var provider in providers)
        {
            var credential = await ReadCredentialAsync(provider.Id, signal).ConfigureAwait(false);
            var auth = await CheckProviderAuthAsync(provider, credential, signal).ConfigureAwait(false);
            if (auth is null)
            {
                continue;
            }

            var models = provider.GetModels();
            available.AddRange(provider.FilterModels?.Invoke(models, credential) ?? models);
        }

        return available;
    }

    private async Task<AuthCheck?> CheckProviderAuthAsync(
        Provider provider,
        Credential? credential,
        CancellationToken signal)
    {
        if (credential is OAuthCredential)
        {
            return provider.Auth.OAuth is null
                ? null
                : new AuthCheck { Source = "OAuth", Type = AuthTypes.OAuth };
        }

        var apiKey = provider.Auth.ApiKey;
        if (apiKey is null)
        {
            return null;
        }

        if (apiKey.Check is not null)
        {
            try
            {
                return await apiKey.Check(
                        new ApiKeyAuthCheckInput
                        {
                            Context = _authContext,
                            Credential = credential as ApiKeyCredential,
                            Signal = signal,
                        })
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ModelsError(
                    ModelsErrorCodes.Auth,
                    $"API key auth check failed for provider {provider.Id}",
                    error);
            }
        }

        var result = await ProviderAuthResolver.ResolveAsync(
                new AuthProviderDescriptor { Id = provider.Id, Auth = provider.Auth },
                _credentials,
                _authContext,
                new AuthResolutionOverrides { Signal = signal },
                signal)
            .ConfigureAwait(false);
        return result is null ? null : new AuthCheck { Source = result.Source, Type = AuthTypes.ApiKey };
    }

    /// <inheritdoc />
    public Task<AuthResult?> GetAuthAsync(
        string providerId,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default) =>
        GetAuthCoreAsync(providerId, null, overrides, cancellationToken);

    /// <inheritdoc />
    public Task<AuthResult?> GetAuthAsync(
        Model model,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return GetAuthCoreAsync(model.Provider, model, overrides, cancellationToken);
    }

    private async Task<AuthResult?> GetAuthCoreAsync(
        string providerId,
        Model? model,
        AuthResolutionOverrides? overrides,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        using var signalSource = Link(overrides?.Signal ?? default, cancellationToken);
        var signal = signalSource.Token;
        signal.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId);
        if (provider is null)
        {
            return null;
        }

        var effectiveOverrides = (overrides ?? new AuthResolutionOverrides()) with { Signal = signal };
        var result = await ProviderAuthResolver.ResolveAsync(
                new AuthProviderDescriptor { Id = provider.Id, Auth = provider.Auth },
                _credentials,
                _authContext,
                effectiveOverrides,
                signal)
            .ConfigureAwait(false);
        if (result is null || model?.Headers is null)
        {
            return result;
        }

        return result with
        {
            Auth = result.Auth with { Headers = MergeHeaders(result.Auth.Headers, ToModelHeaders(model.Headers)) },
        };
    }

    /// <inheritdoc />
    public async Task<Credential> LoginAsync(
        string providerId,
        string type,
        AuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(interaction);
        using var signalSource = Link(interaction.Signal, cancellationToken);
        var signal = signalSource.Token;
        signal.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId) ?? throw new ModelsError(ModelsErrorCodes.Provider, $"Unknown provider: {providerId}");
        Credential credential;
        var adapted = new ProviderInteractionAdapter(interaction, signal);
        if (string.Equals(type, AuthTypes.OAuth, StringComparison.Ordinal))
        {
            if (provider.Auth.OAuth?.Login is not { } login)
            {
                throw new ModelsError(ModelsErrorCodes.Auth, $"{provider.Name} does not support {type} login");
            }

            credential = await login(adapted, signal).ConfigureAwait(false);
        }
        else
        {
            if (provider.Auth.ApiKey?.Login is not { } login)
            {
                throw new ModelsError(ModelsErrorCodes.Auth, $"{provider.Name} does not support {type} login");
            }

            credential = await login(adapted, signal).ConfigureAwait(false);
        }

        try
        {
            await _credentials
                .ModifyAsync(providerId, _ => Task.FromResult<Credential?>(credential), signal)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(
                ModelsErrorCodes.Auth,
                $"Credential store modify failed for {providerId}",
                error);
        }

        return credential;
    }

    /// <inheritdoc />
    public async Task LogoutAsync(
        string providerId,
        AuthOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        using var signalSource = Link(options?.Signal ?? default, cancellationToken);
        var signal = signalSource.Token;
        signal.ThrowIfCancellationRequested();
        try
        {
            await _credentials.DeleteAsync(providerId, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(
                ModelsErrorCodes.Auth,
                $"Credential store delete failed for {providerId}",
                error);
        }
    }

    /// <inheritdoc />
    public AssistantMessageEventStream Stream(Model model, Context context, ModelsApiStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        return LazyStreams.Create(model, () => StreamSetupAsync(model, context, options));
    }

    private async Task<AssistantMessageEventStream> StreamSetupAsync(
        Model model,
        Context context,
        ModelsApiStreamOptions? options)
    {
        var provider = RequireProvider(model);
        var (requestModel, requestOptions) = await ApplyAuthAsync(model, options, options?.TransformHeaders).ConfigureAwait(false);
        return provider.Stream(requestModel, context, (StreamOptions)requestOptions);
    }

    /// <inheritdoc />
    public async Task<AssistantMessage> CompleteAsync(
        Model model,
        Context context,
        ModelsApiStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var stream = Stream(model, context, options);
        return await stream.Result.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public AssistantMessageEventStream StreamSimple(
        Model model,
        Context context,
        ModelsSimpleStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        return LazyStreams.Create(model, () => StreamSimpleSetupAsync(model, context, options));
    }

    private async Task<AssistantMessageEventStream> StreamSimpleSetupAsync(
        Model model,
        Context context,
        ModelsSimpleStreamOptions? options)
    {
        var provider = RequireProvider(model);
        var effectiveOptions = options ?? new ModelsSimpleStreamOptions();
        var (requestModel, requestOptions) = await ApplyAuthAsync(
                model,
                effectiveOptions,
                effectiveOptions.TransformHeaders)
            .ConfigureAwait(false);
        return provider.StreamSimple(requestModel, context, (SimpleStreamOptions)requestOptions);
    }

    /// <inheritdoc />
    public async Task<AssistantMessage> CompleteSimpleAsync(
        Model model,
        Context context,
        ModelsSimpleStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var stream = StreamSimple(model, context, options);
        return await stream.Result.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        ModelsDeferredFetchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        var stream = LazyStreams.Create(model, () => FetchDeferredSetupAsync(model, handle, options));
        return await stream.Result.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssistantMessageEventStream> FetchDeferredSetupAsync(
        Model model,
        DeferredHandle handle,
        ModelsDeferredFetchOptions? options)
    {
        var provider = RequireProvider(model);
        if (!provider.SupportsDeferredFetch)
        {
            throw new ModelsError(
                ModelsErrorCodes.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }

        var effectiveOptions = options ?? new ModelsDeferredFetchOptions();
        var (requestModel, requestOptions) = await ApplyAuthAsync(
                model,
                effectiveOptions,
                effectiveOptions.TransformHeaders)
            .ConfigureAwait(false);
        return provider.FetchDeferred(requestModel, handle, (DeferredFetchOptions)requestOptions)
            ?? throw new ModelsError(
                ModelsErrorCodes.Provider,
                $"Provider {model.Provider} does not support deferred responses");
    }

    /// <inheritdoc />
    public async Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        ModelsDeferredCancelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        var provider = RequireProvider(model);
        if (!provider.SupportsDeferredCancel)
        {
            throw new ModelsError(
                ModelsErrorCodes.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }

        var effectiveOptions = options ?? new ModelsDeferredCancelOptions();
        var (requestModel, requestOptions) = await ApplyAuthAsync(
                model,
                effectiveOptions,
                effectiveOptions.TransformHeaders,
                cancellationToken)
            .ConfigureAwait(false);
        await provider.CancelDeferredAsync(requestModel, handle, (DeferredCancelOptions)requestOptions).ConfigureAwait(false);
    }

    private async Task<(Model RequestModel, ProviderRequestOptions RequestOptions)> ApplyAuthAsync(
        Model model,
        ProviderRequestOptions? options,
        Func<ProviderHeaders, Task<ProviderHeaders>>? transformHeaders,
        CancellationToken cancellationToken = default)
    {
        RequireProvider(model);
        var environment = ToProviderEnvironment(options?.Environment);
        var resolution = await GetAuthAsync(
                model,
                new AuthResolutionOverrides
                {
                    ApiKey = options?.ApiKey,
                    Environment = environment,
                    Signal = options?.Signal ?? default,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution is null)
        {
            throw new ModelsError(ModelsErrorCodes.Auth, $"Provider is not configured: {model.Provider}");
        }

        var headers = MergeHeaders(resolution.Auth.Headers, ToProviderHeaders(options?.Headers));
        if (transformHeaders is not null)
        {
            headers = await transformHeaders(headers ?? new ProviderHeaders()).ConfigureAwait(false);
        }

        var mergedEnvironment = MergeEnvironment(resolution.Env, environment);
        var requestModel = resolution.Auth.BaseUrl is null ? model : model with { BaseUrl = resolution.Auth.BaseUrl };
        var requestOptions = CopyOptions(options, headers, mergedEnvironment, resolution.Auth.ApiKey);
        return (requestModel, requestOptions);
    }

    private static ProviderRequestOptions CopyOptions(
        ProviderRequestOptions? options,
        ProviderHeaders? headers,
        ProviderEnvironment? environment,
        string? resolvedApiKey)
    {
        var common = new ProviderRequestOptions
        {
            Signal = options?.Signal ?? default,
            TelemetryContext = options?.TelemetryContext,
            ApiKey = options?.ApiKey ?? resolvedApiKey,
            Fetch = options?.Fetch,
            Environment = environment,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            Headers = headers,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
        };

        if (options is ModelsDeferredFetchOptions deferredFetch)
        {
            return new DeferredFetchOptions
            {
                Signal = common.Signal,
                TelemetryContext = common.TelemetryContext,
                ApiKey = common.ApiKey,
                Fetch = common.Fetch,
                Environment = common.Environment,
                OnPayload = common.OnPayload,
                OnResponse = common.OnResponse,
                Headers = common.Headers,
                TimeoutMs = common.TimeoutMs,
                MaxRetries = common.MaxRetries,
                MaxRetryDelayMs = common.MaxRetryDelayMs,
                Wait = deferredFetch.Wait,
            };
        }

        if (options is ModelsDeferredCancelOptions)
        {
            return new DeferredCancelOptions
            {
                Signal = common.Signal,
                TelemetryContext = common.TelemetryContext,
                ApiKey = common.ApiKey,
                Fetch = common.Fetch,
                Environment = common.Environment,
                OnPayload = common.OnPayload,
                OnResponse = common.OnResponse,
                Headers = common.Headers,
                TimeoutMs = common.TimeoutMs,
                MaxRetries = common.MaxRetries,
                MaxRetryDelayMs = common.MaxRetryDelayMs,
            };
        }

        var source = options as StreamOptions;
        var stream = new StreamOptions
        {
            Signal = common.Signal,
            TelemetryContext = common.TelemetryContext,
            ApiKey = common.ApiKey,
            Fetch = common.Fetch,
            Environment = common.Environment,
            OnPayload = common.OnPayload,
            OnResponse = common.OnResponse,
            Headers = common.Headers,
            TimeoutMs = common.TimeoutMs,
            MaxRetries = common.MaxRetries,
            MaxRetryDelayMs = common.MaxRetryDelayMs,
            Temperature = source?.Temperature,
            SamplingParameters = source?.SamplingParameters,
            MaxTokens = source?.MaxTokens,
            Transport = source?.Transport,
            CacheRetention = source?.CacheRetention,
            SessionId = source?.SessionId,
            WebSocketConnectTimeoutMs = source?.WebSocketConnectTimeoutMs,
            Metadata = source?.Metadata,
        };

        if (options is ModelsSimpleStreamOptions simple)
        {
            return new SimpleStreamOptions
            {
                Signal = stream.Signal,
                TelemetryContext = stream.TelemetryContext,
                ApiKey = stream.ApiKey,
                Fetch = stream.Fetch,
                Environment = stream.Environment,
                OnPayload = stream.OnPayload,
                OnResponse = stream.OnResponse,
                Headers = stream.Headers,
                TimeoutMs = stream.TimeoutMs,
                MaxRetries = stream.MaxRetries,
                MaxRetryDelayMs = stream.MaxRetryDelayMs,
                Temperature = stream.Temperature,
                SamplingParameters = stream.SamplingParameters,
                MaxTokens = stream.MaxTokens,
                Transport = stream.Transport,
                CacheRetention = stream.CacheRetention,
                SessionId = stream.SessionId,
                WebSocketConnectTimeoutMs = stream.WebSocketConnectTimeoutMs,
                Metadata = stream.Metadata,
                ToolChoice = simple.ToolChoice,
                Reasoning = simple.Reasoning,
                Deferred = simple.Deferred,
                DeferredWindow = simple.DeferredWindow,
                ThinkingBudgets = simple.ThinkingBudgets,
            };
        }

        return stream;
    }

    private Provider RequireProvider(Model model)
    {
        var provider = GetProvider(model.Provider);
        return provider ?? throw new ModelsError(ModelsErrorCodes.Provider, $"Unknown provider: {model.Provider}");
    }

    private static ProviderHeaders? MergeHeaders(ProviderHeaders? baseHeaders, ProviderHeaders? overrideHeaders)
    {
        if (baseHeaders is null && overrideHeaders is null)
        {
            return null;
        }

        var merged = new ProviderHeaders();
        if (baseHeaders is not null)
        {
            foreach (var pair in baseHeaders)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (overrideHeaders is not null)
        {
            foreach (var pair in overrideHeaders)
            {
                var existing = merged.Keys.FirstOrDefault(key =>
                    string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    merged.Remove(existing);
                }

                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    private static ProviderHeaders? ToProviderHeaders(IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var result = new ProviderHeaders();
        foreach (var pair in headers)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static ProviderHeaders? ToModelHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var result = new ProviderHeaders();
        foreach (var pair in headers)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static ProviderEnvironment? ToProviderEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return null;
        }

        var result = new ProviderEnvironment();
        foreach (var pair in environment)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static ProviderEnvironment? MergeEnvironment(
        ProviderEnvironment? resolved,
        ProviderEnvironment? explicitEnvironment)
    {
        if (resolved is null && explicitEnvironment is null)
        {
            return null;
        }

        var result = new ProviderEnvironment();
        if (resolved is not null)
        {
            foreach (var pair in resolved)
            {
                result[pair.Key] = pair.Value;
            }
        }

        if (explicitEnvironment is not null)
        {
            foreach (var pair in explicitEnvironment)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static CancellationTokenSource Link(CancellationToken first, CancellationToken second)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(first, second);
    }

    private sealed class ProviderInteractionAdapter(AuthInteraction inner, CancellationToken signal) : ProviderAuthInteraction
    {
        public CancellationToken Signal { get; } = signal;

        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        {
            var actualPrompt = prompt with { Signal = Signal };
            var effective = Link(Signal, cancellationToken);
            return PromptAndDisposeAsync(inner, actualPrompt, effective);
        }

        public void Notify(AuthEvent @event) => inner.Notify(@event);

        private static async Task<string> PromptAndDisposeAsync(
            AuthInteraction interaction,
            AuthPrompt prompt,
            CancellationTokenSource signalSource)
        {
            using (signalSource)
            {
                return await interaction.PromptAsync(prompt, signalSource.Token).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Creates the default mutable Models provider collection.</summary>
public static class ModelsFactory
{
    /// <summary>Creates a provider registry and auth/stream facade.</summary>
    public static MutableModels CreateModels(CreateModelsOptions? options = null) => new ModelsRuntime(options);
}
