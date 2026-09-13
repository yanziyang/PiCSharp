using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

(string Id, string Source)[] inputs =
[
    ("st-lazy-pairing", "~~a ~~b~~"),
    ("st-two-pairs", "~~a~~ ~~b~~"),
    ("st-three-openers", "~~a ~~b ~~c~~"),
    ("st-tilde-after", "~~a~~~"),
    ("st-nested-strong-tilde-after", "~~**a**~~~"),
    ("st-nested-strong", "~~**a**~~"),
];

var pipeline = new MarkdownPipelineBuilder()
    .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
    .UsePreciseSourceLocation()
    .Build();

Console.WriteLine("=== Markdig 0.44.0, strikethrough-only extras (nesting shown) ===");
foreach (var (id, source) in inputs)
{
    var doc = Markdig.Markdown.Parse(source, pipeline);
    var rendered = string.Join(" / ", doc.Descendants<ParagraphBlock>().Select(p => Render(p.Inline)));
    Console.WriteLine("  " + id.PadRight(30) + rendered);
}

static string Render(Inline? inline) => inline switch
{
    null => string.Empty,
    EmphasisInline e => Tag(e) + "[" + Children(e) + "]",
    ContainerInline c => Children(c),
    LiteralInline l => l.Content.ToString(),
    _ => inline.GetType().Name,
};

static string Children(ContainerInline c)
{
    var s = string.Empty;
    foreach (var child in c)
    {
        s += Render(child);
    }

    return s;
}

static string Tag(EmphasisInline e) => e.DelimiterChar == '~'
    ? (e.DelimiterCount == 2 ? "DEL" : "SUB")
    : (e.DelimiterCount == 2 ? "STRONG" : "EM");
