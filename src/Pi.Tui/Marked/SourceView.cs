// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>
/// A read-only window over an original string. It stands for marked's repeatedly re-sliced <c>src</c> without
/// copying the remainder for every tokenizer call (docs/translation-patterns.md section 15).
/// </summary>
public readonly struct SourceView
{
    /// <summary>The original immutable source string.</summary>
    public string Source { get; }
    /// <summary>Zero-based offset into <see cref="Source"/>.</summary>
    public int Offset { get; }
    /// <summary>Number of UTF-16 code units visible through the view.</summary>
    public int Length { get; }
    /// <summary>Returns a UTF-16 code unit relative to the view.</summary>
    public char this[int index] => Source[Offset + index];
    /// <summary>Whether the view has no remaining source.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Creates a validated view over a string.</summary>
    public SourceView(string source, int offset = 0, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)offset > (uint)source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var actualLength = length ?? source.Length - offset;
        if (actualLength < 0 || offset + actualLength > source.Length) throw new ArgumentOutOfRangeException(nameof(length));
        Source = source;
        Offset = offset;
        Length = actualLength;
    }

    /// <summary>Returns a zero-copy sub-view.</summary>
    public SourceView Slice(int start) => Slice(start, Length - start);

    /// <summary>Returns a zero-copy sub-view.</summary>
    public SourceView Slice(int start, int length)
    {
        if (start < 0 || length < 0 || start + length > Length) throw new ArgumentOutOfRangeException(nameof(start));
        return new SourceView(Source, Offset + start, length);
    }

    /// <summary>
    /// <c>src.substring(start)</c>: an offset past the end yields an empty view instead of throwing. marked relies on
    /// this, for example when a blockquote's <c>raw</c> runs one newline past the remaining source.
    /// </summary>
    public SourceView SliceClamped(int start)
        => start >= Length ? new SourceView(Source, Offset + Length, 0) : Slice(Math.Max(0, start));

    /// <summary><c>src.slice(start, end)</c>: negative offsets count from the end, and offsets clamp.</summary>
    public SourceView SliceJs(int start, int? end = null)
    {
        var from = start < 0 ? Math.Max(Length + start, 0) : Math.Min(start, Length);
        var to = end is not { } bound ? Length : bound < 0 ? Math.Max(Length + bound, 0) : Math.Min(bound, Length);
        return new SourceView(Source, Offset + from, Math.Max(0, to - from));
    }

    /// <summary>Tests a literal at the beginning of the view.</summary>
    public bool StartsWith(string value, StringComparison comparison = StringComparison.Ordinal)
        => value.Length <= Length && Source.AsSpan(Offset, value.Length).Equals(value.AsSpan(), comparison);

    /// <summary>Finds a literal relative to the beginning of the view.</summary>
    public int IndexOf(string value, int start = 0, StringComparison comparison = StringComparison.Ordinal)
    {
        if (start < 0 || start > Length) throw new ArgumentOutOfRangeException(nameof(start));
        var found = Source.AsSpan(Offset + start, Length - start).IndexOf(value.AsSpan(), comparison);
        return found < 0 ? -1 : start + found;
    }

    /// <summary>Materialises only when a token field explicitly needs a string.</summary>
    public string Materialize() => Source.Substring(Offset, Length);

    // The view's characters as a span, which span-based regex calls treat as the whole input, as JavaScript treats a
    // sliced string.
    internal ReadOnlySpan<char> AsSpan() => Source.AsSpan(Offset, Length);

    /// <summary>
    /// <c>regex.exec(src)</c> for a pattern anchored with <c>^</c>: matches the view as if it were its own string, so
    /// lookbehind cannot see before it.
    /// </summary>
    public bool TryMatch(Regex regex, [NotNullWhen(true)] out Match? match)
    {
        match = regex.Match(Source, Offset, Length);
        return match.Success && match.Index == Offset;
    }

    /// <summary>
    /// <c>regex.exec(src)</c> for an unanchored pattern: the first match inside the view. Its <see cref="Capture.Index"/>
    /// stays relative to <see cref="Source"/>; subtract <see cref="Offset"/> for a view-relative index.
    /// </summary>
    public Match MatchIn(Regex regex) => regex.Match(Source, Offset, Length);

    /// <summary>Returns the view's characters for diagnostics only.</summary>
    public override string ToString() => Materialize();
}
