using Pi.Tui;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Additional core-surface checks for focus, overlays, and terminal queries.</summary>
public sealed class TuiCoreTests
{
    [Fact]
    public void Optional_component_members_have_upstream_defaults()
    {
        IComponent component = new MinimalComponent();

        component.HandleInput("ignored");

        Assert.False(component.WantsKeyRelease);
    }

    [Fact]
    public void Overlay_focus_and_lifecycle_restore_the_previous_component()
    {
        var terminal = new MemoryTerminal(20, 5);
        using var tui = new TestTui(terminal);
        var baseComponent = new FocusComponent(["base"]);
        var overlay = new FocusComponent(["overlay"]);
        tui.AddChild(baseComponent);
        tui.SetFocus(baseComponent);

        var handle = tui.ShowOverlay(overlay, new OverlayOptions
        {
            Width = 8,
            Row = 0,
            Col = 2,
        });

        Assert.True(handle.IsFocused());
        Assert.True(tui.HasOverlay());
        tui.RenderNow();
        Assert.Contains("overlay", terminal.GetViewport()[0], StringComparison.Ordinal);

        handle.Unfocus();
        Assert.Same(baseComponent, tui.GetFocusedComponent());
        handle.SetHidden(true);
        Assert.False(tui.HasOverlay());
        handle.Hide();
        Assert.False(tui.HasOverlay());
    }

    [Fact]
    public void Key_release_input_is_filtered_unless_component_requests_it()
    {
        var terminal = new MemoryTerminal(20, 5);
        using var tui = new TestTui(terminal);
        var component = new FocusComponent([]);
        tui.AddChild(component);
        tui.SetFocus(component);
        tui.Start();

        terminal.SendInput("\x1b[97;1:3u");
        Assert.Empty(component.Input);

        component.WantsKeyReleaseValue = true;
        terminal.SendInput("\x1b[97;1:3u");
        Assert.Equal(["\x1b[97;1:3u"], component.Input);
    }

    [Fact]
    public async Task Background_query_resolves_from_an_osc11_response()
    {
        var terminal = new MemoryTerminal(20, 5);
        using var tui = new TestTui(terminal);
        tui.Start();
        var query = tui.QueryTerminalBackgroundColor(1000);
        terminal.SendInput("\x1b]11;#102030\x07");

        Assert.Equal(new RgbColor(16, 32, 48), await query);
    }

    [Fact]
    public async Task Color_scheme_query_resolves_from_a_terminal_report()
    {
        var terminal = new MemoryTerminal(20, 5);
        using var tui = new TestTui(terminal);
        tui.Start();
        var query = tui.QueryTerminalColorScheme(1000);
        terminal.SendInput("\x1b[?997;2n");

        Assert.Equal(TerminalColorScheme.Light, await query);
    }

    [Fact]
    public void Cell_size_response_is_forwarded_through_the_image_seam()
    {
        var terminal = new MemoryTerminal(20, 5);
        var seam = new TestImageSeam();
        using var tui = new TestTui(terminal, imageSeam: seam);
        var component = new TestComponent { Lines = ["content"] };
        tui.AddChild(component);
        tui.Start();

        terminal.SendInput("\x1b[6;18;9t");

        Assert.Equal(new CellDimensions(9, 18), seam.Dimensions);
        Assert.True(component.InvalidationCount > 0);
    }

    private sealed class MinimalComponent : IComponent
    {
        public IReadOnlyList<string> Render(int width) => [];

        public void Invalidate() { }
    }

    private sealed class FocusComponent(IReadOnlyList<string> lines) : IComponent, IFocusable
    {
        public List<string> Input { get; } = [];

        public bool Focused { get; set; }

        public bool WantsKeyReleaseValue { get; set; }

        public bool WantsKeyRelease => WantsKeyReleaseValue;

        public IReadOnlyList<string> Render(int width) => lines;

        public void HandleInput(string data) => Input.Add(data);

        public void Invalidate() { }
    }

    private sealed class TestImageSeam : ITerminalImageSeam
    {
        private TerminalCapabilities _capabilities = new(ImageProtocol.Kitty, true, true);

        public CellDimensions Dimensions { get; private set; } = new(9, 18);

        public TerminalCapabilities GetCapabilities() => _capabilities;

        public void SetCellDimensions(CellDimensions dimensions) => Dimensions = dimensions;

        public CellDimensions GetCellDimensions() => Dimensions;

        public void ResetCapabilitiesCache() => _capabilities = new(null, false, false);

        public void SetCapabilities(TerminalCapabilities capabilities) => _capabilities = capabilities;
    }
}
