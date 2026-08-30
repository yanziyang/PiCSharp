using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;
using Pi.AgentCore.Harness.Session.Testing;

namespace Pi.AgentCore.Tests.Harness.Session;

internal sealed class InMemorySessionBackendFixture : ISessionBackendFixture<SessionMetadata>
{
    public ISessionBackend<SessionMetadata> Repository { get; } = new InMemorySessionBackend();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemorySessionBackend : ISessionBackend<SessionMetadata>
{
    private readonly InMemorySessionRepo _repository = new();

    public Task<Session<SessionMetadata>> CreateAsync(string id, CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(new SessionCreateOptions { Id = id }, cancellationToken);

    public Task<Session<SessionMetadata>> OpenAsync(SessionMetadata metadata, CancellationToken cancellationToken = default) =>
        _repository.OpenAsync(metadata, cancellationToken);

    public Task<IReadOnlyList<SessionMetadata>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken);

    public Task DeleteAsync(SessionMetadata metadata, CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(metadata, cancellationToken);

    public Task<Session<SessionMetadata>> ForkAsync(
        SessionMetadata source,
        ForkOptions options,
        string id,
        CancellationToken cancellationToken = default) =>
        _repository.ForkAsync(source, options, new SessionCreateOptions { Id = id }, cancellationToken);
}

internal sealed class JsonlSessionBackendFixture : ISessionBackendFixture<JsonlSessionMetadata>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-h1-jsonl-" + Guid.NewGuid().ToString("N"));

    public JsonlSessionBackendFixture()
    {
        Directory.CreateDirectory(_root);
        Repository = new JsonlSessionBackend(new JsonlSessionRepo(_root), _root);
    }

    public ISessionBackend<JsonlSessionMetadata> Repository { get; }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class JsonlSessionBackend : ISessionBackend<JsonlSessionMetadata>
{
    private readonly JsonlSessionRepo _repository;
    private readonly string _cwd;

    public JsonlSessionBackend(JsonlSessionRepo repository, string cwd)
    {
        _repository = repository;
        _cwd = cwd;
    }

    public Task<Session<JsonlSessionMetadata>> CreateAsync(string id, CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(new JsonlSessionCreateOptions { Id = id, Cwd = _cwd }, cancellationToken);

    public Task<Session<JsonlSessionMetadata>> OpenAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default) =>
        _repository.OpenAsync(metadata, cancellationToken);

    public Task<IReadOnlyList<JsonlSessionMetadata>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(new JsonlSessionListOptions { Cwd = _cwd }, cancellationToken);

    public Task DeleteAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(metadata, cancellationToken);

    public Task<Session<JsonlSessionMetadata>> ForkAsync(
        JsonlSessionMetadata source,
        ForkOptions options,
        string id,
        CancellationToken cancellationToken = default) =>
        _repository.ForkAsync(
            source,
            options,
            new JsonlSessionCreateOptions { Id = id, Cwd = _cwd },
            cancellationToken);
}
