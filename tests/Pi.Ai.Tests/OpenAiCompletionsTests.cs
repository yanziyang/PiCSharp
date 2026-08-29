using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class OpenAiCompletionsTests
{
    [Fact]
    public void Builds_openai_payload_with_multimodal_history_tools_and_sampling_overrides()
    {
        var model = ModelForOpenAi() with
        {
            Reasoning = true,
            Input = ["text", "image"],
            ThinkingLevelMap = new Dictionary<string, string?> { [ThinkingLevels.High] = "high" },
            SamplingParameters = new Dictionary<string, JsonNode?> { ["top_p"] = 0.8 },
        };
        var context = new Context
        {
            SystemPrompt = "You are concise.",
            Messages =
            [
                UserMessage.Blocks([new TextContent("Read this"), new ImageContent("AQI=", "image/png")], 1),
                new AssistantMessage
                {
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    StopReason = StopReasons.ToolUse,
                    Content =
                    [
                        new ThinkingContent("I should inspect the file."),
                        new ToolCall("call|part", "read_file", new JsonObject { ["path"] = "README.md" }),
                    ],
                    Timestamp = 2,
                },
                new ToolResultMessage
                {
                    ToolCallId = "call|part",
                    ToolName = "read_file",
                    Content = [new TextContent("contents")],
                    IsError = false,
                    Timestamp = 3,
                },
            ],
            Tools =
            [
                new Tool
                {
                    Name = "read_file",
                    Description = "Read a file.",
                    Parameters = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = new JsonArray((JsonNode?)"path"),
                    },
                },
            ],
        };

        var payload = OpenAiCompletionsProvider.BuildPayload(
            model,
            context,
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                MaxTokens = 256,
                Temperature = 0.2,
                Reasoning = ThinkingLevels.High,
                ToolChoice = "auto",
                CacheRetention = CacheRetentions.Long,
                SessionId = "session-1",
                SamplingParameters = new Dictionary<string, JsonNode?> { ["top_p"] = 0.9 },
            });

        Assert.Equal("gpt-5", payload["model"]!.GetValue<string>());
        Assert.True(payload["stream"]!.GetValue<bool>());
        Assert.Equal(256, payload["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal(0.2, payload["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, payload["top_p"]!.GetValue<double>());
        Assert.Equal("auto", payload["tool_choice"]!.GetValue<string>());
        Assert.Equal("high", payload["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("session-1", payload["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal("24h", payload["prompt_cache_retention"]!.GetValue<string>());
        Assert.False(payload["store"]!.GetValue<bool>());
        Assert.True(payload["stream_options"]!["include_usage"]!.GetValue<bool>());

        var messages = Assert.IsType<JsonArray>(payload["messages"]);
        Assert.Equal("developer", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("image_url", messages[1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_part", messages[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal("call_part", messages[3]!["tool_call_id"]!.GetValue<string>());

        var function = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["tools"])[0])!["function"]);
        Assert.False(function["strict"]!.GetValue<bool>());
        Assert.Equal("read_file", function["name"]!.GetValue<string>());
    }

    [Fact]
    public void Uses_legacy_max_tokens_and_provider_overrides_for_openai_compatible_hosts()
    {
        var model = ModelForOpenAi() with
        {
            Provider = "nvidia",
            BaseUrl = "https://integrate.api.nvidia.com/v1",
            Compatibility = new JsonObject { ["supportsUsageInStreaming"] = false },
        };

        var payload = OpenAiCompletionsProvider.BuildPayload(
            model,
            new Context { SystemPrompt = "system" },
            new StreamOptions { MaxTokens = 12 });

        Assert.Equal(12, payload["max_tokens"]!.GetValue<int>());
        Assert.Null(payload["stream_options"]);
        Assert.Null(payload["store"]);
        Assert.Equal("system", payload["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("system", payload["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streams_text_reasoning_tool_calls_usage_and_terminal_result()
    {
        const string responseBody =
            "data: {\"id\":\"resp-1\",\"model\":\"gpt-5-2026\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"resp-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"inspect\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"README.md\\\"}\"}}]},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: {\"id\":\"resp-1\",\"model\":\"gpt-5-2026\",\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":7,\"prompt_tokens_details\":{\"cached_tokens\":5},\"completion_tokens_details\":{\"reasoning_tokens\":2}}}\n\n" +
            "data: [DONE]\n\n";

        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiCompletionsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForOpenAi(), new Context(), new StreamOptions { ApiKey = "test-key" });
        var events = await CollectAsync(stream);
        var result = await stream.Result;

        Assert.Equal("resp-1", result.ResponseId);
        Assert.Equal("gpt-5-2026", result.ResponseModel);
        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Equal("Hello world", Assert.IsType<TextContent>(result.Content[0]).Text);
        Assert.Equal("inspect", Assert.IsType<ThinkingContent>(result.Content[1]).Thinking);
        var call = Assert.IsType<ToolCall>(result.Content[2]);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("README.md", call.Arguments["path"]!.GetValue<string>());
        Assert.Equal(15, result.Usage.Input);
        Assert.Equal(5, result.Usage.CacheRead);
        Assert.Equal(7, result.Usage.Output);
        Assert.Equal(2, result.Usage.Reasoning);
        Assert.Equal(27, result.Usage.TotalTokens);
        Assert.Contains(events, static @event => @event is StreamStartEvent);
        Assert.Contains(events, static @event => @event is TextStartEvent);
        Assert.Contains(events, static @event => @event is TextDeltaEvent text && text.Delta == " world");
        Assert.Contains(events, static @event => @event is ThinkingEndEvent thinking && thinking.Content == "inspect");
        Assert.Contains(events, static @event => @event is ToolCallEndEvent end && end.ToolCall.Name == "read_file");
        Assert.IsType<StreamDoneEvent>(events[^1]);
        Assert.Equal("application/json", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Formats_http_error_body_in_terminal_error_event()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid request\"}}", Encoding.UTF8, "application/json"),
        });
        var provider = new OpenAiCompletionsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForOpenAi(), new Context(), new StreamOptions { ApiKey = "test-key" });
        var events = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(events[^1]);

        Assert.Equal(StopReasons.Error, error.Reason);
        Assert.Contains("400", error.Error.ErrorMessage);
        Assert.Contains("invalid request", error.Error.ErrorMessage);
        Assert.Equal(StopReasons.Error, (await stream.Result).StopReason);
    }

    [Fact]
    public async Task Rejects_missing_api_key_without_sending_request()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiCompletionsProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForOpenAi(), new Context());
        var events = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(events[^1]);

        Assert.Contains("No API key for provider: openai", error.Error.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    private static Model ModelForOpenAi() => new()
    {
        Id = "gpt-5",
        Name = "GPT-5",
        Api = ApiNames.OpenAiCompletions,
        Provider = "openai",
        BaseUrl = "https://api.openai.com/v1",
        Input = ["text"],
        Cost = new ModelCost
        {
            Input = 1,
            Output = 2,
            CacheRead = 0.5,
        },
    };

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
