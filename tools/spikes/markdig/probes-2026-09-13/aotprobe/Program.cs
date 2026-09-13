using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var nl = ((char)10).ToString();
var bs = ((char)92).ToString();

var pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseEmphasisExtras()
    .UseAutoLinks()
    .UseTaskLists()
    .UseMathematics()
    .UsePreciseSourceLocation()
    .Use<ProbeExtension>()
    .Build();

string[] samples =
[
    "# H" + nl + nl + "- a" + nl + "  - b" + nl + nl + "| x | y |" + nl + "|---|---|" + nl + "| 1 | 2 |",
    "~~del~~ and ~single~ `code` [l](http://e.com) <http://e.com> " + bs + "*esc" + bs + "*",
    "> q1" + nl + "> q2" + nl + nl + "$x^2$ and $$y$$ and " + bs + "(z" + bs + ")",
    "- [ ] task" + nl + nl + "```ts" + nl + "const x = 1;" + nl + "``",
];

var total = 0;
var kinds = new HashSet<string>();
foreach (var s in samples)
{
    var doc = Markdig.Markdown.Parse(s, pipeline);
    foreach (var node in doc.Descendants())
    {
        total++;
        var detail = node switch
        {
            MathInline m => "math:" + m.Content.ToString(),
            MathBlock b => "mathblock:" + b.Lines.ToString(),
            EmphasisInline e => "emph:" + e.DelimiterChar + e.DelimiterCount,
            LinkInline l => "link:" + (l.Url ?? string.Empty),
            AutolinkInline a => "autolink:" + a.Url,
            LiteralInline lit => "lit:" + lit.Content.ToString(),
            _ => node.GetType().Name,
        };
        kinds.Add(detail.Split(':')[0]);
    }
}

Console.WriteLine("parsed " + samples.Length + " samples, " + total + " nodes, kinds: " + string.Join(",", kinds.Order()));

sealed class ProbeInlineParser : InlineParser
{
    public ProbeInlineParser() => OpeningCharacters = [(char)92];

    public override bool Match(InlineProcessor processor, ref StringSlice slice) => false;
}

sealed class ProbeBlockParser : BlockParser
{
    public ProbeBlockParser() => OpeningCharacters = [(char)92];

    public override BlockState TryOpen(BlockProcessor processor) => BlockState.None;
}

sealed class ProbeExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<ProbeInlineParser>())
        {
            pipeline.InlineParsers.InsertBefore<EscapeInlineParser>(new ProbeInlineParser());
        }

        if (!pipeline.BlockParsers.Contains<ProbeBlockParser>())
        {
            pipeline.BlockParsers.Insert(0, new ProbeBlockParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
