using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class UtilityTests
{
    [Fact]
    public void Repairs_raw_control_characters_and_invalid_escapes()
    {
        var repaired = JsonParseUtilities.RepairJson("{\"value\":\"line\nnext\\q\"}");

        Assert.Equal("{\"value\":\"line\\nnext\\\\q\"}", repaired);
        var parsed = Assert.IsType<JsonObject>(JsonParseUtilities.ParseJsonWithRepair(repaired));
        Assert.Equal("line\nnext\\q", parsed["value"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_complete_and_partial_streaming_json_without_external_dependencies()
    {
        var complete = Assert.IsType<JsonObject>(JsonParseUtilities.ParseStreamingJson("{\"a\":1,\"b\":true}"));
        Assert.Equal(1, complete["a"]!.GetValue<int>());
        Assert.True(complete["b"]!.GetValue<bool>());

        var partialString = Assert.IsType<JsonObject>(JsonParseUtilities.ParseStreamingJson("{\"name\":\"rea"));
        Assert.Equal("rea", partialString["name"]!.GetValue<string>());

        var partial = Assert.IsType<JsonObject>(JsonParseUtilities.ParseStreamingJson("{\"count\":12"));
        Assert.Equal(12, partial["count"]!.GetValue<int>());

        var partialArray = Assert.IsType<JsonArray>(JsonParseUtilities.ParseStreamingJson("[1, {\"ok\": true"));
        Assert.Equal(2, partialArray.Count);
        Assert.Equal(1, partialArray[0]!.GetValue<int>());
        Assert.True(partialArray[1]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Estimates_context_tokens_using_latest_applicable_usage()
    {
        var staleContext = new Context
        {
            SystemPrompt = "system",
            Messages =
            [
                UserMessage.Text("summary", 200),
                Assistant(100, 9500),
                UserMessage.Text(new string('x', 4000), 300),
            ],
        };

        Assert.Equal(
            new ContextUsageEstimate
            {
                Tokens = 1005,
                UsageTokens = 0,
                TrailingTokens = 1005,
                LastUsageIndex = null,
            },
            EstimateUtilities.EstimateContextTokens(staleContext));

        var currentContext = new Context
        {
            Messages =
            [
                UserMessage.Text("summary", 200),
                Assistant(100, 9500),
                UserMessage.Text("new prompt", 300),
                Assistant(400, 2000),
                UserMessage.Text("tail", 500),
            ],
        };

        var estimate = EstimateUtilities.EstimateContextTokens(currentContext);
        Assert.Equal(2001, estimate.Tokens);
        Assert.Equal(2000, estimate.UsageTokens);
        Assert.Equal(1, estimate.TrailingTokens);
        Assert.Equal(3, estimate.LastUsageIndex);
    }

    [Fact]
    public void Reuses_the_frozen_content_text_counterpart()
    {
        ContentBlock[] content =
        [
            new ThinkingContent("reasoning"),
            new TextContent("first"),
            new ToolCall("1", "read", new JsonObject()),
            new TextContent("second"),
        ];

        Assert.Equal("first\nsecond", MessageUtilities.ContentText(content));
        Assert.Equal("firstsecond", MessageUtilities.ContentText(content, string.Empty));
        Assert.Equal("hello", MessageUtilities.ContentText("hello"));
    }

    [Fact]
    public void Generates_monotonic_rfc9562_uuidv7_values()
    {
        var first = UuidUtilities.UuidV7();
        var second = UuidUtilities.UuidV7();
        var pattern = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant);

        Assert.Matches(pattern, first);
        Assert.Matches(pattern, second);
        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void Detects_provider_overflow_and_recoverable_length_stops()
    {
        var overflow = Assistant(0, 0) with
        {
            StopReason = StopReasons.Error,
            ErrorMessage = "400 Input length (265330) exceeds model's maximum context length (262144).",
        };
        Assert.True(OverflowUtilities.IsContextOverflow(overflow, 262144));

        var throttled = overflow with { ErrorMessage = "Throttling error: Too many tokens, please wait." };
        Assert.False(OverflowUtilities.IsContextOverflow(throttled, 262144));

        var length = Assistant(0, 0) with
        {
            StopReason = StopReasons.Length,
            Usage = new Usage { Input = 100, Output = 0, TotalTokens = 100 },
        };
        Assert.True(OverflowUtilities.IsRecoverableLength(length, 128000));
        Assert.False(OverflowUtilities.IsContextOverflow(length, 200000));
    }

    [Fact]
    public void Normalizes_and_formats_structured_provider_error_bodies()
    {
        var exception = new ProviderErrorMetadataException(
            "403 status code (no body)",
            new ProviderErrorMetadata
            {
                Status = 403,
                ParsedError = new JsonObject { ["error"] = "blocked by gateway WAF" },
            });

        var normalized = ErrorBodyUtilities.NormalizeProviderError(exception);
        Assert.Equal(403, normalized.Status);
        Assert.Equal("{\"error\":\"blocked by gateway WAF\"}", normalized.Body);
        Assert.False(normalized.MessageCarriesBody);
        Assert.Equal(
            "OpenAI API error (403): {\"error\":\"blocked by gateway WAF\"}",
            ErrorBodyUtilities.FormatProviderError(normalized, "OpenAI API error"));

        var carried = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "Permission denied {\"error\":\"blocked\"}",
                new ProviderErrorMetadata
                {
                    Status = 403,
                    Body = "{\"error\":\"blocked\"}",
                }));
        Assert.True(carried.MessageCarriesBody);
        Assert.Equal("Gateway (403): Permission denied {\"error\":\"blocked\"}", ErrorBodyUtilities.FormatProviderError(carried, "Gateway"));

        var nonError = ErrorBodyUtilities.NormalizeProviderError(new Dictionary<string, string> { ["reason"] = "boom" });
        Assert.Equal("{\"reason\":\"boom\"}", nonError.Message);
        Assert.False(nonError.MessageCarriesBody);
    }

    [Fact]
    public void Preserves_provider_error_stream_and_truncates_large_bodies()
    {
        var streamBody = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "Input is too long for requested model.",
                new ProviderErrorMetadata
                {
                    Status = 400,
                    ResponseBodyIsReadableStream = true,
                    ResponseBodyObject = new JsonObject { ["internal"] = "noise" },
                }));
        Assert.Null(streamBody.Body);
        Assert.True(streamBody.MessageCarriesBody);

        var body = new string('x', ErrorBodyUtilities.MaxProviderErrorBodyChars + 50);
        var truncated = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "failed",
                new ProviderErrorMetadata { StatusCode = 500, Body = body }));
        Assert.Contains("... [truncated 50 chars]", truncated.Body);
        Assert.True(truncated.Body!.Length < body.Length);
    }

    [Fact]
    public void Resolves_scoped_proxy_and_respects_no_proxy()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "http://proxy.example:8080",
            ["NO_PROXY"] = "localhost,*.internal.example",
        };

        var proxy = HttpProxyUtilities.ResolveHttpProxyUrlForTarget(new Uri("https://api.example.com/v1"), environment);
        Assert.Equal("http://proxy.example:8080/", proxy!.AbsoluteUri);
        Assert.Null(HttpProxyUtilities.ResolveHttpProxyUrlForTarget(new Uri("https://api.internal.example/v1"), environment));

        var unsupported = new Dictionary<string, string> { ["HTTPS_PROXY"] = "socks5://proxy.example:1080" };
        var error = Assert.Throws<InvalidOperationException>(() =>
            HttpProxyUtilities.ResolveHttpProxyUrlForTarget(new Uri("https://api.example.com"), unsupported));
        Assert.Contains(HttpProxyUtilities.UnsupportedProxyProtocolMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Splits_deferred_tools_after_transcript_use()
    {
        var read = Tool("Read");
        var write = Tool("Write");
        var context = new Context
        {
            Tools = [read, write],
            Messages =
            [
                new ToolResultMessage
                {
                    ToolCallId = "1",
                    ToolName = "discover",
                    AddedToolNames = ["write"],
                    Timestamp = 1,
                },
            ],
        };

        var split = DeferredToolUtilities.SplitDeferredTools(context, true, static name => name.ToLowerInvariant());
        Assert.Equal(["Read"], split.Immediate.Select(tool => tool.Name));
        Assert.Equal(write, split.Deferred["write"]);
    }

    [Fact]
    public async Task Combines_tokens_and_races_aborted_operations()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var combined = AbortSignalUtilities.CombineAbortSignals([first.Token, second.Token]);
        Assert.True(combined.Signal!.Value.CanBeCanceled);

        second.Cancel();
        Assert.True(combined.Signal.Value.IsCancellationRequested);

        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var waiting = AbortUtilities.RaceWithAbortSignal(pending.Task, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public async Task Retries_transient_assistant_and_provider_failures()
    {
        var calls = 0;
        var retried = await RetryUtilities.RetryAssistantCall(
            () =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? Assistant(0, 0) with { StopReason = StopReasons.Error, ErrorMessage = "503 server error" }
                    : Assistant(0, 1));
            },
            new RetryPolicy { Enabled = true, MaxRetries = 1, BaseDelayMs = 1 },
            signal: TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(StopReasons.Stop, retried.StopReason);

        var providerCalls = 0;
        var result = await ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                providerCalls++;
                return providerCalls == 1
                    ? Task.FromException<string>(new ProviderRetryException("busy", 503, new Dictionary<string, string> { ["retry-after-ms"] = "0" }))
                    : Task.FromResult("ok");
            },
            maxRetries: 1,
            signal: TestContext.Current.CancellationToken);
        Assert.Equal("ok", result);
        Assert.Equal(2, providerCalls);
    }

    [Fact]
    public void Builds_provider_compatible_string_enum_and_hashes_utf16()
    {
        var schema = TypeBoxHelpers.StringEnum(["add", "subtract"], "operation", "add");
        Assert.Equal("string", schema["type"]!.GetValue<string>());
        Assert.Equal("operation", schema["description"]!.GetValue<string>());
        Assert.Equal("add", schema["default"]!.GetValue<string>());
        Assert.Equal(2, schema["enum"]!.AsArray().Count);

        var first = HashUtilities.ShortHash("provider|model");
        Assert.Equal(first, HashUtilities.ShortHash("provider|model"));
        Assert.NotEqual(first, HashUtilities.ShortHash("provider|other"));

        var malformed = "ok" + new string('\uD83D', 1) + " " + new string('\uDE08', 1) + " 🙈";
        Assert.Equal("ok  🙈", UnicodeUtilities.SanitizeSurrogates(malformed));
    }

    [Fact]
    public void Resolves_scoped_provider_environment_before_process_environment()
    {
        var environment = new Dictionary<string, string> { ["PI_TEST_UTILITY"] = "scoped" };
        Assert.Equal("scoped", ProviderEnvironmentUtilities.GetProviderEnvValue("PI_TEST_UTILITY", environment));
        Assert.Null(ProviderEnvironmentUtilities.GetProviderEnvValue("PI_TEST_UTILITY_MISSING", environment));
        Assert.StartsWith("pi (", PiUserAgent.GetPiUserAgent(), StringComparison.Ordinal);
    }

    private static Tool Tool(string name) => new()
    {
        Name = name,
        Description = name,
        Parameters = new JsonObject { ["type"] = "object" },
    };

    private static AssistantMessage Assistant(long timestamp, int totalTokens) => new()
    {
        Content = [new TextContent("kept")],
        Api = ApiNames.OpenAiResponses,
        Provider = ProviderNames.Faux,
        Model = "faux-1",
        StopReason = StopReasons.Stop,
        Timestamp = timestamp,
        Usage = new Usage
        {
            Input = totalTokens,
            TotalTokens = totalTokens,
        },
    };
}
