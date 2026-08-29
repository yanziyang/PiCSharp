using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class AnthropicMessagesTests
{
    [Fact]
    public void Builds_anthropic_payload_with_cache_control_images_thinking_tools_and_metadata()
    {
        var model = ModelForAnthropic() with
        {
            Reasoning = true,
            Input = ["text", "image"],
            ThinkingLevelMap = new Dictionary<string, string?> { [ThinkingLevels.High] = "high" },
            Compatibility = new JsonObject
            {
                ["supportsStrictTools"] = true,
                ["supportsLongCacheRetention"] = true,
            },
        };
        var context = new Context
        {
            SystemPrompt = "Follow the repository rules.",
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
                        new ThinkingContent("Need to inspect", "signature"),
                        new ToolCall("call|1", "read", new JsonObject { ["path"] = "README.md" }),
                    ],
                    Timestamp = 2,
                },
                new ToolResultMessage
                {
                    ToolCallId = "call|1",
                    ToolName = "read",
                    Content = [new TextContent("file contents"), new ImageContent("AQI=", "image/png")],
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
                    ConstrainedSampling = new JsonSchemaSampling("strict"),
                },
            ],
        };

        var payload = AnthropicMessagesProvider.BuildPayload(
            model,
            context,
            new AnthropicStreamOptions
            {
                ApiKey = "key",
                MaxTokens = 4096,
                Temperature = 0.1,
                ThinkingEnabled = true,
                ThinkingBudgetTokens = 2048,
                CacheRetention = CacheRetentions.Long,
                ToolChoice = "tool",
                ToolChoiceName = "read",
                Metadata = new Dictionary<string, JsonNode?> { ["user_id"] = "user-1" },
            });

        Assert.Equal("claude-test", payload["model"]!.GetValue<string>());
        Assert.Equal(4096, payload["max_tokens"]!.GetValue<int>());
        Assert.True(payload["stream"]!.GetValue<bool>());
        Assert.Null(payload["temperature"]);
        Assert.Equal("user-1", payload["metadata"]!["user_id"]!.GetValue<string>());
        Assert.Equal("tool", payload["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("read", payload["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("enabled", payload["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(2048, payload["thinking"]!["budget_tokens"]!.GetValue<int>());

        var system = Assert.IsType<JsonArray>(payload["system"]);
        Assert.Equal("1h", system[0]!["cache_control"]!["ttl"]!.GetValue<string>());
        var messages = Assert.IsType<JsonArray>(payload["messages"]);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("image", messages[0]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("thinking", messages[1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", messages[1]!["content"]![1]!["id"]!.GetValue<string>());
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool_result", messages[2]!["content"]![0]!["type"]!.GetValue<string>());
        var toolResultContent = Assert.IsType<JsonArray>(messages[2]!["content"]);
        Assert.True(toolResultContent[toolResultContent.Count - 1]!["cache_control"] is not null);

        var tool = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["tools"])[0]);
        Assert.True(tool["eager_input_streaming"]!.GetValue<bool>());
        Assert.True(tool["strict"]!.GetValue<bool>());
        Assert.Equal("1h", tool["cache_control"]!["ttl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Maps_simple_reasoning_to_budget_or_adaptive_thinking_payload()
    {
        var model = ModelForAnthropic() with
        {
            Reasoning = true,
            MaxTokens = 32000,
            Compatibility = new JsonObject { ["forceAdaptiveThinking"] = true },
        };

        var provider = new AnthropicMessagesProvider(new ProviderHttpClient(new HttpClient(new CapturingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/event-stream"),
            }))));
        JsonObject? captured = null;
        var stream = provider.StreamSimple(
            model,
            new Context(),
            new SimpleStreamOptions
            {
                ApiKey = "key",
                Reasoning = ThinkingLevels.Medium,
                OnPayload = (node, _) =>
                {
                    captured = node as JsonObject;
                    throw new PayloadCapturedException();
                },
            });

        _ = await stream.Result;
        var payload = Assert.IsType<JsonObject>(captured);
        Assert.Equal("adaptive", payload["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("medium", payload["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streams_text_thinking_tool_input_usage_and_response_metadata()
    {
        var events = new[]
        {
            Sse("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = "msg_test",
                    ["model"] = "claude-response",
                    ["usage"] = new JsonObject { ["input_tokens"] = 12, ["output_tokens"] = 0 },
                },
            }),
            Sse("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "Initial" },
            }),
            Sse("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = " text" },
            }),
            Sse("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 }),
            Sse("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 1,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = "Initial thinking",
                    ["signature"] = "sig",
                },
            }),
            Sse("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 1,
                ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = " more" },
            }),
            Sse("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 1,
                ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "+sig" },
            }),
            Sse("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 1 }),
            Sse("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 2,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_1",
                    ["name"] = "read",
                    ["input"] = new JsonObject(),
                },
            }),
            Sse("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 2,
                ["delta"] = new JsonObject
                {
                    ["type"] = "input_json_delta",
                    ["partial_json"] = "{\"path\":\"README.md\"}",
                },
            }),
            Sse("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 2 }),
            Sse("message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" },
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 12,
                    ["output_tokens"] = 5,
                    ["cache_read_input_tokens"] = 2,
                    ["cache_creation_input_tokens"] = 1,
                    ["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = 3 },
                },
            }),
            Sse("message_stop", new JsonObject { ["type"] = "message_stop" }),
        };

        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n\n", events) + "\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new AnthropicMessagesProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForAnthropic(), new Context(), new StreamOptions { ApiKey = "key" });
        var received = await CollectAsync(stream);
        var result = await stream.Result;

        Assert.Equal("msg_test", result.ResponseId);
        Assert.Equal("claude-response", result.ResponseModel);
        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Equal("Initial text", Assert.IsType<TextContent>(result.Content[0]).Text);
        var thinking = Assert.IsType<ThinkingContent>(result.Content[1]);
        Assert.Equal("Initial thinking more", thinking.Thinking);
        Assert.Equal("sig+sig", thinking.ThinkingSignature);
        var tool = Assert.IsType<ToolCall>(result.Content[2]);
        Assert.Equal("README.md", tool.Arguments["path"]!.GetValue<string>());
        Assert.Equal(12, result.Usage.Input);
        Assert.Equal(2, result.Usage.CacheRead);
        Assert.Equal(1, result.Usage.CacheWrite);
        Assert.Equal(5, result.Usage.Output);
        Assert.Equal(3, result.Usage.Reasoning);
        Assert.Equal(20, result.Usage.TotalTokens);
        Assert.Contains(received, static value => value is ThinkingEndEvent);
        Assert.Contains(received, static value => value is ToolCallEndEvent);
        Assert.IsType<StreamDoneEvent>(received[^1]);
        Assert.Equal("2023-06-01", handler.LastRequest!.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("key", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Repairs_malformed_tool_json_and_maps_sensitive_stop_reason()
    {
        var malformed = "{\"path\":\"A\\H\",\"text\":\"col1\tcol2\"}";
        var body = string.Join(
            "\n\n",
            Sse("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject { ["id"] = "msg", ["usage"] = new JsonObject() },
            }),
            Sse("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 0,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "tool",
                    ["name"] = "edit",
                    ["input"] = new JsonObject(),
                },
            }),
            Sse("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = malformed },
            }),
            Sse("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 }),
            Sse("message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject
                {
                    ["stop_reason"] = "sensitive",
                },
            }),
            Sse("message_stop", new JsonObject { ["type"] = "message_stop" }));

        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body + "\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new AnthropicMessagesProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForAnthropic(), new Context(), new StreamOptions { ApiKey = "key" });
        var received = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(received[^1]);

        Assert.Equal(StopReasons.Error, error.Reason);
        Assert.Equal("Provider stopped with: sensitive", error.Error.ErrorMessage);
        var tool = Assert.IsType<ToolCall>(error.Error.Content[0]);
        Assert.Equal("A\\H", tool.Arguments["path"]!.GetValue<string>());
        Assert.Equal("col1\tcol2", tool.Arguments["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Uses_bearer_auth_for_oauth_tokens_and_preserves_http_error_body()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":\"blocked\"}", Encoding.UTF8, "application/json"),
        });
        var provider = new AnthropicMessagesProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(
            ModelForAnthropic(),
            new Context(),
            new StreamOptions { ApiKey = "sk-ant-oat-test" });
        var received = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(received[^1]);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-ant-oat-test", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.False(handler.LastRequest.Headers.Contains("x-api-key"));
        Assert.Contains("403", error.Error.ErrorMessage);
        Assert.Contains("blocked", error.Error.ErrorMessage);
    }

    [Fact]
    public async Task Reports_missing_auth_without_sending_request()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new AnthropicMessagesProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForAnthropic(), new Context());
        var received = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(received[^1]);

        Assert.Contains("No API key for provider: anthropic", error.Error.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    private static Model ModelForAnthropic() => new()
    {
        Id = "claude-test",
        Name = "Claude test",
        Api = ApiNames.AnthropicMessages,
        Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com",
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

    private sealed class PayloadCapturedException : Exception
    {
    }
}
