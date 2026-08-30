using System.Text.Json.Nodes;

namespace Pi.AgentCore.Harness.Session.Jsonl;

/// <summary>Filesystem metadata used by the JSONL repository.</summary>
public sealed record JsonlFileInfo
{
    /// <summary>Whether the path is a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Whether the path is a symbolic link.</summary>
    public bool IsSymbolicLink { get; init; }

    /// <summary>Last-write time in Unix milliseconds.</summary>
    public double ModifiedAt { get; init; }
}

/// <summary>One directory item returned by a JSONL filesystem.</summary>
public sealed record JsonlDirectoryEntry
{
    /// <summary>Absolute or filesystem-native path.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the item is a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Whether the item is a symbolic link.</summary>
    public bool IsSymbolicLink { get; init; }
}

/// <summary>Filesystem surface required by <see cref="JsonlSessionRepo"/>.</summary>
public interface IJsonlFileSystem
{
    /// <summary>Resolves a path to an absolute path.</summary>
    string AbsolutePath(string path);

    /// <summary>Combines path segments.</summary>
    string JoinPath(params string[] paths);

    /// <summary>Reads a complete UTF-8 text file.</summary>
    Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads a UTF-8 text file as physical lines.</summary>
    Task<IReadOnlyList<string>> ReadTextLinesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes a UTF-8 file and flushes it to the operating system.</summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>Appends UTF-8 text and flushes it to the operating system.</summary>
    Task AppendFileAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>Renames a file atomically, replacing an existing destination.</summary>
    Task RenameFileAsync(string source, string destination, CancellationToken cancellationToken = default);

    /// <summary>Gets filesystem metadata.</summary>
    Task<JsonlFileInfo> FileInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists one directory.</summary>
    Task<IReadOnlyList<JsonlDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Tests whether a path exists.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates a directory and its parents.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Removes a file or empty directory.</summary>
    Task RemoveAsync(string path, bool force = false, CancellationToken cancellationToken = default);
}

/// <summary>Default local filesystem implementation for JSONL sessions.</summary>
public sealed class LocalJsonlFileSystem : IJsonlFileSystem
{
    /// <inheritdoc />
    public string AbsolutePath(string path) => System.IO.Path.GetFullPath(path);

    /// <inheritdoc />
    public string JoinPath(params string[] paths) => System.IO.Path.Combine(paths);

    /// <inheritdoc />
    public async Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadTextLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        var content = await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
        return content.Split('\n').Select(static line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public async Task AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public Task RenameFileAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(source, destination, overwrite: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<JsonlFileInfo> FileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var modified = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        var modifiedAt = (modified - DateTime.UnixEpoch).TotalMilliseconds;
        return Task.FromResult<JsonlFileInfo>(new()
        {
            IsDirectory = isDirectory,
            IsSymbolicLink = attributes.HasFlag(FileAttributes.ReparsePoint),
            ModifiedAt = modifiedAt,
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JsonlDirectoryEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(item =>
            {
                var attributes = File.GetAttributes(item);
                return new JsonlDirectoryEntry
                {
                    Path = item,
                    IsDirectory = attributes.HasFlag(FileAttributes.Directory),
                    IsSymbolicLink = attributes.HasFlag(FileAttributes.ReparsePoint),
                };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<JsonlDirectoryEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(path) || Directory.Exists(path));
    }

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string path, bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: force);
        }

        return Task.CompletedTask;
    }
}

/// <summary>JSONL v4 metadata returned by the harness repository.</summary>
public sealed record JsonlSessionMetadata : SessionMetadata
{
    /// <summary>Working directory associated with the session.</summary>
    public required string Cwd { get; init; }

    /// <summary>Absolute JSONL file path.</summary>
    public required string Path { get; init; }

    /// <summary>Current file modification time in Unix milliseconds.</summary>
    public double ModifiedAt { get; init; }

    /// <summary>Source format version.</summary>
    public int SourceFormat { get; init; } = 4;

    /// <summary>Unresolved parent path from a legacy import.</summary>
    public string? LegacyParentSessionPath { get; init; }

    /// <summary>Application metadata bag.</summary>
    public JsonObject? Metadata { get; init; }
}

/// <summary>JSONL repository creation options.</summary>
public sealed record JsonlSessionCreateOptions : SessionCreateOptions
{
    /// <summary>Working directory associated with the session.</summary>
    public required string Cwd { get; init; }

    /// <summary>Application metadata bag.</summary>
    public JsonObject? Metadata { get; init; }
}

/// <summary>JSONL repository list filter.</summary>
public sealed record JsonlSessionListOptions
{
    /// <summary>Optional exact working-directory filter.</summary>
    public string? Cwd { get; init; }
}

/// <summary>JSONL v4 header line.</summary>
public sealed record JsonlV4Header
{
    /// <summary>Header discriminator.</summary>
    public string Kind { get; init; } = "header";

    /// <summary>Session format version.</summary>
    public int Version { get; init; } = 4;

    /// <summary>Session identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Creation time in Unix milliseconds.</summary>
    public long CreatedAt { get; init; }

    /// <summary>Working directory.</summary>
    public required string Cwd { get; init; }

    /// <summary>Resolved parent session identifier.</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>Unresolved legacy parent path.</summary>
    public string? LegacyParentSessionPath { get; init; }

    /// <summary>Application metadata.</summary>
    public JsonObject? Metadata { get; init; }

    internal JsonObject? RawFields { get; init; }
}
