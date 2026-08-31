using System.Net;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4ProviderErrorBodyRegressionTests
{
    [Fact(DisplayName = "openai-completions (body-blind text) surfaces status + body")]
    public async Task Openai_completions_body_blind_text_surfaces_status_and_body()
    {
        var provider = new OpenAiCompletionsProvider(
            new ProviderHttpClient(
                new HttpClient(
                    new R4CapturingHandler(_ => R4TestSupport.JsonResponse(
                        "{\"error\":\"blocked by gateway WAF\"}",
                        HttpStatusCode.Forbidden)))));

        var stream = provider.Stream(
            R4TestSupport.Model(
                api: ApiNames.OpenAiCompletions,
                provider: "openrouter",
                baseUrl: "https://openrouter.ai/api/v1"),
            R4TestSupport.UserContext(),
            new StreamOptions { ApiKey = "test" });
        var result = await stream.Result;

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("403", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("blocked by gateway WAF", result.ErrorMessage, StringComparison.Ordinal);
        Assert.NotEqual("403 status code (no body)", result.ErrorMessage);
    }

    [Fact(DisplayName = "openai-completions does not double-print the OpenRouter metadata.raw extra")]
    public async Task Openai_completions_does_not_double_print_the_OpenRouter_metadata_raw_extra()
    {
        const string reason = "upstream WAF blocked policy XYZ";
        var body = "{\"message\":\"Provider returned error\",\"code\":403,\"metadata\":{\"raw\":\"" + reason + "\"}}";
        var provider = new OpenAiCompletionsProvider(
            new ProviderHttpClient(
                new HttpClient(
                    new R4CapturingHandler(_ => R4TestSupport.JsonResponse(body, HttpStatusCode.Forbidden)))));

        var result = await provider.Stream(
            R4TestSupport.Model(
                api: ApiNames.OpenAiCompletions,
                provider: "openrouter",
                baseUrl: "https://openrouter.ai/api/v1"),
            R4TestSupport.UserContext(),
            new StreamOptions { ApiKey = "test" }).Result;

        Assert.Contains(reason, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result.ErrorMessage, reason));
    }

    [Fact(DisplayName = "openai-responses (status-only) keeps the prefix and surfaces the body")]
    public async Task Openai_responses_status_only_keeps_the_prefix_and_surfaces_the_body()
    {
        var provider = new OpenAiResponsesProvider(
            new ProviderHttpClient(
                new HttpClient(
                    new R4CapturingHandler(_ => R4TestSupport.JsonResponse(
                        "{\"error\":\"blocked by gateway WAF\"}",
                        HttpStatusCode.Forbidden)))));

        var result = await provider.Stream(
            R4TestSupport.Model(baseUrl: "https://api.openai.com/v1"),
            R4TestSupport.UserContext(),
            new StreamOptions { ApiKey = "test" }).Result;

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("OpenAI API error (403)", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("blocked by gateway WAF", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "bedrock (body-blind) surfaces the gateway body instead of Unknown: UnknownError")]
    public async Task Bedrock_body_blind_surfaces_the_gateway_body_instead_of_Unknown_UnknownError()
    {
        var transport = new R4BedrockTransport
        {
            Error = new BedrockConverseTransportException(
                "UnknownError",
                status: 403,
                responseBody: "{\"message\":\"blocked by gateway WAF\"}"),
        };
        var result = await new BedrockConverseProvider(transport).StreamSimple(
            R4TestSupport.Model(
                api: ApiNames.BedrockConverseStream,
                provider: "amazon-bedrock",
                baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com"),
            R4TestSupport.UserContext(),
            new SimpleStreamOptions()).Result;

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("403", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("blocked by gateway WAF", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown: UnknownError", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "bedrock preserves the SDK validation message when the response body is a stream")]
    public async Task Bedrock_preserves_the_SDK_validation_message_when_the_response_body_is_a_stream()
    {
        var transport = new R4BedrockTransport
        {
            Error = new BedrockConverseTransportException(
                "Invocation of model ID anthropic.claude-opus-5 with on-demand throughput isn't supported. Retry with an inference profile.",
                status: 400,
                responseBody: null),
        };
        var result = await new BedrockConverseProvider(transport).StreamSimple(
            R4TestSupport.Model(
                api: ApiNames.BedrockConverseStream,
                provider: "amazon-bedrock",
                id: "global.anthropic.claude-opus-5",
                baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com"),
            R4TestSupport.UserContext(),
            new SimpleStreamOptions()).Result;

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("on-demand throughput isn't supported", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("inference profile", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("_readableState", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string? value, string needle)
    {
        if (string.IsNullOrEmpty(value) || needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }
}

public sealed class R4ProviderErrorBodyPassthroughTests
{
    [Fact(DisplayName = "surfaces the HTTP body reason instead of the opaque SDK message (openrouter images)")]
    public void Surfaces_the_HTTP_body_reason_instead_of_the_opaque_SDK_message_openrouter_images()
    {
        // Pi.Ai currently exposes the provider-neutral image contract but no OpenRouter image
        // adapter. Exercise the same body-blind error path through the public normalizer so this
        // regression remains protected until that adapter is introduced.
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "403 status code (no body)",
                new ProviderErrorMetadata
                {
                    Status = 403,
                    ParsedError = new System.Text.Json.Nodes.JsonObject { ["error"] = "blocked by gateway WAF" },
                }));
        var output = ErrorBodyUtilities.FormatProviderError(normalized);

        Assert.Contains("403", output, StringComparison.Ordinal);
        Assert.Contains("blocked by gateway WAF", output, StringComparison.Ordinal);
        Assert.NotEqual("403 status code (no body)", output);
    }
}
