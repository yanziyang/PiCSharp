using System.Diagnostics.CodeAnalysis;

using Pi.Protocol;

namespace Pi.Client;

/// <summary>Raised when the server returns a protocol-level operation error.</summary>
[SuppressMessage("Design", "CA1710", Justification = "PiServerError is the public error name used by the upstream package.")]
public sealed class PiServerError : Exception
{
    /// <summary>Initializes an error from a protocol error payload.</summary>
    public PiServerError(ProtocolError error)
        : base(error?.Message ?? throw new ArgumentNullException(nameof(error)))
    {
        Code = error.Code;
        Details = error.Details;
    }

    /// <summary>Protocol error code returned by the server.</summary>
    public ProtocolErrorCode Code { get; }

    /// <summary>Optional server-provided structured details.</summary>
    public JsonValue? Details { get; }
}

/// <summary>Raised when a client operation requires an active connection.</summary>
[SuppressMessage("Design", "CA1710", Justification = "PiDisconnectedError is the public error name used by the upstream package.")]
public sealed class PiDisconnectedError : Exception
{
    /// <summary>Initializes a disconnection error.</summary>
    public PiDisconnectedError(string message = "Pi client is disconnected") : base(message) { }
}

/// <summary>Raised when an operation is attempted after client disposal.</summary>
[SuppressMessage("Design", "CA1710", Justification = "PiClientDisposedError is the public error name used by the upstream package.")]
public sealed class PiClientDisposedError : Exception
{
    /// <summary>Initializes a disposed-client error.</summary>
    public PiClientDisposedError() : base("Pi client is disposed") { }
}

/// <summary>Raised when a session lease violates shared/exclusive ownership rules.</summary>
[SuppressMessage("Design", "CA1710", Justification = "PiSessionOwnershipError is the public error name used by the upstream package.")]
public sealed class PiSessionOwnershipError : Exception
{
    /// <summary>Initializes an ownership error.</summary>
    public PiSessionOwnershipError(string sessionId, string message) : base(message)
    {
        SessionId = sessionId;
    }

    /// <summary>Session whose lease could not be acquired.</summary>
    public string SessionId { get; }
}

/// <summary>Raised when a session handle is no longer attached.</summary>
[SuppressMessage("Design", "CA1710", Justification = "PiSessionDetachedError is the public error name used by the upstream package.")]
public sealed class PiSessionDetachedError : Exception
{
    /// <summary>Initializes a detached-session error.</summary>
    public PiSessionDetachedError(string sessionId) : base($"Session {sessionId} is not attached")
    {
        SessionId = sessionId;
    }

    /// <summary>Session represented by the detached handle.</summary>
    public string SessionId { get; }
}

internal static class ClientErrorUtilities
{
    public static Exception ToException(Exception error) => error;

    public static PiDisconnectedError ToDisconnectedError(Exception error) =>
        error as PiDisconnectedError ?? new PiDisconnectedError(error.Message);
}
