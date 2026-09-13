using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var nl = ((char)10).ToString();
var bs = ((char)92).ToString();

(string Id, string Source)[] inputs =
[
    ("tb-escaped-pipe", "| a |" + nl + "|---|" + nl + "| x " + bs + "| y |"),
    ("tb-align", "| l | c | r |" + nl + "|:--|:-:|--:|" + nl + "| 1 | 2 | 3 |"),
    ("tb-overflow", "| a |" + nl + "|---|" + nl + "| x | y |"),
    ("tb-underflow", "| a | b |" + nl + "|---|---|" + nl + "| x |"),
    ("tb-header-only", "| a | b |" + nl + "|---|---|"),
    ("al-angle-email", "mail <user@example.com> now"),
];

foreach (var useHeader in new[] { false, true })
{
    var pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables(new PipeTableOptions { UseHeaderForColumnCount = useHeader })
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UsePreciseSourceLocation()
        .Build();
    Console.WriteLine("=== UseHeaderForColumnCount = " + useHeader + " ===");
    foreach (var (id, source) in inputs)
    {
        Console.WriteLine("  " + id.PadRight(18) + Describe(Markdig.Markdown.Parse(source, pipeline)));
    }
}

static string Describe(MarkdownDocument doc)
{
    var parts = new List<string>();
    foreach (var block in doc)
    {
        if (block is Table table)
        {
            var aligns = string.Join("/", table.ColumnDefinitions.Select(c => c.Alignment?.ToString() ?? "-"));
            var rows = new List<string>();
            foreach (var rowObj in table)
            {
                var row = (TableRow)rowObj;
                var cells = new List<string>();
                foreach (var cellObj in row)
                {
                    var cell = (TableCell)cellObj;
                    var text = string.Concat(cell.Descendants().Select(d => d switch
                    {
                        LiteralInline l => l.Content.ToString(),
                        CodeInline c => "`" + c.Content + "`",
                        _ => string.Empty,
                    }));
                    cells.Add(text);
                }

                rows.Add((row.IsHeader ? "H" : "R") + "[" + string.Join("|", cells) + "]");
            }

            parts.Add("TABLE(defs=" + table.ColumnDefinitions.Count + ",align=" + aligns + ") " + string.Join(" ", rows));
        }
        else
        {
            parts.Add(block.GetType().Name + ": " + string.Join(" ", block.Descendants().Select(d => d switch
            {
                AutolinkInline a => "AUTOLINK(" + a.Url + (a.IsEmail ? ",email" : string.Empty) + ")",
                LinkInline k => "LINK(" + k.Url + ")",
                LiteralInline l => "'" + l.Content.ToString() + "'",
                _ => d.GetType().Name,
            })));
        }
    }

    return string.Join(" ; ", parts);
}
