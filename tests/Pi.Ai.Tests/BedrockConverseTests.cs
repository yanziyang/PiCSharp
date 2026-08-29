using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class BedrockConverseTests
{
    [Fact]
    public void Builds_cached_tool_reasoning_and_multiturn_payloads()
    {
        var model = ModelForBedrock("us.anthropic.claude-sonnet-4-5-20250929-v1:0", reasoning: true);
        var sourceArguments = new JsonObject
        {
            ["path"] = "/workspace/file.cs",
            ["edits"] = new JsonArray
            {
                (JsonNode?)new JsonObject { ["oldText"] = "before", ["newText"] = "after", [""] = "preserve only in source" },
            },
        };
        var payload = BedrockConverseProvider.BuildPayload(
            model,
            new Context
            {
                SystemPrompt = "Be precise",
                Messages =
                [
                    UserMessage.Text("Read the file", 1),
                    new AssistantMessage
                    {
                        Api = model.Api,
                        Provider = model.Provider,
                        Model = model.Id,
                        Content =
                        [
                            new ThinkingContent("inspect", "signature"),
                            new ToolCall("tool|1", "read", sourceArguments),
                        ],
                        StopReason = StopReasons.ToolUse,
                        Timestamp = 2,
                    },
                    new ToolResultMessage
                    {
                        ToolCallId = "tool|1",
                        ToolName = "read",
                        Content = [new TextContent("file contents")],
                        Timestamp = 3,
                    },
                    UserMessage.Text("Continue", 4),
                ],
                Tools =
                [
                    new Tool
                    {
                        Name = "read",
                        Description = "Read a file",
                        Parameters = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["path"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                        ConstrainedSampling = new JsonSchemaSampling("prefer"),
                    },
                ],
            },
            new BedrockOptions
            {
                CacheRetention = CacheRetentions.Long,
                ToolChoice = JsonValue.Create("auto"),
                Reasoning = ThinkingLevels.High,
                ThinkingBudgets = new ThinkingBudgets { High = 16000 },
                RequestMetadata = new Dictionary<string, string> { ["team"] = "pi" },
            });

        Assert.Equal(model.Id, payload["modelId"]!.GetValue<string>());
        Assert.Equal("Be precise", payload["system"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("1h", payload["system"]![1]!["cachePoint"]!["ttl"]!.GetValue<string>());
        Assert.True(payload["toolConfig"]!["toolChoice"]!["auto"] is JsonObject);
        Assert.True(payload["toolConfig"]!["tools"]![0]!["toolSpec"]!["strict"]!.GetValue<bool>());
        Assert.Equal(16000, payload["additionalModelRequestFields"]!["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Equal("pi", payload["requestMetadata"]!["team"]!.GetValue<string>());

        var messages = payload["messages"]!.AsArray();
        var assistant = messages[1]!["content"]![1]!["toolUse"]!;
        Assert.Equal("tool|1", assistant["toolUseId"]!.GetValue<string>());
        Assert.Equal("/workspace/file.cs", assistant["input"]!["path"]!.GetValue<string>());
        Assert.False(assistant["input"]!["edits"]![0]!.AsObject().ContainsKey(""));
        Assert.Equal("preserve only in source", sourceArguments["edits"]![0]![""]!.GetValue<string>());
        var lastMessage = messages[messages.Count - 1]!;
        var lastContent = lastMessage["content"]!.AsArray();
        Assert.Equal("1h", lastContent[lastContent.Count - 1]!["cachePoint"]!["ttl"]!.GetValue<string>());
    }

    [Fact]
    public void Downgrades_images_and_blank_blocks_for_nonvision_models()
    {
        var model = ModelForBedrock("amazon.nova-lite-v1:0", reasoning: false, supportsImages: false);
        var payload = BedrockConverseProvider.BuildPayload(
            model,
            new Context
            {
                Messages =
                [
                    UserMessage.Blocks(
                        [new TextContent("hello"), new ImageContent("aGVsbG8=", "image/png"), new ImageContent("aGVsbG8=", "image/png")],
                        1),
                ],
            },
            new BedrockOptions { CacheRetention = CacheRetentions.None });

        var content = payload["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("(image omitted: model does not support images)", content[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streams_text_thinking_tools_usage_and_stop_reason()
    {
        var transport = new FakeBedrockTransport
        {
            Response = Response(
                new BedrockMessageStartEvent("assistant"),
                new BedrockContentBlockDeltaEvent(0) { ReasoningText = "reason", Signature = "sig" },
                new BedrockContentBlockStopEvent(0),
                new BedrockContentBlockDeltaEvent(1) { Text = "answer" },
                new BedrockContentBlockStopEvent(1),
                new BedrockContentBlockStartEvent(2) { ToolUseId = "tool-1", ToolName = "lookup" },
                new BedrockContentBlockDeltaEvent(2) { ToolInput = "{\"query\":\"pi\"}" },
                new BedrockContentBlockStopEvent(2),
                new BedrockMetadataEvent(100, 25, 10, 3, 125),
                new BedrockMessageStopEvent("tool_use")),
        };
        var provider = new BedrockConverseProvider(transport);
        var result = await provider.Stream(ModelForBedrock("us.anthropic.claude-sonnet-4-5", true), UserContext()).Result;

        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Equal("tool_use", result.RawStopReason);
        Assert.Equal(100, result.Usage.Input);
        Assert.Equal(25, result.Usage.Output);
        Assert.Equal(10, result.Usage.CacheRead);
        Assert.Equal(3, result.Usage.CacheWrite);
        Assert.Equal(["thinking", "text", "toolCall"], result.Content.Select(block => block.Type).ToArray());
        Assert.Equal("sig", ((ThinkingContent)result.Content[0]).ThinkingSignature);
        Assert.Equal("answer", ((TextContent)result.Content[1]).Text);
        var call = Assert.IsType<ToolCall>(result.Content[2]);
        Assert.Equal("pi", call.Arguments["query"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finalizes_redacted_reasoning_when_content_stop_is_missing()
    {
        var bytes = Encoding.UTF8.GetBytes("opaque-reasoning");
        var transport = new FakeBedrockTransport
        {
            Response = Response(
                new BedrockMessageStartEvent("assistant"),
                new BedrockContentBlockDeltaEvent(0) { RedactedContent = bytes },
                new BedrockContentBlockDeltaEvent(1) { Text = "done" },
                new BedrockMessageStopEvent("end_turn")),
        };
        var model = ModelForBedrock("global.openai.gpt-5.6-terra", reasoning: true, supportsImages: false);
        var result = await new BedrockConverseProvider(transport).Stream(model, UserContext()).Result;

        var thinking = Assert.IsType<ThinkingContent>(result.Content[0]);
        Assert.True(thinking.Redacted);
        Assert.Equal(Convert.ToBase64String(bytes), thinking.ThinkingSignature);
        Assert.Equal("[Reasoning redacted]", thinking.Thinking);
        Assert.Equal("done", Assert.IsType<TextContent>(result.Content[1]).Text);
    }

    [Fact]
    public void Replays_redacted_reasoning_as_opaque_bytes()
    {
        var model = ModelForBedrock("global.openai.gpt-5.6-terra", reasoning: true, supportsImages: false);
        var payload = BedrockConverseProvider.BuildPayload(
            model,
            new Context
            {
                Messages =
                [
                    UserMessage.Text("hello", 1),
                    new AssistantMessage
                    {
                        Api = model.Api,
                        Provider = model.Provider,
                        Model = model.Id,
                        Content = [new ThinkingContent("", "b3BhcXVl", true), new TextContent("done")],
                        StopReason = StopReasons.Stop,
                        Timestamp = 2,
                    },
                    UserMessage.Text("continue", 3),
                ],
            },
            new BedrockOptions { CacheRetention = CacheRetentions.None });

        var reasoning = payload["messages"]![1]!["content"]![0]!["reasoningContent"]!;
        Assert.Equal("b3BhcXVl", reasoning["redactedContent"]!.GetValue<string>());
    }

    [Fact]
    public async Task Applies_bedrock_credential_endpoint_and_header_policy()
    {
        var transport = new FakeBedrockTransport { Response = Response(new BedrockMessageStartEvent("assistant"), new BedrockMessageStopEvent("end_turn")) };
        ProviderResponse? observed = null;
        var result = await new BedrockConverseProvider(transport).Stream(
                ModelForBedrock("arn:aws:bedrock:us-west-2:123456789012:application-inference-profile/example", reasoning: false)
                    with
                { BaseUrl = "https://bedrock-vpc.example.com" },
                UserContext(),
                new BedrockOptions
                {
                    Profile = "explicit-profile",
                    Region = "us-west-2",
                    Environment = new Dictionary<string, string>
                    {
                        ["AWS_ACCESS_KEY_ID"] = "access",
                        ["AWS_SECRET_ACCESS_KEY"] = "secret",
                    },
                    Headers = new Dictionary<string, string?>
                    {
                        ["Authorization"] = "caller-auth",
                        ["X-Amz-Date"] = "caller-date",
                        ["Host"] = "caller-host",
                        ["X-Custom"] = "allowed",
                    },
                    OnResponse = (response, _) =>
                    {
                        observed = response;
                        return ValueTask.CompletedTask;
                    },
                })
            .Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal("explicit-profile", transport.Options!.Profile);
        Assert.Equal("us-west-2", transport.Options.Region);
        Assert.Equal("https://bedrock-vpc.example.com", transport.Options.Endpoint);
        Assert.Null(transport.Options.AccessKeyId);
        Assert.Equal("allowed", transport.Options.Headers["X-Custom"]);
        Assert.DoesNotContain(transport.Options.Headers.Keys, key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(200, observed!.Status);
        Assert.Equal("req-1", observed.Headers["x-amzn-requestid"]);
    }

    [Fact]
    public async Task Maps_raw_unknown_stop_reason_and_failure_diagnostics()
    {
        var transport = new FakeBedrockTransport
        {
            Error = new BedrockConverseTransportException(
                "The provided model identifier is invalid.",
                status: 400,
                errorCode: "ValidationException",
                requestId: "request-1"),
        };
        var error = await new BedrockConverseProvider(transport).Stream(ModelForBedrock("model", false), UserContext()).Result;

        Assert.Equal(StopReasons.Error, error.StopReason);
        Assert.Equal("Validation error: The provided model identifier is invalid.", error.ErrorMessage);
        var diagnostic = Assert.Single(error.Diagnostics!);
        Assert.Equal("bedrock_response_failure", diagnostic.Type);
        Assert.Equal(400, diagnostic.Details!["status"]!.GetValue<int>());
        Assert.Equal("ValidationException", diagnostic.Details["errorCode"]!.GetValue<string>());
        Assert.Equal("request-1", diagnostic.Details["requestId"]!.GetValue<string>());

        var rawTransport = new FakeBedrockTransport
        {
            Response = Response(new BedrockMessageStartEvent("assistant"), new BedrockMessageStopEvent("guardrail_intervened")),
        };
        var raw = await new BedrockConverseProvider(rawTransport).Stream(ModelForBedrock("model", false), UserContext()).Result;
        Assert.Equal("guardrail_intervened", raw.RawStopReason);
        Assert.Equal("Provider stopped with: guardrail_intervened", raw.ErrorMessage);
    }

    [Fact]
    public void Builds_adaptive_and_govcloud_thinking_payloads()
    {
        var adaptive = BedrockConverseProvider.BuildPayload(
            ModelForBedrock("global.anthropic.claude-opus-4-8", true),
            UserContext(),
            new BedrockOptions { Reasoning = ThinkingLevels.XHigh });
        Assert.Equal("adaptive", adaptive["additionalModelRequestFields"]!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("xhigh", adaptive["additionalModelRequestFields"]!["output_config"]!["effort"]!.GetValue<string>());

        var gov = BedrockConverseProvider.BuildPayload(
            ModelForBedrock("us-gov.anthropic.claude-sonnet-4-5", true),
            UserContext(),
            new BedrockOptions { Reasoning = ThinkingLevels.High });
        Assert.False(gov["additionalModelRequestFields"]!["thinking"]!.AsObject().ContainsKey("display"));
        Assert.Equal("interleaved-thinking-2025-05-14", gov["additionalModelRequestFields"]!["anthropic_beta"]![0]!.GetValue<string>());
    }

    private static Context UserContext() => new()
    {
        Messages = [UserMessage.Text("hello", 1)],
    };

    private static Model ModelForBedrock(string id, bool reasoning, bool supportsImages = true) => new()
    {
        Id = id,
        Name = id.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "Claude Sonnet 4.5" : id,
        Api = ApiNames.BedrockConverseStream,
        Provider = "amazon-bedrock",
        BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
        Reasoning = reasoning,
        Input = supportsImages ? ["text", "image"] : ["text"],
        ContextWindow = 200_000,
        MaxTokens = 64_000,
        Compatibility = new JsonObject { ["supportsStrictMode"] = true },
    };

    private static BedrockConverseResponse Response(params BedrockConverseEvent[] events) => new()
    {
        Status = 200,
        Headers = new Dictionary<string, string>
        {
            ["x-amzn-requestid"] = "req-1",
        },
        RequestId = "req-1",
        Events = ToAsync(events),
    };

    private static async IAsyncEnumerable<BedrockConverseEvent> ToAsync(
        IEnumerable<BedrockConverseEvent> events)
    {
        foreach (var item in events)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }

    private sealed class FakeBedrockTransport : IBedrockConverseTransport
    {
        public JsonObject? Payload { get; private set; }

        public BedrockTransportOptions? Options { get; private set; }

        public BedrockConverseResponse? Response { get; init; }

        public Exception? Error { get; init; }

        public Task<BedrockConverseResponse> SendAsync(
            JsonObject payload,
            BedrockTransportOptions options,
            CancellationToken cancellationToken)
        {
            Payload = payload.DeepClone().AsObject();
            Options = options;
            if (Error is not null)
            {
                return Task.FromException<BedrockConverseResponse>(Error);
            }

            return Task.FromResult(Response ?? throw new InvalidOperationException("Fake Bedrock response was not configured."));
        }
    }
}
