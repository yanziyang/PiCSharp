// Ported from marked 18.0.5; see LICENSE in this directory.
#pragma warning disable CS1591
using System.Text.Json.Nodes;

namespace Pi.Tui;

public abstract class Token
{
    protected Token(string type, string raw) { Type = type; Raw = raw; }
    public string Type { get; }
    public string Raw { get; set; }
}

public static class Tokens
{
    public sealed class Blockquote : Token
    {
        public Blockquote(string raw, string text, global::System.Collections.Generic.List<Token> children) : base("blockquote", raw) { Text = text; Children = children; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Br : Token { public Br(string raw) : base("br", raw) { } }

    public sealed class Checkbox : Token
    {
        public Checkbox(string raw, bool checkedValue) : base("checkbox", raw) { Checked = checkedValue; }
        public bool Checked { get; set; }
    }

    public sealed class Code : Token
    {
        public Code(string raw, string text) : base("code", raw) { Text = text; }
        public string? Lang { get; set; }
        public bool IsIndented { get; set; }
        public string Text { get; set; }
        public bool? Escaped { get; set; }
    }

    public sealed class Codespan : Token
    {
        public Codespan(string raw, string text) : base("codespan", raw) { Text = text; }
        public string Text { get; set; }
    }

    public sealed class Def : Token
    {
        public Def(string raw, string tag, string href, string? title, bool hasTitle) : base("def", raw) { Tag = tag; Href = href; Title = title; HasTitle = hasTitle; }
        public string Tag { get; set; }
        public string Href { get; set; }
        public string? Title { get; set; }
        public bool HasTitle { get; set; }
    }

    public sealed class Del : Token
    {
        public Del(string raw, string text, global::System.Collections.Generic.List<Token> children) : base("del", raw) { Text = text; Children = children; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Em : Token
    {
        public Em(string raw, string text, global::System.Collections.Generic.List<Token> children) : base("em", raw) { Text = text; Children = children; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Escape : Token
    {
        public Escape(string raw, string text) : base("escape", raw) { Text = text; }
        public string Text { get; set; }
    }

    public class Generic : Token
    {
        public Generic(string type, string raw) : base(type, raw) { }
        public Dictionary<string, JsonNode?> Properties { get; } = new(StringComparer.Ordinal);
        public global::System.Collections.Generic.List<Token>? Children { get; set; }
    }

    public sealed class Heading : Token
    {
        public Heading(string raw, int depth, string text, global::System.Collections.Generic.List<Token> children) : base("heading", raw) { Depth = depth; Text = text; Children = children; }
        public int Depth { get; set; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Hr : Token { public Hr(string raw) : base("hr", raw) { } }

    public sealed class Html : Token
    {
        public Html(string raw, bool pre, bool block, string text) : base("html", raw) { Pre = pre; Block = block; Text = text; }
        public bool Pre { get; set; }
        public bool Block { get; set; }
        public bool? InLink { get; set; }
        public bool? InRawBlock { get; set; }
        public string Text { get; set; }
    }

    public sealed class Image : Token
    {
        public Image(string raw, string href, string? title, string text, global::System.Collections.Generic.List<Token> children) : base("image", raw) { Href = href; Title = title; Text = text; Children = children; }
        public string Href { get; set; }
        public string? Title { get; set; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Link : Token
    {
        public Link(string raw, string href, string? title, bool hasTitle, string text, global::System.Collections.Generic.List<Token> children) : base("link", raw) { Href = href; Title = title; HasTitle = hasTitle; Text = text; Children = children; }
        public string Href { get; set; }
        public string? Title { get; set; }
        public bool HasTitle { get; set; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class List : Token
    {
        public List(string raw, bool ordered, object start, bool loose, global::System.Collections.Generic.List<ListItem> items) : base("list", raw) { Ordered = ordered; Start = start; Loose = loose; Items = items; }
        public bool Ordered { get; set; }
        public object Start { get; set; }
        public bool Loose { get; set; }
        public global::System.Collections.Generic.List<ListItem> Items { get; set; }
    }

    public sealed class ListItem : Token
    {
        public ListItem(string raw, bool task, bool loose, string text, global::System.Collections.Generic.List<Token> children) : base("list_item", raw) { Task = task; Loose = loose; Text = text; Children = children; }
        public bool Task { get; set; }
        public bool? Checked { get; set; }
        public bool Loose { get; set; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Paragraph : Token
    {
        public Paragraph(string raw, string text, global::System.Collections.Generic.List<Token> children) : base("paragraph", raw) { Text = text; Children = children; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Space : Token { public Space(string raw) : base("space", raw) { } }

    public sealed class Strong : Token
    {
        public Strong(string raw, string text, global::System.Collections.Generic.List<Token> children) : base("strong", raw) { Text = text; Children = children; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
    }

    public sealed class Table : Token
    {
        public Table(string raw, global::System.Collections.Generic.List<string?> align, global::System.Collections.Generic.List<TableCell> header, global::System.Collections.Generic.List<global::System.Collections.Generic.List<TableCell>> rows) : base("table", raw) { Align = align; Header = header; Rows = rows; }
        public global::System.Collections.Generic.List<string?> Align { get; set; }
        public global::System.Collections.Generic.List<TableCell> Header { get; set; }
        public global::System.Collections.Generic.List<global::System.Collections.Generic.List<TableCell>> Rows { get; set; }
    }

    public sealed class TableCell
    {
        public TableCell(string text, global::System.Collections.Generic.List<Token> children, bool header, string? align) { Text = text; Children = children; Header = header; Align = align; }
        public string Text { get; set; }
        public global::System.Collections.Generic.List<Token> Children { get; set; }
        public bool Header { get; set; }
        public string? Align { get; set; }
    }

    public sealed class Text : Token
    {
        public Text(string raw, string text, bool? escaped = null) : base("text", raw) { TextValue = text; Escaped = escaped; }
        public string TextValue { get; set; }
        public global::System.Collections.Generic.List<Token>? Children { get; set; }
        public bool? Escaped { get; set; }
    }
}

public sealed class LinkReference
{
    public LinkReference(string href, string? title, bool hasTitle) { Href = href; Title = title; HasTitle = hasTitle; }
    public string Href { get; set; }
    public string? Title { get; set; }
    public bool HasTitle { get; set; }
}

public sealed class TokensList : global::System.Collections.Generic.List<Token>
{
    public Dictionary<string, LinkReference> Links { get; } = new(StringComparer.Ordinal);
}
