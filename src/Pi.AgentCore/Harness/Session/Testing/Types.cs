using Pi.AgentCore.Harness.Session;

namespace Pi.AgentCore.Harness.Session.Testing;

/// <summary>Backend abstraction consumed by the reusable session conformance suite.</summary>
public interface ISessionBackend<TMetadata>
    where TMetadata : SessionMetadata
{
    /// <summary>Creates an isolated session.</summary>
    Task<Session<TMetadata>> CreateAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Opens a session.</summary>
    Task<Session<TMetadata>> OpenAsync(TMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Lists sessions.</summary>
    Task<IReadOnlyList<TMetadata>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a session.</summary>
    Task DeleteAsync(TMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Forks a session with an explicit destination identifier.</summary>
    Task<Session<TMetadata>> ForkAsync(
        TMetadata source,
        ForkOptions options,
        string id,
        CancellationToken cancellationToken = default);
}

/// <summary>Isolated fixture supplied to one conformance case.</summary>
public interface ISessionBackendFixture<TMetadata> : IAsyncDisposable
    where TMetadata : SessionMetadata
{
    /// <summary>Repository under test.</summary>
    ISessionBackend<TMetadata> Repository { get; }
}

/// <summary>One runner-independent conformance case.</summary>
public sealed record SessionBackendConformanceCase<TMetadata>
    where TMetadata : SessionMetadata
{
    /// <summary>Human-readable conformance group.</summary>
    public required string Group { get; init; }

    /// <summary>Exact upstream test name.</summary>
    public required string Name { get; init; }

    /// <summary>Runs the case against a fresh fixture.</summary>
    public required Func<Task> RunAsync { get; init; }
}
