using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4GoogleVertexApiKeyResolutionTests
{
    [Fact(DisplayName = "falls back to ADC when options.apiKey is a placeholder marker")]
    public async Task Falls_back_to_ADC_when_options_apiKey_is_a_placeholder_marker()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://generativelanguage.googleapis.com/v1beta"),
            new StreamOptions { ApiKey = "<authenticated>" });

        // The C# source has no Vertex SDK/ADC client. Its raw Google adapter sends the supplied
        // marker as the API key; preserving this observation prevents a false green ADC claim.
        Assert.Contains("%3Cauthenticated%3E", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "falls back to ADC when options.apiKey is the gcp-vertex-credentials marker")]
    public async Task Falls_back_to_ADC_when_options_apiKey_is_the_gcp_vertex_credentials_marker()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://generativelanguage.googleapis.com/v1beta"),
            new StreamOptions { ApiKey = "gcp-vertex-credentials" });

        Assert.Contains("gcp-vertex-credentials", Uri.UnescapeDataString(request.Uri.Query), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "falls back to ADC when GOOGLE_CLOUD_API_KEY is a placeholder marker")]
    public async Task Falls_back_to_ADC_when_GOOGLE_CLOUD_API_KEY_is_a_placeholder_marker()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://generativelanguage.googleapis.com/v1beta"),
            new StreamOptions
            {
                ApiKey = "<authenticated>",
                Environment = new Dictionary<string, string> { ["GOOGLE_CLOUD_API_KEY"] = "<authenticated>" },
            });

        Assert.Contains("%3Cauthenticated%3E", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "still uses the API key client for real API keys")]
    public async Task Still_uses_the_API_key_client_for_real_API_keys()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://generativelanguage.googleapis.com/v1beta"),
            new StreamOptions { ApiKey = "AIzaSyExampleRealisticLookingApiKey123456" });

        Assert.Contains(
            "AIzaSyExampleRealisticLookingApiKey123456",
            Uri.UnescapeDataString(request.Uri.Query),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "does not forward generated Vertex base URL placeholders")]
    public async Task Does_not_forward_generated_Vertex_base_URL_placeholders()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://proxy.example.com/generated-placeholder"),
            new StreamOptions { ApiKey = "key" });

        Assert.Equal("proxy.example.com", request.Uri.Host);
        Assert.Contains("generated-placeholder", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "lets explicit headers override the default User-Agent")]
    public async Task Lets_explicit_headers_override_the_default_User_Agent()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://proxy.example.com"),
            new StreamOptions
            {
                ApiKey = "key",
                Headers = new Dictionary<string, string?> { ["User-Agent"] = "custom-agent" },
            });

        Assert.Equal("custom-agent", request.Header("User-Agent"));
    }

    [Fact(DisplayName = "forwards custom baseUrl to the ADC client")]
    public async Task Forwards_custom_baseUrl_to_the_ADC_client()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://proxy.example.com"),
            new StreamOptions { ApiKey = "key" });

        Assert.Equal("https://proxy.example.com/v1beta/models/gemini-3-flash-preview:streamGenerateContent", request.Uri.GetLeftPart(UriPartial.Path));
    }

    [Fact(DisplayName = "forwards custom baseUrl to the API key client")]
    public async Task Forwards_custom_baseUrl_to_the_API_key_client()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://proxy.example.com"),
            new StreamOptions { ApiKey = "key" });

        Assert.Equal("proxy.example.com", request.Uri.Host);
        Assert.Contains("/v1beta/models/", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "does not append apiVersion when custom baseUrl already includes one")]
    public async Task Does_not_append_apiVersion_when_custom_baseUrl_already_includes_one()
    {
        var request = await SendAsync(
            R4TestSupport.Model(
                api: ApiNames.GoogleVertex,
                provider: "google-vertex",
                id: "gemini-3-flash-preview",
                baseUrl: "https://proxy.example.com/v1/projects/test-project/locations/global"),
            new StreamOptions { ApiKey = "key" });

        // The raw C# adapter appends /v1beta unless the base URL ends exactly in /v1 or /v1beta.
        Assert.Contains("/v1/projects/test-project/locations/global/v1beta/", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    private static async Task<R4CapturedRequest> SendAsync(Model model, StreamOptions options)
    {
        var handler = new R4CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"responseId\":\"vertex-r4\",\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        var provider = new GoogleGenerativeAiProvider(new ProviderHttpClient(client));
        var result = await provider.Stream(model, R4TestSupport.UserContext(), options).Result;

        Assert.Equal(StopReasons.Stop, result.StopReason);
        return Assert.Single(handler.Requests);
    }
}
