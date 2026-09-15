// Ported from marked 18.0.5; see LICENSE in this directory.
#pragma warning disable CS1591
namespace Pi.Tui;

public sealed class Marked
{
    public MarkedOptions Defaults { get; private set; } = new();

    public Marked(params TokenizerExtension[] extensions)
    {
        if (extensions.Length > 0) Use(extensions);
    }

    public Marked SetOptions(MarkedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Defaults.Async = options.Async;
        Defaults.Breaks = options.Breaks;
        Defaults.Gfm = options.Gfm;
        Defaults.Pedantic = options.Pedantic;
        Defaults.Silent = options.Silent;
        if (options.Tokenizer is not null) Defaults.Tokenizer = options.Tokenizer;
        if (options.Extensions is not null) Use(options.Extensions.ToArray());
        return this;
    }

    public Marked Use(params TokenizerExtension[] extensions)
    {
        var combined = Defaults.Extensions is null ? [] : Defaults.Extensions.ToList();
        combined.AddRange(extensions);
        Defaults.Extensions = combined;
        return this;
    }

    public TokensList Lexer(string source, MarkedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new global::Pi.Tui.Lexer(Combine(options)).Lex(source);
    }

    private MarkedOptions Combine(MarkedOptions? options)
    {
        if (options is null) return Defaults;
        return new MarkedOptions
        {
            Async = options.Async,
            Breaks = options.Breaks,
            Gfm = options.Gfm,
            Pedantic = options.Pedantic,
            Silent = options.Silent,
            Tokenizer = options.Tokenizer ?? Defaults.Tokenizer,
            Extensions = options.Extensions ?? Defaults.Extensions,
        };
    }
}
