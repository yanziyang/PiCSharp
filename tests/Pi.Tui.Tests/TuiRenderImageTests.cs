using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Exercises Kitty image placement in the real TUI renderer.</summary>
public sealed class TuiRenderImageTests
{
    [Fact(DisplayName = "reserves Kitty image rows before drawing during full redraw fallbacks")]
    public async Task Reserves_kitty_image_rows_before_drawing_during_full_redraw_fallbacks()
    {
        var seam = CreateKittySeam();
        var terminal = new MemoryTerminal(40, 5);
        using var tui = new TuiMainScreen(terminal, imageSeam: seam);
        var component = new TestComponent();
        tui.AddChild(component);

        component.Lines = ["l0", "l1", "l2", "l3", "l4"];
        tui.Start();
        await terminal.WaitForRenderAsync();
        var redrawsBeforeImage = tui.FullRedraws;
        terminal.ClearWrites();

        var image = CreateImage(seam, maxWidthCells: 3, widthPx: 30, heightPx: 30);
        var imageLines = image.Render(40);
        var imageSequence = imageLines[0];
        component.Lines = ["l0", "l1", "l2", "l3", "l4", .. imageLines, "after"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var writes = string.Concat(terminal.Writes);
        Assert.True(tui.FullRedraws > redrawsBeforeImage);
        Assert.Contains($"\r\n\r\n\x1b[2A{imageSequence}\x1b[2B", writes, StringComparison.Ordinal);
        Assert.DoesNotContain($"{imageSequence}\r\n\x1b[0m", writes, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "clears reserved Kitty image rows before drawing appended image placements")]
    public async Task Clears_reserved_kitty_image_rows_before_drawing_appended_image_placements()
    {
        var seam = CreateKittySeam();
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal, imageSeam: seam);
        var component = new TestComponent();
        tui.AddChild(component);

        component.Lines = ["before"];
        tui.Start();
        await terminal.WaitForRenderAsync();
        terminal.ClearWrites();

        var image = CreateImage(seam, maxWidthCells: 2, widthPx: 20, heightPx: 20);
        var imageLines = image.Render(40);
        var imageSequence = imageLines[0];
        component.Lines = ["before", .. imageLines, "after"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var writes = string.Concat(terminal.Writes);
        Assert.Contains($"\x1b[2K\r\n\x1b[2K\x1b[1A{imageSequence}\x1b[1B", writes, StringComparison.Ordinal);
        Assert.DoesNotContain($"{imageSequence}\r\n\x1b[2K", writes, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "redraws image lines when an earlier reserved image row changes")]
    public async Task Redraws_image_lines_when_an_earlier_reserved_image_row_changes()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent();
        tui.AddChild(component);

        var image = TerminalImage.EncodeKitty(
            "AAAA",
            new KittyEncodeOptions { Columns = 2, Rows = 2, ImageId = 88, MoveCursor = false });
        component.Lines = [string.Empty, image];
        tui.Start();
        await terminal.WaitForRenderAsync();
        terminal.ClearWrites();

        component.Lines = ["covered", image];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var writes = string.Concat(terminal.Writes);
        var deleteIndex = writes.IndexOf(TerminalImage.DeleteKittyImage(88), StringComparison.Ordinal);
        var drawIndex = writes.IndexOf(image, StringComparison.Ordinal);
        Assert.True(deleteIndex >= 0);
        Assert.True(drawIndex >= 0);
        Assert.True(deleteIndex < drawIndex);
        Assert.DoesNotContain("\x1b[2J", writes, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "deletes previously rendered image ids during full redraws")]
    public async Task Deletes_previously_rendered_image_ids_during_full_redraws()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent();
        tui.AddChild(component);

        component.Lines =
        [
            TerminalImage.EncodeKitty(
                "AAAA",
                new KittyEncodeOptions { Columns = 2, Rows = 2, ImageId = 77, MoveCursor = false }),
        ];
        tui.Start();
        await terminal.WaitForRenderAsync();
        terminal.ClearWrites();

        component.Lines = ["plain text"];
        terminal.Resize(41, 10);
        await terminal.WaitForRenderAsync();

        var writes = string.Concat(terminal.Writes);
        var deleteIndex = writes.IndexOf(TerminalImage.DeleteKittyImage(77), StringComparison.Ordinal);
        var clearIndex = writes.IndexOf("\x1b[2J", StringComparison.Ordinal);
        Assert.True(deleteIndex >= 0, writes.Replace("\x1b", "<ESC>", StringComparison.Ordinal));
        Assert.True(clearIndex >= 0);
        Assert.True(deleteIndex < clearIndex);
    }

    [Fact(DisplayName = "deletes changed image ids before drawing moved placements")]
    public async Task Deletes_changed_image_ids_before_drawing_moved_placements()
    {
        var terminal = new MemoryTerminal(40, 10);
        using var tui = new TuiMainScreen(terminal);
        var component = new TestComponent();
        tui.AddChild(component);

        var oldImage = TerminalImage.EncodeKitty(
            "AAAA",
            new KittyEncodeOptions { Columns = 2, Rows = 2, ImageId = 42, MoveCursor = false });
        component.Lines = ["top", oldImage];
        tui.Start();
        await terminal.WaitForRenderAsync();
        terminal.ClearWrites();

        var newImage = TerminalImage.EncodeKitty(
            "BBBB",
            new KittyEncodeOptions { Columns = 2, Rows = 1, ImageId = 42, MoveCursor = false });
        component.Lines = [newImage, string.Empty];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        var writes = string.Concat(terminal.Writes);
        var deleteIndex = writes.IndexOf(TerminalImage.DeleteKittyImage(42), StringComparison.Ordinal);
        var drawIndex = writes.IndexOf(newImage, StringComparison.Ordinal);
        Assert.True(deleteIndex >= 0);
        Assert.True(drawIndex >= 0);
        Assert.True(deleteIndex < drawIndex);
    }

    [Fact(DisplayName = "does not use cursor-up placement for Kitty images taller than the viewport")]
    public async Task Does_not_use_cursor_up_placement_for_kitty_images_taller_than_the_viewport()
    {
        var seam = CreateKittySeam();
        var terminal = new MemoryTerminal(40, 5);
        using var tui = new TuiMainScreen(terminal, imageSeam: seam);
        var component = new TestComponent();
        tui.AddChild(component);

        component.Lines = ["before"];
        tui.Start();
        await terminal.WaitForRenderAsync();
        terminal.ClearWrites();

        var image = CreateImage(seam, maxWidthCells: 6, widthPx: 60, heightPx: 60);
        var imageLines = image.Render(40);
        var imageSequence = imageLines[0];
        Assert.True(imageLines.Count > terminal.Rows);
        component.Lines = ["before", .. imageLines, "after"];
        tui.RequestRender(true);
        await terminal.WaitForRenderAsync();

        var writes = string.Concat(terminal.Writes);
        Assert.Contains(imageSequence, writes, StringComparison.Ordinal);
        Assert.DoesNotContain($"\x1b[{imageLines.Count - 1}A{imageSequence}", writes, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "falls back to full redraw when Kitty image pre-clear would scroll")]
    public async Task Falls_back_to_full_redraw_when_kitty_image_pre_clear_would_scroll()
    {
        var seam = CreateKittySeam();
        var terminal = new MemoryTerminal(40, 2);
        using var tui = new TuiMainScreen(terminal, imageSeam: seam);
        var component = new TestComponent();
        tui.AddChild(component);

        component.Lines = ["before"];
        tui.Start();
        await terminal.WaitForRenderAsync();
        var redrawsBeforeImage = tui.FullRedraws;
        terminal.ClearWrites();

        var image = CreateImage(seam, maxWidthCells: 3, widthPx: 30, heightPx: 30);
        component.Lines = ["before", .. image.Render(40), "after"];
        tui.RequestRender();
        await terminal.WaitForRenderAsync();

        Assert.True(tui.FullRedraws > redrawsBeforeImage);
        Assert.Contains("\x1b[2J", string.Concat(terminal.Writes), StringComparison.Ordinal);
    }

    private static TerminalImageSeam CreateKittySeam()
    {
        var seam = new TerminalImageSeam();
        seam.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        seam.SetCellDimensions(new CellDimensions(10, 10));
        return seam;
    }

    private static Image CreateImage(TerminalImageSeam seam, int maxWidthCells, int widthPx, int heightPx) =>
        new(
            "AAAA",
            "image/png",
            new ImageTheme { FallbackColor = static value => value },
            new ImageOptions { MaxWidthCells = maxWidthCells },
            new ImageDimensions(widthPx, heightPx),
            seam);
}
