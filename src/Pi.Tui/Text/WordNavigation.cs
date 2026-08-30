using System.Globalization;
using System.Text;

namespace Pi.Tui;

/// <summary>A word-segment result used by the configurable word-navigation helpers.</summary>
public readonly record struct WordSegment(string Segment, bool IsWordLike);

/// <summary>Options for supplying custom word segmentation and atomic segments.</summary>
public sealed class WordNavigationOptions
{
    /// <summary>Custom segmenter for the supplied substring.</summary>
    public Func<string, IEnumerable<WordSegment>>? Segment { get; init; }

    /// <summary>Identifies segments that move as one unit, such as paste markers.</summary>
    public Func<string, bool>? IsAtomicSegment { get; init; }
}

/// <summary>Word-boundary navigation matching Pi's terminal editor behavior.</summary>
public static class WordNavigation
{
    /// <summary>Moves the cursor one word backward, skipping trailing whitespace.</summary>
    public static int FindWordBackward(string text, int cursor, WordNavigationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cursor <= 0)
        {
            return 0;
        }

        var textBeforeCursor = text[..Math.Min(cursor, text.Length)];
        var segments = GetSegments(textBeforeCursor, options).ToList();
        var newCursor = cursor;
        var isAtomic = options?.IsAtomicSegment;

        while (segments.Count > 0 && !IsAtomic(segments[^1].Segment, isAtomic) &&
               TextMeasurement.IsWhitespaceChar(segments[^1].Segment))
        {
            newCursor -= segments[^1].Segment.Length;
            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count == 0)
        {
            return newCursor;
        }

        var last = segments[^1];
        if (IsAtomic(last.Segment, isAtomic))
        {
            newCursor -= last.Segment.Length;
        }
        else if (last.IsWordLike)
        {
            var punctuationIndex = LastPunctuationIndex(last.Segment, out var punctuationLength);
            newCursor -= punctuationIndex < 0
                ? last.Segment.Length
                : last.Segment.Length - punctuationIndex - punctuationLength;
        }
        else
        {
            while (segments.Count > 0 && !IsAtomic(segments[^1].Segment, isAtomic) &&
                   !segments[^1].IsWordLike && !TextMeasurement.IsWhitespaceChar(segments[^1].Segment))
            {
                newCursor -= segments[^1].Segment.Length;
                segments.RemoveAt(segments.Count - 1);
            }
        }

        return newCursor;
    }

    /// <summary>Moves the cursor one word forward, skipping leading whitespace.</summary>
    public static int FindWordForward(string text, int cursor, WordNavigationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cursor >= text.Length)
        {
            return text.Length;
        }

        var textAfterCursor = text[Math.Max(0, cursor)..];
        var segments = GetSegments(textAfterCursor, options).GetEnumerator();
        var newCursor = cursor;
        WordSegment current = default;
        var hasCurrent = segments.MoveNext();

        while (hasCurrent && !IsAtomic((current = segments.Current).Segment, options?.IsAtomicSegment) &&
               TextMeasurement.IsWhitespaceChar(current.Segment))
        {
            newCursor += current.Segment.Length;
            hasCurrent = segments.MoveNext();
        }

        if (!hasCurrent)
        {
            return newCursor;
        }

        if (IsAtomic(current.Segment, options?.IsAtomicSegment))
        {
            newCursor += current.Segment.Length;
        }
        else if (current.IsWordLike)
        {
            newCursor += FirstPunctuationIndex(current.Segment) ?? current.Segment.Length;
        }
        else
        {
            do
            {
                newCursor += current.Segment.Length;
                hasCurrent = segments.MoveNext();
                if (!hasCurrent)
                {
                    break;
                }

                current = segments.Current;
            }
            while (!IsAtomic(current.Segment, options?.IsAtomicSegment) && !current.IsWordLike &&
                   !TextMeasurement.IsWhitespaceChar(current.Segment));
        }

        return newCursor;
    }

    private static IEnumerable<WordSegment> GetSegments(string text, WordNavigationOptions? options) =>
        options?.Segment is { } segment ? segment(text) : SegmentDefault(text);

    private static bool IsAtomic(string segment, Func<string, bool>? predicate) => predicate?.Invoke(segment) == true;

    private static int? FirstPunctuationIndex(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
        {
            if (TextMeasurement.IsPunctuationChar(segment[index].ToString()))
            {
                return index;
            }
        }

        return null;
    }

    private static int LastPunctuationIndex(string segment, out int punctuationLength)
    {
        for (var index = segment.Length - 1; index >= 0; index--)
        {
            if (TextMeasurement.IsPunctuationChar(segment[index].ToString()))
            {
                punctuationLength = 1;
                return index;
            }
        }

        punctuationLength = 0;
        return -1;
    }

    private static List<WordSegment> SegmentDefault(string text)
    {
        var segments = new List<WordSegment>();
        var index = 0;
        while (index < text.Length)
        {
            var codePoint = ReadCodePoint(text, index, out var codePointLength);
            if (TextMeasurement.IsWhitespaceChar(text[index..(index + codePointLength)]))
            {
                var start = index;
                do
                {
                    index += codePointLength;
                    if (index >= text.Length)
                    {
                        break;
                    }

                    codePoint = ReadCodePoint(text, index, out codePointLength);
                }
                while (TextMeasurement.IsWhitespaceChar(text[index..(index + codePointLength)]));

                segments.Add(new WordSegment(text[start..index], false));
                continue;
            }

            if (IsHan(codePoint))
            {
                var start = index;
                var hanCount = 0;
                while (index < text.Length)
                {
                    codePoint = ReadCodePoint(text, index, out codePointLength);
                    if (!IsHan(codePoint))
                    {
                        break;
                    }

                    index += codePointLength;
                    hanCount++;
                    if (hanCount == 2)
                    {
                        segments.Add(new WordSegment(text[start..index], true));
                        start = index;
                        hanCount = 0;
                    }
                }

                if (start < index)
                {
                    segments.Add(new WordSegment(text[start..index], true));
                }

                continue;
            }

            if (IsWordCodePoint(codePoint))
            {
                var start = index;
                index += codePointLength;
                while (index < text.Length)
                {
                    var next = ReadCodePoint(text, index, out var nextLength);
                    if (IsWordCodePoint(next))
                    {
                        index += nextLength;
                        continue;
                    }

                    if (next is '.' or ':' && index + nextLength < text.Length)
                    {
                        var afterPunctuation = ReadCodePoint(text, index + nextLength, out var afterLength);
                        if (IsWordCodePoint(afterPunctuation))
                        {
                            index += nextLength + afterLength;
                            while (index < text.Length)
                            {
                                var following = ReadCodePoint(text, index, out var followingLength);
                                if (!IsWordCodePoint(following))
                                {
                                    break;
                                }

                                index += followingLength;
                            }

                            continue;
                        }
                    }

                    break;
                }

                segments.Add(new WordSegment(text[start..index], true));
                continue;
            }

            var punctuationStart = index;
            index += codePointLength;
            while (index < text.Length)
            {
                var next = ReadCodePoint(text, index, out var nextLength);
                if (TextMeasurement.IsWhitespaceChar(text[index..(index + nextLength)]) || IsWordCodePoint(next) || IsHan(next))
                {
                    break;
                }

                index += nextLength;
            }

            segments.Add(new WordSegment(text[punctuationStart..index], false));
        }

        return segments;
    }

    private static bool IsWordCodePoint(int codePoint)
    {
        if (codePoint == '_')
        {
            return true;
        }

        if (codePoint is >= 0xd800 and <= 0xdfff)
        {
            return false;
        }

        var category = Rune.GetUnicodeCategory(new Rune(codePoint));
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    private static bool IsHan(int codePoint) =>
        codePoint is >= 0x3400 and <= 0x4dbf || codePoint is >= 0x4e00 and <= 0x9fff ||
        codePoint is >= 0xf900 and <= 0xfaff || codePoint is >= 0x20000 and <= 0x2ffff;

    private static int ReadCodePoint(string text, int index, out int length)
    {
        var first = text[index];
        if (char.IsHighSurrogate(first) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(first, text[index + 1]);
        }

        length = 1;
        return first;
    }
}
