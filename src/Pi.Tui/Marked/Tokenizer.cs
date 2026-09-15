// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Marked's overridable block and inline tokenizer.</summary>
public class Tokenizer
{
    private readonly MarkedOptions _defaultOptions;
    private readonly AsyncLocal<TokenizerContext?> _context = new();

    /// <summary>Creates a tokenizer with the supplied default options.</summary>
    public Tokenizer(MarkedOptions? options = null) { _defaultOptions = options ?? new MarkedOptions(); }

    /// <summary>Options for the active lexer invocation.</summary>
    public MarkedOptions Options => _context.Value?.Options ?? _defaultOptions;

    /// <summary>The active lexer invocation for the current asynchronous flow.</summary>
    public Lexer Lexer => _context.Value?.Lexer ?? throw new InvalidOperationException("Tokenizer is not attached to a lexer.");

    internal MarkedRuleSet Rules => _context.Value?.Rules ?? throw new InvalidOperationException("Tokenizer is not attached to a lexer.");

    internal void Bind(Lexer lexer, MarkedOptions options, MarkedRuleSet rules) => _context.Value = new TokenizerContext(lexer, options, rules);

    /// <summary>Tokenizes blank-line space.</summary>
    public virtual Tokens.Space? Space(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Newline);
        return match is null ? null : new Tokens.Space(match.Value);
    }

    /// <summary>Tokenizes an indented code block.</summary>
    public virtual Tokens.Code? Code(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Code);
        if (match is null) return null;
        var raw = Options.Pedantic ? match.Value : MarkedHelpers.TrimTrailingBlankLines(match.Value, Rules.Other);
        return new Tokens.Code(raw, Rules.Other.CodeRemoveIndent.Replace(raw, string.Empty)) { IsIndented = true };
    }

    /// <summary>Tokenizes a fenced code block.</summary>
    public virtual Tokens.Code? Fences(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Fences);
        if (match is null) return null;
        var raw = match.Value;
        var text = MarkedHelpers.IndentCodeCompensation(raw, Group(match, 3) ?? string.Empty, Rules.Other);
        var lang = Group(match, 2) is { } language ? MarkedHelpers.ReplaceJavascriptPunctuationEscapes(language.Trim(), Rules.Inline) : string.Empty;
        return new Tokens.Code(raw, text) { Lang = lang };
    }

    /// <summary>Tokenizes an ATX heading.</summary>
    public virtual Tokens.Heading? Heading(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Heading);
        if (match is null) return null;
        var text = (Group(match, 2) ?? string.Empty).Trim();
        if (Rules.Other.EndingHash.IsMatch(text))
        {
            var trimmed = MarkedHelpers.RTrim(text, '#');
            if (Options.Pedantic) text = trimmed.Trim();
            else if (trimmed.Length == 0 || Rules.Other.EndingSpaceChar.IsMatch(trimmed)) text = trimmed.Trim();
        }
        return new Tokens.Heading(MarkedHelpers.RTrim(match.Value, '\n'), (Group(match, 1) ?? string.Empty).Length, text, Lexer.Inline(text));
    }

    /// <summary>Tokenizes a horizontal rule.</summary>
    public virtual Tokens.Hr? Hr(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Hr);
        return match is null ? null : new Tokens.Hr(MarkedHelpers.RTrim(match.Value, '\n'));
    }

    /// <summary>Tokenizes a blockquote and recursively tokenizes its children.</summary>
    public virtual Tokens.Blockquote? Blockquote(SourceView src)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var match = AtStart(src, Rules.Block.Blockquote);
        if (match is null) return null;
        var lines = MarkedHelpers.RTrim(match.Value, '\n').Split('\n').ToList();
        var raw = string.Empty;
        var text = string.Empty;
        var tokens = new global::System.Collections.Generic.List<Token>();
        while (lines.Count > 0)
        {
            var current = new global::System.Collections.Generic.List<string>();
            var inBlockquote = false;
            var i = 0;
            for (; i < lines.Count; i++)
            {
                if (Rules.Other.BlockquoteStart.IsMatch(lines[i])) { current.Add(lines[i]); inBlockquote = true; }
                else if (!inBlockquote) current.Add(lines[i]);
                else break;
            }
            lines = lines.Skip(i).ToList();
            var currentRaw = string.Join('\n', current);
            var currentText = Rules.Other.BlockquoteSetextReplace.Replace(currentRaw, "\n    $1");
            currentText = Rules.Other.BlockquoteSetextReplace2.Replace(currentText, string.Empty);
            raw = string.IsNullOrEmpty(raw) ? currentRaw : $"{raw}\n{currentRaw}";
            text = string.IsNullOrEmpty(text) ? currentText : $"{text}\n{currentText}";
            var top = Lexer.State.Top;
            Lexer.State.Top = true;
            Lexer.BlockTokens(currentText, tokens, true);
            Lexer.State.Top = top;
            if (lines.Count == 0) break;
            var last = tokens.LastOrDefault();
            if (last is Tokens.Code) break;
            if (last is Tokens.Blockquote oldQuote)
            {
                var newText = oldQuote.Raw + "\n" + string.Join('\n', lines);
                var replacement = Blockquote(new SourceView(newText))!;
                tokens[^1] = replacement;
                raw = raw[..Math.Max(0, raw.Length - oldQuote.Raw.Length)] + replacement.Raw;
                text = text[..Math.Max(0, text.Length - oldQuote.Text.Length)] + replacement.Text;
                break;
            }
            if (last is Tokens.List oldList)
            {
                var newText = oldList.Raw + "\n" + string.Join('\n', lines);
                var replacement = List(new SourceView(newText))!;
                tokens[^1] = replacement;
                raw = raw[..Math.Max(0, raw.Length - oldList.Raw.Length)] + replacement.Raw;
                text = text[..Math.Max(0, text.Length - oldList.Raw.Length)] + replacement.Raw;
                lines = newText[replacement.Raw.Length..].Split('\n').ToList();
            }
        }
        return new Tokens.Blockquote(raw, text, tokens);
    }

    /// <summary>Tokenizes a GFM list.</summary>
    public virtual Tokens.List? List(SourceView src)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var match = AtStart(src, Rules.Block.List);
        if (match is null) return null;
        var bull = (Group(match, 1) ?? string.Empty).Trim();
        var ordered = bull.Length > 1;
        object start = ordered ? int.Parse(bull[..^1], System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var list = new Tokens.List(string.Empty, ordered, start, false, []);
        var bulletPattern = ordered ? $"\\d{{1,9}}{Regex.Escape(bull[^1].ToString())}" : Regex.Escape(bull);
        if (Options.Pedantic) bulletPattern = ordered ? bulletPattern : "[*+-]";
        var itemRegex = Rules.Other.ListItemRegex(bulletPattern);
        var remainder = src;
        var endsWithBlankLine = false;
        while (!remainder.IsEmpty)
        {
            var itemMatch = AtStart(remainder, itemRegex);
            if (itemMatch is null) break;
            if (Rules.Block.Hr.IsMatch(itemMatch.Value)) break;
            var raw = itemMatch.Value;
            remainder = remainder.Slice(raw.Length);
            var firstLine = (Group(itemMatch, 2) ?? string.Empty).Split('\n', 2)[0];
            var line = MarkedHelpers.ExpandTabs(firstLine, (Group(itemMatch, 1) ?? string.Empty).Length);
            var nextLine = remainder.Materialize().Split('\n', 2)[0];
            var blankLine = string.IsNullOrWhiteSpace(line);
            var indent = 0;
            var itemContents = string.Empty;
            if (Options.Pedantic) { indent = 2; itemContents = line.TrimStart(); }
            else if (blankLine) indent = (Group(itemMatch, 1) ?? string.Empty).Length + 1;
            else
            {
                indent = line.IndexOfAnyExcept(' ');
                if (indent < 0) indent = line.Length;
                if (indent > 4) indent = 1;
                itemContents = line[indent..];
                indent += (Group(itemMatch, 1) ?? string.Empty).Length;
            }
            var endEarly = false;
            if (blankLine && Rules.Other.BlankLine.IsMatch(nextLine))
            {
                raw += nextLine + "\n";
                remainder = remainder.Slice(Math.Min(remainder.Length, nextLine.Length + 1));
                endEarly = true;
            }
            if (!endEarly)
            {
                var nextBullet = Rules.Other.NextBulletRegex(indent);
                var hr = Rules.Other.HrRegex(indent);
                var fences = Rules.Other.FencesBeginRegex(indent);
                var heading = Rules.Other.HeadingBeginRegex(indent);
                var html = Rules.Other.HtmlBeginRegex(indent);
                var quote = Rules.Other.BlockquoteBeginRegex(indent);
                while (!remainder.IsEmpty)
                {
                    var rawLine = remainder.Materialize().Split('\n', 2)[0];
                    var nextWithoutTabs = Options.Pedantic ? Rules.Other.ListReplaceNesting.Replace(rawLine, "  ") : Rules.Other.TabCharGlobal.Replace(rawLine, "    ");
                    nextLine = rawLine;
                    if (fences.IsMatch(nextLine) || heading.IsMatch(nextLine) || html.IsMatch(nextLine) || quote.IsMatch(nextLine) || nextBullet.IsMatch(nextLine) || hr.IsMatch(nextLine)) break;
                    var nonSpace = nextWithoutTabs.IndexOfAnyExcept(' ');
                    if (nonSpace >= indent || string.IsNullOrWhiteSpace(nextLine)) itemContents += "\n" + (nextWithoutTabs.Length >= indent ? nextWithoutTabs[indent..] : string.Empty);
                    else
                    {
                        if (blankLine) break;
                        var previousNonSpace = Rules.Other.TabCharGlobal.Replace(line, "    ").IndexOfAnyExcept(' ');
                        if (previousNonSpace >= 4 || fences.IsMatch(line) || heading.IsMatch(line) || hr.IsMatch(line)) break;
                        itemContents += "\n" + nextLine;
                    }
                    blankLine = string.IsNullOrWhiteSpace(nextLine);
                    raw += rawLine + "\n";
                    remainder = remainder.Slice(Math.Min(remainder.Length, rawLine.Length + 1));
                    line = nextWithoutTabs.Length >= indent ? nextWithoutTabs[indent..] : string.Empty;
                }
            }
            if (!list.Loose)
            {
                if (endsWithBlankLine) list.Loose = true;
                else if (Rules.Other.DoubleBlankLine.IsMatch(raw)) endsWithBlankLine = true;
            }
            var task = Options.Gfm && Rules.Other.ListIsTask.IsMatch(itemContents);
            list.Items.Add(new Tokens.ListItem(raw, task, false, itemContents, []));
            list.Raw += raw;
        }
        if (list.Items.Count == 0) return null;
        var lastItem = list.Items[^1];
        lastItem.Raw = lastItem.Raw.TrimEnd();
        lastItem.Text = lastItem.Text.TrimEnd();
        list.Raw = list.Raw.TrimEnd();
        foreach (var item in list.Items)
        {
            Lexer.State.Top = false;
            item.Children = Lexer.BlockTokens(item.Text).ToList();
            var first = item.Children.FirstOrDefault();
            if (item.Task && first is Tokens.Text or Tokens.Paragraph)
            {
                item.Text = Rules.Other.ListReplaceTask.Replace(item.Text, string.Empty, 1);
                if (first is Tokens.Text firstText) { firstText.Raw = Rules.Other.ListReplaceTask.Replace(firstText.Raw, string.Empty, 1); firstText.TextValue = Rules.Other.ListReplaceTask.Replace(firstText.TextValue, string.Empty, 1); }
                if (first is Tokens.Paragraph firstParagraph) { firstParagraph.Raw = Rules.Other.ListReplaceTask.Replace(firstParagraph.Raw, string.Empty, 1); firstParagraph.Text = Rules.Other.ListReplaceTask.Replace(firstParagraph.Text, string.Empty, 1); }
                Lexer.RemoveTaskMarkerFromInlineQueue();
                var taskRaw = Rules.Other.ListTaskCheckbox.Match(item.Raw);
                if (taskRaw.Success)
                {
                    var checkbox = new Tokens.Checkbox(taskRaw.Value + " ", taskRaw.Value != "[ ]");
                    item.Checked = checkbox.Checked;
                    if (list.Loose && first is Tokens.Paragraph paragraph) { paragraph.Raw = checkbox.Raw + paragraph.Raw; paragraph.Text = checkbox.Raw + paragraph.Text; paragraph.Children.Insert(0, checkbox); }
                    else item.Children.Insert(0, checkbox);
                }
            }
            else if (item.Task) item.Task = false;
            if (!list.Loose)
            {
                var spacers = item.Children.OfType<Tokens.Space>().ToList();
                list.Loose = spacers.Any(space => Rules.Other.AnyLine.IsMatch(space.Raw));
            }
        }
        if (list.Loose)
        {
            foreach (var item in list.Items)
            {
                item.Loose = true;
                for (var childIndex = 0; childIndex < item.Children.Count; childIndex++)
                {
                    if (item.Children[childIndex] is Tokens.Text text) item.Children[childIndex] = new Tokens.Paragraph(text.Raw, text.TextValue, text.Children ?? []);
                }
            }
        }
        return list;
    }

    /// <summary>Tokenizes a block HTML construct.</summary>
    public virtual Tokens.Html? Html(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Html);
        if (match is null) return null;
        var raw = MarkedHelpers.TrimTrailingBlankLines(match.Value, Rules.Other);
        var tag = Group(match, 1);
        return new Tokens.Html(raw, tag is "pre" or "script" or "style", true, raw);
    }

    /// <summary>Tokenizes a link definition.</summary>
    public virtual Tokens.Def? Def(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Def);
        if (match is null) return null;
        var tag = (Group(match, 1) ?? string.Empty).ToLowerInvariant();
        tag = Rules.Other.MultipleSpaceGlobal.Replace(tag, " ");
        var href = Group(match, 2) is { } value ? Rules.Other.HrefBrackets.Replace(value, "$1") : string.Empty;
        href = MarkedHelpers.ReplaceJavascriptPunctuationEscapes(href, Rules.Inline);
        var title = Group(match, 3);
        var hasTitle = title is not null;
        if (title is not null) title = MarkedHelpers.ReplaceJavascriptPunctuationEscapes(title[1..^1], Rules.Inline);
        return new Tokens.Def(MarkedHelpers.RTrim(match.Value, '\n'), tag, href, title, hasTitle);
    }

    /// <summary>Tokenizes a GFM table.</summary>
    public virtual Tokens.Table? Table(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Table);
        if (match is null || !Rules.Other.TableDelimiter.IsMatch(Group(match, 2) ?? string.Empty)) return null;
        var headers = MarkedHelpers.SplitCells(Group(match, 1) ?? string.Empty, null, Rules.Other);
        var alignmentText = Rules.Other.TableAlignChars.Replace(Group(match, 2) ?? string.Empty, string.Empty);
        var alignments = alignmentText.Split('|').Select(value => Rules.Other.TableAlignRight.IsMatch(value) ? "right" : Rules.Other.TableAlignCenter.IsMatch(value) ? "center" : Rules.Other.TableAlignLeft.IsMatch(value) ? "left" : null).ToList();
        var rows = string.IsNullOrWhiteSpace(Group(match, 3)) ? [] : Rules.Other.TableRowBlankLine.Replace(Group(match, 3)!, string.Empty).Split('\n');
        if (headers.Length != alignments.Count) return null;
        var header = headers.Select((value, index) => new Tokens.TableCell(value, Lexer.Inline(value), true, alignments[index])).ToList();
        var body = rows.Select(row => MarkedHelpers.SplitCells(row, header.Count, Rules.Other).Select((value, index) => new Tokens.TableCell(value, Lexer.Inline(value), false, alignments[index])).ToList()).ToList();
        return new Tokens.Table(MarkedHelpers.RTrim(match.Value, '\n'), alignments, header, body);
    }

    /// <summary>Tokenizes a setext heading.</summary>
    public virtual Tokens.Heading? Lheading(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Lheading);
        if (match is null) return null;
        var text = (Group(match, 1) ?? string.Empty).Trim();
        return new Tokens.Heading(MarkedHelpers.RTrim(match.Value, '\n'), (Group(match, 2) ?? string.Empty).StartsWith('=') ? 1 : 2, text, Lexer.Inline(text));
    }

    /// <summary>Tokenizes a top-level paragraph.</summary>
    public virtual Tokens.Paragraph? Paragraph(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Paragraph);
        if (match is null) return null;
        var text = Group(match, 1) ?? string.Empty;
        if (text.EndsWith('\n')) text = text[..^1];
        return new Tokens.Paragraph(match.Value, text, Lexer.Inline(text));
    }

    /// <summary>Tokenizes block text.</summary>
    public virtual Tokens.Text? Text(SourceView src)
    {
        var match = AtStart(src, Rules.Block.Text);
        return match is null ? null : new Tokens.Text(match.Value, match.Value) { Children = Lexer.Inline(match.Value) };
    }

    /// <summary>Tokenizes escaped punctuation.</summary>
    public virtual Tokens.Escape? Escape(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Escape);
        return match is null ? null : new Tokens.Escape(match.Value, Group(match, 1) ?? string.Empty);
    }

    /// <summary>Tokenizes an inline HTML tag.</summary>
    public virtual Tokens.Html? Tag(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Tag);
        if (match is null) return null;
        if (!Lexer.State.InLink && Rules.Other.StartATag.IsMatch(match.Value)) Lexer.State.InLink = true;
        else if (Lexer.State.InLink && Rules.Other.EndATag.IsMatch(match.Value)) Lexer.State.InLink = false;
        if (!Lexer.State.InRawBlock && Rules.Other.StartPreScriptTag.IsMatch(match.Value)) Lexer.State.InRawBlock = true;
        else if (Lexer.State.InRawBlock && Rules.Other.EndPreScriptTag.IsMatch(match.Value)) Lexer.State.InRawBlock = false;
        return new Tokens.Html(match.Value, false, false, match.Value) { InLink = Lexer.State.InLink, InRawBlock = Lexer.State.InRawBlock };
    }

    /// <summary>Tokenizes an inline link or image.</summary>
    public virtual Token? Link(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Link);
        if (match is null) return null;
        var trimmedUrl = (Group(match, 2) ?? string.Empty).Trim();
        var cap = match.Value;
        var hrefSource = Group(match, 2) ?? string.Empty;
        var titleSource = Group(match, 3) ?? string.Empty;
        if (!Options.Pedantic && Rules.Other.StartAngleBracket.IsMatch(trimmedUrl))
        {
            if (!Rules.Other.EndAngleBracket.IsMatch(trimmedUrl)) return null;
            var noEnd = MarkedHelpers.RTrim(trimmedUrl[..^1], '\\');
            if ((trimmedUrl.Length - noEnd.Length) % 2 == 0) return null;
        }
        else
        {
            var lastParen = MarkedHelpers.FindClosingBracket(hrefSource, '(', ')');
            if (lastParen == -2) return null;
            if (lastParen > -1)
            {
                var start = cap.StartsWith('!') ? 5 : 4;
                var linkLength = start + (Group(match, 1) ?? string.Empty).Length + lastParen;
                hrefSource = hrefSource[..lastParen]; cap = cap[..Math.Min(cap.Length, linkLength)].Trim(); titleSource = string.Empty;
            }
        }
        string href; string title;
        if (Options.Pedantic)
        {
            var split = Rules.Other.PedanticHrefTitle.Match(hrefSource);
            href = split.Success ? split.Groups[1].Value : hrefSource;
            title = split.Success ? split.Groups[3].Value : string.Empty;
        }
        else title = titleSource.Length > 0 ? titleSource[1..^1] : string.Empty;
        href = hrefSource.Trim();
        if (Rules.Other.StartAngleBracket.IsMatch(href)) href = href[1..^1];
        href = MarkedHelpers.ReplaceJavascriptPunctuationEscapes(href, Rules.Inline);
        title = MarkedHelpers.ReplaceJavascriptPunctuationEscapes(title, Rules.Inline);
        var label = Rules.Other.OutputLinkReplace.Replace(Group(match, 1) ?? string.Empty, "$1");
        return MakeLink(cap, href, title, true, label);
    }

    /// <summary>Tokenizes a reference link, collapsed reference, or unresolved label.</summary>
    public virtual Token? Reflink(SourceView src, Dictionary<string, LinkReference> links)
    {
        var match = AtStart(src, Rules.Inline.RefLink) ?? AtStart(src, Rules.Inline.NoLink);
        if (match is null) return null;
        var linkString = Rules.Other.MultipleSpaceGlobal.Replace(Group(match, 2) ?? Group(match, 1) ?? string.Empty, " ");
        if (!links.TryGetValue(linkString.ToLowerInvariant(), out var link))
        {
            var text = match.Value[0].ToString();
            return new Tokens.Text(text, text);
        }
        var label = Rules.Other.OutputLinkReplace.Replace(Group(match, 1) ?? string.Empty, "$1");
        return MakeLink(match.Value, link.Href, link.Title, true, label);
    }

    /// <summary>Tokenizes emphasis and strong emphasis.</summary>
    public virtual Token? EmStrong(SourceView src, SourceView maskedSrc, string prevChar = "")
    {
        var left = AtStart(src, Rules.Inline.EmStrongLeft);
        if (left is null || (Group(left, 1) is null && Group(left, 2) is null && Group(left, 3) is null && Group(left, 4) is null)) return null;
        if (Group(left, 4) is not null && Rules.Other.UnicodeAlphaNumeric.IsMatch(prevChar)) return null;
        var next = Group(left, 1) ?? Group(left, 3) ?? string.Empty;
        if (next.Length == 0 || prevChar.Length == 0 || Rules.Inline.Punctuation.IsMatch(prevChar))
        {
            var leftLength = MarkedHelpers.RuneLength(left.Value) - 1;
            var delimiterTotal = leftLength;
            var middleDelimiterTotal = 0;
            var endRegex = left.Value[0] == '*' ? Rules.Inline.EmStrongRightAst : Rules.Inline.EmStrongRightUnd;
            var clipped = maskedSrc.Slice(Math.Min(maskedSrc.Length, leftLength));
            var scan = endRegex.Match(clipped.Source, clipped.Offset, clipped.Length);
            while (scan.Success)
            {
                var right = CaptureFirst(scan, 1, 2, 3, 4, 5, 6);
                if (right is null) { scan = scan.NextMatch(); continue; }
                var rightLength = MarkedHelpers.RuneLength(right);
                if (scan.Groups[3].Success || scan.Groups[4].Success) { delimiterTotal += rightLength; scan = scan.NextMatch(); continue; }
                if ((scan.Groups[5].Success || scan.Groups[6].Success) && leftLength % 3 != 0 && (leftLength + rightLength) % 3 == 0) { middleDelimiterTotal += rightLength; scan = scan.NextMatch(); continue; }
                delimiterTotal -= rightLength;
                if (delimiterTotal > 0) { scan = scan.NextMatch(); continue; }
                rightLength = Math.Min(rightLength, rightLength + delimiterTotal + middleDelimiterTotal);
                var lastCharLength = char.IsHighSurrogate(scan.Value[0]) && scan.Value.Length > 1 ? 2 : 1;
                var rawLength = leftLength + (scan.Index - clipped.Offset) + lastCharLength + rightLength;
                var raw = src.Slice(0, Math.Min(src.Length, rawLength)).Materialize();
                if (Math.Min(leftLength, rightLength) % 2 == 1)
                {
                    var text = raw[1..^1];
                    return new Tokens.Em(raw, text, Lexer.InlineTokens(text));
                }
                var strongText = raw[2..^2];
                return new Tokens.Strong(raw, strongText, Lexer.InlineTokens(strongText));
            }
        }
        return null;
    }

    /// <summary>Tokenizes an inline code span.</summary>
    public virtual Tokens.Codespan? Codespan(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Code);
        if (match is null) return null;
        var text = Rules.Other.NewLineCharGlobal.Replace(Group(match, 2) ?? string.Empty, " ");
        if (Rules.Other.NonSpaceChar.IsMatch(text) && Rules.Other.StartingSpaceChar.IsMatch(text) && Rules.Other.EndingSpaceChar.IsMatch(text)) text = text[1..^1];
        return new Tokens.Codespan(match.Value, text);
    }

    /// <summary>Tokenizes a hard line break.</summary>
    public virtual Tokens.Br? Br(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Br);
        return match is null ? null : new Tokens.Br(match.Value);
    }

    /// <summary>Tokenizes GFM strikethrough.</summary>
    public virtual Tokens.Del? Del(SourceView src, SourceView maskedSrc, string prevChar = "")
    {
        var left = AtStart(src, Rules.Inline.DelLDelim);
        if (left is null) return null;
        var next = Group(left, 1) ?? string.Empty;
        if (next.Length == 0 || prevChar.Length == 0 || Rules.Inline.Punctuation.IsMatch(prevChar))
        {
            var leftLength = MarkedHelpers.RuneLength(left.Value) - 1;
            var delimiterTotal = leftLength;
            var clipped = maskedSrc.Slice(Math.Min(maskedSrc.Length, leftLength));
            var scan = Rules.Inline.DelRDelim.Match(clipped.Source, clipped.Offset, clipped.Length);
            while (scan.Success)
            {
                var right = CaptureFirst(scan, 1, 2, 3, 4, 5, 6);
                if (right is null) { scan = scan.NextMatch(); continue; }
                var rightLength = MarkedHelpers.RuneLength(right);
                if (scan.Groups[3].Success || scan.Groups[4].Success) { delimiterTotal += rightLength; scan = scan.NextMatch(); continue; }
                if (rightLength != leftLength) { scan = scan.NextMatch(); continue; }
                delimiterTotal -= rightLength;
                if (delimiterTotal > 0) { scan = scan.NextMatch(); continue; }
                rightLength = Math.Min(rightLength, rightLength + delimiterTotal);
                var lastCharLength = char.IsHighSurrogate(scan.Value[0]) && scan.Value.Length > 1 ? 2 : 1;
                var rawLength = leftLength + (scan.Index - clipped.Offset) + lastCharLength + rightLength;
                var raw = src.Slice(0, Math.Min(src.Length, rawLength)).Materialize();
                var text = raw[leftLength..^leftLength];
                return new Tokens.Del(raw, text, Lexer.InlineTokens(text));
            }
        }
        return null;
    }

    /// <summary>Tokenizes angle-bracket autolinks.</summary>
    public virtual Tokens.Link? Autolink(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Autolink);
        if (match is null) return null;
        var text = Group(match, 1) ?? string.Empty;
        var email = Group(match, 2) == "@";
        return new Tokens.Link(match.Value, email ? "mailto:" + text : text, null, false, text, [new Tokens.Text(text, text)]);
    }

    /// <summary>Tokenizes GFM bare URLs and email addresses.</summary>
    public virtual Tokens.Link? Url(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Url);
        if (match is null) return null;
        var text = match.Value;
        string href;
        if (Group(match, 2) == "@") href = "mailto:" + text;
        else
        {
            string previous;
            do { previous = text; var back = Rules.Inline.Backpedal.Match(text); text = back.Success ? back.Value : string.Empty; } while (previous != text);
            href = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + text : text;
        }
        return new Tokens.Link(text, href, null, false, text, [new Tokens.Text(text, text)]);
    }

    /// <summary>Tokenizes ordinary inline text.</summary>
    public virtual Tokens.Text? InlineText(SourceView src)
    {
        var match = AtStart(src, Rules.Inline.Text);
        return match is null ? null : new Tokens.Text(match.Value, match.Value, Lexer.State.InRawBlock);
    }

    private Token MakeLink(string raw, string href, string? title, bool hasTitle, string text)
    {
        var children = Lexer.InlineTokens(text);
        if (raw.StartsWith('!')) return new Tokens.Image(raw, href, string.IsNullOrEmpty(title) ? null : title, text, children);
        return new Tokens.Link(raw, href, string.IsNullOrEmpty(title) ? null : title, hasTitle, text, children);
    }

    private static Match? AtStart(SourceView source, Regex regex) => source.TryMatch(regex, out var match) ? match : null;
    private static string? Group(Match match, int number) => match.Groups[number].Success ? match.Groups[number].Value : null;
    private static string? CaptureFirst(Match match, params int[] numbers)
    {
        foreach (var number in numbers) if (match.Groups[number].Success) return match.Groups[number].Value;
        return null;
    }
}

internal sealed record TokenizerContext(Lexer Lexer, MarkedOptions Options, MarkedRuleSet Rules);
