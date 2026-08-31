using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorNavigationTests
{
    [Fact(DisplayName = "jumps forward to first occurrence of character on same line")]
    public void Jumps_forward_to_first_occurrence_of_character_on_same_line()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01");
        editor.HandleInput("\x1d"); editor.HandleInput("o"); Assert.Equal((0, 4), editor.GetCursor());
    }

    [Fact(DisplayName = "jumps forward to next occurrence after cursor")]
    public void Jumps_forward_to_next_occurrence_after_cursor()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01"); MoveRight(editor, 4);
        editor.HandleInput("\x1d"); editor.HandleInput("o"); Assert.Equal((0, 7), editor.GetCursor());
    }

    [Fact(DisplayName = "jumps forward across multiple lines")]
    public void Jumps_forward_across_multiple_lines()
    {
        var editor = CreateEditor(); editor.SetText("abc\ndef\nghi"); editor.HandleInput("\x1b[A"); editor.HandleInput("\x1b[A");
        editor.HandleInput("\x01"); editor.HandleInput("\x1d"); editor.HandleInput("g"); Assert.Equal((2, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "jumps backward to first occurrence before cursor on same line")]
    public void Jumps_backward_to_first_occurrence_before_cursor_on_same_line()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x1b\x1d"); editor.HandleInput("o");
        Assert.Equal((0, 7), editor.GetCursor());
    }

    [Fact(DisplayName = "jumps backward across multiple lines")]
    public void Jumps_backward_across_multiple_lines()
    {
        var editor = CreateEditor(); editor.SetText("abc\ndef\nghi"); editor.HandleInput("\x1b\x1d"); editor.HandleInput("a");
        Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "does nothing when character is not found (forward)")]
    public void Does_nothing_when_character_is_not_found_forward()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01");
        editor.HandleInput("\x1d"); editor.HandleInput("z"); Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "does nothing when character is not found (backward)")]
    public void Does_nothing_when_character_is_not_found_backward()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x1b\x1d"); editor.HandleInput("z");
        Assert.Equal((0, 11), editor.GetCursor());
    }

    [Fact(DisplayName = "is case-sensitive")]
    public void Is_case_sensitive()
    {
        var editor = CreateEditor(); editor.SetText("Hello World"); editor.HandleInput("\x01");
        editor.HandleInput("\x1d"); editor.HandleInput("h"); Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("\x1d"); editor.HandleInput("W"); Assert.Equal((0, 6), editor.GetCursor());
    }

    [Fact(DisplayName = "cancels jump mode when Ctrl+] is pressed again")]
    public void Cancels_jump_mode_when_Ctrl_right_bracket_is_pressed_again()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01");
        editor.HandleInput("\x1d"); editor.HandleInput("\x1d"); editor.HandleInput("o"); Assert.Equal("ohello world", editor.GetText());
    }

    [Fact(DisplayName = "cancels jump mode on Escape and processes the Escape")]
    public void Cancels_jump_mode_on_Escape_and_processes_the_Escape()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01");
        editor.HandleInput("\x1d"); editor.HandleInput("\x1b"); Assert.Equal((0, 0), editor.GetCursor());
        editor.HandleInput("o"); Assert.Equal("ohello world", editor.GetText());
    }

    [Fact(DisplayName = "cancels backward jump mode when Ctrl+Alt+] is pressed again")]
    public void Cancels_backward_jump_mode_when_Ctrl_Alt_right_bracket_is_pressed_again()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x1b\x1d"); editor.HandleInput("\x1b\x1d");
        editor.HandleInput("o"); Assert.Equal("hello worldo", editor.GetText());
    }

    [Fact(DisplayName = "searches for special characters")]
    public void Searches_for_special_characters()
    {
        var editor = CreateEditor(); editor.SetText("foo(bar) = baz;"); editor.HandleInput("\x01");
        editor.HandleInput("\x1d"); editor.HandleInput("("); Assert.Equal((0, 3), editor.GetCursor());
        editor.HandleInput("\x1d"); editor.HandleInput("="); Assert.Equal((0, 9), editor.GetCursor());
    }

    [Fact(DisplayName = "handles empty text gracefully")]
    public void Handles_empty_text_gracefully()
    {
        var editor = CreateEditor(); editor.SetText(string.Empty); editor.HandleInput("\x1d"); editor.HandleInput("x");
        Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "resets lastAction when jumping")]
    public void Resets_lastAction_when_jumping()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01"); editor.HandleInput("x");
        editor.HandleInput("\x1d"); editor.HandleInput("o"); editor.HandleInput("Y"); Assert.Equal("xhellYo world", editor.GetText());
        editor.HandleInput("\x1b[45;5u"); Assert.Equal("xhello world", editor.GetText());
    }

    [Fact(DisplayName = "preserves target column when moving up through a shorter line")]
    public void Preserves_target_column_when_moving_up_through_a_shorter_line()
    {
        var editor = CreateEditor(); editor.SetText("2222222222x222\n\n1111111111_111111111111"); editor.HandleInput("\x01"); MoveRight(editor, 10);
        editor.HandleInput("\x1b[A"); Assert.Equal((1, 0), editor.GetCursor()); editor.HandleInput("\x1b[A"); Assert.Equal((0, 10), editor.GetCursor());
    }

    [Fact(DisplayName = "preserves target column when moving down through a shorter line")]
    public void Preserves_target_column_when_moving_down_through_a_shorter_line()
    {
        var editor = CreateEditor(); editor.SetText("1111111111_111\n\n2222222222x222222222222"); Up(editor, 2); editor.HandleInput("\x01"); MoveRight(editor, 10);
        editor.HandleInput("\x1b[B"); Assert.Equal((1, 0), editor.GetCursor()); editor.HandleInput("\x1b[B"); Assert.Equal((2, 10), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on horizontal movement (left arrow)")]
    public void Resets_sticky_column_on_horizontal_movement_left_arrow()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 5);
        Up(editor, 2); Assert.Equal((0, 5), editor.GetCursor()); editor.HandleInput("\x1b[D"); Assert.Equal((0, 4), editor.GetCursor());
        Down(editor, 2); Assert.Equal((2, 4), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on horizontal movement (right arrow)")]
    public void Resets_sticky_column_on_horizontal_movement_right_arrow()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); Up(editor, 2); editor.HandleInput("\x01"); MoveRight(editor, 5);
        Down(editor, 2); Assert.Equal((2, 5), editor.GetCursor()); editor.HandleInput("\x1b[C"); Assert.Equal((2, 6), editor.GetCursor());
        Up(editor, 2); Assert.Equal((0, 6), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on typing")]
    public void Resets_sticky_column_on_typing()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 8);
        Up(editor, 2); Assert.Equal((0, 8), editor.GetCursor()); editor.HandleInput("X"); Assert.Equal((0, 9), editor.GetCursor());
        Down(editor, 2); Assert.Equal((2, 9), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on backspace")]
    public void Resets_sticky_column_on_backspace()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 8);
        Up(editor, 2); editor.HandleInput("\x7f"); Assert.Equal((0, 7), editor.GetCursor()); Down(editor, 2); Assert.Equal((2, 7), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on Ctrl+A (move to line start)")]
    public void Resets_sticky_column_on_Ctrl_A_move_to_line_start()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 8);
        editor.HandleInput("\x1b[A"); editor.HandleInput("\x01"); Assert.Equal((1, 0), editor.GetCursor());
        editor.HandleInput("\x1b[A"); Assert.Equal((0, 0), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on Ctrl+E (move to line end)")]
    public void Resets_sticky_column_on_Ctrl_E_move_to_line_end()
    {
        var editor = CreateEditor(); editor.SetText("12345\n\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 3);
        Up(editor, 2); Assert.Equal((0, 3), editor.GetCursor()); editor.HandleInput("\x05"); Assert.Equal((0, 5), editor.GetCursor());
        Down(editor, 2); Assert.Equal((2, 5), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on word movement (Ctrl+Left)")]
    public void Resets_sticky_column_on_word_movement_Ctrl_Left()
    {
        var editor = CreateEditor(); editor.SetText("hello world\n\nhello world"); Up(editor, 2); Assert.Equal((0, 11), editor.GetCursor());
        editor.HandleInput("\x1b[1;5D"); Assert.Equal((0, 6), editor.GetCursor()); Down(editor, 2); Assert.Equal((2, 6), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on word movement (Ctrl+Right)")]
    public void Resets_sticky_column_on_word_movement_Ctrl_Right()
    {
        var editor = CreateEditor(); editor.SetText("hello world\n\nhello world"); Up(editor, 2); editor.HandleInput("\x01");
        Down(editor, 2); Assert.Equal((2, 0), editor.GetCursor()); editor.HandleInput("\x1b[1;5C"); Assert.Equal((2, 5), editor.GetCursor());
        Up(editor, 2); Assert.Equal((0, 5), editor.GetCursor());
    }

    [Fact(DisplayName = "resets sticky column on undo")]
    public void Resets_sticky_column_on_undo()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); Up(editor, 2); editor.HandleInput("\x01"); MoveRight(editor, 8);
        Down(editor, 2); editor.HandleInput("X"); Assert.Equal((2, 9), editor.GetCursor()); Up(editor, 2); Assert.Equal((0, 9), editor.GetCursor());
        editor.HandleInput("\x1b[45;5u"); Assert.Equal("1234567890\n\n1234567890", editor.GetText()); Assert.Equal((2, 8), editor.GetCursor());
        Up(editor, 2); Assert.Equal((0, 8), editor.GetCursor());
    }

    [Fact(DisplayName = "handles multiple consecutive up/down movements")]
    public void Handles_multiple_consecutive_up_down_movements()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\nab\ncd\nef\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 7);
        Up(editor, 4); Assert.Equal((0, 7), editor.GetCursor()); Down(editor, 4); Assert.Equal((4, 7), editor.GetCursor());
    }

    [Fact(DisplayName = "moves correctly through wrapped visual lines without getting stuck")]
    public void Moves_correctly_through_wrapped_visual_lines_without_getting_stuck()
    {
        var editor = CreateEditor(15, 24); editor.SetText("short\n123456789012345678901234567890"); _ = editor.Render(15);
        editor.HandleInput("\x1b[A"); Assert.Equal(1, editor.GetCursor().Line); editor.HandleInput("\x1b[A"); Assert.Equal(1, editor.GetCursor().Line);
        editor.HandleInput("\x1b[A"); Assert.Equal(0, editor.GetCursor().Line);
    }

    [Fact(DisplayName = "handles setText resetting sticky column")]
    public void Handles_setText_resetting_sticky_column()
    {
        var editor = CreateEditor(); editor.SetText("1234567890\n\n1234567890"); editor.HandleInput("\x01"); MoveRight(editor, 8); editor.HandleInput("\x1b[A");
        editor.SetText("abcdefghij\n\nabcdefghij"); Assert.Equal((2, 10), editor.GetCursor()); Up(editor, 2); Assert.Equal((0, 10), editor.GetCursor());
    }

    [Fact(DisplayName = "sets preferredVisualCol when pressing right at end of prompt (last line)")]
    public void Sets_preferredVisualCol_when_pressing_right_at_end_of_prompt_last_line()
    {
        var editor = CreateEditor(); editor.SetText("111111111x1111111111\n\n333333333_"); Up(editor, 2); editor.HandleInput("\x05");
        Down(editor, 2); Assert.Equal((2, 10), editor.GetCursor()); editor.HandleInput("\x1b[C"); Assert.Equal((2, 10), editor.GetCursor());
        Up(editor, 2); Assert.Equal((0, 10), editor.GetCursor());
    }

    [Fact(DisplayName = "handles editor resizes when preferredVisualCol is on the same line")]
    public void Handles_editor_resizes_when_preferredVisualCol_is_on_the_same_line()
    {
        var editor = CreateEditor(80, 24); editor.SetText("12345678901234567890\n\n12345678901234567890"); editor.HandleInput("\x01"); MoveRight(editor, 15);
        Up(editor, 2); Assert.Equal((0, 15), editor.GetCursor()); _ = editor.Render(12); Down(editor, 2); Assert.Equal(4, editor.GetCursor().Col);
    }

    [Fact(DisplayName = "handles editor resizes when preferredVisualCol is on a different line")]
    public void Handles_editor_resizes_when_preferredVisualCol_is_on_a_different_line()
    {
        var editor = CreateEditor(80, 24); editor.SetText("short\n12345678901234567890"); editor.HandleInput("\x01"); MoveRight(editor, 15);
        editor.HandleInput("\x1b[A"); Assert.Equal((0, 5), editor.GetCursor()); _ = editor.Render(10); editor.HandleInput("\x1b[B");
        Assert.Equal((1, 8), editor.GetCursor()); editor.HandleInput("\x1b[A"); Assert.Equal((0, 5), editor.GetCursor());
        _ = editor.Render(80); editor.HandleInput("\x1b[B"); Assert.Equal((1, 15), editor.GetCursor());
    }

    [Fact(DisplayName = "rewrapped lines: target fits current visual column")]
    public void Rewrapped_lines_target_fits_current_visual_column()
    {
        var editor = CreateEditor(80, 24); editor.SetText("abcdefghijklmnopqr\n123456789012345678"); PositionCursor(editor, 0, 18);
        _ = editor.Render(10); editor.HandleInput("\x1b[B"); Assert.Equal((1, 8), editor.GetCursor());
        _ = editor.Render(80); editor.HandleInput("\x1b[A"); Assert.Equal((0, 8), editor.GetCursor());
        editor.HandleInput("\x1b[B"); Assert.Equal((1, 8), editor.GetCursor());
    }

    [Fact(DisplayName = "rewrapped lines: target shorter than current visual column")]
    public void Rewrapped_lines_target_shorter_than_current_visual_column()
    {
        var editor = CreateEditor(80, 24); editor.SetText("abcdefghijklmnopqr\n123456789012345678\nab"); PositionCursor(editor, 0, 18);
        _ = editor.Render(10); editor.HandleInput("\x1b[B"); Assert.Equal((1, 8), editor.GetCursor());
        _ = editor.Render(80); editor.HandleInput("\x1b[B"); Assert.Equal((2, 2), editor.GetCursor());
        editor.HandleInput("\x1b[A"); Assert.Equal((1, 8), editor.GetCursor());
    }

    private static void PositionCursor(Editor editor, int line, int column)
    {
        Up(editor, 20); Down(editor, line); editor.HandleInput("\x01"); MoveRight(editor, column);
    }

    private static void MoveRight(Editor editor, int count)
    {
        for (var index = 0; index < count; index++) editor.HandleInput("\x1b[C");
    }

    private static void Up(Editor editor, int count)
    {
        for (var index = 0; index < count; index++) editor.HandleInput("\x1b[A");
    }

    private static void Down(Editor editor, int count)
    {
        for (var index = 0; index < count; index++) editor.HandleInput("\x1b[B");
    }
}
