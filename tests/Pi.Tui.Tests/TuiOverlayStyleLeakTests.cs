using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class TuiOverlayStyleLeakTests
{
    [Fact(DisplayName = "should not leak styles when a trailing reset sits beyond the last visible column (no overlay)")]
    public async Task Should_not_leak_styles_when_a_trailing_reset_sits_beyond_the_last_visible_column_no_overlay()
    {
        const int width = 20;
        var baseLine = $"\x1b[3m{new string('X', width)}\x1b[23m";
        var terminal = new MemoryTerminal(width, 6);
        using var tui = new TuiMainScreen(terminal);
        tui.AddChild(new StaticLines([baseLine, "INPUT"]));
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.False(terminal.GetCellItalic(1, 0));
    }

    [Fact(DisplayName = "should not leak styles when overlay slicing drops trailing SGR resets")]
    public async Task Should_not_leak_styles_when_overlay_slicing_drops_trailing_sgr_resets()
    {
        const int width = 20;
        var baseLine = $"\x1b[3m{new string('X', width)}\x1b[23m";
        var terminal = new MemoryTerminal(width, 6);
        using var tui = new TuiMainScreen(terminal);
        tui.AddChild(new StaticLines([baseLine, "INPUT"]));
        tui.ShowOverlay(new StaticLines(["OVR"]), new OverlayOptions { Row = 0, Col = 5, Width = 3 });
        tui.Start();

        await RenderAndFlushAsync(tui, terminal);

        Assert.False(terminal.GetCellItalic(1, 0));
    }

    private static async Task RenderAndFlushAsync(TuiMainScreen tui, MemoryTerminal terminal)
    {
        tui.RequestRender(force: true);
        await terminal.WaitForRenderAsync();
    }

    private sealed class StaticLines(IReadOnlyList<string> lines) : IComponent
    {
        public IReadOnlyList<string> Render(int width) => lines;

        public void Invalidate() { }
    }
}
