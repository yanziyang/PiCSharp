// Ported from marked 18.0.5 (src/helpers.ts, and indentCodeCompensation from src/Tokenizer.ts); see LICENSE in
// this directory.
using System.Text;

namespace Pi.Tui;

internal static class MarkedHelpers
{
    /// <summary>helpers.ts <c>rtrim</c>: removes trailing <paramref name="character"/>s, or with <paramref name="invert"/> the trailing run of other characters.</summary>
    internal static string RTrim(string value, char character, bool invert = false)
    {
        var length = value.Length;
        if (length == 0) return string.Empty;
        var suffixLength = 0;
        while (suffixLength < length)
        {
            var current = value[length - suffixLength - 1];
            if (current == character && !invert) suffixLength++;
            else if (current != character && invert) suffixLength++;
            else break;
        }
        return value[..(length - suffixLength)];
    }

    /// <summary>helpers.ts <c>trimTrailingBlankLines</c>: keeps a single trailing blank line.</summary>
    internal static string TrimTrailingBlankLines(string value)
    {
        var lines = value.Split('\n');
        var end = lines.Length - 1;
        while (end >= 0 && MarkedRules.Instance.Other.BlankLine.IsMatch(lines[end])) end--;
        if (lines.Length - end <= 2) return value;
        return string.Join('\n', lines, 0, end + 1);
    }

    /// <summary>helpers.ts <c>findClosingBracket</c>: -1 when there is no closing bracket, -2 when brackets stay open.</summary>
    internal static int FindClosingBracket(string value, string brackets)
    {
        if (!value.Contains(brackets[1])) return -1;
        var level = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == brackets[0])
            {
                level++;
            }
            else if (value[i] == brackets[1])
            {
                level--;
                if (level < 0) return i;
            }
        }
        return level > 0 ? -2 : -1;
    }

    /// <summary>helpers.ts <c>expandTabs</c>. It iterates code points, so a surrogate pair advances the column once.</summary>
    internal static string ExpandTabs(string line, int indent = 0)
    {
        var column = indent;
        var expanded = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\t')
            {
                var added = 4 - (column % 4);
                expanded.Append(' ', added);
                column += added;
                continue;
            }
            expanded.Append(line[i]);
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) expanded.Append(line[++i]);
            column++;
        }
        return expanded.ToString();
    }

    /// <summary>helpers.ts <c>splitCells</c>.</summary>
    internal static List<string> SplitCells(string tableRow, int? count = null)
    {
        var rules = MarkedRules.Instance.Other;
        // Every cell-delimiting pipe gets a space before it, so an escaped pipe can be told apart.
        var row = rules.FindPipe.Replace(tableRow, match =>
        {
            var escaped = false;
            var current = match.Index;
            while (--current >= 0 && tableRow[current] == '\\') escaped = !escaped;
            return escaped ? "|" : " |";
        });
        var cells = rules.SplitPipe.Split(row).ToList();
        // The first and last cell cannot be empty when the row has no leading or trailing pipe.
        if (Js.Trim(cells[0]).Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && Js.Trim(cells[^1]).Length == 0) cells.RemoveAt(cells.Count - 1);
        if (count is { } columns and not 0)
        {
            if (cells.Count > columns) cells.RemoveRange(columns, cells.Count - columns);
            else while (cells.Count < columns) cells.Add(string.Empty);
        }
        for (var i = 0; i < cells.Count; i++) cells[i] = rules.SlashPipe.Replace(Js.Trim(cells[i]), "|");
        return cells;
    }

    /// <summary>Tokenizer.ts <c>indentCodeCompensation</c>: removes the fence's own indentation from its lines.</summary>
    internal static string IndentCodeCompensation(string raw, string text)
    {
        var rules = MarkedRules.Instance.Other;
        var matchIndentToCode = rules.IndentCodeCompensation.Match(raw);
        if (!matchIndentToCode.Success) return text;
        var indentToCode = matchIndentToCode.Groups[1].Value;
        return string.Join('\n', text.Split('\n').Select(node =>
        {
            var matchIndentInNode = rules.BeginningSpace.Match(node);
            if (!matchIndentInNode.Success) return node;
            return matchIndentInNode.Value.Length >= indentToCode.Length ? Js.Slice(node, indentToCode.Length) : node;
        }));
    }
}
