using System.Globalization;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Utils;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Details returned by the built-in bash tool.</summary>
public sealed record BashToolDetails(TruncationResult? Truncation = null, string? FullOutputPath = null);

/// <summary>Mutable command description passed to a bash preparation callback.</summary>
public sealed class BashExecution
{
    /// <summary>Command that will be executed.</summary>
    public required string Command { get; set; }

    /// <summary>Working directory used for execution.</summary>
    public required string Cwd { get; set; }

    /// <summary>Explicit environment overrides.</summary>
    public IDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the process inherits the environment configured on the execution environment.</summary>
    public bool InheritEnvironment { get; set; } = true;
}

/// <summary>Callback that can change the command before it is executed.</summary>
public delegate Task BashPrepare<TContext>(
    BashExecution execution,
    TContext context,
    CancellationToken cancellationToken) where TContext : ExecutionToolContext;

/// <summary>Options for creating a bash tool.</summary>
public sealed class BashToolOptions<TContext>
    where TContext : ExecutionToolContext
{
    /// <summary>Command prefix inserted before every command.</summary>
    public string? CommandPrefix { get; init; }

    /// <summary>Optional callback that customizes the execution descriptor.</summary>
    public BashPrepare<TContext>? Prepare { get; init; }
}

/// <summary>Factory for the built-in bash tool.</summary>
public static class BashTool
{
    private const double _maxTimeoutSeconds = 2_147_483_647d / 1000d;
    private const int _bashUpdateThrottleMilliseconds = 100;

    /// <summary>Creates a bash tool using the standard execution context.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateBashTool(
        BashToolOptions<ExecutionToolContext>? options = null) =>
        CreateBashTool<ExecutionToolContext>(options);

    /// <summary>Creates a bash tool using a derived execution context.</summary>
    public static AgentHarnessTool<TContext> CreateBashTool<TContext>(
        BashToolOptions<TContext>? options = null)
        where TContext : ExecutionToolContext => new()
        {
            Name = "bash",
            Label = "bash",
            Description = $"Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.",
            Parameters = ToolHelpers.Schema(
                ("command", "string", "Bash command to execute", true),
                ("timeout", "number", "Timeout in seconds (optional, no default timeout)", false)),
            Execute = async (toolCallId, parameters, signal, onUpdate, context) =>
            {
                _ = toolCallId;
                ArgumentNullException.ThrowIfNull(context);
                var input = ToolHelpers.RequireObject(parameters);
                var command = ToolHelpers.RequireString(input, "command");
                var timeout = ToolHelpers.OptionalNumber(input, "timeout");
                ValidateTimeout(timeout);
                var execution = new BashExecution
                {
                    Command = string.IsNullOrEmpty(options?.CommandPrefix)
                        ? command
                        : $"{options.CommandPrefix}\n{command}",
                    Cwd = context.Env.Cwd,
                };
                if (options?.Prepare is not null)
                {
                    await options.Prepare(execution, context, signal).ConfigureAwait(false);
                }

                var updateState = new UpdateState(onUpdate);
                onUpdate?.Invoke(new AgentToolResult { Content = [], Details = null });
                try
                {
                    var capture = await ShellOutput.ExecuteShellWithCaptureAsync(
                            context.Env,
                            execution.Command,
                            new ShellCaptureOptions
                            {
                                Cwd = execution.Cwd,
                                Environment = new Dictionary<string, string>(execution.Environment, StringComparer.Ordinal),
                                InheritEnvironment = execution.InheritEnvironment,
                                Timeout = timeout,
                                AbortSignal = signal,
                                ReturnExecutionErrors = true,
                                OnChunk = updateState.OnChunk,
                            },
                            signal)
                        .ConfigureAwait(false);
                    var result = Result.GetOrThrow(capture);
                    updateState.ClearTimer();
                    updateState.SetLatestProgress(() => new ShellCaptureProgress
                    {
                        Output = result.Output,
                        Truncation = result.Truncation,
                        FullOutputPath = result.FullOutputPath,
                        LastLineBytes = result.LastLineBytes,
                    });
                    updateState.MarkDirtyAndEmit();

                    var outputText = result.Output;
                    JsonObject? details = null;
                    if (result.Truncation.Truncated)
                    {
                        details = ToolHelpers.BashDetails(result.Truncation, result.FullOutputPath);
                        var startLine = result.Truncation.TotalLines - result.Truncation.OutputLines + 1;
                        var endLine = result.Truncation.TotalLines;
                        if (result.Truncation.LastLinePartial)
                        {
                            outputText += $"\n\n[Showing last {Truncate.FormatSize(result.Truncation.OutputBytes)} of line {endLine} (line is {Truncate.FormatSize(result.LastLineBytes)}). Full output: {result.FullOutputPath}]";
                        }
                        else if (result.Truncation.TruncatedBy == "lines")
                        {
                            outputText += $"\n\n[Showing lines {startLine}-{endLine} of {result.Truncation.TotalLines}. Full output: {result.FullOutputPath}]";
                        }
                        else
                        {
                            outputText += $"\n\n[Showing lines {startLine}-{endLine} of {result.Truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {result.FullOutputPath}]";
                        }
                    }

                    var statusPrefix = string.IsNullOrEmpty(outputText) ? string.Empty : $"{outputText}\n\n";
                    if (result.Cancelled)
                    {
                        throw new InvalidOperationException($"{statusPrefix}Command aborted");
                    }

                    if (result.ExecutionError?.Code == ExecutionErrorCodes.Timeout)
                    {
                        throw new InvalidOperationException(
                            $"{statusPrefix}Command timed out after {ToolHelpers.NumberString(timeout!.Value)} seconds",
                            result.ExecutionError);
                    }

                    if (result.ExecutionError is not null)
                    {
                        throw result.ExecutionError;
                    }

                    if (result.ExitCode is not null && result.ExitCode.Value != 0)
                    {
                        throw new InvalidOperationException($"{statusPrefix}Command exited with code {result.ExitCode.Value}");
                    }

                    return ToolHelpers.TextResult(
                        string.IsNullOrEmpty(outputText) ? "(no output)" : outputText,
                        details);
                }
                finally
                {
                    updateState.ClearTimer();
                }
            },
        };

    private static void ValidateTimeout(double? timeout)
    {
        if (timeout is null)
        {
            return;
        }

        if (!double.IsFinite(timeout.Value) || timeout.Value <= 0)
        {
            throw new ArgumentException("Invalid timeout: must be a finite number of seconds.", nameof(timeout));
        }

        if (timeout.Value > _maxTimeoutSeconds)
        {
            throw new ArgumentException(
                $"Invalid timeout: maximum is {_maxTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds",
                nameof(timeout));
        }
    }

    private sealed class UpdateState(Action<AgentToolResult>? onUpdate)
    {
        private readonly object _gate = new();
        private readonly Action<AgentToolResult>? _onUpdate = onUpdate;
        private Func<ShellCaptureProgress>? _latestProgress;
        private Timer? _timer;
        private bool _dirty;
        private long _lastUpdateTicks;

        public Action<string, Func<ShellCaptureProgress>>? OnChunk => HandleChunk;

        public void SetLatestProgress(Func<ShellCaptureProgress> progress)
        {
            lock (_gate)
            {
                _latestProgress = progress;
            }
        }

        public void MarkDirtyAndEmit()
        {
            Func<ShellCaptureProgress>? progress;
            lock (_gate)
            {
                _dirty = true;
                progress = _latestProgress;
            }

            if (progress is not null)
            {
                EmitOutputUpdate();
            }
        }

        public void ClearTimer()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void HandleChunk(string _, Func<ShellCaptureProgress> progress)
        {
            SetLatestProgress(progress);
            lock (_gate)
            {
                _dirty = true;
                if (_onUpdate is null)
                {
                    return;
                }

                var elapsed = _lastUpdateTicks == 0
                    ? long.MaxValue
                    : (DateTime.UtcNow.Ticks - _lastUpdateTicks) / TimeSpan.TicksPerMillisecond;
                var delay = _bashUpdateThrottleMilliseconds - elapsed;
                if (delay <= 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
                else
                {
                    _timer ??= new Timer(static state => ((UpdateState)state!).EmitOutputUpdate(), this, delay, Timeout.Infinite);
                    return;
                }
            }

            EmitOutputUpdate();
        }

        private void EmitOutputUpdate()
        {
            Action<AgentToolResult>? callback;
            ShellCaptureProgress? progress;
            lock (_gate)
            {
                if (!_dirty || _onUpdate is null || _latestProgress is null)
                {
                    return;
                }

                _dirty = false;
                _lastUpdateTicks = DateTime.UtcNow.Ticks;
                callback = _onUpdate;
                progress = _latestProgress();
            }

            var details = progress.Truncation.Truncated
                ? ToolHelpers.BashDetails(progress.Truncation, progress.FullOutputPath)
                : null;
            callback(new AgentToolResult
            {
                Content = [new Pi.Ai.TextContent(progress.Output)],
                Details = details,
            });
        }
    }
}
