// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Tui;

/// <summary>An entry of <c>lexer.inlineQueue</c>: inline source waiting to be tokenised into <see cref="Tokens"/>.</summary>
public sealed class InlineQueueEntry
{
    private string _source;
    private StringBuilder? _pendingSource;
    private int _pendingLength;

    /// <summary>Creates a queue entry.</summary>
    public InlineQueueEntry(string source, List<Token> tokens)
    {
        _source = source;
        Tokens = tokens;
    }

    /// <summary>The inline source. Block tokenizers rewrite it when they merge or trim a token.</summary>
    public string Source
    {
        get
        {
            if (_pendingSource is not null)
            {
                _source = _pendingSource.ToString(0, _pendingLength);
                _pendingSource = null;
            }
            return _source;
        }
        set
        {
            _source = value;
            _pendingSource = null;
        }
    }

    /// <summary>The token list the inline tokens are appended to.</summary>
    public List<Token> Tokens { get; }

    // The first length characters of builder, materialised only when read.
    internal void SetSource(StringBuilder builder, int length)
    {
        _pendingSource = builder;
        _pendingLength = length;
    }
}

/// <summary>
/// marked reads and writes <c>token.text</c> and <c>token.tokens</c> on whatever token it holds, including extension
/// tokens. These helpers give the typed model the same reach.
/// </summary>
internal static class MarkedTokenAccess
{
    internal static string GetText(Token token) => token switch
    {
        Tokens.Paragraph paragraph => paragraph.Text,
        Tokens.Text text => text.TextValue,
        Tokens.Code code => code.Text,
        Tokens.Generic generic when generic.Properties.TryGetValue("text", out var value) && value is JsonValue json && json.TryGetValue<string>(out var text) => text,
        _ => string.Empty,
    };

    internal static void SetText(Token token, string value)
    {
        switch (token)
        {
            case Tokens.Paragraph paragraph: paragraph.Text = value; break;
            case Tokens.Text text: text.TextValue = value; break;
            case Tokens.Code code: code.Text = value; break;
            case Tokens.Generic generic: generic.Properties["text"] = value; break;
        }
    }

    /// <summary><c>token.text += suffix</c>, without copying the text for paragraph and text tokens.</summary>
    internal static void AppendText(Token token, string suffix)
    {
        switch (token)
        {
            case Tokens.Paragraph paragraph: paragraph.AppendText(suffix); break;
            case Tokens.Text text: text.AppendText(suffix); break;
            default: SetText(token, GetText(token) + suffix); break;
        }
    }

    /// <summary><c>entry.src = token.text</c>, without copying the text for paragraph and text tokens.</summary>
    internal static void CopyTextTo(Token token, InlineQueueEntry entry)
    {
        switch (token)
        {
            case Tokens.Paragraph paragraph: paragraph.CopyTextTo(entry); break;
            case Tokens.Text text: text.CopyTextTo(entry); break;
            default: entry.Source = GetText(token); break;
        }
    }

    internal static List<Token>? GetChildren(Token token) => token switch
    {
        Tokens.Paragraph paragraph => paragraph.Children,
        Tokens.Text text => text.Children,
        Tokens.Generic generic => generic.Children,
        _ => null,
    };

    /// <summary>The list tokenizer's <c>token.type = 'paragraph'</c>, keeping the child list itself.</summary>
    internal static Token ToParagraph(Token token)
    {
        switch (token)
        {
            case Tokens.Text text:
                return new Tokens.Paragraph(text.Raw, text.TextValue, text.Children ?? []);
            case Tokens.Generic generic:
                var paragraph = new Tokens.Generic("paragraph", generic.Raw) { Children = generic.Children };
                foreach (var pair in generic.Properties) paragraph.Properties[pair.Key] = pair.Value;
                return paragraph;
            default:
                return token;
        }
    }
}
