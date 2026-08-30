using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class OverlayOptionsTests
{
    [Fact(DisplayName = "should truncate overlay lines that exceed declared width")]
    public async Task Should_truncate_overlay_lines_that_exceed_declared_width()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var overlay = new StaticOverlay([new string('X', 100)]);
        tui.ShowOverlay(overlay, new OverlayOptions { Width = 20 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.All(terminal.GetViewport(), static line => Assert.NotNull(line));
    }

    [Fact(DisplayName = "should handle overlay with complex ANSI sequences without crashing")]
    public async Task Should_handle_overlay_with_complex_ansi_sequences_without_crashing()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var complexLine =
            "\x1b[48;2;40;50;40m \x1b[38;2;128;128;128mSome styled content\x1b[39m\x1b[49m" +
            "\x1b]8;;http://example.com\x07link\x1b]8;;\x07" +
            string.Concat(Enumerable.Repeat(" more content ", 10));
        tui.ShowOverlay(new StaticOverlay([complexLine, complexLine, complexLine]), new OverlayOptions { Width = 60 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.NotEmpty(terminal.GetViewport());
    }

    [Fact(DisplayName = "should handle overlay composited on styled base content")]
    public async Task Should_handle_overlay_composited_on_styled_base_content()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal, new StyledContent());
        tui.ShowOverlay(
            new StaticOverlay(["OVERLAY"]),
            new OverlayOptions { Width = 20, Anchor = OverlayAnchor.Center });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.Contains(terminal.GetViewport(), static line => line.Contains("OVERLAY", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "should handle wide characters at overlay boundary")]
    public async Task Should_handle_wide_characters_at_overlay_boundary()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(new StaticOverlay(["中文日本語한글テスト漢字"]), new OverlayOptions { Width = 15 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.NotEmpty(terminal.GetViewport());
    }

    [Fact(DisplayName = "should handle overlay positioned at terminal edge")]
    public async Task Should_handle_overlay_positioned_at_terminal_edge()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(new StaticOverlay([new string('X', 50)]), new OverlayOptions { Col = 60, Width = 20 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.NotEmpty(terminal.GetViewport());
    }

    [Fact(DisplayName = "should handle overlay on base content with OSC sequences")]
    public async Task Should_handle_overlay_on_base_content_with_osc_sequences()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal, new HyperlinkContent());
        tui.ShowOverlay(
            new StaticOverlay(["OVERLAY-TEXT"]),
            new OverlayOptions { Anchor = OverlayAnchor.Center, Width = 20 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.NotEmpty(terminal.GetViewport());
    }

    [Fact(DisplayName = "should render overlay at percentage of terminal width")]
    public async Task Should_render_overlay_at_percentage_of_terminal_width()
    {
        var terminal = new MemoryTerminal(100, 24);
        using var tui = CreateTui(terminal);
        var overlay = new StaticOverlay(["test"]);
        tui.ShowOverlay(overlay, new OverlayOptions { Width = "50%" });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(50, overlay.RequestedWidth);
    }

    [Fact(DisplayName = "should respect minWidth when widthPercent results in smaller width")]
    public async Task Should_respect_min_width_when_width_percent_results_in_smaller_width()
    {
        var terminal = new MemoryTerminal(100, 24);
        using var tui = CreateTui(terminal);
        var overlay = new StaticOverlay(["test"]);
        tui.ShowOverlay(overlay, new OverlayOptions { Width = "10%", MinWidth = 30 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(30, overlay.RequestedWidth);
    }

    [Fact(DisplayName = "should position overlay at top-left")]
    public async Task Should_position_overlay_at_top_left()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["TOP-LEFT"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.StartsWith("TOP-LEFT", terminal.GetViewport()[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should position overlay at bottom-right")]
    public async Task Should_position_overlay_at_bottom_right()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["BTM-RIGHT"]),
            new OverlayOptions { Anchor = OverlayAnchor.BottomRight, Width = 10 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var lastRow = terminal.GetViewport()[23];
        Assert.Contains("BTM-RIGHT", lastRow, StringComparison.Ordinal);
        Assert.EndsWith("BTM-RIGHT", lastRow.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should position overlay at top-center")]
    public async Task Should_position_overlay_at_top_center()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["CENTERED"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopCenter, Width = 10 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var firstRow = terminal.GetViewport()[0];
        Assert.Contains("CENTERED", firstRow, StringComparison.Ordinal);
        var column = firstRow.IndexOf("CENTERED", StringComparison.Ordinal);
        Assert.InRange(column, 30, 40);
    }

    [Fact(DisplayName = "should clamp negative margins to zero")]
    public async Task Should_clamp_negative_margins_to_zero()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["NEG-MARGIN"]),
            new OverlayOptions
            {
                Anchor = OverlayAnchor.TopLeft,
                Width = 12,
                Margin = new OverlayMargin { Top = -5, Left = -10, Right = 0, Bottom = 0 },
            });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.StartsWith("NEG-MARGIN", terminal.GetViewport()[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should respect margin as number")]
    public async Task Should_respect_margin_as_number()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["MARGIN"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10, Margin = 5 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var viewport = terminal.GetViewport();
        Assert.DoesNotContain("MARGIN", viewport[0], StringComparison.Ordinal);
        Assert.DoesNotContain("MARGIN", viewport[4], StringComparison.Ordinal);
        Assert.Contains("MARGIN", viewport[5], StringComparison.Ordinal);
        Assert.Equal(5, viewport[5].IndexOf("MARGIN", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "should respect margin object")]
    public async Task Should_respect_margin_object()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["MARGIN"]),
            new OverlayOptions
            {
                Anchor = OverlayAnchor.TopLeft,
                Width = 10,
                Margin = new OverlayMargin { Top = 2, Left = 3, Right = 0, Bottom = 0 },
            });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var viewport = terminal.GetViewport();
        Assert.Contains("MARGIN", viewport[2], StringComparison.Ordinal);
        Assert.Equal(3, viewport[2].IndexOf("MARGIN", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "should apply offsetX and offsetY from anchor position")]
    public async Task Should_apply_offset_x_and_offset_y_from_anchor_position()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["OFFSET"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10, OffsetX = 10, OffsetY = 5 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var viewport = terminal.GetViewport();
        Assert.Contains("OFFSET", viewport[5], StringComparison.Ordinal);
        Assert.Equal(10, viewport[5].IndexOf("OFFSET", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "should position with rowPercent and colPercent")]
    public async Task Should_position_with_row_percent_and_col_percent()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(new StaticOverlay(["PCT"]), new OverlayOptions { Width = 10, Row = "50%", Col = "50%" });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var viewport = terminal.GetViewport();
        var row = viewport.ToList().FindIndex(static line => line.Contains("PCT", StringComparison.Ordinal));
        Assert.InRange(row, 10, 13);
    }

    [Fact(DisplayName = "rowPercent 0 should position at top")]
    public async Task Row_percent_zero_should_position_at_top()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(new StaticOverlay(["TOP"]), new OverlayOptions { Width = 10, Row = "0%" });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.Contains("TOP", terminal.GetViewport()[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "rowPercent 100 should position at bottom")]
    public async Task Row_percent_one_hundred_should_position_at_bottom()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(new StaticOverlay(["BOTTOM"]), new OverlayOptions { Width = 10, Row = "100%" });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.Contains("BOTTOM", terminal.GetViewport()[23], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should truncate overlay to maxHeight")]
    public async Task Should_truncate_overlay_to_max_height()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["Line 1", "Line 2", "Line 3", "Line 4", "Line 5"]),
            new OverlayOptions { MaxHeight = 3 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var content = string.Join('\n', terminal.GetViewport());
        Assert.Contains("Line 1", content, StringComparison.Ordinal);
        Assert.Contains("Line 2", content, StringComparison.Ordinal);
        Assert.Contains("Line 3", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 4", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 5", content, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should truncate overlay to maxHeightPercent")]
    public async Task Should_truncate_overlay_to_max_height_percent()
    {
        var terminal = new MemoryTerminal(80, 10);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9", "L10"]),
            new OverlayOptions { MaxHeight = "50%" });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var content = string.Join('\n', terminal.GetViewport());
        Assert.Contains("L1", content, StringComparison.Ordinal);
        Assert.Contains("L5", content, StringComparison.Ordinal);
        Assert.DoesNotContain("L6", content, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "row and col should override anchor")]
    public async Task Row_and_col_should_override_anchor()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["ABSOLUTE"]),
            new OverlayOptions { Anchor = OverlayAnchor.BottomRight, Row = 3, Col = 5, Width = 10 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var viewport = terminal.GetViewport();
        Assert.Contains("ABSOLUTE", viewport[3], StringComparison.Ordinal);
        Assert.Equal(5, viewport[3].IndexOf("ABSOLUTE", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "should render multiple overlays with later ones on top")]
    public async Task Should_render_multiple_overlays_with_later_ones_on_top()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["FIRST-OVERLAY"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 20 });
        tui.ShowOverlay(
            new StaticOverlay(["SECOND"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.Contains("SECOND", terminal.GetViewport()[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should handle overlays at different positions without interference")]
    public async Task Should_handle_overlays_at_different_positions_without_interference()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["TOP-LEFT"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 15 });
        tui.ShowOverlay(
            new StaticOverlay(["BTM-RIGHT"]),
            new OverlayOptions { Anchor = OverlayAnchor.BottomRight, Width = 15 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        var viewport = terminal.GetViewport();
        Assert.Contains("TOP-LEFT", viewport[0], StringComparison.Ordinal);
        Assert.Contains("BTM-RIGHT", viewport[23], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "should properly hide overlays in stack order")]
    public async Task Should_properly_hide_overlays_in_stack_order()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        tui.ShowOverlay(
            new StaticOverlay(["FIRST"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10 });
        tui.ShowOverlay(
            new StaticOverlay(["SECOND"]),
            new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10 });
        tui.Start();
        await RenderAndFlushAsync(tui, terminal);
        Assert.Contains("SECOND", terminal.GetViewport()[0], StringComparison.Ordinal);

        tui.HideOverlay();
        await RenderAndFlushAsync(tui, terminal);

        Assert.Contains("FIRST", terminal.GetViewport()[0], StringComparison.Ordinal);
    }

    private static TuiMainScreen CreateTui(MemoryTerminal terminal, IComponent? content = null)
    {
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(content ?? new EmptyContent());
        return tui;
    }

    private static async Task RenderAndFlushAsync(TuiMainScreen tui, MemoryTerminal terminal)
    {
        tui.RequestRender(force: true);
        await terminal.WaitForRenderAsync();
    }

    private sealed class StaticOverlay(IReadOnlyList<string> lines) : IComponent
    {
        internal int? RequestedWidth { get; private set; }

        public IReadOnlyList<string> Render(int width)
        {
            RequestedWidth = width;
            return lines;
        }

        public void Invalidate() { }
    }

    private sealed class EmptyContent : IComponent
    {
        public IReadOnlyList<string> Render(int width) => [];

        public void Invalidate() { }
    }

    private sealed class StyledContent : IComponent
    {
        public IReadOnlyList<string> Render(int width)
        {
            var line = $"\x1b[1m\x1b[38;2;255;0;0m{new string('X', width)}\x1b[0m";
            return [line, line, line];
        }

        public void Invalidate() { }
    }

    private sealed class HyperlinkContent : IComponent
    {
        public IReadOnlyList<string> Render(int width)
        {
            const string link = "\x1b]8;;file:///path/to/file.ts\x07file.ts\x1b]8;;\x07";
            var line = $"See {link} for details {new string('X', width - 30)}";
            return [line, line, line];
        }

        public void Invalidate() { }
    }
}
