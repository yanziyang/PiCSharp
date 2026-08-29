using System.Diagnostics.CodeAnalysis;

using Pi.Protocol;

namespace Pi.Server;

/// <summary>Message used for failures that must not expose implementation details.</summary>
public static class ServerErrorMessages
{
    /// <summary>Safe response text for unexpected server failures.</summary>
    public const string InternalServerError = "Internal server error";

    /// <summary>Safe response text for unsupported operations.</summary>
    public const string NotImplemented = "Operation is not implemented";
}

/// <summary>A service/runtime error that may safely cross the protocol boundary.</summary>
[SuppressMessage("Design", "CA1710", Justification = "PiServerError is the public error name used by the upstream package.")]
public class PiServerError : Exception
{
    /// <summary>Initializes a typed server operation error.</summary>
    public PiServerError(ProtocolErrorCode code, string message, JsonValue? details = null) : base(message)
    {
        Code = code;
        Details = details;
    }

    /// <summary>Protocol operation error code.</summary>
    public ProtocolErrorCode Code { get; }

    /// <summary>Optional JSON-compatible details returned to the client.</summary>
    public JsonValue? Details { get; }
}

/// <summary>Raised when a session runtime is already processing another operation.</summary>
public sealed class SessionBusyError : PiServerError
{
    /// <summary>Initializes a session-busy error.</summary>
    public SessionBusyError(string message = "Session is busy", JsonValue? details = null)
        : base(ProtocolErrorCode.Busy, message, details) { }
}

/// <summary>Raised when a session runtime is terminating or otherwise locked.</summary>
public sealed class SessionLockedError : PiServerError
{
    /// <summary>Initializes a session-locked error.</summary>
    public SessionLockedError(string message = "Session is locked", JsonValue? details = null)
        : base(ProtocolErrorCode.SessionLocked, message, details) { }
}

/// <summary>Raised when a requested session does not exist.</summary>
public sealed class SessionNotFoundError : PiServerError
{
    /// <summary>Initializes a missing-session error.</summary>
    public SessionNotFoundError(string message = "Session was not found", JsonValue? details = null)
        : base(ProtocolErrorCode.NotFound, message, details) { }
}

/// <summary>Raised when an operation is intentionally unavailable.</summary>
public sealed class NotImplementedError : PiServerError
{
    /// <summary>Initializes an unsupported-operation error.</summary>
    public NotImplementedError() : base(ProtocolErrorCode.NotImplemented, ServerErrorMessages.NotImplemented) { }
}

/// <summary>Unsafe failure retained for diagnostics but serialized as an internal error.</summary>
[SuppressMessage("Design", "CA1710", Justification = "InternalServerError is the public error name used by the upstream package.")]
public sealed class InternalServerError : Exception
{
    /// <summary>Initializes an internal error with its original cause.</summary>
    public InternalServerError(Exception cause) : base(ServerErrorMessages.InternalServerError, cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
    }
}
