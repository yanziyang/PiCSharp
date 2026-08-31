using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorHistoryAndTextTests
{
    [Fact(DisplayName = "does nothing on Up arrow when history is empty")]
    public void Does_nothing_on_Up_arrow_when_history_is_empty()
    {
        var editor = CreateEditor();
        editor.HandleInput("\x1b[A");
        Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "shows most recent history entry on Up arrow when editor is empty")]
    public void Shows_most_recent_history_entry_on_Up_arrow_when_editor_is_empty()
    {
        var editor = CreateEditor();
        editor.AddToHistory("first prompt");
        editor.AddToHistory("second prompt");
        editor.HandleInput("\x1b[A");
        Assert.Equal("second prompt", editor.GetText());
    }

    [Fact(DisplayName = "cycles through history entries on repeated Up arrow")]
    public void Cycles_through_history_entries_on_repeated_Up_arrow()
    {
        var editor = CreateEditor();
        editor.AddToHistory("first");
        editor.AddToHistory("second");
        editor.AddToHistory("third");
        editor.HandleInput("\x1b[A");
        Assert.Equal("third", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("second", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("first", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("first", editor.GetText());
    }

    [Fact(DisplayName = "jumps to start before entering history from a non-empty draft")]
    public void Jumps_to_start_before_entering_history_from_a_non_empty_draft()
    {
        var editor = CreateEditor();
        editor.AddToHistory("prompt");
        editor.SetText("draft");
        editor.HandleInput("\x1b[D");
        editor.HandleInput("\x1b[D");
        editor.HandleInput("\x1b[A");
        Assert.Equal("draft", editor.GetText());
        Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("\x1b[A");
        Assert.Equal("prompt", editor.GetText());
        editor.HandleInput("\x1b[B");
        Assert.Equal("draft", editor.GetText());
        Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "navigates forward through history with Down arrow")]
    public void Navigates_forward_through_history_with_Down_arrow()
    {
        var editor = CreateEditor();
        editor.AddToHistory("first");
        editor.AddToHistory("second");
        editor.AddToHistory("third");
        editor.SetText("draft");
        for (var index = 0; index < 4; index++) editor.HandleInput("\x1b[A");
        editor.HandleInput("\x1b[B");
        Assert.Equal("second", editor.GetText());
        editor.HandleInput("\x1b[B");
        Assert.Equal("third", editor.GetText());
        editor.HandleInput("\x1b[B");
        Assert.Equal("draft", editor.GetText());
    }

    [Fact(DisplayName = "exits history mode when typing a character")]
    public void Exits_history_mode_when_typing_a_character()
    {
        var editor = CreateEditor();
        editor.AddToHistory("old prompt");
        editor.HandleInput("\x1b[A");
        editor.HandleInput("x");
        Assert.Equal("xold prompt", editor.GetText());
    }

    [Fact(DisplayName = "exits history mode on setText")]
    public void Exits_history_mode_on_setText()
    {
        var editor = CreateEditor();
        editor.AddToHistory("first");
        editor.AddToHistory("second");
        editor.HandleInput("\x1b[A");
        editor.SetText(string.Empty);
        editor.HandleInput("\x1b[A");
        Assert.Equal("second", editor.GetText());
    }

    [Fact(DisplayName = "does not add empty strings to history")]
    public void Does_not_add_empty_strings_to_history()
    {
        var editor = CreateEditor();
        editor.AddToHistory(string.Empty);
        editor.AddToHistory("   ");
        editor.AddToHistory("valid");
        editor.HandleInput("\x1b[A");
        Assert.Equal("valid", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("valid", editor.GetText());
    }

    [Fact(DisplayName = "does not add consecutive duplicates to history")]
    public void Does_not_add_consecutive_duplicates_to_history()
    {
        var editor = CreateEditor();
        editor.AddToHistory("same");
        editor.AddToHistory("same");
        editor.AddToHistory("same");
        editor.HandleInput("\x1b[A");
        Assert.Equal("same", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("same", editor.GetText());
    }

    [Fact(DisplayName = "allows non-consecutive duplicates in history")]
    public void Allows_non_consecutive_duplicates_in_history()
    {
        var editor = CreateEditor();
        editor.AddToHistory("first");
        editor.AddToHistory("second");
        editor.AddToHistory("first");
        editor.HandleInput("\x1b[A");
        Assert.Equal("first", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("second", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("first", editor.GetText());
    }

    [Fact(DisplayName = "uses cursor movement instead of history when editor has content")]
    public void Uses_cursor_movement_instead_of_history_when_editor_has_content()
    {
        var editor = CreateEditor();
        editor.AddToHistory("history item");
        editor.SetText("line1\nline2");
        editor.HandleInput("\x1b[A");
        editor.HandleInput("X");
        Assert.Equal("line1X\nline2", editor.GetText());
    }

    [Fact(DisplayName = "limits history to 100 entries")]
    public void Limits_history_to_100_entries()
    {
        var editor = CreateEditor();
        for (var index = 0; index < 105; index++) editor.AddToHistory($"prompt {index}");
        for (var index = 0; index < 100; index++) editor.HandleInput("\x1b[A");
        Assert.Equal("prompt 5", editor.GetText());
        editor.HandleInput("\x1b[A");
        Assert.Equal("prompt 5", editor.GetText());
    }

    [Fact(DisplayName = "places cursor at start after browsing history upward")]
    public void Places_cursor_at_start_after_browsing_history_upward()
    {
        var editor = CreateEditor();
        editor.AddToHistory("older entry");
        editor.AddToHistory("line1\nline2\nline3");
        editor.HandleInput("\x1b[A");
        Assert.Equal("line1\nline2\nline3", editor.GetText());
        Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("\x1b[A");
        Assert.Equal("older entry", editor.GetText());
        Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "places cursor at end after browsing history downward")]
    public void Places_cursor_at_end_after_browsing_history_downward()
    {
        var editor = CreateEditor();
        editor.AddToHistory("older entry");
        editor.AddToHistory("line1\nline2\nline3");
        editor.AddToHistory("newer entry");
        for (var index = 0; index < 3; index++) editor.HandleInput("\x1b[A");
        editor.HandleInput("\x1b[B");
        Assert.Equal("line1\nline2\nline3", editor.GetText());
        Assert.Equal((2, 5), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal("newer entry", editor.GetText());
    }

    [Fact(DisplayName = "allows opposite-direction cursor movement within multi-line history entry")]
    public void Allows_opposite_direction_cursor_movement_within_multi_line_history_entry()
    {
        var editor = CreateEditor();
        editor.AddToHistory("line1\nline2\nline3");
        editor.HandleInput("\x1b[A");
        Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal("line1\nline2\nline3", editor.GetText());
        Assert.Equal((1, 0), editor.GetCursor());
        editor.HandleInput("\x1b[A");
        Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "returns cursor position")]
    public void Returns_cursor_position()
    {
        var editor = CreateEditor();
        Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("a");
        editor.HandleInput("b");
        editor.HandleInput("c");
        Assert.Equal((0, 3), editor.GetCursor());
        editor.HandleInput("\x1b[D");
        Assert.Equal((0, 2), editor.GetCursor());
    }

    [Fact(DisplayName = "returns lines as a defensive copy")]
    public void Returns_lines_as_a_defensive_copy()
    {
        var editor = CreateEditor();
        editor.SetText("a\nb");
        var lines = editor.GetLines().ToArray();
        Assert.Equal(["a", "b"], lines);
        lines[0] = "mutated";
        Assert.Equal(["a", "b"], editor.GetLines());
    }

    [Fact(DisplayName = "inserts backslash immediately (no buffering)")]
    public void Inserts_backslash_immediately_no_buffering()
    {
        var editor = CreateEditor();
        editor.HandleInput("\\");
        Assert.Equal("\\", editor.GetText());
    }

    [Fact(DisplayName = "converts standalone backslash to newline on Enter")]
    public void Converts_standalone_backslash_to_newline_on_Enter()
    {
        var editor = CreateEditor();
        editor.HandleInput("\\");
        editor.HandleInput("\r");
        Assert.Equal("\n", editor.GetText());
    }

    [Fact(DisplayName = "inserts backslash normally when followed by other characters")]
    public void Inserts_backslash_normally_when_followed_by_other_characters()
    {
        var editor = CreateEditor();
        editor.HandleInput("\\");
        editor.HandleInput("x");
        Assert.Equal("\\x", editor.GetText());
    }

    [Fact(DisplayName = "does not trigger newline when backslash is not immediately before cursor")]
    public void Does_not_trigger_newline_when_backslash_is_not_immediately_before_cursor()
    {
        var editor = CreateEditor();
        var submitted = false;
        editor.OnSubmit = _ => submitted = true;
        editor.HandleInput("\\");
        editor.HandleInput("x");
        editor.HandleInput("\r");
        Assert.True(submitted);
    }

    [Fact(DisplayName = "only removes one backslash when multiple are present")]
    public void Only_removes_one_backslash_when_multiple_are_present()
    {
        var editor = CreateEditor();
        editor.HandleInput("\\");
        editor.HandleInput("\\");
        editor.HandleInput("\\");
        Assert.Equal("\\\\\\", editor.GetText());
        editor.HandleInput("\r");
        Assert.Equal("\\\\\n", editor.GetText());
    }

    [Fact(DisplayName = "ignores printable CSI-u sequences with unsupported modifiers")]
    public void Ignores_printable_CSI_u_sequences_with_unsupported_modifiers()
    {
        var editor = CreateEditor();
        editor.HandleInput("\x1b[99;9u");
        Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "inserts shifted CSI-u letters as text")]
    public void Inserts_shifted_CSI_u_letters_as_text()
    {
        var editor = CreateEditor();
        editor.HandleInput("\x1b[69;2u");
        Assert.Equal("E", editor.GetText());
    }

    [Fact(DisplayName = "inserts shifted xterm modifyOtherKeys letters as text")]
    public void Inserts_shifted_xterm_modifyOtherKeys_letters_as_text()
    {
        var editor = CreateEditor();
        editor.HandleInput("\x1b[27;2;69~");
        Assert.Equal("E", editor.GetText());
    }

    [Fact(DisplayName = "inserts mixed ASCII, umlauts, and emojis as literal text")]
    public void Inserts_mixed_ASCII_umlauts_and_emojis_as_literal_text()
    {
        var editor = CreateEditor();
        foreach (var text in new[] { "H", "e", "l", "l", "o", " ", "ä", "ö", "ü", " ", "😀" })
            editor.HandleInput(text);
        Assert.Equal("Hello äöü 😀", editor.GetText());
    }

    [Fact(DisplayName = "deletes single-code-unit unicode characters (umlauts) with Backspace")]
    public void Deletes_single_code_unit_unicode_characters_umlauts_with_Backspace()
    {
        var editor = CreateEditor();
        editor.HandleInput("ä");
        editor.HandleInput("ö");
        editor.HandleInput("ü");
        editor.HandleInput("\x7f");
        Assert.Equal("äö", editor.GetText());
    }

    [Fact(DisplayName = "deletes multi-code-unit emojis with single Backspace")]
    public void Deletes_multi_code_unit_emojis_with_single_Backspace()
    {
        var editor = CreateEditor();
        editor.HandleInput("😀");
        editor.HandleInput("👍");
        editor.HandleInput("\x7f");
        Assert.Equal("😀", editor.GetText());
    }

    [Fact(DisplayName = "inserts characters at the correct position after cursor movement over umlauts")]
    public void Inserts_characters_at_the_correct_position_after_cursor_movement_over_umlauts()
    {
        var editor = CreateEditor();
        editor.HandleInput("ä");
        editor.HandleInput("ö");
        editor.HandleInput("ü");
        editor.HandleInput("\x1b[D");
        editor.HandleInput("\x1b[D");
        editor.HandleInput("x");
        Assert.Equal("äxöü", editor.GetText());
    }

    [Fact(DisplayName = "moves cursor across multi-code-unit emojis with single arrow key")]
    public void Moves_cursor_across_multi_code_unit_emojis_with_single_arrow_key()
    {
        var editor = CreateEditor();
        editor.HandleInput("😀");
        editor.HandleInput("👍");
        editor.HandleInput("🎉");
        editor.HandleInput("\x1b[D");
        editor.HandleInput("\x1b[D");
        editor.HandleInput("x");
        Assert.Equal("😀x👍🎉", editor.GetText());
    }

    [Fact(DisplayName = "preserves umlauts across line breaks")]
    public void Preserves_umlauts_across_line_breaks()
    {
        var editor = CreateEditor();
        editor.HandleInput("ä");
        editor.HandleInput("ö");
        editor.HandleInput("ü");
        editor.HandleInput("\n");
        editor.HandleInput("Ä");
        editor.HandleInput("Ö");
        editor.HandleInput("Ü");
        Assert.Equal("äöü\nÄÖÜ", editor.GetText());
    }

    [Fact(DisplayName = "replaces the entire document with unicode text via setText (paste simulation)")]
    public void Replaces_the_entire_document_with_unicode_text_via_setText_paste_simulation()
    {
        var editor = CreateEditor();
        editor.SetText("Hällö Wörld! 😀 äöüÄÖÜß");
        Assert.Equal("Hällö Wörld! 😀 äöüÄÖÜß", editor.GetText());
    }

    [Fact(DisplayName = "moves cursor to document start on Ctrl+A and inserts at the beginning")]
    public void Moves_cursor_to_document_start_on_Ctrl_A_and_inserts_at_the_beginning()
    {
        var editor = CreateEditor();
        editor.HandleInput("a");
        editor.HandleInput("b");
        editor.HandleInput("\x01");
        editor.HandleInput("x");
        Assert.Equal("xab", editor.GetText());
    }

    [Fact(DisplayName = "deletes words correctly with Ctrl+W and Alt+Backspace")]
    public void Deletes_words_correctly_with_Ctrl_W_and_Alt_Backspace()
    {
        var editor = CreateEditor();
        editor.SetText("foo bar baz"); editor.HandleInput("\x17"); Assert.Equal("foo bar ", editor.GetText());
        editor.SetText("foo bar   "); editor.HandleInput("\x17"); Assert.Equal("foo ", editor.GetText());
        editor.SetText("foo bar..."); editor.HandleInput("\x17"); Assert.Equal("foo bar", editor.GetText());
        editor.SetText("foo.bar"); editor.HandleInput("\x17"); Assert.Equal("foo.", editor.GetText());
        editor.SetText("foo:bar"); editor.HandleInput("\x17"); Assert.Equal("foo:", editor.GetText());
        editor.SetText("line one\nline two"); editor.HandleInput("\x17"); Assert.Equal("line one\nline ", editor.GetText());
        editor.SetText("line one\n"); editor.HandleInput("\x17"); Assert.Equal("line one", editor.GetText());
        editor.SetText("foo 😀😀 bar"); editor.HandleInput("\x17"); Assert.Equal("foo 😀😀 ", editor.GetText());
        editor.HandleInput("\x17"); Assert.Equal("foo ", editor.GetText());
        editor.SetText("foo bar"); editor.HandleInput("\x1b\x7f"); Assert.Equal("foo ", editor.GetText());
    }

    [Fact(DisplayName = "navigates words correctly with Ctrl+Left/Right")]
    public void Navigates_words_correctly_with_Ctrl_Left_Right()
    {
        var editor = CreateEditor();
        editor.SetText("foo bar... baz");
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 11), editor.GetCursor());
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 7), editor.GetCursor());
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 4), editor.GetCursor());
        editor.HandleInput("\x1b[1;5C"); Assert.Equal((0, 7), editor.GetCursor());
        editor.HandleInput("\x1b[1;5C"); Assert.Equal((0, 10), editor.GetCursor());
        editor.HandleInput("\x1b[1;5C"); Assert.Equal((0, 14), editor.GetCursor());
        editor.SetText("   foo bar"); editor.HandleInput("\x01"); editor.HandleInput("\x1b[1;5C");
        Assert.Equal((0, 6), editor.GetCursor());
        editor.SetText("foo.bar baz");
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 8), editor.GetCursor());
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 4), editor.GetCursor());
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 3), editor.GetCursor());
        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[1;5C"); Assert.Equal((0, 3), editor.GetCursor());
        editor.HandleInput("\x1b[1;5C"); Assert.Equal((0, 4), editor.GetCursor());
        editor.HandleInput("\x1b[1;5C"); Assert.Equal((0, 7), editor.GetCursor());
    }

    [Fact(DisplayName = "stops at fullwidth Chinese punctuation (issue #4972)")]
    public void Stops_at_fullwidth_Chinese_punctuation_issue_4972()
    {
        var editor = CreateEditor();
        editor.SetText("你好，世界");
        foreach (var expected in new[] { (0, 3), (0, 2), (0, 0) })
        {
            editor.HandleInput("\x1b[1;5D");
            Assert.Equal(expected, editor.GetCursor());
        }

        foreach (var expected in new[] { (0, 2), (0, 3), (0, 5) })
        {
            editor.HandleInput("\x1b[1;5C");
            Assert.Equal(expected, editor.GetCursor());
        }
    }

    [Fact(DisplayName = "handles mixed CJK and ASCII word movement")]
    public void Handles_mixed_CJK_and_ASCII_word_movement()
    {
        var editor = CreateEditor();
        editor.SetText("hello你好，world世界");
        foreach (var expected in new[] { (0, 13), (0, 8), (0, 7), (0, 5), (0, 0) })
        {
            editor.HandleInput("\x1b[1;5D");
            Assert.Equal(expected, editor.GetCursor());
        }

        foreach (var expected in new[] { (0, 5), (0, 7), (0, 8), (0, 13), (0, 15) })
        {
            editor.HandleInput("\x1b[1;5C");
            Assert.Equal(expected, editor.GetCursor());
        }
    }

    [Fact(DisplayName = "keeps truncated scroll indicators within width and preserves their color (issue #6962)")]
    public void Keeps_truncated_scroll_indicators_within_width_and_preserves_their_color_issue_6962()
    {
        const int width = 10;
        static string BorderColor(string text) => "\x1b[35m" + text + "\x1b[39m";
        var theme = new EditorTheme { BorderColor = BorderColor, SelectList = DefaultTheme.SelectList };
        var editor = CreateEditor(width, theme: theme);
        editor.SetText(string.Join('\n', Enumerable.Range(0, 20).Select(index => $"line {index}")));
        _ = editor.Render(width);
        for (var index = 0; index < 10; index++) editor.HandleInput("\x1b[A");
        var lines = editor.Render(width);
        var topBorder = lines[0];
        var bottomBorder = lines[^1];
        var strippedTop = TextMeasurement.StripTerminalSequences(topBorder);
        var strippedBottom = TextMeasurement.StripTerminalSequences(bottomBorder);
        Assert.StartsWith("─── ↑", strippedTop, StringComparison.Ordinal);
        Assert.StartsWith("─── ↓", strippedBottom, StringComparison.Ordinal);
        Assert.Equal(BorderColor(strippedTop), topBorder);
        Assert.Equal(BorderColor(strippedBottom), bottomBorder);
        foreach (var line in lines) Assert.Equal(width, TextMeasurement.VisibleWidth(line));
    }
}
