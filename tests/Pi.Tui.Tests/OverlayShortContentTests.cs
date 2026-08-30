using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class OverlayShortContentTests
{
    [Fact(DisplayName = "should render overlay when content is shorter than terminal height")]
    public async Task Should_render_overlay_when_content_is_shorter_than_terminal_height()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = new TuiMainScreen(terminal);
        tui.AddChild(new StaticLines(["Line 1", "Line 2", "Line 3"]));
        tui.ShowOverlay(new StaticLines(["OVERLAY_TOP", "OVERLAY_MID", "OVERLAY_BOT"]));
        tui.Start();

        await terminal.WaitForRenderAsync();

        Assert.Contains(terminal.GetViewport(), static line => line.Contains("OVERLAY", StringComparison.Ordinal));
    }

    private sealed class StaticLines(IReadOnlyList<string> lines) : IComponent
    {
        public IReadOnlyList<string> Render(int width) => lines;

        public void Invalidate() { }
    }
}
