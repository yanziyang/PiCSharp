using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4OverflowTests
{
    [Fact(DisplayName = "detects explicit Ollama prompt-too-long errors")]
    public void Detects_explicit_Ollama_prompt_too_long_errors()
    {
        Assert.True(IsOverflow("400 `prompt too long; exceeded max context length by 100918 tokens`"));
    }

    [Fact(DisplayName = "detects Together AI context length errors")]
    public void Detects_Together_AI_context_length_errors()
    {
        Assert.True(IsOverflow("400 The input (516368 tokens) is longer than the model's context length (262144 tokens)."));
    }

    [Fact(DisplayName = "detects LiteLLM-wrapped OpenAI maximum context length errors")]
    public void Detects_LiteLLM_wrapped_OpenAI_maximum_context_length_errors()
    {
        Assert.True(IsOverflow(
            "Error: 503 litellm.ServiceUnavailableError: litellm.MidStreamFallbackError: litellm.APIConnectionError: APIConnectionError: OpenAIException - Requested token count exceeds the model's maximum context length of 131072 tokens."));
    }

    [Fact(DisplayName = "detects OpenAI-compatible parenthesized maximum context length errors")]
    public void Detects_OpenAI_compatible_parenthesized_maximum_context_length_errors()
    {
        Assert.True(IsOverflow("Error: 400 Input length (265330) exceeds model's maximum context length (262144)."));
    }

    [Fact(DisplayName = "detects OpenRouter Poolside maximum allowed input length errors")]
    public void Detects_OpenRouter_Poolside_maximum_allowed_input_length_errors()
    {
        Assert.True(IsOverflow("Provider returned error: Input length 131393 exceeds the maximum allowed input length of 131040 tokens."));
    }

    [Fact(DisplayName = "detects DS4 configured context size errors")]
    public void Detects_DS4_configured_context_size_errors()
    {
        Assert.True(IsOverflow("400 Prompt has 256468 tokens, but the configured context size is 256000 tokens"));
        Assert.True(IsOverflow("Prompt has 5,958,968 tokens, but the configured context size is 256,000 tokens"));
    }

    [Fact(DisplayName = "does not treat generic non-overflow Ollama errors as overflow")]
    public void Does_not_treat_generic_non_overflow_Ollama_errors_as_overflow()
    {
        Assert.False(IsOverflow("500 `model runner crashed unexpectedly`"));
    }

    [Fact(DisplayName = "does not treat Bedrock throttling 'Too many tokens' as overflow")]
    public void Does_not_treat_Bedrock_throttling_Too_many_tokens_as_overflow()
    {
        Assert.False(IsOverflow("Throttling error: Too many tokens, please wait before trying again."));
    }

    [Fact(DisplayName = "does not treat Bedrock service unavailable as overflow")]
    public void Does_not_treat_Bedrock_service_unavailable_as_overflow()
    {
        Assert.False(IsOverflow("Service unavailable: The service is temporarily unavailable."));
    }

    [Fact(DisplayName = "does not treat generic rate limit errors as overflow")]
    public void Does_not_treat_generic_rate_limit_errors_as_overflow()
    {
        Assert.False(IsOverflow("Rate limit exceeded, please retry after 30 seconds."));
    }

    [Fact(DisplayName = "does not treat HTTP 429 style errors as overflow")]
    public void Does_not_treat_HTTP_429_style_errors_as_overflow()
    {
        Assert.False(IsOverflow("Too many requests. Please slow down."));
    }

    [Fact(DisplayName = "detects Xiaomi-style overflow (length stop with zero output and filled context)")]
    public void Detects_Xiaomi_style_overflow_length_stop_with_zero_output_and_filled_context()
    {
        var message = R4TestSupport.LengthMessage(58, 1_048_512, 0, provider: "xiaomi", model: "mimo-v2.5-pro");
        Assert.True(OverflowUtilities.IsContextOverflow(message, 1_048_576));
    }

    [Fact(DisplayName = "treats a length stop below the desired output limit as recoverable")]
    public void Treats_a_length_stop_below_the_desired_output_limit_as_recoverable()
    {
        var message = R4TestSupport.LengthMessage(3, 253_584, 16, 25);
        Assert.True(OverflowUtilities.IsRecoverableLength(message, 128_000));
    }

    [Fact(DisplayName = "does not recover a length stop that reached the desired output limit")]
    public void Does_not_recover_a_length_stop_that_reached_the_desired_output_limit()
    {
        var message = R4TestSupport.LengthMessage(4_062, 0, 1_024);
        Assert.False(OverflowUtilities.IsRecoverableLength(message, 1_024));
    }

    [Fact(DisplayName = "treats zero-output length stops as recoverable without context metadata")]
    public void Treats_zero_output_length_stops_as_recoverable_without_context_metadata()
    {
        var message = R4TestSupport.LengthMessage(100, 0, 0);
        Assert.True(OverflowUtilities.IsRecoverableLength(message, 128_000));
    }

    [Fact(DisplayName = "does not treat normal length stops with output as context overflow")]
    public void Does_not_treat_normal_length_stops_with_output_as_context_overflow()
    {
        var message = R4TestSupport.LengthMessage(1_000, 0, 4_096);
        Assert.False(OverflowUtilities.IsContextOverflow(message, 200_000));
    }

    [Fact(DisplayName = "does not treat zero-output length stops far below context as context overflow")]
    public void Does_not_treat_zero_output_length_stops_far_below_context_as_context_overflow()
    {
        var message = R4TestSupport.LengthMessage(100, 0, 0);
        Assert.False(OverflowUtilities.IsContextOverflow(message, 200_000));
    }

    private static bool IsOverflow(string errorMessage) =>
        OverflowUtilities.IsContextOverflow(
            R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: errorMessage),
            262_144);
}

public sealed class R4ErrorBodyTests
{
    [Fact(DisplayName = "extracts status and body from a Mistral-shaped error")]
    public void Extracts_status_and_body_from_a_Mistral_shaped_error()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "Mistral request failed",
                new ProviderErrorMetadata
                {
                    StatusCode = 403,
                    Body = "{\"error\":\"blocked by gateway WAF\"}",
                }));

        Assert.Equal(403, normalized.Status);
        Assert.Equal("{\"error\":\"blocked by gateway WAF\"}", normalized.Body);
        Assert.False(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "reads the parsed body off an openai APIError when the message is opaque")]
    public void Reads_the_parsed_body_off_an_openai_APIError_when_the_message_is_opaque()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "403 status code (no body)",
                new ProviderErrorMetadata
                {
                    Status = 403,
                    ParsedError = new JsonObject { ["error"] = "blocked by gateway WAF" },
                }));

        Assert.Equal(403, normalized.Status);
        Assert.Equal("{\"error\":\"blocked by gateway WAF\"}", normalized.Body);
        Assert.False(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "preserves the message when @google/genai already folds the body into it")]
    public void Preserves_the_message_when_google_genai_already_folds_the_body_into_it()
    {
        const string message = "{\"error\":{\"code\":403,\"message\":\"Permission denied\"}}";
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                message,
                new ProviderErrorMetadata
                {
                    Status = 403,
                    Body = message,
                }));

        Assert.Equal(403, normalized.Status);
        Assert.Equal(message, normalized.Message);
        Assert.True(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "extracts status and body from a Bedrock-shaped ServiceException")]
    public void Extracts_status_and_body_from_a_Bedrock_shaped_ServiceException()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "UnknownError",
                new ProviderErrorMetadata
                {
                    MetadataHttpStatusCode = 403,
                    ResponseStatusCode = 403,
                    ResponseBody = "{\"message\":\"blocked by gateway WAF\"}",
                }));

        Assert.Equal(403, normalized.Status);
        Assert.Equal("{\"message\":\"blocked by gateway WAF\"}", normalized.Body);
        Assert.False(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "ignores a Bedrock response stream instead of serializing its internals")]
    public void Ignores_a_Bedrock_response_stream_instead_of_serializing_its_internals()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "Invocation of model ID anthropic.claude-opus-5 with on-demand throughput isn't supported.",
                new ProviderErrorMetadata
                {
                    ResponseStatusCode = 400,
                    ResponseBodyIsReadableStream = true,
                }));

        Assert.Equal(400, normalized.Status);
        Assert.Null(normalized.Body);
        Assert.True(normalized.MessageCarriesBody);
        Assert.Contains("on-demand throughput", normalized.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ignores a class-instance response body without a pipe method instead of serializing it")]
    public void Ignores_a_class_instance_response_body_without_a_pipe_method_instead_of_serializing_it()
    {
        // ProviderErrorMetadata is the C# transport-neutral equivalent of the SDK wrapper. A
        // class-instance body is intentionally represented by an absent body, not by a JSON
        // object, because the upstream adapter ignores such instances.
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "Input is too long for requested model.",
                new ProviderErrorMetadata { ResponseStatusCode = 400 }));

        Assert.Equal(400, normalized.Status);
        Assert.Null(normalized.Body);
        Assert.True(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "ignores a class-instance `error` field instead of serializing it")]
    public void Ignores_a_class_instance_error_field_instead_of_serializing_it()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "TLS handshake failed",
                new ProviderErrorMetadata { Status = 502 }));

        Assert.Equal(502, normalized.Status);
        Assert.Null(normalized.Body);
        Assert.Equal("TLS handshake failed", normalized.Message);
        Assert.True(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "still surfaces a plain parsed JSON body object")]
    public void Still_surfaces_a_plain_parsed_JSON_body_object()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "400 status code (no body)",
                new ProviderErrorMetadata
                {
                    Status = 400,
                    ParsedError = new JsonObject
                    {
                        ["message"] = "schema validation failed",
                        ["field"] = "tools[0]",
                    },
                }));

        Assert.Equal(400, normalized.Status);
        Assert.Equal("{\"message\":\"schema validation failed\",\"field\":\"tools[0]\"}", normalized.Body);
        Assert.False(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "JSON-stringifies a non-Error thrown value")]
    public void JSON_stringifies_a_non_Error_thrown_value()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(new Dictionary<string, string> { ["reason"] = "boom" });

        Assert.Null(normalized.Status);
        Assert.Null(normalized.Body);
        Assert.Equal("{\"reason\":\"boom\"}", normalized.Message);
        Assert.False(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "treats an empty parsed body object as no body")]
    public void Treats_an_empty_parsed_body_object_as_no_body()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "403 status code (no body)",
                new ProviderErrorMetadata { Status = 403, ParsedError = new JsonObject() }));

        Assert.Equal(403, normalized.Status);
        Assert.Null(normalized.Body);
        Assert.True(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "truncates the body at the cap")]
    public void Truncates_the_body_at_the_cap()
    {
        var body = new string('x', ErrorBodyUtilities.MaxProviderErrorBodyChars + 50);
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException("failed", new ProviderErrorMetadata { StatusCode = 500, Body = body }));

        Assert.Contains("... [truncated 50 chars]", normalized.Body, StringComparison.Ordinal);
        Assert.True(normalized.Body!.Length < body.Length);
    }

    [Fact(DisplayName = "sets messageCarriesBody when the message already contains the extracted body")]
    public void Sets_messageCarriesBody_when_the_message_already_contains_the_extracted_body()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "500: upstream exploded",
                new ProviderErrorMetadata { StatusCode = 500, Body = "upstream exploded" }));

        Assert.Equal(500, normalized.Status);
        Assert.Equal("upstream exploded", normalized.Body);
        Assert.True(normalized.MessageCarriesBody);
    }

    [Fact(DisplayName = "surfaces status and body without a prefix")]
    public void Surfaces_status_and_body_without_a_prefix()
    {
        var normalized = new NormalizedProviderError
        {
            Status = 403,
            Body = "{\"error\":\"blocked by gateway WAF\"}",
            Message = "403 status code (no body)",
            MessageCarriesBody = false,
        };

        var formatted = ErrorBodyUtilities.FormatProviderError(normalized);
        Assert.Equal("403: {\"error\":\"blocked by gateway WAF\"}", formatted);
        Assert.DoesNotContain("no body", formatted, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "applies a provider prefix with status and body")]
    public void Applies_a_provider_prefix_with_status_and_body()
    {
        var normalized = new NormalizedProviderError
        {
            Status = 403,
            Body = "{\"error\":\"blocked by gateway WAF\"}",
            Message = "403 status code (no body)",
            MessageCarriesBody = false,
        };

        Assert.Equal(
            "OpenAI API error (403): {\"error\":\"blocked by gateway WAF\"}",
            ErrorBodyUtilities.FormatProviderError(normalized, "OpenAI API error"));
    }

    [Fact(DisplayName = "preserves the message (with prefix + status) when it already carries the body")]
    public void Preserves_the_message_with_prefix_and_status_when_it_already_carries_the_body()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(
            new ProviderErrorMetadataException(
                "403 status code (no body) {\"error\":{\"message\":\"Permission denied\"}}",
                new ProviderErrorMetadata
                {
                    Status = 403,
                    Body = "{\"error\":{\"message\":\"Permission denied\"}}",
                }));

        Assert.Equal(
            "OpenAI API error (403): 403 status code (no body) {\"error\":{\"message\":\"Permission denied\"}}",
            ErrorBodyUtilities.FormatProviderError(normalized, "OpenAI API error"));
    }

    [Fact(DisplayName = "returns the bare message for a non-Error value")]
    public void Returns_the_bare_message_for_a_non_Error_value()
    {
        var normalized = ErrorBodyUtilities.NormalizeProviderError(new Dictionary<string, string> { ["reason"] = "boom" });
        Assert.Equal("{\"reason\":\"boom\"}", ErrorBodyUtilities.FormatProviderError(normalized, "Ignored prefix"));
    }
}
