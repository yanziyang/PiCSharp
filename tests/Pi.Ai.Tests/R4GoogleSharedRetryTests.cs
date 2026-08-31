using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class R4GoogleSharedRetryTests
{
    [Fact(DisplayName = "retries a headers-less SDK error with a retryable status")]
    public async Task Retries_a_headers_less_SDK_error_with_a_retryable_status()
    {
        var calls = 0;
        var result = await ProviderRetryUtilities.RetryProviderRequest(
            () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<string>(new ProviderRetryException("got status: 429", 429))
                    : Task.FromResult("ok");
            },
            maxRetries: 1,
            signal: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact(DisplayName = "does not retry when maxRetries is unset")]
    public async Task Does_not_retry_when_maxRetries_is_unset()
    {
        var error = new ProviderRetryException("got status: 429", 429);
        var calls = 0;

        var actual = await Assert.ThrowsAsync<ProviderRetryException>(async () =>
            await ProviderRetryUtilities.RetryProviderRequest(
                () =>
                {
                    calls++;
                    return Task.FromException<string>(error);
                },
                signal: TestContext.Current.CancellationToken));

        Assert.Same(error, actual);
        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "does not retry a non-retryable status")]
    public async Task Does_not_retry_a_non_retryable_status()
    {
        var error = new ProviderRetryException("got status: 400", 400);
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
}
