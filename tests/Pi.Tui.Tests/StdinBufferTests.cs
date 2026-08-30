using System.Text;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream partial escape-sequence and bracketed-paste cases.</summary>
public sealed class StdinBufferTests : IDisposable
{
    private StdinBuffer _buffer;
    private List<string> _emittedSequences;
    private List<string> _emittedPaste;

    public StdinBufferTests()
    {
        _emittedSequences = [];
        _emittedPaste = [];
        _buffer = CreateBuffer(new StdinBufferOptions { Timeout = 10 });
    }

    [Fact(DisplayName = "should pass through regular characters immediately")]
    public void Passes_through_regular_characters_immediately()
    {
        ProcessInput("a");
        Assert.Equal(["a"], _emittedSequences);
    }

    [Fact(DisplayName = "should pass through multiple regular characters")]
    public void Passes_through_multiple_regular_characters()
    {
        ProcessInput("abc");
        Assert.Equal(["a", "b", "c"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle unicode characters")]
    public void Handles_unicode_characters()
    {
        ProcessInput("hello 世界");
        Assert.Equal(["h", "e", "l", "l", "o", " ", "世", "界"], _emittedSequences);
    }

    [Fact(DisplayName = "should pass through complete mouse SGR sequences")]
    public void Passes_through_complete_mouse_sgr_sequences()
    {
        const string mouseSequence = "\x1b[<35;20;5m";
        ProcessInput(mouseSequence);
        Assert.Equal([mouseSequence], _emittedSequences);
    }

    [Fact(DisplayName = "should pass through complete arrow key sequences")]
    public void Passes_through_complete_arrow_key_sequences()
    {
        const string upArrow = "\x1b[A";
        ProcessInput(upArrow);
        Assert.Equal([upArrow], _emittedSequences);
    }

    [Fact(DisplayName = "should pass through complete function key sequences")]
    public void Passes_through_complete_function_key_sequences()
    {
        const string f1 = "\x1b[11~";
        ProcessInput(f1);
        Assert.Equal([f1], _emittedSequences);
    }

    [Fact(DisplayName = "should pass through meta key sequences")]
    public void Passes_through_meta_key_sequences()
    {
        const string metaA = "\u001ba";
        ProcessInput(metaA);
        Assert.Equal([metaA], _emittedSequences);
    }

    [Fact(DisplayName = "should pass through SS3 sequences")]
    public void Passes_through_ss3_sequences()
    {
        const string ss3 = "\x1bOA";
        ProcessInput(ss3);
        Assert.Equal([ss3], _emittedSequences);
    }

    [Fact(DisplayName = "should buffer incomplete mouse SGR sequence")]
    public async Task Buffers_incomplete_mouse_sgr_sequence()
    {
        ProcessInput("\x1b");
        Assert.Empty(_emittedSequences);
        Assert.Equal("\x1b", _buffer.GetBuffer());

        ProcessInput("[<35");
        Assert.Empty(_emittedSequences);
        Assert.Equal("\x1b[<35", _buffer.GetBuffer());

        ProcessInput(";20;5m");
        Assert.Equal(["\x1b[<35;20;5m"], _emittedSequences);
        Assert.Equal(string.Empty, _buffer.GetBuffer());
        await Task.CompletedTask;
    }

    [Fact(DisplayName = "should buffer incomplete CSI sequence")]
    public void Buffers_incomplete_csi_sequence()
    {
        ProcessInput("\x1b[");
        Assert.Empty(_emittedSequences);

        ProcessInput("1;");
        Assert.Empty(_emittedSequences);

        ProcessInput("5H");
        Assert.Equal(["\x1b[1;5H"], _emittedSequences);
    }

    [Fact(DisplayName = "should buffer split across many chunks")]
    public void Buffers_split_sequence_across_many_chunks()
    {
        foreach (var chunk in new[] { "\x1b", "[", "<", "3", "5", ";", "2", "0", ";", "5", "m" })
        {
            ProcessInput(chunk);
        }

        Assert.Equal(["\x1b[<35;20;5m"], _emittedSequences);
    }

    [Fact(DisplayName = "should flush incomplete sequence after timeout")]
    public async Task Flushes_incomplete_sequence_after_timeout()
    {
        ProcessInput("\x1b[<35");
        Assert.Empty(_emittedSequences);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["\x1b[<35"], _emittedSequences);
    }

    [Fact(DisplayName = "should flush a lone ESC as Escape when CR arrives after the timeout")]
    public async Task Flushes_lone_escape_before_late_carriage_return()
    {
        ProcessInput("\x1b");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        ProcessInput("\r");

        Assert.Equal(["\x1b", "\r"], _emittedSequences);
        Assert.True(Keys.MatchesKey(_emittedSequences[0], "escape"));
    }

    [Fact(DisplayName = "should merge ESC + CR split across chunks within a larger timeout")]
    public async Task Merges_escape_and_carriage_return_within_larger_timeout()
    {
        ReplaceBuffer(new StdinBufferOptions { EscapeTimeout = 100 });
        ProcessInput("\x1b");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        ProcessInput("\r");

        Assert.Equal(["\x1b\r"], _emittedSequences);
        Assert.True(Keys.MatchesKey(_emittedSequences[0], "alt+enter"));
    }

    [Fact(DisplayName = "does not apply the sequence timeout to a lone ESC")]
    public async Task Does_not_apply_sequence_timeout_to_lone_escape()
    {
        ReplaceBuffer(new StdinBufferOptions { Timeout = 100 });
        ProcessInput("\x1b");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        ProcessInput("\r");

        Assert.Equal(["\x1b", "\r"], _emittedSequences);
        Assert.True(Keys.MatchesKey(_emittedSequences[0], "escape"));
    }

    [Fact(DisplayName = "keeps fragmented mouse sequences buffered across delayed chunks by default")]
    public async Task Keeps_fragmented_mouse_sequences_buffered_across_delayed_chunks()
    {
        // Keep a wide timeout margin so a busy test process cannot turn the intended
        // 20ms inter-read delay into an accidental timeout.
        using var delayedBuffer = new StdinBuffer(new StdinBufferOptions { Timeout = 250 });
        var delayedSequences = new List<string>();
        delayedBuffer.Data += sequence => delayedSequences.Add(sequence);

        delayedBuffer.Process("\x1b[");
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Empty(delayedSequences);
        delayedBuffer.Process("<65;48;39M");
        Assert.Equal(["\x1b[<65;48;39M"], delayedSequences);
        delayedBuffer.Destroy();
    }

    [Fact(DisplayName = "should handle characters followed by escape sequence")]
    public void Handles_characters_followed_by_escape_sequence()
    {
        ProcessInput("abc\x1b[A");
        Assert.Equal(["a", "b", "c", "\x1b[A"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle escape sequence followed by characters")]
    public void Handles_escape_sequence_followed_by_characters()
    {
        ProcessInput("\x1b[Aabc");
        Assert.Equal(["\x1b[A", "a", "b", "c"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle multiple complete sequences")]
    public void Handles_multiple_complete_sequences()
    {
        ProcessInput("\x1b[A\x1b[B\x1b[C");
        Assert.Equal(["\x1b[A", "\x1b[B", "\x1b[C"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle partial sequence with preceding characters")]
    public void Handles_partial_sequence_with_preceding_characters()
    {
        ProcessInput("abc\x1b[<35");
        Assert.Equal(["a", "b", "c"], _emittedSequences);
        Assert.Equal("\x1b[<35", _buffer.GetBuffer());

        ProcessInput(";20;5m");
        Assert.Equal(["a", "b", "c", "\x1b[<35;20;5m"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle Kitty CSI u press events")]
    public void Handles_kitty_csi_u_press_events()
    {
        ProcessInput("\x1b[97u");
        Assert.Equal(["\x1b[97u"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle Kitty CSI u release events")]
    public void Handles_kitty_csi_u_release_events()
    {
        ProcessInput("\x1b[97;1:3u");
        Assert.Equal(["\x1b[97;1:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle batched Kitty press and release")]
    public void Handles_batched_kitty_press_and_release()
    {
        ProcessInput("\x1b[97u\x1b[97;1:3u");
        Assert.Equal(["\x1b[97u", "\x1b[97;1:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle multiple batched Kitty events")]
    public void Handles_multiple_batched_kitty_events()
    {
        ProcessInput("\x1b[97u\x1b[97;1:3u\x1b[98u\x1b[98;1:3u");
        Assert.Equal(["\x1b[97u", "\x1b[97;1:3u", "\x1b[98u", "\x1b[98;1:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle Kitty arrow keys with event type")]
    public void Handles_kitty_arrow_keys_with_event_type()
    {
        ProcessInput("\x1b[1;1:1A");
        Assert.Equal(["\x1b[1;1:1A"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle Kitty functional keys with event type")]
    public void Handles_kitty_functional_keys_with_event_type()
    {
        ProcessInput("\x1b[3;1:3~");
        Assert.Equal(["\x1b[3;1:3~"], _emittedSequences);
    }

    [Fact(DisplayName = "should split ESC+ESC+CSI into standalone ESC and the CSI sequence (WezTerm Escape key regression)")]
    public void Splits_double_escape_before_csi()
    {
        ProcessInput("\x1b\x1b[27;129:3u");
        Assert.Equal(["\x1b", "\x1b[27;129:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should split ESC+ESC+CSI with no modifier (no num_lock)")]
    public void Splits_double_escape_before_csi_without_modifier()
    {
        ProcessInput("\x1b\x1b[27;1:3u");
        Assert.Equal(["\x1b", "\x1b[27;1:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should still emit ESC+ESC as a single sequence when not followed by a new escape")]
    public void Keeps_double_escape_without_following_escape()
    {
        ProcessInput("\x1b\x1b");
        Assert.Equal(["\x1b\x1b"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle plain characters mixed with Kitty sequences")]
    public void Handles_plain_characters_mixed_with_kitty_sequences()
    {
        ProcessInput("a\x1b[97;1:3u");
        Assert.Equal(["a", "\x1b[97;1:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should drop raw duplicate character after matching Kitty printable sequence")]
    public void Drops_raw_duplicate_after_kitty_printable_sequence()
    {
        ProcessInput("\x1b[224uà");
        Assert.Equal(["\x1b[224u"], _emittedSequences);
    }

    [Fact(DisplayName = "should drop raw duplicate character after matching Kitty printable sequence across chunks")]
    public void Drops_raw_duplicate_after_kitty_printable_sequence_across_chunks()
    {
        ProcessInput("\x1b[64u");
        ProcessInput("@");
        Assert.Equal(["\x1b[64u"], _emittedSequences);
    }

    [Fact(DisplayName = "should keep non-matching plain character after Kitty printable sequence")]
    public void Keeps_nonmatching_plain_character_after_kitty_printable_sequence()
    {
        ProcessInput("\x1b[97ub");
        Assert.Equal(["\x1b[97u", "b"], _emittedSequences);
    }

    [Fact(DisplayName = "should keep raw character after modified Kitty printable sequence")]
    public void Keeps_raw_character_after_modified_kitty_printable_sequence()
    {
        ProcessInput("\x1b[64;3u@");
        Assert.Equal(["\x1b[64;3u", "@"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle rapid typing simulation with Kitty protocol")]
    public void Handles_rapid_typing_simulation_with_kitty_protocol()
    {
        ProcessInput("\x1b[104u\x1b[104;1:3u\x1b[105u\x1b[105;1:3u");
        Assert.Equal(["\x1b[104u", "\x1b[104;1:3u", "\x1b[105u", "\x1b[105;1:3u"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle mouse press event")]
    public void Handles_mouse_press_event()
    {
        ProcessInput("\x1b[<0;10;5M");
        Assert.Equal(["\x1b[<0;10;5M"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle mouse release event")]
    public void Handles_mouse_release_event()
    {
        ProcessInput("\x1b[<0;10;5m");
        Assert.Equal(["\x1b[<0;10;5m"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle mouse move event")]
    public void Handles_mouse_move_event()
    {
        ProcessInput("\x1b[<35;20;5m");
        Assert.Equal(["\x1b[<35;20;5m"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle split mouse events")]
    public void Handles_split_mouse_events()
    {
        ProcessInput("\x1b[<3");
        ProcessInput("5;1");
        ProcessInput("5;");
        ProcessInput("10m");
        Assert.Equal(["\x1b[<35;15;10m"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle multiple mouse events")]
    public void Handles_multiple_mouse_events()
    {
        ProcessInput("\x1b[<35;1;1m\x1b[<35;2;2m\x1b[<35;3;3m");
        Assert.Equal(["\x1b[<35;1;1m", "\x1b[<35;2;2m", "\x1b[<35;3;3m"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle old-style mouse sequence (ESC[M + 3 bytes)")]
    public void Handles_old_style_mouse_sequence()
    {
        ProcessInput("\x1b[M abc");
        Assert.Equal(["\x1b[M ab", "c"], _emittedSequences);
    }

    [Fact(DisplayName = "should buffer incomplete old-style mouse sequence")]
    public void Buffers_incomplete_old_style_mouse_sequence()
    {
        ProcessInput("\x1b[M");
        Assert.Equal("\x1b[M", _buffer.GetBuffer());

        ProcessInput(" a");
        Assert.Equal("\x1b[M a", _buffer.GetBuffer());

        ProcessInput("b");
        Assert.Equal(["\x1b[M ab"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle empty input")]
    public void Handles_empty_input()
    {
        ProcessInput(string.Empty);
        Assert.Equal([string.Empty], _emittedSequences);
    }

    [Fact(DisplayName = "should handle lone escape character with timeout")]
    public async Task Handles_lone_escape_character_with_timeout()
    {
        ProcessInput("\x1b");
        Assert.Empty(_emittedSequences);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["\x1b"], _emittedSequences);
    }

    [Fact(DisplayName = "flushes a lone escape promptly with the longer default sequence timeout")]
    public async Task Flushes_lone_escape_with_default_timeout()
    {
        using var defaultBuffer = new StdinBuffer();
        var defaultSequences = new List<string>();
        defaultBuffer.Data += sequence => defaultSequences.Add(sequence);

        defaultBuffer.Process("\x1b");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(["\x1b"], defaultSequences);
        defaultBuffer.Destroy();
    }

    [Fact(DisplayName = "should handle lone escape character with explicit flush")]
    public void Handles_lone_escape_character_with_explicit_flush()
    {
        ProcessInput("\x1b");
        Assert.Empty(_emittedSequences);

        var flushed = _buffer.Flush();
        Assert.Equal(["\x1b"], flushed);
    }

    [Fact(DisplayName = "should handle buffer input")]
    public void Handles_buffer_input()
    {
        ProcessInput(Encoding.UTF8.GetBytes("\x1b[A"));
        Assert.Equal(["\x1b[A"], _emittedSequences);
    }

    [Fact(DisplayName = "should handle very long sequences")]
    public void Handles_very_long_sequences()
    {
        var longSequence = $"\x1b[{string.Concat(Enumerable.Repeat("1;", 50))}H";
        ProcessInput(longSequence);
        Assert.Equal([longSequence], _emittedSequences);
    }

    [Fact(DisplayName = "should flush incomplete sequences")]
    public void Flushes_incomplete_sequences()
    {
        ProcessInput("\x1b[<35");
        var flushed = _buffer.Flush();
        Assert.Equal(["\x1b[<35"], flushed);
        Assert.Equal(string.Empty, _buffer.GetBuffer());
    }

    [Fact(DisplayName = "should return empty array if nothing to flush")]
    public void Returns_empty_array_if_nothing_to_flush()
    {
        var flushed = _buffer.Flush();
        Assert.Empty(flushed);
    }

    [Fact(DisplayName = "should emit flushed data via timeout")]
    public async Task Emits_flushed_data_via_timeout()
    {
        ProcessInput("\x1b[<35");
        Assert.Empty(_emittedSequences);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["\x1b[<35"], _emittedSequences);
    }

    [Fact(DisplayName = "should clear buffered content without emitting")]
    public void Clears_buffered_content_without_emitting()
    {
        ProcessInput("\x1b[<35");
        Assert.Equal("\x1b[<35", _buffer.GetBuffer());

        _buffer.Clear();
        Assert.Equal(string.Empty, _buffer.GetBuffer());
        Assert.Empty(_emittedSequences);
    }

    [Fact(DisplayName = "should emit paste event for complete bracketed paste")]
    public void Emits_paste_event_for_complete_bracketed_paste()
    {
        const string pasteStart = "\x1b[200~";
        const string pasteEnd = "\x1b[201~";
        ProcessInput(pasteStart + "hello world" + pasteEnd);

        Assert.Equal(["hello world"], _emittedPaste);
        Assert.Empty(_emittedSequences);
    }

    [Fact(DisplayName = "should handle paste arriving in chunks")]
    public void Handles_paste_arriving_in_chunks()
    {
        ProcessInput("\x1b[200~");
        Assert.Empty(_emittedPaste);

        ProcessInput("hello ");
        Assert.Empty(_emittedPaste);

        ProcessInput("world\x1b[201~");
        Assert.Equal(["hello world"], _emittedPaste);
        Assert.Empty(_emittedSequences);
    }

    [Fact(DisplayName = "should handle paste with input before and after")]
    public void Handles_paste_with_input_before_and_after()
    {
        ProcessInput("a");
        ProcessInput("\x1b[200~pasted\x1b[201~");
        ProcessInput("b");

        Assert.Equal(["a", "b"], _emittedSequences);
        Assert.Equal(["pasted"], _emittedPaste);
    }

    [Fact(DisplayName = "should handle paste with newlines")]
    public void Handles_paste_with_newlines()
    {
        ProcessInput("\x1b[200~line1\nline2\nline3\x1b[201~");
        Assert.Equal(["line1\nline2\nline3"], _emittedPaste);
        Assert.Empty(_emittedSequences);
    }

    [Fact(DisplayName = "should handle paste with unicode")]
    public void Handles_paste_with_unicode()
    {
        ProcessInput("\x1b[200~Hello 世界 🎉\x1b[201~");
        Assert.Equal(["Hello 世界 🎉"], _emittedPaste);
        Assert.Empty(_emittedSequences);
    }

    [Fact(DisplayName = "should clear buffer on destroy")]
    public void Clears_buffer_on_destroy()
    {
        ProcessInput("\x1b[<35");
        Assert.Equal("\x1b[<35", _buffer.GetBuffer());

        _buffer.Destroy();
        Assert.Equal(string.Empty, _buffer.GetBuffer());
    }

    [Fact(DisplayName = "should clear pending timeouts on destroy")]
    public async Task Clears_pending_timeouts_on_destroy()
    {
        ProcessInput("\x1b[<35");
        _buffer.Destroy();

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_emittedSequences);
    }

    public void Dispose() => _buffer.Dispose();

    private void ProcessInput(string data) => _buffer.Process(data);

    private void ProcessInput(byte[] data) => _buffer.Process(data);

    private StdinBuffer CreateBuffer(StdinBufferOptions options)
    {
        var buffer = new StdinBuffer(options);
        buffer.Data += sequence => _emittedSequences.Add(sequence);
        buffer.Paste += paste => _emittedPaste.Add(paste);
        return buffer;
    }

    private void ReplaceBuffer(StdinBufferOptions options)
    {
        _buffer.Dispose();
        _emittedSequences = [];
        _emittedPaste = [];
        _buffer = CreateBuffer(options);
    }
}
