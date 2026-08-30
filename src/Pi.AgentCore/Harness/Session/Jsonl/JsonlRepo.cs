using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.AgentCore.Harness.Session.Jsonl;

/// <summary>Repository for Pi harness JSONL v4 sessions.</summary>
public sealed class JsonlSessionRepo : ISessionRepository<JsonlSessionMetadata, JsonlSessionCreateOptions>
{
    private static readonly Regex _validSessionId = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IJsonlFileSystem _fileSystem;
    private readonly string _sessionsRoot;
    private readonly object _reservationLock = new();
    private readonly HashSet<string> _activeCreateDestinations = new(StringComparer.Ordinal);

    /// <summary>Creates a repository over a filesystem and root directory.</summary>
    public JsonlSessionRepo(IJsonlFileSystem fileSystem, string sessionsRoot)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _sessionsRoot = _fileSystem.AbsolutePath(sessionsRoot ?? throw new ArgumentNullException(nameof(sessionsRoot)));
    }

    /// <summary>Creates a repository using the local filesystem.</summary>
    public JsonlSessionRepo(string sessionsRoot)
        : this(new LocalJsonlFileSystem(), sessionsRoot)
    {
    }

    /// <summary>Creates a repository rooted in a temporary local directory.</summary>
    public JsonlSessionRepo()
        : this(new LocalJsonlFileSystem(), Path.Combine(Path.GetTempPath(), "pi-agent-sessions"))
    {
    }

    /// <inheritdoc />
    public async Task<Session<JsonlSessionMetadata>> CreateAsync(
        JsonlSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var id = options.Id ?? Guid.NewGuid().ToString("N");
        ValidateSessionId(id);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var directory = await SessionDirectoryAsync(options.Cwd, cancellationToken).ConfigureAwait(false);
        var path = _fileSystem.JoinPath(directory, FileName(createdAt, id));
        Reserve(path, id);
        try
        {
            await _fileSystem.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            await EnsureDestinationDoesNotExistAsync(directory, id, cancellationToken).ConfigureAwait(false);
            var metadata = options.Metadata is null ? null : SessionJson.CloneObject(options.Metadata);
            if (metadata is not null)
            {
                SessionDurability.Validate(metadata);
            }

            var header = new JsonlV4Header
            {
                Id = id,
                CreatedAt = createdAt,
                Cwd = _fileSystem.AbsolutePath(options.Cwd),
                ParentSessionId = options.ParentSessionId,
                Metadata = metadata,
            };
            var storage = await JsonlSessionStorage.CreateAsync(_fileSystem, path, header, cancellationToken).ConfigureAwait(false);
            return new Session<JsonlSessionMetadata>(storage);
        }
        catch (SessionError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SessionError(SessionErrorCode.Storage, $"Could not create session {id}: {error.Message}", error);
        }
        finally
        {
            Release(path, id);
        }
    }

    /// <inheritdoc />
    public async Task<Session<JsonlSessionMetadata>> OpenAsync(
        JsonlSessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _fileSystem.ExistsAsync(metadata.Path, cancellationToken).ConfigureAwait(false))
        {
            throw new SessionError(SessionErrorCode.NotFound, $"Session not found: {metadata.Id}");
        }

        var storage = await JsonlSessionStorage.LoadAsync(_fileSystem, metadata, cancellationToken).ConfigureAwait(false);
        var loaded = await storage.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(loaded.Id, metadata.Id, StringComparison.Ordinal))
        {
            throw new SessionError(SessionErrorCode.InvalidEntry, $"Session id does not match header: {metadata.Id}");
        }

        return new Session<JsonlSessionMetadata>(storage);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JsonlSessionMetadata>> ListAsync(CancellationToken cancellationToken = default) =>
        ListAsync(options: null, cancellationToken: cancellationToken);

    /// <summary>Lists session metadata, optionally filtered by working directory.</summary>
    public async Task<IReadOnlyList<JsonlSessionMetadata>> ListAsync(
        JsonlSessionListOptions? options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = new List<JsonlDirectoryEntry>();
        if (options?.Cwd is not null)
        {
            var cwdDirectory = await SessionDirectoryAsync(options.Cwd, cancellationToken).ConfigureAwait(false);
            if (await _fileSystem.ExistsAsync(cwdDirectory, cancellationToken).ConfigureAwait(false))
            {
                directories.Add(new JsonlDirectoryEntry { Path = cwdDirectory, IsDirectory = true });
            }
        }
        else
        {
            if (!await _fileSystem.ExistsAsync(_sessionsRoot, cancellationToken).ConfigureAwait(false))
            {
                return [];
            }

            directories.AddRange((await _fileSystem.ListDirectoryAsync(_sessionsRoot, cancellationToken).ConfigureAwait(false))
                .Where(static item => item.IsDirectory || item.IsSymbolicLink));
        }

        var result = new List<JsonlSessionMetadata>();
        foreach (var directory in directories)
        {
            if (!await _fileSystem.ExistsAsync(directory.Path, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            foreach (var item in await _fileSystem.ListDirectoryAsync(directory.Path, cancellationToken).ConfigureAwait(false))
            {
                if (item.IsDirectory || item.IsSymbolicLink || !item.Path.EndsWith(".jsonl", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var lines = await _fileSystem.ReadTextLinesAsync(item.Path, cancellationToken).ConfigureAwait(false);
                    if (lines.Count == 0)
                    {
                        continue;
                    }

                    var header = JsonlCodec.ParseHeader(lines[0]);
                    if (!header.IsSuccess)
                    {
                        continue;
                    }

                    var fileInfo = await _fileSystem.FileInfoAsync(item.Path, cancellationToken).ConfigureAwait(false);
                    var metadata = JsonlCodec.MetadataFromHeader(header.Value!, item.Path, fileInfo.ModifiedAt);
                    if (options?.Cwd is null || string.Equals(metadata.Cwd, _fileSystem.AbsolutePath(options.Cwd), StringComparison.Ordinal))
                    {
                        result.Add(metadata);
                    }
                }
                catch (IOException)
                {
                    // A concurrently removed or unreadable file is not a list result.
                }
                catch (UnauthorizedAccessException)
                {
                    // A concurrently removed or unreadable file is not a list result.
                }
            }
        }

        return result.OrderByDescending(static item => item.ModifiedAt).ToArray();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        if (await _fileSystem.ExistsAsync(metadata.Path, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _fileSystem.RemoveAsync(metadata.Path, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw new SessionError(SessionErrorCode.Storage, $"Could not delete session {metadata.Id}: {error.Message}", error);
            }
        }
    }

    /// <inheritdoc />
    public async Task<Session<JsonlSessionMetadata>> ForkAsync(
        JsonlSessionMetadata source,
        ForkOptions options,
        JsonlSessionCreateOptions createOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createOptions);
        var sourceStorage = await OpenStorageAsync(source, cancellationToken).ConfigureAwait(false);
        var id = createOptions.Id ?? Guid.NewGuid().ToString("N");
        ValidateSessionId(id);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var directory = await SessionDirectoryAsync(createOptions.Cwd, cancellationToken).ConfigureAwait(false);
        var path = _fileSystem.JoinPath(directory, FileName(createdAt, id));
        Reserve(path, id);
        try
        {
            await _fileSystem.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            await EnsureDestinationDoesNotExistAsync(directory, id, cancellationToken).ConfigureAwait(false);
            var metadata = createOptions.Metadata is null ? null : SessionJson.CloneObject(createOptions.Metadata);
            if (metadata is not null)
            {
                SessionDurability.Validate(metadata);
            }

            var header = new JsonlV4Header
            {
                Id = id,
                CreatedAt = createdAt,
                Cwd = _fileSystem.AbsolutePath(createOptions.Cwd),
                ParentSessionId = createOptions.ParentSessionId ?? source.Id,
                Metadata = metadata,
            };
            var storage = await JsonlSessionStorage.ForkAsync(
                _fileSystem,
                path,
                header,
                sourceStorage.CreateForkMutations(options),
                cancellationToken).ConfigureAwait(false);
            return new Session<JsonlSessionMetadata>(storage);
        }
        catch (SessionError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SessionError(SessionErrorCode.Storage, $"Could not fork session {source.Id}: {error.Message}", error);
        }
        finally
        {
            Release(path, id);
        }
    }

    private async Task<JsonlSessionStorage> OpenStorageAsync(
        JsonlSessionMetadata metadata,
        CancellationToken cancellationToken)
    {
        return await JsonlSessionStorage.LoadAsync(_fileSystem, metadata, cancellationToken).ConfigureAwait(false);
    }

    private Task<string> SessionDirectoryAsync(string cwd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absoluteCwd = _fileSystem.AbsolutePath(cwd);
        var normalized = absoluteCwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        return Task.FromResult(_fileSystem.JoinPath(_sessionsRoot, "--" + normalized + "--"));
    }

    private async Task EnsureDestinationDoesNotExistAsync(
        string directory,
        string id,
        CancellationToken cancellationToken)
    {
        var entries = await _fileSystem.ListDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        if (entries.Any(item => !item.IsDirectory && !item.IsSymbolicLink &&
                                Path.GetFileName(item.Path).EndsWith("_" + id + ".jsonl", StringComparison.Ordinal)))
        {
            throw new SessionError(SessionErrorCode.AlreadyExists, $"Session already exists: {id}");
        }
    }

    private void Reserve(string path, string id)
    {
        var reservationKey = (Path.GetDirectoryName(path) ?? path) + "\0" + id;
        lock (_reservationLock)
        {
            if (!_activeCreateDestinations.Add(reservationKey))
            {
                throw new SessionError(SessionErrorCode.AlreadyExists, $"Session already exists: {id}");
            }
        }
    }

    private void Release(string path, string id)
    {
        var reservationKey = (Path.GetDirectoryName(path) ?? path) + "\0" + id;
        lock (_reservationLock)
        {
            _activeCreateDestinations.Remove(reservationKey);
        }
    }

    private static void ValidateSessionId(string id)
    {
        if (id.Length == 0 || !_validSessionId.IsMatch(id))
        {
            throw new SessionError(
                SessionErrorCode.InvalidPayload,
                "Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character");
        }
    }

    private static string FileName(long createdAt, string id)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(createdAt)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture);
        return timestamp + "_" + id + ".jsonl";
    }
}
