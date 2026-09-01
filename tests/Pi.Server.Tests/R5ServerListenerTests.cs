using Pi.Protocol;
using Pi.Server;

using Xunit;

namespace Pi.Server.Tests;

public sealed class R5ServerListenerTests
{
    [Fact(DisplayName = "closes previously started listeners when startup fails")]
    public async Task Closes_previously_started_listeners_when_startup_fails()
    {
        var first = new R5Listener("first");
        var failure = new InvalidOperationException("listener failed");
        var second = new R5Listener("second", failure);
        var server = new PiServer(
            new R5ServerService(),
            new PiServerOptions { Listeners = [first, second] });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(0, second.CloseCount);
    }

    private sealed class R5Listener(string address, Exception? startError = null) : IPiServerListener
    {
        public string? Address { get; private set; } = address;

        public ByteConnectionAcceptor? Accept { get; private set; }

        public int StartCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task StartAsync(ByteConnectionAcceptor accept, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accept);
            StartCount++;
            Accept = accept;
            if (startError is not null)
            {
                return Task.FromException(startError);
            }

            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCount++;
            Address = null;
            return Task.CompletedTask;
        }
    }

    private sealed class R5ServerService : IPiServerService
    {
        public Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionMetadata>>([]);

        public Task<IReadOnlyList<ModelMetadata>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelMetadata>>([]);

        public Task<IPiSessionRuntime> CreateSessionAsync(
            CreateSessionOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IPiSessionRuntime>(new NotSupportedException());

        public Task<IPiSessionRuntime> OpenSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IPiSessionRuntime>(new NotSupportedException());
    }
}
