using System.Diagnostics.CodeAnalysis;
using System.Text;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Env;
using Pi.AgentCore.Harness.Utils;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

[SuppressMessage("Usage", "xUnit1051", Justification = "Harness tests pass the cancellation token through the execution environment APIs.")]
public sealed class SystemExecutionEnvTests
{
    [Fact(DisplayName = "reads, writes, lists, and removes files and directories")]
    public async Task Reads_writes_lists_and_removes_files_and_directories()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);

        Assert.Equal(
            Path.Combine(temp.Path, "nested", "child"),
            Result.GetOrThrow(await env.AbsolutePathAsync("nested/child")));
        Assert.Equal(
            Path.Combine(temp.Path, "nested", "child"),
            Result.GetOrThrow(await env.JoinPathAsync([temp.Path, "nested", "child"])));
        Result.GetOrThrow(await env.CreateDirAsync("nested/child"));
        Result.GetOrThrow(await env.WriteFileAsync("nested/child/file.txt", "hel"));
        Result.GetOrThrow(await env.AppendFileAsync("nested/child/file.txt", "lo"));

        Assert.Equal("hello", Result.GetOrThrow(await env.ReadTextFileAsync("nested/child/file.txt")));
        Assert.Equal(
            ["hello"],
            Result.GetOrThrow(await env.ReadTextLinesAsync("nested/child/file.txt", new ReadTextLinesOptions { MaxLines = 1 })));
        Assert.Equal(
            "hello",
            Encoding.UTF8.GetString(Result.GetOrThrow(await env.ReadBinaryFileAsync("nested/child/file.txt"))));

        var entries = Result.GetOrThrow(await env.ListDirAsync("nested/child"));
        var entry = Assert.Single(entries);
        Assert.Equal("file.txt", entry.Name);
        Assert.Equal(Path.Combine(temp.Path, "nested", "child", "file.txt"), entry.Path);
        Assert.Equal(FileKinds.File, entry.Kind);
        Assert.Equal(5, entry.Size);
        Assert.True(entry.MtimeMs > 0);

        Assert.True(Result.GetOrThrow(await env.ExistsAsync("nested/child/file.txt")));
        Result.GetOrThrow(await env.RemoveAsync("nested/child/file.txt"));
        Assert.False(Result.GetOrThrow(await env.ExistsAsync("nested/child/file.txt")));
    }

    [Fact(DisplayName = "expands home-relative paths and file URLs")]
    public async Task Expands_home_relative_paths_and_file_urls()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(
            Path.Combine(home, "pi-node-env-test"),
            Result.GetOrThrow(await env.AbsolutePathAsync("~/pi-node-env-test")));
        var filePath = Path.Combine(temp.Path, "file with spaces.txt");
        Assert.Equal(filePath, Result.GetOrThrow(await env.AbsolutePathAsync(new Uri(filePath).AbsoluteUri)));
    }

    [Fact(DisplayName = "returns fileInfo for files, directories, and symlinks without following symlinks")]
    public async Task Returns_file_info_for_files_directories_and_symlinks_without_following_symlinks()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.CreateDirAsync("dir", new CreateDirectoryOptions { Recursive = true }));
        Result.GetOrThrow(await env.WriteFileAsync("dir/file.txt", "hello"));
        if (!H4TestSupport.TryCreateSymbolicLink(Path.Combine(temp.Path, "file-link"), Path.Combine(temp.Path, "dir", "file.txt")) ||
            !H4TestSupport.TryCreateSymbolicLink(Path.Combine(temp.Path, "dir-link"), Path.Combine(temp.Path, "dir")))
        {
            return;
        }

        var directory = Result.GetOrThrow(await env.FileInfoAsync("dir"));
        Assert.Equal("dir", directory.Name);
        Assert.Equal(Path.Combine(temp.Path, "dir"), directory.Path);
        Assert.Equal(FileKinds.Directory, directory.Kind);

        var file = Result.GetOrThrow(await env.FileInfoAsync("dir/file.txt"));
        Assert.Equal("file.txt", file.Name);
        Assert.Equal(Path.Combine(temp.Path, "dir", "file.txt"), file.Path);
        Assert.Equal(FileKinds.File, file.Kind);
        Assert.Equal(5, file.Size);

        var fileLink = Result.GetOrThrow(await env.FileInfoAsync("file-link"));
        Assert.Equal("file-link", fileLink.Name);
        Assert.Equal(Path.Combine(temp.Path, "file-link"), fileLink.Path);
        Assert.Equal(FileKinds.Symlink, fileLink.Kind);

        var directoryLink = Result.GetOrThrow(await env.FileInfoAsync("dir-link"));
        Assert.Equal("dir-link", directoryLink.Name);
        Assert.Equal(Path.Combine(temp.Path, "dir-link"), directoryLink.Path);
        Assert.Equal(FileKinds.Symlink, directoryLink.Kind);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp.Path, "dir", "file.txt")),
            Result.GetOrThrow(await env.CanonicalPathAsync("file-link")));
    }

    [Fact(DisplayName = "lists symlinks as symlinks")]
    public async Task Lists_symlinks_as_symlinks()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync("target.txt", "hello"));
        if (!H4TestSupport.TryCreateSymbolicLink(Path.Combine(temp.Path, "link.txt"), Path.Combine(temp.Path, "target.txt")))
        {
            return;
        }

        var entries = Result.GetOrThrow(await env.ListDirAsync("."))
            .Select(static entry => new { entry.Name, entry.Kind })
            .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                new { Name = "link.txt", Kind = FileKinds.Symlink },
                new { Name = "target.txt", Kind = FileKinds.File },
            ],
            entries);
    }

    [Fact(DisplayName = "stops reading text lines at the requested limit")]
    public async Task Stops_reading_text_lines_at_the_requested_limit()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync("file.txt", "one\ntwo\nthree"));

        Assert.Equal(
            ["one"],
            Result.GetOrThrow(await env.ReadTextLinesAsync("file.txt", new ReadTextLinesOptions { MaxLines = 1 })));
    }

    [Fact(DisplayName = "returns FileError for missing paths and keeps exists false for missing paths")]
    public async Task Returns_file_error_for_missing_paths_and_keeps_exists_false_for_missing_paths()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var info = await env.FileInfoAsync("missing.txt");

        Assert.False(info.Ok);
        Assert.NotNull(info.Error);
        Assert.Equal(FileErrorCodes.NotFound, info.Error!.Code);
        Assert.Equal(Path.Combine(temp.Path, "missing.txt"), info.Error.Path);
        Assert.False(Result.GetOrThrow(await env.ExistsAsync("missing.txt")));
    }

    [Fact(DisplayName = "returns FileError for listing non-directories")]
    public async Task Returns_file_error_for_listing_non_directories()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync("file.txt", "hello"));

        var result = await env.ListDirAsync("file.txt");
        Assert.False(result.Ok);
        Assert.Equal(FileErrorCodes.NotDirectory, result.Error!.Code);
    }

    [Fact(DisplayName = "appends to new files and creates parent directories")]
    public async Task Appends_to_new_files_and_creates_parent_directories()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.AppendFileAsync("new/nested/file.txt", "a"));
        Result.GetOrThrow(await env.AppendFileAsync("new/nested/file.txt", "b"));

        Assert.Equal("ab", Result.GetOrThrow(await env.ReadTextFileAsync("new/nested/file.txt")));
    }

    [Fact(DisplayName = "atomically renames a file and replaces the destination")]
    public async Task Atomically_renames_a_file_and_replaces_the_destination()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync("source.txt", "new"));
        Result.GetOrThrow(await env.WriteFileAsync("destination.txt", "old"));
        Result.GetOrThrow(await env.RenameFileAsync("source.txt", "destination.txt"));

        Assert.False(Result.GetOrThrow(await env.ExistsAsync("source.txt")));
        Assert.Equal("new", Result.GetOrThrow(await env.ReadTextFileAsync("destination.txt")));
    }

    [Fact(DisplayName = "reports the source path when rename fails because the source is missing")]
    public async Task Reports_the_source_path_when_rename_fails_because_the_source_is_missing()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync("destination.txt", "unchanged"));

        var result = await env.RenameFileAsync("missing-source.txt", "destination.txt");
        Assert.False(result.Ok);
        Assert.Equal(FileErrorCodes.NotFound, result.Error!.Code);
        Assert.Equal(Path.Combine(temp.Path, "missing-source.txt"), result.Error.Path);
        Assert.Equal("unchanged", Result.GetOrThrow(await env.ReadTextFileAsync("destination.txt")));
    }

    [Fact(DisplayName = "creates temporary directories and files")]
    public async Task Creates_temporary_directories_and_files()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);

        var tempDirectory = Result.GetOrThrow(await env.CreateTempDirAsync("node-env-test-"));
        Assert.True(Directory.Exists(tempDirectory));
        var tempFile = Result.GetOrThrow(await env.CreateTempFileAsync(
            new CreateTemporaryFileOptions { Prefix = "prefix-", Suffix = ".txt" }));
        Assert.True(File.Exists(tempFile));
        Assert.EndsWith(".txt", tempFile, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "honors createDir recursive false and remove recursive/force options")]
    public async Task Honors_create_dir_recursive_false_and_remove_recursive_force_options()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var createResult = await env.CreateDirAsync("missing/child", new CreateDirectoryOptions { Recursive = false });
        Assert.False(createResult.Ok);
        Assert.Equal(FileErrorCodes.NotFound, createResult.Error!.Code);

        Result.GetOrThrow(await env.WriteFileAsync("dir/child/file.txt", "hello"));
        var removeDirectory = await env.RemoveAsync("dir", new RemoveOptions { Recursive = false });
        Assert.False(removeDirectory.Ok);
        Result.GetOrThrow(await env.RemoveAsync("dir", new RemoveOptions { Recursive = true }));
        Assert.False(Result.GetOrThrow(await env.ExistsAsync("dir")));

        var removeMissing = await env.RemoveAsync("missing", new RemoveOptions { Force = false });
        Assert.False(removeMissing.Ok);
        Result.GetOrThrow(await env.RemoveAsync("missing", new RemoveOptions { Force = true }));
    }

    [Fact(DisplayName = "returns aborted results for pre-aborted cancellable file operations")]
    public async Task Returns_aborted_results_for_pre_aborted_cancellable_file_operations()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync("file.txt", "hello"));
        using var controller = new CancellationTokenSource();
        controller.Cancel();
        var signal = controller.Token;

        AssertAborted(await env.ReadTextFileAsync("file.txt", signal));
        AssertAborted(await env.ReadTextLinesAsync("file.txt", new ReadTextLinesOptions(), signal));
        AssertAborted(await env.ReadBinaryFileAsync("file.txt", signal));
        AssertAborted(await env.WriteFileAsync("other.txt", "hello", signal));
        AssertAborted(await env.RenameFileAsync("file.txt", "renamed.txt", signal));
        AssertAborted(await env.ListDirAsync(".", signal));
    }

    [Fact(DisplayName = "cleanup is best-effort")]
    public async Task Cleanup_is_best_effort()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);

        await env.CleanupAsync();
    }

    [Fact(DisplayName = "executes commands in cwd with env overrides")]
    public async Task Executes_commands_in_cwd_with_env_overrides()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var result = Result.GetOrThrow(await env.ExecAsync(
            "printf '%s:%s' \"$PWD\" \"$NODE_ENV_TEST\"",
            new ShellExecOptions { Environment = new Dictionary<string, string> { ["NODE_ENV_TEST"] = "ok" } }));

        Assert.EndsWith(":ok", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(result.Stderr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact(DisplayName = "applies string shell environment overrides when a missing override preserves the base value")]
    public async Task Applies_string_shell_environment_overrides_when_a_missing_override_preserves_the_base_value()
    {
        await AssertShellEnvironmentOverrideAsync(null, "x:/stale/parent.jsonl");
    }

    [Fact(DisplayName = "applies string shell environment overrides when an empty override shadows the base value")]
    public async Task Applies_string_shell_environment_overrides_when_an_empty_override_shadows_the_base_value()
    {
        await AssertShellEnvironmentOverrideAsync(new Dictionary<string, string> { ["PI_SESSION_FILE"] = "" }, "x:");
    }

    [Fact(DisplayName = "applies string shell environment overrides when a string override replaces the base value")]
    public async Task Applies_string_shell_environment_overrides_when_a_string_override_replaces_the_base_value()
    {
        await AssertShellEnvironmentOverrideAsync(
            new Dictionary<string, string> { ["PI_SESSION_FILE"] = "/sessions/current.jsonl" },
            "x:/sessions/current.jsonl");
    }

    [Fact(DisplayName = "can replace rather than inherit the default shell environment")]
    public async Task Can_replace_rather_than_inherit_the_default_shell_environment()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        const string inheritedKey = "PI_NODE_ENV_INHERITED_TEST";
        const string configuredKey = "PI_NODE_ENV_CONFIGURED_TEST";
        const string explicitKey = "PI_NODE_ENV_EXPLICIT_TEST";
        var previousInherited = Environment.GetEnvironmentVariable(inheritedKey);
        Environment.SetEnvironmentVariable(inheritedKey, "host");
        try
        {
            var env = new SystemExecutionEnv(
                temp.Path,
                shellEnvironment: new Dictionary<string, string> { [configuredKey] = "configured" });
            var result = Result.GetOrThrow(await env.ExecAsync(
                $"printf '%s:%s:%s' \"${{{inheritedKey}-}}\" \"${{{configuredKey}-}}\" \"${{{explicitKey}-}}\"",
                new ShellExecOptions
                {
                    InheritEnvironment = false,
                    Environment = new Dictionary<string, string> { [explicitKey] = "explicit" },
                }));

            Assert.Equal("::explicit", result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedKey, previousInherited);
        }
    }

    [Fact(DisplayName = "uses stdin command transport for legacy WSL bash paths")]
    public async Task Uses_stdin_command_transport_for_legacy_wsl_bash_paths()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string shellPath = "C:\\Windows\\System32\\bash.exe";
        var physicalShellPath = Path.Combine(Directory.GetCurrentDirectory(), shellPath);
        try
        {
            await File.WriteAllTextAsync(
                physicalShellPath,
                "#!/bin/sh\nprintf 'args:%s\\n' \"$*\" >&2\nexec /bin/bash \"$@\"\n");
            File.SetUnixFileMode(
                physicalShellPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var temp = H4TestSupport.CreateTempDirectory();
            var env = new SystemExecutionEnv(temp.Path, shellPath);
            var result = Result.GetOrThrow(await env.ExecAsync("name='World'; echo \"Hello, $name!\""));

            Assert.Equal("Hello, World!\n", result.Stdout);
            Assert.Equal("args:-s\n", result.Stderr);
        }
        finally
        {
            File.Delete(physicalShellPath);
        }
    }

    [Fact(DisplayName = "settles after the shell exits when a detached descendant retains inherited stdio")]
    public async Task Settles_after_the_shell_exits_when_a_detached_descendant_retains_inherited_stdio()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var result = Result.GetOrThrow(await env.ExecAsync("sleep 1 & echo child-exiting", new ShellExecOptions { Timeout = 3 }));

        Assert.Contains("child-exiting", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "cleanup terminates active shell processes")]
    public async Task Cleanup_terminates_active_shell_processes()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var execution = env.ExecAsync("touch started; sleep 60");
        for (var attempt = 0; attempt < 100 && !Result.GetOrThrow(await env.ExistsAsync("started")); attempt++)
        {
            await H4TestSupport.DelayAsync(10);
        }

        Assert.True(Result.GetOrThrow(await env.ExistsAsync("started")));
        await env.CleanupAsync();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Ok);
    }

    [Fact(DisplayName = "streams stdout and stderr chunks")]
    public async Task Streams_stdout_and_stderr_chunks()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var result = Result.GetOrThrow(await env.ExecAsync(
            "printf out; printf err >&2",
            new ShellExecOptions
            {
                OnStdout = chunk => stdout.Append(chunk),
                OnStderr = chunk => stderr.Append(chunk),
            }));

        Assert.Equal("out", result.Stdout);
        Assert.Equal("err", result.Stderr);
        Assert.Equal("out", stdout.ToString());
        Assert.Equal("err", stderr.ToString());
    }

    [Fact(DisplayName = "reports a missing working directory before spawning")]
    public async Task Reports_a_missing_working_directory_before_spawning()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(Path.Combine(temp.Path, "missing"));
        var result = await env.ExecAsync("printf ok");

        Assert.False(result.Ok);
        Assert.Equal(ExecutionErrorCodes.SpawnError, result.Error!.Code);
        Assert.Contains("Working directory does not exist", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "returns non-zero command exit codes as successful execution results")]
    public async Task Returns_non_zero_command_exit_codes_as_successful_execution_results()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var result = Result.GetOrThrow(await env.ExecAsync("exit 7"));

        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact(DisplayName = "returns timeout errors for commands exceeding the timeout")]
    public async Task Returns_timeout_errors_for_commands_exceeding_the_timeout()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var result = await env.ExecAsync("sleep 5", new ShellExecOptions { Timeout = 0.01 });

        Assert.False(result.Ok);
        Assert.Equal(ExecutionErrorCodes.Timeout, result.Error!.Code);
    }

    [Fact(DisplayName = "returns callback errors from exec stream handlers")]
    public async Task Returns_callback_errors_from_exec_stream_handlers()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var result = await env.ExecAsync("printf out", new ShellExecOptions
        {
            OnStdout = _ => throw new InvalidOperationException("callback failed"),
        });

        Assert.False(result.Ok);
        Assert.Equal(ExecutionErrorCodes.CallbackError, result.Error!.Code);
        Assert.Equal("callback failed", result.Error.Message);
    }

    [Fact(DisplayName = "returns shell unavailable and spawn errors")]
    public async Task Returns_shell_unavailable_and_spawn_errors()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var missingShellEnv = new SystemExecutionEnv(temp.Path, Path.Combine(temp.Path, "missing-shell"));
        var missingShell = await missingShellEnv.ExecAsync("printf ok");
        Assert.False(missingShell.Ok);
        Assert.Equal(ExecutionErrorCodes.ShellUnavailable, missingShell.Error!.Code);

        var shellPath = Path.Combine(temp.Path, "not-executable-shell");
        var env = new SystemExecutionEnv(temp.Path);
        Result.GetOrThrow(await env.WriteFileAsync(shellPath, "not executable"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shellPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var spawnError = await new SystemExecutionEnv(temp.Path, shellPath).ExecAsync("printf ok");
        Assert.False(spawnError.Ok);
        Assert.Equal(ExecutionErrorCodes.SpawnError, spawnError.Error!.Code);
    }

    [Fact(DisplayName = "returns an aborted result for aborted commands")]
    public async Task Returns_an_aborted_result_for_aborted_commands()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        using var controller = new CancellationTokenSource();
        var execution = env.ExecAsync("sleep 5", new ShellExecOptions { AbortSignal = controller.Token });
        await H4TestSupport.DelayAsync(20);
        controller.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Ok);
        Assert.Equal(ExecutionErrorCodes.Aborted, result.Error!.Code);
    }

    [Fact(DisplayName = "ignores asynchronous taskkill spawn errors during abort")]
    public async Task Ignores_asynchronous_taskkill_spawn_errors_during_abort()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = H4TestSupport.CreateTempDirectory();
        using var controller = new CancellationTokenSource();
        var env = new SystemExecutionEnv(temp.Path, "/bin/bash");
        var execution = env.ExecAsync("exec sleep 60", new ShellExecOptions { AbortSignal = controller.Token });
        await H4TestSupport.DelayAsync(20);
        controller.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Ok);
        Assert.Equal(ExecutionErrorCodes.Aborted, result.Error!.Code);
    }

    [Fact(DisplayName = "captures large shell output to a full output file through the execution env")]
    public async Task Captures_large_shell_output_to_a_full_output_file_through_the_execution_env()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(temp.Path);
        var capture = Result.GetOrThrow(await ShellOutput.ExecuteShellWithCaptureAsync(env, "yes line | head -n 15000"));

        Assert.True(capture.Truncated);
        Assert.NotNull(capture.FullOutputPath);
        var fullOutput = Result.GetOrThrow(await env.ReadTextFileAsync(capture.FullOutputPath!));
        Assert.True(fullOutput.Split('\n').Length > 10_000);
        Assert.True(capture.Output.Length < fullOutput.Length);
    }

    private static async Task AssertShellEnvironmentOverrideAsync(
        IReadOnlyDictionary<string, string>? overrides,
        string expectedSessionFile)
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(
            temp.Path,
            shellEnvironment: new Dictionary<string, string>
            {
                ["PI_SESSION_FILE"] = "/stale/parent.jsonl",
                ["PI_CODING_AGENT"] = "true",
                ["PI_NODE_ENV_PRESERVED_TEST"] = "preserved",
            });
        var result = Result.GetOrThrow(await env.ExecAsync(
            "printf '%s:%s|%s|%s' \"${PI_SESSION_FILE+x}\" \"${PI_SESSION_FILE-}\" \"$PI_CODING_AGENT\" \"$PI_NODE_ENV_PRESERVED_TEST\"",
            new ShellExecOptions { Environment = overrides }));

        Assert.Equal($"{expectedSessionFile}|true|preserved", result.Stdout);
    }

    private static void AssertAborted<T>(Result<T, FileError> result)
    {
        Assert.False(result.Ok);
        Assert.Equal(FileErrorCodes.Aborted, result.Error!.Code);
    }
}
