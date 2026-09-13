using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

string[] inputs =
[
    "x^2 + y^2",
    "2^10^ bytes",
    "a==b==c and ==highlight==",
    "++i++ and ++j++",
    "H~2~O and ~/foo and ~/bar",
    "~~ok~~",
];

var defaultExtras = new MarkdownPipelineBuilder().UseEmphasisExtras().UsePreciseSourceLocation().Build();
var strikeOnly = new MarkdownPipelineBuilder().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UsePreciseSourceLocation().Build();

foreach (var input in inputs)
{
    Console.WriteLine("INPUT: " + input);
    Console.WriteLine("  default extras : " + Describe(Markdig.Markdown.Parse(input, defaultExtras)));
    Console.WriteLine("  strikethrough  : " + Describe(Markdig.Markdown.Parse(input, strikeOnly)));
}

static string Describe(MarkdownDocument doc)
{
    var parts = new List<string>();
    foreach (var node in doc.Descendants())
    {
        parts.Add(node switch
        {
            EmphasisInline e => "EMPH(" + e.DelimiterChar + "x" + e.DelimiterCount + ")",
            LiteralInline l => "lit'" + l.Content.ToString() + "'",
            ParagraphBlock => "P",
            _ => node.GetType().Name,
        });
    }

    return string.Join(" ", parts);
}
