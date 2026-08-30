using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the strict terminal color-report parser cases.</summary>
public sealed class TerminalColorsTests
{
    [Fact(DisplayName = "parses 16-bit OSC 11 rgb responses")]
    public void ParsesOsc11RgbResponses()
    {
        Assert.Equal(
            new RgbColor(0, 128, 255),
            TerminalColors.ParseOsc11BackgroundColor("\x1b]11;rgb:0000/8000/ffff\x07"));
    }

    [Fact(DisplayName = "parses OSC 11 hex responses")]
    public void ParsesOsc11HexResponses()
    {
        Assert.Equal(new RgbColor(255, 255, 255), TerminalColors.ParseOsc11BackgroundColor("\x1b]11;#ffffff\x1b\\"));
        Assert.Equal(new RgbColor(0, 0, 0), TerminalColors.ParseOsc11BackgroundColor("\x1b]11;#000000\x07"));
    }

    [Fact(DisplayName = "rejects non-strict OSC 11 responses")]
    public void RejectsNonStrictOsc11Responses()
    {
        Assert.Null(TerminalColors.ParseOsc11BackgroundColor("x\x1b]11;#ffffff\x07"));
        Assert.Null(TerminalColors.ParseOsc11BackgroundColor("\x1b]10;#ffffff\x07"));
        Assert.Null(TerminalColors.ParseOsc11BackgroundColor("\x1b]11;#ffffff\x07x"));
    }

    [Fact(DisplayName = "parses terminal color scheme reports")]
    public void ParsesColorSchemeReports()
    {
        Assert.Equal(TerminalColorScheme.Dark, TerminalColors.ParseTerminalColorSchemeReport("\x1b[?997;1n"));
        Assert.Equal(TerminalColorScheme.Light, TerminalColors.ParseTerminalColorSchemeReport("\x1b[?997;2n"));
        Assert.Equal(
            TerminalColorScheme.Dark,
            TerminalColors.ParseTerminalColorSchemeReport("\x1b[?997;2n\x1b[?997;1n\x1b[?997;1n"));
        Assert.Equal(
            TerminalColorScheme.Light,
            TerminalColors.ParseTerminalColorSchemeReport("\x1b[?997;1n\x1b[?997;2n\x1b[?997;2n"));
        Assert.Null(TerminalColors.ParseTerminalColorSchemeReport("\x1b[?997;3n"));
        Assert.Null(TerminalColors.ParseTerminalColorSchemeReport("\x1b[?996n"));
        Assert.Null(TerminalColors.ParseTerminalColorSchemeReport("x\x1b[?997;1n"));
    }
}
