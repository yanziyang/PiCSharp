using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

internal sealed class InputFile
{
    public List<InputCase> Cases { get; set; } = new();
}

internal sealed class InputCase
{
    public string Id { get; set; } = string.Empty;
    public int TestIndex { get; set; }
    public string Test { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Kind { get; set; } = "upstream";
    public string Operation { get; set; } = "constructor";
}

internal sealed class CaseResult
{
    public List<string> Types { get; set; } = new();
    public List<NodeResult> Nodes { get; set; } = new();
}

internal sealed class NodeResult
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, object?>? Detail { get; set; }
}

// Marked's replacement tokenizer accepts ~~...~~ but leaves a single-tilde
// pair literal, matching markdown.ts's STRICT_STRIKETHROUGH_REGEX.
internal sealed class StrictSingleTildeLiteralParser : InlineParser
{
    public StrictSingleTildeLiteralParser() => OpeningCharacters = new[] { '~' };

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var text = slice.Text;
        var start = slice.Start;
        if (start < 0 || start >= text.Length || text[start] != '~' || start + 1 >= text.Length || text[start + 1] == '~')
            return false;

        var close = -1;
        for (var index = start + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }
            if (text[index] == '~')
            {
                close = index;
                break;
            }
        }

        if (close < 0 || close + 1 < text.Length && text[close + 1] == '~') return false;
        if (close == start + 1 || char.IsWhiteSpace(text[start + 1]) || char.IsWhiteSpace(text[close - 1]))
            return false;

        processor.Inline = new LiteralInline(text.Substring(start, close - start + 1));
        slice.Start = close + 1;
        return true;
    }
}

internal sealed class StrictStrikethroughExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
        => pipeline.InlineParsers.InsertBefore<EmphasisInlineParser>(new StrictSingleTildeLiteralParser());

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

// A small custom node is enough to prove that the \(...\) form is an ordinary
// Markdig InlineParser extension point. Production can render it as latex.
internal sealed class ParenLatexInline : LeafInline
{
    public string Text { get; init; } = string.Empty;
}

internal sealed class ParenLatexInlineParser : InlineParser
{
    public ParenLatexInlineParser() => OpeningCharacters = new[] { '\\' };

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var text = slice.Text;
        var start = slice.Start;
        if (start + 1 >= text.Length || text[start] != '\\' || text[start + 1] != '(') return false;

        var close = text.IndexOf("\\)", start + 2, StringComparison.Ordinal);
        if (close < 0) return false;
        processor.Inline = new ParenLatexInline { Text = text.Substring(start + 2, close - start - 2) };
        slice.Start = close + 2;
        return true;
    }
}

// The bracket form is a block parser with FencedCodeBlock-like source lines.
// It deliberately lives only in this spike; it is not a port of markdown.ts.
internal sealed class BracketLatexBlock : FencedCodeBlock
{
    public BracketLatexBlock(BlockParser parser) : base(parser) { }
}

internal sealed class BracketLatexBlockParser : BlockParser
{
    public BracketLatexBlockParser() => OpeningCharacters = new[] { '\\' };

    public override BlockState TryOpen(BlockProcessor processor)
    {
        var line = processor.Line;
        if (line.CurrentChar != '\\' || line.PeekChar(1) != '[' || !OnlyWhitespaceAfter(line, 2))
            return BlockState.None;

        processor.NewBlocks.Push(new BracketLatexBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End),
        });
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var line = processor.Line;
        if (line.CurrentChar == '\\' && line.PeekChar(1) == ']' && OnlyWhitespaceAfter(line, 2))
        {
            block.UpdateSpanEnd(line.End);
            return BlockState.BreakDiscard;
        }
        return BlockState.Continue;
    }

    private static bool OnlyWhitespaceAfter(StringSlice line, int offset)
    {
        for (var index = line.Start + offset; index <= line.End; index++)
            if (!char.IsWhiteSpace(line.Text[index])) return false;
        return true;
    }
}

internal sealed class LatexExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.InlineParsers.InsertBefore<EscapeInlineParser>(new ParenLatexInlineParser());
        pipeline.BlockParsers.Insert(0, new BracketLatexBlockParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static InputFile ReadInputs(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("cases", out var cases))
            return new InputFile { Cases = cases.Deserialize<List<InputCase>>(JsonOptions) ?? new List<InputCase>() };

        var legacy = new InputFile();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                legacy.Cases.Add(new InputCase { Id = property.Name, Source = property.Value.GetString() ?? string.Empty });
        }
        return legacy;
    }

    private static MarkdownPipeline BuildComparisonPipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseTaskLists()
            .UsePreciseSourceLocation();
        builder.Extensions.Add(new StrictStrikethroughExtension());
        return builder.Build();
    }

    private static CaseResult ParseSource(string source, MarkdownPipeline pipeline)
    {
        var document = Markdown.Parse(source, pipeline);
        var result = new CaseResult();
        foreach (var block in document) Walk(block, result);
        return result;
    }

    private static void Walk(object node, CaseResult result)
    {
        var type = node.GetType().Name;
        result.Types.Add(type);
        result.Nodes.Add(new NodeResult { Type = type, Detail = Detail(node) });

        if (node is LeafBlock leaf && leaf.Inline is not null)
            foreach (var child in leaf.Inline) Walk(child, result);
        if (node is ContainerBlock container)
            foreach (var child in container) Walk(child, result);
        if (node is ContainerInline inline)
            foreach (var child in inline) Walk(child, result);
    }

    private static Dictionary<string, object?>? Detail(object node)
    {
        var detail = new Dictionary<string, object?>();
        if (TryGetSpan(node, out var span)) detail["span"] = new { start = span.Start, end = span.End };

        if (node is LiteralInline literal)
        {
            detail["content"] = literal.Content.ToString();
            detail["isFirstCharacterEscaped"] = literal.IsFirstCharacterEscaped;
        }
        else if (node is CodeInline code)
        {
            detail["content"] = code.Content.ToString();
            detail["delimiter"] = code.Delimiter.ToString();
            detail["delimiterCount"] = code.DelimiterCount;
        }
        else if (node is EmphasisInline emphasis)
        {
            detail["delimiterChar"] = emphasis.DelimiterChar.ToString();
            detail["delimiterCount"] = emphasis.DelimiterCount;
            detail["isDouble"] = emphasis.DelimiterCount == 2;
        }
        else if (node is LineBreakInline lineBreak)
        {
            detail["isHard"] = lineBreak.IsHard;
            detail["isBackslash"] = lineBreak.IsBackslash;
        }
        else if (node is LinkInline link)
        {
            detail["url"] = link.Url;
            detail["label"] = link.Label;
            detail["isImage"] = link.IsImage;
            detail["isAutoLink"] = link.IsAutoLink;
            detail["urlHasPointyBrackets"] = link.UrlHasPointyBrackets;
        }
        else if (node.GetType().Name == "AutolinkInline")
        {
            detail["url"] = GetMember(node, "Url");
            detail["isEmail"] = GetMember(node, "IsEmail");
        }
        else if (node is HeadingBlock heading)
        {
            detail["level"] = heading.Level;
            detail["isSetext"] = heading.IsSetext;
        }
        else if (node is ListBlock list)
        {
            detail["isOrdered"] = list.IsOrdered;
            detail["orderedStart"] = list.OrderedStart;
            detail["orderedDelimiter"] = list.OrderedDelimiter.ToString();
            detail["bulletType"] = list.BulletType.ToString();
            detail["isLoose"] = list.IsLoose;
        }
        else if (node is ListItemBlock item)
        {
            detail["order"] = item.Order;
            detail["sourceBullet"] = item.SourceBullet.ToString();
            detail["columnWidth"] = item.ColumnWidth;
        }
        else if (node.GetType().Name == "TaskList")
        {
            detail["checked"] = GetMember(node, "Checked");
        }
        else if (node is FencedCodeBlock fenced)
        {
            detail["info"] = fenced.Info;
            detail["fencedChar"] = fenced.FencedChar.ToString();
            detail["openingCount"] = fenced.OpeningFencedCharCount;
            detail["closingCount"] = fenced.ClosingFencedCharCount;
            if (TryGetFieldValue(fenced, "Lines", out var lines)) detail["content"] = lines?.ToString();
        }
        else if (node is Table table)
        {
            detail["columnDefinitions"] = table.ColumnDefinitions.Select(x => x.Alignment.ToString()).ToArray();
        }
        else if (node is TableRow row)
        {
            detail["isHeader"] = row.IsHeader;
        }
        else if (node is TableCell cell)
        {
            detail["columnIndex"] = cell.ColumnIndex;
            detail["columnSpan"] = cell.ColumnSpan;
            detail["rowSpan"] = cell.RowSpan;
        }
        else if (node is QuoteBlock quote)
        {
            detail["quoteChar"] = quote.QuoteChar.ToString();
        }
        else if (node is HtmlBlock htmlBlock)
        {
            detail["htmlType"] = htmlBlock.Type.ToString();
        }
        else if (node.GetType().Name == "HtmlInline")
        {
            detail["tag"] = GetMember(node, "Tag");
        }
        else if (node.GetType().Name == "ParenLatexInline")
        {
            detail["content"] = GetMember(node, "Text");
        }

        return detail.Count == 0 ? null : detail;
    }

    private static bool TryGetSpan(object node, out SourceSpan span)
    {
        for (var type = node.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("Span", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(node) is SourceSpan value)
            {
                span = value;
                return true;
            }
        }
        span = default;
        return false;
    }

    private static string? GetMember(object value, string name)
    {
        var property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null) return property.GetValue(value)?.ToString();
        return null;
    }

    private static bool TryGetFieldValue(object value, string name, out object? fieldValue)
    {
        for (var type = value.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
            {
                fieldValue = field.GetValue(value);
                return true;
            }
        }
        fieldValue = null;
        return false;
    }

    private static bool HasDetail(CaseResult result, string type, string key, object expected)
        => result.Nodes.Any(node => node.Type == type && node.Detail is not null && node.Detail.TryGetValue(key, out var value) && Equals(value, expected));

    private static object ExtensionProofs()
    {
        var strictBuilder = new MarkdownPipelineBuilder().UseEmphasisExtras();
        strictBuilder.Extensions.Add(new StrictStrikethroughExtension());
        var strict = ParseSource("~~double~~ and ~single~", strictBuilder.Build());
        if (!HasDetail(strict, "EmphasisInline", "delimiterCount", 2) || !HasDetail(strict, "LiteralInline", "content", "~single~"))
            throw new InvalidOperationException("strict strikethrough extension proof failed");

        var latexBuilder = new MarkdownPipelineBuilder().UseMathematics();
        latexBuilder.Extensions.Add(new LatexExtension());
        var latexPipeline = latexBuilder.Build();
        var inline = ParseSource("$x$ and \\(y\\)", latexPipeline);
        var dollarBlock = ParseSource("$$\nx\n$$", latexPipeline);
        var bracketBlock = ParseSource("\\[\nx^2\n\\]", latexPipeline);
        if (!inline.Types.Contains("MathInline") || !inline.Types.Contains("ParenLatexInline") || !dollarBlock.Types.Contains("MathBlock") || !bracketBlock.Types.Contains("BracketLatexBlock"))
            throw new InvalidOperationException("LaTeX extension proof failed");

        return new
        {
            strictStrikethrough = new
            {
                source = "~~double~~ and ~single~",
                types = strict.Types,
                nodes = strict.Nodes,
                passed = true,
                check = "double tilde is EmphasisInline; single tilde remains one LiteralInline",
            },
            latex = new
            {
                inline = new { source = "$x$ and \\(y\\)", types = inline.Types, nodes = inline.Nodes, passed = true },
                blockDollar = new { source = "$$\nx\n$$", types = dollarBlock.Types, nodes = dollarBlock.Nodes, passed = true },
                blockBracket = new { source = "\\[\nx^2\n\\]", types = bracketBlock.Types, nodes = bracketBlock.Nodes, passed = true },
                check = "built-in MathInline/MathBlock plus custom InlineParser/BlockParser delimiters",
            },
        };
    }

    public static void Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("usage: MarkdigDump <inputs.json> <markdig.json>");
        var inputs = ReadInputs(args[0]);
        var pipeline = BuildComparisonPipeline();
        var results = new Dictionary<string, CaseResult>();
        foreach (var input in inputs.Cases)
            results[input.Id] = ParseSource(input.Source, pipeline);

        var output = new
        {
            metadata = new
            {
                parser = "Markdig",
                version = typeof(Markdown).Assembly.GetName().Version?.ToString(),
                pipeline = new[] { "UsePipeTables", "UseEmphasisExtras", "UseAutoLinks", "UseTaskLists", "UsePreciseSourceLocation", "StrictStrikethroughExtension" },
                trackedTrivia = false,
                caseCount = results.Count,
            },
            cases = results,
            extensionProofs = ExtensionProofs(),
        };
        File.WriteAllText(args[1], JsonSerializer.Serialize(output, JsonOptions));
        Console.WriteLine($"wrote Markdig streams for {results.Count} cases; extension proofs passed");
    }
}
