using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorKillRingAndUndoTests
{
    private const string _undo = "\x1b[45;5u";

    [Fact(DisplayName = "Ctrl+W saves deleted text to kill ring and Ctrl+Y yanks it")]
    public void Ctrl_W_saves_deleted_text_to_kill_ring_and_Ctrl_Y_yanks_it()
    {
        var editor = CreateEditor();
        editor.SetText("foo bar baz"); editor.HandleInput("\x17"); Assert.Equal("foo bar ", editor.GetText());
        editor.HandleInput("\x01"); editor.HandleInput("\x19"); Assert.Equal("bazfoo bar ", editor.GetText());
    }

    [Fact(DisplayName = "Ctrl+U saves deleted text to kill ring")]
    public void Ctrl_U_saves_deleted_text_to_kill_ring()
    {
        var editor = CreateEditor();
        editor.SetText("hello world"); editor.HandleInput("\x01");
        MoveRight(editor, 6); editor.HandleInput("\x15"); Assert.Equal("world", editor.GetText());
        editor.HandleInput("\x19"); Assert.Equal("hello world", editor.GetText());
    }

    [Fact(DisplayName = "Ctrl+K saves deleted text to kill ring")]
    public void Ctrl_K_saves_deleted_text_to_kill_ring()
    {
        var editor = CreateEditor();
        editor.SetText("hello world"); editor.HandleInput("\x01"); editor.HandleInput("\x0b");
        Assert.Equal(string.Empty, editor.GetText()); editor.HandleInput("\x19"); Assert.Equal("hello world", editor.GetText());
    }

    [Fact(DisplayName = "Ctrl+Y does nothing when kill ring is empty")]
    public void Ctrl_Y_does_nothing_when_kill_ring_is_empty()
    {
        var editor = CreateEditor(); editor.SetText("test"); editor.HandleInput("\x19"); Assert.Equal("test", editor.GetText());
    }

    [Fact(DisplayName = "Alt+Y cycles through kill ring after Ctrl+Y")]
    public void Alt_Y_cycles_through_kill_ring_after_Ctrl_Y()
    {
        var editor = CreateEditor();
        Kill(editor, "first"); Kill(editor, "second"); Kill(editor, "third");
        Assert.Equal(string.Empty, editor.GetText());
        editor.HandleInput("\x19"); Assert.Equal("third", editor.GetText());
        editor.HandleInput("\x1by"); Assert.Equal("second", editor.GetText());
        editor.HandleInput("\x1by"); Assert.Equal("first", editor.GetText());
        editor.HandleInput("\x1by"); Assert.Equal("third", editor.GetText());
    }

    [Fact(DisplayName = "Alt+Y does nothing if not preceded by yank")]
    public void Alt_Y_does_nothing_if_not_preceded_by_yank()
    {
        var editor = CreateEditor(); Kill(editor, "test"); editor.SetText("other"); editor.HandleInput("x");
        Assert.Equal("otherx", editor.GetText()); editor.HandleInput("\x1by"); Assert.Equal("otherx", editor.GetText());
    }

    [Fact(DisplayName = "Alt+Y does nothing if kill ring has ≤1 entry")]
    public void Alt_Y_does_nothing_if_kill_ring_has_at_most_1_entry()
    {
        var editor = CreateEditor(); Kill(editor, "only"); editor.HandleInput("\x19");
        Assert.Equal("only", editor.GetText()); editor.HandleInput("\x1by"); Assert.Equal("only", editor.GetText());
    }

    [Fact(DisplayName = "consecutive Ctrl+W accumulates into one kill ring entry")]
    public void Consecutive_Ctrl_W_accumulates_into_one_kill_ring_entry()
    {
        var editor = CreateEditor(); editor.SetText("one two three");
        editor.HandleInput("\x17"); editor.HandleInput("\x17"); editor.HandleInput("\x17");
        Assert.Equal(string.Empty, editor.GetText()); editor.HandleInput("\x19"); Assert.Equal("one two three", editor.GetText());
    }

    [Fact(DisplayName = "Ctrl+U accumulates multiline deletes including newlines")]
    public void Ctrl_U_accumulates_multiline_deletes_including_newlines()
    {
        var editor = CreateEditor(); editor.SetText("line1\nline2\nline3");
        foreach (var expected in new[] { "line1\nline2\n", "line1\nline2", "line1\n", "line1", string.Empty })
        {
            editor.HandleInput("\x15"); Assert.Equal(expected, editor.GetText());
        }
        editor.HandleInput("\x19"); Assert.Equal("line1\nline2\nline3", editor.GetText());
    }

    [Fact(DisplayName = "backward deletions prepend, forward deletions append during accumulation")]
    public void Backward_deletions_prepend_forward_deletions_append_during_accumulation()
    {
        var editor = CreateEditor(); editor.SetText("prefix|suffix"); editor.HandleInput("\x01"); MoveRight(editor, 6);
        editor.HandleInput("\x0b"); editor.HandleInput("\x0b"); Assert.Equal("prefix", editor.GetText());
        editor.HandleInput("\x19"); Assert.Equal("prefix|suffix", editor.GetText());
    }

    [Fact(DisplayName = "non-delete actions break kill accumulation")]
    public void Non_delete_actions_break_kill_accumulation()
    {
        var editor = CreateEditor(); editor.SetText("foo bar baz"); editor.HandleInput("\x17");
        Assert.Equal("foo bar ", editor.GetText()); editor.HandleInput("x"); Assert.Equal("foo bar x", editor.GetText());
        editor.HandleInput("\x17"); Assert.Equal("foo bar ", editor.GetText());
        editor.HandleInput("\x19"); Assert.Equal("foo bar x", editor.GetText());
        editor.HandleInput("\x1by"); Assert.Equal("foo bar baz", editor.GetText());
    }

    [Fact(DisplayName = "non-yank actions break Alt+Y chain")]
    public void Non_yank_actions_break_Alt_Y_chain()
    {
        var editor = CreateEditor(); Kill(editor, "first"); Kill(editor, "second"); editor.SetText(string.Empty);
        editor.HandleInput("\x19"); Assert.Equal("second", editor.GetText()); editor.HandleInput("x");
        Assert.Equal("secondx", editor.GetText()); editor.HandleInput("\x1by"); Assert.Equal("secondx", editor.GetText());
    }

    [Fact(DisplayName = "kill ring rotation persists after cycling")]
    public void Kill_ring_rotation_persists_after_cycling()
    {
        var editor = CreateEditor(); Kill(editor, "first"); Kill(editor, "second"); Kill(editor, "third");
        editor.SetText(string.Empty); editor.HandleInput("\x19"); editor.HandleInput("\x1by"); Assert.Equal("second", editor.GetText());
        editor.HandleInput("x"); editor.SetText(string.Empty); editor.HandleInput("\x19"); Assert.Equal("second", editor.GetText());
    }

    [Fact(DisplayName = "consecutive deletions across lines coalesce into one entry")]
    public void Consecutive_deletions_across_lines_coalesce_into_one_entry()
    {
        var editor = CreateEditor(); editor.SetText("1\n2\n3");
        foreach (var expected in new[] { "1\n2\n", "1\n2", "1\n", "1", string.Empty })
        {
            editor.HandleInput("\x17"); Assert.Equal(expected, editor.GetText());
        }
        editor.HandleInput("\x19"); Assert.Equal("1\n2\n3", editor.GetText());
    }

    [Fact(DisplayName = "Ctrl+K at line end deletes newline and coalesces")]
    public void Ctrl_K_at_line_end_deletes_newline_and_coalesces()
    {
        var editor = CreateEditor(); Type(editor, "ab"); editor.HandleInput("\n"); Type(editor, "cd");
        editor.HandleInput("\x1b[A"); editor.HandleInput("\x05"); editor.HandleInput("\x0b"); Assert.Equal("abcd", editor.GetText());
        editor.HandleInput("\x0b"); Assert.Equal("ab", editor.GetText()); editor.HandleInput("\x19"); Assert.Equal("ab\ncd", editor.GetText());
    }

    [Fact(DisplayName = "handles yank in middle of text")]
    public void Handles_yank_in_middle_of_text()
    {
        var editor = CreateEditor(); Kill(editor, "word"); editor.SetText("hello world"); editor.HandleInput("\x01");
        MoveRight(editor, 6); editor.HandleInput("\x19"); Assert.Equal("hello wordworld", editor.GetText());
    }

    [Fact(DisplayName = "handles yank-pop in middle of text")]
    public void Handles_yank_pop_in_middle_of_text()
    {
        var editor = CreateEditor(); Kill(editor, "FIRST"); Kill(editor, "SECOND"); editor.SetText("hello world");
        editor.HandleInput("\x01"); MoveRight(editor, 6); editor.HandleInput("\x19"); Assert.Equal("hello SECONDworld", editor.GetText());
        editor.HandleInput("\x1by"); Assert.Equal("hello FIRSTworld", editor.GetText());
    }

    [Fact(DisplayName = "multiline yank and yank-pop in middle of text")]
    public void Multiline_yank_and_yank_pop_in_middle_of_text()
    {
        var editor = CreateEditor(); Kill(editor, "SINGLE"); editor.SetText("A\nB");
        editor.HandleInput("\x15"); editor.HandleInput("\x15"); editor.HandleInput("\x15");
        editor.SetText("hello world"); editor.HandleInput("\x01"); MoveRight(editor, 6); editor.HandleInput("\x19");
        Assert.Equal("hello A\nBworld", editor.GetText()); editor.HandleInput("\x1by"); Assert.Equal("hello SINGLEworld", editor.GetText());
    }

    [Fact(DisplayName = "Alt+D deletes word forward and saves to kill ring")]
    public void Alt_D_deletes_word_forward_and_saves_to_kill_ring()
    {
        var editor = CreateEditor(); editor.SetText("hello world test"); editor.HandleInput("\x01");
        editor.HandleInput("\u001bd"); Assert.Equal(" world test", editor.GetText());
        editor.HandleInput("\u001bd"); Assert.Equal(" test", editor.GetText());
        editor.HandleInput("\x19"); Assert.Equal("hello world test", editor.GetText());
    }

    [Fact(DisplayName = "Alt+D at end of line deletes newline")]
    public void Alt_D_at_end_of_line_deletes_newline()
    {
        var editor = CreateEditor(); editor.SetText("line1\nline2"); editor.HandleInput("\x1b[A"); editor.HandleInput("\x05");
        editor.HandleInput("\u001bd"); Assert.Equal("line1line2", editor.GetText());
        editor.HandleInput("\x19"); Assert.Equal("line1\nline2", editor.GetText());
    }

    [Fact(DisplayName = "does nothing when undo stack is empty")]
    public void Does_nothing_when_undo_stack_is_empty()
    {
        var editor = CreateEditor(); editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "coalesces consecutive word characters into one undo unit")]
    public void Coalesces_consecutive_word_characters_into_one_undo_unit()
    {
        var editor = CreateEditor(); Type(editor, "hello world"); Assert.Equal("hello world", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello", editor.GetText()); editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "undoes spaces one at a time")]
    public void Undoes_spaces_one_at_a_time()
    {
        var editor = CreateEditor(); Type(editor, "hello  "); Assert.Equal("hello  ", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello ", editor.GetText()); editor.HandleInput(_undo); Assert.Equal("hello", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "undoes newlines and signals next word to capture state")]
    public void Undoes_newlines_and_signals_next_word_to_capture_state()
    {
        var editor = CreateEditor(); Type(editor, "hello"); editor.HandleInput("\n"); Type(editor, "world");
        Assert.Equal("hello\nworld", editor.GetText()); editor.HandleInput(_undo); Assert.Equal("hello\n", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello", editor.GetText()); editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "undoes backspace")]
    public void Undoes_backspace()
    {
        var editor = CreateEditor(); Type(editor, "hello"); editor.HandleInput("\x7f"); Assert.Equal("hell", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello", editor.GetText());
    }

    [Fact(DisplayName = "undoes forward delete")]
    public void Undoes_forward_delete()
    {
        var editor = CreateEditor(); Type(editor, "hello"); editor.HandleInput("\x01"); editor.HandleInput("\x1b[C");
        editor.HandleInput("\x1b[3~"); Assert.Equal("hllo", editor.GetText()); editor.HandleInput(_undo); Assert.Equal("hello", editor.GetText());
    }

    [Fact(DisplayName = "undoes Ctrl+W (delete word backward)")]
    public void Undoes_Ctrl_W_delete_word_backward()
    {
        var editor = CreateEditor(); Type(editor, "hello world"); editor.HandleInput("\x17"); Assert.Equal("hello ", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText());
    }

    [Fact(DisplayName = "undoes Ctrl+K (delete to line end)")]
    public void Undoes_Ctrl_K_delete_to_line_end()
    {
        var editor = CreateEditor(); Type(editor, "hello world"); editor.HandleInput("\x01"); MoveRight(editor, 6);
        editor.HandleInput("\x0b"); Assert.Equal("hello ", editor.GetText()); editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText());
        editor.HandleInput("|"); Assert.Equal("hello |world", editor.GetText());
    }

    [Fact(DisplayName = "undoes Ctrl+U (delete to line start)")]
    public void Undoes_Ctrl_U_delete_to_line_start()
    {
        var editor = CreateEditor(); Type(editor, "hello world"); editor.HandleInput("\x01"); MoveRight(editor, 6);
        editor.HandleInput("\x15"); Assert.Equal("world", editor.GetText()); editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText());
    }

    [Fact(DisplayName = "undoes yank")]
    public void Undoes_yank()
    {
        var editor = CreateEditor(); Type(editor, "hello "); editor.HandleInput("\x17"); editor.HandleInput("\x19");
        Assert.Equal("hello ", editor.GetText()); editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "undoes single-line paste atomically")]
    public void Undoes_single_line_paste_atomically()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01"); MoveRight(editor, 5);
        editor.HandleInput("\x1b[200~beep boop\x1b[201~"); Assert.Equal("hellobeep boop world", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText()); editor.HandleInput("|"); Assert.Equal("hello| world", editor.GetText());
    }

    [Fact(DisplayName = "does not trigger autocomplete during single-line paste")]
    public void Does_not_trigger_autocomplete_during_single_line_paste()
    {
        var editor = CreateEditor(); var suggestionCalls = 0;
        editor.SetAutocompleteProvider(new TestAutocompleteProvider
        {
            GetSuggestionsHandler = (_, _, _, _) => { suggestionCalls++; return ValueTask.FromResult<AutocompleteSuggestions?>(null); },
        });
        editor.HandleInput("\x1b[200~look at @node_modules/react/index.js please\x1b[201~");
        Assert.Equal("look at @node_modules/react/index.js please", editor.GetText()); Assert.Equal(0, suggestionCalls);
        Assert.False(editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "decodes CSI-u Ctrl+letter sequences inside bracketed paste (tmux popup)")]
    public void Decodes_CSI_u_Ctrl_letter_sequences_inside_bracketed_paste_tmux_popup()
    {
        var editor = CreateEditor(); editor.HandleInput("\x1b[200~line1\x1b[106;5uline2\x1b[106;5uline3\x1b[201~");
        Assert.Equal("line1\nline2\nline3", editor.GetText());
    }

    [Fact(DisplayName = "undoes multi-line paste atomically")]
    public void Undoes_multi_line_paste_atomically()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01"); MoveRight(editor, 5);
        editor.HandleInput("\x1b[200~line1\nline2\nline3\x1b[201~"); Assert.Equal("helloline1\nline2\nline3 world", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText()); editor.HandleInput("|"); Assert.Equal("hello| world", editor.GetText());
    }

    [Fact(DisplayName = "undoes insertTextAtCursor atomically")]
    public void Undoes_insertTextAtCursor_atomically()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01"); MoveRight(editor, 5);
        editor.InsertTextAtCursor("/tmp/image.png"); Assert.Equal("hello/tmp/image.png world", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText()); editor.HandleInput("|"); Assert.Equal("hello| world", editor.GetText());
    }

    [Fact(DisplayName = "insertTextAtCursor handles multiline text")]
    public void InsertTextAtCursor_handles_multiline_text()
    {
        var editor = CreateEditor(); editor.SetText("hello world"); editor.HandleInput("\x01"); MoveRight(editor, 5);
        editor.InsertTextAtCursor("line1\nline2\nline3"); Assert.Equal("helloline1\nline2\nline3 world", editor.GetText());
        Assert.Equal((2, 5), editor.GetCursor()); editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText());
    }

    [Fact(DisplayName = "insertTextAtCursor normalizes CRLF and CR line endings")]
    public void InsertTextAtCursor_normalizes_CRLF_and_CR_line_endings()
    {
        var editor = CreateEditor(); editor.SetText(string.Empty); editor.InsertTextAtCursor("a\r\nb\r\nc");
        Assert.Equal("a\nb\nc", editor.GetText()); editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
        editor.InsertTextAtCursor("x\ry\rz"); Assert.Equal("x\ny\nz", editor.GetText());
    }

    [Fact(DisplayName = "undoes setText to empty string")]
    public void Undoes_setText_to_empty_string()
    {
        var editor = CreateEditor(); Type(editor, "hello world"); editor.SetText(string.Empty); Assert.Equal(string.Empty, editor.GetText());
        editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText());
    }

    [Fact(DisplayName = "clears undo stack on submit")]
    public void Clears_undo_stack_on_submit()
    {
        var editor = CreateEditor(); var submitted = string.Empty; editor.OnSubmit = text => submitted = text;
        Type(editor, "hello"); editor.HandleInput("\r"); Assert.Equal("hello", submitted); Assert.Equal(string.Empty, editor.GetText());
        editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "exits history browsing mode on undo")]
    public void Exits_history_browsing_mode_on_undo()
    {
        var editor = CreateEditor(); editor.AddToHistory("hello"); Type(editor, "world"); editor.HandleInput("\x17");
        Assert.Equal(string.Empty, editor.GetText()); editor.HandleInput("\x1b[A"); Assert.Equal("hello", editor.GetText());
        editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText()); editor.HandleInput(_undo); Assert.Equal("world", editor.GetText());
    }

    [Fact(DisplayName = "undo restores to pre-history state even after multiple history navigations")]
    public void Undo_restores_to_pre_history_state_even_after_multiple_history_navigations()
    {
        var editor = CreateEditor(); editor.AddToHistory("first"); editor.AddToHistory("second"); editor.AddToHistory("third");
        Type(editor, "current"); editor.HandleInput("\x17"); Assert.Equal(string.Empty, editor.GetText());
        foreach (var expected in new[] { "third", "second", "first" }) { editor.HandleInput("\x1b[A"); Assert.Equal(expected, editor.GetText()); }
        editor.HandleInput(_undo); Assert.Equal(string.Empty, editor.GetText()); editor.HandleInput(_undo); Assert.Equal("current", editor.GetText());
    }

    [Fact(DisplayName = "cursor movement starts new undo unit")]
    public void Cursor_movement_starts_new_undo_unit()
    {
        var editor = CreateEditor(); Type(editor, "hello world");
        for (var index = 0; index < 5; index++) editor.HandleInput("\x1b[D"); Type(editor, "lol");
        Assert.Equal("hello lolworld", editor.GetText()); editor.HandleInput(_undo); Assert.Equal("hello world", editor.GetText());
        editor.HandleInput("|"); Assert.Equal("hello |world", editor.GetText());
    }

    [Fact(DisplayName = "no-op delete operations do not push undo snapshots")]
    public void No_op_delete_operations_do_not_push_undo_snapshots()
    {
        var editor = CreateEditor(); Type(editor, "hello"); editor.HandleInput("\x17"); Assert.Equal(string.Empty, editor.GetText());
        editor.HandleInput("\x17"); editor.HandleInput("\x17"); editor.HandleInput(_undo); Assert.Equal("hello", editor.GetText());
    }

    [Fact(DisplayName = "undoes autocomplete")]
    public async Task Undoes_autocomplete()
    {
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(new TestAutocompleteProvider
        {
            GetSuggestionsHandler = (lines, _, cursorColumn, _) =>
            {
                var prefix = lines[0][..cursorColumn];
                return ValueTask.FromResult(prefix == "di"
                    ? new AutocompleteSuggestions
                    {
                        Items = [new AutocompleteItem { Value = "dist/", Label = "dist/" }],
                        Prefix = "di",
                    }
                    : null);
            },
        });
        editor.HandleInput("d"); editor.HandleInput("i"); editor.HandleInput("\t");
        await WaitForConditionAsync(
            () => editor.GetText() == "dist/",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(editor.IsShowingAutocomplete()); editor.HandleInput(_undo); Assert.Equal("di", editor.GetText());
    }

    private static void Type(Editor editor, string text)
    {
        foreach (var character in text) editor.HandleInput(character.ToString());
    }

    private static void MoveRight(Editor editor, int count)
    {
        for (var index = 0; index < count; index++) editor.HandleInput("\x1b[C");
    }

    private static void Kill(Editor editor, string text)
    {
        editor.SetText(text);
        editor.HandleInput("\x17");
    }
}
