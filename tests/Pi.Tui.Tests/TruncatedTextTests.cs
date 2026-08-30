using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream TruncatedText component coverage.</summary>
public sealed class TruncatedTextTests
{
    [Fact(DisplayName = "pads output lines to exactly match width")]
    public void PadsOutputLinesToWidth()
    {
        var text = new TruncatedText("Hello world", paddingX: 1, paddingY: 0);
        var lines = text.Render(50);

        Assert.Single(lines);
        Assert.Equal(50, TextMeasurement.VisibleWidth(lines[0]));
    }

    [Fact(DisplayName = "pads output with vertical padding lines to width")]
    public void PadsVerticalLinesToWidth()
    {
        var text = new TruncatedText("Hello", paddingX: 0, paddingY: 2);
        var lines = text.Render(40);

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Equal(40, TextMeasurement.VisibleWidth(line)));
    }

    [Fact(DisplayName = "truncates long text and pads to width")]
    public void TruncatesLongTextAndPadsToWidth()
    {
        const string longText = "This is a very long piece of text that will definitely exceed the available width";
        var text = new TruncatedText(longText, paddingX: 1, paddingY: 0);
        var lines = text.Render(30);

        Assert.Single(lines);
        Assert.Equal(30, TextMeasurement.VisibleWidth(lines[0]));
        Assert.Contains("...", TextMeasurement.StripTerminalSequences(lines[0]));
    }

    [Fact(DisplayName = "preserves ANSI codes in output and pads correctly")]
    public void PreservesAnsiCodesAndPadsCorrectly()
    {
        var styledText = "\x1b[31mHello\x1b[0m \x1b[34mworld\x1b[0m";
        var text = new TruncatedText(styledText, paddingX: 1, paddingY: 0);
        var lines = text.Render(40);

        Assert.Single(lines);
        Assert.Equal(40, TextMeasurement.VisibleWidth(lines[0]));
        Assert.Contains("\x1b[", lines[0]);
    }

    [Fact(DisplayName = "truncates styled text and adds reset code before ellipsis")]
    public void TruncatesStyledTextWithResetBeforeEllipsis()
    {
        var text = new TruncatedText(
            $"\x1b[31mThis is a very long red text that will be truncated\x1b[0m",
            paddingX: 1,
            paddingY: 0);
        var lines = text.Render(20);

        Assert.Single(lines);
        Assert.Equal(20, TextMeasurement.VisibleWidth(lines[0]));
        Assert.Contains("\x1b[0m...", lines[0]);
    }

    [Fact(DisplayName = "handles text that fits exactly")]
    public void HandlesTextThatFits()
    {
        var text = new TruncatedText("Hello world", paddingX: 1, paddingY: 0);
        var lines = text.Render(30);

        Assert.Single(lines);
        Assert.Equal(30, TextMeasurement.VisibleWidth(lines[0]));
        Assert.DoesNotContain("...", TextMeasurement.StripTerminalSequences(lines[0]));
    }

    [Fact(DisplayName = "handles empty text")]
    public void HandlesEmptyText()
    {
        var text = new TruncatedText(string.Empty, paddingX: 1, paddingY: 0);
        var lines = text.Render(30);

        Assert.Single(lines);
        Assert.Equal(30, TextMeasurement.VisibleWidth(lines[0]));
    }

    [Fact(DisplayName = "stops at newline and only shows first line")]
    public void StopsAtNewline()
    {
        var text = new TruncatedText("First line\nSecond line\nThird line", paddingX: 1, paddingY: 0);
        var lines = text.Render(40);
        var stripped = TextMeasurement.StripTerminalSequences(lines[0]).Trim();

        Assert.Single(lines);
        Assert.Equal(40, TextMeasurement.VisibleWidth(lines[0]));
        Assert.Contains("First line", stripped);
        Assert.DoesNotContain("Second line", stripped);
        Assert.DoesNotContain("Third line", stripped);
    }

    [Fact(DisplayName = "truncates first line even with newlines in text")]
    public void TruncatesFirstLineWithNewlines()
    {
        var text = new TruncatedText(
            "This is a very long first line that needs truncation\nSecond line",
            paddingX: 1,
            paddingY: 0);
        var lines = text.Render(25);
        var stripped = TextMeasurement.StripTerminalSequences(lines[0]);

        Assert.Single(lines);
        Assert.Equal(25, TextMeasurement.VisibleWidth(lines[0]));
        Assert.Contains("...", stripped);
        Assert.DoesNotContain("Second line", stripped);
    }
}
