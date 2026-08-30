using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Env;
using Pi.AgentCore.Harness.Tools;
using Pi.AgentCore.Harness.Utils;
using Pi.Ai;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

[SuppressMessage("Usage", "xUnit1051", Justification = "Harness tests intentionally exercise the built-in tools with explicit deterministic cancellation tokens.")]
public sealed class ToolsTests
{
    [Fact(DisplayName = "reads text with offsets, limits, and continuation notices")]
    public async Task Reads_text_with_offsets_limits_and_continuation_notices()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "test.txt", string.Join('\n', Enumerable.Range(1, 100).Select(index => $"Line {index}")));

        var result = await ReadTool.CreateReadTool().Execute(
            "read-1",
            new JsonObject { ["path"] = "test.txt", ["offset"] = 41, ["limit"] = 20 },
            CancellationToken.None,
            null,
            context);
        var output = H4TestSupport.TextOutput(result);

        Assert.DoesNotContain("Line 40", output, StringComparison.Ordinal);
        Assert.Contains("Line 41", output, StringComparison.Ordinal);
        Assert.Contains("Line 60", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 61", output, StringComparison.Ordinal);
        Assert.Contains("[40 more lines in file. Use offset=61 to continue.]", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "truncates large text by line count")]
    public async Task Truncates_large_text_by_line_count()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "large.txt", string.Join('\n', Enumerable.Range(1, 2500).Select(index => $"Line {index}")));

        var result = await ReadTool.CreateReadTool().Execute(
            "read-2",
            new JsonObject { ["path"] = "large.txt" },
            CancellationToken.None,
            null,
            context);

        Assert.Contains("[Showing lines 1-2000 of 2500. Use offset=2001 to continue.]", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
        var truncation = GetTruncation(result);
        Assert.True(truncation["truncated"]!.GetValue<bool>());
        Assert.Equal("lines", truncation["truncatedBy"]!.GetValue<string>());
        Assert.Equal(2500, truncation["totalLines"]!.GetValue<int>());
        Assert.Equal(2000, truncation["outputLines"]!.GetValue<int>());
    }

    [Fact(DisplayName = "does not count a trailing newline as an extra line at the truncation limit")]
    public async Task Does_not_count_a_trailing_newline_as_an_extra_line_at_the_truncation_limit()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "exact.txt", string.Join('\n', Enumerable.Repeat("x", 2000)) + '\n');

        var result = await ReadTool.CreateReadTool().Execute(
            "read-exact",
            new JsonObject { ["path"] = "exact.txt" },
            CancellationToken.None,
            null,
            context);

        Assert.Null(result.Details);
        Assert.DoesNotContain("Use offset=", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "rejects offsets beyond the file")]
    public async Task Rejects_offsets_beyond_the_file()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "short.txt", "one\ntwo\nthree");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadTool.CreateReadTool().Execute(
            "read-3",
            new JsonObject { ["path"] = "short.txt", ["offset"] = 100 },
            CancellationToken.None,
            null,
            context));
        Assert.Equal("Offset 100 is beyond end of file (3 lines total)", exception.Message);
    }

    [Fact(DisplayName = "detects supported images by content")]
    public async Task Detects_supported_images_by_content()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==");
        await WriteAsync(context.Env, "image.txt", png);

        var result = await ReadTool.CreateReadTool().Execute(
            "read-4",
            new JsonObject { ["path"] = "image.txt" },
            CancellationToken.None,
            null,
            context);

        Assert.Contains("Read image file [image/png]", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
        var image = Assert.Single(result.Content.OfType<ImageContent>());
        Assert.Equal(Convert.ToBase64String(png), image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact(DisplayName = "delegates image conversion and resizing to an injected processor")]
    public async Task Delegates_image_conversion_and_resizing_to_an_injected_processor()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var bmp = CreateTinyBmp();
        await WriteAsync(context.Env, "image.bmp", bmp);
        byte[]? receivedBytes = null;
        string? receivedMimeType = null;
        bool? receivedAutoResize = null;
        var tool = ReadTool.CreateReadTool(new ReadToolOptions
        {
            AutoResizeImages = false,
            ImageProcessor = (bytes, mimeType, autoResizeImages) =>
            {
                receivedBytes = bytes;
                receivedMimeType = mimeType;
                receivedAutoResize = autoResizeImages;
                return Task.FromResult(ReadImageProcessorResult.Success(
                    "converted",
                    "image/png",
                    ["[Image converted from image/bmp to image/png.]"]));
            },
        });

        var result = await tool.Execute(
            "read-bmp",
            new JsonObject { ["path"] = "image.bmp" },
            CancellationToken.None,
            null,
            context);

        Assert.Equal("image/bmp", receivedMimeType);
        Assert.False(receivedAutoResize);
        Assert.Equal(bmp, receivedBytes);
        Assert.Contains("[Image converted from image/bmp to image/png.]", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
        var image = Assert.Single(result.Content.OfType<ImageContent>());
        Assert.Equal("converted", image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact(DisplayName = "writes files and creates parent directories")]
    public async Task Writes_files_and_creates_parent_directories()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var result = await WriteTool.CreateWriteTool().Execute(
            "write-1",
            new JsonObject { ["path"] = "nested/dir/file.txt", ["content"] = "hello" },
            CancellationToken.None,
            null,
            context);

        Assert.Equal("Successfully wrote 5 bytes to nested/dir/file.txt", H4TestSupport.TextOutput(result));
        Assert.Equal("hello", Result.GetOrThrow(await context.Env.ReadTextFileAsync("nested/dir/file.txt")));
    }

    [Fact(DisplayName = "keeps the mutation queue locked until an aborted write settles")]
    public async Task Keeps_the_mutation_queue_locked_until_an_aborted_write_settles()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new BlockingWriteExecutionEnv(temp.Path);
        var tool = WriteTool.CreateWriteTool();
        using var controller = new CancellationTokenSource();
        var firstWrite = tool.Execute(
            "write-first",
            new JsonObject { ["path"] = "file.txt", ["content"] = "first\n" },
            controller.Token,
            null,
            new ExecutionToolContext { Env = env });
        await env.FirstWriteStarted.Task;
        controller.Cancel();
        var secondWrite = tool.Execute(
            "write-second",
            new JsonObject { ["path"] = "file.txt", ["content"] = "second\n" },
            CancellationToken.None,
            null,
            new ExecutionToolContext { Env = env });

        await H4TestSupport.DelayAsync(20);
        Assert.False(env.SecondWriteStarted);
        env.FinishFirstWrite.TrySetResult(null);
        await Assert.ThrowsAsync<FileError>(() => firstWrite);
        await secondWrite;
        Assert.Equal("second\n", Result.GetOrThrow(await env.ReadTextFileAsync("file.txt")));
    }

    [Fact(DisplayName = "applies disjoint edits and returns both diff formats")]
    public async Task Applies_disjoint_edits_and_returns_both_diff_formats()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var original = "alpha\nbeta\ngamma\ndelta\n";
        await WriteAsync(context.Env, "edit.txt", original);

        var result = await EditTool.CreateEditTool().Execute(
            "edit-1",
            new JsonObject
            {
                ["path"] = "edit.txt",
                ["edits"] = new JsonArray
                {
                    new JsonObject { ["oldText"] = "alpha\n", ["newText"] = "ALPHA\n" },
                    new JsonObject { ["oldText"] = "gamma\n", ["newText"] = "GAMMA\n" },
                },
            },
            CancellationToken.None,
            null,
            context);
        var details = Assert.IsType<JsonObject>(result.Details);

        Assert.Equal("Successfully replaced 2 block(s) in edit.txt.", H4TestSupport.TextOutput(result));
        Assert.Contains("ALPHA", details["diff"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("GAMMA", details["diff"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ALPHA\nbeta\nGAMMA\ndelta\n", ApplyUnifiedPatch(original, details["patch"]!.GetValue<string>()));
        Assert.Equal("ALPHA\nbeta\nGAMMA\ndelta\n", Result.GetOrThrow(await context.Env.ReadTextFileAsync("edit.txt")));
    }

    [Fact(DisplayName = "matches all edits against the original and rejects overlaps")]
    public async Task Matches_all_edits_against_the_original_and_rejects_overlaps()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "edit.txt", "one\ntwo\nthree\n");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => EditTool.CreateEditTool().Execute(
            "edit-2",
            new JsonObject
            {
                ["path"] = "edit.txt",
                ["edits"] = new JsonArray
                {
                    new JsonObject { ["oldText"] = "one\ntwo\n", ["newText"] = "ONE\nTWO\n" },
                    new JsonObject { ["oldText"] = "two\nthree\n", ["newText"] = "TWO\nTHREE\n" },
                },
            },
            CancellationToken.None,
            null,
            context));
        Assert.Contains("overlap", exception.Message, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\nthree\n", Result.GetOrThrow(await context.Env.ReadTextFileAsync("edit.txt")));
    }

    [Fact(DisplayName = "rejects missing and duplicate target text")]
    public async Task Rejects_missing_and_duplicate_target_text()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "edit.txt", "foo foo foo");
        var tool = EditTool.CreateEditTool();

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(
            "edit-3",
            new JsonObject
            {
                ["path"] = "edit.txt",
                ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "bar", ["newText"] = "baz" } },
            },
            CancellationToken.None,
            null,
            context));
        Assert.Contains("Could not find the exact text", missing.Message, StringComparison.Ordinal);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(
            "edit-4",
            new JsonObject
            {
                ["path"] = "edit.txt",
                ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "foo", ["newText"] = "bar" } },
            },
            CancellationToken.None,
            null,
            context));
        Assert.Contains("Found 3 occurrences", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "keeps the mutation queue locked until an aborted edit write settles")]
    public async Task Keeps_the_mutation_queue_locked_until_an_aborted_edit_write_settles()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new BlockingEditExecutionEnv(temp.Path);
        await WriteAsync(env, "file.txt", "alpha\nbeta\n");
        var tool = EditTool.CreateEditTool();
        using var controller = new CancellationTokenSource();
        var firstEdit = tool.Execute(
            "edit-first",
            new JsonObject
            {
                ["path"] = "file.txt",
                ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "alpha", ["newText"] = "ALPHA" } },
            },
            controller.Token,
            null,
            new ExecutionToolContext { Env = env });
        await env.FirstEditWriteStarted.Task;
        controller.Cancel();
        var secondEdit = tool.Execute(
            "edit-second",
            new JsonObject
            {
                ["path"] = "file.txt",
                ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "beta", ["newText"] = "BETA" } },
            },
            CancellationToken.None,
            null,
            new ExecutionToolContext { Env = env });

        await H4TestSupport.DelayAsync(20);
        Assert.False(env.SecondEditWriteStarted);
        env.FinishFirstEditWrite.TrySetResult(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstEdit);
        await secondEdit;
        Assert.True(env.FirstEditWriteSettled);
        Assert.Equal("ALPHA\nBETA\n", Result.GetOrThrow(await env.ReadTextFileAsync("file.txt")));
    }

    [Fact(DisplayName = "serializes concurrent edits through canonical and symlink paths")]
    public async Task Serializes_concurrent_edits_through_canonical_and_symlink_paths()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SlowReadExecutionEnv(temp.Path);
        await WriteAsync(env, "target.txt", "alpha\nbeta\ngamma\n");
        if (!H4TestSupport.TryCreateSymbolicLink(Path.Combine(temp.Path, "link.txt"), Path.Combine(temp.Path, "target.txt")))
        {
            return;
        }

        var tool = EditTool.CreateEditTool();
        await Task.WhenAll(
            tool.Execute(
                "edit-target",
                new JsonObject
                {
                    ["path"] = "target.txt",
                    ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "alpha", ["newText"] = "ALPHA" } },
                },
                CancellationToken.None,
                null,
                new ExecutionToolContext { Env = env }),
            tool.Execute(
                "edit-link",
                new JsonObject
                {
                    ["path"] = "link.txt",
                    ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "beta", ["newText"] = "BETA" } },
                },
                CancellationToken.None,
                null,
                new ExecutionToolContext { Env = env }));

        Assert.Equal("ALPHA\nBETA\ngamma\n", Result.GetOrThrow(await env.ReadTextFileAsync("target.txt")));
    }

    [Fact(DisplayName = "edits regular files through symlinks")]
    public async Task Edits_regular_files_through_symlinks()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "target.txt", "before\n");
        if (!H4TestSupport.TryCreateSymbolicLink(Path.Combine(temp.Path, "link.txt"), Path.Combine(temp.Path, "target.txt")))
        {
            return;
        }

        await EditTool.CreateEditTool().Execute(
            "edit-symlink",
            new JsonObject
            {
                ["path"] = "link.txt",
                ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "before", ["newText"] = "after" } },
            },
            CancellationToken.None,
            null,
            context);

        Assert.Equal("after\n", Result.GetOrThrow(await context.Env.ReadTextFileAsync("target.txt")));
    }

    [Fact(DisplayName = "preserves BOM and CRLF line endings")]
    public async Task Preserves_bom_and_crlf_line_endings()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        await WriteAsync(context.Env, "edit.txt", "\uFEFFone\r\ntwo\r\n");

        await EditTool.CreateEditTool().Execute(
            "edit-5",
            new JsonObject
            {
                ["path"] = "edit.txt",
                ["edits"] = new JsonArray { new JsonObject { ["oldText"] = "two", ["newText"] = "TWO" } },
            },
            CancellationToken.None,
            null,
            context);

        Assert.Equal("\uFEFFone\r\nTWO\r\n", Result.GetOrThrow(await context.Env.ReadTextFileAsync("edit.txt")));
    }

    [Fact(DisplayName = "executes commands and combines stdout and stderr")]
    public async Task Executes_commands_and_combines_stdout_and_stderr()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var result = await BashTool.CreateBashTool().Execute(
            "bash-1",
            new JsonObject { ["command"] = "printf out; printf err >&2" },
            CancellationToken.None,
            null,
            context);

        Assert.Contains("out", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
        Assert.Contains("err", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "reports nonzero exits and timeouts")]
    public async Task Reports_nonzero_exits_and_timeouts()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var tool = BashTool.CreateBashTool();
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(
            "bash-2",
            new JsonObject { ["command"] = "printf failed; exit 7" },
            CancellationToken.None,
            null,
            context));
        Assert.Contains("failed", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Command exited with code 7", failed.Message, StringComparison.Ordinal);

        var timedOut = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(
            "bash-3",
            new JsonObject { ["command"] = "sleep 2", ["timeout"] = 0.01 },
            CancellationToken.None,
            null,
            context));
        Assert.Contains("Command timed out after 0.01 seconds", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "preserves truncated output when a command times out")]
    public async Task Preserves_truncated_output_when_a_command_times_out()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = new ExecutionToolContext { Env = new TimeoutOutputExecutionEnv(temp.Path) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BashTool.CreateBashTool().Execute(
            "bash-timeout-output",
            new JsonObject { ["command"] = "emit-output-then-time-out", ["timeout"] = 0.05 },
            CancellationToken.None,
            null,
            context));

        Assert.Contains("Command timed out after 0.05 seconds", error.Message, StringComparison.Ordinal);
        var marker = "Full output: ";
        var markerIndex = error.Message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var fullOutputPath = error.Message[(markerIndex + marker.Length)..].Split(']', '\n')[0];
        var fullOutput = Result.GetOrThrow(await context.Env.ReadTextFileAsync(fullOutputPath));
        Assert.Contains("line-1\nline-2", fullOutput, StringComparison.Ordinal);
        Assert.Contains("line-2000\nline-2001", fullOutput, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ignores output callbacks after execution settles")]
    public async Task Ignores_output_callbacks_after_execution_settles()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new LateOutputExecutionEnv(temp.Path);
        var updates = new List<string>();
        var result = await BashTool.CreateBashTool().Execute(
            "bash-late",
            new JsonObject { ["command"] = "late" },
            CancellationToken.None,
            update => updates.Add(H4TestSupport.TextOutput(update)),
            new ExecutionToolContext { Env = env });
        await H4TestSupport.DelayAsync(20);

        Assert.Equal("before\n", H4TestSupport.TextOutput(result));
        Assert.DoesNotContain(updates, update => update.Contains("late", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "reports the total size of an oversized final line")]
    public async Task Reports_the_total_size_of_an_oversized_final_line()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var result = await BashTool.CreateBashTool().Execute(
            "bash-long-line",
            new JsonObject { ["command"] = "printf '%060000d' 0" },
            CancellationToken.None,
            null,
            context);

        Assert.Matches("Showing last 50\\.0KB of line 1 \\(line is 58\\.6KB\\)\\. Full output:", H4TestSupport.TextOutput(result));
    }

    [Fact(DisplayName = "prepares command, cwd, and an explicit environment with the turn context")]
    public async Task Prepares_command_cwd_and_an_explicit_environment_with_the_turn_context()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var env = new SystemExecutionEnv(
            temp.Path,
            shellEnvironment: new Dictionary<string, string> { ["PI_BASH_PREPARE_INHERITED"] = "inherited" });
        Result.GetOrThrow(await env.CreateDirAsync("workspace"));
        var context = new PrepareContext { Env = env, Workspace = Path.Combine(temp.Path, "workspace") };
        using var controller = new CancellationTokenSource();
        PrepareContext? receivedContext = null;
        CancellationToken receivedSignal = default;
        var tool = BashTool.CreateBashTool(new BashToolOptions<PrepareContext>
        {
            CommandPrefix = "prefix=ready",
            Prepare = (execution, turnContext, signal) =>
            {
                receivedContext = turnContext;
                receivedSignal = signal;
                execution.Cwd = turnContext.Workspace;
                execution.Environment["PI_BASH_PREPARE_EXPLICIT"] = "explicit";
                execution.InheritEnvironment = false;
                execution.Command += "\nprintf '%s:%s:%s:%s' \"$prefix\" \"${PI_BASH_PREPARE_INHERITED-}\" \"$PI_BASH_PREPARE_EXPLICIT\" \"$PWD\"";
                return Task.CompletedTask;
            },
        });

        var result = await tool.Execute(
            "bash-prepare",
            new JsonObject { ["command"] = ":" },
            controller.Token,
            null,
            context);

        Assert.Same(context, receivedContext);
        Assert.Equal(controller.Token, receivedSignal);
        var expectedPrefix = "ready::explicit:";
        var output = H4TestSupport.TextOutput(result);
        Assert.StartsWith(expectedPrefix, output, StringComparison.Ordinal);
        Assert.EndsWith("/workspace", output.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "supports command prefixes")]
    public async Task Supports_command_prefixes()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var result = await BashTool.CreateBashTool(new BashToolOptions<ExecutionToolContext>
        {
            CommandPrefix = "value=hello",
        }).Execute(
            "bash-4",
            new JsonObject { ["command"] = "printf $value" },
            CancellationToken.None,
            null,
            CreateContext(temp.Path));

        Assert.Equal("hello", H4TestSupport.TextOutput(result));
    }

    [Fact(DisplayName = "coalesces updates and persists truncated full output")]
    public async Task Coalesces_updates_and_persists_truncated_full_output()
    {
        using var temp = H4TestSupport.CreateTempDirectory();
        var context = CreateContext(temp.Path);
        var updates = new List<AgentToolResult>();
        var result = await BashTool.CreateBashTool().Execute(
            "bash-5",
            new JsonObject { ["command"] = "i=1; while [ $i -le 3000 ]; do echo line-$i; i=$((i + 1)); done" },
            CancellationToken.None,
            updates.Add,
            context);

        Assert.True(updates.Count < 25);
        var truncation = GetTruncation(result);
        Assert.True(truncation["truncated"]!.GetValue<bool>());
        Assert.Equal("lines", truncation["truncatedBy"]!.GetValue<string>());
        Assert.Equal(3000, truncation["totalLines"]!.GetValue<int>());
        Assert.Equal(2000, truncation["outputLines"]!.GetValue<int>());
        Assert.Contains("line-3000", H4TestSupport.TextOutput(result), StringComparison.Ordinal);
        Assert.NotEmpty(updates);
        var finalUpdate = updates[^1];
        Assert.Contains("line-3000", H4TestSupport.TextOutput(finalUpdate), StringComparison.Ordinal);
        var finalDetails = Assert.IsType<JsonObject>(finalUpdate.Details);
        var finalTruncation = Assert.IsType<JsonObject>(finalDetails["truncation"]);
        Assert.Equal(3000, finalTruncation["totalLines"]!.GetValue<int>());
        Assert.True(finalTruncation["totalBytes"]!.GetValue<int>() > 0);
        var resultDetails = Assert.IsType<JsonObject>(result.Details);
        Assert.Equal(resultDetails["fullOutputPath"]!.GetValue<string>(), finalDetails["fullOutputPath"]!.GetValue<string>());
        var fullOutputPath = resultDetails["fullOutputPath"]!.GetValue<string>();
        var fullOutput = Result.GetOrThrow(await context.Env.ReadTextFileAsync(fullOutputPath));
        Assert.Contains("line-1\nline-2", fullOutput, StringComparison.Ordinal);
        Assert.Contains("line-2999\nline-3000", fullOutput, StringComparison.Ordinal);
    }

    private static ExecutionToolContext CreateContext(string path) => new() { Env = new SystemExecutionEnv(path) };

    private static async Task WriteAsync(ExecutionEnv env, string path, object content) =>
        Result.GetOrThrow(await env.WriteFileAsync(path, content));

    private static JsonObject GetTruncation(AgentToolResult result)
    {
        var details = Assert.IsType<JsonObject>(result.Details);
        return Assert.IsType<JsonObject>(details["truncation"]);
    }

    private static string ApplyUnifiedPatch(string original, string patch)
    {
        var sourceHadTrailingNewline = original.EndsWith('\n');
        var source = original.Split('\n').ToList();
        if (sourceHadTrailingNewline)
        {
            source.RemoveAt(source.Count - 1);
        }

        var output = new List<string>();
        var sourceIndex = 0;
        var lines = patch.Split('\n', StringSplitOptions.None);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(lines[index], "^@@ -(\\d+)(?:,\\d+)? \\+\\d+(?:,\\d+)? @@");
            Assert.True(match.Success, $"Invalid unified hunk header: {lines[index]}");
            var oldStart = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var targetIndex = Math.Max(0, oldStart - 1);
            while (sourceIndex < targetIndex)
            {
                output.Add(source[sourceIndex++]);
            }

            for (index++; index < lines.Length && !lines[index].StartsWith("@@ ", StringComparison.Ordinal); index++)
            {
                var line = lines[index];
                if (line.StartsWith("\\ No newline", StringComparison.Ordinal) || line.Length == 0)
                {
                    continue;
                }

                var prefix = line[0];
                var text = line[1..];
                switch (prefix)
                {
                    case ' ':
                        Assert.Equal(source[sourceIndex], text);
                        output.Add(source[sourceIndex++]);
                        break;
                    case '-':
                        Assert.Equal(source[sourceIndex], text);
                        sourceIndex++;
                        break;
                    case '+':
                        output.Add(text);
                        break;
                }
            }

            index--;
        }

        while (sourceIndex < source.Count)
        {
            output.Add(source[sourceIndex++]);
        }

        return string.Join('\n', output) + (sourceHadTrailingNewline ? "\n" : string.Empty);
    }

    private static byte[] CreateTinyBmp()
    {
        var bytes = new byte[58];
        bytes[0] = 0x42;
        bytes[1] = 0x4D;
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(1).CopyTo(bytes, 18);
        BitConverter.GetBytes(1).CopyTo(bytes, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(4).CopyTo(bytes, 34);
        return bytes;
    }

    private sealed class PrepareContext : ExecutionToolContext
    {
        public required string Workspace { get; init; }
    }

    private sealed class SlowReadExecutionEnv(string cwd) : SystemExecutionEnv(cwd)
    {
        public override async Task<Result<string, FileError>> ReadTextFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, CancellationToken.None);
            return await base.ReadTextFileAsync(path, cancellationToken);
        }
    }

    private sealed class BlockingWriteExecutionEnv(string cwd) : SystemExecutionEnv(cwd)
    {
        public TaskCompletionSource<object?> FirstWriteStarted { get; } = NewSource();
        public TaskCompletionSource<object?> FinishFirstWrite { get; } = NewSource();
        private int _secondWriteStarted;

        public bool SecondWriteStarted => Volatile.Read(ref _secondWriteStarted) != 0;

        public override async Task<Result<bool, FileError>> WriteFileAsync(
            string path,
            object content,
            CancellationToken cancellationToken = default)
        {
            if (content is string text && text == "first\n")
            {
                FirstWriteStarted.TrySetResult(null);
                await FinishFirstWrite.Task;
            }
            else if (content is string second && second == "second\n")
            {
                Interlocked.Exchange(ref _secondWriteStarted, 1);
            }

            return await base.WriteFileAsync(path, content, cancellationToken);
        }
    }

    private sealed class BlockingEditExecutionEnv(string cwd) : SystemExecutionEnv(cwd)
    {
        public TaskCompletionSource<object?> FirstEditWriteStarted { get; } = NewSource();
        public TaskCompletionSource<object?> FinishFirstEditWrite { get; } = NewSource();
        public bool FirstEditWriteSettled { get; private set; }
        private int _secondEditWriteStarted;

        public bool SecondEditWriteStarted => Volatile.Read(ref _secondEditWriteStarted) != 0;

        public override async Task<Result<bool, FileError>> WriteFileAsync(
            string path,
            object content,
            CancellationToken cancellationToken = default)
        {
            if (content is string text && text == "ALPHA\nbeta\n")
            {
                FirstEditWriteStarted.TrySetResult(null);
                await FinishFirstEditWrite.Task;
                var result = await base.WriteFileAsync(path, content, CancellationToken.None);
                FirstEditWriteSettled = true;
                return result;
            }

            if (content is string second && (second == "ALPHA\nBETA\n" || second == "alpha\nBETA\n"))
            {
                Interlocked.Exchange(ref _secondEditWriteStarted, 1);
            }

            return await base.WriteFileAsync(path, content, cancellationToken);
        }
    }

    private sealed class LateOutputExecutionEnv(string cwd) : SystemExecutionEnv(cwd)
    {
        public override Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
            string command,
            ShellExecOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options?.OnStdout?.Invoke("before\n");
            _ = Task.Run(async () =>
            {
                await Task.Delay(10).ConfigureAwait(false);
                options?.OnStdout?.Invoke("late\n");
            }, CancellationToken.None);
            return Task.FromResult(Result<ShellExecResult, ExecutionError>.Success(new ShellExecResult
            {
                Stdout = "before\n",
                Stderr = string.Empty,
                ExitCode = 0,
            }));
        }
    }

    private sealed class TimeoutOutputExecutionEnv(string cwd) : SystemExecutionEnv(cwd)
    {
        public override Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
            string command,
            ShellExecOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var output = string.Join('\n', Enumerable.Range(1, Truncate.DefaultMaxLines + 1).Select(index => $"line-{index}")) + '\n';
            options?.OnStdout?.Invoke(output);
            return Task.FromResult(Result<ShellExecResult, ExecutionError>.Failure(
                new ExecutionError(ExecutionErrorCodes.Timeout, $"timeout:{options?.Timeout}")));
        }
    }

    private static TaskCompletionSource<object?> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
