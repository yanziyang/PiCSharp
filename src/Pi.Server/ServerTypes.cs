using Pi.Protocol;

namespace Pi.Server;

/// <summary>Transport-agnostic byte connection accepted by a Pi server.</summary>
public interface IByteConnection
{
    /// <summary>Whether the underlying transport has reached its terminal state.</summary>
    bool Closed { get; }

    /// <summary>Sends one complete framed byte chunk in invocation order.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken = default);

    /// <summary>Closes the connection, optionally sending one final frame first.</summary>
    ValueTask CloseAsync(ReadOnlyMemory<byte>? finalChunk = null, CancellationToken cancellationToken = default);
}

/// <summary>Callbacks a byte transport invokes for an accepted connection.</summary>
public sealed class ByteConnectionHandler
{
    /// <summary>Delivers an arbitrary inbound byte chunk.</summary>
    public required Action<ReadOnlyMemory<byte>> OnData { get; init; }

    /// <summary>Reports an orderly terminal close.</summary>
    public required Action OnClose { get; init; }

    /// <summary>Reports a terminal transport error.</summary>
    public required Action<Exception> OnError { get; init; }
}

/// <summary>Accepts already-authorized byte connections.</summary>
public delegate ByteConnectionHandler ByteConnectionAcceptor(IByteConnection connection);

/// <summary>Lifecycle stage for one server connection.</summary>
public enum ConnectionStage
{
    /// <summary>The client hello has not arrived.</summary>
    AwaitingHello,

    /// <summary>The client hello is being validated and the server snapshot is loading.</summary>
    Handshaking,

    /// <summary>The connection is ready for requests.</summary>
    Ready,

    /// <summary>The connection is closing after a protocol or transport failure.</summary>
    Closing,

    /// <summary>The connection is terminal.</summary>
    Closed,
}

/// <summary>Mutable connection state exposed to the session service boundary.</summary>
public sealed class ConnectionState
{
    internal ConnectionState(string id, IByteConnection connection, ClientMessageDecoder decoder, CancellationTokenSource handshakeTimeout)
    {
        Id = id;
        Connection = connection;
        Decoder = decoder;
        HandshakeTimeout = handshakeTimeout;
    }

    /// <summary>Server-assigned connection identifier.</summary>
    public string Id { get; }

    /// <summary>Accepted byte connection.</summary>
    public IByteConnection Connection { get; }

    /// <summary>Session identifiers attached through this connection.</summary>
    public IReadOnlySet<string> SessionIds => SessionIdsInternal;

    /// <summary>Current handshake/transport stage.</summary>
    public ConnectionStage Stage { get; internal set; } = ConnectionStage.AwaitingHello;

    /// <summary>Whether the transport has disconnected.</summary>
    public bool Disconnected { get; internal set; }

    /// <summary>Whether the server hello was sent successfully.</summary>
    public bool HandshakeComplete { get; internal set; }

    internal ClientMessageDecoder Decoder { get; }
    internal HashSet<string> SessionIdsInternal { get; } = new(StringComparer.Ordinal);
    internal CancellationTokenSource HandshakeTimeout { get; }
    internal Task? HandshakeTask { get; set; }

    /// <summary>Returns whether the connection can no longer accept work.</summary>
    public static bool IsTerminal(ConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Disconnected || state.Stage is ConnectionStage.Closing or ConnectionStage.Closed;
    }
}

/// <summary>Supplies an established listener to a <see cref="PiServer"/>.</summary>
public interface IPiServerListener
{
    /// <summary>Human-readable bound address, when the transport has one.</summary>
    string? Address { get; }

    /// <summary>Starts listening and sends authorized connections to <paramref name="accept"/>.</summary>
    Task StartAsync(ByteConnectionAcceptor accept, CancellationToken cancellationToken = default);

    /// <summary>Stops listening.</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Options controlling server listeners, framing, and handshake behavior.</summary>
public sealed class PiServerOptions
{
    /// <summary>Listeners that provide already-authorized byte connections.</summary>
    public required IReadOnlyList<IPiServerListener> Listeners { get; init; }

    /// <summary>Maximum CBOR payload length accepted and emitted.</summary>
    public uint? MaxFrameLength { get; init; }

    /// <summary>Maximum time allowed for the initial client hello.</summary>
    public TimeSpan? HandshakeTimeout { get; init; }

    /// <summary>Optional stable server identifier.</summary>
    public string? ServerId { get; init; }

    /// <summary>Receives errors that cannot cross the protocol boundary.</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>Options passed to a service when creating a durable session.</summary>
public sealed record CreateSessionOptions(
    string Id,
    string? Cwd = null,
    string? Name = null,
    ModelRef? Model = null,
    ThinkingLevel? ThinkingLevel = null);

/// <summary>Text prompt supplied to a live session runtime.</summary>
public sealed record PromptInput(string Text);

/// <summary>Text steering input supplied to a live session runtime.</summary>
public sealed record SteerInput(string Text);

/// <summary>Base event emitted by a live session runtime.</summary>
public abstract record PiSessionRuntimeEvent
{
    /// <summary>Snapshot changed.</summary>
    public sealed record SnapshotChanged : PiSessionRuntimeEvent;

    /// <summary>Incremental transcript progress is available.</summary>
    public sealed record Progress(TranscriptProgress Value) : PiSessionRuntimeEvent;

    /// <summary>The runtime failed with a safe server error.</summary>
    public sealed record Error(PiServerError Value) : PiSessionRuntimeEvent;
}

/// <summary>Runtime for one exclusively acquired durable session.</summary>
public interface IPiSessionRuntime
{
    /// <summary>Returns the current session snapshot.</summary>
    Task<SessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current runtime phase.</summary>
    SessionPhase GetPhase();

    /// <summary>Runs a user prompt.</summary>
    Task PromptAsync(PromptInput input, CancellationToken cancellationToken = default);

    /// <summary>Queues or applies steering input.</summary>
    Task SteerAsync(SteerInput input, CancellationToken cancellationToken = default);

    /// <summary>Aborts active runtime work.</summary>
    Task AbortAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the selected model.</summary>
    Task SetModelAsync(ModelRef model, CancellationToken cancellationToken = default);

    /// <summary>Changes the selected thinking level.</summary>
    Task SetThinkingAsync(ThinkingLevel thinkingLevel, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to runtime events.</summary>
    Unsubscribe Subscribe(Action<PiSessionRuntimeEvent> listener);

    /// <summary>Disposes runtime resources.</summary>
    Task DisposeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Durable-session service boundary used by <see cref="PiServer"/>.</summary>
public interface IPiServerService
{
    /// <summary>Lists durable session metadata.</summary>
    Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists models advertised by the service.</summary>
    Task<IReadOnlyList<ModelMetadata>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates and opens a runtime using the exact supplied session id.</summary>
    Task<IPiSessionRuntime> CreateSessionAsync(CreateSessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Opens an existing session runtime.</summary>
    Task<IPiSessionRuntime> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Supplies a listener removal operation.</summary>
public delegate void Unsubscribe();
