// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Text.RegularExpressions;

namespace Pi.Tui;

internal static class MarkedHelpers
{
    internal static string RTrim(string value, char character, bool invert = false)
    {
        var end = value.Length;
        while (end > 0 && (invert ? value[end - 1] != character : value[end - 1] == character)) end--;
        return value[..end];
    }

    internal static string TrimTrailingBlankLines(string value, OtherRules rules)
    {
        var lines = value.Split('\n');
        var end = lines.Length - 1;
        while (end >= 0 && rules.BlankLine.IsMatch(lines[end])) end--;
        return lines.Length - end <= 2 ? value : string.Join('\n', lines[..(end + 1)]);
    }

    internal static int FindClosingBracket(string value, char open, char close)
    {
        if (value.IndexOf(close) < 0) return -1;
        var level = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\') i++;
            else if (value[i] == open) level++;
            else if (value[i] == close && --level < 0) return i;
        }
        return level > 0 ? -2 : -1;
    }

    internal static string ExpandTabs(string line, int indent = 0)
    {
        var column = indent;
        var result = new System.Text.StringBuilder(line.Length);
        foreach (var character in line)
        {
            if (character == '\t')
            {
                var added = 4 - column % 4;
                result.Append(' ', added);
                column += added;
            }
            else { result.Append(character); column++; }
        }
        return result.ToString();
    }

    internal static string IndentCodeCompensation(string raw, string text, OtherRules rules)
    {
        var match = rules.IndentCodeCompensation.Match(raw);
        if (!match.Success) return text;
        var indent = match.Groups[1].Value;
        return string.Join('\n', text.Split('\n').Select(line =>
        {
            var leading = rules.BeginningSpace.Match(line).Value;
            return leading.Length >= indent.Length ? line[indent.Length..] : line;
        }));
    }

    internal static string[] SplitCells(string row, int? count, OtherRules rules)
    {
        var padded = new System.Text.StringBuilder(row.Length + 8);
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] != '|') { padded.Append(row[i]); continue; }
            var escaped = false;
            var cursor = i - 1;
            while (cursor >= 0 && row[cursor] == '\\') { escaped = !escaped; cursor--; }
            if (escaped) padded.Append('|');
            else padded.Append(" |");
        }
        var cells = padded.ToString().Split(" |", StringSplitOptions.None).ToList();
        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[0])) cells.RemoveAt(0);
        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1])) cells.RemoveAt(cells.Count - 1);
        if (count.HasValue)
        {
            if (cells.Count > count.Value) cells.RemoveRange(count.Value, cells.Count - count.Value);
            while (cells.Count < count.Value) cells.Add(string.Empty);
        }
        for (var i = 0; i < cells.Count; i++) cells[i] = cells[i].Trim().Replace("\\|", "|", StringComparison.Ordinal);
        return cells.ToArray();
    }

    internal static string ReplaceJavascriptPunctuationEscapes(string value, InlineRules rules)
        => rules.AnyPunctuation.Replace(value, "$1");

    internal static int RuneLength(string value) => value.EnumerateRunes().Count();
}
