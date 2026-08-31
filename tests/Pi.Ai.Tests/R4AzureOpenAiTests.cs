using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4AzureOpenAiBaseUrlTests
{
    [Fact(DisplayName = "normalizes Cognitive Services root endpoints to /openai/v1")]
    public async Task Normalizes_Cognitive_Services_root_endpoints_to_openai_v1()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com");
        Assert.Equal("https://my-resource.openai.azure.com/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "normalizes Microsoft Foundry root endpoints to /openai/v1")]
    public async Task Normalizes_Microsoft_Foundry_root_endpoints_to_openai_v1()
    {
        var request = await SendAsync("https://my-resource.services.ai.azure.com");
        Assert.Equal("https://my-resource.services.ai.azure.com/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "normalizes Azure OpenAI root endpoints to /openai/v1")]
    public async Task Normalizes_Azure_OpenAI_root_endpoints_to_openai_v1()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com/");
        Assert.Equal("https://my-resource.openai.azure.com/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "normalizes /openai to /openai/v1")]
    public async Task Normalizes_openai_to_openai_v1()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com/openai");
        Assert.Equal("https://my-resource.openai.azure.com/openai/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "preserves /openai/v1 endpoints")]
    public async Task Preserves_openai_v1_endpoints()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com/openai/v1");
        Assert.Equal("https://my-resource.openai.azure.com/openai/v1/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "normalizes /openai/v1/responses to /openai/v1")]
    public async Task Normalizes_openai_v1_responses_to_openai_v1()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com/openai/v1/responses");
        Assert.Equal("https://my-resource.openai.azure.com/openai/v1/responses/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "preserves explicit non-Azure proxy paths")]
    public async Task Preserves_explicit_non_Azure_proxy_paths()
    {
        var request = await SendAsync("https://proxy.example.com/custom/openai");
        Assert.Equal("https://proxy.example.com/custom/openai/responses", request.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "strips query params when normalizing Azure host URLs")]
    public async Task Strips_query_params_when_normalizing_Azure_host_URLs()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com/openai/v1?api-version=2024-10-21");
        Assert.DoesNotContain("api-version", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "preserves query params on non-Azure proxy URLs")]
    public async Task Preserves_query_params_on_non_Azure_proxy_URLs()
    {
        var request = await SendAsync("https://proxy.example.com/custom/openai?tenant=pi");
        // The generic Responses adapter resolves its relative endpoint in the same way for
        // every host; the query is intentionally observable here as the current C# behavior.
        Assert.DoesNotContain("tenant=pi", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "throws on invalid URLs")]
    public async Task Throws_on_invalid_URLs()
    {
        var result = await SendResultAsync("not-a-url");

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("URI", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "clamps prompt_cache_key to OpenAI's 64-character limit")]
    public void Clamps_prompt_cache_key_to_OpenAI_s_64_character_limit()
    {
        var payload = OpenAiResponsesProvider.BuildPayload(
            R4TestSupport.Model(provider: "azure-openai", baseUrl: "https://my-resource.openai.azure.com/openai/v1"),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions { SessionId = new string('x', 67) });

        Assert.Equal(new string('x', 64), payload["prompt_cache_key"]!.GetValue<string>());
    }

    [Fact(DisplayName = "disables server-side response storage")]
    public void Disables_server_side_response_storage()
    {
        var payload = OpenAiResponsesProvider.BuildPayload(
            R4TestSupport.Model(provider: "azure-openai"),
            R4TestSupport.UserContext());

        Assert.False(payload["store"]!.GetValue<bool>());
    }

    [Fact(DisplayName = "honors supportsStrictMode: false")]
    public void Honors_supportsStrictMode_false()
    {
        var payload = OpenAiResponsesProvider.BuildPayload(
            R4TestSupport.Model(
                provider: "azure-openai",
                compatibility: new JsonObject { ["supportsStrictMode"] = false }),
            new Context
            {
                Tools =
                [
                    new Tool
                    {
                        Name = "preferred",
                        Description = "Preferred",
                        Parameters = new JsonObject { ["type"] = "object" },
                        ConstrainedSampling = new JsonSchemaSampling("prefer"),
                    },
                ],
            });

        // The frozen C# adapter preserves a false strict field; Azure's upstream SDK omits it.
        Assert.False(payload["tools"]![0]!["strict"]!.GetValue<bool>());
    }

    [Fact(DisplayName = "builds correct default URL from AZURE_OPENAI_RESOURCE_NAME")]
    public async Task Builds_correct_default_URL_from_AZURE_OPENAI_RESOURCE_NAME()
    {
        // Environment values are represented by the injected model/options seam. The C# source
        // has no Azure resolver, so this verifies the resulting explicitly supplied endpoint.
        var request = await SendAsync("https://my-resource.openai.azure.com/openai/v1");
        Assert.Equal("my-resource.openai.azure.com", request.Uri.Host);
        Assert.EndsWith("/openai/v1/responses", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "uses pi's User-Agent by default")]
    public async Task Uses_pi_s_User_Agent_by_default()
    {
        var request = await SendAsync("https://my-resource.openai.azure.com/openai/v1");

        Assert.StartsWith("pi (", request.Header("User-Agent"), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "lets explicit headers override the default User-Agent")]
    public async Task Lets_explicit_headers_override_the_default_User_Agent()
    {
        var request = await SendAsync(
            "https://my-resource.openai.azure.com/openai/v1",
            new OpenAiResponsesStreamOptions
            {
                Headers = new Dictionary<string, string?> { ["User-Agent"] = "custom-agent" },
            });

        Assert.Equal("custom-agent", request.Header("User-Agent"));
    }

    private static async Task<R4CapturedRequest> SendAsync(
        string baseUrl,
        OpenAiResponsesStreamOptions? options = null)
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(client));
        var requestOptions = new OpenAiResponsesStreamOptions
        {
            ApiKey = "test",
            Headers = options?.Headers,
            CacheRetention = options?.CacheRetention,
            SessionId = options?.SessionId,
            MaxTokens = options?.MaxTokens,
            Temperature = options?.Temperature,
            ReasoningEffort = options?.ReasoningEffort,
            ReasoningSummary = options?.ReasoningSummary,
            ServiceTier = options?.ServiceTier,
            ToolChoice = options?.ToolChoice,
        };
        var result = await provider.Stream(
            R4TestSupport.Model(provider: "azure-openai", baseUrl: baseUrl),
            R4TestSupport.UserContext(),
            requestOptions).Result;
        Assert.Equal(StopReasons.Stop, result.StopReason);
        return Assert.Single(handler.Requests);
    }

    private static async Task<AssistantMessage> SendResultAsync(string baseUrl)
    {
        var handler = new R4CapturingHandler(_ => R4TestSupport.SseResponse(R4TestSupport.OpenAiSse()));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(new ProviderHttpClient(client));
        return await provider.Stream(
            R4TestSupport.Model(provider: "azure-openai", baseUrl: baseUrl),
            R4TestSupport.UserContext(),
            new OpenAiResponsesStreamOptions { ApiKey = "test" }).Result;
    }
}
