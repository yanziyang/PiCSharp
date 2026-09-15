using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Tui;

// Independent verification harness for the T5.9 marked port, run against the built Pi.Tui.dll.
var mode = args.Length > 0 ? args[0] : "";
var dir = args.Length > 1 ? args[1] : "";
switch (mode)
{
    case "fuzz": return Fuzz(dir);
    case "bench": return Bench(dir);
    case "concurrency": return Concurrency(dir, args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1, args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 2);
    case "coverage": return Coverage(args.Length > 1 ? args[1] : "");
    case "overhead": return Overhead();
    case "trace": return Trace(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine("usage: fuzz|bench <dir> | concurrency <dir> [stride] [passes] | coverage <inputs.jsonl> | overhead");
        return 2;
}

static (string Id, string Source)[] ReadInputs(string file) =>
    File.ReadAllLines(file, new UTF8Encoding(false)).Where(line => line.Length > 0)
        .Select(line => JsonNode.Parse(line)!.AsObject())
        .Select(value => (value["id"]!.GetValue<string>(), value["source"]!.GetValue<string>()))
        .ToArray();

static (string Name, Marked Parser)[] Parsers() => [("plain", new Marked()), ("pi", MarkdownParser.Parser)];

static string Serialize(Marked parser, string source)
{
    JsonObject result;
    try
    {
        var tokens = parser.Lexer(source);
        result = new JsonObject { ["tokens"] = TokenJson.ToArray(tokens), ["links"] = TokenJson.ToLinks(tokens.Links) };
    }
    catch (Exception exception)
    {
        result = new JsonObject { ["error"] = exception.Message };
    }
    return result.ToJsonString();
}

static int Fuzz(string dir)
{
    var inputs = ReadInputs(Path.Combine(dir, "inputs.jsonl"));
    foreach (var (name, parser) in Parsers())
    {
        using var writer = new StreamWriter(Path.Combine(dir, $"cs-{name}.jsonl"), false, new UTF8Encoding(false)) { NewLine = "\n" };
        var errors = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var (id, source) in inputs)
        {
            var json = Serialize(parser, source);
            if (json.StartsWith("{\"error\"", StringComparison.Ordinal)) errors++;
            writer.WriteLine("{\"id\":" + JsonValue.Create(id).ToJsonString() + ",\"result\":" + json + "}");
        }
        Console.WriteLine($"{name}: {inputs.Length} cases, {errors} errors, {stopwatch.Elapsed.TotalSeconds:F1} s");
    }
    return 0;
}

static int Bench(string dir)
{
    var files = Directory.GetFiles(Path.Combine(dir, "bench"), "*.md").OrderBy(file => file, StringComparer.Ordinal).ToArray();
    foreach (var (name, parser) in Parsers())
    {
        foreach (var file in files)
        {
            var text = File.ReadAllText(file, new UTF8Encoding(false));
            var warm = Stopwatch.StartNew();
            _ = parser.Lexer(text);
            warm.Stop();
            var count = warm.Elapsed.TotalSeconds > 20 ? 1 : 5;
            var runs = new List<double>();
            var gen2 = GC.CollectionCount(2);
            var allocated = GC.GetTotalAllocatedBytes(true);
            for (var i = 0; i < count; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                _ = parser.Lexer(text);
                runs.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            runs.Sort();
            Console.WriteLine($"{name,-5} {Path.GetFileNameWithoutExtension(file),-14} chars={text.Length,8} warm={warm.Elapsed.TotalMilliseconds,9:F1} ms median={runs[runs.Count / 2],9:F1} ms runs={count} gen2/run={(GC.CollectionCount(2) - gen2) / (double)count,5:F1} alloc/run={(GC.GetTotalAllocatedBytes(true) - allocated) / count / 1048576.0,8:F1} MB");
        }
    }
    return 0;
}

static int Concurrency(string dir, int stride, int passes)
{
    // Every stride-th case: a race needs interleavings, not all 6,881 cases, and the run must fit a timeout.
    var inputs = ReadInputs(Path.Combine(dir, "inputs.jsonl")).Where((_, index) => index % stride == 0).ToArray();
    var failures = 0;
    foreach (var (name, parser) in Parsers())
    {
        var baseline = Stopwatch.StartNew();
        var expected = inputs.Select(input => Serialize(parser, input.Source)).ToArray();
        Console.WriteLine($"{name}: single-threaded baseline over {inputs.Length} cases in {baseline.Elapsed.TotalSeconds:F1} s");
        var threadCount = Math.Max(8, Environment.ProcessorCount);
        var mismatches = 0;
        using var gate = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, threadCount).Select(worker => new Thread(() =>
        {
            gate.Wait();
            for (var pass = 0; pass < passes; pass++)
            {
                for (var k = 0; k < inputs.Length; k++)
                {
                    var rotated = (k + worker * 997) % inputs.Length;
                    var index = worker % 2 == 0 ? rotated : inputs.Length - 1 - rotated;
                    if (!string.Equals(Serialize(parser, inputs[index].Source), expected[index], StringComparison.Ordinal)) Interlocked.Increment(ref mismatches);
                }
            }
        }, 16 * 1024 * 1024)).ToArray();
        foreach (var thread in threads) thread.Start();
        var stopwatch = Stopwatch.StartNew();
        gate.Set();
        foreach (var thread in threads) thread.Join();
        Console.WriteLine($"{name}: {threadCount} explicit threads x {inputs.Length} cases x {passes} passes, {mismatches} mismatches, {stopwatch.Elapsed.TotalSeconds:F1} s");
        failures += mismatches;
    }
    return failures == 0 ? 0 : 1;
}

static int Coverage(string inputsFile)
{
    var inputs = ReadInputs(inputsFile);
    var tokenizer = new ProducedCounter();
    var parser = new Marked();
    parser.SetOptions(new MarkedOptions { Tokenizer = tokenizer });
    foreach (var (_, source) in inputs)
    {
        try { _ = parser.Lexer(source); } catch (Exception) { }
    }
    Console.WriteLine($"tokens produced (non-null returns) over {inputs.Length} cases of {Path.GetFileName(inputsFile)}:");
    Console.WriteLine("  " + string.Join(", ", ProducedCounter.Names.Select(name => $"{name}={tokenizer.Produced.GetValueOrDefault(name)}")));
    return 0;
}

static int Trace(string[] sources)
{
    foreach (var argument in sources)
    {
        var source = argument.Replace("\\n", "\n", StringComparison.Ordinal);
        try
        {
            var tokens = new Marked().Lexer(source);
            Console.WriteLine($"ok {argument}: {TokenJson.ToArray(tokens).ToJsonString()}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{argument}: {exception}");
        }
    }
    return 0;
}

static int Overhead()
{
    foreach (var (name, parser) in Parsers())
    {
        for (var i = 0; i < 50; i++) _ = parser.Lexer("a");
        const int calls = 2000;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < calls; i++) _ = parser.Lexer("a");
        stopwatch.Stop();
        Console.WriteLine($"{name}: Lexer(\"a\") {stopwatch.Elapsed.TotalMilliseconds / calls:F3} ms per call, {(GC.GetTotalAllocatedBytes(true) - allocated) / calls / 1024.0:F0} KB allocated per call");
    }
    return 0;
}

internal sealed class ProducedCounter : Tokenizer
{
    public static readonly string[] Names =
    [
        "Space", "Code", "Fences", "Heading", "Hr", "Blockquote", "List", "Html", "Def", "Table", "Lheading", "Paragraph",
        "Text", "Escape", "Tag", "Link", "Reflink", "EmStrong", "Codespan", "Br", "Del", "Autolink", "Url", "InlineText",
    ];

    public Dictionary<string, int> Produced { get; } = new(StringComparer.Ordinal);

    private T? Count<T>(string name, T? value) where T : class
    {
        if (value is not null) Produced[name] = Produced.GetValueOrDefault(name) + 1;
        return value;
    }

    public override Tokens.Space? Space(SourceView src) => Count(nameof(Space), base.Space(src));
    public override Tokens.Code? Code(SourceView src) => Count(nameof(Code), base.Code(src));
    public override Tokens.Code? Fences(SourceView src) => Count(nameof(Fences), base.Fences(src));
    public override Tokens.Heading? Heading(SourceView src) => Count(nameof(Heading), base.Heading(src));
    public override Tokens.Hr? Hr(SourceView src) => Count(nameof(Hr), base.Hr(src));
    public override Tokens.Blockquote? Blockquote(SourceView src) => Count(nameof(Blockquote), base.Blockquote(src));
    public override Tokens.List? List(SourceView src) => Count(nameof(List), base.List(src));
    public override Tokens.Html? Html(SourceView src) => Count(nameof(Html), base.Html(src));
    public override Tokens.Def? Def(SourceView src) => Count(nameof(Def), base.Def(src));
    public override Tokens.Table? Table(SourceView src) => Count(nameof(Table), base.Table(src));
    public override Tokens.Heading? Lheading(SourceView src) => Count(nameof(Lheading), base.Lheading(src));
    public override Tokens.Paragraph? Paragraph(SourceView src) => Count(nameof(Paragraph), base.Paragraph(src));
    public override Tokens.Text? Text(SourceView src) => Count(nameof(Text), base.Text(src));
    public override Tokens.Escape? Escape(SourceView src) => Count(nameof(Escape), base.Escape(src));
    public override Tokens.Html? Tag(SourceView src) => Count(nameof(Tag), base.Tag(src));
    public override Token? Link(SourceView src) => Count(nameof(Link), base.Link(src));
    public override Token? Reflink(SourceView src, Dictionary<string, LinkReference> links) => Count(nameof(Reflink), base.Reflink(src, links));
    public override Token? EmStrong(SourceView src, SourceView maskedSrc, string prevChar = "") => Count(nameof(EmStrong), base.EmStrong(src, maskedSrc, prevChar));
    public override Tokens.Codespan? Codespan(SourceView src) => Count(nameof(Codespan), base.Codespan(src));
    public override Tokens.Br? Br(SourceView src) => Count(nameof(Br), base.Br(src));
    public override Tokens.Del? Del(SourceView src, SourceView maskedSrc, string prevChar = "") => Count(nameof(Del), base.Del(src, maskedSrc, prevChar));
    public override Tokens.Link? Autolink(SourceView src) => Count(nameof(Autolink), base.Autolink(src));
    public override Tokens.Link? Url(SourceView src) => Count(nameof(Url), base.Url(src));
    public override Tokens.Text? InlineText(SourceView src) => Count(nameof(InlineText), base.InlineText(src));
}
