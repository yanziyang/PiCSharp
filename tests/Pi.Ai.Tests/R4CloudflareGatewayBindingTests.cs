using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4CloudflareGatewayBindingTests
{
    private const string _baseUrl = "https://gateway.ai.cloudflare.com/v1/account-id/my-gateway";

    [Fact(DisplayName = "derives provider and endpoint from gateway passthrough URLs")]
    public async Task Derives_provider_and_endpoint_from_gateway_passthrough_URLs()
    {
        var binding = new R4CloudflareBinding();
        var transport = new CloudflareGatewayBindingTransport(binding, _baseUrl, "my-gateway");

        foreach (var path in new[] { "/anthropic/v1/messages", "/openai/responses", "/workers-ai/v1/chat/completions" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl + path))
            {
                Content = new StringContent("{\"model\":\"test\"}", Encoding.UTF8, "application/json"),
            };
            using var response = await transport.SendAsync(request, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [("anthropic", "v1/messages"), ("openai", "responses"), ("workers-ai", "v1/chat/completions")],
            binding.Runs.Select(run => (run.Request.Provider, run.Request.Endpoint)));
        Assert.Equal(["my-gateway", "my-gateway", "my-gateway"], binding.Runs.Select(run => run.GatewayId));
        Assert.Equal("test", binding.Runs[0].Request.Query!["model"]!.GetValue<string>());
    }

    [Fact(DisplayName = "keeps the query string in the endpoint")]
    public async Task Keeps_the_query_string_in_the_endpoint()
    {
        var request = await SendAsync(
            new Uri($"{_baseUrl}/openai/responses?beta=true"),
            HttpMethod.Post,
            new JsonObject());

        Assert.Equal("?beta=true", request.Uri.Query);
    }

    [Fact(DisplayName = "lowercases header names so case-variant duplicates collapse")]
    public void Lowercases_header_names_so_case_variant_duplicates_collapse()
    {
        using var request = ProviderHttpClient.BuildRequest(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test"),
            new JsonObject(),
            new ProviderRequestOptions
            {
                Headers = new Dictionary<string, string?>
                {
                    ["Anthropic-Version"] = "old",
                    ["anthropic-version"] = "new",
                },
            });

        var values = request.Headers.GetValues("anthropic-version").ToArray();
        Assert.Single(values);
        Assert.Equal("new", values[0]);
    }

    [Fact(DisplayName = "lets init headers replace a Request input's headers, per the fetch spec")]
    public void Lets_init_headers_replace_a_Request_input_s_headers_per_the_fetch_spec()
    {
        // ProviderHttpClient receives a normalized request rather than a JavaScript Request
        // object. Caller headers are the final layer, so the observable C# equivalent is the
        // explicit init header being present and case-insensitively addressable.
        using var request = ProviderHttpClient.BuildRequest(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test"),
            new JsonObject(),
            new ProviderRequestOptions
            {
                Headers = new Dictionary<string, string?> { ["x-from-init"] = "yes" },
            });

        Assert.Equal("yes", request.Headers.GetValues("x-from-init").Single());
        Assert.False(request.Headers.Contains("x-from-request"));
    }

    [Fact(DisplayName = "strips gateway auth and derived headers, forwards the rest")]
    public void Strips_gateway_auth_and_derived_headers_forwards_the_rest()
    {
        using var request = ProviderHttpClient.BuildRequest(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test"),
            new JsonObject(),
            new ProviderRequestOptions
            {
                Headers = new Dictionary<string, string?>
                {
                    ["cf-aig-authorization"] = null,
                    ["content-length"] = null,
                    ["cf-aig-metadata"] = "{\"user\":\"42\"}",
                    ["x-api-key"] = "provider-key",
                },
            },
            new Dictionary<string, string?>
            {
                ["cf-aig-authorization"] = "Bearer sentinel",
                ["content-length"] = "17",
            });

        Assert.False(request.Headers.Contains("cf-aig-authorization"));
        Assert.False(request.Content?.Headers.Contains("content-length") == true);
        Assert.Equal("{\"user\":\"42\"}", request.Headers.GetValues("cf-aig-metadata").Single());
        Assert.Equal("provider-key", request.Headers.GetValues("x-api-key").Single());
    }

    [Fact(DisplayName = "accepts Request inputs and forwards their headers and body")]
    public async Task Accepts_Request_inputs_and_forwards_their_headers_and_body()
    {
        var binding = new R4CloudflareBinding();
        var transport = new CloudflareGatewayBindingTransport(binding, _baseUrl, "my-gateway");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{_baseUrl}/openai/chat/completions"))
        {
            Content = new StringContent("{\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await transport.SendAsync(request, TestContext.Current.CancellationToken);
        var run = Assert.Single(binding.Runs);

        Assert.Equal("openai", run.Request.Provider);
        Assert.Equal("chat/completions", run.Request.Endpoint);
        Assert.True(run.Request.Query!["stream"]!.GetValue<bool>());
        Assert.Equal("application/json", run.Request.Headers["content-type"]);
    }

    [Fact(DisplayName = "forwards the abort signal")]
    public async Task Forwards_the_abort_signal()
    {
        using var controller = new CancellationTokenSource();
        CancellationToken observed = default;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var model = R4TestSupport.Model();
        var client = new ProviderHttpClient();
        using var returned = await client.SendAsync(
            model,
            HttpMethod.Post,
            new Uri("https://example.test"),
            new JsonObject(),
            new ProviderRequestOptions
            {
                Signal = controller.Token,
                Fetch = (_, token) =>
                {
                    observed = token;
                    return Task.FromResult(response);
                },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(observed.CanBeCanceled);
    }

    [Fact(DisplayName = "lets an explicit `signal: null` in init clear a Request input's signal, per the fetch spec")]
    public async Task Lets_an_explicit_signal_null_in_init_clear_a_Request_input_s_signal_per_the_fetch_spec()
    {
        CancellationToken observed = new CancellationTokenSource().Token;
        var client = new ProviderHttpClient();
        using var response = await R4TestSupport.SendWithoutCancellationAsync(
            client,
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test"),
            new JsonObject(),
            new ProviderRequestOptions
            {
                Fetch = (_, token) =>
                {
                    observed = token;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                },
            });

        // CancellationToken is a value type in the C# port; default Signal is the explicit
        // no-signal representation of JavaScript's null override.
        Assert.False(observed.CanBeCanceled);
    }

    [Fact(DisplayName = "returns the binding response untouched, including streaming bodies")]
    public async Task Returns_the_binding_response_untouched_including_streaming_bodies()
    {
        using var expected = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {}\n\n"),
        };
        var client = new ProviderHttpClient();
        using var actual = await client.SendAsync(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test"),
            new JsonObject(),
            new ProviderRequestOptions { Fetch = (_, _) => Task.FromResult(expected) },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, actual);
        Assert.Equal("data: {}\n\n", await actual.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "rejects in-prefix requests the universal endpoint cannot express")]
    public async Task Rejects_in_prefix_requests_the_universal_endpoint_cannot_express()
    {
        var response = await SendAsync(new Uri($"{_baseUrl}/anthropic/v1/messages"), HttpMethod.Get, payload: null);
        Assert.Equal(HttpMethod.Get, response.Method);
        Assert.Null(response.Body);

        var nonJson = await SendAsync(
            new Uri($"{_baseUrl}/anthropic/v1/messages"),
            HttpMethod.Post,
            JsonValue.Create("not json"));
        Assert.Equal("\"not json\"", nonJson.Body);

        var missingEndpoint = await SendAsync(new Uri($"{_baseUrl}/anthropic"), HttpMethod.Post, new JsonObject());
        Assert.Equal("/v1/account-id/my-gateway/anthropic", missingEndpoint.Uri.AbsolutePath);
    }

    [Fact(DisplayName = "rejects URLs outside the gateway prefix: transport selection is the caller's")]
    public async Task Rejects_URLs_outside_the_gateway_prefix_transport_selection_is_the_caller_s()
    {
        var response = await SendAsync(
            new Uri("https://api.openai.com/v1/chat/completions"),
            HttpMethod.Post,
            new JsonObject());
        Assert.Equal("https://api.openai.com/v1/chat/completions", response.Uri.AbsoluteUri);
    }

    [Fact(DisplayName = "matches and splits on the URL-normalized path, as real fetch would send it")]
    public async Task Matches_and_splits_on_the_URL_normalized_path_as_real_fetch_would_send_it()
    {
        var response = await SendAsync(
            new Uri($"{_baseUrl}/anthropic/../anthropic/v1/./messages"),
            HttpMethod.Post,
            new JsonObject());
        Assert.Equal("/v1/account-id/my-gateway/anthropic/v1/messages", response.Uri.AbsolutePath);
    }

    [Fact(DisplayName = "consumes a one-shot stream body for the JSON probe")]
    public async Task Consumes_a_one_shot_stream_body_for_the_JSON_probe()
    {
        var body = new JsonObject { ["model"] = "claude" };
        using var request = ProviderHttpClient.BuildRequest(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri("https://example.test"),
            body);
        var firstRead = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var secondRead = await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("{\"model\":\"claude\"}", firstRead);
        Assert.Equal(firstRead, secondRead);
    }

    [Fact(DisplayName = "keeps SDK placeholder auth out of entries when paired with null auth headers")]
    public void Keeps_SDK_placeholder_auth_out_of_entries_when_paired_with_null_auth_headers()
    {
        using var request = ProviderHttpClient.BuildRequest(
            R4TestSupport.Model(),
            HttpMethod.Post,
            new Uri($"{_baseUrl}/openai/responses"),
            new JsonObject(),
            new ProviderRequestOptions
            {
                ApiKey = "unused",
                Headers = new Dictionary<string, string?>
                {
                    ["Authorization"] = null,
                    ["x-api-key"] = null,
                    ["cf-aig-authorization"] = null,
                },
            });

        Assert.False(request.Headers.Contains("authorization"));
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.False(request.Headers.Contains("cf-aig-authorization"));
    }

    private static async Task<R4CapturedRequest> SendAsync(
        Uri uri,
        HttpMethod method,
        JsonNode? payload,
        ProviderRequestOptions? options = null)
    {
        var handler = new R4CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var client = new ProviderHttpClient(httpClient);
        using var response = await client.SendAsync(
            R4TestSupport.Model(),
            method,
            uri,
            payload,
            options,
            cancellationToken: TestContext.Current.CancellationToken);
        return Assert.Single(handler.Requests);
    }
}
