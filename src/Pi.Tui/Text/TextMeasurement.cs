using System.Globalization;
using System.Text;

namespace Pi.Tui;

/// <summary>One terminal escape sequence and its UTF-16 length.</summary>
public readonly record struct AnsiCode(string Code, int Length);

/// <summary>Visible text and its measured terminal-cell width.</summary>
public readonly record struct TextSlice(string Text, int Width);

/// <summary>Text before and after an overlay region, with measured widths.</summary>
public readonly record struct TextSegments(string Before, int BeforeWidth, string After, int AfterWidth);

/// <summary>The terminal-cell range occupied by a grapheme cluster.</summary>
public readonly record struct GraphemeCellRange(int Start, int End);

/// <summary>
/// Terminal text measurement and ANSI-aware text manipulation helpers.
/// The behavior mirrors the pinned Pi TUI utility module.
/// </summary>
public static class TextMeasurement
{
    private const int _tabWidth = 3;
    private const int _widthCacheSize = 512;
    private const string _segmentReset = "\x1b[0m\x1b]8;;\x07";
    private const string _punctuation = "(){}[]<>.,;:'\"!?+-=*/\\|&%^$#@~`";

    private static readonly object _widthCacheGate = new();
    private static readonly Dictionary<string, int> _widthCache = new(StringComparer.Ordinal);

    /// <summary>Returns the terminal width of a string, excluding supported control sequences.</summary>
    public static int VisibleWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }

        if (IsPrintableAscii(text))
        {
            return text.Length;
        }

        lock (_widthCacheGate)
        {
            if (_widthCache.TryGetValue(text, out var cached))
            {
                return cached;
            }
        }

        var clean = text.Contains('\t', StringComparison.Ordinal)
            ? text.Replace("\t", "   ", StringComparison.Ordinal)
            : text;
        if (clean.Contains('\x1b', StringComparison.Ordinal))
        {
            clean = StripTerminalSequences(clean);
        }

        var width = 0;
        foreach (var grapheme in EnumerateGraphemes(clean))
        {
            width += GraphemeWidth(grapheme.Text);
        }

        lock (_widthCacheGate)
        {
            if (_widthCache.Count >= _widthCacheSize)
            {
                var first = _widthCache.Keys.FirstOrDefault();
                if (first is not null)
                {
                    _widthCache.Remove(first);
                }
            }

            _widthCache[text] = width;
        }

        return width;
    }

    /// <summary>Removes supported ANSI, OSC, and APC control sequences.</summary>
    public static string StripTerminalSequences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('\x1b', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var ansi = ExtractAnsiCode(text, index);
            if (ansi is not null)
            {
                index += ansi.Value.Length;
                continue;
            }

            result.Append(text[index]);
            index++;
        }

        return result.ToString();
    }

    /// <summary>Returns the grapheme-cell range covering a visible column.</summary>
    public static GraphemeCellRange? GetGraphemeCellRange(string line, int column)
    {
        ArgumentNullException.ThrowIfNull(line);
        var currentColumn = 0;
        var index = 0;
        while (index < line.Length)
        {
            var ansi = ExtractAnsiCode(line, index);
            if (ansi is not null)
            {
                index += ansi.Value.Length;
                continue;
            }

            var textEnd = FindNextAnsi(line, index);
            foreach (var grapheme in EnumerateGraphemes(line[index..textEnd]))
            {
                var width = GraphemeWidth(grapheme.Text);
                if (width > 0 && column >= currentColumn && column < currentColumn + width)
                {
                    return new GraphemeCellRange(currentColumn, currentColumn + width);
                }

                currentColumn += width;
            }

            index = textEnd;
        }

        return null;
    }

    /// <summary>Returns the OSC 8 URL covering a visible terminal column.</summary>
    public static string? GetOsc8LinkAtColumn(string line, int column)
    {
        ArgumentNullException.ThrowIfNull(line);
        string? activeUrl = null;
        var currentColumn = 0;
        var index = 0;
        while (index < line.Length)
        {
            var ansi = ExtractAnsiCode(line, index);
            if (ansi is not null)
            {
                var parsed = ParseOsc8Hyperlink(ansi.Value.Code);
                if (parsed.IsOsc8)
                {
                    activeUrl = parsed.Hyperlink?.Url;
                }

                index += ansi.Value.Length;
                continue;
            }

            var textEnd = FindNextAnsi(line, index);
            foreach (var grapheme in EnumerateGraphemes(line[index..textEnd]))
            {
                var width = grapheme.Text == "\t" ? _tabWidth : GraphemeWidth(grapheme.Text);
                if (column >= currentColumn && column < currentColumn + width)
                {
                    return activeUrl;
                }

                currentColumn += width;
            }

            index = textEnd;
        }

        return null;
    }

    /// <summary>
    /// Normalizes terminal-only text. Visible tabs become three spaces and Thai/Lao AM
    /// vowels use their compatibility decomposition; control sequences remain byte-identical.
    /// </summary>
    public static string NormalizeTerminalOutput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text
            .Replace("\u0e33", "\u0e4d\u0e32", StringComparison.Ordinal)
            .Replace("\u0eb3", "\u0ecd\u0eb2", StringComparison.Ordinal);
        if (!normalized.Contains('\t', StringComparison.Ordinal))
        {
            return normalized;
        }

        var result = new StringBuilder(normalized.Length);
        var index = 0;
        while (index < normalized.Length)
        {
            var ansi = ExtractAnsiCode(normalized, index);
            if (ansi is not null)
            {
                result.Append(ansi.Value.Code);
                index += ansi.Value.Length;
                continue;
            }

            result.Append(normalized[index] == '\t' ? "   " : normalized[index]);
            index++;
        }

        return result.ToString();
    }

    /// <summary>Extracts a supported CSI, OSC, or APC sequence at a UTF-16 position.</summary>
    public static AnsiCode? ExtractAnsiCode(string text, int position)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (position < 0 || position >= text.Length || text[position] != '\x1b')
        {
            return null;
        }

        var next = position + 1 < text.Length ? text[position + 1] : '\0';
        if (next == '[')
        {
            var index = position + 2;
            while (index < text.Length && text[index] is not ('m' or 'G' or 'K' or 'H' or 'J'))
            {
                index++;
            }

            return index < text.Length
                ? new AnsiCode(text[position..(index + 1)], index + 1 - position)
                : null;
        }

        if (next is ']' or '_')
        {
            var index = position + 2;
            while (index < text.Length)
            {
                if (text[index] == '\x07')
                {
                    return new AnsiCode(text[position..(index + 1)], index + 1 - position);
                }

                if (text[index] == '\x1b' && index + 1 < text.Length && text[index + 1] == '\\')
                {
                    return new AnsiCode(text[position..(index + 2)], index + 2 - position);
                }

                index++;
            }
        }

        return null;
    }

    /// <summary>Wraps text at visible terminal width while preserving ANSI state.</summary>
    public static IReadOnlyList<string> WrapTextWithAnsi(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        var inputLines = SplitLines(text);
        var result = new List<string>();
        var tracker = new AnsiCodeTracker();
        foreach (var inputLine in inputLines)
        {
            var prefix = result.Count > 0 ? tracker.GetActiveCodes() : string.Empty;
            var wrappedLines = WrapSingleLine(prefix + inputLine, width);
            result.AddRange(wrappedLines);
            UpdateTrackerFromText(inputLine, tracker);
        }

        return result.Count > 0 ? result : [string.Empty];
    }

    /// <summary>Checks whether a segment consists only of whitespace characters.</summary>
    public static bool IsWhitespaceChar(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return false;
        }

        return EnumerateCodePoints(text).All(codePoint => IsWhitespaceCodePoint(codePoint.Value));
    }

    /// <summary>Checks whether text contains one of Pi's ASCII word-navigation punctuation marks.</summary>
    public static bool IsPunctuationChar(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(character => _punctuation.Contains(character, StringComparison.Ordinal));
    }

    /// <summary>Applies a background callback after padding a line to the requested width.</summary>
    public static string ApplyBackgroundToLine(string line, int width, Func<string, string> background)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(background);
        var padding = new string(' ', Math.Max(0, width - VisibleWidth(line)));
        return background(line + padding);
    }

    /// <summary>Truncates text to a visible width, optionally adding an ellipsis and padding.</summary>
    public static string TruncateToWidth(string text, int maxWidth, string ellipsis = "...", bool pad = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ellipsis);
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        if (text.Length == 0)
        {
            return pad ? new string(' ', maxWidth) : string.Empty;
        }

        var ellipsisWidth = VisibleWidth(ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            var textWidth = VisibleWidth(text);
            if (textWidth <= maxWidth)
            {
                return pad ? text + new string(' ', maxWidth - textWidth) : text;
            }

            var clippedEllipsis = TruncateFragmentToWidth(ellipsis, maxWidth);
            if (clippedEllipsis.Width == 0)
            {
                return pad ? new string(' ', maxWidth) : string.Empty;
            }

            return FinalizeTruncatedResult(
                string.Empty,
                0,
                clippedEllipsis.Text,
                clippedEllipsis.Width,
                maxWidth,
                pad);
        }

        if (IsPrintableAscii(text))
        {
            if (text.Length <= maxWidth)
            {
                return pad ? text + new string(' ', maxWidth - text.Length) : text;
            }

            var targetWidth = maxWidth - ellipsisWidth;
            return FinalizeTruncatedResult(
                text[..targetWidth],
                targetWidth,
                ellipsis,
                ellipsisWidth,
                maxWidth,
                pad);
        }

        var target = maxWidth - ellipsisWidth;
        var result = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        var visibleSoFar = 0;
        var keptWidth = 0;
        var keepContiguousPrefix = true;
        var overflowed = false;
        var exhaustedInput = false;
        var hasAnsi = text.Contains('\x1b', StringComparison.Ordinal);
        var hasTabs = text.Contains('\t', StringComparison.Ordinal);

        if (!hasAnsi && !hasTabs)
        {
            foreach (var grapheme in EnumerateGraphemes(text))
            {
                var graphemeWidth = GraphemeWidth(grapheme.Text);
                if (keepContiguousPrefix && keptWidth + graphemeWidth <= target)
                {
                    result.Append(grapheme.Text);
                    keptWidth += graphemeWidth;
                }
                else
                {
                    keepContiguousPrefix = false;
                }

                visibleSoFar += graphemeWidth;
                if (visibleSoFar > maxWidth)
                {
                    overflowed = true;
                    break;
                }
            }

            exhaustedInput = !overflowed;
        }
        else
        {
            var index = 0;
            while (index < text.Length)
            {
                var ansi = ExtractAnsiCode(text, index);
                if (ansi is not null)
                {
                    pendingAnsi.Append(ansi.Value.Code);
                    index += ansi.Value.Length;
                    continue;
                }

                if (text[index] == '\t')
                {
                    if (keepContiguousPrefix && keptWidth + _tabWidth <= target)
                    {
                        if (pendingAnsi.Length > 0)
                        {
                            result.Append(pendingAnsi);
                            pendingAnsi.Clear();
                        }

                        result.Append('\t');
                        keptWidth += _tabWidth;
                    }
                    else
                    {
                        keepContiguousPrefix = false;
                        pendingAnsi.Clear();
                    }

                    visibleSoFar += _tabWidth;
                    if (visibleSoFar > maxWidth)
                    {
                        overflowed = true;
                        break;
                    }

                    index++;
                    continue;
                }

                var end = index;
                while (end < text.Length && text[end] != '\t' && ExtractAnsiCode(text, end) is null)
                {
                    end++;
                }

                foreach (var grapheme in EnumerateGraphemes(text[index..end]))
                {
                    var graphemeWidth = GraphemeWidth(grapheme.Text);
                    if (keepContiguousPrefix && keptWidth + graphemeWidth <= target)
                    {
                        if (pendingAnsi.Length > 0)
                        {
                            result.Append(pendingAnsi);
                            pendingAnsi.Clear();
                        }

                        result.Append(grapheme.Text);
                        keptWidth += graphemeWidth;
                    }
                    else
                    {
                        keepContiguousPrefix = false;
                        pendingAnsi.Clear();
                    }

                    visibleSoFar += graphemeWidth;
                    if (visibleSoFar > maxWidth)
                    {
                        overflowed = true;
                        break;
                    }
                }

                if (overflowed)
                {
                    break;
                }

                index = end;
            }

            exhaustedInput = index >= text.Length;
        }

        if (!overflowed && exhaustedInput)
        {
            return pad ? text + new string(' ', Math.Max(0, maxWidth - visibleSoFar)) : text;
        }

        return FinalizeTruncatedResult(result.ToString(), keptWidth, ellipsis, ellipsisWidth, maxWidth, pad);
    }

    /// <summary>Extracts visible columns from a line, optionally requiring complete graphemes.</summary>
    public static string SliceByColumn(string line, int startColumn, int length, bool strict = false) =>
        SliceWithWidth(line, startColumn, length, strict).Text;

    /// <summary>Extracts visible columns and reports the width of the extracted text.</summary>
    public static TextSlice SliceWithWidth(string line, int startColumn, int length, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (length <= 0)
        {
            return new TextSlice(string.Empty, 0);
        }

        var endColumn = startColumn + length;
        var result = new StringBuilder();
        var resultWidth = 0;
        var currentColumn = 0;
        var index = 0;
        var pendingAnsi = new StringBuilder();
        while (index < line.Length)
        {
            var ansi = ExtractAnsiCode(line, index);
            if (ansi is not null)
            {
                if (currentColumn >= startColumn && currentColumn < endColumn)
                {
                    result.Append(ansi.Value.Code);
                }
                else if (currentColumn < startColumn)
                {
                    pendingAnsi.Append(ansi.Value.Code);
                }

                index += ansi.Value.Length;
                continue;
            }

            var textEnd = FindNextAnsi(line, index);
            foreach (var grapheme in EnumerateGraphemes(line[index..textEnd]))
            {
                var graphemeWidth = GraphemeWidth(grapheme.Text);
                var inRange = currentColumn >= startColumn && currentColumn < endColumn;
                var fits = !strict || currentColumn + graphemeWidth <= endColumn;
                if (inRange && fits)
                {
                    if (pendingAnsi.Length > 0)
                    {
                        result.Append(pendingAnsi);
                        pendingAnsi.Clear();
                    }

                    result.Append(grapheme.Text);
                    resultWidth += graphemeWidth;
                }

                currentColumn += graphemeWidth;
                if (currentColumn >= endColumn)
                {
                    break;
                }
            }

            index = textEnd;
            if (currentColumn >= endColumn)
            {
                break;
            }
        }

        return new TextSlice(result.ToString(), resultWidth);
    }

    /// <summary>Extracts the portions before and after an overlay region in one pass.</summary>
    public static TextSegments ExtractSegments(
        string line,
        int beforeEnd,
        int afterStart,
        int afterLength,
        bool strictAfter = false)
    {
        ArgumentNullException.ThrowIfNull(line);
        var before = new StringBuilder();
        var after = new StringBuilder();
        var beforeWidth = 0;
        var afterWidth = 0;
        var currentColumn = 0;
        var index = 0;
        var pendingAnsiBefore = new StringBuilder();
        var afterStarted = false;
        var afterEnd = afterStart + afterLength;
        var tracker = new AnsiCodeTracker();

        while (index < line.Length)
        {
            var ansi = ExtractAnsiCode(line, index);
            if (ansi is not null)
            {
                tracker.Process(ansi.Value.Code);
                if (currentColumn < beforeEnd)
                {
                    pendingAnsiBefore.Append(ansi.Value.Code);
                }
                else if (currentColumn >= afterStart && currentColumn < afterEnd && afterStarted)
                {
                    after.Append(ansi.Value.Code);
                }

                index += ansi.Value.Length;
                continue;
            }

            var textEnd = FindNextAnsi(line, index);
            foreach (var grapheme in EnumerateGraphemes(line[index..textEnd]))
            {
                var graphemeWidth = GraphemeWidth(grapheme.Text);
                if (currentColumn < beforeEnd && currentColumn + graphemeWidth <= beforeEnd)
                {
                    if (pendingAnsiBefore.Length > 0)
                    {
                        before.Append(pendingAnsiBefore);
                        pendingAnsiBefore.Clear();
                    }

                    before.Append(grapheme.Text);
                    beforeWidth += graphemeWidth;
                }
                else if (currentColumn >= afterStart && currentColumn < afterEnd)
                {
                    var fits = !strictAfter || currentColumn + graphemeWidth <= afterEnd;
                    if (fits)
                    {
                        if (!afterStarted)
                        {
                            after.Append(tracker.GetActiveCodes());
                            afterStarted = true;
                        }

                        after.Append(grapheme.Text);
                        afterWidth += graphemeWidth;
                    }
                }

                currentColumn += graphemeWidth;
                if (afterLength <= 0 ? currentColumn >= beforeEnd : currentColumn >= afterEnd)
                {
                    break;
                }
            }

            index = textEnd;
            if (afterLength <= 0 ? currentColumn >= beforeEnd : currentColumn >= afterEnd)
            {
                break;
            }
        }

        return new TextSegments(before.ToString(), beforeWidth, after.ToString(), afterWidth);
    }

    /// <summary>Composites an overlay line into a base line at a terminal-cell column.</summary>
    public static string CompositeTuiLine(
        string baseLine,
        string overlayLine,
        int startColumn,
        int overlayWidth,
        int totalWidth)
    {
        ArgumentNullException.ThrowIfNull(baseLine);
        ArgumentNullException.ThrowIfNull(overlayLine);
        if (IsImageLine(baseLine))
        {
            return baseLine;
        }

        var afterStart = startColumn + overlayWidth;
        var baseSegments = ExtractSegments(baseLine, startColumn, afterStart, totalWidth - afterStart, strictAfter: true);
        var overlay = SliceWithWidth(overlayLine, 0, overlayWidth, strict: true);
        var beforePadding = Math.Max(0, startColumn - baseSegments.BeforeWidth);
        var overlayPadding = Math.Max(0, overlayWidth - overlay.Width);
        var actualBeforeWidth = Math.Max(startColumn, baseSegments.BeforeWidth);
        var actualOverlayWidth = Math.Max(overlayWidth, overlay.Width);
        var afterTarget = Math.Max(0, totalWidth - actualBeforeWidth - actualOverlayWidth);
        var afterPadding = Math.Max(0, afterTarget - baseSegments.AfterWidth);
        var result = string.Concat(
            baseSegments.Before,
            new string(' ', beforePadding),
            _segmentReset,
            overlay.Text,
            new string(' ', overlayPadding),
            _segmentReset,
            baseSegments.After,
            new string(' ', afterPadding));

        return VisibleWidth(result) <= totalWidth
            ? result
            : SliceByColumn(result, 0, totalWidth, strict: true);
    }

    private static string[] WrapSingleLine(string line, int width)
    {
        if (line.Length == 0)
        {
            return [string.Empty];
        }

        if (VisibleWidth(line) <= width)
        {
            return [line];
        }

        var wrapped = new List<string>();
        var tracker = new AnsiCodeTracker();
        var tokens = SplitIntoTokensWithAnsi(line);
        var currentLine = new StringBuilder();
        var currentVisibleLength = 0;

        foreach (var token in tokens)
        {
            var tokenVisibleLength = VisibleWidth(token);
            var isWhitespace = IsWhitespaceChar(token);
            if (tokenVisibleLength > width && !isWhitespace)
            {
                if (currentLine.Length > 0)
                {
                    var lineEndReset = tracker.GetLineEndReset();
                    if (lineEndReset.Length > 0)
                    {
                        currentLine.Append(lineEndReset);
                    }

                    wrapped.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentVisibleLength = 0;
                }

                var broken = BreakLongWord(token, width, tracker);
                for (var index = 0; index < broken.Count - 1; index++)
                {
                    wrapped.Add(broken[index]);
                }

                currentLine.Append(broken[^1]);
                currentVisibleLength = VisibleWidth(currentLine.ToString());
                continue;
            }

            var totalNeeded = currentVisibleLength + tokenVisibleLength;
            if (totalNeeded > width && currentVisibleLength > 0)
            {
                var lineToWrap = currentLine.ToString().TrimEnd();
                var lineEndReset = tracker.GetLineEndReset();
                if (lineEndReset.Length > 0)
                {
                    lineToWrap += lineEndReset;
                }

                wrapped.Add(lineToWrap);
                currentLine.Clear();
                if (isWhitespace)
                {
                    currentLine.Append(tracker.GetActiveCodes());
                    currentVisibleLength = 0;
                }
                else
                {
                    currentLine.Append(tracker.GetActiveCodes());
                    currentLine.Append(token);
                    currentVisibleLength = tokenVisibleLength;
                }
            }
            else
            {
                currentLine.Append(token);
                currentVisibleLength += tokenVisibleLength;
            }

            UpdateTrackerFromText(token, tracker);
        }

        if (currentLine.Length > 0)
        {
            wrapped.Add(currentLine.ToString());
        }

        return wrapped.Count > 0 ? wrapped.Select(lineText => lineText.TrimEnd()).ToArray() : [string.Empty];
    }

    private static List<string> BreakLongWord(string word, int width, AnsiCodeTracker tracker)
    {
        var parts = new List<(bool IsAnsi, string Value)>();
        var index = 0;
        while (index < word.Length)
        {
            var ansi = ExtractAnsiCode(word, index);
            if (ansi is not null)
            {
                parts.Add((true, ansi.Value.Code));
                index += ansi.Value.Length;
                continue;
            }

            var end = index;
            while (end < word.Length && ExtractAnsiCode(word, end) is null)
            {
                end++;
            }

            parts.AddRange(EnumerateGraphemes(word[index..end]).Select(grapheme => (false, grapheme.Text)));
            index = end;
        }

        var lines = new List<string>();
        var currentLine = new StringBuilder(tracker.GetActiveCodes());
        var currentWidth = 0;
        foreach (var part in parts)
        {
            if (part.IsAnsi)
            {
                currentLine.Append(part.Value);
                tracker.Process(part.Value);
                continue;
            }

            if (part.Value.Length == 0)
            {
                continue;
            }

            var graphemeWidth = VisibleWidth(part.Value);
            if (currentWidth + graphemeWidth > width)
            {
                var lineEndReset = tracker.GetLineEndReset();
                if (lineEndReset.Length > 0)
                {
                    currentLine.Append(lineEndReset);
                }

                lines.Add(currentLine.ToString());
                currentLine.Clear();
                currentLine.Append(tracker.GetActiveCodes());
                currentWidth = 0;
            }

            currentLine.Append(part.Value);
            currentWidth += graphemeWidth;
        }

        if (currentLine.Length > 0)
        {
            lines.Add(currentLine.ToString());
        }

        return lines.Count > 0 ? lines : [string.Empty];
    }

    private static string FinalizeTruncatedResult(
        string prefix,
        int prefixWidth,
        string ellipsis,
        int ellipsisWidth,
        int maxWidth,
        bool pad)
    {
        var hyperlinkClose = GetActiveOsc8Close(prefix);
        var result = ellipsis.Length > 0
            ? string.Concat(prefix, hyperlinkClose, "\x1b[0m", ellipsis, "\x1b[0m")
            : string.Concat(prefix, hyperlinkClose, "\x1b[0m");
        return pad ? result + new string(' ', Math.Max(0, maxWidth - prefixWidth - ellipsisWidth)) : result;
    }

    private static TextSlice TruncateFragmentToWidth(string text, int maxWidth)
    {
        if (maxWidth <= 0 || text.Length == 0)
        {
            return new TextSlice(string.Empty, 0);
        }

        if (IsPrintableAscii(text))
        {
            var clipped = text[..Math.Min(text.Length, maxWidth)];
            return new TextSlice(clipped, clipped.Length);
        }

        var hasAnsi = text.Contains('\x1b', StringComparison.Ordinal);
        var hasTabs = text.Contains('\t', StringComparison.Ordinal);
        if (!hasAnsi && !hasTabs)
        {
            var result = new StringBuilder();
            var width = 0;
            foreach (var grapheme in EnumerateGraphemes(text))
            {
                var graphemeWidth = GraphemeWidth(grapheme.Text);
                if (width + graphemeWidth > maxWidth)
                {
                    break;
                }

                result.Append(grapheme.Text);
                width += graphemeWidth;
            }

            return new TextSlice(result.ToString(), width);
        }

        var clippedResult = new StringBuilder();
        var clippedWidth = 0;
        var pendingAnsi = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            var ansi = ExtractAnsiCode(text, index);
            if (ansi is not null)
            {
                pendingAnsi.Append(ansi.Value.Code);
                index += ansi.Value.Length;
                continue;
            }

            if (text[index] == '\t')
            {
                if (clippedWidth + _tabWidth > maxWidth)
                {
                    break;
                }

                if (pendingAnsi.Length > 0)
                {
                    clippedResult.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }

                clippedResult.Append('\t');
                clippedWidth += _tabWidth;
                index++;
                continue;
            }

            var end = index;
            while (end < text.Length && text[end] != '\t' && ExtractAnsiCode(text, end) is null)
            {
                end++;
            }

            foreach (var grapheme in EnumerateGraphemes(text[index..end]))
            {
                var graphemeWidth = GraphemeWidth(grapheme.Text);
                if (clippedWidth + graphemeWidth > maxWidth)
                {
                    return new TextSlice(clippedResult.ToString(), clippedWidth);
                }

                if (pendingAnsi.Length > 0)
                {
                    clippedResult.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }

                clippedResult.Append(grapheme.Text);
                clippedWidth += graphemeWidth;
            }

            index = end;
        }

        return new TextSlice(clippedResult.ToString(), clippedWidth);
    }

    private static bool IsPrintableAscii(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var code = text[index];
            if (code < 0x20 || code > 0x7e)
            {
                return false;
            }
        }

        return true;
    }

    private static int GraphemeWidth(string segment)
    {
        if (segment == "\t")
        {
            return _tabWidth;
        }

        if (IsTerminalSpacingMarkCluster(segment))
        {
            return EnumerateCodePoints(segment).Count();
        }

        if (EnumerateCodePoints(segment).All(codePoint => IsZeroWidthCodePoint(codePoint.Value)))
        {
            return 0;
        }

        if (CouldBeEmoji(segment) && IsRgiEmoji(segment))
        {
            return 2;
        }

        var codePoints = EnumerateCodePoints(segment).ToArray();
        var baseIndex = Array.FindIndex(codePoints, codePoint => !IsLeadingNonPrintingCodePoint(codePoint.Value));
        if (baseIndex < 0)
        {
            return 0;
        }

        var baseCodePoint = codePoints[baseIndex].Value;
        if (baseCodePoint is >= 0x1f1e6 and <= 0x1f1ff)
        {
            return 2;
        }

        var width = EastAsianWidth.GetWidth(baseCodePoint);
        var followsMark = false;
        for (var index = baseIndex + 1; index < codePoints.Length; index++)
        {
            var codePoint = codePoints[index].Value;
            if (IsTerminalSpacingMarkCodePoint(codePoint))
            {
                width += 1;
                followsMark = false;
            }
            else if (IsMarkCodePoint(codePoint))
            {
                followsMark = true;
            }
            else if (!IsNonPrintingCodePoint(codePoint))
            {
                if (followsMark || codePoint is >= 0xff00 and <= 0xffef)
                {
                    width += EastAsianWidth.GetWidth(codePoint);
                }
                else if (codePoint is 0x0e33 or 0x0eb3)
                {
                    width += 1;
                }

                followsMark = false;
            }
        }

        return width;
    }

    private static bool CouldBeEmoji(string segment)
    {
        var first = EnumerateCodePoints(segment).FirstOrDefault();
        var codePoint = first.Value;
        return codePoint is >= 0x1f000 and <= 0x1fbff ||
               codePoint is >= 0x2300 and <= 0x23ff ||
               codePoint is >= 0x2600 and <= 0x27bf ||
               codePoint is >= 0x2b50 and <= 0x2b55 ||
               segment.Contains('\ufe0f', StringComparison.Ordinal) ||
               EnumerateCodePoints(segment).Count() > 1;
    }

    private static bool IsRgiEmoji(string segment)
    {
        var codePoints = EnumerateCodePoints(segment).Select(codePoint => codePoint.Value).ToArray();
        if (codePoints.Any(codePoint => codePoint is >= 0x1f1e6 and <= 0x1f1ff))
        {
            return true;
        }

        var hasEmojiBase = codePoints.Any(codePoint =>
            codePoint is >= 0x1f000 and <= 0x1fbff ||
            codePoint is >= 0x2300 and <= 0x23ff ||
            codePoint is >= 0x2600 and <= 0x27bf ||
            codePoint is >= 0x2b50 and <= 0x2b55 ||
            codePoint is 0x23 or 0x2a or >= 0x30 and <= 0x39 ||
            codePoint is 0xa9 or 0xae);
        return hasEmojiBase && (codePoints.Length > 1 || codePoints[0] >= 0x1f000);
    }

    private static bool IsCjkBreak(string segment)
    {
        var codePoint = EnumerateCodePoints(segment).FirstOrDefault().Value;
        return codePoint is >= 0x3400 and <= 0x4dbf ||
               codePoint is >= 0x4e00 and <= 0x9fff ||
               codePoint is >= 0xf900 and <= 0xfaff ||
               codePoint is >= 0x20000 and <= 0x2ffff ||
               codePoint is >= 0x3040 and <= 0x309f ||
               codePoint is >= 0x30a0 and <= 0x30ff ||
               codePoint is >= 0x31f0 and <= 0x31ff ||
               codePoint is >= 0x1100 and <= 0x11ff ||
               codePoint is >= 0x3130 and <= 0x318f ||
               codePoint is >= 0xa960 and <= 0xa97f ||
               codePoint is >= 0xac00 and <= 0xd7af ||
               codePoint is >= 0x3100 and <= 0x312f ||
               codePoint is >= 0x31a0 and <= 0x31bf;
    }

    private static List<string> SplitIntoTokensWithAnsi(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        TokenKind? currentKind = null;
        var index = 0;

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString());
            current.Clear();
            currentKind = null;
        }

        while (index < text.Length)
        {
            var ansi = ExtractAnsiCode(text, index);
            if (ansi is not null)
            {
                pendingAnsi.Append(ansi.Value.Code);
                index += ansi.Value.Length;
                continue;
            }

            var end = FindNextAnsi(text, index);
            foreach (var grapheme in EnumerateGraphemes(text[index..end]))
            {
                var segment = grapheme.Text;
                var segmentIsSpace = segment == " ";
                if (!segmentIsSpace && IsCjkBreak(segment))
                {
                    FlushCurrent();
                    tokens.Add(pendingAnsi.ToString() + segment);
                    pendingAnsi.Clear();
                    continue;
                }

                var segmentKind = segmentIsSpace ? TokenKind.Space : TokenKind.Word;
                if (current.Length > 0 && currentKind != segmentKind)
                {
                    FlushCurrent();
                }

                if (pendingAnsi.Length > 0)
                {
                    current.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }

                currentKind = segmentKind;
                current.Append(segment);
            }

            index = end;
        }

        if (pendingAnsi.Length > 0)
        {
            if (current.Length > 0)
            {
                current.Append(pendingAnsi);
            }
            else if (tokens.Count > 0)
            {
                tokens[^1] += pendingAnsi;
            }
            else
            {
                current.Append(pendingAnsi);
            }
        }

        FlushCurrent();
        return tokens;
    }

    private static int FindNextAnsi(string text, int start)
    {
        var index = start;
        while (index < text.Length && ExtractAnsiCode(text, index) is null)
        {
            index++;
        }

        return index;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add(text[start..index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }

    private static bool IsImageLine(string line) =>
        line.Contains("\x1b_G", StringComparison.Ordinal) || line.Contains("\x1b]1337;File=", StringComparison.Ordinal);

    private static string GetActiveOsc8Close(string prefix)
    {
        if (!prefix.Contains("\x1b]8;", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        ActiveHyperlink? active = null;
        var index = 0;
        while (index < prefix.Length)
        {
            var ansi = ExtractAnsiCode(prefix, index);
            if (ansi is not null)
            {
                var parsed = ParseOsc8Hyperlink(ansi.Value.Code);
                if (parsed.IsOsc8)
                {
                    active = parsed.Hyperlink;
                }

                index += ansi.Value.Length;
            }
            else
            {
                index++;
            }
        }

        return active is null ? string.Empty : FormatOsc8Close(active.Value.Terminator);
    }

    private static (bool IsOsc8, ActiveHyperlink? Hyperlink) ParseOsc8Hyperlink(string ansiCode)
    {
        if (!ansiCode.StartsWith("\x1b]8;", StringComparison.Ordinal))
        {
            return (false, null);
        }

        var terminator = ansiCode.EndsWith('\x07') ? "\x07" : "\x1b\\";
        var bodyLength = terminator == "\x07" ? ansiCode.Length - 5 : ansiCode.Length - 6;
        if (bodyLength < 0)
        {
            return (false, null);
        }

        var body = ansiCode.Substring(4, bodyLength);
        var separator = body.IndexOf(';');
        if (separator < 0)
        {
            return (false, null);
        }

        var parameters = body[..separator];
        var url = body[(separator + 1)..];
        return (true, url.Length == 0 ? null : new ActiveHyperlink(parameters, url, terminator));
    }

    private static string FormatOsc8Hyperlink(ActiveHyperlink hyperlink) =>
        $"\x1b]8;{hyperlink.Parameters};{hyperlink.Url}{hyperlink.Terminator}";

    private static string FormatOsc8Close(string terminator) => $"\x1b]8;;{terminator}";

    private static void UpdateTrackerFromText(string text, AnsiCodeTracker tracker)
    {
        var index = 0;
        while (index < text.Length)
        {
            var ansi = ExtractAnsiCode(text, index);
            if (ansi is not null)
            {
                tracker.Process(ansi.Value.Code);
                index += ansi.Value.Length;
            }
            else
            {
                index++;
            }
        }
    }

    private static bool IsTerminalSpacingMarkCluster(string text) =>
        text.Length > 0 && EnumerateCodePoints(text).All(codePoint => IsTerminalSpacingMarkCodePoint(codePoint.Value));

    private static bool IsTerminalSpacingMarkCodePoint(int codePoint)
    {
        if (codePoint is 0x1734 or 0x302e or 0x302f)
        {
            return false;
        }

        if (codePoint is 0x065f or 0x0f7f or 0x102b or 0x102c or 0x1031 or >= 0x1033 and <= 0x1035 or
            0x1038 or >= 0x103a and <= 0x103e)
        {
            return true;
        }

        return GetUnicodeCategory(codePoint) == UnicodeCategory.SpacingCombiningMark;
    }

    private static bool IsZeroWidthCodePoint(int codePoint) =>
        IsDefaultIgnorableCodePoint(codePoint) || IsControlCodePoint(codePoint) || IsMarkCodePoint(codePoint) ||
        codePoint is >= 0xd800 and <= 0xdfff;

    private static bool IsLeadingNonPrintingCodePoint(int codePoint) =>
        IsDefaultIgnorableCodePoint(codePoint) || IsControlCodePoint(codePoint) ||
        GetUnicodeCategory(codePoint) == UnicodeCategory.Format || IsMarkCodePoint(codePoint) ||
        codePoint is >= 0xd800 and <= 0xdfff;

    private static bool IsNonPrintingCodePoint(int codePoint) => IsLeadingNonPrintingCodePoint(codePoint);

    private static bool IsMarkCodePoint(int codePoint)
    {
        var category = GetUnicodeCategory(codePoint);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private static bool IsControlCodePoint(int codePoint) =>
        GetUnicodeCategory(codePoint) is UnicodeCategory.Control or UnicodeCategory.LineSeparator or
        UnicodeCategory.ParagraphSeparator;

    private static bool IsDefaultIgnorableCodePoint(int codePoint) =>
        codePoint is 0x00ad or 0x034f or 0x061c or 0x115f or 0x1160 or 0x17b4 or 0x17b5 or
        >= 0x180b and <= 0x180f or >= 0x200b and <= 0x200f or >= 0x202a and <= 0x202e or
        >= 0x2060 and <= 0x206f or 0x3164 or >= 0xfe00 and <= 0xfe0f or 0xfeff or
        >= 0xfff0 and <= 0xfff5 or >= 0xe0000 and <= 0xe0fff;

    private static bool IsWhitespaceCodePoint(int codePoint) =>
        codePoint == 0xfeff ||
        (codePoint <= char.MaxValue && char.IsWhiteSpace((char)codePoint)) ||
        (codePoint <= 0x10ffff && Rune.GetUnicodeCategory(new Rune(codePoint)) == UnicodeCategory.SpaceSeparator);

    private static UnicodeCategory GetUnicodeCategory(int codePoint)
    {
        if (codePoint is >= 0xd800 and <= 0xdfff)
        {
            return UnicodeCategory.Surrogate;
        }

        return Rune.GetUnicodeCategory(new Rune(codePoint));
    }

    private static IEnumerable<CodePoint> EnumerateCodePoints(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (char.IsHighSurrogate(value) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                yield return new CodePoint(char.ConvertToUtf32(value, text[++index]));
            }
            else
            {
                yield return new CodePoint(value);
            }
        }
    }

    private static IEnumerable<Grapheme> EnumerateGraphemes(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return new Grapheme(enumerator.GetTextElement(), enumerator.ElementIndex);
        }
    }

    private readonly record struct CodePoint(int Value);

    private readonly record struct Grapheme(string Text, int Start);

    private readonly record struct ActiveHyperlink(string Parameters, string Url, string Terminator);

    private enum TokenKind
    {
        Space,
        Word,
    }

    private sealed class AnsiCodeTracker
    {
        private bool _bold;
        private bool _dim;
        private bool _italic;
        private bool _underline;
        private bool _blink;
        private bool _inverse;
        private bool _hidden;
        private bool _strikethrough;
        private string? _foreground;
        private string? _background;
        private ActiveHyperlink? _activeHyperlink;

        internal void Process(string ansiCode)
        {
            var parsedHyperlink = ParseOsc8Hyperlink(ansiCode);
            if (parsedHyperlink.IsOsc8)
            {
                _activeHyperlink = parsedHyperlink.Hyperlink;
                return;
            }

            if (!ansiCode.EndsWith('m') || !ansiCode.StartsWith("\x1b[", StringComparison.Ordinal))
            {
                return;
            }

            var parameters = ansiCode[2..^1];
            if (parameters.Length == 0 || parameters == "0")
            {
                ResetSgr();
                return;
            }

            var parts = parameters.Split(';');
            var index = 0;
            while (index < parts.Length)
            {
                if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    index++;
                    continue;
                }

                if (code is 38 or 48)
                {
                    if (index + 2 < parts.Length && parts[index + 1] == "5")
                    {
                        var colorCode = $"{parts[index]};{parts[index + 1]};{parts[index + 2]}";
                        if (code == 38)
                        {
                            _foreground = colorCode;
                        }
                        else
                        {
                            _background = colorCode;
                        }

                        index += 3;
                        continue;
                    }

                    if (index + 4 < parts.Length && parts[index + 1] == "2")
                    {
                        var colorCode = $"{parts[index]};{parts[index + 1]};{parts[index + 2]};{parts[index + 3]};{parts[index + 4]}";
                        if (code == 38)
                        {
                            _foreground = colorCode;
                        }
                        else
                        {
                            _background = colorCode;
                        }

                        index += 5;
                        continue;
                    }
                }

                switch (code)
                {
                    case 0:
                        ResetSgr();
                        break;
                    case 1:
                        _bold = true;
                        break;
                    case 2:
                        _dim = true;
                        break;
                    case 3:
                        _italic = true;
                        break;
                    case 4:
                        _underline = true;
                        break;
                    case 5:
                        _blink = true;
                        break;
                    case 7:
                        _inverse = true;
                        break;
                    case 8:
                        _hidden = true;
                        break;
                    case 9:
                        _strikethrough = true;
                        break;
                    case 21:
                        _bold = false;
                        break;
                    case 22:
                        _bold = false;
                        _dim = false;
                        break;
                    case 23:
                        _italic = false;
                        break;
                    case 24:
                        _underline = false;
                        break;
                    case 25:
                        _blink = false;
                        break;
                    case 27:
                        _inverse = false;
                        break;
                    case 28:
                        _hidden = false;
                        break;
                    case 29:
                        _strikethrough = false;
                        break;
                    case 39:
                        _foreground = null;
                        break;
                    case 49:
                        _background = null;
                        break;
                    default:
                        if (code is >= 30 and <= 37 or >= 90 and <= 97)
                        {
                            _foreground = code.ToString(CultureInfo.InvariantCulture);
                        }
                        else if (code is >= 40 and <= 47 or >= 100 and <= 107)
                        {
                            _background = code.ToString(CultureInfo.InvariantCulture);
                        }

                        break;
                }

                index++;
            }
        }

        internal void Clear()
        {
            ResetSgr();
            _activeHyperlink = null;
        }

        internal string GetActiveCodes()
        {
            var codes = new List<string>();
            if (_bold) codes.Add("1");
            if (_dim) codes.Add("2");
            if (_italic) codes.Add("3");
            if (_underline) codes.Add("4");
            if (_blink) codes.Add("5");
            if (_inverse) codes.Add("7");
            if (_hidden) codes.Add("8");
            if (_strikethrough) codes.Add("9");
            if (_foreground is not null) codes.Add(_foreground);
            if (_background is not null) codes.Add(_background);

            var result = codes.Count > 0 ? $"\x1b[{string.Join(';', codes)}m" : string.Empty;
            if (_activeHyperlink is not null)
            {
                result += FormatOsc8Hyperlink(_activeHyperlink.Value);
            }

            return result;
        }

        internal string GetLineEndReset()
        {
            var result = _underline ? "\x1b[24m" : string.Empty;
            if (_activeHyperlink is not null)
            {
                result += FormatOsc8Close(_activeHyperlink.Value.Terminator);
            }

            return result;
        }

        private void ResetSgr()
        {
            _bold = false;
            _dim = false;
            _italic = false;
            _underline = false;
            _blink = false;
            _inverse = false;
            _hidden = false;
            _strikethrough = false;
            _foreground = null;
            _background = null;
        }
    }
}
