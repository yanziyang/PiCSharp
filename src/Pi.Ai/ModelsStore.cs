using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Persisted dynamic model catalog for one provider.</summary>
public sealed record ModelsStoreEntry
{
    /// <summary>Models returned by the remote catalog.</summary>
    public required IReadOnlyList<Model> Models { get; init; }

    /// <summary>Unix timestamp from the remote catalog's Last-Modified header.</summary>
    public long? LastModified { get; init; }

    /// <summary>Unix timestamp of the last completed remote check.</summary>
    public long? CheckedAt { get; init; }

    /// <summary>Opaque ETag validator, including quotes when supplied by the server.</summary>
    public string? Etag { get; init; }

    /// <summary>Creates an isolated copy suitable for a store boundary.</summary>
    public ModelsStoreEntry DeepCopy() => this with { Models = Models.Select(CloneModel).ToArray() };

    private static Model CloneModel(Model model) => model with
    {
        ThinkingLevelMap = model.ThinkingLevelMap is null
            ? null
            : new Dictionary<string, string?>(model.ThinkingLevelMap, StringComparer.Ordinal),
        Input = model.Input.ToArray(),
        Cost = model.Cost with
        {
            Tiers = model.Cost.Tiers
                .Select(static tier => new ModelCostTier
                {
                    Input = tier.Input,
                    Output = tier.Output,
                    CacheRead = tier.CacheRead,
                    CacheWrite = tier.CacheWrite,
                    InputTokensAbove = tier.InputTokensAbove,
                })
                .ToArray(),
        },
        SamplingParameters = model.SamplingParameters is null
            ? null
            : model.SamplingParameters.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value?.DeepClone(),
                StringComparer.Ordinal),
        Headers = model.Headers is null
            ? null
            : new Dictionary<string, string>(model.Headers, StringComparer.OrdinalIgnoreCase),
        Compatibility = model.Compatibility?.DeepClone()?.AsObject(),
    };
}

/// <summary>Persistent model catalogs keyed by provider identifier.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi models-store contract name.")]
public interface ModelsStore
{
    /// <summary>Reads the last persisted catalog for a provider.</summary>
    Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Writes a provider catalog.</summary>
    Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Deletes a provider catalog.</summary>
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>In-memory model catalog store used by tests and host applications without persistence.</summary>
public sealed class InMemoryModelsStore : ModelsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelsStoreEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(providerId, out var entry) ? entry.DeepCopy() : null);
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries[providerId] = entry.DeepCopy();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Remove(providerId);
        }

        return Task.CompletedTask;
    }
}
