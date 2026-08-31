using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Additional R4 coverage for branches not selected by the twelve upstream files. These tests
/// derive their expectations from the provider wire contracts and keep all external I/O injected.
/// </summary>
public sealed class R4BranchCoverageTests
{
    [Fact]
    public async Task SseReader_flushes_an_event_record_at_EOF_and_joins_data_fields()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(
            "event: terminal\nunknown: ignored\ndata: first\ndata: second"));

        var events = new List<SseEvent>();
        await foreach (var @event in SseReader.ReadAsync(body, TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        var result = Assert.Single(events);
        Assert.Equal("terminal", result.Event);
        Assert.Equal("first\nsecond", result.Data);
        Assert.Equal(
            ["event: terminal", "unknown: ignored", "data: first", "data: second"],
            result.RawLines);
    }

    [Fact]
    public async Task SseReader_keeps_colons_in_data_values_and_empty_data_lines()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes("data:\ndata: value:with:colons\n\n"));

        var result = Assert.Single(await ReadSseAsync(body));

        Assert.Equal("\nvalue:with:colons", result.Data);
    }

    [Fact]
    public async Task ProviderHttpClient_orders_payload_and_response_hooks_and_sends_replaced_json()
    {
        var order = new List<string>();
        var handler = new R4CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Headers = { { "x-r4", "response" } },
        });
        using var httpClient = new HttpClient(handler);
        var client = new ProviderHttpClient(httpClient);

        using var response = await client.SendAsync(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test/r4"),
            new JsonObject { ["original"] = true },
            new ProviderRequestOptions
            {
                OnPayload = (_, _) =>
                {
                    order.Add("payload");
                    return ValueTask.FromResult<JsonNode?>(new JsonObject { ["replaced"] = true });
                },
                OnResponse = (metadata, _) =>
                {
                    order.Add("response");
                    Assert.Equal(201, metadata.Status);
                    Assert.Equal("response", metadata.Headers["x-r4"]);
                    return ValueTask.CompletedTask;
                },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["payload", "response"], order);
        Assert.Equal("{\"replaced\":true}", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public void ProviderHttpClient_does_not_create_content_for_a_null_payload()
    {
        using var request = ProviderHttpClient.BuildRequest(
            R4TestSupport.Model(),
            HttpMethod.Get,
            new Uri("https://example.test/r4"),
            null);

        Assert.Null(request.Content);
    }

    [Fact]
    public async Task ProviderRetry_honors_an_explicit_retry_true_header_on_a_400_status()
    {
        var calls = 0;
        var result = await ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<string>(new ProviderRetryException(
                        "explicit retry",
                        400,
                        new Dictionary<string, string>
                        {
                            ["x-should-retry"] = "true",
                            ["retry-after-ms"] = "0",
                        }))
                    : Task.FromResult("ok");
            },
            maxRetries: 1,
            signal: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ProviderRetry_retries_a_request_timeout_status_without_server_delay()
    {
        var calls = 0;
        var result = await ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<string>(new ProviderRetryException(
                        "request timeout",
                        408,
                        new Dictionary<string, string> { ["retry-after-ms"] = "0" }))
                    : Task.FromResult("ok");
            },
            maxRetries: 1,
            signal: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RetryClassifier_matches_the_wrapped_DNS_failure_literal()
    {
        const string message =
            "The pending stream has been canceled (caused by: getaddrinfo ENOTFOUND bedrock-runtime.us-east-1.amazonaws.com)";

        var assistant = R4TestSupport.Assistant(
            stopReason: StopReasons.Error,
            errorMessage: message);

        Assert.True(RetryUtilities.IsRetryableAssistantError(assistant));
    }

    [Fact]
    public void OverflowClassifier_respects_the_zero_output_context_threshold_boundary()
    {
        var atThreshold = R4TestSupport.LengthMessage(990, 0, 0);
        var belowThreshold = R4TestSupport.LengthMessage(989, 0, 0);

        Assert.True(OverflowUtilities.IsContextOverflow(atThreshold, 1_000));
        Assert.False(OverflowUtilities.IsContextOverflow(belowThreshold, 1_000));
    }

    [Fact]
    public async Task Bedrock_uses_injected_environment_for_auth_and_transport_flags()
    {
        var transport = new R4BedrockTransport
        {
            Response = R4TestSupport.BedrockResponse(
                new BedrockMessageStartEvent("assistant"),
                new BedrockMessageStopEvent("end_turn")),
        };
        var model = R4TestSupport.Model(
            api: ApiNames.BedrockConverseStream,
            provider: "amazon-bedrock",
            id: "us.anthropic.claude-opus-4-8",
            baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com");

        var result = await new BedrockConverseProvider(transport).Stream(
            model,
            R4TestSupport.UserContext(),
            new BedrockOptions
            {
                ApiKey = "ignored-by-skip-auth",
                Environment = new Dictionary<string, string>
                {
                    ["AWS_REGION"] = "ap-southeast-1",
                    ["AWS_BEDROCK_SKIP_AUTH"] = "1",
                    ["AWS_BEDROCK_FORCE_HTTP1"] = "1",
                },
            }).Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        var options = Assert.IsType<BedrockTransportOptions>(transport.Options);
        Assert.Equal("ap-southeast-1", options.Region);
        Assert.True(options.SkipAuth);
        Assert.True(options.ForceHttp1);
        Assert.Null(options.BearerToken);
    }

    [Fact]
    public async Task Google_reports_a_stream_that_ends_without_a_finish_reason()
    {
        var body = R4TestSupport.Data(new JsonObject
        {
            ["candidates"] = new JsonArray
            {
                (JsonNode?)new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            (JsonNode?)new JsonObject { ["text"] = "partial" },
                        },
                    },
                },
            },
        });
        var handler = new R4CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        using var httpClient = new HttpClient(handler);
        var result = await new GoogleGenerativeAiProvider(new ProviderHttpClient(httpClient)).Stream(
            R4TestSupport.Model(
                api: ApiNames.GoogleGenerativeAi,
                provider: "google",
                id: "gemini-2.5-flash",
                baseUrl: "https://generativelanguage.googleapis.com/v1beta"),
            R4TestSupport.UserContext(),
            new StreamOptions { ApiKey = "test-key" }).Result;

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Equal("Google stream ended without a finish reason", result.ErrorMessage);
    }

    [Fact]
    public async Task OpenAi_ignores_blank_SSE_data_records_before_the_terminal_event()
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(
            "data: \n\n" + R4TestSupport.OpenAiSse()));
        using var httpClient = new HttpClient(handler);
        var result = await new OpenAiResponsesProvider(new ProviderHttpClient(httpClient)).Stream(
            R4TestSupport.Model(),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions { ApiKey = "test-key" }).Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal("Hello", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    private static async Task<IReadOnlyList<SseEvent>> ReadSseAsync(Stream body)
    {
        var events = new List<SseEvent>();
        await foreach (var @event in SseReader.ReadAsync(body, TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        return events;
    }
}
