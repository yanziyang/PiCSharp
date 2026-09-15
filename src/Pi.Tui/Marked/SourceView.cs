// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>
/// A read-only window over an original string. It is the C# equivalent of marked's
/// repeatedly re-sliced remainder, without copying the remainder for every tokenizer call.
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

    /// <summary>
    /// Matches inside the view's bounded region. The match index is converted to a view-relative index.
    /// </summary>
    public bool TryMatch(Regex regex, [NotNullWhen(true)] out Match? match)
    {
        match = regex.Match(Source, Offset, Length);
        return match.Success && match.Index == Offset;
    }

    /// <summary>Gets a regex capture without materialising the view.</summary>
    public static string? Capture(Match match, string nameOrNumber)
    {
        var group = match.Groups[nameOrNumber];
        return group.Success ? group.Value : null;
    }

    /// <summary>Gets the length of the matched source relative to this view.</summary>
    public int MatchLength(Match match) => match.Success && match.Index == Offset ? match.Length : 0;

    /// <summary>Returns the view's characters for diagnostics only.</summary>
    public override string ToString() => Materialize();
}
