using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Pi.AgentCore.Harness;
using PiFileInfo = Pi.AgentCore.Harness.FileInfo;

namespace Pi.AgentCore.Harness.Env;

/// <summary>Construction options for <see cref="SystemExecutionEnv"/>.</summary>
public sealed class SystemExecutionEnvOptions
{
    /// <summary>Current working directory used for relative paths and commands.</summary>
    public required string Cwd { get; init; }

    /// <summary>Optional explicit bash executable.</summary>
    public string? ShellPath { get; init; }

    /// <summary>Environment values inherited by shell commands before per-call overrides.</summary>
    public IReadOnlyDictionary<string, string>? ShellEnvironment { get; init; }
}

/// <summary>
/// System-backed execution environment. This is the .NET counterpart of upstream
/// <c>NodeExecutionEnv</c>; the name is intentionally changed because this port has no Node runtime.
/// </summary>
public class SystemExecutionEnv : ExecutionEnv
{
    private const int _maxTimeoutMilliseconds = int.MaxValue;
    private const double _maxTimeoutSeconds = _maxTimeoutMilliseconds / 1000d;
    private const int _exitStandardIoGraceMilliseconds = 100;
    private static readonly Regex _legacyWslBashPath = new(
        @"^[a-z]:\\windows\\(?:system32|sysnative)\\bash\.exe$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string? _shellPath;
    private readonly IReadOnlyDictionary<string, string>? _shellEnvironment;
    private readonly ConcurrentDictionary<int, ActiveProcess> _activeProcesses = new();

    /// <summary>Creates an environment from explicit options.</summary>
    public SystemExecutionEnv(SystemExecutionEnvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Cwd = Path.GetFullPath(options.Cwd);
        _shellPath = options.ShellPath;
        _shellEnvironment = options.ShellEnvironment is null
            ? null
            : new Dictionary<string, string>(options.ShellEnvironment, StringComparer.Ordinal);
    }

    /// <summary>Creates an environment from a working directory and optional shell settings.</summary>
    public SystemExecutionEnv(
        string cwd,
        string? shellPath = null,
        IReadOnlyDictionary<string, string>? shellEnvironment = null)
        : this(new SystemExecutionEnvOptions
        {
            Cwd = cwd,
            ShellPath = shellPath,
            ShellEnvironment = shellEnvironment,
        })
    {
    }

    /// <inheritdoc />
    public string Cwd { get; }

    /// <inheritdoc />
    public Task<Result<string, FileError>> AbsolutePathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<string, FileError>.Failure(AbortedFileError(path)));
        }

        try
        {
            return Task.FromResult(Result<string, FileError>.Success(ResolvePath(path)));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<string, FileError>.Failure(ToFileError(error, path)));
        }
    }

    /// <inheritdoc />
    public Task<Result<string, FileError>> JoinPathAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<string, FileError>.Failure(AbortedFileError()));
        }

        try
        {
            return Task.FromResult(Result<string, FileError>.Success(Path.Join(parts.ToArray())));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<string, FileError>.Failure(ToFileError(error)));
        }
    }

    /// <inheritdoc />
    public virtual async Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
        string command,
        ShellExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        options ??= new ShellExecOptions();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(options.AbortSignal, cancellationToken);
        var signal = linked.Token;
        if (signal.IsCancellationRequested)
        {
            return Result<ShellExecResult, ExecutionError>.Failure(
                new ExecutionError(ExecutionErrorCodes.Aborted, "aborted"));
        }

        var timeout = ResolveTimeoutMilliseconds(options.Timeout);
        if (!timeout.Ok)
        {
            return Result<ShellExecResult, ExecutionError>.Failure(timeout.Error!);
        }

        string cwd;
        try
        {
            cwd = options.Cwd is null ? Cwd : ResolvePath(options.Cwd);
        }
        catch (Exception error)
        {
            return Result<ShellExecResult, ExecutionError>.Failure(
                new ExecutionError(ExecutionErrorCodes.SpawnError, error.Message, error));
        }

        if (!Directory.Exists(cwd))
        {
            return Result<ShellExecResult, ExecutionError>.Failure(
                new ExecutionError(
                    ExecutionErrorCodes.SpawnError,
                    $"Working directory does not exist: {cwd}\nCannot execute bash commands."));
        }

        var shellConfig = await GetShellConfigAsync().ConfigureAwait(false);
        if (!shellConfig.Ok)
        {
            return Result<ShellExecResult, ExecutionError>.Failure(shellConfig.Error!);
        }

        return await ExecuteProcessAsync(command, cwd, shellConfig.Value!, options, timeout.Value, signal)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<Result<string, FileError>> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = TryResolvePath(path, out var resolvedPath, out var pathError);
        if (!resolved)
        {
            return Result<string, FileError>.Failure(pathError!);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<string, FileError>.Failure(AbortedFileError(resolvedPath));
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Result<string, FileError>.Failure(AbortedFileError(resolvedPath))
                : Result<string, FileError>.Success(_utf8.GetString(bytes));
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Failure(ToFileError(error, resolvedPath));
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(
        string path,
        ReadTextLinesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = TryResolvePath(path, out var resolvedPath, out var pathError);
        if (!resolved)
        {
            return Result<IReadOnlyList<string>, FileError>.Failure(pathError!);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<string>, FileError>.Failure(AbortedFileError(resolvedPath));
        }

        if (options?.MaxLines is <= 0)
        {
            return Result<IReadOnlyList<string>, FileError>.Success([]);
        }

        try
        {
            await using var stream = new FileStream(
                resolvedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, _utf8, detectEncodingFromByteOrderMarks: false);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Result<IReadOnlyList<string>, FileError>.Failure(AbortedFileError(resolvedPath));
                }

                lines.Add(line);
                if (options?.MaxLines is not null && lines.Count >= options.MaxLines.Value)
                {
                    break;
                }
            }

            return cancellationToken.IsCancellationRequested
                ? Result<IReadOnlyList<string>, FileError>.Failure(AbortedFileError(resolvedPath))
                : Result<IReadOnlyList<string>, FileError>.Success(lines);
        }
        catch (Exception error)
        {
            return Result<IReadOnlyList<string>, FileError>.Failure(ToFileError(error, resolvedPath));
        }
    }

    /// <inheritdoc />
    public async Task<Result<byte[], FileError>> ReadBinaryFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = TryResolvePath(path, out var resolvedPath, out var pathError);
        if (!resolved)
        {
            return Result<byte[], FileError>.Failure(pathError!);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<byte[], FileError>.Failure(AbortedFileError(resolvedPath));
        }

        try
        {
            return Result<byte[], FileError>.Success(
                await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            return Result<byte[], FileError>.Failure(ToFileError(error, resolvedPath));
        }
    }

    /// <inheritdoc />
    public virtual async Task<Result<bool, FileError>> WriteFileAsync(
        string path,
        object content,
        CancellationToken cancellationToken = default)
    {
        var resolved = TryResolvePath(path, out var resolvedPath, out var pathError);
        if (!resolved)
        {
            return Result<bool, FileError>.Failure(pathError!);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<bool, FileError>.Failure(AbortedFileError(resolvedPath));
        }

        try
        {
            var bytes = GetContentBytes(content);
            var parent = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Result<bool, FileError>.Failure(AbortedFileError(resolvedPath));
            }

            await File.WriteAllBytesAsync(resolvedPath, bytes, cancellationToken).ConfigureAwait(false);
            return Result<bool, FileError>.Success(true);
        }
        catch (Exception error)
        {
            return Result<bool, FileError>.Failure(ToFileError(error, resolvedPath));
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool, FileError>> AppendFileAsync(
        string path,
        object content,
        CancellationToken cancellationToken = default)
    {
        var resolved = TryResolvePath(path, out var resolvedPath, out var pathError);
        if (!resolved)
        {
            return Result<bool, FileError>.Failure(pathError!);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<bool, FileError>.Failure(AbortedFileError(resolvedPath));
        }

        try
        {
            var bytes = GetContentBytes(content);
            var parent = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using var stream = new FileStream(
                resolvedPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return Result<bool, FileError>.Success(true);
        }
        catch (Exception error)
        {
            return Result<bool, FileError>.Failure(ToFileError(error, resolvedPath));
        }
    }

    /// <inheritdoc />
    public Task<Result<bool, FileError>> RenameFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var sourceResolved = TryResolvePath(sourcePath, out var source, out var sourceError);
        if (!sourceResolved)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(sourceError!));
        }

        var destinationResolved = TryResolvePath(destinationPath, out var destination, out var destinationError);
        if (!destinationResolved)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(destinationError!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(AbortedFileError(destination)));
        }

        try
        {
            File.Move(source, destination, overwrite: true);
            return Task.FromResult(Result<bool, FileError>.Success(true));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(ToFileError(error, source)));
        }
    }

    /// <inheritdoc />
    public Task<Result<PiFileInfo, FileError>> FileInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(path, out var resolved, out var pathError))
        {
            return Task.FromResult(Result<PiFileInfo, FileError>.Failure(pathError!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<PiFileInfo, FileError>.Failure(AbortedFileError(resolved)));
        }

        try
        {
            return Task.FromResult(GetFileInfo(resolved));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<PiFileInfo, FileError>.Failure(ToFileError(error, resolved)));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<PiFileInfo>, FileError>> ListDirAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(path, out var resolved, out var pathError))
        {
            return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(pathError!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(AbortedFileError(resolved)));
        }

        try
        {
            if (File.Exists(resolved) || (IsSymbolicLink(resolved) && !Directory.Exists(resolved)))
            {
                return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(
                    new FileError(FileErrorCodes.NotDirectory, $"Not a directory: {resolved}", resolved)));
            }

            if (!Directory.Exists(resolved))
            {
                return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(
                    new FileError(FileErrorCodes.NotFound, $"Path not found: {resolved}", resolved)));
            }

            var result = new List<PiFileInfo>();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(resolved))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(AbortedFileError(resolved)));
                }

                var entryInfo = GetFileInfo(Path.GetFullPath(entryPath));
                if (!entryInfo.Ok)
                {
                    return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(entryInfo.Error!));
                }

                result.Add(entryInfo.Value!);
            }

            return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Success(result));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<IReadOnlyList<PiFileInfo>, FileError>.Failure(ToFileError(error, resolved)));
        }
    }

    /// <inheritdoc />
    public Task<Result<string, FileError>> CanonicalPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(path, out var resolved, out var pathError))
        {
            return Task.FromResult(Result<string, FileError>.Failure(pathError!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<string, FileError>.Failure(AbortedFileError(resolved)));
        }

        try
        {
            if (!File.Exists(resolved) && !Directory.Exists(resolved) && !IsSymbolicLink(resolved))
            {
                return Task.FromResult(Result<string, FileError>.Failure(
                    new FileError(FileErrorCodes.NotFound, $"Path not found: {resolved}", resolved)));
            }

            FileSystemInfo? target = null;
            try
            {
                target = new System.IO.FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (FileNotFoundException)
            {
                // Try the directory implementation below.
            }

            if (target is null)
            {
                target = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true);
            }

            return Task.FromResult(Result<string, FileError>.Success(
                target is null ? resolved : Path.GetFullPath(target.FullName)));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<string, FileError>.Failure(ToFileError(error, resolved)));
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool, FileError>> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = await FileInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            return Result<bool, FileError>.Success(true);
        }

        return result.Error?.Code == FileErrorCodes.NotFound
            ? Result<bool, FileError>.Success(false)
            : Result<bool, FileError>.Failure(result.Error!);
    }

    /// <inheritdoc />
    public Task<Result<bool, FileError>> CreateDirAsync(
        string path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(path, out var resolved, out var pathError))
        {
            return Task.FromResult(Result<bool, FileError>.Failure(pathError!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(AbortedFileError(resolved)));
        }

        options ??= new CreateDirectoryOptions();
        try
        {
            if (!options.Recursive)
            {
                var parent = Directory.GetParent(resolved)?.FullName;
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    return Task.FromResult(Result<bool, FileError>.Failure(
                        new FileError(FileErrorCodes.NotFound, $"Path not found: {parent}", parent)));
                }
            }

            Directory.CreateDirectory(resolved);
            return Task.FromResult(Result<bool, FileError>.Success(true));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(ToFileError(error, resolved)));
        }
    }

    /// <inheritdoc />
    public Task<Result<bool, FileError>> RemoveAsync(
        string path,
        RemoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(path, out var resolved, out var pathError))
        {
            return Task.FromResult(Result<bool, FileError>.Failure(pathError!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(AbortedFileError(resolved)));
        }

        options ??= new RemoveOptions();
        try
        {
            if (IsSymbolicLink(resolved) || File.Exists(resolved))
            {
                File.Delete(resolved);
            }
            else if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, options.Recursive);
            }
            else if (!options.Force)
            {
                return Task.FromResult(Result<bool, FileError>.Failure(
                    new FileError(FileErrorCodes.NotFound, $"Path not found: {resolved}", resolved)));
            }

            return Task.FromResult(Result<bool, FileError>.Success(true));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<bool, FileError>.Failure(ToFileError(error, resolved)));
        }
    }

    /// <inheritdoc />
    public Task<Result<string, FileError>> CreateTempDirAsync(
        string prefix = "tmp-",
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<string, FileError>.Failure(AbortedFileError()));
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Task.FromResult(Result<string, FileError>.Success(path));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<string, FileError>.Failure(ToFileError(error)));
        }
    }

    /// <inheritdoc />
    public async Task<Result<string, FileError>> CreateTempFileAsync(
        CreateTemporaryFileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CreateTemporaryFileOptions();
        var directory = await CreateTempDirAsync("tmp-", cancellationToken).ConfigureAwait(false);
        if (!directory.Ok)
        {
            return Result<string, FileError>.Failure(directory.Error!);
        }

        var filePath = Path.Combine(
            directory.Value!,
            $"{options.Prefix}{Guid.NewGuid():N}{options.Suffix}");
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result<string, FileError>.Failure(AbortedFileError(filePath));
            }

            await File.WriteAllBytesAsync(filePath, [], cancellationToken).ConfigureAwait(false);
            return Result<string, FileError>.Success(filePath);
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Failure(ToFileError(error, filePath));
        }
    }

    /// <inheritdoc />
    public Task CleanupAsync()
    {
        foreach (var activeProcess in _activeProcesses.Values.ToArray())
        {
            try
            {
                activeProcess.StopSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The process completed concurrently; cleanup remains best effort.
            }

            KillProcessTree(activeProcess.Process);
        }

        _activeProcesses.Clear();
        return Task.CompletedTask;
    }

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private async Task<Result<ShellExecResult, ExecutionError>> ExecuteProcessAsync(
        string command,
        string cwd,
        ShellConfig config,
        ShellExecOptions options,
        int timeoutMilliseconds,
        CancellationToken signal)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = config.Shell,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = config.CommandTransport == CommandTransport.StandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in config.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (config.CommandTransport == CommandTransport.Arguments)
        {
            process.StartInfo.ArgumentList.Add(command);
        }

        if (!options.InheritEnvironment)
        {
            process.StartInfo.Environment.Clear();
        }
        if (options.InheritEnvironment && _shellEnvironment is not null)
        {
            foreach (var pair in _shellEnvironment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }
        if (options.Environment is not null)
        {
            foreach (var pair in options.Environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            if (!process.Start())
            {
                return Result<ShellExecResult, ExecutionError>.Failure(
                    new ExecutionError(ExecutionErrorCodes.SpawnError, "Unable to start shell process."));
            }
        }
        catch (Exception error)
        {
            return Result<ShellExecResult, ExecutionError>.Failure(
                new ExecutionError(ExecutionErrorCodes.SpawnError, error.Message, error));
        }

        using var processStop = new CancellationTokenSource();
        _activeProcesses[process.Id] = new ActiveProcess(process, processStop);
        using var streamCancellation = new CancellationTokenSource();
        Exception? callbackError = null;
        var callbackGate = new object();
        void HandleChunk(Action<string>? callback, string chunk)
        {
            if (callback is null)
            {
                return;
            }

            try
            {
                callback(chunk);
            }
            catch (Exception error)
            {
                lock (callbackGate)
                {
                    callbackError ??= error;
                }
                KillProcessTree(process);
                streamCancellation.Cancel();
            }
        }

        var stdoutTask = ReadStreamAsync(process.StandardOutput, chunk => HandleChunk(options.OnStdout, chunk), streamCancellation.Token);
        var stderrTask = ReadStreamAsync(process.StandardError, chunk => HandleChunk(options.OnStderr, chunk), streamCancellation.Token);
        try
        {
            if (config.CommandTransport == CommandTransport.StandardInput)
            {
                try
                {
                    await process.StandardInput.WriteAsync(command).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (Exception error)
                {
                    lock (callbackGate)
                    {
                        callbackError ??= error;
                    }
                    KillProcessTree(process);
                    streamCancellation.Cancel();
                }
            }

            var waitForExit = process.WaitForExitAsync(CancellationToken.None);
            var timeoutTask = timeoutMilliseconds > 0
                ? Task.Delay(timeoutMilliseconds, CancellationToken.None)
                : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, signal);
            var cleanupTask = Task.Delay(Timeout.InfiniteTimeSpan, processStop.Token);
            var winner = await Task.WhenAny(waitForExit, timeoutTask, cancellationTask, cleanupTask).ConfigureAwait(false);
            var timedOut = winner == timeoutTask && !waitForExit.IsCompleted;
            var aborted = winner == cancellationTask && !waitForExit.IsCompleted;
            var stoppedByCleanup = winner == cleanupTask && !waitForExit.IsCompleted;
            if (timedOut || aborted || stoppedByCleanup)
            {
                KillProcessTree(process);
                await Task.WhenAny(
                        waitForExit,
                        Task.Delay(_exitStandardIoGraceMilliseconds, CancellationToken.None))
                    .ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await waitForExit.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    lock (callbackGate)
                    {
                        callbackError ??= error;
                    }
                }
            }

            var streams = Task.WhenAll(stdoutTask, stderrTask);
            if (!streams.IsCompleted)
            {
                await Task.WhenAny(
                        streams,
                        Task.Delay(_exitStandardIoGraceMilliseconds, CancellationToken.None))
                    .ConfigureAwait(false);
                if (!streams.IsCompleted)
                {
                    streamCancellation.Cancel();
                }
            }

            string[] output;
            try
            {
                output = await streams.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (callbackGate)
                {
                    callbackError ??= error;
                }
                output = [string.Empty, string.Empty];
            }

            lock (callbackGate)
            {
                if (callbackError is not null)
                {
                    return Result<ShellExecResult, ExecutionError>.Failure(
                        new ExecutionError(ExecutionErrorCodes.CallbackError, callbackError.Message, callbackError));
                }
            }

            if (timedOut)
            {
                return Result<ShellExecResult, ExecutionError>.Failure(
                    new ExecutionError(
                        ExecutionErrorCodes.Timeout,
                        $"timeout:{options.Timeout?.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (aborted || signal.IsCancellationRequested)
            {
                return Result<ShellExecResult, ExecutionError>.Failure(
                    new ExecutionError(ExecutionErrorCodes.Aborted, "aborted"));
            }

            return Result<ShellExecResult, ExecutionError>.Success(new ShellExecResult
            {
                Stdout = output[0],
                Stderr = output[1],
                ExitCode = process.HasExited ? process.ExitCode : 0,
            });
        }
        finally
        {
            _activeProcesses.TryRemove(process.Id, out _);
        }
    }

    private static async Task<string> ReadStreamAsync(
        StreamReader reader,
        Action<string> onChunk,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return builder.ToString();
                }

                var chunk = new string(buffer, 0, count);
                builder.Append(chunk);
                onChunk(chunk);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return builder.ToString();
        }
    }

    private sealed record ActiveProcess(Process Process, CancellationTokenSource StopSource);

    private async Task<Result<ShellConfig, ExecutionError>> GetShellConfigAsync()
    {
        if (!string.IsNullOrEmpty(_shellPath))
        {
            if (File.Exists(_shellPath))
            {
                return Result<ShellConfig, ExecutionError>.Success(CreateShellConfig(_shellPath));
            }

            return Result<ShellConfig, ExecutionError>.Failure(
                new ExecutionError(ExecutionErrorCodes.ShellUnavailable, $"Custom shell path not found: {_shellPath}"));
        }

        if (OperatingSystem.IsWindows())
        {
            var candidates = new List<string>();
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
            }
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "Git", "bin", "bash.exe"));
            }

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Result<ShellConfig, ExecutionError>.Success(CreateShellConfig(candidate));
                }
            }

            var bashOnPath = await FindExecutableOnPathAsync("where", "bash.exe").ConfigureAwait(false);
            if (bashOnPath is not null)
            {
                return Result<ShellConfig, ExecutionError>.Success(CreateShellConfig(bashOnPath));
            }

            return Result<ShellConfig, ExecutionError>.Failure(
                new ExecutionError(
                    ExecutionErrorCodes.ShellUnavailable,
                    "No bash shell found. Options:\n" +
                    "  1. Install Git for Windows: https://git-scm.com/download/win\n" +
                    "  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n" +
                    "  3. Configure an explicit shellPath\n\n" +
                    $"Searched Git Bash in:\n{string.Join("\n", candidates.Select(static path => $"  {path}"))}"));
        }

        if (File.Exists("/bin/bash"))
        {
            return Result<ShellConfig, ExecutionError>.Success(CreateShellConfig("/bin/bash"));
        }

        var bash = await FindExecutableOnPathAsync("which", "bash").ConfigureAwait(false);
        return Result<ShellConfig, ExecutionError>.Success(
            bash is not null ? CreateShellConfig(bash) : new ShellConfig("sh", ["-c"], CommandTransport.Arguments));
    }

    private static async Task<string?> FindExecutableOnPathAsync(string probe, string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = probe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(executable);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var first = output.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return !string.IsNullOrEmpty(first) && File.Exists(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    private static ShellConfig CreateShellConfig(string shell) =>
        _legacyWslBashPath.IsMatch(shell.Replace('/', '\\'))
            ? new ShellConfig(shell, ["-s"], CommandTransport.StandardInput)
            : new ShellConfig(shell, ["-c"], CommandTransport.Arguments);

    private static Result<int, ExecutionError> ResolveTimeoutMilliseconds(double? timeout)
    {
        if (timeout is null)
        {
            return Result<int, ExecutionError>.Success(0);
        }

        if (!double.IsFinite(timeout.Value) || timeout.Value <= 0)
        {
            return Result<int, ExecutionError>.Failure(
                new ExecutionError(ExecutionErrorCodes.Timeout, "Invalid timeout: must be a finite number of seconds"));
        }

        var milliseconds = timeout.Value * 1000d;
        if (milliseconds > _maxTimeoutMilliseconds)
        {
            return Result<int, ExecutionError>.Failure(
                new ExecutionError(
                    ExecutionErrorCodes.Timeout,
                    $"Invalid timeout: maximum is {_maxTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds"));
        }

        return Result<int, ExecutionError>.Success(Math.Max(1, (int)Math.Ceiling(milliseconds)));
    }

    private string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (normalized == "~")
        {
            normalized = home;
        }
        else if (normalized.StartsWith("~/", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && normalized.StartsWith("~\\", StringComparison.Ordinal)))
        {
            normalized = Path.Combine(home, normalized[2..]);
        }
        else if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            normalized = uri.LocalPath;
        }

        return Path.IsPathFullyQualified(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(Cwd, normalized));
    }

    private bool TryResolvePath(string path, out string resolved, out FileError? error)
    {
        try
        {
            resolved = ResolvePath(path);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            resolved = string.Empty;
            error = ToFileError(exception, path);
            return false;
        }
    }

    private static Result<PiFileInfo, FileError> GetFileInfo(string resolved)
    {
        if (!IsSymbolicLink(resolved) && !File.Exists(resolved) && !Directory.Exists(resolved))
        {
            return Result<PiFileInfo, FileError>.Failure(
                new FileError(FileErrorCodes.NotFound, $"Path not found: {resolved}", resolved));
        }

        var kind = IsSymbolicLink(resolved)
            ? FileKinds.Symlink
            : Directory.Exists(resolved) ? FileKinds.Directory : FileKinds.File;
        try
        {
            var systemInfo = kind == FileKinds.Directory
                ? (FileSystemInfo)new DirectoryInfo(resolved)
                : new System.IO.FileInfo(resolved);
            var size = kind == FileKinds.File ? ((System.IO.FileInfo)systemInfo).Length : 0;
            return Result<PiFileInfo, FileError>.Success(new PiFileInfo
            {
                Name = Path.GetFileName(resolved),
                Path = resolved,
                Kind = kind,
                Size = size,
                MtimeMs = new DateTimeOffset(systemInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception error)
        {
            return Result<PiFileInfo, FileError>.Failure(ToFileError(error, resolved));
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            if (new System.IO.FileInfo(path).LinkTarget is not null)
            {
                return true;
            }
        }
        catch
        {
            // Try the directory representation below.
        }

        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] GetContentBytes(object content) => content switch
    {
        string text => _utf8.GetBytes(text),
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Memory<byte> memory => memory.ToArray(),
        _ => throw new ArgumentException("File content must be a string or byte array.", nameof(content)),
    };

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                using var taskkill = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(
                            Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows",
                            "System32",
                            "taskkill.exe"),
                        ArgumentList = { "/F", "/T", "/PID", process.Id.ToString(CultureInfo.InvariantCulture) },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                try
                {
                    taskkill.Start();
                    taskkill.BeginErrorReadLine();
                    taskkill.WaitForExit(1000);
                }
                catch
                {
                    // The target may already have exited or taskkill may be unavailable.
                }
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cleanup and cancellation are best effort.
        }
    }

    private static FileError AbortedFileError(string? path = null) =>
        new(FileErrorCodes.Aborted, "aborted", path);

    private static FileError ToFileError(Exception error, string? fallbackPath = null)
    {
        if (error is FileError fileError)
        {
            return fileError;
        }

        var path = fallbackPath;
        var code = error switch
        {
            OperationCanceledException => FileErrorCodes.Aborted,
            UnauthorizedAccessException => FileErrorCodes.PermissionDenied,
            FileNotFoundException => FileErrorCodes.NotFound,
            DirectoryNotFoundException => FileErrorCodes.NotFound,
            NotSupportedException => FileErrorCodes.NotSupported,
            ArgumentException => FileErrorCodes.Invalid,
            IOException io when io.Message.Contains("not a directory", StringComparison.OrdinalIgnoreCase) => FileErrorCodes.NotDirectory,
            IOException io when io.Message.Contains("is a directory", StringComparison.OrdinalIgnoreCase) => FileErrorCodes.IsDirectory,
            _ => FileErrorCodes.Unknown,
        };
        return new FileError(code, error.Message, path, error);
    }

    private sealed record ShellConfig(string Shell, IReadOnlyList<string> Arguments, CommandTransport CommandTransport);

    private enum CommandTransport
    {
        Arguments,
        StandardInput,
    }
}
