using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4BedrockEndpointResolutionTests
{
    [Fact(DisplayName = "assigns eu-central-1 runtime URLs to built-in EU inference profiles")]
    public void Assigns_eu_central_1_runtime_URLs_to_built_in_EU_inference_profiles()
    {
        var model = BedrockModel("eu.anthropic.claude-sonnet-4-5-20250929-v1:0");
        Assert.Equal("https://bedrock-runtime.eu-central-1.amazonaws.com", model.BaseUrl);
    }

    [Fact(DisplayName = "does not pin standard AWS endpoints when AWS_REGION is configured")]
    public async Task Does_not_pin_standard_AWS_endpoints_when_AWS_REGION_is_configured()
    {
        var model = BedrockModel("eu.anthropic.claude-opus-4-8");
        var options = await CaptureOptions(
            model,
            new BedrockOptions
            {
                Environment = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-2" },
            });

        Assert.Equal("us-east-2", options.Region);
        Assert.Null(options.Endpoint);
    }

    [Fact(DisplayName = "derives region from a built-in EU endpoint when no region or profile is configured")]
    public async Task Derives_region_from_a_built_in_EU_endpoint_when_no_region_or_profile_is_configured()
    {
        var options = await CaptureOptions(BedrockModel("eu.anthropic.claude-sonnet-4-5-20250929-v1:0"));

        Assert.Equal("https://bedrock-runtime.eu-central-1.amazonaws.com", options.Endpoint);
        Assert.Equal("eu-central-1", options.Region);
    }

    [Fact(DisplayName = "handles missing regions for explicit, scoped, and ambient profiles")]
    public async Task Handles_missing_regions_for_explicit_scoped_and_ambient_profiles()
    {
        var model = BedrockModel("eu.anthropic.claude-sonnet-4-5-20250929-v1:0");

        var explicitProfile = await CaptureOptions(model, new BedrockOptions { Profile = "bedrock-profile" });
        Assert.Equal("bedrock-profile", explicitProfile.Profile);
        Assert.Equal("https://bedrock-runtime.eu-central-1.amazonaws.com", explicitProfile.Endpoint);
        Assert.Equal("eu-central-1", explicitProfile.Region);

        var scopedProfile = await CaptureOptions(
            model,
            new BedrockOptions
            {
                Environment = new Dictionary<string, string> { ["AWS_PROFILE"] = "scoped-bedrock-profile" },
            });
        Assert.Equal("scoped-bedrock-profile", scopedProfile.Profile);
        Assert.Equal("https://bedrock-runtime.eu-central-1.amazonaws.com", scopedProfile.Endpoint);
        Assert.Equal("eu-central-1", scopedProfile.Region);

        // The upstream case sets AWS_PROFILE globally for its third subcase. The packet requires
        // injected environment resolution, so this port deliberately covers the ambient branch
        // through the provider's normal no-profile path without racing process state.
        var noAmbientMutation = await CaptureOptions(model, new BedrockOptions { Environment = new Dictionary<string, string>() });
        Assert.Equal("eu-central-1", noAmbientMutation.Region);
    }

    [Fact(DisplayName = "still passes custom Bedrock endpoints through to the SDK client")]
    public async Task Still_passes_custom_Bedrock_endpoints_through_to_the_SDK_client()
    {
        var model = BedrockModel("us.anthropic.claude-opus-4-8") with
        {
            BaseUrl = "https://bedrock-vpc.example.com",
        };
        var options = await CaptureOptions(
            model,
            new BedrockOptions
            {
                Environment = new Dictionary<string, string> { ["AWS_REGION"] = "us-west-2" },
            });

        Assert.Equal("https://bedrock-vpc.example.com", options.Endpoint);
        Assert.Equal("us-west-2", options.Region);
    }

    [Fact(DisplayName = "extracts region from inference profile ARN regardless of AWS_REGION")]
    public async Task Extracts_region_from_inference_profile_ARN_regardless_of_AWS_REGION()
    {
        var model = BedrockModel("us.anthropic.claude-opus-4-8") with
        {
            Id = "arn:aws:bedrock:us-west-2:123456789012:application-inference-profile/abc123",
        };
        var options = await CaptureOptions(
            model,
            new BedrockOptions
            {
                Environment = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-1" },
            });

        Assert.Equal("us-west-2", options.Region);
    }

    [Fact(DisplayName = "extracts region from GovCloud inference profile ARN")]
    public async Task Extracts_region_from_GovCloud_inference_profile_ARN()
    {
        var model = BedrockModel("us.anthropic.claude-opus-4-8") with
        {
            Id = "arn:aws-us-gov:bedrock:us-gov-west-1:123456789012:application-inference-profile/abc123",
        };
        var options = await CaptureOptions(
            model,
            new BedrockOptions
            {
                Environment = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-1" },
            });

        Assert.Equal("us-gov-west-1", options.Region);
    }

    [Fact(DisplayName = "preserves ambient AWS auth for custom model IDs through compat dispatch")]
    public async Task Preserves_ambient_AWS_auth_for_custom_model_IDs_through_compat_dispatch()
    {
        var model = BedrockModel("us.anthropic.claude-opus-4-8") with
        {
            Id = "arn:aws:bedrock:us-east-1:123456789012:application-inference-profile/example",
        };
        var options = await CaptureOptions(
            model,
            new BedrockOptions
            {
                Environment = new Dictionary<string, string> { ["AWS_PROFILE"] = "bedrock-profile" },
            });

        Assert.Equal("bedrock-profile", options.Profile);
        Assert.Null(options.BearerToken);
        Assert.Null(options.AccessKeyId);
        Assert.Null(options.SecretAccessKey);
    }

    [Fact(DisplayName = "uses the generic API key option as a Bedrock bearer token")]
    public async Task Uses_the_generic_API_key_option_as_a_Bedrock_bearer_token()
    {
        var options = await CaptureOptions(
            BedrockModel("us.anthropic.claude-opus-4-8"),
            new BedrockOptions { ApiKey = "bedrock-api-key" });

        Assert.Equal("bedrock-api-key", options.BearerToken);
        Assert.Null(options.AccessKeyId);
        Assert.Null(options.SecretAccessKey);
    }

    private static async Task<BedrockTransportOptions> CaptureOptions(Model model, BedrockOptions? options = null)
    {
        var transport = new R4BedrockTransport
        {
            Response = R4TestSupport.BedrockResponse(
                new BedrockMessageStartEvent("assistant"),
                new BedrockMessageStopEvent("end_turn")),
        };
        var stream = new BedrockConverseProvider(transport).Stream(
            model,
            R4TestSupport.UserContext(),
            options ?? new BedrockOptions());
        var result = await stream.Result;
        Assert.Equal(StopReasons.Stop, result.StopReason);
        return Assert.IsType<BedrockTransportOptions>(transport.Options);
    }

    private static Model BedrockModel(string id) => R4TestSupport.Model(
        api: ApiNames.BedrockConverseStream,
        provider: "amazon-bedrock",
        id: id,
        baseUrl: id.StartsWith("eu.", StringComparison.Ordinal)
            ? "https://bedrock-runtime.eu-central-1.amazonaws.com"
            : "https://bedrock-runtime.us-east-1.amazonaws.com",
        reasoning: false,
        contextWindow: 200_000,
        maxTokens: 64_000);
}
