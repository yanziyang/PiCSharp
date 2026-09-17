// Ported from marked 18.0.5 (src/Instance.ts, the options, extension and lexer paths); see LICENSE in this
// directory.
#pragma warning disable CS1591
namespace Pi.Tui;

public sealed class Marked
{
    public MarkedOptions Defaults { get; private set; } = new();

    public Marked(params TokenizerExtension[] extensions)
    {
        if (extensions.Length > 0) Use(extensions);
    }

    /// <summary>Instance.ts <c>setOptions</c>: merges option values into the defaults.</summary>
    public Marked SetOptions(MarkedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Defaults.Async = options.Async;
        Defaults.Breaks = options.Breaks;
        Defaults.Gfm = options.Gfm;
        Defaults.Pedantic = options.Pedantic;
        Defaults.Silent = options.Silent;
        if (options.Tokenizer is not null) Defaults.Tokenizer = options.Tokenizer;
        if (options.Extensions is not null)
        {
            foreach (var extension in options.Extensions) TokenizerExtension.Validate(extension);
            Defaults.Extensions = options.Extensions.ToList();
        }
        return this;
    }

    /// <summary>Instance.ts <c>use</c> for tokenizer extensions. Invalid extensions throw here, as in marked.</summary>
    public Marked Use(params TokenizerExtension[] extensions)
    {
        foreach (var extension in extensions) TokenizerExtension.Validate(extension);
        var combined = Defaults.Extensions is null ? [] : Defaults.Extensions.ToList();
        combined.AddRange(extensions);
        Defaults.Extensions = combined;
        return this;
    }

    /// <summary>Instance.ts <c>lexer</c>: <paramref name="options"/>, when given, replace the defaults.</summary>
    public TokensList Lexer(string source, MarkedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return global::Pi.Tui.Lexer.Lex(source, options ?? Defaults);
    }
}
