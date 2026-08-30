using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.AgentCore.Tests;

public sealed class ProxyTests
{
    [Fact(DisplayName = "preserves tool-call metadata received only on toolcall_end")]
    public async Task Preserves_tool_call_metadata_received_only_on_toolcall_end()
    {
        var body = Sse(
            new JsonObject { ["type"] = "start" },
            new JsonObject
            {
                ["type"] = "toolcall_start",
                ["contentIndex"] = 0,
                ["id"] = "call_test|fc_test",
                ["toolName"] = "lookup",
            },
            new JsonObject
            {
                ["type"] = "toolcall_delta",
                ["contentIndex"] = 0,
                ["delta"] = "{\"value\":\"hello\"}",
            },
            new JsonObject
            {
                ["type"] = "toolcall_end",
                ["contentIndex"] = 0,
                ["toolCall"] = new JsonObject
                {
                    ["type"] = "toolCall",
                    ["id"] = "call_test|fc_test",
                    ["name"] = "lookup",
                    ["arguments"] = new JsonObject { ["value"] = "hello" },
                    ["namespace"] = "dynamic_tools",
                },
            },
            new JsonObject
            {
                ["type"] = "done",
                ["reason"] = "toolUse",
                ["usage"] = UsageJson(),
            });

        var collected = await ReadAsync(Start(body));
        var endEvent = Assert.Single(collected.Events.OfType<ToolCallEndEvent>());

        Assert.Equal("dynamic_tools", endEvent.ToolCall.Namespace);
        var toolCall = Assert.IsType<ToolCall>(collected.Result.Content[0]);
        Assert.Equal("hello", toolCall.Arguments["value"]!.GetValue<string>());
        Assert.Equal("dynamic_tools", toolCall.Namespace);
    }

    [Fact(DisplayName = "streams text, thinking and tool-call content in order and forwards completion usage")]
    public async Task Streams_text_thinking_and_tool_call_content_in_order_and_forwards_completion_usage()
    {
        var body = Sse(
            new JsonObject { ["type"] = "start" },
            new JsonObject { ["type"] = "text_start", ["contentIndex"] = 0 },
            new JsonObject { ["type"] = "text_delta", ["contentIndex"] = 0, ["delta"] = "Hello " },
            new JsonObject { ["type"] = "text_delta", ["contentIndex"] = 0, ["delta"] = "world" },
            new JsonObject
            {
                ["type"] = "text_end",
                ["contentIndex"] = 0,
                ["contentSignature"] = "text-signature",
            },
            new JsonObject { ["type"] = "thinking_start", ["contentIndex"] = 1 },
            new JsonObject { ["type"] = "thinking_delta", ["contentIndex"] = 1, ["delta"] = "Reason" },
            new JsonObject
            {
                ["type"] = "thinking_end",
                ["contentIndex"] = 1,
                ["contentSignature"] = "thinking-signature",
            },
            new JsonObject
            {
                ["type"] = "toolcall_start",
                ["contentIndex"] = 2,
                ["id"] = "call-1",
                ["toolName"] = "lookup",
            },
            new JsonObject
            {
                ["type"] = "toolcall_delta",
                ["contentIndex"] = 2,
                ["delta"] = "{\"value\":",
            },
            new JsonObject
            {
                ["type"] = "toolcall_delta",
                ["contentIndex"] = 2,
                ["delta"] = "\"hello\"}",
            },
            new JsonObject
            {
                ["type"] = "toolcall_end",
                ["contentIndex"] = 2,
                ["toolCall"] = new JsonObject
                {
                    ["type"] = "toolCall",
                    ["id"] = "call-1",
                    ["name"] = "lookup",
                    ["arguments"] = new JsonObject { ["value"] = "hello" },
                },
            },
            new JsonObject
            {
                ["type"] = "done",
                ["reason"] = "toolUse",
                ["usage"] = UsageJson(input: 4, output: 7, cacheRead: 2, cacheWrite: 3),
            });

        var collected = await ReadAsync(Start(body));

        Assert.Equal(
            [
                "start",
                "text_start",
                "text_delta",
                "text_delta",
                "text_end",
                "thinking_start",
                "thinking_delta",
                "thinking_end",
                "toolcall_start",
                "toolcall_delta",
                "toolcall_delta",
                "toolcall_end",
                "done",
            ],
            collected.Events.Select(static @event => @event.Type));

        var text = Assert.IsType<TextContent>(collected.Result.Content[0]);
        Assert.Equal("Hello world", text.Text);
        Assert.Equal("text-signature", text.TextSignature);

        var thinking = Assert.IsType<ThinkingContent>(collected.Result.Content[1]);
        Assert.Equal("Reason", thinking.Thinking);
        Assert.Equal("thinking-signature", thinking.ThinkingSignature);

        var toolCall = Assert.IsType<ToolCall>(collected.Result.Content[2]);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("lookup", toolCall.Name);
        Assert.Equal("hello", toolCall.Arguments["value"]!.GetValue<string>());
        Assert.Equal(StopReasons.ToolUse, collected.Result.StopReason);
        Assert.Equal(4, collected.Result.Usage.Input);
        Assert.Equal(7, collected.Result.Usage.Output);
        Assert.Equal(2, collected.Result.Usage.CacheRead);
        Assert.Equal(3, collected.Result.Usage.CacheWrite);
    }

    [Fact(DisplayName = "reconstructs streamed events across UTF-8 and line boundaries")]
    public async Task Reconstructs_streamed_events_across_utf8_and_line_boundaries()
    {
        var body = "data: {\"type\":\"start\"}\n\n" +
                   "data: {\"type\":\"text_start\",\"contentIndex\":0}\n\n" +
                   "data: {\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"🌍\"}\n\n" +
                   "data: {\"type\":\"text_end\",\"contentIndex\":0,\"contentSignature\":\"sig\"}\n\n" +
                   $"data: {{\"type\":\"done\",\"reason\":\"stop\",\"usage\":{UsageJson().ToJsonString()}}}\n\n";
        using var responseBody = new ChunkedReadStream(Encoding.UTF8.GetBytes(body));

        var stream = Proxy.StreamProxy(
            Model(),
            new Context(),
            new ProxyStreamOptions
            {
                AuthToken = "test-token",
                ProxyUrl = "https://proxy.example.com",
                Fetch = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(responseBody),
                }),
            });

        var collected = await ReadAsync(stream);

        var text = Assert.IsType<TextContent>(collected.Result.Content[0]);
        Assert.Equal("🌍", text.Text);
        Assert.Equal("sig", text.TextSignature);
        Assert.Equal(StopReasons.Stop, collected.Result.StopReason);
    }

    [Fact(DisplayName = "sends the upstream proxy envelope and serializable stream options")]
    public async Task Sends_the_upstream_proxy_envelope_and_serializable_stream_options()
    {
        var request = new TaskCompletionSource<RequestSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = Sse(
            new JsonObject
            {
                ["type"] = "done",
                ["reason"] = "stop",
                ["usage"] = UsageJson(),
            });

        var options = new ProxyStreamOptions
        {
            AuthToken = "test-token",
            ProxyUrl = "https://proxy.example.com",
            Temperature = 0.2,
            SamplingParameters = new Dictionary<string, JsonNode?> { ["top_p"] = 0.9 },
            MaxTokens = 77,
            Reasoning = "high",
            CacheRetention = "short",
            SessionId = "session-1",
            Headers = new Dictionary<string, string?>
            {
                ["X-Mode"] = "test",
                ["X-Optional"] = null,
            },
            Metadata = new Dictionary<string, JsonNode?> { ["traceId"] = "trace-1" },
            Transport = "sse",
            ThinkingBudgets = new ThinkingBudgets
            {
                Minimal = 10,
                Low = 20,
                Medium = 30,
                High = 40,
            },
            MaxRetryDelayMs = 500,
            Fetch = async (incoming, cancellationToken) =>
            {
                var content = incoming.Content is null
                    ? string.Empty
                    : await incoming.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                request.TrySetResult(new RequestSnapshot(
                    incoming.Method,
                    incoming.RequestUri,
                    incoming.Headers.Authorization?.Scheme,
                    incoming.Headers.Authorization?.Parameter,
                    incoming.Content?.Headers.ContentType?.MediaType,
                    content));
                return EventResponse(body);
            },
        };

        var collected = await ReadAsync(Proxy.StreamProxy(Model(), new Context { SystemPrompt = "system" }, options));
        var captured = await request.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(new Uri("https://proxy.example.com/api/stream"), captured.RequestUri);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("test-token", captured.AuthorizationParameter);
        Assert.Equal("application/json", captured.ContentType);
        Assert.Equal(StopReasons.Stop, collected.Result.StopReason);

        var root = JsonNode.Parse(captured.Body)!.AsObject();
        Assert.Equal(["model", "context", "options"], root.Select(static property => property.Key));
        Assert.Equal("gpt-5.4", root["model"]!["id"]!.GetValue<string>());
        Assert.Equal("system", root["context"]!["systemPrompt"]!.GetValue<string>());

        var serializedOptions = root["options"]!.AsObject();
        Assert.Equal(
            [
                "temperature",
                "samplingParams",
                "maxTokens",
                "reasoning",
                "cacheRetention",
                "sessionId",
                "headers",
                "metadata",
                "transport",
                "thinkingBudgets",
                "maxRetryDelayMs",
            ],
            serializedOptions.Select(static property => property.Key));
        Assert.Equal(0.2, serializedOptions["temperature"]!.GetValue<double>());
        Assert.Equal(77, serializedOptions["maxTokens"]!.GetValue<int>());
        Assert.Equal("high", serializedOptions["reasoning"]!.GetValue<string>());
        Assert.Equal("trace-1", serializedOptions["metadata"]!["traceId"]!.GetValue<string>());
        Assert.True(serializedOptions["headers"]!.AsObject().ContainsKey("X-Optional"));
        Assert.Null(serializedOptions["headers"]!["X-Optional"]);
        Assert.Equal(40, serializedOptions["thinkingBudgets"]!["high"]!.GetValue<int>());
    }

    [Fact(DisplayName = "ignores non-data lines, blank data and unknown proxy events")]
    public async Task Ignores_non_data_lines_blank_data_and_unknown_proxy_events()
    {
        var body = ": heartbeat\n" +
                   "event: ignored\n" +
                   "data: \n" +
                   "data: {\"type\":\"future_event\"}\n\n" +
                   Sse(
                       new JsonObject
                       {
                           ["type"] = "done",
                           ["reason"] = "stop",
                           ["usage"] = UsageJson(),
                       });

        var collected = await ReadAsync(Start(body));

        var completed = Assert.Single(collected.Events);
        Assert.IsType<StreamDoneEvent>(completed);
        Assert.Equal(StopReasons.Stop, collected.Result.StopReason);
    }

    [Fact(DisplayName = "propagates proxy error events as terminal errors")]
    public async Task Propagates_proxy_error_events_as_terminal_errors()
    {
        var body = Sse(
            new JsonObject
            {
                ["type"] = "error",
                ["reason"] = "error",
                ["errorMessage"] = "provider rejected the request",
                ["usage"] = UsageJson(input: 3, output: 1),
            });

        var collected = await ReadAsync(Start(body));
        var errorEvent = Assert.Single(collected.Events.OfType<StreamErrorEvent>());

        Assert.Equal(StopReasons.Error, errorEvent.Reason);
        Assert.Equal("provider rejected the request", errorEvent.Error.ErrorMessage);
        Assert.Equal(3, errorEvent.Error.Usage.Input);
        Assert.Equal(1, errorEvent.Error.Usage.Output);
        Assert.Same(errorEvent.Error, collected.Result);
    }

    [Fact(DisplayName = "propagates proxy HTTP error bodies as stream errors")]
    public async Task Propagates_proxy_http_error_bodies_as_stream_errors()
    {
        var stream = Proxy.StreamProxy(
            Model(),
            new Context(),
            Options((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid token\"}", Encoding.UTF8, "application/json"),
            })));

        var collected = await ReadAsync(stream);
        var errorEvent = Assert.Single(collected.Events.OfType<StreamErrorEvent>());

        Assert.Equal(StopReasons.Error, errorEvent.Reason);
        Assert.Equal("Proxy error: invalid token", errorEvent.Error.ErrorMessage);
        Assert.Equal(StopReasons.Error, collected.Result.StopReason);
    }

    [Fact(DisplayName = "falls back to proxy status text when the error body is not JSON")]
    public async Task Falls_back_to_proxy_status_text_when_the_error_body_is_not_json()
    {
        var stream = Proxy.StreamProxy(
            Model(),
            new Context(),
            Options((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                ReasonPhrase = "Bad Gateway",
                Content = new StringContent("not json", Encoding.UTF8, "text/plain"),
            })));

        var collected = await ReadAsync(stream);
        var errorEvent = Assert.Single(collected.Events.OfType<StreamErrorEvent>());

        Assert.Equal("Proxy error: 502 Bad Gateway", errorEvent.Error.ErrorMessage);
    }

    [Fact(DisplayName = "propagates transport exceptions as stream errors")]
    public async Task Propagates_transport_exceptions_as_stream_errors()
    {
        var stream = Proxy.StreamProxy(
            Model(),
            new Context(),
            Options((_, _) => Task.FromException<HttpResponseMessage>(new InvalidOperationException("transport failed"))));

        var collected = await ReadAsync(stream);
        var errorEvent = Assert.Single(collected.Events.OfType<StreamErrorEvent>());

        Assert.Equal(StopReasons.Error, errorEvent.Reason);
        Assert.Equal("transport failed", errorEvent.Error.ErrorMessage);
    }

    [Fact(DisplayName = "reports malformed proxy events as stream errors")]
    public async Task Reports_malformed_proxy_events_as_stream_errors()
    {
        var body = "data: {not-json}\n\n";
        var collected = await ReadAsync(Start(body));
        var errorEvent = Assert.Single(collected.Events.OfType<StreamErrorEvent>());

        Assert.Equal(StopReasons.Error, errorEvent.Reason);
        Assert.NotNull(errorEvent.Error.ErrorMessage);
        Assert.NotEmpty(errorEvent.Error.ErrorMessage);
    }

    [Fact(DisplayName = "reports cancellation as an aborted stream error")]
    public async Task Reports_cancellation_as_an_aborted_stream_error()
    {
        using var cancellation = new CancellationTokenSource();
        using var responseBody = new CancellationControlledStream(Encoding.UTF8.GetBytes("data: {\"type\":\"start\"}\n\n"));

        var stream = Proxy.StreamProxy(
            Model(),
            new Context(),
            new ProxyStreamOptions
            {
                AuthToken = "test-token",
                ProxyUrl = "https://proxy.example.com",
                Signal = cancellation.Token,
                Fetch = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(responseBody),
                }),
            });

        await responseBody.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        responseBody.Release();

        var collected = await ReadAsync(stream);
        var errorEvent = Assert.Single(collected.Events.OfType<StreamErrorEvent>());

        Assert.Equal(StopReasons.Aborted, errorEvent.Reason);
        Assert.Equal(StopReasons.Aborted, errorEvent.Error.StopReason);
        Assert.Equal("Request aborted by user", errorEvent.Error.ErrorMessage);
    }

    private static AssistantMessageEventStream Start(string body) =>
        Proxy.StreamProxy(Model(), new Context(), Options((_, _) => Task.FromResult(EventResponse(body))));

    private static ProxyStreamOptions Options(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fetch) => new()
        {
            AuthToken = "test-token",
            ProxyUrl = "https://proxy.example.com",
            Fetch = fetch,
        };

    private static Model Model() => new()
    {
        Id = "gpt-5.4",
        Name = "GPT-5.4",
        Api = ApiNames.OpenAiResponses,
        Provider = "openai",
        BaseUrl = "https://api.openai.com/v1",
        Reasoning = true,
        Input = ["text"],
        Cost = new ModelCost(),
        ContextWindow = 400000,
        MaxTokens = 128000,
    };

    private static JsonObject UsageJson(
        int input = 0,
        int output = 0,
        int cacheRead = 0,
        int cacheWrite = 0) => new()
        {
            ["input"] = input,
            ["output"] = output,
            ["cacheRead"] = cacheRead,
            ["cacheWrite"] = cacheWrite,
            ["totalTokens"] = input + output + cacheRead + cacheWrite,
            ["cost"] = new JsonObject
            {
                ["input"] = 0,
                ["output"] = 0,
                ["cacheRead"] = 0,
                ["cacheWrite"] = 0,
                ["total"] = 0,
            },
        };

    private static string Sse(params JsonObject[] events) =>
        string.Concat(events.Select(static @event => $"data: {@event.ToJsonString()}\n\n"));

    private static HttpResponseMessage EventResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async Task<CollectedStream> ReadAsync(AssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        return new CollectedStream(events, await stream.Result.ConfigureAwait(false));
    }

    private sealed record CollectedStream(IReadOnlyList<AssistantMessageEvent> Events, AssistantMessage Result);

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? ContentType,
        string Body);

    private sealed class CancellationControlledStream(byte[] firstChunk) : Stream
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => firstChunk.Length;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            firstChunk.AsSpan().CopyTo(buffer.Span);
            return firstChunk.Length;
        }
    }

    private sealed class ChunkedReadStream(byte[] bytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= bytes.Length)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = bytes[_offset++];
            return ValueTask.FromResult(1);
        }
    }
}
