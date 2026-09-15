// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Marked's block and inline lexer, returning the token stream consumed by pi-tui.</summary>
public sealed class Lexer
{
    private readonly Tokenizer _tokenizer;
    private readonly MarkedRuleSet _rules = new();
    private readonly List<TokenizerExtension> _blockExtensions = [];
    private readonly List<TokenizerExtension> _inlineExtensions = [];
    private readonly List<TokenizerStartFunction> _blockStarts = [];
    private readonly List<TokenizerStartFunction> _inlineStarts = [];
    private readonly List<(string Source, global::System.Collections.Generic.List<Token> Tokens)> _inlineQueue = [];

    /// <summary>Token list for this invocation, including the off-array link map.</summary>
    public TokensList Tokens { get; } = new();

    /// <summary>Mutable marked lexer state.</summary>
    public LexerState State { get; } = new();

    /// <summary>Options used by this invocation.</summary>
    public MarkedOptions Options { get; }

    /// <summary>Creates a lexer. The tokenizer may be shared safely across threads.</summary>
    public Lexer(MarkedOptions? options = null)
    {
        Options = Validate(options ?? new MarkedOptions());
        _tokenizer = Options.Tokenizer ?? new Tokenizer(Options);
        _tokenizer.Bind(this, Options, _rules);
        ConfigureExtensions();
    }

    /// <summary>Lexes a complete Markdown document with default options.</summary>
    public static TokensList Lex(string source, MarkedOptions? options = null) => new Lexer(options).Lex(source);

    /// <summary>Lexes a complete Markdown document.</summary>
    public TokensList Lex(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        BlockTokens(new SourceView(normalized), Tokens);
        for (var i = 0; i < _inlineQueue.Count; i++) InlineTokens(_inlineQueue[i].Source, _inlineQueue[i].Tokens);
        _inlineQueue.Clear();
        return Tokens;
    }

    /// <summary>Creates an inline token queue entry and returns its mutable token list.</summary>
    public global::System.Collections.Generic.List<Token> Inline(string source, global::System.Collections.Generic.List<Token>? tokens = null)
    {
        var result = tokens ?? [];
        _inlineQueue.Add((source, result));
        return result;
    }

    /// <summary>Lexes block tokens from a string.</summary>
    public global::System.Collections.Generic.List<Token> BlockTokens(string source, global::System.Collections.Generic.List<Token>? tokens = null, bool lastParagraphClipped = false)
        => BlockTokens(new SourceView(source), tokens ?? [], lastParagraphClipped);

    /// <summary>Lexes block tokens from a no-copy source view.</summary>
    public global::System.Collections.Generic.List<Token> BlockTokens(SourceView source, global::System.Collections.Generic.List<Token>? tokens = null, bool lastParagraphClipped = false)
    {
        _tokenizer.Bind(this, Options, _rules);
        if (Options.Pedantic)
        {
            source = new SourceView(RulesReplacePedantic(source.Materialize()));
        }
        var sourceLength = int.MaxValue;
        while (!source.IsEmpty)
        {
            if (source.Length >= sourceLength) { InfiniteLoopError(source[0]); break; }
            sourceLength = source.Length;
            Token? token = null;
            if (TryExtension(_blockExtensions, source, tokens!, out var extensionToken) && extensionToken is not null) { source = source.Slice(extensionToken.Raw.Length); continue; }
            if ((token = _tokenizer.Space(source)) is not null)
            {
                source = source.Slice(token.Raw.Length);
                if (token.Raw.Length == 1 && tokens!.Count > 0) tokens[^1].Raw += "\n";
                else tokens!.Add(token);
                continue;
            }
            if ((token = _tokenizer.Code(source)) is not null)
            {
                source = source.Slice(token.Raw.Length);
                if (tokens!.LastOrDefault() is Token previous && (previous.Type is "paragraph" or "text") && token is Tokens.Code code)
                {
                    previous.Raw += (previous.Raw.EndsWith('\n') ? string.Empty : "\n") + code.Raw;
                    if (previous is Tokens.Paragraph paragraph) paragraph.Text += "\n" + code.Text;
                    else if (previous is Tokens.Text text) text.TextValue += "\n" + code.Text;
                    UpdateLastInlineSource(previous);
                }
                else tokens!.Add(token);
                continue;
            }
            if ((token = _tokenizer.Fences(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Heading(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Hr(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Blockquote(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.List(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Html(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Def(source)) is not null && token is Tokens.Def definition)
            {
                source = source.Slice(token.Raw.Length);
                if (tokens!.LastOrDefault() is Token previous && (previous.Type is "paragraph" or "text"))
                {
                    previous.Raw += (previous.Raw.EndsWith('\n') ? string.Empty : "\n") + definition.Raw;
                    if (previous is Tokens.Paragraph paragraph) paragraph.Text += "\n" + definition.Raw;
                    else if (previous is Tokens.Text text) text.TextValue += "\n" + definition.Raw;
                    UpdateLastInlineSource(previous);
                }
                else if (!Tokens.Links.ContainsKey(definition.Tag))
                {
                    Tokens.Links[definition.Tag] = new LinkReference(definition.Href, definition.Title, definition.HasTitle);
                    tokens!.Add(definition);
                }
                continue;
            }
            if ((token = _tokenizer.Table(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Lheading(source)) is not null) { source = source.Slice(token.Raw.Length); tokens!.Add(token); continue; }

            var cutSource = source;
            if (_blockStarts.Count > 0)
            {
                var startIndex = int.MaxValue;
                var temp = source.Slice(Math.Min(1, source.Length));
                foreach (var start in _blockStarts)
                {
                    var value = start(this, temp);
                    if (value is >= 0) startIndex = Math.Min(startIndex, value.Value);
                }
                if (startIndex < int.MaxValue) cutSource = source.Slice(0, Math.Min(source.Length, startIndex + 1));
            }
            if (State.Top && (token = _tokenizer.Paragraph(cutSource)) is not null)
            {
                if (lastParagraphClipped && tokens!.LastOrDefault() is Tokens.Paragraph previous)
                {
                    previous.Raw += (previous.Raw.EndsWith('\n') ? string.Empty : "\n") + token.Raw;
                    if (token is Tokens.Paragraph paragraph) previous.Text += "\n" + paragraph.Text;
                    _inlineQueue.RemoveAt(Math.Max(0, _inlineQueue.Count - 1));
                    UpdateLastInlineSource(previous);
                }
                else tokens!.Add(token);
                lastParagraphClipped = cutSource.Length != source.Length;
                source = source.Slice(token.Raw.Length);
                continue;
            }
            if ((token = _tokenizer.Text(source)) is not null)
            {
                source = source.Slice(token.Raw.Length);
                if (tokens!.LastOrDefault() is Tokens.Text previous)
                {
                    previous.Raw += (previous.Raw.EndsWith('\n') ? string.Empty : "\n") + token.Raw;
                    if (token is Tokens.Text text) previous.TextValue += "\n" + text.TextValue;
                    _inlineQueue.RemoveAt(Math.Max(0, _inlineQueue.Count - 1));
                    UpdateLastInlineSource(previous);
                }
                else tokens!.Add(token);
                continue;
            }
            InfiniteLoopError(source[0]);
            break;
        }
        State.Top = true;
        return tokens!;
    }

    /// <summary>Lexes inline tokens from a string.</summary>
    public global::System.Collections.Generic.List<Token> InlineTokens(string source, global::System.Collections.Generic.List<Token>? tokens = null)
        => InlineTokens(new SourceView(source), tokens ?? []);

    /// <summary>Lexes inline tokens from a no-copy source view.</summary>
    public global::System.Collections.Generic.List<Token> InlineTokens(SourceView source, global::System.Collections.Generic.List<Token>? tokens = null)
    {
        _tokenizer.Bind(this, Options, _rules);
        var masked = Mask(source.Materialize());
        var remaining = source;
        var maskedRemaining = new SourceView(masked);
        var sourceLength = int.MaxValue;
        var keepPrevious = false;
        var previousChar = string.Empty;
        while (!remaining.IsEmpty)
        {
            if (remaining.Length >= sourceLength) { InfiniteLoopError(remaining[0]); break; }
            sourceLength = remaining.Length;
            if (!keepPrevious) previousChar = string.Empty;
            keepPrevious = false;
            Token? token = null;
            if (TryExtension(_inlineExtensions, remaining, tokens!, out var extensionToken) && extensionToken is not null) { Consume(ref remaining, ref maskedRemaining, extensionToken.Raw.Length); continue; }
            if ((token = _tokenizer.Escape(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Tag(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Link(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Reflink(remaining, Tokens.Links)) is not null)
            {
                Consume(ref remaining, ref maskedRemaining, token.Raw.Length);
                if (token is Tokens.Text text && tokens!.LastOrDefault() is Tokens.Text previous) { previous.Raw += text.Raw; previous.TextValue += text.TextValue; }
                else tokens!.Add(token);
                continue;
            }
            if ((token = _tokenizer.EmStrong(remaining, maskedRemaining, previousChar)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Codespan(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Br(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Del(remaining, maskedRemaining, previousChar)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if ((token = _tokenizer.Autolink(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }
            if (!State.InLink && (token = _tokenizer.Url(remaining)) is not null) { Consume(ref remaining, ref maskedRemaining, token.Raw.Length); tokens!.Add(token); continue; }

            var cutSource = remaining;
            if (_inlineStarts.Count > 0)
            {
                var startIndex = int.MaxValue;
                var temp = remaining.Slice(Math.Min(1, remaining.Length));
                foreach (var start in _inlineStarts)
                {
                    var value = start(this, temp);
                    if (value is >= 0) startIndex = Math.Min(startIndex, value.Value);
                }
                if (startIndex < int.MaxValue) cutSource = remaining.Slice(0, Math.Min(remaining.Length, startIndex + 1));
            }
            if ((token = _tokenizer.InlineText(cutSource)) is not null)
            {
                Consume(ref remaining, ref maskedRemaining, token.Raw.Length);
                if (!token.Raw.EndsWith('_')) previousChar = token.Raw[^1].ToString();
                keepPrevious = true;
                if (token is Tokens.Text text && tokens!.LastOrDefault() is Tokens.Text previous) { previous.Raw += text.Raw; previous.TextValue += text.TextValue; }
                else tokens!.Add(token);
                continue;
            }
            InfiniteLoopError(remaining[0]);
            break;
        }
        return tokens!;
    }

    private void ConfigureExtensions()
    {
        if (Options.Extensions is null) return;
        foreach (var extension in Options.Extensions)
        {
            if (string.IsNullOrEmpty(extension.Name)) throw new InvalidOperationException("extension name required");
            if (extension.Level is not ("block" or "inline")) throw new InvalidOperationException("extension level must be 'block' or 'inline'");
            if (extension.Level == "block") { _blockExtensions.Insert(0, extension); if (extension.Start is not null) _blockStarts.Add(extension.Start); }
            else { _inlineExtensions.Insert(0, extension); if (extension.Start is not null) _inlineStarts.Add(extension.Start); }
        }
    }

    private bool TryExtension(List<TokenizerExtension> extensions, SourceView source, global::System.Collections.Generic.List<Token> tokens, out Token? token)
    {
        foreach (var extension in extensions)
        {
            token = extension.Tokenizer(this, source, tokens);
            if (token is not null) { tokens.Add(token); return true; }
        }
        token = null;
        return false;
    }

    private string Mask(string source)
    {
        var masked = source.ToCharArray();
        var links = Tokens.Links.Keys.ToHashSet(StringComparer.Ordinal);
        if (links.Count > 0)
        {
            foreach (Match match in _rules.Inline.RefLinkSearch.Matches(source))
            {
                var labelStart = match.Value.LastIndexOf('[') + 1;
                if (labelStart > 0 && links.Contains(match.Value[labelStart..^1])) WriteBracketMask(masked, match.Index, match.Length, 0);
            }
        }

        var escapedSource = new string(masked);
        foreach (Match match in _rules.Inline.AnyPunctuation.Matches(escapedSource)) WriteFixedMask(masked, match.Index, match.Length, '+');

        var blockSource = new string(masked);
        foreach (Match match in _rules.Inline.BlockSkip.Matches(blockSource))
        {
            var offset = match.Groups["b"].Success ? match.Groups["b"].Length : 0;
            WriteBracketMask(masked, match.Index, match.Length, offset);
        }
        return new string(masked);
    }

    private static void WriteFixedMask(char[] destination, int index, int length, char character)
    {
        for (var i = index; i < index + length; i++) destination[i] = character;
    }

    private static void WriteBracketMask(char[] destination, int index, int length, int offset)
    {
        var innerLength = length - offset;
        if (innerLength <= 0) return;
        var start = index + offset;
        destination[start] = '[';
        for (var i = 1; i < innerLength - 1; i++) destination[start + i] = 'a';
        if (innerLength > 1) destination[start + innerLength - 1] = ']';
    }

    private static void Consume(ref SourceView source, ref SourceView masked, int length)
    {
        source = source.Slice(length);
        masked = masked.Slice(Math.Min(length, masked.Length));
    }

    private void UpdateLastInlineSource(Token token)
    {
        if (_inlineQueue.Count == 0) return;
        var source = token switch { Tokens.Paragraph paragraph => paragraph.Text, Tokens.Text text => text.TextValue, _ => string.Empty };
        _inlineQueue[^1] = (source, _inlineQueue[^1].Tokens);
    }

    internal void RemoveTaskMarkerFromInlineQueue()
    {
        for (var i = _inlineQueue.Count - 1; i >= 0; i--)
        {
            if (!_rules.Other.ListIsTask.IsMatch(_inlineQueue[i].Source)) continue;
            _inlineQueue[i] = (_rules.Other.ListReplaceTask.Replace(_inlineQueue[i].Source, string.Empty, 1), _inlineQueue[i].Tokens);
            return;
        }
    }

    private void InfiniteLoopError(char value)
    {
        var message = "Infinite loop on byte: " + (int)value;
        if (Options.Silent) Console.Error.WriteLine(message); else throw new InvalidOperationException(message);
    }

    private string RulesReplacePedantic(string value) => _rules.Other.TabCharGlobal.Replace(value, "    ").Replace(_rules.Other.SpaceLine.ToString(), string.Empty, StringComparison.Ordinal);

    private static MarkedOptions Validate(MarkedOptions options)
    {
        if (!options.Gfm) throw new NotSupportedException("Unsupported option: gfm");
        if (options.Pedantic) throw new NotSupportedException("Unsupported option: pedantic");
        if (options.Breaks) throw new NotSupportedException("Unsupported option: breaks");
        if (options.Async) throw new NotSupportedException("Unsupported option: async");
        if (options.Silent) throw new NotSupportedException("Unsupported option: silent");
        return options;
    }
}

/// <summary>Mutable state associated with one lexer invocation.</summary>
public sealed class LexerState
{
    /// <summary>Whether inline links may be opened.</summary>
    public bool InLink { get; set; }
    /// <summary>Whether inline text is inside a raw HTML block.</summary>
    public bool InRawBlock { get; set; }
    /// <summary>Whether the lexer is at top-level block context.</summary>
    public bool Top { get; set; } = true;
}
