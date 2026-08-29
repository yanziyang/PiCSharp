using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class GoogleGenerativeAiTests
{
    [Fact]
    public void Builds_gemini_payload_with_system_thinking_tools_and_tool_choice()
    {
        var model = ModelForGoogle("gemini-3.1-pro-preview") with
        {
            Reasoning = true,
            Input = ["text", "image"],
            Compatibility = new JsonObject(),
        };
        var context = new Context
        {
            SystemPrompt = "Follow the repository rules.",
            Messages =
            [
                UserMessage.Blocks([new TextContent("Inspect"), new ImageContent("AQI=", "image/png")], 1),
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
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                    ConstrainedSampling = new JsonSchemaSampling("require"),
                },
            ],
        };

        var payload = GoogleGenerativeAiProvider.BuildPayload(
            model,
            context,
            new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions { Enabled = true, Level = "HIGH" },
                ToolChoice = "any",
                Temperature = 0.2,
                MaxTokens = 4096,
            });

        Assert.Equal(model.Id, payload["model"]!.GetValue<string>());
        Assert.Equal("Follow the repository rules.", payload["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("HIGH", payload["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
        Assert.True(payload["thinkingConfig"]!["includeThoughts"]!.GetValue<bool>());
        Assert.Equal("ANY", payload["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>());
        Assert.Equal(0.2, payload["generationConfig"]!["temperature"]!.GetValue<double>());
        Assert.Equal("image/png", payload["contents"]![0]!["parts"]![1]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.True(payload["tools"]![0]!["functionDeclarations"]![0]!.AsObject().ContainsKey("parametersJsonSchema"));
        Assert.False(payload["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]!["additionalProperties"] is null);
    }

    [Fact]
    public void Converts_signed_history_tool_ids_and_merged_function_responses()
    {
        var model = ModelForGoogle("gemini-3.1-flash-preview");
        var context = new Context
        {
            Messages =
            [
                UserMessage.Text("Hi", 1),
                new AssistantMessage
                {
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    StopReason = StopReasons.ToolUse,
                    Content =
                    [
                        new ThinkingContent("", "AAAAAAAAAAAAAAAAAAAAAA=="),
                        new ToolCall("call|one", "read", new JsonObject { ["path"] = "README.md" }, "AAAAAAAAAAAAAAAAAAAAAA=="),
                    ],
                    Timestamp = 2,
                },
                new ToolResultMessage
                {
                    ToolCallId = "call|one",
                    ToolName = "read",
                    Content = [new TextContent("first")],
                    Timestamp = 3,
                },
                new ToolResultMessage
                {
                    ToolCallId = "call|two",
                    ToolName = "write",
                    Content = [new TextContent("second")],
                    Timestamp = 4,
                },
            ],
        };

        var contents = GoogleShared.ConvertMessages(model, context);
        var modelTurn = contents.OfType<JsonObject>().Single(item => item["role"]!.GetValue<string>() == "model");
        var modelParts = Assert.IsType<JsonArray>(modelTurn["parts"]);
        var toolPart = Assert.IsType<JsonObject>(modelParts[1]);
        var functionCall = Assert.IsType<JsonObject>(toolPart["functionCall"]);
        Assert.Equal("call|one", functionCall["id"]!.GetValue<string>());
        var thinkingPart = Assert.IsType<JsonObject>(modelParts[0]);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAA==", thinkingPart["thoughtSignature"]!.GetValue<string>());
        var toolTurn = contents.Last()!;
        Assert.Equal(2, toolTurn["parts"]!.AsArray().Count);
        Assert.Equal("call|one", toolTurn["parts"]![0]!["functionResponse"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Routes_pre_gemini3_tool_images_to_a_separate_user_turn()
    {
        var model = ModelForGoogle("gemini-2.5-flash") with { Input = ["text", "image"] };
        var contents = GoogleShared.ConvertMessages(
            model,
            new Context
            {
                Messages =
                [
                    new ToolResultMessage
                    {
                        ToolCallId = "call_1",
                        ToolName = "inspect",
                        Content = [new ImageContent("AQI=", "image/png")],
                        Timestamp = 1,
                    },
                ],
            });

        Assert.Equal(2, contents.Count);
        Assert.Equal("(see attached image)", contents[0]!["parts"]![0]!["functionResponse"]!["response"]!["output"]!.GetValue<string>());
        Assert.Equal("Tool result image:", contents[1]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.True(contents[1]!["parts"]![1]!["inlineData"] is not null);
    }

    [Fact]
    public async Task Streams_text_thinking_tool_usage_and_google_finish_reason()
    {
        var body = string.Join(
            "\n\n",
            Sse(new JsonObject
            {
                ["responseId"] = "response-1",
                ["candidates"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray
                            {
                                (JsonNode?)new JsonObject
                                {
                                    ["text"] = "private",
                                    ["thought"] = true,
                                    ["thoughtSignature"] = "AAAAAAAAAAAAAAAAAAAAAA==",
                                },
                            },
                        },
                    },
                },
            }),
            Sse(new JsonObject
            {
                ["candidates"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray { (JsonNode?)new JsonObject { ["text"] = "answer" } },
                        },
                    },
                },
            }),
            Sse(new JsonObject
            {
                ["candidates"] = new JsonArray
                {
                    (JsonNode?)new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray
                            {
                                (JsonNode?)new JsonObject
                                {
                                    ["functionCall"] = new JsonObject
                                    {
                                        ["name"] = "read",
                                        ["id"] = "call_1",
                                        ["args"] = new JsonObject { ["path"] = "README.md" },
                                    },
                                },
                            },
                        },
                        ["finishReason"] = "STOP",
                    },
                },
                ["usageMetadata"] = new JsonObject
                {
                    ["promptTokenCount"] = 20,
                    ["cachedContentTokenCount"] = 5,
                    ["candidatesTokenCount"] = 7,
                    ["thoughtsTokenCount"] = 2,
                    ["totalTokenCount"] = 27,
                },
            })) + "\n\n";

        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new GoogleGenerativeAiProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(
            ModelForGoogle("gemini-3.1-flash-preview"),
            new Context(),
            new StreamOptions { ApiKey = "test-key" });
        var events = await CollectAsync(stream);
        var result = await stream.Result;

        Assert.Equal("response-1", result.ResponseId);
        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Equal("private", Assert.IsType<ThinkingContent>(result.Content[0]).Thinking);
        Assert.Equal("answer", Assert.IsType<TextContent>(result.Content[1]).Text);
        Assert.Equal("call_1", Assert.IsType<ToolCall>(result.Content[2]).Id);
        Assert.Equal(15, result.Usage.Input);
        Assert.Equal(5, result.Usage.CacheRead);
        Assert.Equal(9, result.Usage.Output);
        Assert.Equal(2, result.Usage.Reasoning);
        Assert.Equal(27, result.Usage.TotalTokens);
        Assert.Contains(events, static value => value is ThinkingEndEvent);
        Assert.Contains(events, static value => value is ToolCallEndEvent);
        Assert.IsType<StreamDoneEvent>(events[^1]);
        Assert.Equal("test-key", handler.LastRequest!.RequestUri!.Query.Split("key=", StringSplitOptions.None)[1]);
        Assert.Equal("text/event-stream", handler.LastRequest.Headers.Accept.Single().MediaType);
    }

    [Fact]
    public async Task Preserves_google_http_errors_and_rejects_missing_auth()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}", Encoding.UTF8, "application/json"),
        });
        var provider = new GoogleGenerativeAiProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForGoogle("gemini-2.5-flash"), new Context());
        var events = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(events[^1]);

        Assert.Contains("No API key for provider: google", error.Error.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Maps_sensitive_finish_reason_to_a_stream_error()
    {
        var body = Sse(new JsonObject
        {
            ["candidates"] = new JsonArray
            {
                (JsonNode?)new JsonObject { ["finishReason"] = "SAFETY" },
            },
        }) + "\n\n";
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new GoogleGenerativeAiProvider(new ProviderHttpClient(new HttpClient(handler)));
        var stream = provider.Stream(ModelForGoogle("gemini-2.5-flash"), new Context(), new StreamOptions { ApiKey = "key" });
        var events = await CollectAsync(stream);
        var error = Assert.IsType<StreamErrorEvent>(events[^1]);

        Assert.Equal(StopReasons.Error, error.Reason);
        Assert.Equal("Provider stopped with: SAFETY", error.Error.ErrorMessage);
    }

    [Fact]
    public void Resolves_thinking_levels_and_strict_tool_mode()
    {
        var model = ModelForGoogle("gemini-3.1-flash-preview") with
        {
            Reasoning = true,
            ThinkingLevelMap = new Dictionary<string, string?> { [ThinkingLevels.XHigh] = "LOW" },
        };
        Assert.Equal("low", GoogleShared.ResolveThinkingLevel(model, ThinkingLevels.XHigh));
        Assert.Equal("LOW", GoogleShared.GetThinkingLevel(model, ThinkingLevels.Low));
        Assert.Equal("VALIDATED", GoogleShared.ResolveGoogleFunctionCallingMode(
            [new Tool
            {
                Name = "read",
                Description = "Read",
                Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                ConstrainedSampling = new JsonSchemaSampling("require"),
            }],
            null,
            true));
        Assert.Throws<InvalidOperationException>(() => GoogleShared.ResolveGoogleFunctionCallingMode(
            [new Tool
            {
                Name = "read",
                Description = "Read",
                Parameters = new JsonObject { ["type"] = "object" },
                ConstrainedSampling = new JsonSchemaSampling("require"),
            }],
            null,
            false));
    }

    private static Model ModelForGoogle(string id) => new()
    {
        Id = id,
        Name = id,
        Api = ApiNames.GoogleGenerativeAi,
        Provider = "google",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Input = ["text"],
        MaxTokens = 8192,
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }
}
