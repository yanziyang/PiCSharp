using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4ProviderRetryTests
{
    [Fact(DisplayName = "retries retryable provider errors")]
    public async Task Retries_retryable_provider_errors()
    {
        var calls = 0;
        var result = await ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<string>(
                        new ProviderRetryException(
                            "provider rate limited",
                            429,
                            new Dictionary<string, string> { ["retry-after-ms"] = "1000" }))
                    : Task.FromResult("ok");
            },
            maxRetries: 1,
            signal: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact(DisplayName = "does not retry errors the provider marks as non-retryable")]
    public async Task Does_not_retry_errors_the_provider_marks_as_non_retryable()
    {
        var error = new ProviderRetryException(
            "provider rate limited",
            429,
            new Dictionary<string, string> { ["x-should-retry"] = "false" });
        var calls = 0;

        var actual = await Assert.ThrowsAsync<ProviderRetryException>(async () =>
            await ProviderRetryUtilities.RetryProviderRequest(
                () =>
                {
                    calls++;
                    return Task.FromException<string>(error);
                },
                maxRetries: 2,
                signal: TestContext.Current.CancellationToken));

        Assert.Same(error, actual);
        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "rejects a provider-requested retry delay above the limit")]
    public async Task Rejects_a_provider_requested_retry_delay_above_the_limit()
    {
        var calls = 0;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ProviderRetryUtilities.RetryProviderRequest(
                () =>
                {
                    calls++;
                    return Task.FromException<string>(
                        new ProviderRetryException(
                            "Provider error: 429",
                            429,
                            new Dictionary<string, string> { ["retry-after"] = "277403" }));
                },
                maxRetries: 1,
                maxRetryDelayMs: 1_000,
                signal: TestContext.Current.CancellationToken));

        Assert.Contains("Server requested 277403s retry delay (max: 1s)", actual.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "allows disabling the provider-requested retry delay cap")]
    public async Task Allows_disabling_the_provider_requested_retry_delay_cap()
    {
        var calls = 0;
        var result = await ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<string>(
                        new ProviderRetryException(
                            "provider rate limited",
                            429,
                            new Dictionary<string, string> { ["retry-after"] = "2" }))
                    : Task.FromResult("ok");
            },
            maxRetries: 1,
            maxRetryDelayMs: 0,
            signal: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact(DisplayName = "aborts a provider-requested retry delay")]
    public async Task Aborts_a_provider_requested_retry_delay()
    {
        using var controller = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var retry = ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                calls++;
                requestStarted.TrySetResult();
                return Task.FromException<string>(
                    new ProviderRetryException(
                        "provider rate limited",
                        429,
                        new Dictionary<string, string> { ["retry-after"] = "277403" }));
            },
            maxRetries: 2,
            maxRetryDelayMs: 0,
            signal: controller.Token);

        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        controller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await retry);
        Assert.Equal(1, calls);
    }
}
