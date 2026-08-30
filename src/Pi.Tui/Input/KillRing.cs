namespace Pi.Tui;

/// <summary>Options for adding an entry to the Emacs-style kill ring.</summary>
public sealed class KillRingPushOptions
{
    /// <summary>Whether accumulated text is placed before the existing entry.</summary>
    public bool Prepend { get; init; }

    /// <summary>Whether to merge text into the most recent entry.</summary>
    public bool Accumulate { get; init; }
}

/// <summary>Ring buffer for Emacs-style kill, yank, and yank-pop operations.</summary>
public sealed class KillRing
{
    private readonly List<string> _ring = [];

    /// <summary>Adds deleted text to the ring, optionally accumulating it.</summary>
    public void Push(string text, KillRingPushOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (text.Length == 0) return;

        if (options.Accumulate && _ring.Count > 0)
        {
            var last = _ring[^1];
            _ring.RemoveAt(_ring.Count - 1);
            _ring.Add(options.Prepend ? text + last : last + text);
        }
        else
        {
            _ring.Add(text);
        }
    }

    /// <summary>Adds deleted text using explicit prepend and accumulate flags.</summary>
    public void Push(string text, bool prepend, bool accumulate = false) =>
        Push(text, new KillRingPushOptions { Prepend = prepend, Accumulate = accumulate });

    /// <summary>Returns the most recent entry without modifying the ring.</summary>
    public string? Peek() => _ring.Count > 0 ? _ring[^1] : null;

    /// <summary>Moves the most recent entry to the front for yank-pop cycling.</summary>
    public void Rotate()
    {
        if (_ring.Count <= 1) return;
        var last = _ring[^1];
        _ring.RemoveAt(_ring.Count - 1);
        _ring.Insert(0, last);
    }

    /// <summary>Returns the number of entries in the ring.</summary>
    public int Length => _ring.Count;
}

