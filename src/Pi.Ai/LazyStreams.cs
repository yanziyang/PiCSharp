namespace Pi.Ai;

/// <summary>Lazy setup helpers for provider streams and dynamically loaded API implementations.</summary>
public static class LazyStreams
{
    /// <summary>
    /// Returns a stream immediately while running authentication/provider setup behind it. Setup
    /// failures are represented by the same terminal assistant error message as Pi's lazy stream.
    /// </summary>
    public static AssistantMessageEventStream Create(
        Model model,
        Func<Task<AssistantMessageEventStream>> setup)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(setup);
        var outer = new AssistantMessageEventStream();
        _ = CompleteAsync(outer, model, setup);
        return outer;
    }

    /// <summary>Creates a lazy wrapper around a provider API loader.</summary>
    public static ProviderStreams CreateApi(Func<Task<ProviderStreams>> load, LazyApiCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        var api = new LazyProviderStreams(load);
        if (capabilities?.FetchDeferred == true)
        {
            api.EnableDeferredFetch();
        }

        if (capabilities?.CancelDeferred == true)
        {
            api.EnableDeferredCancel();
        }

        return api;
    }

    private static async Task CompleteAsync(
        AssistantMessageEventStream outer,
        Model model,
        Func<Task<AssistantMessageEventStream>> setup)
    {
        try
        {
            var inner = await setup().ConfigureAwait(false);
            await foreach (var @event in inner.ConfigureAwait(false))
            {
                outer.Push(@event);
            }

            outer.End(await inner.Result.ConfigureAwait(false));
        }
        catch (Exception error)
        {
            var message = CreateSetupErrorMessage(model, error);
            outer.Push(new StreamErrorEvent(StopReasons.Error, message));
            outer.End(message);
        }
    }

    private static AssistantMessage CreateSetupErrorMessage(Model model, Exception error) => new()
    {
        Content = [],
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Usage = new Usage(),
        StopReason = StopReasons.Error,
        ErrorMessage = error.Message,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private sealed class LazyProviderStreams(Func<Task<ProviderStreams>> load) : ProviderStreams
    {
        private readonly Func<Task<ProviderStreams>> _load = load;
        private bool _fetchDeferred;
        private bool _cancelDeferred;

        public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
            Create(model, async () => (await _load().ConfigureAwait(false)).Stream(model, context, options));

        public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
            Create(model, async () => (await _load().ConfigureAwait(false)).StreamSimple(model, context, options));

        public AssistantMessageEventStream? FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions? options = null)
        {
            if (!_fetchDeferred)
            {
                return null;
            }

            return Create(
                model,
                async () =>
                {
                    var implementation = await _load().ConfigureAwait(false);
                    return implementation.FetchDeferred(model, handle, options)
                        ?? throw new InvalidOperationException("API does not support deferred responses");
                });
        }

        public Task CancelDeferredAsync(
            Model model,
            DeferredHandle handle,
            DeferredCancelOptions? options = null)
        {
            if (!_cancelDeferred)
            {
                return Task.FromException(new InvalidOperationException("API cannot cancel deferred responses"));
            }

            return CancelDeferredCoreAsync(model, handle, options);
        }

        public void EnableDeferredFetch() => _fetchDeferred = true;

        public void EnableDeferredCancel() => _cancelDeferred = true;

        private async Task CancelDeferredCoreAsync(Model model, DeferredHandle handle, DeferredCancelOptions? options)
        {
            var implementation = await _load().ConfigureAwait(false);
            if (implementation is null)
            {
                throw new InvalidOperationException("API cannot cancel deferred responses");
            }

            await implementation.CancelDeferredAsync(model, handle, options).ConfigureAwait(false);
        }
    }
}

/// <summary>Optional capabilities for lazy deferred API wrappers.</summary>
public sealed record LazyApiCapabilities
{
    /// <summary>Whether deferred response fetching is exposed.</summary>
    public bool FetchDeferred { get; init; }

    /// <summary>Whether deferred response cancellation is exposed.</summary>
    public bool CancelDeferred { get; init; }
}
