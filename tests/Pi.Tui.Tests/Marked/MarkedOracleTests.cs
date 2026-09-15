using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Compares the marked port with the committed marked 18.0.5 oracle.</summary>
public sealed class MarkedOracleTests
{
    private static readonly string _root = FindRoot();
    private static readonly string _fixtureDirectory = Path.Combine(_root, "tests", "fixtures", "marked");
    private static readonly OracleCase[] _cases = ReadCorpus();

    [Fact(DisplayName = "plain marked tokens and links match the oracle")]
    public void Plain_tokens_and_links_match_the_oracle() => CompareFixture("plain", static () => new Marked());

    [Fact(DisplayName = "pi markdown parser tokens and links match the oracle")]
    public void Pi_markdown_parser_tokens_and_links_match_the_oracle() => CompareFixture("pi", static () => MarkdownParser.Parser);

    [Fact(DisplayName = "marked extension probe tokens and links match the oracle")]
    public void Marked_extension_probe_tokens_and_links_match_the_oracle() => CompareFixture("probe", static () => CreateProbeParser());

    [Fact(DisplayName = "fixture headers pin the corpus and marked version")]
    public void Fixture_headers_pin_the_corpus_and_marked_version()
    {
        if (IsNestingProbe) return;
        var corpusBytes = File.ReadAllBytes(Path.Combine(_fixtureDirectory, "corpus.jsonl"));
        var corpusHash = Convert.ToHexString(SHA256.HashData(corpusBytes)).ToLowerInvariant();
        var pin = File.ReadAllText(Path.Combine(_root, "reference", "marked", "PINNED"));
        var pinMatch = Regex.Match(pin, @"^marked\s+(\S+)\s+(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(pinMatch.Success, "reference/marked/PINNED is not parseable.");

        foreach (var configuration in new[] { "plain", "pi", "probe" })
        {
            var fixture = ReadFixture(configuration);
            Assert.Equal(pinMatch.Groups[1].Value, fixture.Header["markedVersion"]!.GetValue<string>());
            Assert.Equal(pinMatch.Groups[2].Value, fixture.Header["integrity"]!.GetValue<string>());
            Assert.Equal(configuration, fixture.Header["configuration"]!.GetValue<string>());
            Assert.Equal(corpusHash, fixture.Header["corpusSha256"]!.GetValue<string>());
            Assert.Equal(_cases.Length, fixture.Header["caseCount"]!.GetValue<int>());
            Assert.Equal(_cases.Length, fixture.Cases.Count);
        }
    }

    [Fact(DisplayName = "all tokenizer methods can be overridden and the source view is zero-copy")]
    public void All_tokenizer_methods_can_be_overridden_and_source_view_is_zero_copy()
    {
        if (IsNestingProbe) return;
        var source = "prefix **bold** and `code`";
        var view = new SourceView(source, 7, 8);
        var slice = view.Slice(2, 3);
        Assert.Same(source, view.Source);
        Assert.Same(source, slice.Source);
        Assert.Equal(7, view.Offset);
        Assert.Equal(2, slice.Offset - view.Offset);
        Assert.Equal("bol", slice.Materialize());

        var tokenizer = new CountingTokenizer();
        var parser = new Marked();
        parser.SetOptions(new MarkedOptions { Tokenizer = tokenizer });
        foreach (var item in _cases) _ = parser.Lexer(item.Source);
        Assert.Equal(24, tokenizer.Hits.Count);
        Assert.All(_tokenizerMethodNames, name => Assert.True(tokenizer.Hits.ContainsKey(name), $"{name} was never dispatched."));
    }

    [Fact(DisplayName = "plain pi and probe coverage reaches every tokenizer method")]
    public void Plain_pi_and_probe_coverage_reaches_every_tokenizer_method()
    {
        if (IsNestingProbe) return;
        var snapshots = new[]
        {
            CaptureCoverage("plain", new CountingTokenizer(), static tokenizer => new Marked().SetOptions(new MarkedOptions { Tokenizer = tokenizer })),
            CaptureCoverage("pi", new CountingPiTokenizer(), static tokenizer => MarkdownParser.CreateParserForTests(tokenizer)),
            CaptureCoverage("probe", new CountingTokenizer(), static tokenizer => CreateProbeParser(tokenizer)),
        };

        foreach (var snapshot in snapshots)
        {
            Assert.Equal(24, snapshot.Hits.Count);
            Assert.All(_tokenizerMethodNames, name => Assert.True(snapshot.Hits.TryGetValue(name, out var count) && count > 0, $"{snapshot.Name}.{name} was not reached."));
            Console.WriteLine(FormatCoverage(snapshot));
        }
    }

    [Fact(DisplayName = "marked inline tag rule stops ordinary text")]
    public void Marked_inline_tag_rule_stops_ordinary_text()
    {
        if (IsNestingProbe) return;
        var rules = new MarkedRuleSet();
        var text = "This is text with <thinking>hidden content</thinking> that should be visible";
        Assert.Equal("This is text with ", rules.Inline.Text.Match(text).Value);
        Assert.Matches(rules.Inline.Tag, "<thinking>");
    }

    [Fact(DisplayName = "one parser instance can lex concurrently")]
    public async Task One_parser_instance_can_lex_concurrently()
    {
        if (IsNestingProbe) return;
        var parser = MarkdownParser.Parser;
        var selected = _cases.Where(static item => item.Source.Length > 0).Take(64).ToArray();
        var expected = selected.Select(item => LexJson(parser, item.Source)).ToArray();
        var failures = new List<string>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async worker =>
        {
            await Task.Yield();
            foreach (var (item, expectedJson) in selected.Zip(expected))
            {
                var actual = LexJson(parser, item.Source);
                if (Difference(expectedJson, actual, "$") is { } difference)
                {
                    lock (failures) failures.Add($"worker {worker}, case {item.Id}: {difference}");
                }
            }
        }));
        Assert.Empty(failures);
    }

    [Fact(DisplayName = "ten thousand block nesting levels do not crash the process")]
    public void Ten_thousand_block_nesting_levels_do_not_crash_the_process()
    {
        if (IsNestingProbe)
        {
            RunNestingSource(string.Concat(Enumerable.Repeat("> ", 10_000)) + "leaf");
            RunNestingSource(string.Concat(Enumerable.Repeat("- ", 10_000)) + "leaf");
            return;
        }

        var project = Path.Combine(_root, "tests", "Pi.Tui.Tests", "Pi.Tui.Tests.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--filter-class");
        startInfo.ArgumentList.Add("Pi.Tui.Tests.MarkedOracleTests");
        startInfo.Environment["PI_MARKED_NESTING_PROBE"] = "1";
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        if (!process!.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The 10,000-level nesting probe did not finish within 30 seconds.");
        }
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"The 10,000-level nesting probe exited {process.ExitCode}.\n{output}");
    }

    private static void RunNestingSource(string source)
    {
        try
        {
            _ = new Marked().Lexer(source);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            Console.WriteLine($"Nesting probe stopped with {exception.GetType().Name}: {exception.Message}");
        }
    }

    [Fact(DisplayName = "unsupported marked flags name the option")]
    public void Unsupported_marked_flags_name_the_option()
    {
        if (IsNestingProbe) return;
        Assert.Throws<NotSupportedException>(() => new Marked().Lexer("x", new MarkedOptions { Gfm = false }));
        Assert.Throws<NotSupportedException>(() => new Marked().Lexer("x", new MarkedOptions { Pedantic = true }));
        Assert.Throws<NotSupportedException>(() => new Marked().Lexer("x", new MarkedOptions { Breaks = true }));
        Assert.Throws<NotSupportedException>(() => new Marked().Lexer("x", new MarkedOptions { Async = true }));
        Assert.Throws<NotSupportedException>(() => new Marked().Lexer("x", new MarkedOptions { Silent = true }));
        Assert.Contains("gfm", Assert.Throws<NotSupportedException>(() => new Marked().Lexer("x", new MarkedOptions { Gfm = false })).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "probe block override falls back to the base tokenizer")]
    public void Probe_block_override_falls_back_to_the_base_tokenizer()
    {
        if (IsNestingProbe) return;
        var tokenizer = new ProbeTokenizer();
        var parser = new Marked();
        parser.SetOptions(new MarkedOptions { Tokenizer = tokenizer });
        var result = parser.Lexer("# heading");
        Assert.Equal("heading", result[0].Type);
        Assert.True(tokenizer.HeadingCalls > 0);
    }

    private static void CompareFixture(string configuration, Func<Marked> parserFactory)
    {
        if (IsNestingProbe) return;
        var fixture = ReadFixture(configuration);
        var parser = parserFactory();
        foreach (var oracleCase in fixture.Cases)
        {
            var actual = LexJson(parser, oracleCase.Source);
            var expected = oracleCase.Result;
            if (Difference(expected, actual, "$") is { } difference)
            {
                Assert.Fail($"{configuration} case '{oracleCase.Id}' differs at {difference.Path}.\nExpected: {TrimJson(difference.Expected)}\nActual:   {TrimJson(difference.Actual)}");
            }
        }
    }

    private static JsonObject LexJson(Marked parser, string source)
    {
        try
        {
            var tokens = parser.Lexer(source);
            return new JsonObject
            {
                ["tokens"] = TokenJson.ToArray(tokens),
                ["links"] = TokenJson.ToLinks(tokens.Links),
            };
        }
        catch (Exception exception)
        {
            return new JsonObject { ["error"] = exception.Message };
        }
    }

    private static Fixture ReadFixture(string configuration)
    {
        var lines = File.ReadAllLines(Path.Combine(_fixtureDirectory, configuration + ".jsonl"));
        Assert.NotEmpty(lines);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        var cases = lines.Skip(1).Where(static line => line.Length > 0).Select(line =>
        {
            var value = JsonNode.Parse(line)!.AsObject();
            return new OracleCase(
                value["id"]!.GetValue<string>(),
                value["source"]!.GetValue<string>(),
                value["result"]!.AsObject());
        }).ToArray();
        return new Fixture(header, cases);
    }

    private static OracleCase[] ReadCorpus()
    {
        var path = Path.Combine(_fixtureDirectory, "corpus.jsonl");
        return File.ReadAllLines(path).Where(static line => line.Length > 0).Select(line =>
        {
            var value = JsonNode.Parse(line)!.AsObject();
            return new OracleCase(value["id"]!.GetValue<string>(), value["source"]!.GetValue<string>(), new JsonObject());
        }).ToArray();
    }

    private static Marked CreateProbeParser(Tokenizer? tokenizer = null)
    {
        var parser = new Marked();
        parser.SetOptions(new MarkedOptions { Tokenizer = tokenizer ?? new ProbeTokenizer() });
        parser.Use(
            new TokenizerExtension
            {
                Name = "probeBlockFirst",
                Level = "block",
                Start = static (_, source) => source.IndexOf("@@", comparison: StringComparison.Ordinal),
                Tokenizer = static (lexer, source, tokens) => ProbeBlock(lexer, source, tokens, "probe_block_first"),
            },
            new TokenizerExtension
            {
                Name = "probeBlockSecond",
                Level = "block",
                Start = static (_, source) => source.IndexOf("@@", comparison: StringComparison.Ordinal),
                Tokenizer = static (lexer, source, tokens) => ProbeBlock(lexer, source, tokens, "probe_block_second"),
            },
            new TokenizerExtension
            {
                Name = "probeBlockNegativeStart",
                Level = "block",
                Start = static (_, _) => -1,
                Tokenizer = static (_, _, _) => null,
            },
            new TokenizerExtension
            {
                Name = "probeInlineContext",
                Level = "inline",
                Start = static (_, source) => source.IndexOf("[[", comparison: StringComparison.Ordinal),
                Tokenizer = static (lexer, source, tokens) => ProbeInline(lexer, source, tokens),
            });
        return parser;
    }

    private static Tokens.Generic? ProbeBlock(Lexer lexer, SourceView source, IList<Token> tokens, string type)
    {
        if (!source.StartsWith("@@B:", StringComparison.Ordinal)) return null;
        var end = source.IndexOf("@@", 4, StringComparison.Ordinal);
        if (end < 0) return null;
        var raw = source.Slice(0, end + 2).Materialize();
        var text = raw[4..^4];
        var token = new Tokens.Generic(type, raw);
        token.Properties["text"] = text;
        token.Properties["seen"] = JsonValue.Create(tokens.Count);
        token.Children = lexer.InlineTokens(text);
        return token;
    }

    private static Tokens.Generic? ProbeInline(Lexer lexer, SourceView source, IList<Token> tokens)
    {
        if (!source.StartsWith("[[", StringComparison.Ordinal)) return null;
        var end = source.IndexOf("]]", start: 2, comparison: StringComparison.Ordinal);
        if (end < 0) return null;
        var raw = source.Slice(0, end + 2).Materialize();
        var text = raw[2..^2];
        var token = new Tokens.Generic("probe_inline", raw);
        token.Properties["text"] = text;
        token.Properties["seen"] = JsonValue.Create(tokens.Count);
        token.Children = lexer.InlineTokens(text);
        return token;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PiCSharp.slnx"))) return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate the PiCSharp repository root.");
    }

    private static string TrimJson(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length <= 1800 ? text : text[..1800] + "…";
    }

    private static DifferenceResult? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : new DifferenceResult(path, expected, actual);
        }
        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var key in expectedObject.Select(static pair => pair.Key).Union(actualObject.Select(static pair => pair.Key)).OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!expectedObject.ContainsKey(key) || !actualObject.ContainsKey(key)) return new DifferenceResult(path + "." + key, expectedObject[key], actualObject[key]);
                if (Difference(expectedObject[key], actualObject[key], path + "." + key) is { } difference) return difference;
            }
            return null;
        }
        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count) return new DifferenceResult(path + ".length", expectedArray, actualArray);
            for (var i = 0; i < expectedArray.Count; i++)
            {
                if (Difference(expectedArray[i], actualArray[i], path + "[" + i + "]") is { } difference) return difference;
            }
            return null;
        }
        return expected.ToJsonString() == actual.ToJsonString() ? null : new DifferenceResult(path, expected, actual);
    }

    private static CoverageSnapshot CaptureCoverage(string name, Tokenizer tokenizer, Func<Tokenizer, Marked> parserFactory)
    {
        var parser = parserFactory(tokenizer);
        var tokenTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in _cases)
        {
            var tokens = parser.Lexer(item.Source);
            CountTokenTypes(TokenJson.ToArray(tokens), tokenTypes);
        }
        var hits = tokenizer is IHitCounter counter
            ? new Dictionary<string, int>(counter.Hits, StringComparer.Ordinal)
            : throw new InvalidOperationException($"Coverage tokenizer for {name} does not expose hit counts.");
        return new CoverageSnapshot(name, tokenTypes, hits);
    }

    private static void CountTokenTypes(JsonNode? node, Dictionary<string, int> counts)
    {
        if (node is JsonObject value && value["type"] is JsonValue type)
        {
            var name = type.GetValue<string>();
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        if (node is JsonObject objectValue)
        {
            foreach (var child in objectValue) CountTokenTypes(child.Value, counts);
        }
        else if (node is JsonArray arrayValue)
        {
            foreach (var child in arrayValue) CountTokenTypes(child, counts);
        }
    }

    private static string FormatCoverage(CoverageSnapshot snapshot)
        => $"{snapshot.Name}: token types [{string.Join(", ", snapshot.TokenTypes.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}"))}] methods [{string.Join(", ", snapshot.Hits.Select(static pair => $"{pair.Key}={pair.Value}"))}]";

    private sealed record Fixture(JsonObject Header, IReadOnlyList<OracleCase> Cases);
    private sealed record OracleCase(string Id, string Source, JsonObject Result);
    private sealed record DifferenceResult(string Path, JsonNode? Expected, JsonNode? Actual);

    private static readonly string[] _tokenizerMethodNames =
    [
        nameof(Tokenizer.Space), nameof(Tokenizer.Code), nameof(Tokenizer.Fences), nameof(Tokenizer.Heading),
        nameof(Tokenizer.Hr), nameof(Tokenizer.Blockquote), nameof(Tokenizer.List), nameof(Tokenizer.Html),
        nameof(Tokenizer.Def), nameof(Tokenizer.Table), nameof(Tokenizer.Lheading), nameof(Tokenizer.Paragraph),
        nameof(Tokenizer.Text), nameof(Tokenizer.Escape), nameof(Tokenizer.Tag), nameof(Tokenizer.Link),
        nameof(Tokenizer.Reflink), nameof(Tokenizer.EmStrong), nameof(Tokenizer.Codespan), nameof(Tokenizer.Br),
        nameof(Tokenizer.Del), nameof(Tokenizer.Autolink), nameof(Tokenizer.Url), nameof(Tokenizer.InlineText),
    ];

    private static bool IsNestingProbe => string.Equals(Environment.GetEnvironmentVariable("PI_MARKED_NESTING_PROBE"), "1", StringComparison.Ordinal);

    private interface IHitCounter
    {
        Dictionary<string, int> Hits { get; }
    }

    private sealed record CoverageSnapshot(string Name, Dictionary<string, int> TokenTypes, Dictionary<string, int> Hits);

    private class CountingTokenizer : Tokenizer, IHitCounter
    {
        public Dictionary<string, int> Hits { get; } = new(StringComparer.Ordinal);
        protected void Hit(string name) => Hits[name] = Hits.GetValueOrDefault(name) + 1;
        public override Tokens.Space? Space(SourceView source) { Hit(nameof(Space)); return base.Space(source); }
        public override Tokens.Code? Code(SourceView source) { Hit(nameof(Code)); return base.Code(source); }
        public override Tokens.Code? Fences(SourceView source) { Hit(nameof(Fences)); return base.Fences(source); }
        public override Tokens.Heading? Heading(SourceView source) { Hit(nameof(Heading)); return base.Heading(source); }
        public override Tokens.Hr? Hr(SourceView source) { Hit(nameof(Hr)); return base.Hr(source); }
        public override Tokens.Blockquote? Blockquote(SourceView source) { Hit(nameof(Blockquote)); return base.Blockquote(source); }
        public override Tokens.List? List(SourceView source) { Hit(nameof(List)); return base.List(source); }
        public override Tokens.Html? Html(SourceView source) { Hit(nameof(Html)); return base.Html(source); }
        public override Tokens.Def? Def(SourceView source) { Hit(nameof(Def)); return base.Def(source); }
        public override Tokens.Table? Table(SourceView source) { Hit(nameof(Table)); return base.Table(source); }
        public override Tokens.Heading? Lheading(SourceView source) { Hit(nameof(Lheading)); return base.Lheading(source); }
        public override Tokens.Paragraph? Paragraph(SourceView source) { Hit(nameof(Paragraph)); return base.Paragraph(source); }
        public override Tokens.Text? Text(SourceView source) { Hit(nameof(Text)); return base.Text(source); }
        public override Tokens.Escape? Escape(SourceView source) { Hit(nameof(Escape)); return base.Escape(source); }
        public override Tokens.Html? Tag(SourceView source) { Hit(nameof(Tag)); return base.Tag(source); }
        public override Token? Link(SourceView source) { Hit(nameof(Link)); return base.Link(source); }
        public override Token? Reflink(SourceView source, Dictionary<string, LinkReference> links) { Hit(nameof(Reflink)); return base.Reflink(source, links); }
        public override Token? EmStrong(SourceView source, SourceView maskedSource, string previous = "") { Hit(nameof(EmStrong)); return base.EmStrong(source, maskedSource, previous); }
        public override Tokens.Codespan? Codespan(SourceView source) { Hit(nameof(Codespan)); return base.Codespan(source); }
        public override Tokens.Br? Br(SourceView source) { Hit(nameof(Br)); return base.Br(source); }
        public override Tokens.Del? Del(SourceView source, SourceView maskedSource, string previous = "") { Hit(nameof(Del)); return base.Del(source, maskedSource, previous); }
        public override Tokens.Link? Autolink(SourceView source) { Hit(nameof(Autolink)); return base.Autolink(source); }
        public override Tokens.Link? Url(SourceView source) { Hit(nameof(Url)); return base.Url(source); }
        public override Tokens.Text? InlineText(SourceView source) { Hit(nameof(InlineText)); return base.InlineText(source); }
    }

    private sealed class CountingPiTokenizer : MarkdownParser.StrictStrikethroughTokenizer, IHitCounter
    {
        public Dictionary<string, int> Hits { get; } = new(StringComparer.Ordinal);
        private void Hit(string name) => Hits[name] = Hits.GetValueOrDefault(name) + 1;
        public override Tokens.Space? Space(SourceView source) { Hit(nameof(Space)); return base.Space(source); }
        public override Tokens.Code? Code(SourceView source) { Hit(nameof(Code)); return base.Code(source); }
        public override Tokens.Code? Fences(SourceView source) { Hit(nameof(Fences)); return base.Fences(source); }
        public override Tokens.Heading? Heading(SourceView source) { Hit(nameof(Heading)); return base.Heading(source); }
        public override Tokens.Hr? Hr(SourceView source) { Hit(nameof(Hr)); return base.Hr(source); }
        public override Tokens.Blockquote? Blockquote(SourceView source) { Hit(nameof(Blockquote)); return base.Blockquote(source); }
        public override Tokens.List? List(SourceView source) { Hit(nameof(List)); return base.List(source); }
        public override Tokens.Html? Html(SourceView source) { Hit(nameof(Html)); return base.Html(source); }
        public override Tokens.Def? Def(SourceView source) { Hit(nameof(Def)); return base.Def(source); }
        public override Tokens.Table? Table(SourceView source) { Hit(nameof(Table)); return base.Table(source); }
        public override Tokens.Heading? Lheading(SourceView source) { Hit(nameof(Lheading)); return base.Lheading(source); }
        public override Tokens.Paragraph? Paragraph(SourceView source) { Hit(nameof(Paragraph)); return base.Paragraph(source); }
        public override Tokens.Text? Text(SourceView source) { Hit(nameof(Text)); return base.Text(source); }
        public override Tokens.Escape? Escape(SourceView source) { Hit(nameof(Escape)); return base.Escape(source); }
        public override Tokens.Html? Tag(SourceView source) { Hit(nameof(Tag)); return base.Tag(source); }
        public override Token? Link(SourceView source) { Hit(nameof(Link)); return base.Link(source); }
        public override Token? Reflink(SourceView source, Dictionary<string, LinkReference> links) { Hit(nameof(Reflink)); return base.Reflink(source, links); }
        public override Token? EmStrong(SourceView source, SourceView maskedSource, string previous = "") { Hit(nameof(EmStrong)); return base.EmStrong(source, maskedSource, previous); }
        public override Tokens.Codespan? Codespan(SourceView source) { Hit(nameof(Codespan)); return base.Codespan(source); }
        public override Tokens.Br? Br(SourceView source) { Hit(nameof(Br)); return base.Br(source); }
        public override Tokens.Del? Del(SourceView source, SourceView maskedSource, string previous = "") { Hit(nameof(Del)); return base.Del(source, maskedSource, previous); }
        public override Tokens.Link? Autolink(SourceView source) { Hit(nameof(Autolink)); return base.Autolink(source); }
        public override Tokens.Link? Url(SourceView source) { Hit(nameof(Url)); return base.Url(source); }
        public override Tokens.Text? InlineText(SourceView source) { Hit(nameof(InlineText)); return base.InlineText(source); }
    }

    private sealed class ProbeTokenizer : CountingTokenizer
    {
        public int HeadingCalls { get; private set; }
        public override Tokens.Heading? Heading(SourceView source)
        {
            HeadingCalls++;
            return base.Heading(source);
        }
    }
}
