using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;

namespace Pi.AgentCore.Harness;

/// <summary>Result of an operation that reports expected failures as values.</summary>
[SuppressMessage("Design", "CA1000", Justification = "Static success and failure factories preserve the upstream result contract.")]
public readonly record struct Result<TValue, TError>
{
    private Result(bool ok, TValue? value, TError? error)
    {
        Ok = ok;
        Value = value;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool Ok { get; }

    /// <summary>Successful value, when <see cref="Ok"/> is true.</summary>
    public TValue? Value { get; }

    /// <summary>Failure value, when <see cref="Ok"/> is false.</summary>
    public TError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static Result<TValue, TError> Success(TValue value) => new(true, value, default);

    /// <summary>Creates a failed result.</summary>
    public static Result<TValue, TError> Failure(TError error) => new(false, default, error);
}

/// <summary>Factory and discriminator helpers for <see cref="Result{TValue,TError}"/>.</summary>
public static partial class Result
{
    /// <summary>Creates a successful result.</summary>
    public static Result<TValue, TError> Ok<TValue, TError>(TValue value) => Result<TValue, TError>.Success(value);

    /// <summary>Creates a failed result.</summary>
    public static Result<TValue, TError> Err<TValue, TError>(TError error) => Result<TValue, TError>.Failure(error);

    /// <summary>Returns whether a result is successful.</summary>
    public static bool IsOk<TValue, TError>(Result<TValue, TError> result) => result.Ok;

    /// <summary>Returns whether a result is failed.</summary>
    public static bool IsErr<TValue, TError>(Result<TValue, TError> result) => !result.Ok;
}

/// <summary>Helpers for consuming a fallible harness result.</summary>
public static class ResultHelpers
{
    /// <summary>Returns the value or throws the contained exception.</summary>
    public static TValue GetOrThrow<TValue, TError>(Result<TValue, TError> result)
    {
        if (result.Ok)
        {
            return result.Value!;
        }

        if (result.Error is Exception exception)
        {
            throw exception;
        }

        throw new InvalidOperationException(result.Error?.ToString() ?? "Result failed without an error value.");
    }

    /// <summary>Returns the success value or null.</summary>
    public static TValue? GetOrNull<TValue, TError>(Result<TValue, TError> result) where TValue : class =>
        result.Ok ? result.Value : null;

    /// <summary>Normalizes an arbitrary thrown value to an exception.</summary>
    public static Exception ToError(object? error) => error switch
    {
        Exception exception => exception,
        string text => new InvalidOperationException(text),
        _ => new InvalidOperationException(error?.ToString() ?? "null"),
    };
}

/// <summary>Skill content loaded from a <c>SKILL.md</c> file or supplied by an application.</summary>
public sealed record Skill
{
    /// <summary>Stable model-visible skill name.</summary>
    public required string Name { get; init; }

    /// <summary>Short model-visible description.</summary>
    public required string Description { get; init; }

    /// <summary>Full skill instructions.</summary>
    public required string Content { get; init; }

    /// <summary>Absolute skill-file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Whether explicit invocation remains available while model invocation is hidden.</summary>
    public bool DisableModelInvocation { get; init; }
}

/// <summary>Prompt template available for explicit invocation.</summary>
public sealed record PromptTemplate
{
    /// <summary>Stable template name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description shown in command lists.</summary>
    public string? Description { get; init; }

    /// <summary>Template content.</summary>
    public required string Content { get; init; }
}

/// <summary>Resources made available to harness invocation and prompt construction.</summary>
public sealed record AgentHarnessResources
{
    /// <summary>Prompt templates available for explicit invocation.</summary>
    public IReadOnlyList<PromptTemplate> PromptTemplates { get; init; } = [];

    /// <summary>Skills available to the model and explicit invocation.</summary>
    public IReadOnlyList<Skill> Skills { get; init; } = [];
}

/// <summary>Generic resources container for applications with derived skill or template types.</summary>
public sealed record AgentHarnessResources<TSkill, TPromptTemplate>
{
    /// <summary>Prompt templates available for explicit invocation.</summary>
    public IReadOnlyList<TPromptTemplate> PromptTemplates { get; init; } = [];

    /// <summary>Skills available to the model and explicit invocation.</summary>
    public IReadOnlyList<TSkill> Skills { get; init; } = [];
}

/// <summary>Provider request options owned by the harness and snapshotted per turn.</summary>
public sealed record AgentHarnessStreamOptions
{
    /// <summary>Preferred provider transport.</summary>
    public string? Transport { get; init; }

    /// <summary>Provider request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Maximum provider retry attempts.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>Optional cap for provider retry delays.</summary>
    public int? MaxRetryDelayMs { get; init; }

    /// <summary>Additional request headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Provider metadata.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Metadata { get; init; }

    /// <summary>Provider cache-retention hint.</summary>
    public string? CacheRetention { get; init; }
}

/// <summary>Partial stream-option patch returned by a provider hook.</summary>
public sealed record AgentHarnessStreamOptionsPatch
{
    /// <summary>Transport replacement.</summary>
    public string? Transport { get; init; }

    /// <summary>Timeout replacement.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Retry-count replacement.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>Retry-delay replacement.</summary>
    public int? MaxRetryDelayMs { get; init; }

    /// <summary>Header patch; null values delete keys.</summary>
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    /// <summary>Metadata patch; null values delete keys.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Metadata { get; init; }

    /// <summary>Cache-retention replacement.</summary>
    public string? CacheRetention { get; init; }
}

/// <summary>Filesystem object kind used by harness capabilities.</summary>
public static class FileKinds
{
    /// <summary>Regular file.</summary>
    public const string File = "file";

    /// <summary>Directory.</summary>
    public const string Directory = "directory";

    /// <summary>Symbolic link.</summary>
    public const string Symlink = "symlink";
}

/// <summary>Stable filesystem failure codes.</summary>
public static class FileErrorCodes
{
    /// <summary>Operation was aborted.</summary>
    public const string Aborted = "aborted";

    /// <summary>Path was not found.</summary>
    public const string NotFound = "not_found";

    /// <summary>Permission was denied.</summary>
    public const string PermissionDenied = "permission_denied";

    /// <summary>Expected a directory.</summary>
    public const string NotDirectory = "not_directory";

    /// <summary>Expected a file.</summary>
    public const string IsDirectory = "is_directory";

    /// <summary>Path or argument was invalid.</summary>
    public const string Invalid = "invalid";

    /// <summary>Operation is unsupported.</summary>
    public const string NotSupported = "not_supported";

    /// <summary>Unknown filesystem failure.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Failure returned by a filesystem capability.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name is the upstream public filesystem error contract.")]
public sealed class FileError : Exception
{
    /// <summary>Stable backend-independent error code.</summary>
    public string Code { get; }

    /// <summary>Addressed path associated with the failure.</summary>
    public string? Path { get; }

    /// <summary>Creates a filesystem failure.</summary>
    public FileError(string code, string message, string? path = null, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
        Path = path;
    }
}

/// <summary>Stable execution failure codes.</summary>
public static class ExecutionErrorCodes
{
    /// <summary>Operation was aborted.</summary>
    public const string Aborted = "aborted";

    /// <summary>Command exceeded its timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>No shell executable was available.</summary>
    public const string ShellUnavailable = "shell_unavailable";

    /// <summary>Process creation failed.</summary>
    public const string SpawnError = "spawn_error";

    /// <summary>Output callback failed.</summary>
    public const string CallbackError = "callback_error";

    /// <summary>Unknown execution failure.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Failure returned by a shell execution capability.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name is the upstream public execution error contract.")]
public sealed class ExecutionError : Exception
{
    /// <summary>Stable backend-independent error code.</summary>
    public string Code { get; }

    /// <summary>Creates an execution failure.</summary>
    public ExecutionError(string code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}

/// <summary>Stable compaction failure codes.</summary>
public static class CompactionErrorCodes
{
    /// <summary>Operation was aborted.</summary>
    public const string Aborted = "aborted";

    /// <summary>Summary generation failed.</summary>
    public const string SummarizationFailed = "summarization_failed";
}

/// <summary>Failure returned by compaction helpers.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name is the upstream public compaction error contract.")]
public sealed class CompactionError : Exception
{
    /// <summary>Stable error code.</summary>
    public string Code { get; }

    /// <summary>Creates a compaction failure.</summary>
    public CompactionError(string code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}

/// <summary>Stable branch-summary failure codes.</summary>
public static class BranchSummaryErrorCodes
{
    /// <summary>Operation was aborted.</summary>
    public const string Aborted = "aborted";

    /// <summary>Summary generation failed.</summary>
    public const string SummarizationFailed = "summarization_failed";
}

/// <summary>Failure returned by branch summarization helpers.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "The name is the upstream public branch-summary error contract.")]
public sealed class BranchSummaryError : Exception
{
    /// <summary>Stable error code.</summary>
    public string Code { get; }

    /// <summary>Creates a branch-summary failure.</summary>
    public BranchSummaryError(string code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}

/// <summary>Metadata for an addressed filesystem object.</summary>
public sealed record FileInfo
{
    /// <summary>Basename.</summary>
    public required string Name { get; init; }

    /// <summary>Addressed path.</summary>
    public required string Path { get; init; }

    /// <summary>Object kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Modification time in Unix milliseconds.</summary>
    public double MtimeMs { get; init; }
}

/// <summary>Options for reading text lines.</summary>
public sealed record ReadTextLinesOptions
{
    /// <summary>Maximum lines to return.</summary>
    public int? MaxLines { get; init; }
}

/// <summary>Options for directory creation.</summary>
public sealed record CreateDirectoryOptions
{
    /// <summary>Whether parent directories are created.</summary>
    public bool Recursive { get; init; } = true;
}

/// <summary>Options for removing a filesystem object.</summary>
public sealed record RemoveOptions
{
    /// <summary>Whether directories are removed recursively.</summary>
    public bool Recursive { get; init; }

    /// <summary>Whether missing paths are ignored.</summary>
    public bool Force { get; init; }
}

/// <summary>Options for creating a temporary file.</summary>
public sealed record CreateTemporaryFileOptions
{
    /// <summary>Filename prefix.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Filename suffix.</summary>
    public string Suffix { get; init; } = string.Empty;
}

/// <summary>Filesystem capability used by the harness.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "The name preserves the upstream filesystem capability contract.")]
public interface FileSystem
{
    /// <summary>Current working directory for relative paths.</summary>
    string Cwd { get; }

    /// <summary>Returns an addressed absolute path.</summary>
    Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Joins path segments in the filesystem namespace.</summary>
    Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default);

    /// <summary>Reads UTF-8 text.</summary>
    Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads UTF-8 lines.</summary>
    Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(
        string path,
        ReadTextLinesOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads binary content.</summary>
    Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates or overwrites a file.</summary>
    Task<Result<bool, FileError>> WriteFileAsync(string path, object content, CancellationToken cancellationToken = default);

    /// <summary>Creates or appends to a file.</summary>
    Task<Result<bool, FileError>> AppendFileAsync(string path, object content, CancellationToken cancellationToken = default);

    /// <summary>Atomically renames a file.</summary>
    Task<Result<bool, FileError>> RenameFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>Returns metadata without following symlinks.</summary>
    Task<Result<FileInfo, FileError>> FileInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists direct directory children without following symlinks.</summary>
    Task<Result<IReadOnlyList<FileInfo>, FileError>> ListDirAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Resolves an existing path canonically.</summary>
    Task<Result<string, FileError>> CanonicalPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Checks existence, returning errors other than missing.</summary>
    Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates a directory.</summary>
    Task<Result<bool, FileError>> CreateDirAsync(
        string path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a file or directory.</summary>
    Task<Result<bool, FileError>> RemoveAsync(
        string path,
        RemoveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a temporary directory.</summary>
    Task<Result<string, FileError>> CreateTempDirAsync(
        string prefix = "tmp-",
        CancellationToken cancellationToken = default);

    /// <summary>Creates a temporary file.</summary>
    Task<Result<string, FileError>> CreateTempFileAsync(
        CreateTemporaryFileOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Releases filesystem resources on a best-effort basis.</summary>
    Task CleanupAsync();
}

/// <summary>Options for shell execution.</summary>
public sealed record ShellExecOptions
{
    /// <summary>Working directory override.</summary>
    public string? Cwd { get; init; }

    /// <summary>Environment overrides.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Whether inherited environment variables are used.</summary>
    public bool InheritEnvironment { get; init; } = true;

    /// <summary>Timeout in seconds.</summary>
    public double? Timeout { get; init; }

    /// <summary>Cancellation token used to terminate the command.</summary>
    public CancellationToken AbortSignal { get; init; }

    /// <summary>Receives stdout chunks.</summary>
    public Action<string>? OnStdout { get; init; }

    /// <summary>Receives stderr chunks.</summary>
    public Action<string>? OnStderr { get; init; }
}

/// <summary>Shell command result.</summary>
public sealed record ShellExecResult
{
    /// <summary>Captured standard output.</summary>
    public required string Stdout { get; init; }

    /// <summary>Captured standard error.</summary>
    public required string Stderr { get; init; }

    /// <summary>Process exit code.</summary>
    public int ExitCode { get; init; }
}

/// <summary>Shell execution capability used by the harness.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "The name preserves the upstream shell capability contract.")]
public interface Shell
{
    /// <summary>Executes a shell command.</summary>
    Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
        string command,
        ShellExecOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Releases shell resources on a best-effort basis.</summary>
    Task CleanupAsync();
}

/// <summary>Combined filesystem and process execution environment.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "The name preserves the upstream execution environment contract.")]
public interface ExecutionEnv : FileSystem, Shell
{
}

/// <summary>Delegate used to execute a harness tool with resolved context.</summary>
public delegate Task<AgentToolResult> AgentHarnessToolExecutor<TContext>(
    string toolCallId,
    JsonNode parameters,
    CancellationToken cancellationToken,
    Action<AgentToolResult>? onUpdate,
    TContext? context) where TContext : class;

/// <summary>Tool definition executed by an application-defined harness context.</summary>
public sealed record AgentHarnessTool<TContext> where TContext : class
{
    /// <summary>Stable tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable label.</summary>
    public required string Label { get; init; }

    /// <summary>Model-visible description.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema for parameters.</summary>
    public JsonNode Parameters { get; init; } = new JsonObject();

    /// <summary>Tool execution callback.</summary>
    public required AgentHarnessToolExecutor<TContext> Execute { get; init; }
}

/// <summary>Static context or per-turn context provider.</summary>
public delegate ValueTask<TContext?> AgentHarnessToolContextSource<TContext>() where TContext : class;

/// <summary>Tagged error value used for exhaustive error matching.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "This is a tagged value object represented as an exception for C# result interop.")]
public class TaggedErrorValue : Exception
{
    private readonly IReadOnlyDictionary<string, object?> _properties;

    /// <summary>Stable error tag.</summary>
    public string Tag { get; }

    /// <summary>Additional error properties.</summary>
    public IReadOnlyDictionary<string, object?> Properties => _properties;

    /// <summary>Creates a tagged error.</summary>
    public TaggedErrorValue(string tag, string message, IReadOnlyDictionary<string, object?>? properties = null)
        : base(message)
    {
        Tag = tag;
        _properties = properties ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>Returns a JSON-compatible representation.</summary>
    public JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["_tag"] = Tag,
            ["message"] = Message,
        };
        foreach (var property in _properties)
        {
            result[property.Key] = JsonValue.Create(property.Value?.ToString());
        }

        return result;
    }
}

/// <summary>Factory and matching helpers for tagged errors.</summary>
public static class TaggedError
{
    /// <summary>Creates a tagged error.</summary>
    public static TaggedErrorValue Create(
        string tag,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        new(tag, message, properties);

    /// <summary>Tests whether a value has a requested tag.</summary>
    public static bool Is(object? value, string tag) => value is TaggedErrorValue error && error.Tag == tag;

    /// <summary>Dispatches a tagged error to its matching callback.</summary>
    public static TValue Match<TValue>(
        TaggedErrorValue error,
        IReadOnlyDictionary<string, Func<TaggedErrorValue, TValue>> matchers) =>
        matchers[error.Tag](error);
}
