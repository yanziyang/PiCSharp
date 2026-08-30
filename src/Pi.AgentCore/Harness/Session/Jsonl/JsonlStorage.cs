namespace Pi.AgentCore.Harness.Session.Jsonl;

/// <summary>Append-only JSONL v4 session storage.</summary>
public sealed class JsonlSessionStorage : ISessionStorage<JsonlSessionMetadata>, IAsyncDisposable
{
    private readonly IJsonlFileSystem _fileSystem;
    private readonly JsonlSessionMetadata _metadata;
    private readonly SessionState _state;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private JsonlSessionStorage(
        IJsonlFileSystem fileSystem,
        JsonlSessionMetadata metadata,
        SessionState state)
    {
        _fileSystem = fileSystem;
        _metadata = metadata;
        _state = state;
    }

    internal IReadOnlyList<SessionMutation> CreateForkMutations(ForkOptions options) => _state.CreateForkMutations(options);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates and persists a new JSONL v4 session.</summary>
    public static async Task<JsonlSessionStorage> CreateAsync(
        IJsonlFileSystem fileSystem,
        string path,
        JsonlV4Header header,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(header);
        try
        {
            await fileSystem.CreateDirectoryAsync(Path.GetDirectoryName(path) ?? ".", cancellationToken).ConfigureAwait(false);
            await fileSystem.WriteFileAsync(path, JsonlCodec.EncodeHeader(header), cancellationToken).ConfigureAwait(false);
            var fileInfo = await fileSystem.FileInfoAsync(path, cancellationToken).ConfigureAwait(false);
            return new JsonlSessionStorage(
                fileSystem,
                JsonlCodec.MetadataFromHeader(header, path, fileInfo.ModifiedAt),
                new SessionState());
        }
        catch (SessionError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw StorageError($"Could not create session {path}", error);
        }
    }

    /// <summary>Loads and replays a JSONL v4 session.</summary>
    public static async Task<JsonlSessionStorage> LoadAsync(
        IJsonlFileSystem fileSystem,
        JsonlSessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(metadata);
        string content;
        try
        {
            content = await fileSystem.ReadTextFileAsync(metadata.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw StorageError($"Could not read session {metadata.Path}", error);
        }

        var lines = content.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            throw InvalidFile(metadata.Path, 1, new JsonlDecodeError(JsonlDecodeErrorKind.Schema, "is missing a header"));
        }

        var headerResult = JsonlCodec.ParseHeader(lines[0]);
        if (!headerResult.IsSuccess)
        {
            throw InvalidFile(metadata.Path, 1, headerResult.Error!);
        }

        JsonlFileInfo fileInfo;
        try
        {
            fileInfo = await fileSystem.FileInfoAsync(metadata.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw StorageError($"Could not inspect session {metadata.Path}", error);
        }

        var header = headerResult.Value!;
        var effectiveMetadata = JsonlCodec.MetadataFromHeader(header, metadata.Path, fileInfo.ModifiedAt);
        var state = new SessionState();
        for (var index = 1; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutationResult = JsonlCodec.ParseMutation(lines[index]);
            if (!mutationResult.IsSuccess)
            {
                var isFinalLine = index == lines.Count - 1;
                if (isFinalLine && mutationResult.Error!.Kind == JsonlDecodeErrorKind.Syntax)
                {
                    var prefix = string.Join('\n', lines.Take(index)) + "\n";
                    await PublishFileAtomicallyAsync(
                        fileSystem,
                        metadata.Path,
                        temporary => fileSystem.WriteFileAsync(temporary, prefix, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    return new JsonlSessionStorage(fileSystem, effectiveMetadata, state);
                }

                throw InvalidFile(metadata.Path, index + 1, mutationResult.Error!);
            }

            try
            {
                state.ApplyMutation(mutationResult.Value!);
            }
            catch (SessionError error) when (error.Code == SessionErrorCode.InvalidEntry)
            {
                throw InvalidFile(
                    metadata.Path,
                    index + 1,
                    new JsonlDecodeError(JsonlDecodeErrorKind.Schema, error.Message, error));
            }
        }

        if (!content.EndsWith('\n'))
        {
            try
            {
                await fileSystem.AppendFileAsync(metadata.Path, "\n", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw StorageError($"Could not repair session {metadata.Path}", error);
            }
        }

        return new JsonlSessionStorage(fileSystem, effectiveMetadata, state);
    }

    /// <summary>Stages a fork and publishes it using an atomic rename.</summary>
    public static async Task<JsonlSessionStorage> ForkAsync(
        IJsonlFileSystem fileSystem,
        string path,
        JsonlV4Header header,
        IReadOnlyList<SessionMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(mutations);
        await PublishFileAtomicallyAsync(
            fileSystem,
            path,
            async temporary =>
            {
                await fileSystem.WriteFileAsync(temporary, JsonlCodec.EncodeHeader(header), cancellationToken).ConfigureAwait(false);
                var stagedState = new SessionState();
                foreach (var mutation in mutations)
                {
                    await fileSystem.AppendFileAsync(temporary, JsonlCodec.EncodeMutation(mutation), cancellationToken).ConfigureAwait(false);
                    stagedState.ApplyMutation(mutation);
                }
            },
            cancellationToken).ConfigureAwait(false);

        return await LoadAsync(
            fileSystem,
            JsonlCodec.MetadataFromHeader(header, path, 0),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<JsonlSessionMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_metadata with
        {
            Metadata = _metadata.Metadata is null ? null : SessionJson.CloneObject(_metadata.Metadata),
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetLanes());
    }

    /// <inheritdoc />
    public Task CreateLaneAsync(string lane, string? at, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () =>
            {
                _state.ValidateNewLane(lane);
                _state.ValidateTarget(at);
                var mutation = new LaneMutation { Seq = _state.NextSequence, Lane = lane, LeafId = at };
                return AppendAndApplyAsync(mutation, cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () =>
            {
                _state.RequireLane(lane);
                _state.ValidateTarget(to);
                var mutation = new LaneMutation { Seq = _state.NextSequence, Lane = lane, LeafId = to };
                return AppendAndApplyAsync(mutation, cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Entry> AppendEntryAsync(Entry entry, string lane, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async () =>
            {
                var leaf = _state.RequireLane(lane);
                _state.ValidateUnusedId(entry.Id);
                var committed = entry with
                {
                    ParentId = leaf,
                    Seq = _state.NextSequence,
                    Timestamp = UnixMilliseconds(),
                };
                await AppendAndApplyAsync(
                    new EntryMutation { Seq = committed.Seq, Lane = lane, Entry = committed },
                    cancellationToken).ConfigureAwait(false);
                return _state.GetEntry(committed.Id)!;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<LaneRecord> AppendRecordAsync(LaneRecord record, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async () =>
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
                await AppendAndApplyAsync(new RecordMutation { Seq = committed.Seq, Record = committed }, cancellationToken).ConfigureAwait(false);
                return JsonlCodec.DecodeRecordObject(SessionJson.RecordToJson(committed));
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Entry?> GetEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetEntry(id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Entry>> FindEntriesAsync(EntryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.FindEntries(query));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Entry>> FindEntriesOnBranchAsync(
        EntryQuery query,
        string start,
        BranchBounds bounds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.FindEntriesOnBranch(query, start, bounds));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(RecordQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.FindRecords(query));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.FindOpenOperations(lane, limit));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LogItem>> GetLogAsync(LogOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetLog(options));
    }

    /// <inheritdoc />
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetName());
    }

    /// <inheritdoc />
    public Task SetNameAsync(string? name, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () => AppendAndApplyAsync(
                new FactMutation { Seq = _state.NextSequence, Fact = "name", Name = name },
                cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetLabel(id));
    }

    /// <inheritdoc />
    public Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () =>
            {
                _state.ValidateTarget(id);
                return AppendAndApplyAsync(
                    new FactMutation { Seq = _state.NextSequence, Fact = "label", TargetId = id, Label = label },
                    cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetStats());
    }

    private async Task AppendAndApplyAsync(SessionMutation mutation, CancellationToken cancellationToken)
    {
        try
        {
            await _fileSystem.AppendFileAsync(_metadata.Path, JsonlCodec.EncodeMutation(mutation), cancellationToken).ConfigureAwait(false);
        }
        catch (SessionError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw StorageError($"Could not append to session {_metadata.Path}", error);
        }

        _state.ApplyMutation(mutation);
    }

    private async Task<T> EnqueueAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task<bool> EnqueueAsync(Func<Task> operation, CancellationToken cancellationToken) =>
        EnqueueAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private static async Task PublishFileAtomicallyAsync(
        IJsonlFileSystem fileSystem,
        string destination,
        Func<string, Task> populate,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp";
        try
        {
            await populate(temporary).ConfigureAwait(false);
            await fileSystem.RenameFileAsync(temporary, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionError)
        {
            await TryRemoveAsync(fileSystem, temporary, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            await TryRemoveAsync(fileSystem, temporary, cancellationToken).ConfigureAwait(false);
            throw StorageError($"Could not publish session {destination}", error);
        }
    }

    private static async Task TryRemoveAsync(IJsonlFileSystem fileSystem, string path, CancellationToken cancellationToken)
    {
        try
        {
            await fileSystem.RemoveAsync(path, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort; preserve the original failure.
        }
    }

    private static SessionError InvalidFile(string path, int line, JsonlDecodeError error) =>
        new(
            SessionErrorCode.InvalidEntry,
            $"Invalid JSONL v4 session {path}: line {line} {error.Message}",
            error);

    private static SessionError StorageError(string message, Exception error) =>
        new(SessionErrorCode.Storage, message + ": " + error.Message, error);

    private static long UnixMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
