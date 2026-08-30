namespace Pi.AgentCore.Harness.Session;

/// <summary>In-memory session storage used by tests and ephemeral hosts.</summary>
public sealed class InMemorySessionStorage<TMetadata> : ISessionStorage<TMetadata>, IAsyncDisposable
    where TMetadata : SessionMetadata
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TMetadata _metadata;
    private readonly SessionState _state = new();

    /// <summary>Creates an empty in-memory session.</summary>
    public InMemorySessionStorage(TMetadata metadata)
    {
        _metadata = SessionJson.CloneMetadata(metadata ?? throw new ArgumentNullException(nameof(metadata)));
    }

    internal static InMemorySessionStorage<TMetadata> Fork(TMetadata metadata, IReadOnlyList<SessionMutation> mutations)
    {
        var fork = new InMemorySessionStorage<TMetadata>(metadata);
        foreach (var mutation in mutations)
        {
            fork._state.ApplyMutation(mutation);
        }

        return fork;
    }

    internal IReadOnlyList<SessionMutation> CreateForkMutations(ForkOptions options) => _state.CreateForkMutations(options);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return SessionJson.CloneMetadata(_metadata);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.GetLanes();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task CreateLaneAsync(string lane, string? at, CancellationToken cancellationToken = default) =>
        WriteAsync(
            () =>
            {
                _state.ValidateNewLane(lane);
                _state.ValidateTarget(at);
                var mutation = new LaneMutation { Seq = _state.NextSequence, Lane = lane, LeafId = at };
                _state.ApplyMutation(mutation);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default) =>
        WriteAsync(
            () =>
            {
                _state.RequireLane(lane);
                _state.ValidateTarget(to);
                var mutation = new LaneMutation { Seq = _state.NextSequence, Lane = lane, LeafId = to };
                _state.ApplyMutation(mutation);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Entry> AppendEntryAsync(
        Entry entry,
        string lane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var leaf = _state.RequireLane(lane);
            _state.ValidateUnusedId(entry.Id);
            var committed = entry with
            {
                ParentId = leaf,
                Seq = _state.NextSequence,
                Timestamp = UnixMilliseconds(),
            };
            _state.ApplyMutation(new EntryMutation { Seq = committed.Seq, Lane = lane, Entry = committed });
            return _state.GetEntry(committed.Id)!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<LaneRecord> AppendRecordAsync(
        LaneRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state.RequireLane(record.Lane);
            _state.ValidateUnusedId(record.Id);
            if (record is OperationStartedRecord && _state.FindOpenOperations(record.Lane, null).Count > 0)
            {
                throw new SessionError(
                    SessionErrorCode.Storage,
                    $"Lane {record.Lane} already has an open operation {record.Id}");
            }

            var committed = record with { Seq = _state.NextSequence, Timestamp = UnixMilliseconds() };
            _state.ApplyMutation(new RecordMutation { Seq = committed.Seq, Record = committed });
            return Jsonl.JsonlCodec.DecodeRecordObject(SessionJson.RecordToJson(committed));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Entry?> GetEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.GetEntry(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entry>> FindEntriesAsync(EntryQuery query, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.FindEntries(query);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entry>> FindEntriesOnBranchAsync(
        EntryQuery query,
        string start,
        BranchBounds bounds,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.FindEntriesOnBranch(query, start, bounds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(RecordQuery query, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.FindRecords(query);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.FindOpenOperations(lane, limit);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogItem>> GetLogAsync(LogOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.GetLog(options);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.GetName();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task SetNameAsync(string? name, CancellationToken cancellationToken = default) =>
        WriteAsync(
            () => _state.ApplyMutation(new FactMutation { Seq = _state.NextSequence, Fact = "name", Name = name }),
            cancellationToken);

    /// <inheritdoc />
    public async Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.GetLabel(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default) =>
        WriteAsync(
            () =>
            {
                _state.ValidateTarget(id);
                _state.ApplyMutation(new FactMutation
                {
                    Seq = _state.NextSequence,
                    Fact = "label",
                    TargetId = id,
                    Label = label,
                });
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.GetStats();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync(Action action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static long UnixMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

}

/// <summary>In-memory repository.</summary>
public sealed class InMemorySessionRepo : ISessionRepository<SessionMetadata, SessionCreateOptions>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, InMemorySessionStorage<SessionMetadata>> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Session<SessionMetadata>> CreateAsync(
        SessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = options.Id ?? Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            if (_sessions.ContainsKey(id))
            {
                throw new SessionError(SessionErrorCode.AlreadyExists, $"Session already exists: {id}");
            }

            var metadata = new SessionMetadata
            {
                Id = id,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ParentSessionId = options.ParentSessionId,
            };
            var storage = new InMemorySessionStorage<SessionMetadata>(metadata);
            _sessions[id] = storage;
            return Task.FromResult(new Session<SessionMetadata>(storage));
        }
    }

    /// <summary>Creates a session using default options.</summary>
    public Task<Session<SessionMetadata>> CreateAsync(CancellationToken cancellationToken = default) =>
        CreateAsync(new SessionCreateOptions(), cancellationToken);

    /// <inheritdoc />
    public Task<Session<SessionMetadata>> OpenAsync(
        SessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_sessions.TryGetValue(metadata.Id, out var storage))
            {
                throw new SessionError(SessionErrorCode.NotFound, $"Session not found: {metadata.Id}");
            }

            return Task.FromResult(new Session<SessionMetadata>(storage));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var sessions = _sessions.Values.Select(storage => storage.GetMetadataAsync(cancellationToken).GetAwaiter().GetResult()).ToArray();
            return Task.FromResult<IReadOnlyList<SessionMetadata>>(sessions);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(SessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _sessions.Remove(metadata.Id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Session<SessionMetadata>> ForkAsync(
        SessionMetadata source,
        ForkOptions options,
        SessionCreateOptions createOptions,
        CancellationToken cancellationToken = default)
    {
        var sourceStorage = await GetStorageAsync(source.Id, cancellationToken).ConfigureAwait(false);
        var id = createOptions.Id ?? Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            if (_sessions.ContainsKey(id))
            {
                throw new SessionError(SessionErrorCode.AlreadyExists, $"Session already exists: {id}");
            }
        }

        var metadata = new SessionMetadata
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ParentSessionId = createOptions.ParentSessionId ?? source.Id,
        };
        var forkStorage = InMemorySessionStorage<SessionMetadata>.Fork(metadata, sourceStorage.CreateForkMutations(options));
        var session = new Session<SessionMetadata>(forkStorage);
        lock (_sync)
        {
            if (_sessions.ContainsKey(id))
            {
                throw new SessionError(SessionErrorCode.AlreadyExists, $"Session already exists: {id}");
            }

            _sessions[id] = forkStorage;
        }

        return session;
    }

    /// <summary>Forks with a generated destination identifier.</summary>
    public Task<Session<SessionMetadata>> ForkAsync(
        SessionMetadata source,
        ForkOptions options,
        CancellationToken cancellationToken = default) =>
        ForkAsync(source, options, new SessionCreateOptions(), cancellationToken);

    private Task<InMemorySessionStorage<SessionMetadata>> GetStorageAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return _sessions.TryGetValue(id, out var storage)
                ? Task.FromResult(storage)
                : throw new SessionError(SessionErrorCode.NotFound, $"Session not found: {id}");
        }
    }

}
