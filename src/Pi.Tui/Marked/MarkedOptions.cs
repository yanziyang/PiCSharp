// Ported from marked 18.0.5 (src/MarkedOptions.ts, and the tokenizer-extension checks of src/Instance.ts); see
// LICENSE in this directory.
#pragma warning disable CS1591
namespace Pi.Tui;

public delegate Token? TokenizerExtensionFunction(Lexer lexer, SourceView source, IList<Token> tokens);
public delegate int? TokenizerStartFunction(Lexer lexer, SourceView source);

public sealed class TokenizerExtension
{
    public required string Name { get; init; }
    public required string Level { get; init; }
    public TokenizerStartFunction? Start { get; init; }
    public required TokenizerExtensionFunction Tokenizer { get; init; }

    // Instance.ts use(): the checks and messages marked throws when an extension is registered.
    internal static void Validate(TokenizerExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (string.IsNullOrEmpty(extension.Name)) throw new InvalidOperationException("extension name required");
        if (extension.Level is not ("block" or "inline")) throw new InvalidOperationException("extension level must be 'block' or 'inline'");
    }
}

public sealed class MarkedOptions
{
    public bool Async { get; set; }
    public bool Breaks { get; set; }
    public bool Gfm { get; set; } = true;
    public bool Pedantic { get; set; }
    public bool Silent { get; set; }
    public Tokenizer? Tokenizer { get; set; }
    public IList<TokenizerExtension>? Extensions { get; set; }
}
