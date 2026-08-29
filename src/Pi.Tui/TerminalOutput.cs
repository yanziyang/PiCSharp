using System.Text;

namespace Pi.Tui;

/// <summary>Bounded terminal output writer used by full and differential renders.</summary>
public sealed class TerminalOutputWriter
{
    /// <summary>Maximum number of UTF-16 code units written in one terminal call.</summary>
    public const int MaxWriteCharacters = 1024 * 1024;

    private readonly Action<string> _write;
    private readonly StringBuilder _buffer = new(MaxWriteCharacters);
    private long _writtenCharacters;

    /// <summary>Initializes a bounded writer around a terminal write delegate.</summary>
    public TerminalOutputWriter(Action<string> write)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <summary>Appends terminal data, flushing full chunks as needed.</summary>
    public void Append(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var offset = 0;
        while (offset < value.Length)
        {
            var capacity = MaxWriteCharacters - _buffer.Length;
            if (capacity == 0)
            {
                Flush();
                continue;
            }

            var end = Math.Min(value.Length, offset + capacity);
            if (
                end < value.Length &&
                end > offset &&
                char.IsHighSurrogate(value[end - 1]) &&
                char.IsLowSurrogate(value[end]))
            {
                end--;
            }

            if (end == offset)
            {
                Flush();
                continue;
            }

            _buffer.Append(value.AsSpan(offset, end - offset));
            offset = end;
            if (_buffer.Length == MaxWriteCharacters)
            {
                Flush();
            }
        }
    }

    /// <summary>Writes the current chunk, if it contains data.</summary>
    public void Flush()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        _write(_buffer.ToString());
        _writtenCharacters += _buffer.Length;
        _buffer.Clear();
    }

    /// <summary>Number of UTF-16 code units appended or flushed so far.</summary>
    public long Length => _writtenCharacters + _buffer.Length;
}
