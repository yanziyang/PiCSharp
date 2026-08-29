using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Decodes_sse_line_endings_comments_multiline_data_and_eof_flush()
    {
        const string body = ": heartbeat\r\nevent: message\r\ndata: first\r\ndata: second\r\n\r\nevent: done\ndata: final";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var events = new List<SseEvent>();

        await foreach (var @event in SseReader.ReadAsync(input, TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal("message", events[0].Event);
        Assert.Equal("first\nsecond", events[0].Data);
        Assert.Equal([": heartbeat", "event: message", "data: first", "data: second"], events[0].RawLines);
        Assert.Equal("done", events[1].Event);
        Assert.Equal("final", events[1].Data);
    }

    [Fact]
    public async Task Sends_json_with_header_precedence_and_invokes_response_callback()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok", Encoding.UTF8, "application/json"),
        });
        var client = new ProviderHttpClient(new HttpClient(handler));
        var callback = (ProviderResponse?)null;
        var model = new Model
        {
            Id = "test-model",
            Name = "Test model",
            Api = ApiNames.OpenAiCompletions,
            Provider = "test",
            BaseUrl = "https://example.test",
            Headers = new Dictionary<string, string> { ["X-Model"] = "model" },
        };

        using var response = await client.SendAsync(
            model,
            HttpMethod.Post,
            new Uri("https://example.test/v1/chat/completions"),
            new JsonObject { ["model"] = "test-model" },
            new ProviderRequestOptions
            {
                ApiKey = "secret",
                Headers = new Dictionary<string, string?>
                {
                    ["X-Model"] = null,
                    ["X-Caller"] = "caller",
                },
                OnResponse = (metadata, _) =>
                {
                    callback = metadata;
                    return ValueTask.CompletedTask;
                },
            },
            new Dictionary<string, string?> { ["Accept"] = "text/event-stream" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, callback!.Status);
        Assert.Equal("secret", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.False(handler.LastRequest.Headers.Contains("X-Model"));
        Assert.Equal("caller", handler.LastRequest.Headers.GetValues("X-Caller").Single());
        Assert.Equal("text/event-stream", handler.LastRequest.Headers.GetValues("Accept").Single());
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Captures_non_success_status_and_response_body_without_sdk_dependencies()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":\"blocked\"}", Encoding.UTF8, "application/json"),
        });
        var client = new ProviderHttpClient(new HttpClient(handler));
        var model = TestModel();

        var error = await Assert.ThrowsAsync<ProviderErrorMetadataException>(async () =>
            await client.SendAsync(
                model,
                HttpMethod.Post,
                new Uri("https://example.test/v1"),
                new JsonObject { ["hello"] = "world" },
                new ProviderRequestOptions { ApiKey = "secret" },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(403, error.Metadata.Status);
        Assert.Equal("{\"error\":\"blocked\"}", error.Metadata.ResponseBody);
        Assert.Equal("403 status code (no body)", error.Message);
    }

    [Fact]
    public async Task Applies_request_timeout_to_custom_transport_delegate()
    {
        var model = TestModel();
        var client = new ProviderHttpClient();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.SendAsync(
                model,
                HttpMethod.Post,
                new Uri("https://example.test/v1"),
                new JsonObject(),
                new ProviderRequestOptions
                {
                    TimeoutMs = 10,
                    Fetch = async (_, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    },
                },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error is TaskCanceledException or OperationCanceledException);
    }

    private static Model TestModel() => new()
    {
        Id = "test-model",
        Name = "Test model",
        Api = ApiNames.OpenAiCompletions,
        Provider = "test",
        BaseUrl = "https://example.test",
    };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }
}
