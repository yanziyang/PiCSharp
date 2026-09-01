using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

/// <summary>Shared deterministic fixtures for the R4 transport and resilience ports.</summary>
internal static class R4TestSupport
{
    public static Model Model(
        string api = ApiNames.OpenAiResponses,
        string provider = "openai",
        string id = "gpt-test",
        string baseUrl = "https://api.openai.com/v1",
        bool reasoning = false,
        int contextWindow = 128_000,
        int maxTokens = 4_096,
        JsonObject? compatibility = null) => new()
        {
            Id = id,
            Name = id,
            Api = api,
            Provider = provider,
            BaseUrl = baseUrl,
            Reasoning = reasoning,
            Input = ["text"],
            Cost = new ModelCost(),
            ContextWindow = contextWindow,
            MaxTokens = maxTokens,
            Compatibility = compatibility,
        };

    public static Context UserContext(string text = "hello") => new()
    {
        Messages = [UserMessage.Text(text, 1)],
    };

    public static AssistantMessage Assistant(
        string text = "",
        string stopReason = StopReasons.Stop,
        string? errorMessage = null,
        int input = 0,
        int output = 0,
        int cacheRead = 0,
        int cacheWrite = 0) => new()
        {
            Api = ApiNames.OpenAiResponses,
            Provider = ProviderNames.Faux,
            Model = "faux-1",
            Content = string.IsNullOrEmpty(text) ? [] : [new TextContent(text)],
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            Timestamp = 1,
            Usage = new Usage
            {
                Input = input,
                Output = output,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                TotalTokens = input + output + cacheRead + cacheWrite,
            },
        };

    public static AssistantMessage LengthMessage(
        int input,
        int cacheRead,
        int output,
        int cacheWrite = 0,
        string api = ApiNames.OpenAiResponses,
        string provider = "openai",
        string model = "gpt-test") => new()
        {
            Api = api,
            Provider = provider,
            Model = model,
            Content = [],
            StopReason = StopReasons.Length,
            Timestamp = 1,
            Usage = new Usage
            {
                Input = input,
                CacheRead = cacheRead,
                Output = output,
                CacheWrite = cacheWrite,
                TotalTokens = input + cacheRead + output + cacheWrite,
            },
        };

    public static string OpenAiSse(string status = "completed", bool includeDone = false, string delta = "Hello")
    {
        var events = new List<string>
        {
            Data(new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = new JsonObject { ["id"] = "resp_1" },
            }),
            Data(new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = 0,
                ["item"] = new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = "msg_1",
                    ["role"] = "assistant",
                    ["status"] = "in_progress",
                    ["content"] = new JsonArray(),
                },
            }),
            Data(new JsonObject
            {
                ["type"] = "response.output_text.delta",
                ["output_index"] = 0,
                ["delta"] = delta,
            }),
            Data(new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 0,
                ["item"] = new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = "msg_1",
                    ["role"] = "assistant",
                    ["status"] = "completed",
                    ["content"] = new JsonArray
                    {
                        (JsonNode?)new JsonObject { ["type"] = "output_text", ["text"] = delta },
                    },
                },
            }),
        };

        var terminal = new JsonObject
        {
            ["type"] = status == "incomplete" ? "response.incomplete" : "response.completed",
            ["response"] = new JsonObject
            {
                ["id"] = "resp_1",
                ["status"] = status,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 5,
                    ["output_tokens"] = 3,
                    ["total_tokens"] = 8,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                },
            },
        };
        if (status == "incomplete")
        {
            terminal["response"]!["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
        }

        events.Add(Data(terminal));
        if (includeDone)
        {
            events.Add("data: [DONE]\n\n");
        }

        return string.Concat(events);
    }

    public static HttpResponseMessage SseResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    public static HttpResponseMessage JsonResponse(string body, HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    public static string Data(JsonObject value) => $"data: {value.ToJsonString()}\n\n";

    public static async Task<(IReadOnlyList<AssistantMessageEvent> Events, AssistantMessage Result)> DrainAsync(
        AssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var item in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return (events, await stream.Result);
    }

    public static Task<HttpResponseMessage> SendWithoutCancellationAsync(
        ProviderHttpClient client,
        Model model,
        HttpMethod method,
        Uri uri,
        JsonNode? payload,
        ProviderRequestOptions options) =>
        client.SendAsync(model, method, uri, payload, options, cancellationToken: CancellationToken.None);

    public static BedrockConverseResponse BedrockResponse(params BedrockConverseEvent[] events) => new()
    {
        Status = 200,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amzn-requestid"] = "req-r4",
        },
        RequestId = "req-r4",
        Events = ToAsync(events),
    };

    private static async IAsyncEnumerable<BedrockConverseEvent> ToAsync(IEnumerable<BedrockConverseEvent> events)
    {
        foreach (var item in events)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}

/// <summary>Captures an HTTP request without opening a socket.</summary>
internal sealed class R4CapturingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _requestCount;

    public R4CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => responder(request))
    {
    }

    public R4CapturingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<R4CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Headers)
        {
            headers[pair.Key] = FormatHeaderValue(pair.Key, pair.Value);
        }

        if (request.Content is not null)
        {
            foreach (var pair in request.Content.Headers)
            {
                headers[pair.Key] = FormatHeaderValue(pair.Key, pair.Value);
            }
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new R4CapturedRequest(request.Method, request.RequestUri!, headers, body));
        return _responder(request, ++_requestCount);
    }

    private static string FormatHeaderValue(string name, IEnumerable<string> values) =>
        string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase)
            ? string.Join(" ", values)
            : string.Join(", ", values);
}

internal sealed record R4CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Captures calls made through the Cloudflare gateway binding transport.</summary>
internal sealed class R4CloudflareBinding : ICloudflareAiGatewayBinding
{
    public HttpResponseMessage? Response { get; init; }

    public List<R4GatewayRun> Runs { get; } = [];

    public ICloudflareAiGateway Gateway(string id) => new GatewayImplementation(this, id);

    private sealed class GatewayImplementation(R4CloudflareBinding owner, string id) : ICloudflareAiGateway
    {
        public Task<HttpResponseMessage> RunAsync(
            CloudflareAiGatewayUniversalRequest request,
            CancellationToken cancellationToken = default)
        {
            owner.Runs.Add(new R4GatewayRun(id, request, cancellationToken));
            return Task.FromResult(owner.Response ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

internal sealed record R4GatewayRun(
    string GatewayId,
    CloudflareAiGatewayUniversalRequest Request,
    CancellationToken CancellationToken);

/// <summary>Captures the public options sent to an injected Bedrock transport.</summary>
internal sealed class R4BedrockTransport : IBedrockConverseTransport
{
    public BedrockConverseResponse? Response { get; init; }

    public Exception? Error { get; init; }

    public JsonObject? Payload { get; private set; }

    public BedrockTransportOptions? Options { get; private set; }

    public int Calls { get; private set; }

    public Task<BedrockConverseResponse> SendAsync(
        JsonObject payload,
        BedrockTransportOptions options,
        CancellationToken cancellationToken)
    {
        Calls++;
        Payload = payload.DeepClone().AsObject();
        Options = options;
        if (Error is not null)
        {
            return Task.FromException<BedrockConverseResponse>(Error);
        }

        return Task.FromResult(Response ?? throw new InvalidOperationException("R4 Bedrock response was not configured."));
    }
}
