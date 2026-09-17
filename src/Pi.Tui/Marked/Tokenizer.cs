// Ported from marked 18.0.5 (src/Tokenizer.ts); see LICENSE in this directory.
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using TokenList = System.Collections.Generic.List<Pi.Tui.Token>;

namespace Pi.Tui;

/// <summary>
/// marked's overridable block and inline tokenizer. Each method mirrors the Tokenizer.ts method of the same name and
/// receives <c>src</c> as a <see cref="SourceView"/>. Pedantic branches are not ported: <see cref="Lexer"/> rejects
/// that option.
/// </summary>
public class Tokenizer
{
    private readonly MarkedOptions _defaultOptions;

    // One tokenizer may serve many lexers at once (setOptions({ tokenizer })), so this.lexer and this.options are
    // bound per flow of execution instead of being stored on the shared instance.
    private readonly AsyncLocal<TokenizerContext?> _context = new();

    /// <summary>Creates a tokenizer with the supplied default options.</summary>
    public Tokenizer(MarkedOptions? options = null) { _defaultOptions = options ?? new MarkedOptions(); }

    /// <summary><c>this.options</c>: the options of the active lexer.</summary>
    public MarkedOptions Options => _context.Value?.Options ?? _defaultOptions;

    /// <summary><c>this.lexer</c>: the active lexer.</summary>
    public Lexer Lexer => _context.Value?.Lexer ?? throw new InvalidOperationException("Tokenizer is not attached to a lexer.");

    /// <summary><c>this.rules</c>.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "this.rules is an instance member in marked, and subclasses reach it that way.")]
    public MarkedRules Rules => MarkedRules.Instance;

    internal void Bind(Lexer lexer, MarkedOptions options)
    {
        var current = _context.Value;
        if (current is null || !ReferenceEquals(current.Lexer, lexer) || !ReferenceEquals(current.Options, options))
        {
            _context.Value = new TokenizerContext(lexer, options);
        }
    }

    /// <summary>Tokenizer.ts <c>space</c>.</summary>
    public virtual Tokens.Space? Space(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Newline);
        return cap is not null && cap.Length > 0 ? new Tokens.Space(cap.Value) : null;
    }

    /// <summary>Tokenizer.ts <c>code</c>: indented code.</summary>
    public virtual Tokens.Code? Code(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Code);
        if (cap is null) return null;
        var raw = MarkedHelpers.TrimTrailingBlankLines(cap.Value);
        var text = Js.ReplaceAll(Rules.Other.CodeRemoveIndent, raw, string.Empty);
        return new Tokens.Code(raw, text) { IsIndented = true };
    }

    /// <summary>Tokenizer.ts <c>fences</c>.</summary>
    public virtual Tokens.Code? Fences(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Fences);
        if (cap is null) return null;
        var raw = cap.Value;
        var text = MarkedHelpers.IndentCodeCompensation(raw, Group(cap, 3) ?? string.Empty);
        var language = Group(cap, 2);
        var lang = Js.Truthy(language) ? Js.ReplaceAll(Rules.Inline.AnyPunctuation, Js.Trim(language!), "$1") : language;
        return new Tokens.Code(raw, text) { Lang = lang };
    }

    /// <summary>Tokenizer.ts <c>heading</c>.</summary>
    public virtual Tokens.Heading? Heading(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Heading);
        if (cap is null) return null;
        var text = Js.Trim(Group(cap, 2)!);
        if (Rules.Other.EndingHash.IsMatch(text))
        {
            var trimmed = MarkedHelpers.RTrim(text, '#');
            // CommonMark requires a space before trailing #s.
            if (!Js.Truthy(trimmed) || Rules.Other.EndingSpaceChar.IsMatch(trimmed)) text = Js.Trim(trimmed);
        }
        return new Tokens.Heading(MarkedHelpers.RTrim(cap.Value, '\n'), Group(cap, 1)!.Length, text, Lexer.Inline(text));
    }

    /// <summary>Tokenizer.ts <c>hr</c>.</summary>
    public virtual Tokens.Hr? Hr(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Hr);
        return cap is null ? null : new Tokens.Hr(MarkedHelpers.RTrim(cap.Value, '\n'));
    }

    /// <summary>Tokenizer.ts <c>blockquote</c>.</summary>
    public virtual Tokens.Blockquote? Blockquote(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Blockquote);
        if (cap is null) return null;
        var lines = MarkedHelpers.RTrim(cap.Value, '\n').Split('\n').ToList();
        var raw = string.Empty;
        var text = string.Empty;
        var tokens = new TokenList();
        while (lines.Count > 0)
        {
            var inBlockquote = false;
            var currentLines = new List<string>();
            int i;
            for (i = 0; i < lines.Count; i++)
            {
                // Collect lines up to a continuation.
                if (Rules.Other.BlockquoteStart.IsMatch(lines[i]))
                {
                    currentLines.Add(lines[i]);
                    inBlockquote = true;
                }
                else if (!inBlockquote)
                {
                    currentLines.Add(lines[i]);
                }
                else
                {
                    break;
                }
            }
            lines = lines.GetRange(i, lines.Count - i);

            var currentRaw = string.Join('\n', currentLines);
            // Precede a setext continuation with four spaces so it is not a setext heading.
            var currentText = Js.ReplaceAll(Rules.Other.BlockquoteSetextReplace2, Js.ReplaceAll(Rules.Other.BlockquoteSetextReplace, currentRaw, "\n    $1"), string.Empty);
            raw = Js.Truthy(raw) ? raw + "\n" + currentRaw : currentRaw;
            text = Js.Truthy(text) ? text + "\n" + currentText : currentText;

            // Parse the quoted lines as top-level tokens, merging paragraphs when this is a continuation.
            var top = Lexer.State.Top;
            Lexer.State.Top = true;
            Lexer.BlockTokens(currentText, tokens, true);
            Lexer.State.Top = top;

            if (lines.Count == 0) break;
            var lastToken = tokens.Count > 0 ? tokens[^1] : null;
            if (lastToken?.Type == "code") break;
            if (lastToken?.Type == "blockquote" && lastToken is Tokens.Blockquote oldQuote)
            {
                // Include the continuation in the nested blockquote.
                var newText = oldQuote.Raw + "\n" + string.Join('\n', lines);
                var newToken = Blockquote(new SourceView(newText))!;
                tokens[^1] = newToken;
                raw = Js.Substring(raw, 0, raw.Length - oldQuote.Raw.Length) + newToken.Raw;
                text = Js.Substring(text, 0, text.Length - oldQuote.Text.Length) + newToken.Text;
                break;
            }
            if (lastToken?.Type == "list" && lastToken is Tokens.List oldList)
            {
                // Include the continuation in the nested list.
                var newText = oldList.Raw + "\n" + string.Join('\n', lines);
                var newToken = List(new SourceView(newText))!;
                tokens[^1] = newToken;
                raw = Js.Substring(raw, 0, raw.Length - oldList.Raw.Length) + newToken.Raw;
                text = Js.Substring(text, 0, text.Length - oldList.Raw.Length) + newToken.Raw;
                lines = Js.Substring(newText, tokens[^1].Raw.Length).Split('\n').ToList();
            }
        }
        return new Tokens.Blockquote(raw, text, tokens);
    }

    /// <summary>Tokenizer.ts <c>list</c>.</summary>
    public virtual Tokens.List? List(SourceView src)
    {
        var cap = Exec(src, Rules.Block.List);
        if (cap is null) return null;
        var bull = Js.Trim(Group(cap, 1)!);
        var isOrdered = bull.Length > 1;
        var list = new Tokens.List(string.Empty, isOrdered, isOrdered ? int.Parse(bull[..^1], System.Globalization.CultureInfo.InvariantCulture) : string.Empty, false, []);
        bull = isOrdered ? @"\d{1,9}\" + bull[^1] : @"\" + bull;

        var itemRegex = Rules.Other.ListItemRegex(bull);
        var endsWithBlankLine = false;
        // marked grows raw, itemContents and list.raw with +=, which V8's rope strings make cheap. Builders keep the
        // same text without copying the growing string for every line.
        var listRaw = new StringBuilder();
        // Check whether the current bullet can start a new list item.
        while (!src.IsEmpty)
        {
            var endEarly = false;
            if ((cap = Exec(src, itemRegex)) is null) break;
            // End the list if the bullet was actually a thematic break.
            if (Exec(src, Rules.Block.Hr) is not null) break;

            var raw = new StringBuilder(cap.Value);
            src = src.SliceClamped(cap.Value.Length);

            var line = MarkedHelpers.ExpandTabs(Group(cap, 2)!.Split('\n', 2)[0], Group(cap, 1)!.Length);
            var nextLine = Js.FirstLine(src);
            var blankLine = Js.Trim(line).Length == 0;

            var indent = 0;
            var itemContents = new StringBuilder();
            if (blankLine)
            {
                indent = Group(cap, 1)!.Length + 1;
            }
            else
            {
                indent = Js.Search(Rules.Other.NonSpaceChar, line);
                // Indented code (more than four spaces) counts as one space of indentation.
                indent = indent > 4 ? 1 : indent;
                itemContents.Append(Js.Slice(line, indent));
                indent += Group(cap, 1)!.Length;
            }

            // An item begins with at most one blank line.
            if (blankLine && Rules.Other.BlankLine.IsMatch(nextLine))
            {
                raw.Append(nextLine).Append('\n');
                src = src.SliceClamped(nextLine.Length + 1);
                endEarly = true;
            }

            if (!endEarly)
            {
                var nextBulletRegex = Rules.Other.NextBulletRegex(indent);
                var hrRegex = Rules.Other.HrRegex(indent);
                var fencesBeginRegex = Rules.Other.FencesBeginRegex(indent);
                var headingBeginRegex = Rules.Other.HeadingBeginRegex(indent);
                var htmlBeginRegex = Rules.Other.HtmlBeginRegex(indent);
                var blockquoteBeginRegex = Rules.Other.BlockquoteBeginRegex(indent);

                // Check whether the following lines belong to this item.
                while (!src.IsEmpty)
                {
                    var rawLine = Js.FirstLine(src);
                    nextLine = rawLine;
                    var nextLineWithoutTabs = Js.ReplaceAll(Rules.Other.TabCharGlobal, nextLine, "    ");

                    if (fencesBeginRegex.IsMatch(nextLine)) break;
                    if (headingBeginRegex.IsMatch(nextLine)) break;
                    if (htmlBeginRegex.IsMatch(nextLine)) break;
                    if (blockquoteBeginRegex.IsMatch(nextLine)) break;
                    if (nextBulletRegex.IsMatch(nextLine)) break;
                    if (hrRegex.IsMatch(nextLine)) break;

                    if (Js.Search(Rules.Other.NonSpaceChar, nextLineWithoutTabs) >= indent || Js.Trim(nextLine).Length == 0)
                    {
                        // Dedent if possible.
                        itemContents.Append('\n').Append(Js.Slice(nextLineWithoutTabs, indent));
                    }
                    else
                    {
                        // Not enough indentation.
                        if (blankLine) break;
                        // Paragraph continuation, unless the last line was another block element.
                        if (Js.Search(Rules.Other.NonSpaceChar, Js.ReplaceAll(Rules.Other.TabCharGlobal, line, "    ")) >= 4) break;
                        if (fencesBeginRegex.IsMatch(line)) break;
                        if (headingBeginRegex.IsMatch(line)) break;
                        if (hrRegex.IsMatch(line)) break;
                        itemContents.Append('\n').Append(nextLine);
                    }

                    blankLine = Js.Trim(nextLine).Length == 0;
                    raw.Append(rawLine).Append('\n');
                    src = src.SliceClamped(rawLine.Length + 1);
                    line = Js.Slice(nextLineWithoutTabs, indent);
                }
            }

            if (!list.Loose)
            {
                // The list is loose if the previous item ended with a blank line.
                if (endsWithBlankLine) list.Loose = true;
                else if (Rules.Other.DoubleBlankLine.IsMatch(raw.ToString())) endsWithBlankLine = true;
            }

            var itemRaw = raw.ToString();
            var contents = itemContents.ToString();
            list.Items.Add(new Tokens.ListItem(itemRaw, Options.Gfm && Rules.Other.ListIsTask.IsMatch(contents), false, contents, []));
            listRaw.Append(itemRaw);
        }

        // Do not consume the newlines at the end of the final item.
        if (list.Items.Count == 0) return null;
        var lastItem = list.Items[^1];
        lastItem.Raw = Js.TrimEnd(lastItem.Raw);
        lastItem.Text = Js.TrimEnd(lastItem.Text);
        list.Raw = Js.TrimEnd(listRaw.ToString());

        // Item child tokens are handled last, because the final item had to be trimmed first.
        foreach (var item in list.Items)
        {
            Lexer.State.Top = false;
            item.Children = Lexer.BlockTokens(item.Text, []);
            var itemToken = item.Children.Count > 0 ? item.Children[0] : null;
            if (item.Task && itemToken is not null && itemToken.Type is "text" or "paragraph")
            {
                // Remove the checkbox markdown from the item tokens.
                item.Text = Js.ReplaceFirst(Rules.Other.ListReplaceTask, item.Text, string.Empty);
                itemToken.Raw = Js.ReplaceFirst(Rules.Other.ListReplaceTask, itemToken.Raw, string.Empty);
                MarkedTokenAccess.SetText(itemToken, Js.ReplaceFirst(Rules.Other.ListReplaceTask, MarkedTokenAccess.GetText(itemToken), string.Empty));
                for (var i = Lexer.InlineQueue.Count - 1; i >= 0; i--)
                {
                    if (Rules.Other.ListIsTask.IsMatch(Lexer.InlineQueue[i].Source))
                    {
                        Lexer.InlineQueue[i].Source = Js.ReplaceFirst(Rules.Other.ListReplaceTask, Lexer.InlineQueue[i].Source, string.Empty);
                        break;
                    }
                }

                var taskRaw = Rules.Other.ListTaskCheckbox.Match(item.Raw);
                if (taskRaw.Success)
                {
                    var checkbox = new Tokens.Checkbox(taskRaw.Value + " ", taskRaw.Value != "[ ]");
                    item.Checked = checkbox.Checked;
                    if (list.Loose)
                    {
                        var first = item.Children.Count > 0 ? item.Children[0] : null;
                        if (first is not null && first.Type is "paragraph" or "text" && MarkedTokenAccess.GetChildren(first) is { } firstTokens)
                        {
                            first.Raw = checkbox.Raw + first.Raw;
                            MarkedTokenAccess.SetText(first, checkbox.Raw + MarkedTokenAccess.GetText(first));
                            firstTokens.Insert(0, checkbox);
                        }
                        else
                        {
                            item.Children.Insert(0, new Tokens.Paragraph(checkbox.Raw, checkbox.Raw, [checkbox]));
                        }
                    }
                    else
                    {
                        item.Children.Insert(0, checkbox);
                    }
                }
            }
            else if (item.Task)
            {
                item.Task = false;
            }

            if (!list.Loose)
            {
                // Check whether the list should be loose.
                var spacers = item.Children.Where(token => token.Type == "space").ToList();
                list.Loose = spacers.Count > 0 && spacers.Any(token => Rules.Other.AnyLine.IsMatch(token.Raw));
            }
        }

        // Set every item loose if the list is loose.
        if (list.Loose)
        {
            foreach (var item in list.Items)
            {
                item.Loose = true;
                for (var i = 0; i < item.Children.Count; i++)
                {
                    if (item.Children[i].Type == "text") item.Children[i] = MarkedTokenAccess.ToParagraph(item.Children[i]);
                }
            }
        }
        return list;
    }

    /// <summary>Tokenizer.ts <c>html</c>: an HTML block.</summary>
    public virtual Tokens.Html? Html(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Html);
        if (cap is null) return null;
        var raw = MarkedHelpers.TrimTrailingBlankLines(cap.Value);
        return new Tokens.Html(raw, Group(cap, 1) is "pre" or "script" or "style", true, raw);
    }

    /// <summary>Tokenizer.ts <c>def</c>: a link reference definition.</summary>
    public virtual Tokens.Def? Def(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Def);
        if (cap is null) return null;
        var tag = Js.ReplaceAll(Rules.Other.MultipleSpaceGlobal, Js.ToLowerCase(Group(cap, 1)!), " ");
        var hrefSource = Group(cap, 2);
        var href = Js.Truthy(hrefSource)
            ? Js.ReplaceAll(Rules.Inline.AnyPunctuation, Js.ReplaceFirst(Rules.Other.HrefBrackets, hrefSource!, "$1"), "$1")
            : string.Empty;
        var titleSource = Group(cap, 3);
        var title = Js.Truthy(titleSource)
            ? Js.ReplaceAll(Rules.Inline.AnyPunctuation, Js.Substring(titleSource!, 1, titleSource!.Length - 1), "$1")
            : titleSource;
        return new Tokens.Def(MarkedHelpers.RTrim(cap.Value, '\n'), tag, href, title, title is not null);
    }

    /// <summary>Tokenizer.ts <c>table</c>.</summary>
    public virtual Tokens.Table? Table(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Table);
        if (cap is null) return null;
        // The delimiter row must have a pipe or a colon; otherwise it is a setext heading.
        if (!Rules.Other.TableDelimiter.IsMatch(Group(cap, 2)!)) return null;

        var headers = MarkedHelpers.SplitCells(Group(cap, 1)!);
        var aligns = Js.ReplaceAll(Rules.Other.TableAlignChars, Group(cap, 2)!, string.Empty).Split('|');
        var rowsSource = Group(cap, 3);
        var rows = rowsSource is not null && Js.Trim(rowsSource).Length > 0
            ? Js.ReplaceFirst(Rules.Other.TableRowBlankLine, rowsSource, string.Empty).Split('\n')
            : [];
        var item = new Tokens.Table(MarkedHelpers.RTrim(cap.Value, '\n'), [], [], []);

        // Header and alignment columns must be equal; rows can differ.
        if (headers.Count != aligns.Length) return null;

        foreach (var align in aligns)
        {
            if (Rules.Other.TableAlignRight.IsMatch(align)) item.Align.Add("right");
            else if (Rules.Other.TableAlignCenter.IsMatch(align)) item.Align.Add("center");
            else if (Rules.Other.TableAlignLeft.IsMatch(align)) item.Align.Add("left");
            else item.Align.Add(null);
        }
        for (var i = 0; i < headers.Count; i++)
        {
            item.Header.Add(new Tokens.TableCell(headers[i], Lexer.Inline(headers[i]), true, item.Align[i]));
        }
        foreach (var row in rows)
        {
            var cells = MarkedHelpers.SplitCells(row, item.Header.Count);
            var rowCells = new List<Tokens.TableCell>(cells.Count);
            for (var i = 0; i < cells.Count; i++) rowCells.Add(new Tokens.TableCell(cells[i], Lexer.Inline(cells[i]), false, item.Align[i]));
            item.Rows.Add(rowCells);
        }
        return item;
    }

    /// <summary>Tokenizer.ts <c>lheading</c>: a setext heading.</summary>
    public virtual Tokens.Heading? Lheading(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Lheading);
        if (cap is null) return null;
        var text = Js.Trim(Group(cap, 1)!);
        return new Tokens.Heading(MarkedHelpers.RTrim(cap.Value, '\n'), Group(cap, 2)![0] == '=' ? 1 : 2, text, Lexer.Inline(text));
    }

    /// <summary>Tokenizer.ts <c>paragraph</c>.</summary>
    public virtual Tokens.Paragraph? Paragraph(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Paragraph);
        if (cap is null) return null;
        var content = Group(cap, 1)!;
        var text = content.Length > 0 && content[^1] == '\n' ? Js.Slice(content, 0, -1) : content;
        return new Tokens.Paragraph(cap.Value, text, Lexer.Inline(text));
    }

    /// <summary>Tokenizer.ts <c>text</c>: block text, outside the top level.</summary>
    public virtual Tokens.Text? Text(SourceView src)
    {
        var cap = Exec(src, Rules.Block.Text);
        return cap is null ? null : new Tokens.Text(cap.Value, cap.Value) { Children = Lexer.Inline(cap.Value) };
    }

    /// <summary>Tokenizer.ts <c>escape</c>.</summary>
    public virtual Tokens.Escape? Escape(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Escape);
        return cap is null ? null : new Tokens.Escape(cap.Value, Group(cap, 1)!);
    }

    /// <summary>Tokenizer.ts <c>tag</c>: inline HTML.</summary>
    public virtual Tokens.Html? Tag(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Tag);
        if (cap is null) return null;
        var state = Lexer.State;
        if (!state.InLink && Rules.Other.StartATag.IsMatch(cap.Value)) state.InLink = true;
        else if (state.InLink && Rules.Other.EndATag.IsMatch(cap.Value)) state.InLink = false;
        if (!state.InRawBlock && Rules.Other.StartPreScriptTag.IsMatch(cap.Value)) state.InRawBlock = true;
        else if (state.InRawBlock && Rules.Other.EndPreScriptTag.IsMatch(cap.Value)) state.InRawBlock = false;
        return new Tokens.Html(cap.Value, false, false, cap.Value) { InLink = state.InLink, InRawBlock = state.InRawBlock };
    }

    /// <summary>Tokenizer.ts <c>link</c>: an inline link or image.</summary>
    public virtual Token? Link(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Link);
        if (cap is null) return null;
        var cap0 = cap.Value;
        var cap1 = Group(cap, 1)!;
        var cap2 = Group(cap, 2)!;
        var cap3 = Group(cap, 3);
        var trimmedUrl = Js.Trim(cap2);
        if (Rules.Other.StartAngleBracket.IsMatch(trimmedUrl))
        {
            // CommonMark requires matching angle brackets.
            if (!Rules.Other.EndAngleBracket.IsMatch(trimmedUrl)) return null;
            // The ending angle bracket cannot be escaped.
            var rtrimSlash = MarkedHelpers.RTrim(Js.Slice(trimmedUrl, 0, -1), '\\');
            if ((trimmedUrl.Length - rtrimSlash.Length) % 2 == 0) return null;
        }
        else
        {
            // Find the closing parenthesis.
            var lastParenIndex = MarkedHelpers.FindClosingBracket(cap2, "()");
            if (lastParenIndex == -2) return null;
            if (lastParenIndex > -1)
            {
                var start = cap0.StartsWith('!') ? 5 : 4;
                var linkLength = start + cap1.Length + lastParenIndex;
                cap2 = Js.Substring(cap2, 0, lastParenIndex);
                cap0 = Js.Trim(Js.Substring(cap0, 0, linkLength));
                cap3 = string.Empty;
            }
        }
        var href = cap2;
        var title = Js.Truthy(cap3) ? Js.Slice(cap3!, 1, -1) : string.Empty;
        href = Js.Trim(href);
        if (Rules.Other.StartAngleBracket.IsMatch(href)) href = Js.Slice(href, 1, -1);
        return OutputLink(
            cap0,
            cap1,
            Js.Truthy(href) ? Js.ReplaceAll(Rules.Inline.AnyPunctuation, href, "$1") : href,
            Js.Truthy(title) ? Js.ReplaceAll(Rules.Inline.AnyPunctuation, title, "$1") : title);
    }

    /// <summary>Tokenizer.ts <c>reflink</c>: a reference, collapsed or shortcut link.</summary>
    public virtual Token? Reflink(SourceView src, Dictionary<string, LinkReference> links)
    {
        var cap = Exec(src, Rules.Inline.Reflink) ?? Exec(src, Rules.Inline.Nolink);
        if (cap is null) return null;
        var reference = Group(cap, 2);
        var linkString = Js.ReplaceAll(Rules.Other.MultipleSpaceGlobal, Js.Truthy(reference) ? reference! : Group(cap, 1)!, " ");
        if (!links.TryGetValue(Js.ToLowerCase(linkString), out var link))
        {
            var text = cap.Value[..1];
            return new Tokens.Text(text, text);
        }
        return OutputLink(cap.Value, Group(cap, 1)!, link.Href, link.Title);
    }

    /// <summary>Tokenizer.ts <c>emStrong</c>.</summary>
    public virtual Token? EmStrong(SourceView src, SourceView maskedSrc, string prevChar = "")
    {
        var match = Exec(src, Rules.Inline.EmStrongLDelim);
        if (match is null) return null;
        if (!Js.Truthy(Group(match, 1)) && !Js.Truthy(Group(match, 2)) && !Js.Truthy(Group(match, 3)) && !Js.Truthy(Group(match, 4))) return null;

        // _ cannot sit between two alphanumerics; \p{L}\p{N} includes non-English letters and numbers.
        if (Js.Truthy(Group(match, 4)) && Rules.Other.UnicodeAlphaNumeric.IsMatch(prevChar)) return null;

        var nextChar = Js.Truthy(Group(match, 1)) ? Group(match, 1)! : Group(match, 3) ?? string.Empty;
        if (Js.Truthy(nextChar) && Js.Truthy(prevChar) && !Rules.Inline.Punctuation.IsMatch(prevChar)) return null;

        // The unicode regex counts an emoji as one character, so count code points.
        var lLength = Js.CodePointCount(match.Value) - 1;
        var delimTotal = lLength;
        var midDelimTotal = 0;
        var delimiter = match.Value[0];
        var (endReg, rightDelim, leftDelim) = delimiter == '*'
            ? (Rules.Inline.EmStrongRDelimAst, Rules.Inline.EmStrongRDelimAstGroups12, Rules.Inline.EmStrongRDelimAstGroups34)
            : (Rules.Inline.EmStrongRDelimUnd, Rules.Inline.EmStrongRDelimUndGroups12, Rules.Inline.EmStrongRDelimUndGroups34);

        // Clip maskedSrc to the same section of the string as src, counting from its end as marked does.
        var clipped = maskedSrc.SliceJs(lLength - src.Length).AsSpan();
        foreach (var scan in endReg.EnumerateMatches(clipped))
        {
            // rDelim = match[1] || ... || match[6], read without captures: each group is a delimiter run after one
            // character, so a match has one exactly when the delimiter follows its first code point.
            var lastCharLength = Js.FirstCodePointLength(clipped.Slice(scan.Index, scan.Length));
            if (scan.Length <= lastCharLength || clipped[scan.Index + lastCharLength] != delimiter) continue; // Skip a single * in __abc*abc__.
            var rLength = Js.CodePointCount(clipped.Slice(scan.Index + lastCharLength, scan.Length - lastCharLength));

            if (!rightDelim.IsMatch(clipped, scan.Index))
            {
                if (leftDelim.IsMatch(clipped, scan.Index))
                {
                    // Found another left delimiter: match[3] or match[4].
                    delimTotal += rLength;
                    continue;
                }

                // Either a left or a right delimiter, match[5] or match[6]: CommonMark emphasis rules 9 and 10.
                if (lLength % 3 != 0 && (lLength + rLength) % 3 == 0)
                {
                    midDelimTotal += rLength;
                    continue;
                }
            }

            delimTotal -= rLength;
            if (delimTotal > 0) continue; // Not enough closing delimiters yet.

            // Remove extra characters: *a*** becomes *a*.
            rLength = Math.Min(rLength, rLength + delimTotal + midDelimTotal);
            // The first character of the match can be a surrogate pair.
            var raw = src.SliceJs(0, lLength + scan.Index + lastCharLength + rLength).Materialize();

            // An odd smallest delimiter makes em: *a***.
            if (Math.Min(lLength, rLength) % 2 != 0)
            {
                var emText = Js.Slice(raw, 1, -1);
                return new Tokens.Em(raw, emText, Lexer.InlineTokens(emText));
            }
            // An even smallest delimiter makes strong: **a***.
            var strongText = Js.Slice(raw, 2, -2);
            return new Tokens.Strong(raw, strongText, Lexer.InlineTokens(strongText));
        }
        return null;
    }

    /// <summary>Tokenizer.ts <c>codespan</c>.</summary>
    public virtual Tokens.Codespan? Codespan(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Code);
        if (cap is null) return null;
        var text = Js.ReplaceAll(Rules.Other.NewLineCharGlobal, Group(cap, 2)!, " ");
        var hasNonSpaceChars = Rules.Other.NonSpaceChar.IsMatch(text);
        var hasSpaceCharsOnBothEnds = Rules.Other.StartingSpaceChar.IsMatch(text) && Rules.Other.EndingSpaceChar.IsMatch(text);
        if (hasNonSpaceChars && hasSpaceCharsOnBothEnds) text = Js.Substring(text, 1, text.Length - 1);
        return new Tokens.Codespan(cap.Value, text);
    }

    /// <summary>Tokenizer.ts <c>br</c>.</summary>
    public virtual Tokens.Br? Br(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Br);
        return cap is null ? null : new Tokens.Br(cap.Value);
    }

    /// <summary>Tokenizer.ts <c>del</c>: GFM strikethrough.</summary>
    public virtual Tokens.Del? Del(SourceView src, SourceView maskedSrc, string prevChar = "")
    {
        var match = Exec(src, Rules.Inline.DelLDelim);
        if (match is null) return null;

        var nextChar = Group(match, 1) ?? string.Empty;
        if (Js.Truthy(nextChar) && Js.Truthy(prevChar) && !Rules.Inline.Punctuation.IsMatch(prevChar)) return null;

        // The unicode regex counts an emoji as one character, so count code points.
        var lLength = Js.CodePointCount(match.Value) - 1;
        var delimTotal = lLength;

        // Clip maskedSrc to the same section of the string as src.
        var clipped = maskedSrc.SliceJs(lLength - src.Length).AsSpan();
        foreach (var scan in Rules.Inline.DelRDelim.EnumerateMatches(clipped))
        {
            // rDelim = match[1] || ... || match[6], read without captures as in emStrong.
            var lastCharLength = Js.FirstCodePointLength(clipped.Slice(scan.Index, scan.Length));
            if (scan.Length <= lastCharLength || clipped[scan.Index + lastCharLength] != '~') continue;
            var rLength = Js.CodePointCount(clipped.Slice(scan.Index + lastCharLength, scan.Length - lastCharLength));
            if (rLength != lLength) continue;

            if (!Rules.Inline.DelRDelimGroups12.IsMatch(clipped, scan.Index) && Rules.Inline.DelRDelimGroups34.IsMatch(clipped, scan.Index))
            {
                // Found another left delimiter: match[3] or match[4].
                delimTotal += rLength;
                continue;
            }

            delimTotal -= rLength;
            if (delimTotal > 0) continue; // Not enough closing delimiters yet.

            // Remove extra characters.
            rLength = Math.Min(rLength, rLength + delimTotal);
            var raw = src.SliceJs(0, lLength + scan.Index + lastCharLength + rLength).Materialize();

            // Only single ~ or double ~~ make a del token.
            var text = Js.Slice(raw, lLength, -lLength);
            return new Tokens.Del(raw, text, Lexer.InlineTokens(text));
        }
        return null;
    }

    /// <summary>Tokenizer.ts <c>autolink</c>: <c>&lt;scheme:...&gt;</c> and <c>&lt;email&gt;</c>.</summary>
    public virtual Tokens.Link? Autolink(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Autolink);
        if (cap is null) return null;
        var text = Group(cap, 1)!;
        var href = Group(cap, 2) == "@" ? "mailto:" + text : text;
        return new Tokens.Link(cap.Value, href, null, false, text, [new Tokens.Text(text, text)]);
    }

    /// <summary>Tokenizer.ts <c>url</c>: GFM bare URLs and email addresses.</summary>
    public virtual Tokens.Link? Url(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Url);
        if (cap is null) return null;
        var raw = cap.Value;
        string text;
        string href;
        if (Group(cap, 2) == "@")
        {
            text = raw;
            href = "mailto:" + text;
        }
        else
        {
            // Extended autolink path validation.
            string previous;
            do
            {
                previous = raw;
                var backpedal = Rules.Inline.Backpedal.Match(raw);
                raw = backpedal.Success ? backpedal.Value : string.Empty;
            }
            while (previous != raw);
            text = raw;
            href = Group(cap, 1) == "www." ? "http://" + raw : raw;
        }
        return new Tokens.Link(raw, href, null, false, text, [new Tokens.Text(text, text)]);
    }

    /// <summary>Tokenizer.ts <c>inlineText</c>.</summary>
    public virtual Tokens.Text? InlineText(SourceView src)
    {
        var cap = Exec(src, Rules.Inline.Text);
        return cap is null ? null : new Tokens.Text(cap.Value, cap.Value, Lexer.State.InRawBlock);
    }

    // Tokenizer.ts outputLink: the token for a resolved inline or reference link.
    private Token OutputLink(string raw, string label, string href, string? linkTitle)
    {
        var title = Js.Truthy(linkTitle) ? linkTitle : null;
        var text = Js.ReplaceAll(Rules.Other.OutputLinkReplace, label, "$1");
        Lexer.State.InLink = true;
        var children = Lexer.InlineTokens(text);
        Lexer.State.InLink = false;
        return raw.Length > 0 && raw[0] == '!'
            ? new Tokens.Image(raw, href, title, text, children)
            : new Tokens.Link(raw, href, title, true, text, children);
    }

    private static Match? Exec(SourceView source, Regex regex) => source.TryMatch(regex, out var match) ? match : null;

    // cap[n]: null where JavaScript has undefined.
    private static string? Group(Match match, int number) => match.Groups[number].Success ? match.Groups[number].Value : null;
}

internal sealed record TokenizerContext(Lexer Lexer, MarkedOptions Options);
