using Pi.Protocol;

namespace Pi.Client;

internal sealed class SessionHandleCallbacks
{
    public required Func<bool> IsAttached { get; init; }
    public required Func<SessionSnapshot?> GetSnapshot { get; init; }
    public required Func<Action<SessionSnapshot>, Unsubscribe> Subscribe { get; init; }
    public required Func<Action<ServerEvent>, Unsubscribe> OnEvent { get; init; }
    public required Func<CancellationToken, Task> DetachAsync { get; init; }
    public required Func<CancellationToken, Task> DisposeAsync { get; init; }
    public required Func<Command, CancellationToken, Task<CommandResult>> RequestAsync { get; init; }
}

/// <summary>Lease-backed handle for commands and subscriptions on one attached session.</summary>
public sealed class SessionHandle : IAsyncDisposable
{
    private readonly SessionHandleCallbacks _callbacks;

    internal SessionHandle(string id, SessionHandleCallbacks callbacks)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    /// <summary>Stable server-assigned session identifier.</summary>
    public string Id { get; }

    /// <summary>Whether the handle still owns an attached session lease.</summary>
    public bool Attached => _callbacks.IsAttached();

    /// <summary>Alias for <see cref="Attached"/> matching the TypeScript lease contract.</summary>
    public bool Active => Attached;

    /// <summary>Latest authoritative snapshot visible through this handle.</summary>
    public SessionSnapshot? Snapshot => _callbacks.GetSnapshot();

    /// <summary>Subscribes to snapshots for this session.</summary>
    public Unsubscribe Subscribe(Action<SessionSnapshot> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _callbacks.Subscribe(listener);
    }

    /// <summary>Subscribes to events for this session.</summary>
    public Unsubscribe OnEvent(Action<ServerEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _callbacks.OnEvent(listener);
    }

    /// <summary>Releases this lease and detaches when it is the final lease.</summary>
    public Task DetachAsync(CancellationToken cancellationToken = default) =>
        _callbacks.DetachAsync(cancellationToken);

    /// <summary>Disposes this lease, reconciling a failed detach when necessary.</summary>
    public Task DisposeAsync(CancellationToken cancellationToken = default) =>
        _callbacks.DisposeAsync(cancellationToken);

    /// <summary>Sends a prompt to this session.</summary>
    public async Task<SessionSnapshot> PromptAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new PromptCommand(Id, text), cancellationToken).ConfigureAwait(false);
        return GetSession(result);
    }

    /// <summary>Sends a steering message to this session.</summary>
    public async Task<SessionSnapshot> SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new SteerCommand(Id, text), cancellationToken).ConfigureAwait(false);
        return GetSession(result);
    }

    /// <summary>Aborts the active operation in this session.</summary>
    public async Task<SessionSnapshot> AbortAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new AbortCommand(Id), cancellationToken).ConfigureAwait(false);
        return GetSession(result);
    }

    /// <summary>Changes the model for this session.</summary>
    public async Task<SessionSnapshot> SetModelAsync(ModelRef model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = await RequestAsync(new SetModelCommand(Id, model), cancellationToken).ConfigureAwait(false);
        return GetSession(result);
    }

    /// <summary>Changes the thinking level for this session.</summary>
    public async Task<SessionSnapshot> SetThinkingAsync(
        ThinkingLevel thinkingLevel,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new SetThinkingCommand(Id, thinkingLevel), cancellationToken).ConfigureAwait(false);
        return GetSession(result);
    }

    /// <summary>Releases the lease using the asynchronous-disposal contract.</summary>
    public ValueTask DisposeAsync() => new(DisposeAsync(CancellationToken.None));

    private Task<CommandResult> RequestAsync(Command command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _callbacks.RequestAsync(command, cancellationToken);
    }

    private static SessionSnapshot GetSession(CommandResult result) => result switch
    {
        PromptResult prompt => prompt.Session,
        SteerResult steer => steer.Session,
        AbortResult abort => abort.Session,
        SetModelResult model => model.Session,
        SetThinkingResult thinking => thinking.Session,
        _ => throw new InvalidOperationException("Session command returned an unexpected result"),
    };
}
