using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream ANSI wrapping, truncation, and width regression cases.</summary>
public sealed class TextMeasurementTests
{
    [Fact(DisplayName = "should not apply underline style before the styled text")]
    public void Wrap_DoesNotApplyUnderlineBeforeStyledText()
    {
        const string underlineOn = "\x1b[4m";
        const string underlineOff = "\x1b[24m";
        var url = "https://example.com/very/long/path/that/will/wrap";

        var wrapped = TextMeasurement.WrapTextWithAnsi($"read this thread {underlineOn}{url}{underlineOff}", 40);

        Assert.Equal("read this thread", wrapped[0]);
        Assert.StartsWith(underlineOn, wrapped[1]);
        Assert.Contains("https://", wrapped[1]);
    }

    [Fact(DisplayName = "should not have whitespace before underline reset code")]
    public void Wrap_DoesNotLeaveWhitespaceBeforeUnderlineReset()
    {
        const string underlineOn = "\x1b[4m";
        const string underlineOff = "\x1b[24m";

        var wrapped = TextMeasurement.WrapTextWithAnsi($"{underlineOn}underlined text here {underlineOff}more", 18);

        Assert.DoesNotContain($" {underlineOff}", wrapped[0]);
    }

    [Fact(DisplayName = "should not bleed underline to padding - each line should end with reset for underline only")]
    public void Wrap_ResetsUnderlineWithoutResettingOtherStyles()
    {
        const string underlineOn = "\x1b[4m";
        const string underlineOff = "\x1b[24m";
        var url = "https://example.com/very/long/path/that/will/definitely/wrap";

        var wrapped = TextMeasurement.WrapTextWithAnsi($"prefix {underlineOn}{url}{underlineOff} suffix", 30);

        for (var index = 1; index < wrapped.Count - 1; index++)
        {
            if (wrapped[index].Contains(underlineOn, StringComparison.Ordinal))
            {
                Assert.EndsWith(underlineOff, wrapped[index]);
                Assert.DoesNotContain("\x1b[0m", wrapped[index]);
            }
        }
    }

    [Fact(DisplayName = "should preserve background color across wrapped lines without full reset")]
    public void Wrap_PreservesBackgroundAcrossLines()
    {
        const string background = "\x1b[44m";
        const string reset = "\x1b[0m";

        var wrapped = TextMeasurement.WrapTextWithAnsi($"{background}hello world this is blue background text{reset}", 15);

        Assert.All(wrapped, line => Assert.Contains(background, line));
        for (var index = 0; index < wrapped.Count - 1; index++)
        {
            Assert.DoesNotContain(reset, wrapped[index]);
        }
    }

    [Fact(DisplayName = "should reset underline but preserve background when wrapping underlined text inside background")]
    public void Wrap_ResetsUnderlineAndPreservesBackground()
    {
        const string underlineOn = "\x1b[4m";
        const string underlineOff = "\x1b[24m";
        const string reset = "\x1b[0m";

        var text = $"\x1b[41mprefix {underlineOn}UNDERLINED_CONTENT_THAT_WRAPS{underlineOff} suffix{reset}";
        var wrapped = TextMeasurement.WrapTextWithAnsi(text, 20);

        Assert.All(wrapped, line =>
            Assert.True(
                line.Contains("[41m", StringComparison.Ordinal) ||
                line.Contains(";41m", StringComparison.Ordinal) ||
                line.Contains("[41;", StringComparison.Ordinal)));
        for (var index = 0; index < wrapped.Count - 1; index++)
        {
            var line = wrapped[index];
            if ((line.Contains("[4m", StringComparison.Ordinal) ||
                 line.Contains("[4;", StringComparison.Ordinal) ||
                 line.Contains(";4m", StringComparison.Ordinal)) &&
                !line.Contains(underlineOff, StringComparison.Ordinal))
            {
                Assert.EndsWith(underlineOff, line);
                Assert.DoesNotContain(reset, line);
            }
        }
    }

    [Fact(DisplayName = "should handle LF, CRLF, and CR line endings")]
    public void Wrap_HandlesAllLineEndings()
    {
        Assert.Equal(
            ["first", "second", "third", "fourth"],
            TextMeasurement.WrapTextWithAnsi("first\nsecond\r\nthird\rfourth", 80));
    }

    [Fact(DisplayName = "should preserve ANSI state across CRLF and CR line endings")]
    public void Wrap_PreservesAnsiAcrossLineEndings()
    {
        const string red = "\x1b[31m";
        const string reset = "\x1b[0m";

        Assert.Equal(
            [$"{red}first", $"{red}second", $"{red}third{reset}"],
            TextMeasurement.WrapTextWithAnsi($"{red}first\r\nsecond\rthird{reset}", 80));
    }

    [Fact(DisplayName = "should wrap plain text correctly")]
    public void Wrap_WrapsPlainText()
    {
        var wrapped = TextMeasurement.WrapTextWithAnsi("hello world this is a test", 10);

        Assert.True(wrapped.Count > 1);
        Assert.All(wrapped, line => Assert.True(TextMeasurement.VisibleWidth(line) <= 10));
    }

    [Fact(DisplayName = "should break CJK runs at grapheme boundaries after Latin text")]
    public void Wrap_BreaksCjkAtGraphemeBoundaries()
    {
        var text = "This is an example 中文汉字测试段落内容中文汉字测试段落内容.";

        var wrapped = TextMeasurement.WrapTextWithAnsi(text, 40);

        Assert.Equal(["This is an example 中文汉字测试段落内容", "中文汉字测试段落内容."], wrapped);
        Assert.All(wrapped, line => Assert.True(TextMeasurement.VisibleWidth(line) <= 40));
    }

    [Fact(DisplayName = "should preserve color codes when wrapping CJK runs")]
    public void Wrap_PreservesColorCodesWhenBreakingCjk()
    {
        const string red = "\x1b[31m";
        const string reset = "\x1b[0m";
        var text = $"{red}This is an example 中文汉字测试段落内容中文汉字测试段落内容.{reset}";

        var wrapped = TextMeasurement.WrapTextWithAnsi(text, 40);

        Assert.Equal(2, wrapped.Count);
        Assert.Equal($"{red}This is an example 中文汉字测试段落内容", wrapped[0]);
        Assert.Equal($"{red}中文汉字测试段落内容.{reset}", wrapped[1]);
        Assert.All(wrapped, line => Assert.True(TextMeasurement.VisibleWidth(line) <= 40));
    }

    [Fact(DisplayName = "should ignore OSC 133 semantic markers in visible width")]
    public void Width_IgnoresOsc133BelMarkers()
    {
        Assert.Equal(5, TextMeasurement.VisibleWidth("\x1b]133;A\x07hello\x1b]133;B\x07"));
    }

    [Fact(DisplayName = "should ignore OSC sequences terminated with ST in visible width")]
    public void Width_IgnoresOscStMarkers()
    {
        Assert.Equal(5, TextMeasurement.VisibleWidth("\x1b]133;A\x1b\\hello\x1b]133;B\x1b\\"));
    }

    [Fact(DisplayName = "should treat isolated regional indicators as width 2")]
    public void Width_TreatsRegionalIndicatorsAsWide()
    {
        Assert.Equal(2, TextMeasurement.VisibleWidth("🇨"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("🇨🇳"));
    }

    [Fact(DisplayName = "should truncate trailing whitespace that exceeds width")]
    public void Wrap_TrimsTrailingWhitespaceThatOverflows()
    {
        var wrapped = TextMeasurement.WrapTextWithAnsi("  ", 1);

        Assert.True(TextMeasurement.VisibleWidth(wrapped[0]) <= 1);
    }

    [Fact(DisplayName = "should preserve color codes across wraps")]
    public void Wrap_PreservesColorCodesAcrossWraps()
    {
        const string red = "\x1b[31m";
        const string reset = "\x1b[0m";
        var wrapped = TextMeasurement.WrapTextWithAnsi($"{red}hello world this is red{reset}", 10);

        for (var index = 1; index < wrapped.Count; index++)
        {
            Assert.StartsWith(red, wrapped[index]);
        }

        for (var index = 0; index < wrapped.Count - 1; index++)
        {
            Assert.DoesNotContain(reset, wrapped[index]);
        }
    }

    [Fact(DisplayName = "re-emits OSC 8 open at the start of continuation lines")]
    public void Wrap_ReopensOsc8LinksOnContinuationLines()
    {
        const string url = "https://example.com";
        var open = $"\x1b]8;;{url}\x1b\\";
        var input = $"{open}0123456789\x1b]8;;\x1b\\";

        var wrapped = TextMeasurement.WrapTextWithAnsi(input, 6);

        Assert.All(wrapped, line =>
        {
            if (TextMeasurement.StripTerminalSequences(line).Trim().Length > 0)
            {
                Assert.Contains(open, line);
            }
        });
    }

    [Fact(DisplayName = "closes OSC 8 before each line break")]
    public void Wrap_ClosesOsc8LinksBeforeLineBreaks()
    {
        const string url = "https://example.com";
        var open = $"\x1b]8;;{url}\x1b\\";
        var close = "\x1b]8;;\x1b\\";
        var wrapped = TextMeasurement.WrapTextWithAnsi($"{open}0123456789{close}", 6);

        for (var index = 0; index < wrapped.Count - 1; index++)
        {
            if (wrapped[index].Contains(open, StringComparison.Ordinal))
            {
                Assert.EndsWith(close, wrapped[index]);
            }
        }
    }

    [Fact(DisplayName = "preserves BEL terminators when wrapping OAuth-style hyperlinks")]
    public void Wrap_PreservesBelOsc8Terminators()
    {
        var url = $"https://example.com/oauth/{new string('a', 32)}";
        var open = $"\x1b]8;;{url}\x07";
        var close = "\x1b]8;;\x07";
        var wrapped = TextMeasurement.WrapTextWithAnsi($"{open}{url}{close}", 20);

        Assert.True(wrapped.Count > 1);
        Assert.All(wrapped, line =>
        {
            Assert.Contains(open, line);
            Assert.DoesNotContain($"\x1b]8;;{url}\x1b\\", line);
        });
        foreach (var line in wrapped.Take(wrapped.Count - 1))
        {
            Assert.EndsWith(close, line);
        }
    }

    [Fact(DisplayName = "does not emit OSC 8 sequences on lines that are outside the hyperlink")]
    public void Wrap_DoesNotEmitOsc8OutsideLink()
    {
        const string url = "https://example.com";
        var open = $"\x1b]8;;{url}\x1b\\";
        var close = "\x1b]8;;\x1b\\";
        var line = TextMeasurement.WrapTextWithAnsi($"before {open}link{close} after", 80).Single();

        Assert.Equal(1, CountOccurrences(line, open));
        Assert.Equal(1, CountOccurrences(line, close));
    }

    [Fact(DisplayName = "keeps output within width for very large unicode input")]
    public void Truncate_HandlesVeryLargeUnicodeInput()
    {
        var text = string.Concat(Enumerable.Repeat("🙂界", 100_000));

        var truncated = TextMeasurement.TruncateToWidth(text, 40, "…");

        Assert.True(TextMeasurement.VisibleWidth(truncated) <= 40);
        Assert.EndsWith("…\x1b[0m", truncated);
    }

    [Fact(DisplayName = "preserves ANSI styling for kept text and resets before and after ellipsis")]
    public void Truncate_PreservesAnsiAndResetsAroundEllipsis()
    {
        var text = $"\x1b[31m{string.Concat(Enumerable.Repeat("hello ", 1000))}\x1b[0m";

        var truncated = TextMeasurement.TruncateToWidth(text, 20, "…");

        Assert.True(TextMeasurement.VisibleWidth(truncated) <= 20);
        Assert.Contains("\x1b[31m", truncated);
        Assert.EndsWith("\x1b[0m…\x1b[0m", truncated);
    }

    [Fact(DisplayName = "closes a BEL-terminated OSC 8 link when truncating its label")]
    public void Truncate_ClosesBelOsc8Link()
    {
        const string open = "\x1b]8;;https://example.com\x07";
        const string close = "\x1b]8;;\x07";
        var text = $"{open}some-longer-label-here{close}";

        Assert.Equal($"{open}some-longer-{close}\x1b[0m...\x1b[0m", TextMeasurement.TruncateToWidth(text, 15));
    }

    [Fact(DisplayName = "handles malformed ANSI escape prefixes without hanging")]
    public void Truncate_HandlesMalformedAnsi()
    {
        var text = $"abc\x1bnot-ansi {string.Concat(Enumerable.Repeat("🙂", 1000))}";

        var truncated = TextMeasurement.TruncateToWidth(text, 20, "…");

        Assert.True(TextMeasurement.VisibleWidth(truncated) <= 20);
    }

    [Fact(DisplayName = "clips wide ellipsis safely and brackets it with resets")]
    public void Truncate_ClipsWideEllipsisSafely()
    {
        Assert.Equal(string.Empty, TextMeasurement.TruncateToWidth("abcdef", 1, "🙂"));
        Assert.Equal("\x1b[0m🙂\x1b[0m", TextMeasurement.TruncateToWidth("abcdef", 2, "🙂"));
        Assert.True(TextMeasurement.VisibleWidth(TextMeasurement.TruncateToWidth("abcdef", 2, "🙂")) <= 2);
    }

    [Fact(DisplayName = "returns the original text when it already fits even if ellipsis is too wide")]
    public void Truncate_ReturnsFittingTextWithWideEllipsis()
    {
        Assert.Equal("a", TextMeasurement.TruncateToWidth("a", 2, "🙂"));
        Assert.Equal("界", TextMeasurement.TruncateToWidth("界", 2, "🙂"));
    }

    [Fact(DisplayName = "pads truncated output to requested width")]
    public void Truncate_PadsToRequestedWidth()
    {
        var truncated = TextMeasurement.TruncateToWidth("🙂界🙂界🙂界", 8, "…", pad: true);

        Assert.Equal(8, TextMeasurement.VisibleWidth(truncated));
    }

    [Fact(DisplayName = "adds a trailing reset when truncating without an ellipsis")]
    public void Truncate_ResetsWhenEllipsisIsEmpty()
    {
        var truncated = TextMeasurement.TruncateToWidth($"\x1b[31m{new string('h', 100)}", 10, string.Empty);

        Assert.True(TextMeasurement.VisibleWidth(truncated) <= 10);
        Assert.EndsWith("\x1b[0m", truncated);
    }

    [Fact(DisplayName = "keeps a contiguous prefix instead of skipping a wide grapheme and resuming later")]
    public void Truncate_KeepsContiguousPrefix()
    {
        var truncated = TextMeasurement.TruncateToWidth("🙂\t界 \x1b_abc\x07", 7, "…", pad: true);

        Assert.Equal("🙂\t\x1b[0m…\x1b[0m ", truncated);
    }

    [Fact(DisplayName = "counts tabs inline and skips ANSI inline")]
    public void Width_CountsTabsAndSkipsAnsi()
    {
        Assert.Equal(5, TextMeasurement.VisibleWidth("\t\x1b[31m界\x1b[0m"));
    }

    [Fact(DisplayName = "counts Indic conjunct spacing code points within grapheme clusters")]
    public void Width_CountsIndicConjunctSpacingCodePoints()
    {
        Assert.Equal(2, TextMeasurement.VisibleWidth("र्क"));
        Assert.Equal(5, TextMeasurement.VisibleWidth("नेटवर्क"));
        Assert.Equal(33, TextMeasurement.VisibleWidth("सर्वाधिकार सुरक्षित। ऑर्डर पर क्लिक करें"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("র্ক"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ર્ક"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ର୍କ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ర్క"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ര്‍ക"));
    }

    [Fact(DisplayName = "keeps ordinary combining marks zero-width")]
    public void Width_KeepsOrdinaryCombiningMarksZeroWidth()
    {
        Assert.Equal(1, TextMeasurement.VisibleWidth("e\u0301"));
        Assert.Equal(5, TextMeasurement.VisibleWidth("čřžůú"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("שָׁ"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("بّ"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("རྐ"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("ᜠ᜴"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("가〮"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("가〯"));
    }

    [Fact(DisplayName = "keeps CJK and Japanese width accounting unchanged")]
    public void Width_KeepsCjkAndJapaneseWidths()
    {
        Assert.Equal(4, TextMeasurement.VisibleWidth("网络"));
        Assert.Equal(12, TextMeasurement.VisibleWidth("ネットワーク"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("が"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("か\u3099"));
    }

    [Fact(DisplayName = "counts Myanmar marks that terminals allocate cells for")]
    public void Width_CountsMyanmarTerminalMarks()
    {
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကာ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကေ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("က်"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကျ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကြ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကဳ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကဴ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကဵ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ကး"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("ကို"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("က္"));
    }

    [Fact(DisplayName = "keeps Thai and Lao AM clusters at their normal cell width")]
    public void Width_KeepsThaiAndLaoAmClustersAtNormalWidth()
    {
        Assert.Equal(1, TextMeasurement.VisibleWidth("ำ"));
        Assert.Equal(1, TextMeasurement.VisibleWidth("ຳ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("กำ"));
        Assert.Equal(2, TextMeasurement.VisibleWidth("ກຳ"));
    }

    [Fact(DisplayName = "normalizes Thai and Lao AM vowels only for terminal output")]
    public void Width_NormalizesThaiAndLaoAmOnlyForOutput()
    {
        Assert.Equal("ํา", TextMeasurement.NormalizeTerminalOutput("ำ"));
        Assert.Equal("ໍາ", TextMeasurement.NormalizeTerminalOutput("ຳ"));
        Assert.Equal(
            TextMeasurement.VisibleWidth("ำabc"),
            TextMeasurement.VisibleWidth(TextMeasurement.NormalizeTerminalOutput("ำabc")));
        Assert.Equal(
            TextMeasurement.VisibleWidth("ຳabc"),
            TextMeasurement.VisibleWidth(TextMeasurement.NormalizeTerminalOutput("ຳabc")));
    }

    [Fact(DisplayName = "treats partial flag grapheme as full-width to avoid streaming render drift")]
    public void RegionalIndicator_PartialFlagIsFullWidth()
    {
        Assert.Equal(2, TextMeasurement.VisibleWidth("🇨"));
        Assert.Equal(10, TextMeasurement.VisibleWidth("      - 🇨"));
    }

    [Fact(DisplayName = "wraps intermediate partial-flag list line before overflow")]
    public void RegionalIndicator_WrapsBeforeOverflow()
    {
        var wrapped = TextMeasurement.WrapTextWithAnsi("      - 🇨", 9);

        Assert.Equal(2, wrapped.Count);
        Assert.Equal(7, TextMeasurement.VisibleWidth(wrapped[0]));
        Assert.Equal(2, TextMeasurement.VisibleWidth(wrapped[1]));
    }

    [Fact(DisplayName = "treats all regional-indicator singleton graphemes as width 2")]
    public void RegionalIndicator_AllSingletonsAreFullWidth()
    {
        for (var codePoint = 0x1f1e6; codePoint <= 0x1f1ff; codePoint++)
        {
            Assert.Equal(2, TextMeasurement.VisibleWidth(char.ConvertFromUtf32(codePoint)));
        }
    }

    [Fact(DisplayName = "keeps full flag pairs at width 2")]
    public void RegionalIndicator_FlagPairsRemainWidthTwo()
    {
        foreach (var flag in new[] { "🇯🇵", "🇺🇸", "🇬🇧", "🇨🇳", "🇩🇪", "🇫🇷" })
        {
            Assert.Equal(2, TextMeasurement.VisibleWidth(flag));
        }
    }

    [Fact(DisplayName = "keeps common streaming emoji intermediates at stable width")]
    public void RegionalIndicator_CommonEmojiRemainStable()
    {
        foreach (var sample in new[] { "👍", "👍🏻", "✅", "⚡", "⚡️", "👨", "👨‍💻", "🏳️‍🌈" })
        {
            Assert.Equal(2, TextMeasurement.VisibleWidth(sample));
        }
    }

    [Fact(DisplayName = "excludes a wide grapheme from before when overlay starts inside it")]
    public void Overlay_ExcludesWideGraphemeAtStart()
    {
        var segments = TextMeasurement.ExtractSegments("abcd让EFGH", 5, 9, 11, strictAfter: true);

        Assert.Equal("abcd", segments.Before);
        Assert.Equal(4, segments.BeforeWidth);
        Assert.Equal(segments.BeforeWidth, TextMeasurement.VisibleWidth(segments.Before));
        Assert.Equal("H", segments.After);
        Assert.Equal(1, segments.AfterWidth);
    }

    [Fact(DisplayName = "keeps ASCII before-segment behavior at the same boundary")]
    public void Overlay_KeepsAsciiBoundaryBehavior()
    {
        var segments = TextMeasurement.ExtractSegments("abcdG EFGH", 5, 9, 11, strictAfter: true);

        Assert.Equal("abcdG", segments.Before);
        Assert.Equal(5, segments.BeforeWidth);
        Assert.Equal(segments.BeforeWidth, TextMeasurement.VisibleWidth(segments.Before));
    }

    [Fact(DisplayName = "composites an overlay at the requested column when it starts inside a wide grapheme")]
    public void Overlay_CompositesInsideWideGrapheme()
    {
        var output = TextMeasurement.CompositeTuiLine("abcd让EFGH", "│XX│", 5, 4, 20);
        var prefix = TextMeasurement.SliceByColumn(output, 0, 5, strict: true);
        var overlay = TextMeasurement.SliceByColumn(output, 5, 4, strict: true);

        Assert.DoesNotContain("让", output);
        Assert.Equal(20, TextMeasurement.VisibleWidth(output));
        Assert.Equal(5, TextMeasurement.VisibleWidth(prefix));
        Assert.Equal(4, TextMeasurement.VisibleWidth(overlay));
        Assert.Contains("│XX│", overlay);
    }

    [Fact(DisplayName = "composites an overlay when it starts at a wide grapheme boundary")]
    public void Overlay_CompositesAtWideGraphemeBoundary()
    {
        var output = TextMeasurement.CompositeTuiLine("abcd让EFGH", "│XX│", 4, 4, 20);
        var overlay = TextMeasurement.SliceByColumn(output, 4, 4, strict: true);

        Assert.DoesNotContain("让", output);
        Assert.Equal(20, TextMeasurement.VisibleWidth(output));
        Assert.Equal(4, TextMeasurement.VisibleWidth(overlay));
        Assert.Contains("│XX│", overlay);
    }

    [Fact(DisplayName = "keeps slice helper widths consistent with visible width")]
    public void Tabs_SliceWidthMatchesVisibleWidth()
    {
        var text = "out 192M\t.pi/skill-tests/results-ha";
        var slice = TextMeasurement.SliceWithWidth(text, 0, 10, strict: true);

        Assert.Equal("out 192M", slice.Text);
        Assert.Equal(8, slice.Width);
        Assert.Equal(slice.Width, TextMeasurement.VisibleWidth(slice.Text));
    }

    [Fact(DisplayName = "keeps overlay segment widths consistent with visible width")]
    public void Tabs_OverlaySegmentWidthMatchesVisibleWidth()
    {
        var text = "out 192M\t.pi/skill-tests/results-ha";
        var segments = TextMeasurement.ExtractSegments(text, 10, 13, 10, strictAfter: true);
        var tabFits = TextMeasurement.ExtractSegments(text, 11, 13, 10, strictAfter: true);

        Assert.Equal("out 192M", segments.Before);
        Assert.Equal(8, segments.BeforeWidth);
        Assert.Equal(segments.BeforeWidth, TextMeasurement.VisibleWidth(segments.Before));
        Assert.Equal("out 192M\t", tabFits.Before);
        Assert.Equal(11, tabFits.BeforeWidth);
        Assert.Equal(tabFits.BeforeWidth, TextMeasurement.VisibleWidth(tabFits.Before));
    }

    [Fact(DisplayName = "keeps tabs inside terminal control sequences byte-identical")]
    public void Tabs_PreservesTabsInsideControlSequences()
    {
        var controls = new[]
        {
            "\x1b]8;;https://example.test/a\tb\x07",
            "\x1b]0;window\ttitle\x1b\\",
            "\x1b_payload\tdata\x1b\\",
        };

        foreach (var control in controls)
        {
            Assert.Equal($"{control}label   text", TextMeasurement.NormalizeTerminalOutput($"{control}label\ttext"));
        }
    }

    [Fact(DisplayName = "keeps tab-containing overlays on one physical terminal row")]
    public void Tabs_OverlayStaysOnOnePhysicalRow()
    {
        const int width = 16;
        var baseLine = "base 1".PadRight(width);
        var overlay = TextMeasurement.NormalizeTerminalOutput("\tX");
        var output = TextMeasurement.CompositeTuiLine(baseLine, overlay, 4, 4, width);

        Assert.Equal("base   X        ", TextMeasurement.StripTerminalSequences(output));
        Assert.DoesNotContain('\t', output);
        Assert.Equal(width, TextMeasurement.VisibleWidth(output));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
