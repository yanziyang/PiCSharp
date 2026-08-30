using Pi.Tui;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream TUI render scheduling and differential-rendering cases.</summary>
public sealed class TuiRenderTests
{
    private static readonly int[] _termuxHeights = [15, 8, 14, 11];

    [Fact(DisplayName = "renders keyboard input without waiting for a throttled frame")]
    public async Task Renders_keyboard_input_without_waiting_for_a_throttled_frame()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new InputComponent { Lines = ["initial"] };
        tui.AddChild(component);
        tui.SetFocus(component);
        tui.Start();
        tui.RenderNow();
        var renderCountBeforeInput = component.RenderCount;

        component.Lines = ["pending"];
        tui.RequestRender();
        terminal.SendInput("first");
        terminal.SendInput("second");
        terminal.SendInput("typed");
        await WaitForConditionAsync(() => component.RenderCount >= renderCountBeforeInput + 1);

        Assert.Equal(renderCountBeforeInput + 1, component.RenderCount);
        Assert.Equal(["typed"], component.Lines);
    }

    [Fact(DisplayName = "writes redraw logs to the provided directory")]
    public async Task Writes_redraw_logs_to_the_provided_directory()
    {
        var logDirectory = Directory.CreateTempSubdirectory("pi-tui-log-");
        var previous = Environment.GetEnvironmentVariable("PI_DEBUG_REDRAW");
        try
        {
            Environment.SetEnvironmentVariable("PI_DEBUG_REDRAW", "1");
            var terminal = new MemoryTerminal(40, 10);
            using var tui = new TuiMainScreen(terminal, logDirectory: logDirectory.FullName);
            var component = new TestComponent { Lines = ["test"] };
            tui.AddChild(component);
            tui.Start();
            await terminal.WaitForRenderAsync();

            var logPath = Path.Combine(logDirectory.FullName, "pi-debug.log");
            Assert.True(File.Exists(logPath));
            Assert.Contains("fullRender: first render", File.ReadAllText(logPath), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_DEBUG_REDRAW", previous);
            logDirectory.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "splits a large full render without changing its output")]
    public void Splits_a_large_full_render_without_changing_its_output()
    {
        var terminal = new BoundedWriteTerminal();
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent();
        var kittyLine = $"\x1b_Ga=T,f=100;{new string('A', 1_200_000)}\x1b\\";
        component.Lines = [kittyLine, kittyLine];
        tui.AddChild(component);

        tui.RenderNow();

        Assert.True(terminal.Writes.Count > 2, "large output should be split across terminal writes");
        Assert.All(terminal.Writes, write => Assert.True(write.Length <= TerminalOutputWriter.MaxWriteCharacters));
        var output = string.Concat(terminal.Writes);
        Assert.StartsWith("\x1b[?2026h" + kittyLine + "\r\n" + kittyLine + "\x1b[?2026l", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "splits large differential updates without a full redraw")]
    public void Splits_large_differential_updates_without_a_full_redraw()
    {
        var terminal = new BoundedWriteTerminal();
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["before"] };
        tui.AddChild(component);
        tui.RenderNow();
        terminal.Writes.Clear();

        var kittyLine = $"\x1b_Ga=T,f=100;{new string('A', 1_200_000)}\x1b\\";
        component.Lines = ["before", kittyLine, kittyLine];
        tui.RenderNow();

        Assert.True(terminal.Writes.Count > 2);
        Assert.All(terminal.Writes, write => Assert.True(write.Length <= TerminalOutputWriter.MaxWriteCharacters));
        var output = string.Concat(terminal.Writes);
        Assert.StartsWith("\x1b[?2026h", output, StringComparison.Ordinal);
        Assert.Contains("\x1b[?2026l", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[2J", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "triggers full re-render when terminal height changes")]
    public async Task Triggers_full_re_render_when_terminal_height_changes()
    {
        await WithEnvironmentAsync("TERMUX_VERSION", null, async () =>
        {
            var terminal = new MemoryTerminal(40, 10);
            using var tui = new TuiMainScreen(terminal);
            var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2"] };
            tui.AddChild(component);
            tui.Start();
            await terminal.WaitForRenderAsync();
            var initialRedraws = tui.FullRedraws;

            terminal.Resize(40, 15);
            await WaitForConditionAsync(() => tui.FullRedraws > initialRedraws);

            Assert.Contains("Line 0", terminal.GetViewport()[0], StringComparison.Ordinal);
        });
    }

    [Fact(DisplayName = "skips full re-render on height changes in Termux")]
    public async Task Skips_full_re_render_on_height_changes_in_termux()
    {
        await WithEnvironmentAsync("TERMUX_VERSION", "1", async () =>
        {
            var terminal = new MemoryTerminal(40, 10);
            using var tui = new TuiMainScreen(terminal);
            var component = new TestComponent
            {
                Lines = Enumerable.Range(0, 20).Select(index => $"Line {index}").ToArray(),
            };
            tui.AddChild(component);
            tui.Start();
            await terminal.WaitForRenderAsync();
            terminal.ClearWrites();
            var initialRedraws = tui.FullRedraws;

            foreach (var height in _termuxHeights)
            {
                terminal.Resize(40, height);
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }

            Assert.Equal(initialRedraws, tui.FullRedraws);
            Assert.DoesNotContain("\x1b[2J", string.Concat(terminal.Writes), StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b[3J", string.Concat(terminal.Writes), StringComparison.Ordinal);
            Assert.Contains("Line 19", string.Join('\n', terminal.GetViewport()), StringComparison.Ordinal);
        });
    }

    [Fact(DisplayName = "triggers full re-render when terminal width changes")]
    public async Task Triggers_full_re_render_when_terminal_width_changes()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        var initialRedraws = tui.FullRedraws;

        terminal.Resize(60, 10);
        await WaitForConditionAsync(() => tui.FullRedraws > initialRedraws);

        Assert.True(tui.FullRedraws > initialRedraws);
    }

    [Fact(DisplayName = "clears empty rows when content shrinks significantly")]
    public async Task Clears_empty_rows_when_content_shrinks_significantly()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        tui.SetClearOnShrink(true);
        var component = new TestComponent
        {
            Lines = ["Line 0", "Line 1", "Line 2", "Line 3", "Line 4", "Line 5"],
        };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        var initialRedraws = tui.FullRedraws;

        component.Lines = ["Line 0", "Line 1"];
        tui.RequestRender();
        await WaitForConditionAsync(() => tui.FullRedraws > initialRedraws);

        var viewport = terminal.GetViewport();
        Assert.Contains("Line 0", viewport[0], StringComparison.Ordinal);
        Assert.Contains("Line 1", viewport[1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewport[2].Trim());
        Assert.Equal(string.Empty, viewport[3].Trim());
    }

    [Fact(DisplayName = "handles shrink to single line")]
    public async Task Handles_shrink_to_single_line()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        tui.SetClearOnShrink(true);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2", "Line 3"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();

        component.Lines = ["Only line"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var viewport = terminal.GetViewport();
        Assert.Contains("Only line", viewport[0], StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewport[1].Trim());
    }

    [Fact(DisplayName = "handles shrink to empty")]
    public async Task Handles_shrink_to_empty()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        tui.SetClearOnShrink(true);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();

        component.Lines = [];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        Assert.All(terminal.GetViewport(), line => Assert.Equal(string.Empty, line.Trim()));
    }

    [Fact(DisplayName = "tracks cursor correctly when content shrinks with unchanged remaining lines")]
    public async Task Tracks_cursor_correctly_when_content_shrinks_with_unchanged_remaining_lines()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent
        {
            Lines = ["Line 0", "Line 1", "Line 2", "Line 3", "Line 4"],
        };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();

        component.Lines = ["Line 0", "Line 1", "Line 2"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();
        component.Lines = ["Line 0", "CHANGED", "Line 2"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        Assert.Contains("CHANGED", terminal.GetViewport()[1], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "renders correctly when only a middle line changes (spinner case)")]
    public async Task Renders_correctly_when_only_a_middle_line_changes_spinner_case()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["Header", "Working...", "Footer"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();

        foreach (var frame in new[] { "|", "/", "-", "\\" })
        {
            component.Lines = ["Header", $"Working {frame}", "Footer"];
            tui.RequestRender();
            await terminal.WaitForRenderAsync();
            var viewport = terminal.GetViewport();
            Assert.Contains("Header", viewport[0], StringComparison.Ordinal);
            Assert.Contains($"Working {frame}", viewport[1], StringComparison.Ordinal);
            Assert.Contains("Footer", viewport[2], StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "resets styles after each rendered line")]
    public async Task Resets_styles_after_each_rendered_line()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["\x1b[3mItalic", "Plain"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();

        Assert.False(terminal.GetCellItalic(1, 0));
    }

    [Fact(DisplayName = "renders correctly when first line changes but rest stays same")]
    public async Task Renders_correctly_when_first_line_changes_but_rest_stays_same()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2", "Line 3"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        component.Lines = ["CHANGED", "Line 1", "Line 2", "Line 3"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var viewport = terminal.GetViewport();
        Assert.Contains("CHANGED", viewport[0], StringComparison.Ordinal);
        Assert.Contains("Line 1", viewport[1], StringComparison.Ordinal);
        Assert.Contains("Line 2", viewport[2], StringComparison.Ordinal);
        Assert.Contains("Line 3", viewport[3], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "renders correctly when last line changes but rest stays same")]
    public async Task Renders_correctly_when_last_line_changes_but_rest_stays_same()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2", "Line 3"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        component.Lines = ["Line 0", "Line 1", "Line 2", "CHANGED"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var viewport = terminal.GetViewport();
        Assert.Contains("Line 0", viewport[0], StringComparison.Ordinal);
        Assert.Contains("Line 1", viewport[1], StringComparison.Ordinal);
        Assert.Contains("Line 2", viewport[2], StringComparison.Ordinal);
        Assert.Contains("CHANGED", viewport[3], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "renders correctly when multiple non-adjacent lines change")]
    public async Task Renders_correctly_when_multiple_non_adjacent_lines_change()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2", "Line 3", "Line 4"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        component.Lines = ["Line 0", "CHANGED 1", "Line 2", "CHANGED 3", "Line 4"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var viewport = terminal.GetViewport();
        Assert.Contains("Line 0", viewport[0], StringComparison.Ordinal);
        Assert.Contains("CHANGED 1", viewport[1], StringComparison.Ordinal);
        Assert.Contains("Line 2", viewport[2], StringComparison.Ordinal);
        Assert.Contains("CHANGED 3", viewport[3], StringComparison.Ordinal);
        Assert.Contains("Line 4", viewport[4], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "handles transition from content to empty and back to content")]
    public async Task Handles_transition_from_content_to_empty_and_back_to_content()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent { Lines = ["Line 0", "Line 1", "Line 2"] };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        Assert.Contains("Line 0", terminal.GetViewport()[0], StringComparison.Ordinal);

        component.Lines = [];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();
        component.Lines = ["New Line 0", "New Line 1"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var viewport = terminal.GetViewport();
        Assert.Contains("New Line 0", viewport[0], StringComparison.Ordinal);
        Assert.Contains("New Line 1", viewport[1], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "full re-renders when deleted lines move the viewport upward")]
    public async Task Full_re_renders_when_deleted_lines_move_the_viewport_upward()
    {
        var terminal = new MemoryTerminal(20, 5);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent
        {
            Lines = Enumerable.Range(0, 12).Select(index => $"Line {index}").ToArray(),
        };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        var initialRedraws = tui.FullRedraws;

        component.Lines = Enumerable.Range(0, 7).Select(index => $"Line {index}").ToArray();
        tui.RequestRender();
        await WaitForConditionAsync(() => tui.FullRedraws > initialRedraws);

        Assert.True(tui.FullRedraws > initialRedraws);
        Assert.Equal(["Line 2", "Line 3", "Line 4", "Line 5", "Line 6"], terminal.GetViewport());
    }

    [Fact(DisplayName = "appends after a shrink without another full redraw once the viewport is reset")]
    public async Task Appends_after_a_shrink_without_another_full_redraw_once_the_viewport_is_reset()
    {
        var terminal = new MemoryTerminal(20, 5);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent
        {
            Lines = Enumerable.Range(0, 8).Select(index => $"Line {index}").ToArray(),
        };
        tui.AddChild(component);
        tui.Start();
        await terminal.WaitForRenderAsync();
        var initialRedraws = tui.FullRedraws;

        component.Lines = ["Line 0", "Line 1"];
        tui.RequestRender();
        await WaitForConditionAsync(() => tui.FullRedraws > initialRedraws);
        var redrawsAfterShrink = tui.FullRedraws;

        component.Lines = ["Line 0", "Line 1", "Line 2"];
        tui.RequestRender();
        await WaitForConditionAsync(() => terminal.GetViewport().ElementAtOrDefault(2) == "Line 2");

        Assert.Equal(redrawsAfterShrink, tui.FullRedraws);
        Assert.Equal(["Line 0", "Line 1", "Line 2", string.Empty, string.Empty], terminal.GetViewport());
    }

    [Fact(DisplayName = "clears stale content when maxLinesRendered was inflated by a transient component")]
    public async Task Clears_stale_content_when_max_lines_rendered_was_inflated_by_a_transient_component()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var chat = new TestComponent();
        var editor = new TestComponent();
        tui.AddChild(chat);
        tui.AddChild(editor);
        var longChat = Enumerable.Range(0, 15).Select(index => $"Chat {index}").ToArray();
        var shortChat = Enumerable.Range(0, 12).Select(index => $"Chat {index}").ToArray();
        chat.Lines = longChat;
        editor.Lines = ["Editor 0", "Editor 1", "Editor 2"];
        tui.Start();
        await terminal.WaitForRenderAsync();

        editor.Lines = Enumerable.Range(0, 8).Select(index => $"Selector {index}").ToArray();
        tui.RequestRender();
        await terminal.WaitForRenderAsync();
        editor.Lines = ["Editor 0", "Editor 1", "Editor 2"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();
        var redrawsBeforeSwitch = tui.FullRedraws;

        chat.Lines = shortChat;
        tui.RequestRender();
        await WaitForConditionAsync(() => tui.FullRedraws > redrawsBeforeSwitch);

        var viewport = terminal.GetViewport();
        Assert.All(viewport, line =>
        {
            Assert.DoesNotContain("Chat 12", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Chat 13", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Chat 14", line, StringComparison.Ordinal);
        });
        Assert.Equal(
            ["Chat 5", "Chat 6", "Chat 7", "Chat 8", "Chat 9", "Chat 10", "Chat 11", "Editor 0", "Editor 1", "Editor 2"],
            viewport);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the TUI render condition.");
    }

    private static async Task WithEnvironmentAsync(string name, string? value, Func<Task> action)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
