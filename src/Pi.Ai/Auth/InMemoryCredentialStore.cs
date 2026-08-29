namespace Pi.Ai;

/// <summary>
/// Default in-memory credential store. Writes are serialized per provider through a promise-like
/// task chain, matching Pi's read-modify-write semantics.
/// </summary>
public sealed class InMemoryCredentialStore : CredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Credential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _chains = new(StringComparer.Ordinal);

    /// <summary>Reads a stored credential, if present.</summary>
    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _credentials.TryGetValue(providerId, out var credential);
            return Task.FromResult<Credential?>(credential);
        }
    }

    /// <summary>Lists provider/type metadata without exposing credential values.</summary>
    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CredentialInfo> result = _credentials
                .Select(static pair => new CredentialInfo(pair.Key, pair.Value.Type))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <summary>Runs a serialized read-modify-write for one provider.</summary>
    public Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> updater,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentNullException.ThrowIfNull(updater);

        return EnqueueAsync(
            providerId,
            async () =>
            {
                Credential? current;
                lock (_gate)
                {
                    _credentials.TryGetValue(providerId, out current);
                }

                var next = await updater(current).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (next is not null)
                {
                    lock (_gate)
                    {
                        _credentials[providerId] = next;
                    }
                }

                return next ?? current;
            },
            cancellationToken);
    }

    /// <summary>Removes a provider credential, serialized against modifications.</summary>
    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        await EnqueueAsync<object?>(
                providerId,
                () =>
                {
                    lock (_gate)
                    {
                        _credentials.Remove(providerId);
                    }

                    return Task.FromResult<object?>(null);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<T> EnqueueAsync<T>(
        string providerId,
        Func<Task<T>> task,
        CancellationToken cancellationToken)
    {
        Task previous;
        Task<T> queued;
        lock (_gate)
        {
            previous = _chains.TryGetValue(providerId, out var existing) ? existing : Task.CompletedTask;
            queued = ExecuteAfterAsync(previous, task, cancellationToken);
            Task? chainTail = null;
            chainTail = queued.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        if (_chains.TryGetValue(providerId, out var current) && ReferenceEquals(current, chainTail))
                        {
                            _chains.Remove(providerId);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            _chains[providerId] = chainTail;
        }

        // The queued operation itself remains in the chain if the caller stops waiting. This is
        // important for the upstream invariant that a refresh cannot be interleaved by a later
        // modify merely because the first caller's cancellation raced its completion.
        return queued.WaitAsync(cancellationToken);
    }

    private static async Task<T> ExecuteAfterAsync<T>(
        Task previous,
        Func<Task<T>> task,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed predecessor must not poison the provider's serialized queue.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await task().ConfigureAwait(false);
    }
}
