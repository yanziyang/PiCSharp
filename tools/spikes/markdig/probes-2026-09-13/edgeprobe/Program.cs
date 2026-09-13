using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var nl = ((char)10).ToString();
var bs = ((char)92).ToString();
var bt = ((char)96).ToString();

(string Id, string Source)[] inputs =
[
    ("al-www", "see www.example.com now"),
    ("al-ftp", "ftp://example.com/file"),
    ("al-mailto-scheme", "write mailto:a@b.com today"),
    ("al-tel", "call tel:+15551234"),
    ("al-email", "mail user@example.com please"),
    ("al-trailing-dot", "go to https://example.com."),
    ("al-parens", "(https://example.com)"),
    ("al-balanced-parens", "https://en.wikipedia.org/wiki/Foo_(bar)"),
    ("tb-no-delim", "| a | b |" + nl + "| 1 | 2 |"),
    ("tb-no-outer-pipes", "a | b" + nl + "--- | ---" + nl + "1 | 2"),
    ("tb-escaped-pipe", "| a |" + nl + "|---|" + nl + "| x " + bs + "| y |"),
    ("tb-code-pipe", "| a |" + nl + "|---|" + nl + "| " + bt + "x|y" + bt + " |"),
    ("tb-align", "| l | c | r |" + nl + "|:--|:-:|--:|" + nl + "| 1 | 2 | 3 |"),
    ("st-padded", "~~ padded ~~"),
    ("st-tilde-after", "~~a~~~"),
    ("st-triple", "~~~triple~~~"),
    ("st-space-before-close", "~~a ~~"),
    ("st-letter-after", "~~a~~b"),
    ("st-intraword", "a~~b~~c"),
    ("st-escaped", "~~a" + bs + "~~~"),
];

var pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
    .UseAutoLinks()
    .UseTaskLists()
    .UsePreciseSourceLocation()
    .Build();

foreach (var (id, source) in inputs)
{
    Console.WriteLine(id.PadRight(22) + Describe(Markdig.Markdown.Parse(source, pipeline)));
}

static string Describe(MarkdownDocument doc)
{
    var parts = new List<string>();
    foreach (var node in doc.Descendants())
    {
        parts.Add(node switch
        {
            Table t => "TABLE(cols=" + t.ColumnDefinitions.Count + ",align=" + string.Join("/", t.ColumnDefinitions.Select(c => c.Alignment?.ToString() ?? "-")) + ")",
            TableRow => "ROW",
            TableCell => "CELL",
            LinkInline l => "LINK(" + l.Url + (l.IsAutoLink ? ",auto" : string.Empty) + ")",
            AutolinkInline a => "AUTOLINK(" + a.Url + (a.IsEmail ? ",email" : string.Empty) + ")",
            EmphasisInline e => "EMPH(" + e.DelimiterChar + "x" + e.DelimiterCount + ")",
            CodeInline c => "CODE'" + c.Content + "'",
            LiteralInline lit => "'" + lit.Content.ToString() + "'",
            ParagraphBlock => "P",
            _ => node.GetType().Name,
        });
    }

    return string.Join(" ", parts);
}
