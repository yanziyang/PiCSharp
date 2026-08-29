using Pi.Protocol;

namespace Pi.Server;

internal sealed class ServerSnapshotPublisher
{
    private readonly string _serverId;
    private readonly IPiServerService _service;
    private readonly ISet<ConnectionState> _connections;
    private readonly Func<bool> _isClosing;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionMetadata>>> _listSessions;
    private readonly Func<ConnectionState, EventEnvelope, Task<bool>> _sendMessage;
    private readonly Action<Exception> _reportError;
    private readonly object _queueLock = new();
    private Task _broadcastQueue = Task.CompletedTask;
    private long _revision;

    internal ServerSnapshotPublisher(
        string serverId,
        IPiServerService service,
        ISet<ConnectionState> connections,
        Func<bool> isClosing,
        Func<CancellationToken, Task<IReadOnlyList<SessionMetadata>>> listSessions,
        Func<ConnectionState, EventEnvelope, Task<bool>> sendMessage,
        Action<Exception> reportError)
    {
        _serverId = serverId;
        _service = service;
        _connections = connections;
        _isClosing = isClosing;
        _listSessions = listSessions;
        _sendMessage = sendMessage;
        _reportError = reportError;
    }

    internal long CurrentRevision => Interlocked.Read(ref _revision);

    internal async Task<ServerSnapshot> GetAsync(
        IReadOnlyList<ModelMetadata>? models = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _listSessions(cancellationToken).ConfigureAwait(false);
        models ??= await _service.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return new ServerSnapshot(
            _serverId,
            ProtocolConstants.ProtocolVersion,
            CurrentRevision,
            sessions,
            models);
    }

    internal Task BroadcastAsync(CancellationToken cancellationToken = default)
    {
        lock (_queueLock)
        {
            var broadcast = _broadcastQueue.ContinueWith(
                _ => PerformBroadcastAsync(cancellationToken),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
            _broadcastQueue = broadcast.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted && completed.Exception is not null)
                    {
                        _reportError(completed.Exception.GetBaseException());
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return broadcast;
        }
    }

    private async Task PerformBroadcastAsync(CancellationToken cancellationToken)
    {
        var readyConnections = _connections
            .Where(static connection =>
                connection.Stage == ConnectionStage.Ready && !connection.Disconnected)
            .ToArray();
        if (readyConnections.Length == 0 || _isClosing())
        {
            return;
        }

        var revision = Interlocked.Increment(ref _revision);
        var models = await _service.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetAsync(models, cancellationToken).ConfigureAwait(false);
        var snapshot = current with { Revision = revision };
        var envelope = new EventEnvelope(new ServerSnapshotEvent(snapshot));
        foreach (var connection in readyConnections)
        {
            await _sendMessage(connection, envelope).ConfigureAwait(false);
        }
    }
}
