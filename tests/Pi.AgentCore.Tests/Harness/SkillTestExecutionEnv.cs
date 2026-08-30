using System.Text;

using Pi.AgentCore.Harness;

namespace Pi.AgentCore.Tests.Harness;

internal sealed class SkillTestExecutionEnv : ExecutionEnv
{
    private sealed record Node(string Kind, string? Content = null, string? Target = null);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public SkillTestExecutionEnv()
    {
        Cwd = NormalizePath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pi-skill-test-{Guid.NewGuid():N}"));
        _nodes[Cwd] = new Node(FileKinds.Directory);
    }

    public string Cwd { get; }

    public string Absolute(string path) => Address(path);

    public void AddDirectory(string path)
    {
        var address = Address(path);
        EnsureDirectory(address);
    }

    public void AddFile(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var address = Address(path);
        EnsureDirectory(System.IO.Path.GetDirectoryName(address)!);
        _nodes[address] = new Node(FileKinds.File, content);
    }

    public void AddSymlink(string path, string target)
    {
        var address = Address(path);
        EnsureDirectory(System.IO.Path.GetDirectoryName(address)!);
        var targetAddress = System.IO.Path.IsPathRooted(target)
            ? NormalizePath(target)
            : NormalizePath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(address)!, target));
        _nodes[address] = new Node(FileKinds.Symlink, Target: targetAddress);
    }

    public Task<Result<string, FileError>> AbsolutePathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<string, FileError>.Success(Address(path)));
    }

    public Task<Result<string, FileError>> JoinPathAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return Task.FromResult(Result<string, FileError>.Failure(Failure(FileErrorCodes.Invalid, "No path parts.")));
        }

        return Task.FromResult(Result<string, FileError>.Success(Address(System.IO.Path.Combine(parts.ToArray()))));
    }

    public Task<Result<string, FileError>> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = ResolveFollowed(Address(path));
        if (!_nodes.TryGetValue(address, out var node))
        {
            return Task.FromResult(Result<string, FileError>.Failure(Failure(FileErrorCodes.NotFound, "Path not found.", path)));
        }

        return node.Kind == FileKinds.File
            ? Task.FromResult(Result<string, FileError>.Success(node.Content ?? string.Empty))
            : Task.FromResult(Result<string, FileError>.Failure(Failure(FileErrorCodes.IsDirectory, "Path is not a file.", path)));
    }

    public async Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(
        string path,
        ReadTextLinesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!text.Ok)
        {
            return Result<IReadOnlyList<string>, FileError>.Failure(text.Error!);
        }

        var lines = text.Value!.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).AsEnumerable();
        if (options?.MaxLines is int maxLines)
        {
            lines = lines.Take(maxLines);
        }

        return Result<IReadOnlyList<string>, FileError>.Success(lines.ToArray());
    }

    public async Task<Result<byte[], FileError>> ReadBinaryFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var text = await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
        return text.Ok
            ? Result<byte[], FileError>.Success(Encoding.UTF8.GetBytes(text.Value!))
            : Result<byte[], FileError>.Failure(text.Error!);
    }

    public Task<Result<bool, FileError>> WriteFileAsync(
        string path,
        object content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddFile(path, content as string ?? content.ToString() ?? string.Empty);
        return Task.FromResult(Result<bool, FileError>.Success(true));
    }

    public async Task<Result<bool, FileError>> AppendFileAsync(
        string path,
        object content,
        CancellationToken cancellationToken = default)
    {
        var existing = await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
        var text = content as string ?? content.ToString() ?? string.Empty;
        AddFile(path, (existing.Ok ? existing.Value : string.Empty) + text);
        return Result<bool, FileError>.Success(true);
    }

    public Task<Result<bool, FileError>> RenameFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolveFollowed(Address(sourcePath));
        if (!_nodes.TryGetValue(source, out var node))
        {
            return Task.FromResult(Result<bool, FileError>.Failure(Failure(FileErrorCodes.NotFound, "Path not found.", sourcePath)));
        }

        var destination = Address(destinationPath);
        EnsureDirectory(System.IO.Path.GetDirectoryName(destination)!);
        _nodes[destination] = node;
        _nodes.Remove(source);
        return Task.FromResult(Result<bool, FileError>.Success(true));
    }

    public Task<Result<Pi.AgentCore.Harness.FileInfo, FileError>> FileInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = Address(path);
        if (!_nodes.TryGetValue(address, out var node))
        {
            var resolved = ResolveFollowed(address);
            if (!_nodes.TryGetValue(resolved, out node))
            {
                return Task.FromResult(Result<Pi.AgentCore.Harness.FileInfo, FileError>.Failure(
                    Failure(FileErrorCodes.NotFound, "Path not found.", path)));
            }
        }

        return Task.FromResult(Result<Pi.AgentCore.Harness.FileInfo, FileError>.Success(ToFileInfo(address, node)));
    }

    public Task<Result<IReadOnlyList<Pi.AgentCore.Harness.FileInfo>, FileError>> ListDirAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = Address(path);
        var actual = ResolveFollowed(requested);
        if (!_nodes.TryGetValue(actual, out var directory))
        {
            return Task.FromResult(Result<IReadOnlyList<Pi.AgentCore.Harness.FileInfo>, FileError>.Failure(
                Failure(FileErrorCodes.NotFound, "Path not found.", path)));
        }

        if (directory.Kind != FileKinds.Directory)
        {
            return Task.FromResult(Result<IReadOnlyList<Pi.AgentCore.Harness.FileInfo>, FileError>.Failure(
                Failure(FileErrorCodes.NotDirectory, "Path is not a directory.", path)));
        }

        var prefix = actual + System.IO.Path.DirectorySeparatorChar;
        var children = _nodes
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => (Path: pair.Key, Node: pair.Value))
            .Where(pair => !pair.Path[prefix.Length..].Contains(System.IO.Path.DirectorySeparatorChar))
            .OrderBy(pair => pair.Path, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var name = System.IO.Path.GetFileName(pair.Path);
                var addressedPath = NormalizePath(System.IO.Path.Combine(requested, name));
                return ToFileInfo(addressedPath, pair.Node);
            })
            .ToArray();
        return Task.FromResult(Result<IReadOnlyList<Pi.AgentCore.Harness.FileInfo>, FileError>.Success(children));
    }

    public Task<Result<string, FileError>> CanonicalPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = Address(path);
        return _nodes.ContainsKey(ResolveFollowed(address))
            ? Task.FromResult(Result<string, FileError>.Success(ResolveFollowed(address)))
            : Task.FromResult(Result<string, FileError>.Failure(Failure(FileErrorCodes.NotFound, "Path not found.", path)));
    }

    public Task<Result<bool, FileError>> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<bool, FileError>.Success(_nodes.ContainsKey(ResolveFollowed(Address(path)))));
    }

    public Task<Result<bool, FileError>> CreateDirAsync(
        string path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddDirectory(path);
        return Task.FromResult(Result<bool, FileError>.Success(true));
    }

    public Task<Result<bool, FileError>> RemoveAsync(
        string path,
        RemoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = Address(path);
        var removed = _nodes.Remove(address);
        if (options?.Recursive == true)
        {
            foreach (var child in _nodes.Keys.Where(candidate => candidate.StartsWith(address + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _nodes.Remove(child);
                removed = true;
            }
        }

        return Task.FromResult(Result<bool, FileError>.Success(removed || options?.Force == true));
    }

    public Task<Result<string, FileError>> CreateTempDirAsync(
        string prefix = "tmp-",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Address(prefix + Guid.NewGuid().ToString("N"));
        AddDirectory(path);
        return Task.FromResult(Result<string, FileError>.Success(path));
    }

    public Task<Result<string, FileError>> CreateTempFileAsync(
        CreateTemporaryFileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actual = options ?? new CreateTemporaryFileOptions();
        var path = Address(actual.Prefix + Guid.NewGuid().ToString("N") + actual.Suffix);
        AddFile(path, string.Empty);
        return Task.FromResult(Result<string, FileError>.Success(path));
    }

    public Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
        string command,
        ShellExecOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ShellExecResult, ExecutionError>.Failure(
            new ExecutionError(ExecutionErrorCodes.Unknown, "Shell execution is not available in the test environment.")));

    public Task CleanupAsync() => Task.CompletedTask;

    private string Address(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NormalizePath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Cwd, path));
    }

    private void EnsureDirectory(string address)
    {
        address = NormalizePath(address);
        if (_nodes.ContainsKey(address))
        {
            return;
        }

        var parent = System.IO.Path.GetDirectoryName(address);
        if (parent is not null && !string.Equals(parent, address, StringComparison.OrdinalIgnoreCase))
        {
            EnsureDirectory(parent);
        }

        _nodes[address] = new Node(FileKinds.Directory);
    }

    private string ResolveFollowed(string address)
    {
        var resolved = address;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var link = _nodes
                .Where(pair => pair.Value.Kind == FileKinds.Symlink &&
                               (string.Equals(resolved, pair.Key, StringComparison.OrdinalIgnoreCase) ||
                                resolved.StartsWith(pair.Key + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(pair => pair.Key.Length)
                .FirstOrDefault();
            if (link.Value is null)
            {
                return resolved;
            }

            var suffix = resolved.Length == link.Key.Length ? string.Empty : resolved[(link.Key.Length + 1)..];
            resolved = NormalizePath(string.IsNullOrEmpty(suffix)
                ? link.Value.Target!
                : System.IO.Path.Combine(link.Value.Target!, suffix));
        }

        return resolved;
    }

    private static Pi.AgentCore.Harness.FileInfo ToFileInfo(string address, Node node) => new()
    {
        Name = System.IO.Path.GetFileName(address),
        Path = address,
        Kind = node.Kind,
        Size = node.Content is null ? 0 : Encoding.UTF8.GetByteCount(node.Content),
        MtimeMs = 0,
    };

    private static FileError Failure(string code, string message, string? path = null) => new(code, message, path);

    private static string NormalizePath(string path) =>
        System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
}
