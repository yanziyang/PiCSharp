// Ported from marked 18.0.5; see LICENSE in this directory.
#pragma warning disable CS1591
namespace Pi.Tui;

public delegate Token? TokenizerExtensionFunction(Lexer lexer, SourceView source, global::System.Collections.Generic.IList<Token> tokens);
public delegate int? TokenizerStartFunction(Lexer lexer, SourceView source);

public sealed class TokenizerExtension
{
    public required string Name { get; init; }
    public required string Level { get; init; }
    public TokenizerStartFunction? Start { get; init; }
    public required TokenizerExtensionFunction Tokenizer { get; init; }
}

public sealed class MarkedOptions
{
    public bool Async { get; set; }
    public bool Breaks { get; set; }
    public bool Gfm { get; set; } = true;
    public bool Pedantic { get; set; }
    public bool Silent { get; set; }
    public Tokenizer? Tokenizer { get; set; }
    public global::System.Collections.Generic.IList<TokenizerExtension>? Extensions { get; set; }
}
