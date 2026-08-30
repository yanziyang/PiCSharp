using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class TabWidthTests
{
    [Fact(DisplayName = "keeps slice helper widths consistent with visible width")]
    public void Keeps_slice_helper_widths_consistent_with_visible_width()
    {
        const string text = "out 192M\t.pi/skill-tests/results-ha";
        var slice = TextMeasurement.SliceWithWidth(text, 0, 10, strict: true);

        Assert.Equal("out 192M", slice.Text);
        Assert.Equal(8, slice.Width);
        Assert.Equal(TextMeasurement.VisibleWidth(slice.Text), slice.Width);
    }

    [Fact(DisplayName = "keeps overlay segment widths consistent with visible width")]
    public void Keeps_overlay_segment_widths_consistent_with_visible_width()
    {
        const string text = "out 192M\t.pi/skill-tests/results-ha";
        var segments = TextMeasurement.ExtractSegments(text, 10, 13, 10, strictAfter: true);

        Assert.Equal("out 192M", segments.Before);
        Assert.Equal(8, segments.BeforeWidth);
        Assert.Equal(TextMeasurement.VisibleWidth(segments.Before), segments.BeforeWidth);

        var tabFits = TextMeasurement.ExtractSegments(text, 11, 13, 10, strictAfter: true);
        Assert.Equal("out 192M\t", tabFits.Before);
        Assert.Equal(11, tabFits.BeforeWidth);
        Assert.Equal(TextMeasurement.VisibleWidth(tabFits.Before), tabFits.BeforeWidth);
    }

    [Fact(DisplayName = "keeps tabs inside terminal control sequences byte-identical")]
    public void Keeps_tabs_inside_terminal_control_sequences_byte_identical()
    {
        string[] controlSequences =
        [
            "\x1b]8;;https://example.test/a\tb\x07",
            "\x1b]0;window\ttitle\x1b\\",
            "\x1b_payload\tdata\x1b\\",
        ];

        foreach (var controlSequence in controlSequences)
        {
            Assert.Equal(
                $"{controlSequence}label   text",
                TextMeasurement.NormalizeTerminalOutput($"{controlSequence}label\ttext"));
        }
    }

    [Fact(DisplayName = "keeps tab-containing overlays on one physical terminal row")]
    public async Task Keeps_tab_containing_overlays_on_one_physical_terminal_row()
    {
        var terminal = new MemoryTerminal(16, 3);
        using var tui = new TuiMainScreen(terminal);
        tui.AddChild(new FullViewportContent());
        tui.ShowOverlay(new TabStatusOverlay(), new OverlayOptions { Width = 4, Row = 1, Col = 4 });
        tui.Start();

        await terminal.WaitForRenderAsync();

        var viewport = terminal.GetViewport();
        Assert.Equal("base 0          ", viewport[0]);
        Assert.Equal("base   X        ", viewport[1]);
        Assert.Equal("base 2          ", viewport[2]);
        Assert.DoesNotContain("\t", string.Concat(terminal.Writes), StringComparison.Ordinal);
    }

    private sealed class FullViewportContent : IComponent
    {
        private static readonly string[] _lines = ["base 0", "base 1", "base 2"];

        public IReadOnlyList<string> Render(int width) =>
            _lines.Select(line => line.PadRight(width)).ToArray();

        public void Invalidate() { }
    }

    private sealed class TabStatusOverlay : IComponent
    {
        public IReadOnlyList<string> Render(int width) => ["\tX"];

        public void Invalidate() { }
    }
}
