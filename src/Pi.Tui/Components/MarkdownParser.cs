#pragma warning disable CS1591
using System.Text.RegularExpressions;

namespace Pi.Tui;

internal static class MarkdownParser
{
    internal static Marked Parser { get; } = CreateParser();

    internal static Marked CreateParserForTests(Tokenizer tokenizer) => CreateParser(tokenizer);

    private static Marked CreateParser(Tokenizer? tokenizer = null)
    {
        var parser = new Marked();
        parser.SetOptions(new MarkedOptions { Tokenizer = tokenizer ?? new StrictStrikethroughTokenizer() });
        parser.Use(
            new TokenizerExtension
            {
                Name = "latexBlock",
                Level = "block",
                Start = LatexBlockStart,
                Tokenizer = TokenizeBlockLatex,
            },
            new TokenizerExtension
            {
                Name = "latex",
                Level = "inline",
                Start = LatexInlineStart,
                Tokenizer = TokenizeInlineLatex,
            });
        return parser;
    }

    internal class StrictStrikethroughTokenizer : Tokenizer
    {
        private static readonly Regex _strict = new(@"^(~~)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)", RegexOptions.CultureInvariant);

        public override Tokens.Del? Del(SourceView src, SourceView maskedSrc, string prevChar = "")
        {
            if (!src.TryMatch(_strict, out var match)) return null;
            var text = match.Groups[2].Value;
            return new Tokens.Del(match.Value, text, Lexer.InlineTokens(text));
        }
    }

    private static int? LatexBlockStart(Lexer lexer, SourceView source)
    {
        var match = Regex.Match(source.Materialize(), @"(?:^|\n) {0,3}(?:\$\$|\\\[)", RegexOptions.CultureInvariant);
        return match.Success ? match.Index + (match.Value.StartsWith('\n') ? 1 : 0) : null;
    }

    private static Tokens.Generic? TokenizeBlockLatex(Lexer lexer, SourceView source, global::System.Collections.Generic.IList<Token> tokens)
    {
        var text = source.Materialize();
        var dollar = Regex.Match(text, @"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*?)\$\$[ \t]*(?:\n|$)", RegexOptions.CultureInvariant);
        if (dollar.Success && dollar.Groups[1].Value.Length > 0) return Latex("latexBlock", dollar.Value, dollar.Groups[1].Value.Trim());
        var bracket = Regex.Match(text, @"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*?)\\\][ \t]*(?:\n|$)", RegexOptions.CultureInvariant);
        if (bracket.Success && bracket.Groups[1].Value.Length > 0) return Latex("latexBlock", bracket.Value, bracket.Groups[1].Value.Trim());
        var pendingBracket = Regex.Match(text, @"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*)$", RegexOptions.CultureInvariant);
        if (pendingBracket.Success) return Latex("latexBlock", pendingBracket.Value, pendingBracket.Groups[1].Value, true);
        var pendingDollar = Regex.Match(text, @"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*)$", RegexOptions.CultureInvariant);
        if (pendingDollar.Success && LooksLikePendingDollarMath(pendingDollar.Groups[1].Value)) return Latex("latexBlock", pendingDollar.Value, pendingDollar.Groups[1].Value, true);
        return null;
    }

    private static int? LatexInlineStart(Lexer lexer, SourceView source)
    {
        var text = source.Materialize();
        var values = new[] { text.IndexOf('$'), text.IndexOf("\\(", StringComparison.Ordinal), text.IndexOf("\\[", StringComparison.Ordinal) }.Where(value => value >= 0).ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static Tokens.Generic? TokenizeInlineLatex(Lexer lexer, SourceView source, global::System.Collections.Generic.IList<Token> tokens)
    {
        var text = source.Materialize();
        string opening;
        string closing;
        if (text.StartsWith("$$", StringComparison.Ordinal)) { opening = "$$"; closing = "$$"; }
        else if (text.StartsWith("\\(", StringComparison.Ordinal)) { opening = "\\("; closing = "\\)"; }
        else if (text.StartsWith("\\[", StringComparison.Ordinal)) { opening = "\\["; closing = "\\]"; }
        else if (text.StartsWith('$') && !Regex.IsMatch(text, @"^\$\s", RegexOptions.CultureInvariant)) { opening = "$"; closing = "$"; }
        else return null;
        var closingIndex = FindClosingDelimiter(text, closing, opening.Length);
        if (closingIndex >= 0 && opening == "$" && (Regex.IsMatch(text[opening.Length..closingIndex], @"\s$", RegexOptions.CultureInvariant) || Regex.IsMatch(text[(closingIndex + 1)..], @"^\d", RegexOptions.CultureInvariant) || Regex.IsMatch(text[opening.Length..closingIndex], @"^[A-Z_][A-Z0-9_]*(?:[^A-Za-z0-9_\s])?$", RegexOptions.CultureInvariant) && Regex.IsMatch(text[(closingIndex + 1)..], @"^[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant) || text[opening.Length..closingIndex].Contains('`')))
            return null;
        if (closingIndex < 0)
        {
            var pendingSource = text[opening.Length..];
            return opening.StartsWith('\\') || LooksLikePendingDollarMath(pendingSource) ? Latex("latex", text, pendingSource, true) : null;
        }
        var body = text[opening.Length..closingIndex];
        if (body.Length == 0 || body.Contains('\n')) return null;
        return Latex("latex", text[..(closingIndex + closing.Length)], body);
    }

    private static Tokens.Generic Latex(string type, string raw, string text, bool pending = false)
    {
        var token = new Tokens.Generic(type, raw);
        token.Properties["text"] = text;
        if (pending) token.Properties["pending"] = true;
        return token;
    }

    private static bool IsEscaped(string source, int index)
    {
        var count = 0;
        for (var i = index - 1; i >= 0 && source[i] == '\\'; i--) count++;
        return count % 2 == 1;
    }

    private static int FindClosingDelimiter(string source, string closing, int start)
    {
        var index = source.IndexOf(closing, start, StringComparison.Ordinal);
        while (index >= 0 && IsEscaped(source, index)) index = source.IndexOf(closing, index + closing.Length, StringComparison.Ordinal);
        return index;
    }

    private static bool LooksLikePendingDollarMath(string source) => Regex.IsMatch(source, @"\\[A-Za-z]+|[_^=+*/<>()[\]|±≤≥≠≈∈→⇒∞∫∑√-]", RegexOptions.CultureInvariant);

}
