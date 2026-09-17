// pi-tui's parser configuration from components/markdown.ts (lines 7-144 and 171-175): marked with a strict
// strikethrough tokenizer and the LaTeX tokenizer extensions. Its regexes are generated from markdown.ts by
// tools/marked-oracle/generate-regexes.mjs, like marked's own.
using TokenList = System.Collections.Generic.IList<Pi.Tui.Token>;

namespace Pi.Tui;

internal static class MarkdownParser
{
    // Declared before Parser: static initialisers run in textual order.
    private static readonly TokenizerExtension[] _latexExtensions =
    [
        new TokenizerExtension { Name = "latexBlock", Level = "block", Start = LatexBlockStart, Tokenizer = TokenizeBlockLatex },
        new TokenizerExtension { Name = "latex", Level = "inline", Start = LatexInlineStart, Tokenizer = TokenizeInlineLatex },
    ];

    /// <summary>markdown.ts <c>markdownParser</c>.</summary>
    internal static Marked Parser { get; } = CreateParser();

    internal static Marked CreateParserForTests(Tokenizer tokenizer) => CreateParser(tokenizer);

    private static Marked CreateParser(Tokenizer? tokenizer = null)
    {
        var parser = new Marked();
        parser.SetOptions(new MarkedOptions { Tokenizer = tokenizer ?? new StrictStrikethroughTokenizer() });
        parser.Use(_latexExtensions);
        return parser;
    }

    /// <summary>markdown.ts <c>StrictStrikethroughTokenizer</c>: <c>~single~</c> is not strikethrough.</summary>
    internal class StrictStrikethroughTokenizer : Tokenizer
    {
        public override Tokens.Del? Del(SourceView src, SourceView maskedSrc, string prevChar = "")
        {
            if (!src.TryMatch(MarkedRegexes.PiStrictStrikethrough(), out var match)) return null;
            var text = match.Groups[2].Value;
            return new Tokens.Del(match.Value, text, Lexer.InlineTokens(text));
        }
    }

    // markdown.ts isEscaped.
    private static bool IsEscaped(SourceView source, int index)
    {
        var backslashes = 0;
        for (var position = index - 1; position >= 0 && source[position] == '\\'; position--) backslashes++;
        return backslashes % 2 == 1;
    }

    // markdown.ts findClosingDelimiter.
    private static int FindClosingDelimiter(SourceView source, string closing, int start)
    {
        var index = source.IndexOf(closing, Math.Min(start, source.Length));
        while (index >= 0 && IsEscaped(source, index)) index = source.IndexOf(closing, Math.Min(index + closing.Length, source.Length));
        return index;
    }

    // markdown.ts looksLikePendingDollarMath.
    private static bool LooksLikePendingDollarMath(SourceView source) => source.MatchIn(MarkedRegexes.PiPendingDollarMath()).Success;

    // markdown.ts tokenizeInlineLatex.
    private static Tokens.Generic? TokenizeInlineLatex(Lexer lexer, SourceView source, TokenList tokens)
    {
        string opening;
        string closing;
        if (source.StartsWith("$$"))
        {
            opening = "$$";
            closing = "$$";
        }
        else if (source.StartsWith("\\("))
        {
            opening = "\\(";
            closing = "\\)";
        }
        else if (source.StartsWith("\\["))
        {
            opening = "\\[";
            closing = "\\]";
        }
        else if (source.StartsWith("$") && !source.TryMatch(MarkedRegexes.PiDollarThenWhitespace(), out _))
        {
            opening = "$";
            closing = "$";
        }
        else
        {
            return null;
        }

        var closingIndex = FindClosingDelimiter(source, closing, opening.Length);
        if (closingIndex >= 0 && opening == "$")
        {
            var body = source.SliceJs(opening.Length, closingIndex);
            var after = source.SliceJs(closingIndex + 1);
            var bodyText = body.Materialize();
            if (MarkedRegexes.PiEndsWithWhitespace().IsMatch(bodyText)
                || after.TryMatch(MarkedRegexes.PiStartsWithDigit(), out _)
                || (MarkedRegexes.PiUpperIdentifier().IsMatch(bodyText) && after.TryMatch(MarkedRegexes.PiIdentifierStart(), out _))
                || bodyText.Contains('`'))
            {
                return null;
            }
        }

        if (closingIndex < 0)
        {
            var pendingSource = source.SliceJs(opening.Length);
            if (opening.StartsWith('\\') || LooksLikePendingDollarMath(pendingSource))
            {
                return Latex("latex", source.Materialize(), pendingSource.Materialize(), pending: true);
            }
            return null;
        }

        var text = source.SliceJs(opening.Length, closingIndex).Materialize();
        if (text.Length == 0 || text.Contains('\n')) return null;
        return Latex("latex", source.SliceJs(0, closingIndex + closing.Length).Materialize(), text);
    }

    // markdown.ts tokenizeBlockLatex.
    private static Tokens.Generic? TokenizeBlockLatex(Lexer lexer, SourceView source, TokenList tokens)
    {
        if (source.TryMatch(MarkedRegexes.PiDollarBlock(), out var dollar) && dollar.Groups[1].Length > 0)
        {
            return Latex("latexBlock", dollar.Value, Js.Trim(dollar.Groups[1].Value));
        }
        if (source.TryMatch(MarkedRegexes.PiBracketBlock(), out var bracket) && bracket.Groups[1].Length > 0)
        {
            return Latex("latexBlock", bracket.Value, Js.Trim(bracket.Groups[1].Value));
        }
        if (source.TryMatch(MarkedRegexes.PiPendingBracketBlock(), out var pendingBracket))
        {
            return Latex("latexBlock", pendingBracket.Value, pendingBracket.Groups[1].Value, pending: true);
        }
        if (source.TryMatch(MarkedRegexes.PiPendingDollarBlock(), out var pendingDollar)
            && pendingDollar.Groups[1].Length > 0
            && MarkedRegexes.PiPendingDollarMath().IsMatch(pendingDollar.Groups[1].Value))
        {
            return Latex("latexBlock", pendingDollar.Value, pendingDollar.Groups[1].Value, pending: true);
        }
        return null;
    }

    // The latexBlock extension's start.
    private static int? LatexBlockStart(Lexer lexer, SourceView source)
    {
        var match = source.MatchIn(MarkedRegexes.PiLatexBlockStart());
        if (!match.Success) return null;
        return match.Index - source.Offset + (match.Value.StartsWith('\n') ? 1 : 0);
    }

    // The latex extension's start: the first of $, \( and \[.
    private static int? LatexInlineStart(Lexer lexer, SourceView source)
    {
        var start = int.MaxValue;
        foreach (var opener in new[] { "$", "\\(", "\\[" })
        {
            var index = source.IndexOf(opener);
            if (index >= 0) start = Math.Min(start, index);
        }
        return start == int.MaxValue ? null : start;
    }

    private static Tokens.Generic Latex(string type, string raw, string text, bool pending = false)
    {
        var token = new Tokens.Generic(type, raw);
        token.Properties["text"] = text;
        if (pending) token.Properties["pending"] = true;
        return token;
    }
}
