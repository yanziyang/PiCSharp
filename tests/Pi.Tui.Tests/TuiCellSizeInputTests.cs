using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class TuiCellSizeInputTests
{
    [Fact(DisplayName = "forwards bare escape even when a cell size query was sent at startup")]
    public void Forwards_bare_escape_even_when_a_cell_size_query_was_sent_at_startup()
    {
        var seam = CreateImageSeam();
        var terminal = new MemoryTerminal(80, 24);
        using var tui = new TuiMainScreen(terminal, imageSeam: seam);
        var recorder = new InputRecorder();
        tui.SetFocus(recorder);
        tui.Start();

        terminal.SendInput("\x1b");

        Assert.Equal(["\x1b"], recorder.Inputs);
    }

    [Fact(DisplayName = "consumes cell size responses and still forwards later user input")]
    public void Consumes_cell_size_responses_and_still_forwards_later_user_input()
    {
        var seam = CreateImageSeam();
        seam.SetCellDimensions(new CellDimensions(9, 18));
        var terminal = new MemoryTerminal(80, 24);
        using var tui = new TuiMainScreen(terminal, imageSeam: seam);
        var recorder = new InputRecorder();
        tui.SetFocus(recorder);
        tui.Start();

        terminal.SendInput("\x1b[6;20;10t");
        Assert.Empty(recorder.Inputs);
        Assert.Equal(new CellDimensions(10, 20), seam.GetCellDimensions());

        terminal.SendInput("q");

        Assert.Equal(["q"], recorder.Inputs);
    }

    private static NoImageTerminalImageSeam CreateImageSeam()
    {
        var seam = new NoImageTerminalImageSeam();
        seam.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        return seam;
    }

    private sealed class InputRecorder : IComponent
    {
        internal List<string> Inputs { get; } = [];

        public IReadOnlyList<string> Render(int width) => [string.Empty];

        public void HandleInput(string data) => Inputs.Add(data);

        public void Invalidate() { }
    }
}
