using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4RetryClassificationTests
{
    private const string _openAiExplicitRetryMessage =
        "An error occurred while processing your request. You can retry your request, or contact us through our help center at help.openai.com if the error persists. Please include the request ID req_******** in your message.";
    private const string _bedrockExplicitRetryMessage =
        "{\"message\":\"The system encountered an unexpected error during processing. Try your request again.\"}";
    private const string _nvidiaNimResourceExhaustedMessage =
        "ResourceExhausted: Worker local total request limit reached (288/48)";
    private const string _bunFetchSocketClosedMessage =
        "The socket connection was closed unexpectedly. For more information, pass `verbose: true` in the second argument to fetch()";
    private const string _openAiResponsesEarlyEofMessage =
        "OpenAI Responses stream ended before a terminal response event";

    [Fact(DisplayName = "matches explicit provider retry guidance")]
    public void Matches_explicit_provider_retry_guidance()
    {
        Assert.True(IsRetryable(_openAiExplicitRetryMessage));
        Assert.True(IsRetryable(_bedrockExplicitRetryMessage));
        Assert.True(IsRetryable(_nvidiaNimResourceExhaustedMessage));
    }

    [Fact(DisplayName = "matches Bun fetch socket drop wording")]
    public void Matches_Bun_fetch_socket_drop_wording()
    {
        Assert.True(IsRetryable(_bunFetchSocketClosedMessage));
    }

    [Fact(DisplayName = "matches upstream request buffer exhaustion wording")]
    public void Matches_upstream_request_buffer_exhaustion_wording()
    {
        Assert.True(IsRetryable("Error: exceeded request buffer limit while retrying upstream"));
    }

    [Fact(DisplayName = "matches OpenAI Responses streams that end before terminal events")]
    public void Matches_OpenAI_Responses_streams_that_end_before_terminal_events()
    {
        Assert.True(IsRetryable(_openAiResponsesEarlyEofMessage));
    }

    [Fact(DisplayName = "keeps provider limit errors non-retryable")]
    public void Keeps_provider_limit_errors_non_retryable()
    {
        Assert.False(IsRetryable("429 quota exceeded"));
    }

    [Fact(DisplayName = "classifies assistant error messages")]
    public void Classifies_assistant_error_messages()
    {
        Assert.True(IsRetryable("overloaded_error"));
        Assert.True(IsRetryable("524 status code (no body)"));
        Assert.False(RetryUtilities.IsRetryableAssistantError(R4TestSupport.Assistant("not an error")));
    }

    private static bool IsRetryable(string message) =>
        RetryUtilities.IsRetryableAssistantError(
            R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: message));
}

public sealed class R4AssistantRetryTests
{
    private static readonly RetryPolicy _enabled = new() { Enabled = true, MaxRetries = 3, BaseDelayMs = 0 };
    private static readonly RetryPolicy _disabled = new() { Enabled = false, MaxRetries = 3, BaseDelayMs = 0 };

    [Fact(DisplayName = "returns a successful response immediately without retrying")]
    public async Task Returns_a_successful_response_immediately_without_retrying()
    {
        var calls = 0;
        var result = await RetryUtilities.RetryAssistantCall(
            () =>
            {
                calls++;
                return Task.FromResult(R4TestSupport.Assistant("ok"));
            },
            _enabled,
            signal: TestContext.Current.CancellationToken);

        Assert.Equal([new TextContent("ok")], result.Content);
        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "does not retry an aborted message")]
    public async Task Does_not_retry_an_aborted_message()
    {
        var calls = 0;
        var scheduled = 0;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(R4TestSupport.Assistant(stopReason: StopReasons.Aborted));
            },
            _enabled,
            new RetryCallbacks
            {
                OnRetryScheduled = (_, _, _, _) =>
                {
                    scheduled++;
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StopReasons.Aborted, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Equal(0, scheduled);
    }

    [Fact(DisplayName = "does not retry a non-retryable error (quota/billing)")]
    public async Task Does_not_retry_a_non_retryable_error_quota_billing()
    {
        var calls = 0;
        var scheduled = 0;
        var finished = 0;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(
                    R4TestSupport.Assistant(
                        stopReason: StopReasons.Error,
                        errorMessage: "insufficient_quota"));
            },
            _enabled,
            new RetryCallbacks
            {
                OnRetryScheduled = (_, _, _, _) =>
                {
                    scheduled++;
                    return Task.CompletedTask;
                },
                OnRetryFinished = (_, _, _) =>
                {
                    finished++;
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Equal(0, scheduled);
        Assert.Equal(0, finished);
    }

    [Fact(DisplayName = "retries a transient error up to maxRetries then returns the final error")]
    public async Task Retries_a_transient_error_up_to_maxRetries_then_returns_the_final_error()
    {
        var calls = 0;
        var scheduled = 0;
        (bool Success, int Attempt, string? Error)? finished = null;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(
                    R4TestSupport.Assistant(
                        stopReason: StopReasons.Error,
                        errorMessage: "terminated"));
            },
            _enabled,
            new RetryCallbacks
            {
                OnRetryScheduled = (_, _, _, _) =>
                {
                    scheduled++;
                    return Task.CompletedTask;
                },
                OnRetryFinished = (success, attempt, error) =>
                {
                    finished = (success, attempt, error);
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Equal(4, calls);
        Assert.Equal(3, scheduled);
        Assert.Equal((false, 3, "terminated"), finished);
    }

    [Fact(DisplayName = "stops retrying once a call succeeds")]
    public async Task Stops_retrying_once_a_call_succeeds()
    {
        var calls = 0;
        (bool Success, int Attempt, string? Error)? finished = null;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(
                    calls < 3
                        ? R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: "terminated")
                        : R4TestSupport.Assistant("recovered"));
            },
            _enabled,
            new RetryCallbacks
            {
                OnRetryFinished = (success, attempt, error) =>
                {
                    finished = (success, attempt, error);
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([new TextContent("recovered")], result.Content);
        Assert.Equal(3, calls);
        Assert.Equal((true, 2, (string?)null), finished);
    }

    [Fact(DisplayName = "reports an aborted retried call as unsuccessful")]
    public async Task Reports_an_aborted_retried_call_as_unsuccessful()
    {
        var calls = 0;
        (bool Success, int Attempt, string? Error)? finished = null;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(
                    calls == 1
                        ? R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: "terminated")
                        : R4TestSupport.Assistant(stopReason: StopReasons.Aborted));
            },
            _enabled,
            new RetryCallbacks
            {
                OnRetryFinished = (success, attempt, error) =>
                {
                    finished = (success, attempt, error);
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StopReasons.Aborted, result.StopReason);
        Assert.Equal(2, calls);
        Assert.Equal((false, 1, (string?)null), finished);
    }

    [Fact(DisplayName = "does not retry when policy is disabled")]
    public async Task Does_not_retry_when_policy_is_disabled()
    {
        var calls = 0;
        var scheduled = 0;
        var finished = 0;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: "terminated"));
            },
            _disabled,
            new RetryCallbacks
            {
                OnRetryScheduled = (_, _, _, _) =>
                {
                    scheduled++;
                    return Task.CompletedTask;
                },
                OnRetryFinished = (_, _, _) =>
                {
                    finished++;
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Equal(0, scheduled);
        Assert.Equal(0, finished);
    }

    [Fact(DisplayName = "emits onRetryAttemptStart after backoff before each retried call")]
    public async Task Emits_onRetryAttemptStart_after_backoff_before_each_retried_call()
    {
        var events = new List<string>();
        var calls = 0;
        var scheduled = 0;
        var attemptStarts = 0;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                events.Add($"produce:{calls}");
                calls++;
                return Task.FromResult(
                    calls < 3
                        ? R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: "terminated")
                        : R4TestSupport.Assistant("recovered"));
            },
            _enabled,
            new RetryCallbacks
            {
                OnRetryScheduled = (attempt, _, _, _) =>
                {
                    scheduled++;
                    events.Add($"retry:{attempt}");
                    return Task.CompletedTask;
                },
                OnRetryAttemptStart = () =>
                {
                    attemptStarts++;
                    events.Add("attempt-start");
                    return Task.CompletedTask;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([new TextContent("recovered")], result.Content);
        Assert.Equal(2, scheduled);
        Assert.Equal(2, attemptStarts);
        Assert.Equal(
            ["produce:0", "retry:1", "attempt-start", "produce:1", "retry:2", "attempt-start", "produce:2"],
            events);
    }

    [Fact(DisplayName = "aborts backoff sleep via signal, returns an aborted message, and emits onRetryFinished(false)")]
    public async Task Aborts_backoff_sleep_via_signal_returns_an_aborted_message_and_emits_onRetryFinished_false()
    {
        using var controller = new CancellationTokenSource();
        var calls = 0;
        (bool Success, int Attempt, string? Error)? finished = null;
        var result = await RetryUtilities.RetryAssistantCall(
            ()
            =>
            {
                calls++;
                return Task.FromResult(R4TestSupport.Assistant(stopReason: StopReasons.Error, errorMessage: "terminated"));
            },
            new RetryPolicy { Enabled = true, MaxRetries = 5, BaseDelayMs = 10_000 },
            new RetryCallbacks
            {
                OnRetryScheduled = (_, _, _, _) =>
                {
                    controller.Cancel();
                    return Task.CompletedTask;
                },
                OnRetryFinished = (success, attempt, error) =>
                {
                    finished = (success, attempt, error);
                    return Task.CompletedTask;
                },
            },
            controller.Token);

        Assert.Equal(StopReasons.Aborted, result.StopReason);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(1, calls);
        Assert.Equal((false, 1, "terminated"), finished);
    }
}
