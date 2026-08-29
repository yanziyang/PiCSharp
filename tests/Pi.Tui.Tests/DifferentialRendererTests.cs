using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Tests the renderer's byte-oriented terminal update contract.</summary>
public sealed class DifferentialRendererTests
{
    [Fact]
    public void TerminalOutputWriterFlushesOnlyNonEmptyBoundedChunks()
    {
        var writes = new List<string>();
        var writer = new TerminalOutputWriter(writes.Add);

        writer.Append("first");
        Assert.Empty(writes);
        writer.Flush();
        writer.Flush();

        Assert.Equal(["first"], writes);
        Assert.Equal(5, writer.Length);
    }

    [Fact]
    public void TerminalOutputWriterDoesNotSplitSurrogatePairsAtChunkBoundary()
    {
        var writes = new List<string>();
        var writer = new TerminalOutputWriter(writes.Add);
        var prefix = new string('a', TerminalOutputWriter.MaxWriteCharacters - 1);

        writer.Append(prefix + "😀z");
        writer.Flush();

        Assert.Equal(2, writes.Count);
        Assert.Equal(TerminalOutputWriter.MaxWriteCharacters - 1, writes[0].Length);
        Assert.False(char.IsHighSurrogate(writes[0][^1]));
        Assert.Equal("😀z", writes[1]);
        Assert.Equal(prefix.Length + "😀z".Length, writer.Length);
    }

    [Fact]
    public void InitialRenderWritesTheLineBufferWithoutClearingMainScreen()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);

        var result = renderer.Render(["one", "two"], 20, 4);

        Assert.True(result.FullRedraw);
        Assert.Equal(1, renderer.FullRedrawCount);
        Assert.Equal(["one", "two"], result.Lines);
        Assert.Equal("\x1b[?2026hone\r\ntwo\x1b[?2026l\x1b[?25l", string.Concat(writes));
        Assert.DoesNotContain("\x1b[2J", string.Concat(writes));
    }

    [Fact]
    public void SingleLineEditUsesDifferentialUpdate()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);
        renderer.Render(["one", "two", "three"], 20, 3);
        writes.Clear();

        var result = renderer.Render(["one", "TWO", "three"], 20, 3);
        var output = string.Concat(writes);

        Assert.False(result.FullRedraw);
        Assert.Equal(1, result.FirstChangedLine);
        Assert.Equal(1, result.LastChangedLine);
        Assert.Contains("\x1b[?2026h", output);
        Assert.Contains("\x1b[2KTWO", output);
        Assert.DoesNotContain("\x1b[2J", output);
        Assert.True(result.OutputCharacters < 50);
    }

    [Fact]
    public void WidthChangePerformsClearingFullRedraw()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);
        renderer.Render(["one"], 20, 3);
        writes.Clear();

        var result = renderer.Render(["one"], 21, 3);

        Assert.True(result.FullRedraw);
        Assert.Equal(2, renderer.FullRedrawCount);
        Assert.Contains("\x1b[2J\x1b[H\x1b[3J", string.Concat(writes));
    }

    [Fact]
    public void ContentShrinkClearsStaleRowsWithoutFullRedraw()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);
        renderer.Render(["one", "two", "three"], 20, 3);
        writes.Clear();

        var result = renderer.Render(["one"], 20, 3);
        var output = string.Concat(writes);

        Assert.False(result.FullRedraw);
        Assert.Equal(1, result.FirstChangedLine);
        Assert.Equal(2, result.LastChangedLine);
        Assert.Contains("\x1b[2K", output);
        Assert.DoesNotContain("\x1b[2J", output);
        Assert.Equal(["one"], renderer.PreviousLines);
    }

    [Fact]
    public void CursorMarkerIsRemovedAndHardwareCursorIsPositioned()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);

        var result = renderer.Render(
            [$"hello{TuiConstants.CursorMarker} world"],
            20,
            3,
            new DifferentialRenderOptions { ShowHardwareCursor = true });
        var output = string.Concat(writes);

        Assert.Equal(new CursorPosition(0, 5), result.Cursor);
        Assert.Equal(["hello world"], result.Lines);
        Assert.DoesNotContain(TuiConstants.CursorMarker, output);
        Assert.Contains("\x1b[6G\x1b[?25h", output);
    }

    [Fact]
    public void NoOpFrameOnlyUpdatesCursorState()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);
        var lines = new[] { $"x{TuiConstants.CursorMarker}" };
        renderer.Render(lines, 20, 2, new DifferentialRenderOptions { ShowHardwareCursor = true });
        writes.Clear();

        var result = renderer.Render(lines, 20, 2, new DifferentialRenderOptions { ShowHardwareCursor = true });

        Assert.False(result.FullRedraw);
        Assert.Equal(-1, result.FirstChangedLine);
        Assert.Equal(-1, result.LastChangedLine);
        Assert.Equal("\x1b[2G\x1b[?25h", string.Concat(writes));
    }

    [Fact]
    public void ResetForcesTheNextRenderToClear()
    {
        var writes = new List<string>();
        var renderer = new DifferentialRenderer(writes.Add);
        renderer.Render(["one"], 20, 2);
        renderer.Reset();
        writes.Clear();

        var result = renderer.Render(["one"], 20, 2);

        Assert.True(result.FullRedraw);
        Assert.Contains("\x1b[2J", string.Concat(writes));
    }
}
