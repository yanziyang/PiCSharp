using System.Text;

namespace Pi.AgentCore.Harness.Utils;

/// <summary>Progress snapshot produced while capturing shell output.</summary>
public sealed record ShellCaptureProgress
{
    /// <summary>Current tail-oriented output.</summary>
    public required string Output { get; init; }

    /// <summary>Current truncation accounting.</summary>
    public required TruncationResult Truncation { get; init; }

    /// <summary>Path containing the complete output after truncation starts.</summary>
    public string? FullOutputPath { get; init; }

    /// <summary>UTF-8 bytes in the current final line.</summary>
    public int LastLineBytes { get; init; }
}

/// <summary>Options for combined stdout/stderr capture.</summary>
public sealed record ShellCaptureOptions
{
    /// <summary>Working directory override.</summary>
    public string? Cwd { get; init; }

    /// <summary>Environment overrides.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Whether the execution environment is inherited.</summary>
    public bool InheritEnvironment { get; init; } = true;

    /// <summary>Timeout in seconds.</summary>
    public double? Timeout { get; init; }

    /// <summary>Cancellation requested by the caller.</summary>
    public CancellationToken AbortSignal { get; init; }

    /// <summary>Receives output chunks and a lazy current-progress callback.</summary>
    public Action<string, Func<ShellCaptureProgress>>? OnChunk { get; init; }

    /// <summary>Returns execution failures in the successful capture value.</summary>
    public bool ReturnExecutionErrors { get; init; }
}

/// <summary>Final output and status returned by shell capture.</summary>
public sealed record ShellCaptureResult
{
    /// <summary>Current tail-oriented output.</summary>
    public required string Output { get; init; }

    /// <summary>Final truncation accounting.</summary>
    public required TruncationResult Truncation { get; init; }

    /// <summary>Path containing the complete output after truncation starts.</summary>
    public string? FullOutputPath { get; init; }

    /// <summary>UTF-8 bytes in the current final line.</summary>
    public int LastLineBytes { get; init; }

    /// <summary>Process exit code, or null when execution was interrupted before exit.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether the command was cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Whether output exceeded one of the default limits.</summary>
    public bool Truncated { get; init; }

    /// <summary>Execution failure retained when requested by the caller.</summary>
    public ExecutionError? ExecutionError { get; init; }
}

/// <summary>Shell-output sanitisation and capture helpers.</summary>
public static class ShellOutput
{
    private const int _maxOutputBytes = Truncate.DefaultMaxBytes * 2;
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Removes control and Unicode formatting characters that corrupt tool output.</summary>
    public static string SanitizeBinaryOutput(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var code = rune.Value;
            if (code == 0x09 || code == 0x0A || code == 0x0D)
            {
                builder.Append(rune);
            }
            else if (code > 0x1F && (code < 0xFFF9 || code > 0xFFFB))
            {
                builder.Append(rune);
            }
        }

        return builder.ToString();
    }

    /// <summary>Executes a command while retaining the bounded tail and full-output file.</summary>
    public static async Task<Result<ShellCaptureResult, ExecutionError>> ExecuteShellWithCaptureAsync(
        ExecutionEnv env,
        string command,
        ShellCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(command);
        options ??= new ShellCaptureOptions();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(options.AbortSignal, cancellationToken);
        var signal = linked.Token;
        var state = new CaptureState(env, options);

        try
        {
            var result = await env.ExecAsync(
                    command,
                    new ShellExecOptions
                    {
                        Cwd = options.Cwd,
                        Environment = options.Environment,
                        InheritEnvironment = options.InheritEnvironment,
                        Timeout = options.Timeout,
                        AbortSignal = signal,
                        OnStdout = state.OnChunk,
                        OnStderr = state.OnChunk,
                    },
                    signal)
                .ConfigureAwait(false);
            state.StopAcceptingOutput();
            state.EnsureFullOutputFileIfTruncated();
            var writeResult = await state.WriteChain.ConfigureAwait(false);
            if (!writeResult.Ok)
            {
                return Result<ShellCaptureResult, ExecutionError>.Failure(ToExecutionError(writeResult.Error));
            }

            var progress = state.CreateProgress();
            if (state.CaptureError is not null)
            {
                return Result<ShellCaptureResult, ExecutionError>.Failure(state.CaptureError);
            }

            if (!result.Ok)
            {
                if (result.Error?.Code == ExecutionErrorCodes.Aborted || signal.IsCancellationRequested)
                {
                    return Result<ShellCaptureResult, ExecutionError>.Success(
                        CaptureState.ToResult(progress, exitCode: null, cancelled: true, executionError: null));
                }

                if (options.ReturnExecutionErrors)
                {
                    return Result<ShellCaptureResult, ExecutionError>.Success(
                        CaptureState.ToResult(progress, exitCode: null, cancelled: false, executionError: result.Error));
                }

                return Result<ShellCaptureResult, ExecutionError>.Failure(result.Error!);
            }

            var cancelled = signal.IsCancellationRequested;
            return Result<ShellCaptureResult, ExecutionError>.Success(
                CaptureState.ToResult(progress, cancelled ? null : result.Value!.ExitCode, cancelled, executionError: null));
        }
        catch (Exception error)
        {
            state.StopAcceptingOutput();
            return Result<ShellCaptureResult, ExecutionError>.Failure(ToExecutionError(error));
        }
    }

    private static ExecutionError ToExecutionError(object? error) => error switch
    {
        ExecutionError executionError => executionError,
        Exception exception => new ExecutionError(ExecutionErrorCodes.Unknown, exception.Message, exception),
        _ => new ExecutionError(ExecutionErrorCodes.Unknown, error?.ToString() ?? "null"),
    };

    private static string TrimToLastUtf8Bytes(string value, int maxBytes)
    {
        var bytes = _utf8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
        {
            start++;
        }

        return _utf8.GetString(bytes, start, bytes.Length - start);
    }

    private sealed class CaptureState
    {
        private readonly object _gate = new();
        private readonly ExecutionEnv _env;
        private readonly ShellCaptureOptions _options;
        private string _tailOutput = string.Empty;
        private int _totalBytes;
        private int _completedLines;
        private bool _hasOpenLine;
        private int _currentLineBytes;
        private bool _fullOutputRequested;
        private bool _acceptingOutput = true;
        private string? _fullOutputPath;
        private ExecutionError? _captureError;
        private Task<Result<bool, FileError>> _writeChain =
            Task.FromResult(Result<bool, FileError>.Success(true));

        public CaptureState(ExecutionEnv env, ShellCaptureOptions options)
        {
            _env = env;
            _options = options;
        }

        public ExecutionError? CaptureError
        {
            get
            {
                lock (_gate)
                {
                    return _captureError;
                }
            }
        }

        public Task<Result<bool, FileError>> WriteChain
        {
            get
            {
                lock (_gate)
                {
                    return _writeChain;
                }
            }
        }

        public void OnChunk(string chunk)
        {
            if (chunk is null)
            {
                return;
            }
            Func<ShellCaptureProgress>? progressFactory = null;
            var cleanText = string.Empty;
            lock (_gate)
            {
                if (!_acceptingOutput)
                {
                    return;
                }

                try
                {
                    cleanText = SanitizeBinaryOutput(chunk).Replace("\r", string.Empty, StringComparison.Ordinal);
                    var textBytes = _utf8.GetByteCount(cleanText);
                    _totalBytes += textBytes;
                    _completedLines += cleanText.Count(static character => character == '\n');
                    var lastNewline = cleanText.LastIndexOf('\n');
                    if (lastNewline >= 0)
                    {
                        var trailingText = cleanText[(lastNewline + 1)..];
                        _currentLineBytes = _utf8.GetByteCount(trailingText);
                        _hasOpenLine = trailingText.Length > 0;
                    }
                    else if (cleanText.Length > 0)
                    {
                        _currentLineBytes += textBytes;
                        _hasOpenLine = true;
                    }

                    _tailOutput += cleanText;
                    var totalLines = _completedLines + (_hasOpenLine ? 1 : 0);
                    if ((_totalBytes > Truncate.DefaultMaxBytes || totalLines > Truncate.DefaultMaxLines) &&
                        !_fullOutputRequested)
                    {
                        EnsureFullOutputFile(_tailOutput);
                    }
                    else if (_fullOutputRequested)
                    {
                        AppendFullOutput(cleanText);
                    }

                    _tailOutput = TrimToLastUtf8Bytes(_tailOutput, _maxOutputBytes);
                    progressFactory = CreateProgress;
                }
                catch (Exception error)
                {
                    _captureError = ToExecutionError(error);
                }
            }

            if (progressFactory is not null && _options.OnChunk is not null)
            {
                try
                {
                    _options.OnChunk(cleanText, progressFactory);
                }
                catch (Exception error)
                {
                    lock (_gate)
                    {
                        _captureError = ToExecutionError(error);
                    }
                }
            }
        }

        public void StopAcceptingOutput()
        {
            lock (_gate)
            {
                _acceptingOutput = false;
            }
        }

        public void EnsureFullOutputFileIfTruncated()
        {
            lock (_gate)
            {
                var progress = CreateProgress();
                if (progress.Truncation.Truncated && !_fullOutputRequested)
                {
                    EnsureFullOutputFile(_tailOutput);
                }
            }
        }

        public ShellCaptureProgress CreateProgress()
        {
            lock (_gate)
            {
                var tailTruncation = Truncate.TruncateTail(_tailOutput);
                var totalLines = _completedLines + (_hasOpenLine ? 1 : 0);
                var truncated = totalLines > Truncate.DefaultMaxLines || _totalBytes > Truncate.DefaultMaxBytes;
                var truncation = tailTruncation with
                {
                    Truncated = truncated,
                    TruncatedBy = truncated
                        ? tailTruncation.TruncatedBy ?? (_totalBytes > Truncate.DefaultMaxBytes ? "bytes" : "lines")
                        : null,
                    TotalLines = totalLines,
                    TotalBytes = _totalBytes,
                };
                return new ShellCaptureProgress
                {
                    Output = truncated ? truncation.Content : _tailOutput,
                    Truncation = truncation,
                    FullOutputPath = _fullOutputPath,
                    LastLineBytes = _currentLineBytes,
                };
            }
        }

        public static ShellCaptureResult ToResult(
            ShellCaptureProgress progress,
            int? exitCode,
            bool cancelled,
            ExecutionError? executionError) => new()
            {
                Output = progress.Output,
                Truncation = progress.Truncation,
                FullOutputPath = progress.FullOutputPath,
                LastLineBytes = progress.LastLineBytes,
                ExitCode = exitCode,
                Cancelled = cancelled,
                Truncated = progress.Truncation.Truncated,
                ExecutionError = executionError,
            };

        private void EnsureFullOutputFile(string initialContent)
        {
            if (_fullOutputRequested || _captureError is not null)
            {
                return;
            }

            _fullOutputRequested = true;
            _writeChain = AppendAfterAsync(_writeChain, async () =>
            {
                var tempFile = await _env.CreateTempFileAsync(
                        new CreateTemporaryFileOptions { Prefix = "bash-", Suffix = ".log" })
                    .ConfigureAwait(false);
                if (!tempFile.Ok)
                {
                    return Result<bool, FileError>.Failure(tempFile.Error!);
                }

                _fullOutputPath = tempFile.Value;
                return await _env.AppendFileAsync(tempFile.Value!, initialContent).ConfigureAwait(false);
            });
        }

        private void AppendFullOutput(string text)
        {
            if (!_fullOutputRequested || _captureError is not null)
            {
                return;
            }

            _writeChain = AppendAfterAsync(_writeChain, async () =>
            {
                if (_fullOutputPath is null)
                {
                    return Result<bool, FileError>.Failure(
                        new FileError(FileErrorCodes.Unknown, "Full output path was not created"));
                }

                return await _env.AppendFileAsync(_fullOutputPath, text).ConfigureAwait(false);
            });
        }

        private static async Task<Result<bool, FileError>> AppendAfterAsync(
            Task<Result<bool, FileError>> previous,
            Func<Task<Result<bool, FileError>>> operation)
        {
            var prior = await previous.ConfigureAwait(false);
            if (!prior.Ok)
            {
                return prior;
            }

            return await operation().ConfigureAwait(false);
        }
    }
}
