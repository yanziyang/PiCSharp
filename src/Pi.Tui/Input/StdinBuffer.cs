using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Options controlling how long incomplete terminal sequences remain buffered.</summary>
public sealed class StdinBufferOptions
{
    /// <summary>Maximum wait, in milliseconds, for an incomplete escape sequence.</summary>
    public int? Timeout { get; init; }

    /// <summary>Maximum wait, in milliseconds, for a lone Escape byte.</summary>
    public int? EscapeTimeout { get; init; }
}

/// <summary>Buffers terminal input until complete key or control sequences are available.</summary>
public sealed class StdinBuffer : IDisposable
{
    private const string _escapeSequence = "\x1b";
    private const int _defaultSequenceTimeoutMilliseconds = 50;
    private const int _defaultEscapeTimeoutMilliseconds = 10;
    private const string _bracketedPasteStart = "\x1b[200~";
    private const string _bracketedPasteEnd = "\x1b[201~";

    private static readonly Regex _completeMouseSequenceRegex = new(
        @"^<[0-9]+;[0-9]+;[0-9]+[Mm]$",
        RegexOptions.CultureInvariant);

    private static readonly Regex _unmodifiedKittyPrintableRegex = new(
        @"^\x1b\[([0-9]+)(?::[0-9]*)?(?::[0-9]+)?u$",
        RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly int _timeoutMilliseconds;
    private readonly int _escapeTimeoutMilliseconds;
    private string _buffer = string.Empty;
    private Timer? _timeout;
    private bool _pasteMode;
    private string _pasteBuffer = string.Empty;
    private int? _pendingKittyPrintableCodepoint;

    /// <summary>Initializes a stdin buffer with the upstream timeout defaults.</summary>
    public StdinBuffer(StdinBufferOptions? options = null)
    {
        _timeoutMilliseconds = options?.Timeout ?? _defaultSequenceTimeoutMilliseconds;
        _escapeTimeoutMilliseconds = options?.EscapeTimeout ?? _defaultEscapeTimeoutMilliseconds;
    }

    /// <summary>Receives a complete or partial UTF-16 input chunk.</summary>
    public event Action<string>? Data;

    /// <summary>Receives the contents of a completed bracketed paste.</summary>
    public event Action<string>? Paste;

    /// <summary>Processes a text input chunk and emits all complete sequences.</summary>
    public void Process(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            ProcessCore(data);
        }
    }

    /// <summary>Processes a byte input chunk using UTF-8 decoding.</summary>
    public void Process(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = data.Length == 1 && data[0] > 127
            ? $"\x1b{(char)(data[0] - 128)}"
            : Encoding.UTF8.GetString(data);
        Process(text);
    }

    /// <summary>Processes a read-only byte input chunk using UTF-8 decoding.</summary>
    public void Process(ReadOnlyMemory<byte> data) => Process(data.ToArray());

    /// <summary>Flushes the currently buffered incomplete sequence without emitting it.</summary>
    public IReadOnlyList<string> Flush()
    {
        lock (_gate)
        {
            ClearTimeoutCore();
            if (_buffer.Length == 0)
            {
                return [];
            }

            var sequences = new[] { _buffer };
            _buffer = string.Empty;
            _pendingKittyPrintableCodepoint = null;
            return sequences;
        }
    }

    /// <summary>Clears buffered input, paste state, and pending timeout state.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
        }
    }

    /// <summary>Returns the currently buffered incomplete input.</summary>
    public string GetBuffer()
    {
        lock (_gate)
        {
            return _buffer;
        }
    }

    /// <summary>Clears buffered input and pending timers.</summary>
    public void Destroy() => Clear();

    /// <inheritdoc />
    public void Dispose() => Destroy();

    private void ProcessCore(string data)
    {
        ClearTimeoutCore();

        if (data.Length == 0 && _buffer.Length == 0)
        {
            EmitDataSequence(string.Empty);
            return;
        }

        _buffer += data;

        if (_pasteMode)
        {
            _pasteBuffer += _buffer;
            _buffer = string.Empty;
            CompletePasteIfPresent();
            return;
        }

        var startIndex = _buffer.IndexOf(_bracketedPasteStart, StringComparison.Ordinal);
        if (startIndex != -1)
        {
            if (startIndex > 0)
            {
                var beforePaste = _buffer[..startIndex];
                var result = ExtractCompleteSequences(beforePaste);
                foreach (var sequence in result.Sequences)
                {
                    EmitDataSequence(sequence);
                }
            }

            _pendingKittyPrintableCodepoint = null;
            _buffer = _buffer[(startIndex + _bracketedPasteStart.Length)..];
            _pasteMode = true;
            _pasteBuffer = _buffer;
            _buffer = string.Empty;
            CompletePasteIfPresent();
            return;
        }

        var extracted = ExtractCompleteSequences(_buffer);
        _buffer = extracted.Remainder;
        foreach (var sequence in extracted.Sequences)
        {
            EmitDataSequence(sequence);
        }

        if (_buffer.Length > 0)
        {
            var timeoutMilliseconds = _buffer == _escapeSequence ? _escapeTimeoutMilliseconds : _timeoutMilliseconds;
            ScheduleTimeout(timeoutMilliseconds);
        }
    }

    private void CompletePasteIfPresent()
    {
        var endIndex = _pasteBuffer.IndexOf(_bracketedPasteEnd, StringComparison.Ordinal);
        if (endIndex == -1)
        {
            return;
        }

        var pastedContent = _pasteBuffer[..endIndex];
        var remaining = _pasteBuffer[(endIndex + _bracketedPasteEnd.Length)..];

        _pasteMode = false;
        _pasteBuffer = string.Empty;
        _pendingKittyPrintableCodepoint = null;
        Paste?.Invoke(pastedContent);

        if (remaining.Length > 0)
        {
            ProcessCore(remaining);
        }
    }

    private void EmitDataSequence(string sequence)
    {
        var rawCodepoint = sequence.Length == 1 ? sequence[0] : (int?)null;
        if (rawCodepoint.HasValue && rawCodepoint == _pendingKittyPrintableCodepoint)
        {
            _pendingKittyPrintableCodepoint = null;
            return;
        }

        _pendingKittyPrintableCodepoint = ParseUnmodifiedKittyPrintableCodepoint(sequence);
        Data?.Invoke(sequence);
    }

    private void ScheduleTimeout(int timeoutMilliseconds)
    {
        var dueTime = Math.Max(0, timeoutMilliseconds);
        _timeout = new Timer(static state => ((StdinBuffer)state!).OnTimeout(), this, dueTime, Timeout.Infinite);
    }

    private void OnTimeout()
    {
        lock (_gate)
        {
            _timeout?.Dispose();
            _timeout = null;
            if (_buffer.Length == 0)
            {
                return;
            }

            var sequence = _buffer;
            _buffer = string.Empty;
            _pendingKittyPrintableCodepoint = null;
            EmitDataSequence(sequence);
        }
    }

    private void ClearTimeoutCore()
    {
        _timeout?.Dispose();
        _timeout = null;
    }

    private void ClearCore()
    {
        ClearTimeoutCore();
        _buffer = string.Empty;
        _pasteMode = false;
        _pasteBuffer = string.Empty;
        _pendingKittyPrintableCodepoint = null;
    }

    private static SequenceStatus IsCompleteSequence(string data)
    {
        if (!data.StartsWith(_escapeSequence, StringComparison.Ordinal)) return SequenceStatus.NotEscape;
        if (data.Length == 1) return SequenceStatus.Incomplete;

        var afterEscape = data[1..];
        if (afterEscape.StartsWith('['))
        {
            if (afterEscape.StartsWith("[M", StringComparison.Ordinal))
            {
                return data.Length >= 6 ? SequenceStatus.Complete : SequenceStatus.Incomplete;
            }

            return IsCompleteCsiSequence(data);
        }

        if (afterEscape.StartsWith(']')) return IsCompleteOscSequence(data);
        if (afterEscape.StartsWith('P')) return IsCompleteDcsSequence(data);
        if (afterEscape.StartsWith('_')) return IsCompleteApcSequence(data);
        if (afterEscape.StartsWith('O'))
        {
            return afterEscape.Length >= 2 ? SequenceStatus.Complete : SequenceStatus.Incomplete;
        }

        if (afterEscape.Length == 1) return SequenceStatus.Complete;
        return SequenceStatus.Complete;
    }

    private static SequenceStatus IsCompleteCsiSequence(string data)
    {
        if (!data.StartsWith($"{_escapeSequence}[", StringComparison.Ordinal)) return SequenceStatus.Complete;
        if (data.Length < 3) return SequenceStatus.Incomplete;

        var payload = data[2..];
        var lastCharacter = payload[^1];
        var lastCode = lastCharacter;
        if (lastCode is >= '\x40' and <= '\x7e')
        {
            if (payload.StartsWith('<'))
            {
                if (_completeMouseSequenceRegex.IsMatch(payload)) return SequenceStatus.Complete;
                if (lastCharacter is 'M' or 'm')
                {
                    var parts = payload[1..^1].Split(';');
                    if (parts.Length == 3 && parts.All(static part => part.Length > 0 && part.All(static ch => ch is >= '0' and <= '9')))
                    {
                        return SequenceStatus.Complete;
                    }
                }

                return SequenceStatus.Incomplete;
            }

            return SequenceStatus.Complete;
        }

        return SequenceStatus.Incomplete;
    }

    private static SequenceStatus IsCompleteOscSequence(string data)
    {
        if (!data.StartsWith($"{_escapeSequence}]", StringComparison.Ordinal)) return SequenceStatus.Complete;
        return data.EndsWith($"{_escapeSequence}\\", StringComparison.Ordinal) || data.EndsWith('\x07')
            ? SequenceStatus.Complete
            : SequenceStatus.Incomplete;
    }

    private static SequenceStatus IsCompleteDcsSequence(string data)
    {
        if (!data.StartsWith($"{_escapeSequence}P", StringComparison.Ordinal)) return SequenceStatus.Complete;
        return data.EndsWith($"{_escapeSequence}\\", StringComparison.Ordinal) ? SequenceStatus.Complete : SequenceStatus.Incomplete;
    }

    private static SequenceStatus IsCompleteApcSequence(string data)
    {
        if (!data.StartsWith($"{_escapeSequence}_", StringComparison.Ordinal)) return SequenceStatus.Complete;
        return data.EndsWith($"{_escapeSequence}\\", StringComparison.Ordinal) ? SequenceStatus.Complete : SequenceStatus.Incomplete;
    }

    private static (List<string> Sequences, string Remainder) ExtractCompleteSequences(string buffer)
    {
        var sequences = new List<string>();
        var position = 0;

        while (position < buffer.Length)
        {
            var remaining = buffer[position..];
            if (remaining.StartsWith(_escapeSequence, StringComparison.Ordinal))
            {
                var sequenceEnd = 1;
                while (sequenceEnd <= remaining.Length)
                {
                    var candidate = remaining[..sequenceEnd];
                    var status = IsCompleteSequence(candidate);
                    if (status == SequenceStatus.Complete)
                    {
                        if (candidate == "\x1b\x1b" && sequenceEnd < remaining.Length)
                        {
                            var nextCharacter = remaining[sequenceEnd];
                            if (nextCharacter is '[' or ']' or 'O' or 'P' or '_')
                            {
                                sequences.Add(_escapeSequence);
                                position++;
                                break;
                            }
                        }

                        sequences.Add(candidate);
                        position += sequenceEnd;
                        break;
                    }

                    if (status == SequenceStatus.Incomplete)
                    {
                        sequenceEnd++;
                        continue;
                    }

                    sequences.Add(candidate);
                    position += sequenceEnd;
                    break;
                }

                if (sequenceEnd > remaining.Length)
                {
                    return (sequences, remaining);
                }
            }
            else
            {
                sequences.Add(remaining[0].ToString());
                position++;
            }
        }

        return (sequences, string.Empty);
    }

    private static int? ParseUnmodifiedKittyPrintableCodepoint(string sequence)
    {
        var match = _unmodifiedKittyPrintableRegex.Match(sequence);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var codepoint) || codepoint < 32)
        {
            return null;
        }

        return codepoint;
    }

    private enum SequenceStatus
    {
        Complete,
        Incomplete,
        NotEscape,
    }
}
