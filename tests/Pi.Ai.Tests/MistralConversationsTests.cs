using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class MistralConversationsTests
{
    [Fact]
    public async Task Serializes_sdk_payload_to_mistral_wire_names_and_runs_callbacks()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse(TerminalEvent("stop")) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        JsonObject? callbackPayload = null;
        ProviderResponse? callbackResponse = null;
        var model = ModelForMistral("mistral-large-latest");
        var context = new Context
        {
            SystemPrompt = "Be precise",
            Messages =
            [
                UserMessage.Blocks([new TextContent("describe"), new ImageContent("aGVsbG8=", "image/png")], 1),
            ],
            Tools =
            [
                new Tool
                {
                    Name = "lookup",
                    Description = "Look something up",
                    Parameters = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                    },
                },
            ],
        };

        var message = await provider.Stream(
                model,
                context,
                new MistralOptions
                {
                    ApiKey = "secret",
                    MaxTokens = 123,
                    PromptMode = "reasoning",
                    ReasoningEffort = "high",
                    ToolChoice = new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "lookup" },
                    },
                    SessionId = "session-1",
                    OnPayload = (payload, _) =>
                    {
                        callbackPayload = Assert.IsType<JsonObject>(payload);
                        callbackPayload["topP"] = 0.9;
                        callbackPayload["randomSeed"] = 42;
                        callbackPayload["presencePenalty"] = 0.1;
                        callbackPayload["frequencyPenalty"] = 0.2;
                        callbackPayload["parallelToolCalls"] = true;
                        callbackPayload["safePrompt"] = true;
                        callbackPayload["responseFormat"] = new JsonObject
                        {
                            ["type"] = "json_schema",
                            ["jsonSchema"] = new JsonObject
                            {
                                ["name"] = "result",
                                ["schemaDefinition"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject { ["maxTokens"] = new JsonObject { ["type"] = "number" } },
                                },
                            },
                        };
                        return ValueTask.FromResult<JsonNode?>(callbackPayload);
                    },
                    OnResponse = (response, _) =>
                    {
                        callbackResponse = response;
                        return ValueTask.CompletedTask;
                    },
                })
            .Result;

        Assert.Equal(StopReasons.Stop, message.StopReason);
        Assert.Equal(123, callbackPayload!["maxTokens"]!.GetValue<int>());
        Assert.Equal("reasoning", callbackPayload["promptMode"]!.GetValue<string>());
        Assert.Equal("session-1", callbackPayload["promptCacheKey"]!.GetValue<string>());
        Assert.Equal(200, callbackResponse!.Status);
        Assert.Equal("https://api.mistral.ai/v1/chat/completions", handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer secret", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Equal("text/event-stream", handler.LastRequest.Headers.Accept.Single().MediaType);
        Assert.Equal("session-1", handler.LastRequest.Headers.GetValues("x-affinity").Single());

        var wire = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal(123, wire["max_tokens"]!.GetValue<int>());
        Assert.Equal("reasoning", wire["prompt_mode"]!.GetValue<string>());
        Assert.Equal("high", wire["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("session-1", wire["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal(0.9, wire["top_p"]!.GetValue<double>());
        Assert.Equal(42, wire["random_seed"]!.GetValue<int>());
        Assert.Equal("json_schema", wire["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("object", wire["response_format"]!["json_schema"]!["schema"]!["type"]!.GetValue<string>());
        Assert.Null(wire["maxTokens"]);
        Assert.Equal("image_url", wire["messages"]![1]!["content"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Serializes_assistant_thinking_tool_calls_and_tool_results()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse(TerminalEvent("stop")) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var model = ModelForMistral("mistral-large-latest");
        await provider.Stream(
                model,
                new Context
                {
                    Messages =
                    [
                        new AssistantMessage
                        {
                            Api = model.Api,
                            Provider = model.Provider,
                            Model = model.Id,
                            Content =
                            [
                                new ThinkingContent("reason"),
                                new TextContent("answer"),
                                new ToolCall("abc123456", "lookup", new JsonObject { ["query"] = "pi" }),
                            ],
                            StopReason = StopReasons.ToolUse,
                            Timestamp = 1,
                        },
                        new ToolResultMessage
                        {
                            ToolCallId = "abc123456",
                            ToolName = "lookup",
                            Content = [new TextContent("found"), new ImageContent("aGVsbG8=", "image/png")],
                            Timestamp = 2,
                        },
                    ],
                },
                new MistralOptions { ApiKey = "test" })
            .Result;

        var wire = JsonNode.Parse(handler.LastBody!)!.AsObject();
        var messages = wire["messages"]!.AsArray();
        Assert.Equal("thinking", messages[0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("function", messages[0]!["tool_calls"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("{\"query\":\"pi\"}", messages[0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("abc123456", messages[1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("image_url", messages[1]!["content"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Parses_native_thinking_text_fragmented_tool_calls_and_cached_usage()
    {
        var model = ModelForMistral("mistral-large-latest");
        var events = new[]
        {
            new JsonObject
            {
                ["id"] = "response-1",
                ["choices"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["finish_reason"] = null,
                        ["delta"] = new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                (JsonNode?)new JsonObject
                                {
                                    ["type"] = "thinking",
                                    ["thinking"] = new JsonArray
                                    {
                                        (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = "reason" },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            new JsonObject
            {
                ["id"] = "response-1",
                ["choices"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["finish_reason"] = null,
                        ["delta"] = new JsonObject { ["content"] = new JsonArray { (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = "answer" } } },
                    },
                },
            },
            new JsonObject
            {
                ["id"] = "response-1",
                ["choices"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["finish_reason"] = null,
                        ["delta"] = new JsonObject
                        {
                            ["tool_calls"] = new JsonArray
                            {
                                (JsonNode?)new JsonObject
                                {
                                    ["id"] = "abc123456",
                                    ["index"] = 0,
                                    ["function"] = new JsonObject { ["name"] = "lookup", ["arguments"] = "{\"query\":" },
                                },
                            },
                        },
                    },
                },
            },
            new JsonObject
            {
                ["id"] = "response-1",
                ["choices"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["finish_reason"] = "tool_calls",
                        ["delta"] = new JsonObject
                        {
                            ["tool_calls"] = new JsonArray
                            {
                                (JsonNode?)new JsonObject
                                {
                                    ["index"] = 0,
                                    ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "\"pi\"}" },
                                },
                            },
                        },
                    },
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 10,
                    ["completion_tokens"] = 4,
                    ["total_tokens"] = 14,
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 3 },
                },
            },
        };
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n\n", events.Select(Sse)) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(model, new Context(), new MistralOptions { ApiKey = "test" });
        var received = await CollectAsync(stream);
        var result = await stream.Result;

        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Equal("tool_calls", result.RawStopReason);
        Assert.Equal("response-1", result.ResponseId);
        Assert.Equal("reason", Assert.IsType<ThinkingContent>(result.Content[0]).Thinking);
        Assert.Equal("answer", Assert.IsType<TextContent>(result.Content[1]).Text);
        var tool = Assert.IsType<ToolCall>(result.Content[2]);
        Assert.Equal("abc123456", tool.Id);
        Assert.Equal("pi", tool.Arguments["query"]!.GetValue<string>());
        Assert.Equal(7, result.Usage.Input);
        Assert.Equal(4, result.Usage.Output);
        Assert.Equal(3, result.Usage.CacheRead);
        Assert.Equal(14, result.Usage.TotalTokens);
        Assert.Contains(received, static @event => @event is ThinkingEndEvent);
        Assert.Contains(received, static @event => @event is ToolCallEndEvent);
        Assert.IsType<StreamDoneEvent>(received[^1]);
    }

    [Fact]
    public async Task Normalizes_foreign_tool_ids_and_resolves_reasoning_modes()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse(TerminalEvent("stop")) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var model = ModelForMistral("mistral-small-2603") with { Reasoning = true };
        await provider.StreamSimple(
                model,
                new Context
                {
                    Messages =
                    [
                        new AssistantMessage
                        {
                            Api = ApiNames.OpenAiCompletions,
                            Provider = "openai",
                            Model = "other",
                            StopReason = StopReasons.ToolUse,
                            Content = [new ToolCall("bad|id", "echo", new JsonObject { ["value"] = "x" })],
                            Timestamp = 1,
                        },
                        new ToolResultMessage
                        {
                            ToolCallId = "bad|id",
                            ToolName = "echo",
                            Content = [new TextContent("ok")],
                            Timestamp = 2,
                        },
                    ],
                },
                new SimpleStreamOptions { ApiKey = "test", Reasoning = ThinkingLevels.Medium, SessionId = "session" })
            .Result;

        var wire = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("high", wire["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("session", wire["prompt_cache_key"]!.GetValue<string>());
        var id = wire["messages"]![0]!["tool_calls"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(9, id.Length);
        Assert.DoesNotContain("|", id);
        Assert.Equal(id, wire["messages"]![1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Preserves_status_body_and_raw_provider_finish_reasons()
    {
        using var errorHandler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"blocked by gateway\"}", Encoding.UTF8, "application/json"),
        });
        var errorProvider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(errorHandler)));
        var error = await errorProvider.Stream(ModelForMistral("mistral-large-latest"), new Context(), new MistralOptions { ApiKey = "test" }).Result;
        Assert.Equal("Mistral API error (403): {\"message\":\"blocked by gateway\"}", error.ErrorMessage);

        using var streamHandler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse(TerminalEvent("unmapped_error")) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(streamHandler)));
        var result = await provider.Stream(ModelForMistral("mistral-large-latest"), new Context(), new MistralOptions { ApiKey = "test" }).Result;
        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Equal("unmapped_error", result.RawStopReason);
        Assert.Equal("Provider stopped with: unmapped_error", result.ErrorMessage);
    }

    [Fact]
    public async Task Preserves_utf8_when_sse_bytes_arrive_one_at_a_time()
    {
        var model = ModelForMistral("mistral-large-latest");
        var eventText = Sse(new JsonObject
        {
            ["id"] = "bytewise",
            ["choices"] = new JsonArray
            {
                (JsonNode?)new JsonObject
                {
                    ["finish_reason"] = "stop",
                    ["delta"] = new JsonObject { ["content"] = "héllo 🌍" },
                },
            },
        }) + "\r\n\r\ndata: [DONE]\r\n\r\n";
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BytewiseStream(Encoding.UTF8.GetBytes(eventText))),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var result = await provider.Stream(model, new Context(), new MistralOptions { ApiKey = "test" }).Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal("héllo 🌍", Assert.IsType<TextContent>(result.Content[0]).Text);
    }

    [Fact]
    public async Task Applies_timeout_while_waiting_for_the_next_sse_record()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverEndingStream()),
        });
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var result = await provider.Stream(
                ModelForMistral("mistral-large-latest"),
                new Context(),
                new MistralOptions { ApiKey = "test", TimeoutMs = 20 })
            .Result;

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("timeout", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_missing_auth_without_sending_a_request()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new MistralConversationsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var result = await provider.Stream(ModelForMistral("mistral-large-latest"), new Context()).Result;

        Assert.Contains("No API key for provider: mistral", result.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    private static Model ModelForMistral(string id) => new()
    {
        Id = id,
        Name = id,
        Api = ApiNames.MistralConversations,
        Provider = "mistral",
        BaseUrl = "https://api.mistral.ai",
        Input = ["text", "image"],
        MaxTokens = 8192,
    };

    private static JsonObject TerminalEvent(string finishReason) => new()
    {
        ["id"] = "mistral-response-id",
        ["model"] = "mistral-large-latest",
        ["choices"] = new JsonArray
        {
            (JsonNode?)new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = finishReason,
                ["delta"] = new JsonObject(),
            },
        },
        ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1, ["total_tokens"] = 2 },
    };

    private static string Sse(JsonObject data) => $"data: {data.ToJsonString()}";

    private static async Task<List<AssistantMessageEvent>> CollectAsync(AssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        return events;
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return responder(request);
        }
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitAsync(cancellationToken));

        private static async Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class BytewiseStream(byte[] bytes) : Stream
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
            if (_offset >= bytes.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = bytes[_offset++];
            return ValueTask.FromResult(1);
        }
    }
}
