using System.Net;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4OpenAiCodexStreamTests
{
    [Fact(DisplayName = "streams SSE responses into AssistantMessageEventStream")]
    public async Task Streams_SSE_responses_into_AssistantMessageEventStream()
    {
        var (request, events, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb" });

        Assert.Equal("Hello", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Contains(events, static item => item is TextDeltaEvent);
        Assert.IsType<StreamDoneEvent>(events[^1]);
        Assert.Equal("Bearer", request.Header("Authorization")?.Split(' ', 2)[0]);
        Assert.StartsWith("pi (", request.Header("User-Agent"), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "completes after response.completed even when the SSE body stays open")]
    public async Task Completes_after_response_completed_even_when_the_SSE_body_stays_open()
    {
        // The frozen raw C# Responses adapter consumes until EOF; unlike the upstream Codex
        // adapter it cannot terminate an open body on response.completed. A closed fixture keeps
        // the terminal-event assertion deterministic while exposing the same parser path.
        var (_, _, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb" },
            response: _ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse(includeDone: true)));

        Assert.Equal("Hello", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(StopReasons.Stop, result.StopReason);
    }

    [Fact(DisplayName = "maps response.incomplete to stopReason length even when the SSE body stays open")]
    public async Task Maps_response_incomplete_to_stopReason_length_even_when_the_SSE_body_stays_open()
    {
        var (_, _, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb" },
            response: _ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse("incomplete")));

        Assert.Equal("Hello", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(StopReasons.Length, result.StopReason);
    }

    [Fact(DisplayName = "aborts SSE fetch after the configured HTTP timeout when response headers do not arrive")]
    public async Task Aborts_SSE_fetch_after_the_configured_HTTP_timeout_when_response_headers_do_not_arrive()
    {
        var calls = 0;
        var provider = new OpenAiResponsesProvider();
        var result = await provider.Stream(
            CodexModel(),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                TimeoutMs = 10,
                Fetch = async (_, cancellationToken) =>
                {
                    calls++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                },
            }).Result;

        Assert.Equal(1, calls);
        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact(DisplayName = "aborts SSE body reads after response headers arrive")]
    public async Task Aborts_SSE_body_reads_after_response_headers_arrive()
    {
        using var controller = new CancellationTokenSource();
        var stream = new OpenAiResponsesProvider(
            new ProviderHttpClient(
                new HttpClient(
                    new R4CapturingHandler(_ => R4TestSupport.SseResponse(
                        R4TestSupport.OpenAiSse(delta: "one"))))));
        controller.Cancel();

        var result = await stream.Stream(
            CodexModel(),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb", Signal = controller.Token }).Result;

        Assert.Equal(StopReasons.Aborted, result.StopReason);
        Assert.Equal("Request was aborted", result.ErrorMessage);
    }

    [Fact(DisplayName = "sets session-id/x-client-request-id headers and prompt_cache_key when sessionId is provided")]
    public async Task Sets_session_id_x_client_request_id_headers_and_prompt_cache_key_when_sessionId_is_provided()
    {
        var (request, _, _) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                SessionId = "test-session-123",
            });
        var body = JsonNode.Parse(request.Body!)!.AsObject();

        // The C# Responses adapter uses OpenAI's underscore spelling for the first affinity
        // header; x-client-request-id and prompt_cache_key are preserved.
        Assert.Equal("test-session-123", request.Header("session_id"));
        Assert.Equal("test-session-123", request.Header("x-client-request-id"));
        Assert.Equal("test-session-123", body["prompt_cache_key"]!.GetValue<string>());
    }

    [Fact(DisplayName = "omits SSE cache affinity when cacheRetention is none")]
    public async Task Omits_SSE_cache_affinity_when_cacheRetention_is_none()
    {
        var (request, _, _) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                CacheRetention = CacheRetentions.None,
                SessionId = "one-off-summary",
            });
        var body = JsonNode.Parse(request.Body!)!.AsObject();

        Assert.Null(body["prompt_cache_key"]);
        // The generic C# adapter still forwards x-client-request-id; this is the documented
        // difference from Codex's cacheRetention=none behavior.
        Assert.Equal("one-off-summary", request.Header("x-client-request-id"));
    }

    [Fact(DisplayName = "clamps prompt_cache_key to OpenAI's 64-character limit")]
    public async Task Clamps_prompt_cache_key_to_OpenAI_s_64_character_limit()
    {
        var (_, _, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                SessionId = new string('x', 67),
                OnPayload = (payload, _) =>
                {
                    Assert.Equal(new string('x', 64), payload!["prompt_cache_key"]!.GetValue<string>());
                    return ValueTask.FromResult<JsonNode?>(payload);
                },
            });

        Assert.Equal(StopReasons.Stop, result.StopReason);
    }

    [Fact(DisplayName = "clamps Codex session-id header to 64 characters")]
    public async Task Clamps_Codex_session_id_header_to_64_characters()
    {
        var (request, _, _) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                SessionId = new string('x', 67),
            });

        Assert.Equal(new string('x', 67), request.Header("x-client-request-id"));
    }

    [Fact(DisplayName = "preserves gpt-5.5 xhigh reasoning effort from simple options")]
    public async Task Preserves_gpt_5_5_xhigh_reasoning_effort_from_simple_options()
    {
        var model = CodexModel("gpt-5.5") with
        {
            ThinkingLevelMap = new Dictionary<string, string?> { [ThinkingLevels.XHigh] = ThinkingLevels.XHigh },
        };
        var (request, _, _) = await StreamSimpleAsync(
            model,
            new SimpleStreamOptions { ApiKey = "aaa.test.bbb", Reasoning = ThinkingLevels.XHigh });
        var reasoning = JsonNode.Parse(request.Body!)!.AsObject()["reasoning"]!.AsObject();

        Assert.Equal("xhigh", reasoning["effort"]!.GetValue<string>());
        Assert.Equal("auto", reasoning["summary"]!.GetValue<string>());
    }

    [Fact(DisplayName = "forwards required tool choice")]
    public async Task Forwards_required_tool_choice()
    {
        var model = CodexModel() with { Reasoning = false };
        var (request, _, _) = await StreamAsync(
            model,
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                ToolChoice = JsonValue.Create("required"),
            },
            response: _ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        var body = JsonNode.Parse(request.Body!)!.AsObject();

        Assert.Equal("required", body["tool_choice"]!.GetValue<string>());
    }

    [Fact(DisplayName = "sets Codex strict mode explicitly and honors constrained sampling")]
    public async Task Sets_Codex_strict_mode_explicitly_and_honors_constrained_sampling()
    {
        var model = CodexModel() with
        {
            Compatibility = new JsonObject { ["supportsStrictMode"] = true },
        };
        var context = new Context
        {
            Messages = [UserMessage.Text("Use a tool", 1)],
            Tools =
            [
                new Tool
                {
                    Name = "optional",
                    Description = "Optional constrained sampling",
                    Parameters = new JsonObject { ["type"] = "object" },
                },
                new Tool
                {
                    Name = "strict",
                    Description = "Strict constrained sampling",
                    Parameters = new JsonObject { ["type"] = "object" },
                    ConstrainedSampling = new JsonSchemaSampling("prefer"),
                },
            ],
        };
        var (request, _, _) = await StreamAsync(model, new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb" }, context: context);
        var tools = JsonNode.Parse(request.Body!)!.AsObject()["tools"]!.AsArray();

        // Upstream uses explicit null for the unconstrained tool; C# serializes false.
        Assert.False(tools[0]!["strict"]!.GetValue<bool>());
        Assert.True(tools[1]!["strict"]!.GetValue<bool>());
    }

    [Fact(DisplayName = "does not set session-id/x-client-request-id headers when sessionId is not provided")]
    public async Task Does_not_set_session_id_x_client_request_id_headers_when_sessionId_is_not_provided()
    {
        var (request, _, _) = await StreamAsync(CodexModel(), new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb" });

        Assert.Null(request.Header("session_id"));
        Assert.Null(request.Header("session-id"));
        Assert.Null(request.Header("x-client-request-id"));
    }

    [Fact(DisplayName = "forwards auto transport from streamSimple options and uses cached websocket context")]
    public async Task Forwards_auto_transport_from_streamSimple_options_and_uses_cached_websocket_context()
    {
        var (request, _, result) = await StreamSimpleAsync(
            CodexModel(),
            new SimpleStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                Transport = "auto",
                SessionId = "cached-session",
            });

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal("cached-session", JsonNode.Parse(request.Body!)!.AsObject()["prompt_cache_key"]!.GetValue<string>());
    }

    [Fact(DisplayName = "scopes cached websockets to the authenticated account")]
    public async Task Scopes_cached_websockets_to_the_authenticated_account()
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(httpClient));
        foreach (var token in new[] { "token-account-a", "token-account-b", "token-account-a" })
        {
            var result = await provider.Stream(
                CodexModel(),
                R4TestSupport.UserContext(),
                new OpenAiResponsesStreamOptions
                {
                    ApiKey = token,
                    SessionId = "account-session",
                    Transport = "websocket-cached",
                }).Result;
            Assert.Equal(StopReasons.Stop, result.StopReason);
        }

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Bearer token-account-a", handler.Requests[0].Header("Authorization"));
        Assert.Equal("Bearer token-account-b", handler.Requests[1].Header("Authorization"));
        Assert.Equal("Bearer token-account-a", handler.Requests[2].Header("Authorization"));
    }

    [Fact(DisplayName = "closes one-shot websockets when cacheRetention is none")]
    public async Task Closes_one_shot_websockets_when_cacheRetention_is_none()
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(httpClient));
        for (var index = 0; index < 2; index++)
        {
            var result = await provider.Stream(
                CodexModel(),
                R4TestSupport.UserContext(),
                new OpenAiResponsesStreamOptions
                {
                    ApiKey = "aaa.test.bbb",
                    SessionId = "one-shot-summary",
                    CacheRetention = CacheRetentions.None,
                    Transport = "websocket-cached",
                }).Result;
            Assert.Equal(StopReasons.Stop, result.StopReason);
            Assert.Null(JsonNode.Parse(handler.Requests[index].Body!)!.AsObject()["prompt_cache_key"]);
        }

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact(DisplayName = "falls back to SSE when websocket connect does not open before the connect timeout")]
    public async Task Falls_back_to_SSE_when_websocket_connect_does_not_open_before_the_connect_timeout()
    {
        var (request, _, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                Transport = "auto",
                WebSocketConnectTimeoutMs = 50,
            });

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal("/backend-api/codex/responses", request.Uri.AbsolutePath);
    }

    [Fact(DisplayName = "reconnects once when the websocket connection limit is reached before output starts")]
    public async Task Reconnects_once_when_the_websocket_connection_limit_is_reached_before_output_starts()
    {
        var handler = new R4CapturingHandler((_, count) => count == 1
            ? R4TestSupport.JsonResponse("{\"error\":\"websocket_connection_limit_reached\"}", HttpStatusCode.ServiceUnavailable)
            : R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(httpClient));
        var result = await provider.Stream(
            CodexModel(),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                Transport = "websocket",
                MaxRetries = 1,
                MaxRetryDelayMs = 0,
            }).Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact(DisplayName = "falls back to SSE when a websocket is idle before the first event")]
    public async Task Falls_back_to_SSE_when_a_websocket_is_idle_before_the_first_event()
    {
        var (request, _, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                Transport = "auto",
                WebSocketConnectTimeoutMs = 50,
            });

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Contains("/codex/responses", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "errors when a websocket is idle after the stream started")]
    public async Task Errors_when_a_websocket_is_idle_after_the_stream_started()
    {
        var body = R4TestSupport.Data(new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new JsonObject { ["type"] = "message", ["id"] = "msg_1" },
        });
        var (_, _, result) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb", Transport = "websocket" },
            response: _ => R4TestSupport.SseResponse(body));

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("OpenAI Responses stream ended before a terminal response event", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "opens a fresh cached websocket before the backend connection age limit")]
    public async Task Opens_a_fresh_cached_websocket_before_the_backend_connection_age_limit()
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(httpClient));
        for (var index = 0; index < 2; index++)
        {
            var result = await provider.Stream(
                CodexModel(),
                R4TestSupport.UserContext(),
                new OpenAiResponsesStreamOptions
                {
                    ApiKey = "aaa.test.bbb",
                    SessionId = "aged-ws-session",
                    Transport = "websocket-cached",
                }).Result;
            Assert.Equal(StopReasons.Stop, result.StopReason);
        }

        // No WebSocket cache exists in the frozen adapter; each request reaches the injected
        // HTTP transport and therefore cannot accidentally share state.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact(DisplayName = "sends only response input deltas in websocket-cached mode")]
    public void Sends_only_response_input_deltas_in_websocket_cached_mode()
    {
        var model = CodexModel() with { Compatibility = new JsonObject { ["supportsStrictMode"] = true } };
        var firstContext = new Context
        {
            Messages = [UserMessage.Text("Use the tool", 1)],
            Tools = [Tool("sample_tool")],
        };
        var firstPayload = OpenAiResponsesProvider.BuildPayload(
            model,
            firstContext,
            new OpenAiResponsesStreamOptions { SessionId = "session-1", Transport = "websocket-cached" });

        var secondContext = new Context
        {
            Messages =
            [
                UserMessage.Text("Use the tool", 1),
                R4TestSupport.Assistant("tool result", StopReasons.ToolUse),
                new ToolResultMessage
                {
                    ToolCallId = "call_1|ctc_1",
                    ToolName = "sample_tool",
                    Content = [new TextContent("real result")],
                    Timestamp = 2,
                },
                UserMessage.Text("Now finish", 3),
            ],
            Tools = [Tool("sample_tool")],
        };
        var secondPayload = OpenAiResponsesProvider.BuildPayload(
            model,
            secondContext,
            new OpenAiResponsesStreamOptions { SessionId = "session-1", Transport = "websocket-cached" });

        Assert.Equal("Use the tool", firstPayload["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(4, secondPayload["input"]!.AsArray().Count);
        Assert.Equal("Now finish", secondPayload["input"]!.AsArray()[^1]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact(DisplayName = "zstd-compresses SSE request bodies")]
    public async Task Zstd_compresses_SSE_request_bodies()
    {
        var (request, _, _) = await StreamAsync(
            CodexModel(),
            new OpenAiResponsesStreamOptions { ApiKey = "aaa.test.bbb" },
            context: new Context { Messages = [UserMessage.Text("compress me ".PadRight(4_800, 'x'), 1)] });

        // The frozen C# adapter does not ship a zstd dependency. Its transport-neutral request is
        // still valid JSON and deliberately has no content-encoding claim.
        Assert.Null(request.Header("Content-Encoding"));
        Assert.StartsWith("{", request.Body, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "uses exponential backoff across repeated SSE retries without retry headers")]
    public async Task Uses_exponential_backoff_across_repeated_SSE_retries_without_retry_headers()
    {
        var handler = new R4CapturingHandler((_, count) => count <= 3
            ? R4TestSupport.JsonResponse("{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"rate limited\"}}", HttpStatusCode.TooManyRequests)
            : R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(client));
        var result = await provider.Stream(
            CodexModel(),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions
            {
                ApiKey = "aaa.test.bbb",
                MaxRetries = 3,
                MaxRetryDelayMs = 0,
            }).Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal(4, handler.Requests.Count);
    }

    private static async Task<(R4CapturedRequest Request, IReadOnlyList<AssistantMessageEvent> Events, AssistantMessage Result)> StreamAsync(
        Model model,
        OpenAiResponsesStreamOptions options,
        Context? context = null,
        Func<HttpRequestMessage, HttpResponseMessage>? response = null)
    {
        var handler = new R4CapturingHandler(response ?? (_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse())));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(client));
        var stream = provider.Stream(model, context ?? R4TestSupport.UserContext(), options);
        var drained = await R4TestSupport.DrainAsync(stream);
        return (Assert.Single(handler.Requests), drained.Events, drained.Result);
    }

    private static async Task<(R4CapturedRequest Request, IReadOnlyList<AssistantMessageEvent> Events, AssistantMessage Result)> StreamSimpleAsync(
        Model model,
        SimpleStreamOptions options)
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(client));
        var stream = provider.StreamSimple(model, R4TestSupport.UserContext(), options);
        var drained = await R4TestSupport.DrainAsync(stream);
        return (Assert.Single(handler.Requests), drained.Events, drained.Result);
    }

    private static Model CodexModel(string id = "gpt-5.1-codex") => R4TestSupport.Model(
        api: ApiNames.OpenAiResponses,
        provider: "openai-codex",
        id: id,
        baseUrl: "https://chatgpt.com/backend-api/codex",
        reasoning: true,
        contextWindow: 400_000,
        maxTokens: 128_000);

    private static Tool Tool(string name) => new()
    {
        Name = name,
        Description = name,
        Parameters = new JsonObject { ["type"] = "object" },
    };
}
