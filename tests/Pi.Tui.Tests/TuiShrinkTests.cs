using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class TuiShrinkTests
{
    [Fact(DisplayName = "clears all rendered lines when content shrinks to zero")]
    public async Task Clears_all_rendered_lines_when_content_shrinks_to_zero()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        tui.AddChild(new StaticLines(["first", "second", "third"]));
        tui.Start();
        await terminal.WaitForRenderAsync();
        Assert.Contains(terminal.GetViewport(), static line => line.Contains("first", StringComparison.Ordinal));
        Assert.Contains(terminal.GetViewport(), static line => line.Contains("second", StringComparison.Ordinal));
        Assert.Contains(terminal.GetViewport(), static line => line.Contains("third", StringComparison.Ordinal));

        tui.Clear();
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        Assert.DoesNotContain(terminal.GetViewport(), static line => line.Contains("first", StringComparison.Ordinal));
        Assert.DoesNotContain(terminal.GetViewport(), static line => line.Contains("second", StringComparison.Ordinal));
        Assert.DoesNotContain(terminal.GetViewport(), static line => line.Contains("third", StringComparison.Ordinal));
    }

    private sealed class StaticLines(IReadOnlyList<string> lines) : IComponent
    {
        public IReadOnlyList<string> Render(int width) => lines;

        public void Invalidate() { }
    }
}
