using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>One targeted exact-text replacement.</summary>
public sealed record Edit(string OldText, string NewText);

/// <summary>Result of searching for one replacement target.</summary>
public sealed record FuzzyMatchResult(
    bool Found,
    int Index,
    int MatchLength,
    bool UsedFuzzyMatch,
    string ContentForReplacement);

/// <summary>Content before and after applying a group of edits.</summary>
public sealed record AppliedEditsResult(string BaseContent, string NewContent);

/// <summary>Display diff and first changed line.</summary>
public sealed record DiffStringResult(string Diff, int? FirstChangedLine);

/// <summary>Line-ending and replacement utilities shared by edit-like tools.</summary>
public static class EditDiff
{
    private static readonly Regex _unicodeDashPattern = new("[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]", RegexOptions.CultureInvariant);
    private static readonly Regex _unicodeSpacePattern = new("[\u00A0\u2002-\u200A\u202F\u205F\u3000]", RegexOptions.CultureInvariant);

    /// <summary>Detects whether the first newline is CRLF or LF.</summary>
    public static string DetectLineEnding(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var crlfIndex = content.IndexOf("\r\n", StringComparison.Ordinal);
        var lfIndex = content.IndexOf('\n');
        if (lfIndex < 0 || crlfIndex < 0)
        {
            return "\n";
        }

        return crlfIndex < lfIndex ? "\r\n" : "\n";
    }

    /// <summary>Normalizes CRLF and CR line endings to LF.</summary>
    public static string NormalizeToLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Restores the requested line ending.</summary>
    public static string RestoreLineEndings(string text, string ending) =>
        ending == "\r\n" ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;

    /// <summary>Applies the upstream fuzzy-normalisation sequence.</summary>
    public static string NormalizeForFuzzyMatch(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Normalize(NormalizationForm.FormKC)
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .ToArray();
        var normalized = string.Join('\n', lines)
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201A', '\'')
            .Replace('\u201B', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u201E', '"')
            .Replace('\u201F', '"');
        normalized = _unicodeDashPattern.Replace(normalized, "-");
        return _unicodeSpacePattern.Replace(normalized, " ");
    }

    /// <summary>Finds an exact target first and then a fuzzy-normalised target.</summary>
    public static FuzzyMatchResult FuzzyFindText(string content, string oldText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);
        var exactIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exactIndex >= 0)
        {
            return new FuzzyMatchResult(true, exactIndex, oldText.Length, false, content);
        }

        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        var fuzzyIndex = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal);
        return fuzzyIndex < 0
            ? new FuzzyMatchResult(false, -1, 0, false, content)
            : new FuzzyMatchResult(true, fuzzyIndex, fuzzyOldText.Length, true, fuzzyContent);
    }

    /// <summary>Removes a UTF-8 BOM while returning it for restoration.</summary>
    public static (string Bom, string Text) StripBom(string content) =>
        content.StartsWith('\uFEFF') ? ("\uFEFF", content[1..]) : (string.Empty, content);

    /// <summary>Applies replacements matched against the same normalized source content.</summary>
    public static AppliedEditsResult ApplyEditsToNormalizedContent(
        string normalizedContent,
        IReadOnlyList<Edit> edits,
        string path)
    {
        ArgumentNullException.ThrowIfNull(normalizedContent);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(path);
        if (edits.Count == 0)
        {
            throw new ArgumentException("Edit tool input is invalid. edits must contain at least one replacement.", nameof(edits));
        }

        var normalizedEdits = edits
            .Select(static edit => new Edit(NormalizeToLf(edit.OldText), NormalizeToLf(edit.NewText)))
            .ToArray();
        for (var index = 0; index < normalizedEdits.Length; index++)
        {
            if (normalizedEdits[index].OldText.Length == 0)
            {
                throw new InvalidOperationException(GetEmptyOldTextError(path, index, normalizedEdits.Length));
            }
        }

        var initialMatches = normalizedEdits.Select(edit => FuzzyFindText(normalizedContent, edit.OldText)).ToArray();
        var usedFuzzyMatch = initialMatches.Any(static match => match.UsedFuzzyMatch);
        var replacementBaseContent = usedFuzzyMatch ? NormalizeForFuzzyMatch(normalizedContent) : normalizedContent;
        var matchedEdits = new List<MatchedEdit>(normalizedEdits.Length);
        for (var index = 0; index < normalizedEdits.Length; index++)
        {
            var edit = normalizedEdits[index];
            var matchResult = FuzzyFindText(replacementBaseContent, edit.OldText);
            if (!matchResult.Found)
            {
                throw new InvalidOperationException(GetNotFoundError(path, index, normalizedEdits.Length));
            }

            var occurrences = CountOccurrences(replacementBaseContent, edit.OldText);
            if (occurrences > 1)
            {
                throw new InvalidOperationException(GetDuplicateError(path, index, normalizedEdits.Length, occurrences));
            }

            matchedEdits.Add(new MatchedEdit(index, matchResult.Index, matchResult.MatchLength, edit.NewText));
        }

        matchedEdits.Sort(static (left, right) => left.MatchIndex.CompareTo(right.MatchIndex));
        for (var index = 1; index < matchedEdits.Count; index++)
        {
            var previous = matchedEdits[index - 1];
            var current = matchedEdits[index];
            if (previous.MatchIndex + previous.MatchLength > current.MatchIndex)
            {
                throw new InvalidOperationException(
                    $"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
            }
        }

        var baseContent = normalizedContent;
        var newContent = usedFuzzyMatch
            ? ApplyReplacementsPreservingUnchangedLines(normalizedContent, replacementBaseContent, matchedEdits)
            : ApplyReplacements(replacementBaseContent, matchedEdits);
        if (baseContent == newContent)
        {
            throw new InvalidOperationException(GetNoChangeError(path, normalizedEdits.Length));
        }

        return new AppliedEditsResult(baseContent, newContent);
    }

    /// <summary>Generates the standard unified patch used in tool details.</summary>
    public static string GenerateUnifiedPatch(
        string path,
        string oldContent,
        string newContent,
        int contextLines = 4)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(oldContent);
        ArgumentNullException.ThrowIfNull(newContent);
        var operations = BuildDiffOperations(oldContent, newContent);
        var changed = operations
            .Select((operation, index) => (operation, index))
            .Where(static pair => pair.operation.Kind != DiffKind.Unchanged)
            .Select(static pair => pair.index)
            .ToArray();

        var builder = new StringBuilder();
        builder.Append("===================================================================\n");
        builder.Append("--- ").Append(path).Append('\n');
        builder.Append("+++ ").Append(path).Append('\n');
        if (changed.Length == 0)
        {
            return builder.ToString();
        }

        var hunks = BuildHunks(changed, operations.Count, Math.Max(0, contextLines));
        foreach (var hunk in hunks)
        {
            var slice = operations.Skip(hunk.Start).Take(hunk.End - hunk.Start).ToArray();
            var oldStart = slice[0].OldLine;
            var newStart = slice[0].NewLine;
            var oldCount = slice.Count(static operation => operation.Kind is DiffKind.Unchanged or DiffKind.Removed);
            var newCount = slice.Count(static operation => operation.Kind is DiffKind.Unchanged or DiffKind.Added);
            builder.Append("@@ -").Append(FormatRange(oldStart, oldCount))
                .Append(" +").Append(FormatRange(newStart, newCount)).Append(" @@\n");
            foreach (var operation in slice)
            {
                var prefix = operation.Kind switch
                {
                    DiffKind.Added => '+',
                    DiffKind.Removed => '-',
                    _ => ' ',
                };
                builder.Append(prefix).Append(operation.Text).Append('\n');
                if (!operation.HasNewline)
                {
                    builder.Append("\\ No newline at end of file\n");
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Generates the line-numbered display diff shown by the edit tool.</summary>
    public static DiffStringResult GenerateDiffString(
        string oldContent,
        string newContent,
        int contextLines = 4)
    {
        var parts = BuildDiffParts(oldContent, newContent);
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var lineNumberWidth = Math.Max(oldLines.Length, newLines.Length).ToString(CultureInfo.InvariantCulture).Length;
        var oldLineNumber = 1;
        var newLineNumber = 1;
        var lastWasChange = false;
        int? firstChangedLine = null;
        var output = new List<string>();

        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            if (part.Kind is DiffKind.Added or DiffKind.Removed)
            {
                firstChangedLine ??= newLineNumber;
                foreach (var line in part.Lines)
                {
                    if (part.Kind == DiffKind.Added)
                    {
                        output.Add($"+{newLineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumberWidth)} {line.Text}");
                        newLineNumber++;
                    }
                    else
                    {
                        output.Add($"-{oldLineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumberWidth)} {line.Text}");
                        oldLineNumber++;
                    }
                }

                lastWasChange = true;
                continue;
            }

            var nextPartIsChange = partIndex < parts.Count - 1 && parts[partIndex + 1].Kind is DiffKind.Added or DiffKind.Removed;
            var hasLeadingChange = lastWasChange;
            var hasTrailingChange = nextPartIsChange;
            var raw = part.Lines.Select(static line => line.Text).ToArray();
            if (hasLeadingChange && hasTrailingChange)
            {
                if (raw.Length <= contextLines * 2)
                {
                    AppendContext(output, raw, ref oldLineNumber, ref newLineNumber, lineNumberWidth);
                }
                else
                {
                    AppendContext(output, raw[..contextLines], ref oldLineNumber, ref newLineNumber, lineNumberWidth);
                    output.Add($" {string.Empty.PadLeft(lineNumberWidth)} ...");
                    var skipped = raw.Length - contextLines * 2;
                    oldLineNumber += skipped;
                    newLineNumber += skipped;
                    AppendContext(output, raw[^contextLines..], ref oldLineNumber, ref newLineNumber, lineNumberWidth);
                }
            }
            else if (hasLeadingChange)
            {
                var shown = raw[..Math.Min(contextLines, raw.Length)];
                AppendContext(output, shown, ref oldLineNumber, ref newLineNumber, lineNumberWidth);
                var skipped = raw.Length - shown.Length;
                if (skipped > 0)
                {
                    output.Add($" {string.Empty.PadLeft(lineNumberWidth)} ...");
                    oldLineNumber += skipped;
                    newLineNumber += skipped;
                }
            }
            else if (hasTrailingChange)
            {
                var skipped = Math.Max(0, raw.Length - contextLines);
                if (skipped > 0)
                {
                    output.Add($" {string.Empty.PadLeft(lineNumberWidth)} ...");
                    oldLineNumber += skipped;
                    newLineNumber += skipped;
                }

                AppendContext(output, raw[skipped..], ref oldLineNumber, ref newLineNumber, lineNumberWidth);
            }
            else
            {
                oldLineNumber += raw.Length;
                newLineNumber += raw.Length;
            }

            lastWasChange = false;
        }

        return new DiffStringResult(string.Join('\n', output), firstChangedLine);
    }

    private static void AppendContext(
        List<string> output,
        IReadOnlyList<string> lines,
        ref int oldLineNumber,
        ref int newLineNumber,
        int width)
    {
        foreach (var line in lines)
        {
            output.Add($" {oldLineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width)} {line}");
            oldLineNumber++;
            newLineNumber++;
        }
    }

    private static string FormatRange(int start, int count) =>
        count == 1 ? start.ToString(CultureInfo.InvariantCulture) :
        $"{start.ToString(CultureInfo.InvariantCulture)},{count.ToString(CultureInfo.InvariantCulture)}";

    private static List<(int Start, int End)> BuildHunks(IReadOnlyList<int> changed, int operationCount, int contextLines)
    {
        var hunks = new List<(int Start, int End)>();
        foreach (var changedIndex in changed)
        {
            var start = Math.Max(0, changedIndex - contextLines);
            var end = Math.Min(operationCount, changedIndex + contextLines + 1);
            if (hunks.Count > 0 && start <= hunks[^1].End)
            {
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
            }
            else
            {
                hunks.Add((start, end));
            }
        }

        return hunks;
    }

    private static List<DiffPart> BuildDiffParts(string oldContent, string newContent)
    {
        var oldLines = SplitLinesWithEndings(oldContent);
        var newLines = SplitLinesWithEndings(newContent);
        var oldCount = oldLines.Count;
        var newCount = newLines.Count;
        var lcs = new int[oldCount + 1, newCount + 1];
        for (var oldIndex = oldCount - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newCount - 1; newIndex >= 0; newIndex--)
            {
                lcs[oldIndex, newIndex] = LinesEqual(oldLines[oldIndex], newLines[newIndex])
                    ? lcs[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lcs[oldIndex + 1, newIndex], lcs[oldIndex, newIndex + 1]);
            }
        }

        var parts = new List<DiffPart>();
        var oldPosition = 0;
        var newPosition = 0;
        while (oldPosition < oldCount || newPosition < newCount)
        {
            if (oldPosition < oldCount && newPosition < newCount && LinesEqual(oldLines[oldPosition], newLines[newPosition]))
            {
                AddPartLine(parts, DiffKind.Unchanged, oldLines[oldPosition++]);
                newPosition++;
            }
            else if (oldPosition < oldCount &&
                (newPosition >= newCount || lcs[oldPosition + 1, newPosition] >= lcs[oldPosition, newPosition + 1]))
            {
                AddPartLine(parts, DiffKind.Removed, oldLines[oldPosition++]);
            }
            else
            {
                AddPartLine(parts, DiffKind.Added, newLines[newPosition++]);
            }
        }

        return parts;
    }

    private static List<DiffOperation> BuildDiffOperations(string oldContent, string newContent)
    {
        var oldLine = 1;
        var newLine = 1;
        var operations = new List<DiffOperation>();
        foreach (var part in BuildDiffParts(oldContent, newContent))
        {
            foreach (var line in part.Lines)
            {
                operations.Add(new DiffOperation(part.Kind, line.Text, line.HasNewline, oldLine, newLine));
                if (part.Kind is DiffKind.Unchanged or DiffKind.Removed)
                {
                    oldLine++;
                }
                if (part.Kind is DiffKind.Unchanged or DiffKind.Added)
                {
                    newLine++;
                }
            }
        }

        return operations;
    }

    private static void AddPartLine(List<DiffPart> parts, DiffKind kind, LineToken line)
    {
        if (parts.Count == 0 || parts[^1].Kind != kind)
        {
            parts.Add(new DiffPart(kind));
        }

        parts[^1].Lines.Add(line);
    }

    private static List<LineToken> SplitLinesWithEndings(string content)
    {
        var lines = new List<LineToken>();
        var start = 0;
        while (start < content.Length)
        {
            var newline = content.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(new LineToken(content[start..], false));
                break;
            }

            lines.Add(new LineToken(content[start..newline], true));
            start = newline + 1;
        }

        return lines;
    }

    private static bool LinesEqual(LineToken left, LineToken right) =>
        left.HasNewline == right.HasNewline && left.Text == right.Text;

    private static int CountOccurrences(string content, string oldText)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        return fuzzyContent.Split(fuzzyOldText, StringSplitOptions.None).Length - 1;
    }

    private static string ApplyReplacements(string content, IReadOnlyList<MatchedEdit> replacements)
    {
        var result = content;
        for (var index = replacements.Count - 1; index >= 0; index--)
        {
            var replacement = replacements[index];
            var matchIndex = replacement.MatchIndex;
            result = result[..matchIndex] + replacement.NewText + result[(matchIndex + replacement.MatchLength)..];
        }

        return result;
    }

    private static string ApplyReplacementsPreservingUnchangedLines(
        string originalContent,
        string baseContent,
        IReadOnlyList<MatchedEdit> replacements)
    {
        var originalLines = SplitLinesWithEndings(originalContent);
        var baseLines = GetLineSpans(baseContent);
        if (originalLines.Count != baseLines.Count)
        {
            throw new InvalidOperationException("Cannot preserve unchanged lines because the base content has a different line count.");
        }

        var groups = new List<ReplacementGroup>();
        foreach (var replacement in replacements.OrderBy(static replacement => replacement.MatchIndex))
        {
            var range = GetReplacementLineRange(baseLines, replacement);
            if (groups.Count > 0 && range.StartLine < groups[^1].EndLine)
            {
                groups[^1].EndLine = Math.Max(groups[^1].EndLine, range.EndLine);
                groups[^1].Replacements.Add(replacement);
            }
            else
            {
                groups.Add(new ReplacementGroup(range.StartLine, range.EndLine, [replacement]));
            }
        }

        var originalLineIndex = 0;
        var result = new StringBuilder();
        foreach (var group in groups)
        {
            for (var index = originalLineIndex; index < group.StartLine; index++)
            {
                result.Append(originalLines[index].Text);
                if (originalLines[index].HasNewline)
                {
                    result.Append('\n');
                }
            }

            var groupStartOffset = baseLines[group.StartLine].Start;
            var groupEndOffset = baseLines[group.EndLine - 1].End;
            result.Append(ApplyReplacements(baseContent[groupStartOffset..groupEndOffset], group.Replacements));
            originalLineIndex = group.EndLine;
        }

        for (var index = originalLineIndex; index < originalLines.Count; index++)
        {
            result.Append(originalLines[index].Text);
            if (originalLines[index].HasNewline)
            {
                result.Append('\n');
            }
        }

        return result.ToString();
    }

    private static List<LineSpan> GetLineSpans(string content)
    {
        var spans = new List<LineSpan>();
        var offset = 0;
        foreach (var line in SplitLinesWithEndings(content))
        {
            var length = line.Text.Length + (line.HasNewline ? 1 : 0);
            spans.Add(new LineSpan(offset, offset + length));
            offset += length;
        }

        return spans;
    }

    private static (int StartLine, int EndLine) GetReplacementLineRange(
        IReadOnlyList<LineSpan> lines,
        MatchedEdit replacement)
    {
        var replacementStart = replacement.MatchIndex;
        var replacementEnd = replacement.MatchIndex + replacement.MatchLength;
        var startLine = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (replacementStart >= lines[index].Start && replacementStart < lines[index].End)
            {
                startLine = index;
                break;
            }
        }

        if (startLine < 0)
        {
            throw new InvalidOperationException("Replacement range is outside the base content.");
        }

        var endLine = startLine;
        while (endLine < lines.Count && lines[endLine].End < replacementEnd)
        {
            endLine++;
        }
        if (endLine >= lines.Count)
        {
            throw new InvalidOperationException("Replacement range is outside the base content.");
        }

        return (startLine, endLine + 1);
    }

    private static string GetNotFoundError(string path, int index, int totalEdits) => totalEdits == 1
        ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
        : $"Could not find edits[{index}] in {path}. The oldText must match exactly including all whitespace and newlines.";

    private static string GetDuplicateError(string path, int index, int totalEdits, int occurrences) => totalEdits == 1
        ? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
        : $"Found {occurrences} occurrences of edits[{index}] in {path}. Each oldText must be unique. Please provide more context to make it unique.";

    private static string GetEmptyOldTextError(string path, int index, int totalEdits) => totalEdits == 1
        ? $"oldText must not be empty in {path}."
        : $"edits[{index}].oldText must not be empty in {path}.";

    private static string GetNoChangeError(string path, int totalEdits) => totalEdits == 1
        ? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
        : $"No changes made to {path}. The replacements produced identical content.";

    private sealed record LineToken(string Text, bool HasNewline);

    private sealed class DiffPart(DiffKind kind)
    {
        public DiffKind Kind { get; } = kind;
        public List<LineToken> Lines { get; } = [];
    }

    private sealed record DiffOperation(DiffKind Kind, string Text, bool HasNewline, int OldLine, int NewLine);

    private sealed record LineSpan(int Start, int End);

    private sealed record MatchedEdit(int EditIndex, int MatchIndex, int MatchLength, string NewText);

    private sealed class ReplacementGroup(int startLine, int endLine, List<MatchedEdit> replacements)
    {
        public int StartLine { get; } = startLine;
        public int EndLine { get; set; } = endLine;
        public List<MatchedEdit> Replacements { get; } = replacements;
    }

    private enum DiffKind
    {
        Unchanged,
        Added,
        Removed,
    }
}
