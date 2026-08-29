using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class OpenAiResponsesTests
{
    [Fact]
    public void Builds_responses_payload_with_reasoning_cache_tools_and_multimodal_history()
    {
        var model = ModelForResponses() with
        {
            Reasoning = true,
            Input = ["text", "image"],
            Compatibility = new JsonObject
            {
                ["supportsDeveloperRole"] = true,
                ["supportsStrictMode"] = true,
                ["supportsLongCacheRetention"] = true,
            },
        };
        var context = new Context
        {
            SystemPrompt = "Be concise.",
            Messages =
            [
                UserMessage.Blocks([new TextContent("Inspect"), new ImageContent("AQI=", "image/png")], 1),
                new AssistantMessage
                {
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    StopReason = StopReasons.ToolUse,
                    Content =
                    [
                        new ThinkingContent("private", new JsonObject
                        {
                            ["type"] = "reasoning",
                            ["id"] = "rs_1",
                            ["summary"] = new JsonArray(),
                        }.ToJsonString()),
                        new TextContent("answer", "{\"v\":1,\"id\":\"msg_1\",\"phase\":\"final_answer\"}"),
                        new ToolCall("call_1|fc_1", "read", new JsonObject { ["path"] = "README.md" }),
                    ],
                    Timestamp = 2,
                },
                new ToolResultMessage
                {
                    ToolCallId = "call_1|fc_1",
                    ToolName = "read",
                    Content = [new TextContent("contents")],
                    Timestamp = 3,
                },
            ],
            Tools =
            [
                new Tool
                {
                    Name = "read",
                    Description = "Read a file.",
                    Parameters = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                    },
                    ConstrainedSampling = new JsonSchemaSampling("prefer"),
                },
            ],
        };

        var payload = OpenAiResponsesProvider.BuildPayload(
            model,
            context,
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "key",
                MaxTokens = 8,
                ReasoningEffort = ThinkingLevels.High,
                ReasoningSummary = "detailed",
                ServiceTier = "priority",
                ToolChoice = JsonValue.Create("required"),
                CacheRetention = CacheRetentions.Long,
                SessionId = new string('x', 70),
            });

        Assert.Equal(16, payload["max_output_tokens"]!.GetValue<int>());
        Assert.Equal("priority", payload["service_tier"]!.GetValue<string>());
        Assert.Equal("high", payload["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("detailed", payload["reasoning"]!["summary"]!.GetValue<string>());
        Assert.Equal(new string('x', 64), payload["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal("24h", payload["prompt_cache_retention"]!.GetValue<string>());
        Assert.Equal("required", payload["tool_choice"]!.GetValue<string>());
        Assert.Equal("developer", payload["input"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("input_image", payload["input"]![1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("reasoning", payload["input"]![2]!["type"]!.GetValue<string>());
        Assert.Equal("msg_1", payload["input"]![3]!["id"]!.GetValue<string>());
        Assert.Equal("function_call", payload["input"]![4]!["type"]!.GetValue<string>());
        Assert.Equal("fc_1", payload["input"]![4]!["id"]!.GetValue<string>());
        Assert.Equal("function_call_output", payload["input"]![5]!["type"]!.GetValue<string>());
        Assert.True(payload["tools"]![0]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public void Uses_system_role_and_explicit_cache_mode_when_reasoning_or_cache_is_disabled()
    {
        var model = ModelForResponses() with
        {
            Reasoning = true,
            ThinkingLevelMap = new Dictionary<string, string?> { [ThinkingLevels.Off] = null },
            Compatibility = new JsonObject
            {
                ["supportsDeveloperRole"] = false,
                ["supportsExplicitPromptCacheMode"] = true,
            },
        };
        var payload = OpenAiResponsesProvider.BuildPayload(
            model,
            new Context { SystemPrompt = "sys" },
            new OpenAiResponsesStreamOptions { CacheRetention = CacheRetentions.None });

        Assert.Equal("system", payload["input"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("explicit", payload["prompt_cache_options"]!["mode"]!.GetValue<string>());
        Assert.Null(payload["prompt_cache_key"]);
        Assert.Null(payload["reasoning"]);
    }

    [Fact]
    public async Task Streams_response_items_deltas_usage_and_terminal_status()
    {
        var body = string.Join(
            "\n\n",
            Sse("response.created", new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = new JsonObject { ["id"] = "resp_1" },
            }),
            Sse("response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = 0,
                ["item"] = new JsonObject { ["type"] = "message", ["id"] = "msg_1" },
            }),
            Sse("response.output_text.delta", new JsonObject
            {
                ["type"] = "response.output_text.delta",
                ["output_index"] = 0,
                ["delta"] = "Hello",
            }),
            Sse("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 0,
                ["item"] = new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = "msg_1",
                    ["phase"] = "final_answer",
                    ["content"] = new JsonArray
                    {
                        (JsonNode?)new JsonObject { ["type"] = "output_text", ["text"] = "Hello" },
                    },
                },
            }),
            Sse("response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = 1,
                ["item"] = new JsonObject
                {
                    ["type"] = "function_call",
                    ["id"] = "fc_1",
                    ["call_id"] = "call_1",
                    ["name"] = "read",
                    ["arguments"] = "",
                },
            }),
            Sse("response.function_call_arguments.delta", new JsonObject
            {
                ["type"] = "response.function_call_arguments.delta",
                ["output_index"] = 1,
                ["delta"] = "{\"path\":\"README.md\"}",
            }),
            Sse("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 1,
                ["item"] = new JsonObject
                {
                    ["type"] = "function_call",
                    ["id"] = "fc_1",
                    ["call_id"] = "call_1",
                    ["name"] = "read",
                    ["arguments"] = "{\"path\":\"README.md\"}",
                },
            }),
            Sse("response.completed", new JsonObject
            {
                ["type"] = "response.completed",
                ["response"] = new JsonObject
                {
                    ["id"] = "resp_1",
                    ["status"] = "completed",
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = 20,
                        ["output_tokens"] = 7,
                        ["total_tokens"] = 27,
                        ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 5, ["cache_write_tokens"] = 1 },
                        ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 2 },
                    },
                },
            })) + "\n\n";

        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForResponses(), new Context(), new StreamOptions { ApiKey = "key" });
        var events = await CollectAsync(stream);
        var result = await stream.Result;

        Assert.Equal("resp_1", result.ResponseId);
        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Equal("Hello", Assert.IsType<TextContent>(result.Content[0]).Text);
        var tool = Assert.IsType<ToolCall>(result.Content[1]);
        Assert.Equal("call_1|fc_1", tool.Id);
        Assert.Equal("README.md", tool.Arguments["path"]!.GetValue<string>());
        Assert.Equal(14, result.Usage.Input);
        Assert.Equal(5, result.Usage.CacheRead);
        Assert.Equal(1, result.Usage.CacheWrite);
        Assert.Equal(7, result.Usage.Output);
        Assert.Equal(2, result.Usage.Reasoning);
        Assert.Equal(27, result.Usage.TotalTokens);
        Assert.Contains(events, static value => value is TextDeltaEvent);
        Assert.Contains(events, static value => value is ToolCallEndEvent);
        Assert.IsType<StreamDoneEvent>(events[^1]);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("key", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.EndsWith("/v1/responses", handler.LastRequest.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maps_incomplete_max_output_status_to_length_and_preserves_http_errors()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad request\"}", Encoding.UTF8, "application/json"),
        });
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForResponses(), new Context(), new StreamOptions { ApiKey = "key" });
        var received = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(received[^1]);
        Assert.Contains("400", error.Error.ErrorMessage);
        Assert.Contains("bad request", error.Error.ErrorMessage);

        var payload = OpenAiResponsesProvider.BuildPayload(
            ModelForResponses(),
            new Context(),
            new OpenAiResponsesStreamOptions { MaxTokens = 1 });
        Assert.Equal(16, payload["max_output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Normalizes_foreign_responses_tool_item_ids_to_fc_hashes()
    {
        var model = ModelForResponses() with { Provider = "openai-codex" };
        var raw = "call|foreign/item/id";
        var context = new Context
        {
            Messages =
            [
                new AssistantMessage
                {
                    Api = ApiNames.OpenAiResponses,
                    Provider = "github-copilot",
                    Model = "gpt-5",
                    StopReason = StopReasons.ToolUse,
                    Content = [new ToolCall(raw, "edit", new JsonObject())],
                    Timestamp = 1,
                },
            ],
        };

        var input = OpenAiResponsesProvider.ConvertMessages(model, context);
        var function = Assert.IsType<JsonObject>(input[0]);
        Assert.Equal($"fc_{HashUtilities.ShortHash("foreign/item/id")}", function["id"]!.GetValue<string>());
        Assert.True(function["id"]!.GetValue<string>().Length <= 64);
    }

    private static Model ModelForResponses() => new()
    {
        Id = "gpt-5",
        Name = "GPT-5",
        Api = ApiNames.OpenAiResponses,
        Provider = "openai",
        BaseUrl = "https://api.openai.com/v1",
        Input = ["text"],
        MaxTokens = 8192,
    };

    private static string Sse(string eventName, JsonObject data) => $"event: {eventName}\ndata: {data.ToJsonString()}";

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }
}
