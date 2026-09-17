// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>
/// JavaScript string semantics that marked's lexer relies on and .NET does not share
/// (docs/translation-patterns.md sections 12 and 15). Each member names the JavaScript it stands for.
/// </summary>
internal static class Js
{
    /// <summary>A JavaScript WhiteSpace or LineTerminator code unit, as <c>trim()</c> and <c>\s</c> use them.</summary>
    internal static bool IsWhiteSpace(char unit) => (int)unit switch
    {
        0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 => true,
        >= 0x2000 and <= 0x200A => true,
        0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF => true,
        _ => false,
    };

    /// <summary><c>value.trim()</c>.</summary>
    internal static string Trim(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsWhiteSpace(value[start])) start++;
        while (end > start && IsWhiteSpace(value[end - 1])) end--;
        return start == 0 && end == value.Length ? value : value[start..end];
    }

    /// <summary><c>value.trimStart()</c>.</summary>
    internal static string TrimStart(string value)
    {
        var start = 0;
        while (start < value.Length && IsWhiteSpace(value[start])) start++;
        return start == 0 ? value : value[start..];
    }

    /// <summary><c>value.trimEnd()</c>.</summary>
    internal static string TrimEnd(string value)
    {
        var end = value.Length;
        while (end > 0 && IsWhiteSpace(value[end - 1])) end--;
        return end == value.Length ? value : value[..end];
    }

    /// <summary>JavaScript truthiness of an optional string: <c>undefined</c> and <c>""</c> are both false.</summary>
    internal static bool Truthy(string? value) => !string.IsNullOrEmpty(value);

    /// <summary><c>value.slice(start, end)</c>: negative offsets count from the end, and offsets clamp.</summary>
    internal static string Slice(string value, int start, int? end = null)
    {
        var length = value.Length;
        var from = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
        var to = end is not { } bound ? length : bound < 0 ? Math.Max(length + bound, 0) : Math.Min(bound, length);
        return from >= to ? string.Empty : value.Substring(from, to - from);
    }

    /// <summary><c>value.substring(start, end)</c>: offsets clamp to the string, and a reversed pair swaps.</summary>
    internal static string Substring(string value, int start, int? end = null)
    {
        var length = value.Length;
        var from = Math.Clamp(start, 0, length);
        var to = Math.Clamp(end ?? length, 0, length);
        if (from > to) (from, to) = (to, from);
        return value.Substring(from, to - from);
    }

    /// <summary><c>[...value].length</c>: code points, a lone surrogate counting as one.</summary>
    internal static int CodePointCount(ReadOnlySpan<char> value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            count++;
        }
        return count;
    }

    /// <summary><c>[...value][0].length</c>: the UTF-16 length of the first code point.</summary>
    internal static int FirstCodePointLength(ReadOnlySpan<char> value)
        => value.Length > 1 && char.IsHighSurrogate(value[0]) && char.IsLowSurrogate(value[1]) ? 2 : 1;

    /// <summary><c>value.search(regex)</c>.</summary>
    internal static int Search(Regex regex, string value)
    {
        var match = regex.Match(value);
        return match.Success ? match.Index : -1;
    }

    /// <summary><c>value.replace(regex, replacement)</c> where the regex has no g flag: the first match only.</summary>
    internal static string ReplaceFirst(Regex regex, string value, string replacement) => regex.Replace(value, replacement, 1);

    /// <summary><c>value.replace(regex, replacement)</c> where the regex has the g flag: every match.</summary>
    internal static string ReplaceAll(Regex regex, string value, string replacement) => regex.Replace(value, replacement);

    /// <summary><c>src.split('\n', 1)[0]</c> over a source view, copying only the line.</summary>
    internal static string FirstLine(SourceView source)
    {
        var newline = source.IndexOf("\n");
        return newline < 0 ? source.Materialize() : source.Slice(0, newline).Materialize();
    }

    /// <summary>
    /// <c>value.toLowerCase()</c>: Unicode's full lowercase mapping. It differs from
    /// <see cref="string.ToLowerInvariant()"/> where marked's link-reference labels can see it: U+0130 lowercases
    /// to two code units, and a final capital sigma becomes U+03C2.
    /// </summary>
    internal static string ToLowerCase(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            var width = CodePointAt(value, i, out var codePoint);
            if (codePoint == 0x130) builder.Append('i').Append((char)0x307);
            else if (codePoint == 0x3A3) builder.Append(IsFinalSigma(value, i) ? (char)0x3C2 : (char)0x3C3);
            else if (width == 1 && char.IsSurrogate(value[i])) builder.Append(value[i]);
            else builder.Append(Rune.ToLowerInvariant(new Rune(codePoint)).ToString());
            i += width;
        }
        return builder.ToString();
    }

    private static int CodePointAt(string value, int index, out int codePoint)
    {
        if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
        {
            codePoint = char.ConvertToUtf32(value[index], value[index + 1]);
            return 2;
        }
        codePoint = value[index];
        return 1;
    }

    private static int CodePointBefore(string value, int index, out int codePoint)
    {
        if (index >= 2 && char.IsLowSurrogate(value[index - 1]) && char.IsHighSurrogate(value[index - 2]))
        {
            codePoint = char.ConvertToUtf32(value[index - 2], value[index - 1]);
            return 2;
        }
        codePoint = value[index - 1];
        return 1;
    }

    // Unicode's Final_Sigma condition: a cased letter, then any case-ignorable characters, before the sigma, and no
    // run of case-ignorable characters ending in a cased letter after it. Cased and case-ignorable come from general
    // categories plus the word-break apostrophes and stops; V8 uses ICU's exact properties, which differ only for
    // rare characters.
    private static bool IsFinalSigma(string value, int index)
    {
        var casedBefore = false;
        for (var i = index; i > 0;)
        {
            i -= CodePointBefore(value, i, out var before);
            if (IsCaseIgnorable(before)) continue;
            casedBefore = IsCased(before);
            break;
        }
        if (!casedBefore) return false;
        for (var i = index + 1; i < value.Length;)
        {
            i += CodePointAt(value, i, out var after);
            if (IsCaseIgnorable(after)) continue;
            return !IsCased(after);
        }
        return true;
    }

    private static bool IsCased(int codePoint)
    {
        if (codePoint is >= 0xD800 and <= 0xDFFF) return false;
        return Rune.GetUnicodeCategory(new Rune(codePoint)) is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;
    }

    private static bool IsCaseIgnorable(int codePoint)
    {
        if (codePoint is 0x27 or 0x2E or 0x3A or 0xB7 or 0x387 or 0x55F or 0x5F4 or 0x2018 or 0x2019 or 0x2024
            or 0x2027 or 0xFE13 or 0xFE52 or 0xFE55 or 0xFF07 or 0xFF0E or 0xFF1A)
        {
            return true;
        }
        if (codePoint is >= 0xD800 and <= 0xDFFF) return false;
        return Rune.GetUnicodeCategory(new Rune(codePoint)) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.ModifierLetter
            or UnicodeCategory.ModifierSymbol;
    }
}
