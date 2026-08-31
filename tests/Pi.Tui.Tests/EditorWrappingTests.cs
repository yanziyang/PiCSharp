using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorWrappingTests
{
    [Fact(DisplayName = "wraps lines correctly when text contains wide emojis")]
    public void Wraps_lines_correctly_when_text_contains_wide_emojis()
    {
        var editor = CreateEditor();
        const int width = 20;
        editor.SetText("Hello ✅ World");
        var lines = editor.Render(width);
        AssertContentWidths(lines, width);
    }

    [Fact(DisplayName = "wraps long text with emojis at correct positions")]
    public void Wraps_long_text_with_emojis_at_correct_positions()
    {
        var editor = CreateEditor();
        const int width = 10;
        editor.SetText("✅✅✅✅✅✅");
        var lines = editor.Render(width);
        AssertContentWidths(lines, width);
    }

    [Fact(DisplayName = "renders isolated Thai and Lao AM clusters without width drift")]
    public void Renders_isolated_Thai_and_Lao_AM_clusters_without_width_drift()
    {
        foreach (var text in new[] { "ำabc", "ຳabc" })
        {
            var editor = CreateEditor();
            const int width = 8;
            editor.SetText(text);
            foreach (var line in editor.Render(width))
                Assert.Equal(width, TextMeasurement.VisibleWidth(line));
        }
    }

    [Fact(DisplayName = "wraps CJK characters correctly (each is 2 columns wide)")]
    public void Wraps_CJK_characters_correctly_each_is_2_columns_wide()
    {
        var editor = CreateEditor();
        const int width = 11;
        editor.SetText("日本語テスト");
        var lines = editor.Render(width);
        AssertContentWidths(lines, width);
        var contentLines = lines.Skip(1).SkipLast(1)
            .Select(line => TextMeasurement.StripTerminalSequences(line).Trim())
            .ToArray();
        Assert.Equal(2, contentLines.Length);
        Assert.Equal("日本語テス", contentLines[0]);
        Assert.Equal("ト", contentLines[1]);
    }

    [Fact(DisplayName = "handles mixed ASCII and wide characters in wrapping")]
    public void Handles_mixed_ASCII_and_wide_characters_in_wrapping()
    {
        var editor = CreateEditor();
        const int width = 16;
        editor.SetText("Test ✅ OK 日本");
        var contentLines = editor.Render(width).Skip(1).SkipLast(1).ToArray();
        Assert.Single(contentLines);
        Assert.Equal(width, TextMeasurement.VisibleWidth(contentLines[0]));
    }

    [Fact(DisplayName = "renders cursor correctly on wide characters")]
    public void Renders_cursor_correctly_on_wide_characters()
    {
        var editor = CreateEditor();
        const int width = 20;
        editor.SetText("A✅B");
        var contentLine = editor.Render(width)[1];
        Assert.Contains("\x1b[7m", contentLine, StringComparison.Ordinal);
        Assert.Equal(width, TextMeasurement.VisibleWidth(contentLine));
    }

    [Fact(DisplayName = "does not exceed terminal width with emoji at wrap boundary")]
    public void Does_not_exceed_terminal_width_with_emoji_at_wrap_boundary()
    {
        var editor = CreateEditor();
        const int width = 11;
        editor.SetText("0123456789✅");
        foreach (var line in editor.Render(width).Skip(1).SkipLast(1))
            Assert.True(TextMeasurement.VisibleWidth(line) <= width);
    }

    [Fact(DisplayName = "shows cursor at end of line before wrap, wraps on next char")]
    public void Shows_cursor_at_end_of_line_before_wrap_wraps_on_next_char()
    {
        const int width = 10;
        foreach (var paddingX in new[] { 0, 1 })
        {
            var editor = new Editor(
                CreateTestTui(width + paddingX),
                DefaultTheme,
                new EditorOptions { PaddingX = paddingX });
            foreach (var character in "aaaaaaaaa") editor.HandleInput(character.ToString());
            var contentLines = editor.Render(width + paddingX).Skip(1).SkipLast(1).ToArray();
            Assert.Single(contentLines);
            Assert.EndsWith("\x1b[7m \x1b[0m", contentLines[0], StringComparison.Ordinal);
            editor.HandleInput("a");
            contentLines = editor.Render(width + paddingX).Skip(1).SkipLast(1).ToArray();
            Assert.Equal(2, contentLines.Length);
        }
    }

    [Fact(DisplayName = "wraps at word boundaries instead of mid-word")]
    public void Wraps_at_word_boundaries_instead_of_mid_word()
    {
        var editor = CreateEditor();
        const int width = 40;
        editor.SetText("Hello world this is a test of word wrapping functionality");
        var contentLines = editor.Render(width).Skip(1).SkipLast(1)
            .Select(line => TextMeasurement.StripTerminalSequences(line).Trim())
            .ToArray();
        Assert.True(contentLines[0].Length == 0 || contentLines[0][^1] != '-');
        foreach (var line in contentLines)
        {
            var trimmed = line.TrimEnd();
            var lastCharacter = trimmed.Length == 0 ? string.Empty : trimmed[^1].ToString();
            Assert.True(lastCharacter.Length == 0 || char.IsLetterOrDigit(lastCharacter[0]) || ".,!?;:_".Contains(lastCharacter, StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "does not start lines with leading whitespace after word wrap")]
    public void Does_not_start_lines_with_leading_whitespace_after_word_wrap()
    {
        var editor = CreateEditor();
        const int width = 20;
        editor.SetText("Word1 Word2 Word3 Word4 Word5 Word6");
        var contentLines = editor.Render(width).Skip(1).SkipLast(1).ToArray();
        foreach (var contentLine in contentLines)
        {
            var line = TextMeasurement.StripTerminalSequences(contentLine);
            var trimmedStart = line.TrimStart();
            if (trimmedStart.Length > 0)
                Assert.False(line.TrimEnd().Length > 0 && char.IsWhiteSpace(line.TrimEnd()[0]));
        }
    }

    [Fact(DisplayName = "breaks long words (URLs) at character level")]
    public void Breaks_long_words_URLs_at_character_level()
    {
        var editor = CreateEditor();
        const int width = 30;
        editor.SetText("Check https://example.com/very/long/path/that/exceeds/width here");
        AssertContentWidths(editor.Render(width), width);
    }

    [Fact(DisplayName = "preserves multiple spaces within words on same line")]
    public void Preserves_multiple_spaces_within_words_on_same_line()
    {
        var editor = CreateEditor();
        editor.SetText("Word1   Word2    Word3");
        var contentLine = TextMeasurement.StripTerminalSequences(editor.Render(50)[1]).Trim();
        Assert.Contains("Word1   Word2", contentLine, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "handles empty string")]
    public void Handles_empty_string()
    {
        var editor = CreateEditor();
        editor.SetText(string.Empty);
        Assert.Equal(3, editor.Render(40).Count);
    }

    [Fact(DisplayName = "handles single word that fits exactly")]
    public void Handles_single_word_that_fits_exactly()
    {
        var editor = CreateEditor();
        editor.SetText("1234567890");
        var lines = editor.Render(11);
        Assert.Equal(3, lines.Count);
        Assert.Contains("1234567890", TextMeasurement.StripTerminalSequences(lines[1]), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "wraps word to next line when it ends exactly at terminal width")]
    public void Wraps_word_to_next_line_when_it_ends_exactly_at_terminal_width() =>
        AssertChunks("hello world test", 11, "hello ", "world test");

    [Fact(DisplayName = "keeps whitespace at terminal width boundary on same line")]
    public void Keeps_whitespace_at_terminal_width_boundary_on_same_line() =>
        AssertChunks("hello world test", 12, "hello world ", "test");

    [Fact(DisplayName = "handles unbreakable word filling width exactly followed by space")]
    public void Handles_unbreakable_word_filling_width_exactly_followed_by_space() =>
        AssertChunks("aaaaaaaaaaaa aaaa", 12, "aaaaaaaaaaaa", " aaaa");

    [Fact(DisplayName = "wraps word to next line when it fits width but not remaining space")]
    public void Wraps_word_to_next_line_when_it_fits_width_but_not_remaining_space() =>
        AssertChunks("      aaaaaaaaaaaa", 12, "      ", "aaaaaaaaaaaa");

    [Fact(DisplayName = "keeps word with multi-space and following word together when they fit")]
    public void Keeps_word_with_multi_space_and_following_word_together_when_they_fit() =>
        AssertChunks("Lorem ipsum dolor sit amet,    consectetur", 30, "Lorem ipsum dolor sit ", "amet,    consectetur");

    [Fact(DisplayName = "keeps word with multi-space and following word when they fill width exactly")]
    public void Keeps_word_with_multi_space_and_following_word_when_they_fill_width_exactly() =>
        AssertChunks("Lorem ipsum dolor sit amet,              consectetur", 30, "Lorem ipsum dolor sit ", "amet,              consectetur");

    [Fact(DisplayName = "splits when word plus multi-space plus word exceeds width")]
    public void Splits_when_word_plus_multi_space_plus_word_exceeds_width() =>
        AssertChunks("Lorem ipsum dolor sit amet,               consectetur", 30, "Lorem ipsum dolor sit ", "amet,               ", "consectetur");

    [Fact(DisplayName = "breaks long whitespace at line boundary")]
    public void Breaks_long_whitespace_at_line_boundary() =>
        AssertChunks("Lorem ipsum dolor sit amet,                         consectetur", 30, "Lorem ipsum dolor sit ", "amet,                         ", "consectetur");

    [Fact(DisplayName = "breaks long whitespace at line boundary 2")]
    public void Breaks_long_whitespace_at_line_boundary_2() =>
        AssertChunks("Lorem ipsum dolor sit amet,                          consectetur", 30, "Lorem ipsum dolor sit ", "amet,                         ", " consectetur");

    [Fact(DisplayName = "breaks whitespace spanning full lines")]
    public void Breaks_whitespace_spanning_full_lines() =>
        AssertChunks("Lorem ipsum dolor sit amet,                                     consectetur", 30, "Lorem ipsum dolor sit ", "amet,                         ", "            consectetur");

    [Fact(DisplayName = "force-breaks when wide char after word boundary wrap still overflows")]
    public void Force_breaks_when_wide_char_after_word_boundary_wrap_still_overflows()
    {
        var line = " " + new string('a', 186) + "你";
        var chunks = Editor.WordWrapLine(line, 187);
        Assert.All(chunks, chunk => Assert.True(TextMeasurement.VisibleWidth(chunk.Text) <= 187));
        Assert.Equal(line, Reconstruct(line, chunks));
    }

    [Fact(DisplayName = "splits oversized atomic segment across multiple chunks")]
    public void Splits_oversized_atomic_segment_across_multiple_chunks()
    {
        const string marker = "[paste #1 +20 lines]";
        var line = "A" + marker + "B";
        var segments = new[]
        {
            new EditorTextSegment("A", 0, line),
            new EditorTextSegment(marker, 1, line),
            new EditorTextSegment("B", 1 + marker.Length, line),
        };
        AssertAtomicChunks(line, Editor.WordWrapLine(line, 10, segments));
    }

    [Fact(DisplayName = "splits oversized atomic segment at start of line")]
    public void Splits_oversized_atomic_segment_at_start_of_line()
    {
        const string marker = "[paste #1 +20 lines]";
        var line = marker + "B";
        var chunks = Editor.WordWrapLine(line, 10,
        [
            new EditorTextSegment(marker, 0, line),
            new EditorTextSegment("B", marker.Length, line),
        ]);
        AssertAtomicChunks(line, chunks);
        Assert.Contains("B", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "splits oversized atomic segment at end of line")]
    public void Splits_oversized_atomic_segment_at_end_of_line()
    {
        const string marker = "[paste #1 +20 lines]";
        var line = "A" + marker;
        var chunks = Editor.WordWrapLine(line, 10,
        [
            new EditorTextSegment("A", 0, line),
            new EditorTextSegment(marker, 1, line),
        ]);
        AssertAtomicChunks(line, chunks);
        Assert.Equal("A", chunks[0].Text);
    }

    [Fact(DisplayName = "splits consecutive oversized atomic segments")]
    public void Splits_consecutive_oversized_atomic_segments()
    {
        const string first = "[paste #1 +20 lines]";
        const string second = "[paste #2 +30 lines]";
        var line = first + second;
        var chunks = Editor.WordWrapLine(line, 10,
        [
            new EditorTextSegment(first, 0, line),
            new EditorTextSegment(second, first.Length, line),
        ]);
        AssertAtomicChunks(line, chunks);
    }

    [Fact(DisplayName = "wraps normally after oversized atomic segment")]
    public void Wraps_normally_after_oversized_atomic_segment()
    {
        const string marker = "[paste #1 +20 lines]";
        var line = marker + " hello world";
        var segments = new List<EditorTextSegment> { new(marker, 0, line) };
        for (var index = marker.Length; index < line.Length; index++)
            segments.Add(new EditorTextSegment(line[index].ToString(), index, line));
        var chunks = Editor.WordWrapLine(line, 10, segments);
        AssertAtomicChunks(line, chunks);
        Assert.Equal("world", chunks[^1].Text);
    }

    private static void AssertContentWidths(IReadOnlyList<string> lines, int width)
    {
        for (var index = 1; index < lines.Count - 1; index++)
            Assert.Equal(width, TextMeasurement.VisibleWidth(lines[index]));
    }

    private static void AssertChunks(string line, int width, params string[] expected)
    {
        var chunks = Editor.WordWrapLine(line, width);
        Assert.Equal(expected, chunks.Select(chunk => chunk.Text));
    }

    private static string Reconstruct(string line, IReadOnlyList<TextChunk> chunks) =>
        string.Concat(chunks.Select(chunk => line[chunk.StartIndex..chunk.EndIndex]));

    private static void AssertAtomicChunks(string line, IReadOnlyList<TextChunk> chunks)
    {
        Assert.All(chunks, chunk => Assert.True(TextMeasurement.VisibleWidth(chunk.Text) <= 10));
        Assert.Equal(line, Reconstruct(line, chunks));
    }
}
