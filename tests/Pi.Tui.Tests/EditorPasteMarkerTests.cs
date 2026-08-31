using System.Text.RegularExpressions;
using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorPasteMarkerTests
{
    private static readonly Regex _lineMarkerRegex = new(
        @"\[paste #\d+ \+\d+ lines\]",
        RegexOptions.CultureInvariant);

    private static readonly Regex _characterMarkerRegex = new(
        @"\[paste #\d+ \d+ chars\]",
        RegexOptions.CultureInvariant);

    [Fact(DisplayName = "creates a paste marker for large pastes")]
    public void Creates_a_paste_marker_for_large_pastes()
    {
        var editor = CreateEditor();
        var text = PasteWithMarker(editor);
        Assert.Matches(_lineMarkerRegex, text);
    }

    [Fact(DisplayName = "treats paste marker as single unit for right arrow")]
    public void Treats_paste_marker_as_single_unit_for_right_arrow()
    {
        var editor = CreateEditor();
        editor.HandleInput("A");
        PasteWithMarker(editor);
        editor.HandleInput("B");

        editor.HandleInput("\x01");
        Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, 1), editor.GetCursor());
        editor.HandleInput("\x1b[C");
        var marker = GetLineMarker(editor);
        Assert.Equal((0, 1 + marker.Length), editor.GetCursor());
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, 1 + marker.Length + 1), editor.GetCursor());
    }

    [Fact(DisplayName = "treats paste marker as single unit for left arrow")]
    public void Treats_paste_marker_as_single_unit_for_left_arrow()
    {
        var editor = CreateEditor();
        editor.HandleInput("A");
        PasteWithMarker(editor);
        editor.HandleInput("B");

        editor.HandleInput("\x1b[D");
        var marker = GetLineMarker(editor);
        Assert.Equal((0, 1 + marker.Length), editor.GetCursor());
        editor.HandleInput("\x1b[D");
        Assert.Equal((0, 1), editor.GetCursor());
        editor.HandleInput("\x1b[D");
        Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "treats paste marker as single unit for backspace")]
    public void Treats_paste_marker_as_single_unit_for_backspace()
    {
        var editor = CreateEditor();
        editor.HandleInput("A");
        PasteWithMarker(editor);
        editor.HandleInput("B");
        var marker = GetLineMarker(editor);

        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[C");
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, 1 + marker.Length), editor.GetCursor());
        editor.HandleInput("\x7f");
        Assert.Equal("AB", editor.GetText());
        Assert.Equal((0, 1), editor.GetCursor());
    }

    [Fact(DisplayName = "treats paste marker as single unit for forward delete")]
    public void Treats_paste_marker_as_single_unit_for_forward_delete()
    {
        var editor = CreateEditor();
        editor.HandleInput("A");
        PasteWithMarker(editor);
        editor.HandleInput("B");
        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[C");
        editor.HandleInput("\x1b[3~");
        Assert.Equal("AB", editor.GetText());
        Assert.Equal((0, 1), editor.GetCursor());
    }

    [Fact(DisplayName = "treats paste marker as single unit for word movement")]
    public void Treats_paste_marker_as_single_unit_for_word_movement()
    {
        var editor = CreateEditor();
        editor.HandleInput("X");
        editor.HandleInput(" ");
        PasteWithMarker(editor);
        editor.HandleInput(" ");
        editor.HandleInput("Y");
        var marker = GetLineMarker(editor);

        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[1;5C");
        Assert.Equal((0, 1), editor.GetCursor());
        editor.HandleInput("\x1b[1;5C");
        Assert.Equal((0, 2 + marker.Length), editor.GetCursor());
    }

    [Fact(DisplayName = "undo restores marker after backspace deletion")]
    public void Undo_restores_marker_after_backspace_deletion()
    {
        var editor = CreateEditor();
        editor.HandleInput("A");
        PasteWithMarker(editor);
        editor.HandleInput("B");
        var textBefore = editor.GetText();
        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[C");
        editor.HandleInput("\x1b[C");
        editor.HandleInput("\x7f");
        Assert.Equal("AB", editor.GetText());
        editor.HandleInput("\x1b[45;5u");
        Assert.Equal(textBefore, editor.GetText());
    }

    [Fact(DisplayName = "undo after paste marker deletion restores the paste registry")]
    public void Undo_after_paste_marker_deletion_restores_the_paste_registry()
    {
        var editor = CreateEditor();
        var submitted = string.Empty;
        editor.OnSubmit = text => submitted = text;
        var paste = BigPaste("alpha");
        Paste(editor, paste);
        editor.HandleInput("\x7f");
        editor.HandleInput("\x1b[45;5u");
        editor.HandleInput("\r");
        Assert.Equal(paste, submitted);
    }

    [Fact(DisplayName = "undo after deleting the first of two paste markers restores both registry entries")]
    public void Undo_after_deleting_the_first_of_two_paste_markers_restores_both_registry_entries()
    {
        var editor = CreateEditor();
        var submitted = string.Empty;
        editor.OnSubmit = text => submitted = text;
        var pasteA = BigPaste("alpha");
        var pasteB = BigPaste("beta");
        Paste(editor, pasteA);
        Paste(editor, pasteB);
        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[C");
        editor.HandleInput("\x7f");
        editor.HandleInput("\x1b[45;5u");
        editor.HandleInput("\r");
        Assert.Equal(pasteA + pasteB, submitted);
    }

    [Fact(DisplayName = "renumbers the paste registry in ascending id order when markers are out of order in text")]
    public void Renumbers_the_paste_registry_in_ascending_id_order_when_markers_are_out_of_order_in_text()
    {
        var editor = CreateEditor();
        var submitted = string.Empty;
        editor.OnSubmit = text => submitted = text;
        var pasteA = BigPaste("alpha");
        var pasteB = BigPaste("beta");
        var pasteC = BigPaste("gamma");
        Paste(editor, pasteA);
        editor.HandleInput("\x01");
        Paste(editor, pasteB);
        editor.HandleInput("\x01");
        Paste(editor, pasteC);
        editor.HandleInput("\x05");
        editor.HandleInput("\x7f");
        editor.HandleInput("\r");
        Assert.Equal(pasteC + pasteB, submitted);
    }

    [Fact(DisplayName = "undo after setText restores paste markers and registry")]
    public void Undo_after_setText_restores_paste_markers_and_registry()
    {
        var editor = CreateEditor();
        var submitted = string.Empty;
        editor.OnSubmit = text => submitted = text;
        var paste = BigPaste("alpha");
        Paste(editor, paste);
        editor.SetText("replacement");
        editor.HandleInput("\x1b[45;5u");
        editor.HandleInput("\r");
        Assert.Equal(paste, submitted);
    }

    [Fact(DisplayName = "handles multiple paste markers in same line")]
    public void Handles_multiple_paste_markers_in_same_line()
    {
        var editor = CreateEditor();
        PasteWithMarker(editor);
        editor.HandleInput(" ");
        PasteWithMarker(editor);
        var matches = _lineMarkerRegex.Matches(editor.GetText());
        Assert.Equal(2, matches.Count);

        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, matches[0].Length), editor.GetCursor());
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, matches[0].Length + 1), editor.GetCursor());
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, matches[0].Length + 1 + matches[1].Length), editor.GetCursor());
    }

    [Fact(DisplayName = "does not treat manually typed marker-like text as atomic (no valid paste ID)")]
    public void Does_not_treat_manually_typed_marker_like_text_as_atomic_no_valid_paste_ID()
    {
        var editor = CreateEditor();
        const string fakeMarker = "[paste #99 +5 lines]";
        foreach (var character in fakeMarker)
        {
            editor.HandleInput(character.ToString());
        }

        Assert.Equal(fakeMarker, editor.GetText());
        editor.HandleInput("\x01");
        editor.HandleInput("\x1b[C");
        Assert.Equal((0, 1), editor.GetCursor());
    }

    [Fact(DisplayName = "does not crash when paste marker is wider than terminal width")]
    public void Does_not_crash_when_paste_marker_is_wider_than_terminal_width()
    {
        var editor = CreateEditor();
        Paste(editor, RepeatedLines("line", 47));
        var marker = _lineMarkerRegex.Match(editor.GetText());
        Assert.True(marker.Success, "paste marker should be created");
        Assert.True(TextMeasurement.VisibleWidth(marker.Value) > 8, "marker should be wider than render width");
        foreach (var line in editor.Render(8))
        {
            Assert.True(
                TextMeasurement.VisibleWidth(line) <= 8,
                $"line exceeds width 8: visible={TextMeasurement.VisibleWidth(line)} text={line}");
        }
    }

    [Fact(DisplayName = "does not crash when text + paste marker exceeds terminal width with cursor on marker")]
    public void Does_not_crash_when_text_and_paste_marker_exceeds_terminal_width_with_cursor_on_marker()
    {
        var editor = CreateEditor();
        RepeatInput(editor, "b", 35);
        Paste(editor, RepeatedLines("line", 27));
        RepeatInput(editor, "b", 4);
        RepeatInput(editor, "\x1b[D", 5);

        const int renderWidth = 54;
        foreach (var line in editor.Render(renderWidth))
        {
            Assert.True(
                TextMeasurement.VisibleWidth(line) <= renderWidth,
                $"line exceeds width {renderWidth}: visible={TextMeasurement.VisibleWidth(line)} text={line}");
        }
    }

    [Fact(DisplayName = "wordWrapLine re-checks overflow after backtracking to wrap opportunity")]
    public void WordWrapLine_re_checks_overflow_after_backtracking_to_wrap_opportunity()
    {
        var editor = CreateEditor();
        editor.HandleInput(" ");
        RepeatInput(editor, "b", 35);
        Paste(editor, RepeatedLines("line", 27));
        RepeatInput(editor, "b", 4);

        const int renderWidth = 54;
        foreach (var line in editor.Render(renderWidth))
        {
            Assert.True(
                TextMeasurement.VisibleWidth(line) <= renderWidth,
                $"line exceeds width {renderWidth}: visible={TextMeasurement.VisibleWidth(line)} text={line}");
        }
    }

    [Fact(DisplayName = "expands large pasted content literally in getExpandedText")]
    public void Expands_large_pasted_content_literally_in_getExpandedText()
    {
        var editor = CreateEditor();
        var pastedText = LiteralPasteText();
        Paste(editor, pastedText);
        Assert.Matches(_lineMarkerRegex, editor.GetText());
        Assert.Equal(pastedText, editor.GetExpandedText());
    }

    [Fact(DisplayName = "snaps to the paste marker start when navigating down into it")]
    public void Snaps_to_the_paste_marker_start_when_navigating_down_into_it()
    {
        var editor = CreateEditor();
        editor.SetText("12345678901234567890\n\nhello ");
        Paste(editor, new string('x', 2000));
        editor.Render(80);
        Assert.Matches(_characterMarkerRegex, editor.GetText());

        editor.HandleInput("\x1b[A");
        editor.HandleInput("\x1b[A");
        editor.HandleInput("\x01");
        RepeatInput(editor, "\x1b[C", 10);
        Assert.Equal((0, 10), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((1, 0), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((2, 6), editor.GetCursor());
    }

    [Fact(DisplayName = "preserves sticky column when navigating through paste marker line")]
    public void Preserves_sticky_column_when_navigating_through_paste_marker_line()
    {
        var editor = CreateEditor(30, 24);
        foreach (var character in "1234567890123456") editor.HandleInput(character.ToString());
        editor.HandleInput("\n");
        editor.HandleInput("\n");
        Paste(editor, new string('x', 2000));
        editor.HandleInput("\n");
        editor.HandleInput("\n");
        foreach (var character in "abcdefghijklmnop") editor.HandleInput(character.ToString());
        editor.Render(30);

        RepeatInput(editor, "\x1b[A", 4);
        editor.HandleInput("\x01");
        RepeatInput(editor, "\x1b[C", 10);
        Assert.Equal((0, 10), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((1, 0), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((2, 0), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((3, 0), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((4, 10), editor.GetCursor());
    }

    [Fact(DisplayName = "does not get stuck moving down from a multi-visual-line paste marker")]
    public void Does_not_get_stuck_moving_down_from_a_multi_visual_line_paste_marker()
    {
        var editor = CreateEditor(20, 24);
        foreach (var character in "abcdefgh") editor.HandleInput(character.ToString());
        Paste(editor, RepeatedLines("line", 100));
        foreach (var character in "ijklmnopqr") editor.HandleInput(character.ToString());
        editor.HandleInput("\n");
        foreach (var character in "123456789012345678") editor.HandleInput(character.ToString());
        editor.Render(20);

        var marker = _lineMarkerRegex.Match(editor.GetText());
        Assert.True(marker.Success, "paste marker should be created");
        Assert.True(marker.Length > 20, "marker should be wider than terminal");
        const int markerStart = 8;
        var markerEnd = markerStart + marker.Length;

        editor.HandleInput("\x1b[A");
        editor.HandleInput("\x01");
        RepeatInput(editor, "\x1b[C", 6);
        Assert.Equal((0, 6), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal((0, markerStart), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal(0, editor.GetCursor().Line);
        Assert.Equal(markerEnd, editor.GetCursor().Col);
        editor.HandleInput("\x1b[A");
        Assert.Equal((0, markerStart), editor.GetCursor());
        editor.HandleInput("\x1b[A");
        Assert.Equal((0, 6), editor.GetCursor());
    }

    [Fact(DisplayName = "skips marker continuation VLs when preferred col falls in marker tail")]
    public void Skips_marker_continuation_VLs_when_preferred_col_falls_in_marker_tail()
    {
        var editor = CreateEditor(20, 24);
        foreach (var character in "abcdefgh") editor.HandleInput(character.ToString());
        Paste(editor, RepeatedLines("line", 100));
        foreach (var character in "ijklmnopqr") editor.HandleInput(character.ToString());
        editor.HandleInput("\n");
        foreach (var character in "123456789012345678") editor.HandleInput(character.ToString());
        editor.Render(20);

        editor.HandleInput("\x1b[A");
        editor.HandleInput("\x01");
        RepeatInput(editor, "\x1b[C", 3);
        Assert.Equal((0, 3), editor.GetCursor());
        editor.HandleInput("\x1b[B");
        Assert.Equal(8, editor.GetCursor().Col);
        editor.HandleInput("\x1b[B");
        Assert.Equal((1, 3), editor.GetCursor());
        editor.HandleInput("\x1b[A");
        Assert.Equal(8, editor.GetCursor().Col);
        editor.HandleInput("\x1b[A");
        Assert.Equal((0, 3), editor.GetCursor());
    }

    [Fact(DisplayName = "submits large pasted content literally")]
    public void Submits_large_pasted_content_literally()
    {
        var editor = CreateEditor();
        var pastedText = LiteralPasteText();
        var submitted = string.Empty;
        editor.OnSubmit = text => submitted = text;
        Paste(editor, pastedText);
        editor.HandleInput("\r");
        Assert.Equal(pastedText, submitted);
    }

    private static string PasteWithMarker(Editor editor)
    {
        Paste(editor, RepeatedLines("line", 20));
        return editor.GetText();
    }

    private static string BigPaste(string tag) =>
        string.Join('\n', Enumerable.Range(0, 12).Select(index => $"{tag}{index}"));

    private static string RepeatedLines(string line, int count) =>
        string.Join('\n', Enumerable.Repeat(line, count));

    private static void Paste(Editor editor, string text) =>
        editor.HandleInput($"\x1b[200~{text}\x1b[201~");

    private static string GetLineMarker(Editor editor)
    {
        var match = _lineMarkerRegex.Match(editor.GetText());
        Assert.True(match.Success, "paste marker should be created");
        return match.Value;
    }

    private static void RepeatInput(Editor editor, string input, int count)
    {
        for (var index = 0; index < count; index++) editor.HandleInput(input);
    }

    private static string LiteralPasteText() => string.Join(
        '\n',
        "line 1",
        "line 2",
        "line 3",
        "line 4",
        "line 5",
        "line 6",
        "line 7",
        "line 8",
        "line 9",
        "line 10",
        "tokens $1 $2 $& $$ $` $' end");
}
