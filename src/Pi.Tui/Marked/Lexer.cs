// Ported from marked 18.0.5 (src/Lexer.ts); see LICENSE in this directory.
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using MarkedTokens = Pi.Tui.Tokens;

namespace Pi.Tui;

/// <summary>marked's block and inline lexer, returning the token stream pi-tui renders.</summary>
public sealed class Lexer
{
    // marked recurses through blockTokens and inlineTokens once per nesting level, until V8 throws a RangeError. On
    // Node 24, marked lexes 2,000 to 3,500 levels of blockquotes, lists or emphasis, depending on the construct and on
    // JIT state (measured 2026-09-16). A .NET thread holds only a few hundred levels, so a call that finds the stack
    // nearly full continues on a new thread with a larger stack, and a fixed limit above V8 stands in for its stack.
    internal const int MaxNestingDepth = 5_000;
    private const int _nestingStackSize = 64 * 1024 * 1024;

    private int _depth;
    private readonly Tokenizer _tokenizer;
    private readonly TokenizerExtensionFunction[] _blockExtensions;
    private readonly TokenizerExtensionFunction[] _inlineExtensions;
    private readonly TokenizerStartFunction[] _startBlock;
    private readonly TokenizerStartFunction[] _startInline;

    /// <summary><c>this.tokens</c>: the token list, including the off-array link map.</summary>
    public TokensList Tokens { get; } = new();

    /// <summary><c>this.state</c>.</summary>
    public LexerState State { get; } = new();

    /// <summary><c>this.options</c>.</summary>
    public MarkedOptions Options { get; }

    /// <summary><c>this.inlineQueue</c>: inline source queued by block tokenizers until the block pass ends.</summary>
    public List<InlineQueueEntry> InlineQueue { get; } = [];

    /// <summary>Creates a lexer. The tokenizer may be shared by lexers on other threads.</summary>
    public Lexer(MarkedOptions? options = null)
    {
        Options = Validate(options ?? new MarkedOptions());
        _tokenizer = Options.Tokenizer ?? new Tokenizer(Options);
        _tokenizer.Bind(this, Options);

        // Instance.ts use(): a tokenizer extension added later runs first (unshift), while start functions run in
        // the order their extensions were added (push).
        var blockExtensions = new List<TokenizerExtensionFunction>();
        var inlineExtensions = new List<TokenizerExtensionFunction>();
        var startBlock = new List<TokenizerStartFunction>();
        var startInline = new List<TokenizerStartFunction>();
        foreach (var extension in Options.Extensions ?? [])
        {
            TokenizerExtension.Validate(extension);
            if (extension.Level == "block")
            {
                blockExtensions.Insert(0, extension.Tokenizer);
                if (extension.Start is not null) startBlock.Add(extension.Start);
            }
            else
            {
                inlineExtensions.Insert(0, extension.Tokenizer);
                if (extension.Start is not null) startInline.Add(extension.Start);
            }
        }
        _blockExtensions = [.. blockExtensions];
        _inlineExtensions = [.. inlineExtensions];
        _startBlock = [.. startBlock];
        _startInline = [.. startInline];
    }

    /// <summary>Lexer.ts static <c>lex</c>.</summary>
    public static TokensList Lex(string source, MarkedOptions? options = null) => new Lexer(options).Lex(source);

    /// <summary>Lexer.ts static <c>lexInline</c>.</summary>
    public static List<Token> LexInline(string source, MarkedOptions? options = null) => new Lexer(options).InlineTokens(source);

    /// <summary>Lexer.ts <c>lex</c>: lexes a complete Markdown document.</summary>
    public TokensList Lex(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source = Js.ReplaceAll(MarkedRules.Instance.Other.CarriageReturn, source, "\n");
        BlockTokens(new SourceView(source), Tokens);
        for (var i = 0; i < InlineQueue.Count; i++) InlineTokens(InlineQueue[i].Source, InlineQueue[i].Tokens);
        InlineQueue.Clear();
        return Tokens;
    }

    /// <summary>Lexer.ts <c>blockTokens</c>.</summary>
    public List<Token> BlockTokens(string source, List<Token>? tokens = null, bool lastParagraphClipped = false)
        => BlockTokens(new SourceView(source), tokens ?? [], lastParagraphClipped);

    /// <summary>Lexer.ts <c>blockTokens</c> over a source view.</summary>
    /// <exception cref="InsufficientExecutionStackException">Blocks nest more than 5,000 levels deep.</exception>
    public List<Token> BlockTokens(SourceView src, List<Token> tokens, bool lastParagraphClipped = false)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return BlockTokensOnNewStack(src, tokens, lastParagraphClipped);
        EnterNesting();
        try
        {
            return BlockTokensCore(src, tokens, lastParagraphClipped);
        }
        finally
        {
            _depth--;
        }
    }

    private List<Token> BlockTokensOnNewStack(SourceView src, List<Token> tokens, bool lastParagraphClipped)
        => OnNewStack(() => BlockTokens(src, tokens, lastParagraphClipped));

    private List<Token> BlockTokensCore(SourceView src, List<Token> tokens, bool lastParagraphClipped)
    {
        _tokenizer.Bind(this, Options);
        var srcLength = int.MaxValue;
        while (!src.IsEmpty)
        {
            if (src.Length < srcLength)
            {
                srcLength = src.Length;
            }
            else
            {
                InfiniteLoopError(src[0]);
                break;
            }

            Token? token;
            if (TryExtensions(_blockExtensions, ref src, tokens)) continue;

            // newline
            if ((token = _tokenizer.Space(src)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                // A single \n spacer terminates the last line, so it moves there instead of adding a token.
                if (token.Raw.Length == 1 && tokens.Count > 0) tokens[^1].AppendRaw("\n");
                else tokens.Add(token);
                continue;
            }

            // code
            if ((token = _tokenizer.Code(src)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                var lastToken = LastOf(tokens);
                // Indented code cannot interrupt a paragraph.
                if (lastToken?.Type is "paragraph" or "text")
                {
                    lastToken.AppendRaw((lastToken.RawEndsWith('\n') ? string.Empty : "\n") + token.Raw);
                    MarkedTokenAccess.AppendText(lastToken, "\n" + MarkedTokenAccess.GetText(token));
                    MarkedTokenAccess.CopyTextTo(lastToken, InlineQueue[^1]);
                }
                else
                {
                    tokens.Add(token);
                }
                continue;
            }

            if ((token = _tokenizer.Fences(src)) is not null
                || (token = _tokenizer.Heading(src)) is not null
                || (token = _tokenizer.Hr(src)) is not null
                || (token = _tokenizer.Blockquote(src)) is not null
                || (token = _tokenizer.List(src)) is not null
                || (token = _tokenizer.Html(src)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                tokens.Add(token);
                continue;
            }

            // def
            if (_tokenizer.Def(src) is { } definition)
            {
                src = src.SliceClamped(definition.Raw.Length);
                var lastToken = LastOf(tokens);
                if (lastToken?.Type is "paragraph" or "text")
                {
                    lastToken.AppendRaw((lastToken.RawEndsWith('\n') ? string.Empty : "\n") + definition.Raw);
                    MarkedTokenAccess.AppendText(lastToken, "\n" + definition.Raw);
                    MarkedTokenAccess.CopyTextTo(lastToken, InlineQueue[^1]);
                }
                else if (!Tokens.Links.ContainsKey(definition.Tag))
                {
                    Tokens.Links[definition.Tag] = new LinkReference(definition.Href, definition.Title, definition.HasTitle);
                    tokens.Add(definition);
                }
                continue;
            }

            // table (gfm), lheading
            if ((token = _tokenizer.Table(src)) is not null || (token = _tokenizer.Lheading(src)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                tokens.Add(token);
                continue;
            }

            // Top-level paragraph. Clip src to the first extension start so a paragraph cannot consume an extension.
            var cutSrc = src;
            if (_startBlock.Length > 0)
            {
                var startIndex = int.MaxValue;
                var tempSrc = src.SliceJs(1);
                foreach (var getStartIndex in _startBlock)
                {
                    if (getStartIndex(this, tempSrc) is { } tempStart and >= 0) startIndex = Math.Min(startIndex, tempStart);
                }
                if (startIndex < int.MaxValue) cutSrc = src.SliceJs(0, startIndex + 1);
            }
            if (State.Top && (token = _tokenizer.Paragraph(cutSrc)) is not null)
            {
                var lastToken = LastOf(tokens);
                if (lastParagraphClipped && lastToken?.Type == "paragraph")
                {
                    lastToken.AppendRaw((lastToken.RawEndsWith('\n') ? string.Empty : "\n") + token.Raw);
                    MarkedTokenAccess.AppendText(lastToken, "\n" + MarkedTokenAccess.GetText(token));
                    PopInlineQueue();
                    MarkedTokenAccess.CopyTextTo(lastToken, InlineQueue[^1]);
                }
                else
                {
                    tokens.Add(token);
                }
                lastParagraphClipped = cutSrc.Length != src.Length;
                src = src.SliceClamped(token.Raw.Length);
                continue;
            }

            // text
            if ((token = _tokenizer.Text(src)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                var lastToken = LastOf(tokens);
                if (lastToken?.Type == "text")
                {
                    lastToken.AppendRaw((lastToken.RawEndsWith('\n') ? string.Empty : "\n") + token.Raw);
                    MarkedTokenAccess.AppendText(lastToken, "\n" + MarkedTokenAccess.GetText(token));
                    PopInlineQueue();
                    MarkedTokenAccess.CopyTextTo(lastToken, InlineQueue[^1]);
                }
                else
                {
                    tokens.Add(token);
                }
                continue;
            }

            InfiniteLoopError(src[0]);
            break;
        }

        State.Top = true;
        return tokens;
    }

    /// <summary>Lexer.ts <c>inline</c>: queues inline source for after the block pass.</summary>
    public List<Token> Inline(string source, List<Token>? tokens = null)
    {
        var list = tokens ?? [];
        InlineQueue.Add(new InlineQueueEntry(source, list));
        return list;
    }

    /// <summary>Lexer.ts <c>inlineTokens</c>.</summary>
    /// <exception cref="InsufficientExecutionStackException">Inline tokens nest more than 5,000 levels deep.</exception>
    public List<Token> InlineTokens(string source, List<Token>? tokens = null)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return InlineTokensOnNewStack(source, tokens);
        EnterNesting();
        try
        {
            return InlineTokensCore(source, tokens);
        }
        finally
        {
            _depth--;
        }
    }

    private List<Token> InlineTokensOnNewStack(string source, List<Token>? tokens)
        => OnNewStack(() => InlineTokens(source, tokens));

    private List<Token> InlineTokensCore(string source, List<Token>? tokens)
    {
        var list = tokens ?? [];
        _tokenizer.Bind(this, Options);
        var rules = MarkedRules.Instance;

        // A copy of the source with links, escapes, code spans and HTML masked, so they cannot interfere with em
        // and strong. marked rebuilds the string for every match, which V8's rope strings make cheap and .NET
        // strings make quadratic, so the edits happen in a buffer instead. Each search resumes where marked's
        // lastIndex does: at the previous match's end, in the text as already edited.
        var buffer = source.ToCharArray();
        var length = buffer.Length;
        if (Tokens.Links.Count > 0)
        {
            var start = 0;
            while (FirstMatch(rules.Inline.ReflinkSearch, buffer, length, start) is { } found)
            {
                var (index, matchLength) = found;
                var matched = new ReadOnlySpan<char>(buffer, index, matchLength);
                var labelStart = matched.LastIndexOf('[') + 1;
                var label = matched.Slice(labelStart, Math.Max(0, matchLength - 1 - labelStart)).ToString();
                if (Tokens.Links.ContainsKey(label)) MaskBrackets(buffer, index, matchLength);
                start = index + Math.Max(matchLength, 1);
            }
        }

        // Escaped punctuation becomes "++". An escaped astral symbol is three code units, so the rest of the text
        // moves left by one, exactly as it does in marked's rebuilt string.
        var escapeStart = 0;
        while (FirstMatch(rules.Inline.AnyPunctuation, buffer, length, escapeStart) is { } escape)
        {
            var (index, matchLength) = escape;
            buffer[index] = '+';
            buffer[index + 1] = '+';
            if (matchLength > 2)
            {
                Array.Copy(buffer, index + matchLength, buffer, index + 2, length - index - matchLength);
                length -= matchLength - 2;
            }
            escapeStart = index + matchLength;
        }

        // Links, code spans and HTML. marked's offset is match[2]'s length, and group 2 is the empty group before
        // a code span, so the whole match is always masked.
        var skipStart = 0;
        while (FirstMatch(rules.Inline.BlockSkip, buffer, length, skipStart) is { } skip)
        {
            var (index, matchLength) = skip;
            MaskBrackets(buffer, index, matchLength);
            skipStart = index + Math.Max(matchLength, 1);
        }

        var masked = new SourceView(new string(buffer, 0, length));
        var src = new SourceView(source);
        var keepPrevChar = false;
        var prevChar = string.Empty;
        var srcLength = int.MaxValue;
        while (!src.IsEmpty)
        {
            if (src.Length < srcLength)
            {
                srcLength = src.Length;
            }
            else
            {
                InfiniteLoopError(src[0]);
                break;
            }

            if (!keepPrevChar) prevChar = string.Empty;
            keepPrevChar = false;

            Token? token;
            if (TryExtensions(_inlineExtensions, ref src, list)) continue;

            if ((token = _tokenizer.Escape(src)) is not null
                || (token = _tokenizer.Tag(src)) is not null
                || (token = _tokenizer.Link(src)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                list.Add(token);
                continue;
            }

            // reflink, nolink
            if ((token = _tokenizer.Reflink(src, Tokens.Links)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                var lastToken = LastOf(list);
                if (token.Type == "text" && lastToken?.Type == "text")
                {
                    lastToken.AppendRaw(token.Raw);
                    MarkedTokenAccess.AppendText(lastToken, MarkedTokenAccess.GetText(token));
                }
                else
                {
                    list.Add(token);
                }
                continue;
            }

            if ((token = _tokenizer.EmStrong(src, masked, prevChar)) is not null
                || (token = _tokenizer.Codespan(src)) is not null
                || (token = _tokenizer.Br(src)) is not null
                || (token = _tokenizer.Del(src, masked, prevChar)) is not null
                || (token = _tokenizer.Autolink(src)) is not null
                || (!State.InLink && (token = _tokenizer.Url(src)) is not null))
            {
                src = src.SliceClamped(token.Raw.Length);
                list.Add(token);
                continue;
            }

            // Text. Clip src to the first extension start so inlineText cannot consume an extension.
            var cutSrc = src;
            if (_startInline.Length > 0)
            {
                var startIndex = int.MaxValue;
                var tempSrc = src.SliceJs(1);
                foreach (var getStartIndex in _startInline)
                {
                    if (getStartIndex(this, tempSrc) is { } tempStart and >= 0) startIndex = Math.Min(startIndex, tempStart);
                }
                if (startIndex < int.MaxValue) cutSrc = src.SliceJs(0, startIndex + 1);
            }
            if ((token = _tokenizer.InlineText(cutSrc)) is not null)
            {
                src = src.SliceClamped(token.Raw.Length);
                // Track prevChar before a run of _ starts.
                if (!token.Raw.EndsWith('_')) prevChar = Js.Slice(token.Raw, -1);
                keepPrevChar = true;
                var lastToken = LastOf(list);
                if (lastToken?.Type == "text")
                {
                    lastToken.AppendRaw(token.Raw);
                    MarkedTokenAccess.AppendText(lastToken, MarkedTokenAccess.GetText(token));
                }
                else
                {
                    list.Add(token);
                }
                continue;
            }

            InfiniteLoopError(src[0]);
            break;
        }
        return list;
    }

    private bool TryExtensions(TokenizerExtensionFunction[] extensions, ref SourceView src, List<Token> tokens)
    {
        foreach (var extension in extensions)
        {
            if (extension(this, src, tokens) is not { } token) continue;
            src = src.SliceClamped(token.Raw.Length);
            tokens.Add(token);
            return true;
        }
        return false;
    }

    private static Token? LastOf(List<Token> tokens) => tokens.Count > 0 ? tokens[^1] : null;

    private void EnterNesting()
    {
        if (_depth > MaxNestingDepth) throw new InsufficientExecutionStackException($"Markdown nests more than {MaxNestingDepth} levels deep.");
        _depth++;
    }

    // Runs a nested lexing call on a new thread with a larger stack and waits for it. The execution context flows to
    // the new thread, so the tokenizer stays bound to this lexer.
    private static List<Token> OnNewStack(Func<List<Token>> call)
    {
        List<Token>? result = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = call();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            },
            _nestingStackSize)
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result!;
    }

    // regex.exec with lastIndex = start over buffer[0..length]: the first match at or after start, where lookbehind
    // still sees the text before start, or null once start is past the end.
    private static (int Index, int Length)? FirstMatch(System.Text.RegularExpressions.Regex regex, char[] buffer, int length, int start)
    {
        if (start > length) return null;
        foreach (var match in regex.EnumerateMatches(new ReadOnlySpan<char>(buffer, 0, length), start)) return (match.Index, match.Length);
        return null;
    }

    // '[' + 'a'.repeat(length - 2) + ']' written over a match.
    private static void MaskBrackets(char[] buffer, int index, int length)
    {
        buffer[index] = '[';
        for (var i = index + 1; i < index + length - 1; i++) buffer[i] = 'a';
        buffer[index + length - 1] = ']';
    }

    // Array.prototype.pop() on an empty array returns undefined instead of throwing.
    private void PopInlineQueue()
    {
        if (InlineQueue.Count > 0) InlineQueue.RemoveAt(InlineQueue.Count - 1);
    }

    private static void InfiniteLoopError(char unit)
        => throw new InvalidOperationException("Infinite loop on byte: " + (int)unit);

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

/// <summary>Lexer.ts <c>state</c>.</summary>
public sealed class LexerState
{
    /// <summary>Whether the inline lexer is inside a link.</summary>
    public bool InLink { get; set; }
    /// <summary>Whether the inline lexer is inside raw HTML such as <c>pre</c> or <c>script</c>.</summary>
    public bool InRawBlock { get; set; }
    /// <summary>Whether the block lexer is at the top level.</summary>
    public bool Top { get; set; } = true;
}
