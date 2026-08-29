using Pi.Protocol;

namespace Pi.Client;

/// <summary>Current lifecycle state of a Pi client connection.</summary>
public enum ConnectionState
{
    /// <summary>No transport is active.</summary>
    Disconnected,

    /// <summary>A transport is opening and the protocol handshake is pending.</summary>
    Connecting,

    /// <summary>The protocol handshake completed successfully.</summary>
    Connected,
}

/// <summary>Describes a client connection state transition.</summary>
public sealed record ConnectionStateChange(ConnectionState State, Exception? Error = null);

/// <summary>Removes a registered client listener.</summary>
public delegate void Unsubscribe();

/// <summary>Receives arbitrary inbound byte chunks from a transport.</summary>
public delegate void TransportDataHandler(ReadOnlyMemory<byte> Chunk);

/// <summary>Receives an orderly transport close notification.</summary>
public delegate void TransportCloseHandler();

/// <summary>Receives a terminal transport error notification.</summary>
public delegate void TransportErrorHandler(Exception Error);

/// <summary>Callbacks supplied to a byte transport when it is created.</summary>
public sealed class ByteTransportHandlers
{
    /// <summary>Delivers an arbitrary inbound byte chunk.</summary>
    public required TransportDataHandler OnData { get; init; }

    /// <summary>Reports an orderly terminal close.</summary>
    public required TransportCloseHandler OnClose { get; init; }

    /// <summary>Reports a terminal transport failure.</summary>
    public required TransportErrorHandler OnError { get; init; }
}

/// <summary>Transport used by <see cref="PiClient"/> to send framed protocol bytes.</summary>
public interface IByteTransport
{
    /// <summary>Sends one byte chunk and preserves invocation order.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> Chunk, CancellationToken CancellationToken = default);

    /// <summary>Closes the transport; repeated calls must be harmless.</summary>
    void Close();
}

/// <summary>Creates a fresh transport and binds its inbound callbacks.</summary>
public delegate ValueTask<IByteTransport> ByteTransportFactory(ByteTransportHandlers Handlers);

/// <summary>Options for constructing a protocol client.</summary>
public sealed class PiClientOptions
{
    /// <summary>Creates a fresh connected transport for each connection attempt.</summary>
    public required ByteTransportFactory TransportFactory { get; init; }

    /// <summary>Maximum CBOR payload length accepted in either direction.</summary>
    public uint? MaxFrameLength { get; init; }

    /// <summary>Receives subscriber failures without corrupting client state.</summary>
    public Action<Exception>? OnListenerError { get; init; }
}

/// <summary>Options accepted when creating a session.</summary>
public sealed record CreateSessionOptions(
    string? Cwd = null,
    string? Name = null,
    ModelRef? Model = null,
    ThinkingLevel? ThinkingLevel = null);

/// <summary>Controls how a session lease participates in ownership checks.</summary>
public enum SessionLeaseMode
{
    /// <summary>Multiple shared leases may coexist.</summary>
    Shared,

    /// <summary>The lease excludes all other leases.</summary>
    Exclusive,
}

/// <summary>Options for acquiring an existing session.</summary>
public sealed record AcquireSessionOptions(SessionLeaseMode Mode);
