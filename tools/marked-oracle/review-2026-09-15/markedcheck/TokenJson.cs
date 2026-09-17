// Serialises the port's tokens into marked's JSON shape, so tests compare them with the recorded oracle.
using System.Text.Json.Nodes;

using Pi.Tui;

/// <summary>Serialises the mutable C# token model into marked's exact JSON shape.</summary>
internal static class TokenJson
{
    /// <summary>Creates a JSON node with the same keys marked emits for a token.</summary>
    public static JsonObject ToObject(Token token)
    {
        var result = new JsonObject { ["type"] = token.Type, ["raw"] = token.Raw };
        switch (token)
        {
            case Tokens.Blockquote value: result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Br: break;
            case Tokens.Checkbox value: result["checked"] = value.Checked; break;
            case Tokens.Code value:
                if (value.IsIndented) result["codeBlockStyle"] = "indented";
                if (value.Lang is not null) result["lang"] = value.Lang;
                result["text"] = value.Text;
                if (value.Escaped.HasValue) result["escaped"] = value.Escaped.Value;
                break;
            case Tokens.Codespan value: result["text"] = value.Text; break;
            case Tokens.Def value:
                result["tag"] = value.Tag; result["href"] = value.Href;
                if (value.HasTitle) result["title"] = value.Title;
                break;
            case Tokens.Del value: result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Em value: result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Escape value: result["text"] = value.Text; break;
            case Tokens.Generic value:
                foreach (var pair in value.Properties) result[pair.Key] = pair.Value?.DeepClone();
                if (value.Children is not null) result["tokens"] = ToArray(value.Children);
                break;
            case Tokens.Heading value: result["depth"] = value.Depth; result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Hr: break;
            case Tokens.Html value:
                if (value.Block) result["pre"] = value.Pre;
                if (value.InLink.HasValue) result["inLink"] = value.InLink.Value;
                if (value.InRawBlock.HasValue) result["inRawBlock"] = value.InRawBlock.Value;
                result["block"] = value.Block; result["text"] = value.Text;
                break;
            case Tokens.Image value:
                result["href"] = value.Href; result["title"] = value.Title; result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Link value:
                result["href"] = value.Href;
                if (value.HasTitle) result["title"] = value.Title;
                result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.List value:
                result["ordered"] = value.Ordered;
                result["start"] = value.Start is int start ? JsonValue.Create(start) : JsonValue.Create((string)value.Start);
                result["loose"] = value.Loose;
                result["items"] = ToArray(value.Items.Cast<Token>()); break;
            case Tokens.ListItem value:
                result["task"] = value.Task;
                if (value.Checked.HasValue) result["checked"] = value.Checked.Value;
                result["loose"] = value.Loose; result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Paragraph value: result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Space: break;
            case Tokens.Strong value: result["text"] = value.Text; result["tokens"] = ToArray(value.Children); break;
            case Tokens.Table value:
                result["align"] = ToScalars(value.Align); result["header"] = ToCells(value.Header); result["rows"] = new JsonArray(value.Rows.Select(ToCells).Cast<JsonNode>().ToArray()); break;
            case Tokens.Text value:
                result["text"] = value.TextValue;
                if (value.Children is not null) result["tokens"] = ToArray(value.Children);
                if (value.Escaped.HasValue) result["escaped"] = value.Escaped.Value;
                break;
            default: throw new InvalidOperationException($"Unknown token type '{token.GetType().Name}'.");
        }
        return result;
    }

    /// <summary>Creates a JSON array for a token sequence.</summary>
    public static JsonArray ToArray(IEnumerable<Token> tokens)
    {
        var result = new JsonArray();
        foreach (var token in tokens) result.Add((JsonNode)ToObject(token));
        return result;
    }

    private static JsonArray ToScalars(IEnumerable<string?> values)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add((JsonNode?)(value is null ? null : JsonValue.Create(value)));
        return result;
    }

    private static JsonArray ToCells(IEnumerable<Tokens.TableCell> cells)
    {
        var result = new JsonArray();
        foreach (var cell in cells)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["text"] = cell.Text,
                ["tokens"] = ToArray(cell.Children),
                ["header"] = cell.Header,
                ["align"] = cell.Align,
            });
        }
        return result;
    }

    /// <summary>Creates the off-array link object emitted by marked.</summary>
    public static JsonObject ToLinks(IEnumerable<KeyValuePair<string, LinkReference>> links)
    {
        var result = new JsonObject();
        foreach (var pair in links)
        {
            var value = new JsonObject { ["href"] = pair.Value.Href };
            if (pair.Value.HasTitle) value["title"] = pair.Value.Title;
            result[pair.Key] = value;
        }
        return result;
    }
}
