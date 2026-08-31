using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[0]))!;

// Match what markdown.ts needs: tables and strikethrough are marked defaults (GFM),
// so the Markdig pipeline has to opt into the equivalents.
var pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseEmphasisExtras()
    .UseAutoLinks()
    .Build();

static void Walk(object node, List<string> outList)
{
    outList.Add(node.GetType().Name);

    if (node is LeafBlock leaf)
    {
        if (leaf.Inline is not null)
        {
            foreach (var child in leaf.Inline)
            {
                Walk(child, outList);
            }
        }

        return;
    }

    if (node is ContainerBlock container)
    {
        foreach (var child in container)
        {
            Walk(child, outList);
        }

        return;
    }

    if (node is ContainerInline ci)
    {
        foreach (var child in ci)
        {
            Walk(child, outList);
        }
    }
}

var result = new Dictionary<string, List<string>>();
foreach (var (name, src) in inputs)
{
    var doc = Markdown.Parse(src, pipeline);
    var types = new List<string>();
    foreach (var block in doc)
    {
        Walk(block, types);
    }

    result[name] = types;
}

File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote Markdig node types for {inputs.Count} cases");
