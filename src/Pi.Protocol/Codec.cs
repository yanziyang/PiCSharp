using System.Globalization;

namespace Pi.Protocol;

/// <summary>Raised when a decoded value does not satisfy the Pi protocol schema.</summary>
public sealed class ProtocolValidationError : PiException
{
    /// <summary>Initializes a protocol validation error.</summary>
    public ProtocolValidationError(string message) : base(message) { }

    /// <summary>Initializes a protocol validation error with an inner exception.</summary>
    public ProtocolValidationError(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Public entry points for validating and encoding Pi protocol messages.</summary>
public static class ProtocolCodec
{
    /// <summary>Validates a decoded client message and returns its typed representation.</summary>
    public static ClientMessage ParseClientMessage(object? value)
    {
        try
        {
            return ProtocolParsing.ParseClientMessage(value is ClientMessage typed ? ProtocolWire.ToWire(typed) : value);
        }
        catch (ProtocolValidationError)
        {
            throw new ProtocolValidationError("Invalid client protocol message");
        }
        catch (Exception exception)
        {
            throw new ProtocolValidationError("Invalid client protocol message", exception);
        }
    }

    /// <summary>Validates a decoded server message and returns its typed representation.</summary>
    public static ServerMessage ParseServerMessage(object? value)
    {
        try
        {
            return ProtocolParsing.ParseServerMessage(value is ServerMessage typed ? ProtocolWire.ToWire(typed) : value);
        }
        catch (ProtocolValidationError)
        {
            throw new ProtocolValidationError("Invalid server protocol message");
        }
        catch (Exception exception)
        {
            throw new ProtocolValidationError("Invalid server protocol message", exception);
        }
    }

    /// <summary>Validates and encodes one complete length-prefixed client message.</summary>
    public static byte[] EncodeClientMessage(ClientMessage message, FrameDecoderOptions? options = null)
    {
        return EncodeProtocolMessage(message, static value => ParseClientMessage(value), "client", options, ProtocolWire.ToWire);
    }

    /// <summary>Validates and encodes a decoded client message value.</summary>
    public static byte[] EncodeClientMessage(object? message, FrameDecoderOptions? options = null)
    {
        if (message is ClientMessage typed)
        {
            return EncodeClientMessage(typed, options);
        }

        ParseClientMessage(message);
        return EncodeRawProtocolMessage(message, "client", options);
    }

    /// <summary>Validates and encodes one complete length-prefixed server message.</summary>
    public static byte[] EncodeServerMessage(ServerMessage message, FrameDecoderOptions? options = null)
    {
        return EncodeProtocolMessage(message, static value => ParseServerMessage(value), "server", options, ProtocolWire.ToWire);
    }

    /// <summary>Validates and encodes a decoded server message value.</summary>
    public static byte[] EncodeServerMessage(object? message, FrameDecoderOptions? options = null)
    {
        if (message is ServerMessage typed)
        {
            return EncodeServerMessage(typed, options);
        }

        ParseServerMessage(message);
        return EncodeRawProtocolMessage(message, "server", options);
    }

    private static byte[] EncodeProtocolMessage<TMessage>(
        TMessage message,
        Func<object?, TMessage> parse,
        string kind,
        FrameDecoderOptions? options,
        Func<TMessage, OrderedMap> toWire)
    {
        try
        {
            TMessage validated = parse(toWire(message));
            uint maxFrameLength = Framing.ResolveMaxFrameLength(options);
            byte[] payload = CborEncoder.EncodeCbor(
                toWire(validated),
                new CborOptions { MaxByteLength = maxFrameLength });
            byte[] frame = Framing.EncodeFrame(payload);
            Framing.AssertCompleteFrame(frame, new FrameDecoderOptions { MaxFrameLength = maxFrameLength });
            return frame;
        }
        catch (ProtocolValidationError)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ProtocolValidationError(
                $"Unable to encode {kind} protocol message: {BoundedErrorMessage(exception)}", exception);
        }
    }

    private static byte[] EncodeRawProtocolMessage(object? message, string kind, FrameDecoderOptions? options)
    {
        try
        {
            uint maxFrameLength = Framing.ResolveMaxFrameLength(options);
            byte[] payload = CborEncoder.EncodeCbor(
                message,
                new CborOptions { MaxByteLength = maxFrameLength });
            byte[] frame = Framing.EncodeFrame(payload);
            Framing.AssertCompleteFrame(frame, new FrameDecoderOptions { MaxFrameLength = maxFrameLength });
            return frame;
        }
        catch (ProtocolValidationError)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ProtocolValidationError(
                $"Unable to encode {kind} protocol message: {BoundedErrorMessage(exception)}", exception);
        }
    }

    private static string BoundedErrorMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 500 ? message : $"{message[..497]}...";
    }
}

/// <summary>Incrementally decodes and validates framed client messages.</summary>
public sealed class ClientMessageDecoder
{
    private readonly ValidatedMessageDecoder<ClientMessage> _decoder;

    /// <summary>Initializes a client message decoder.</summary>
    public ClientMessageDecoder(FrameDecoderOptions? options = null)
    {
        _decoder = new ValidatedMessageDecoder<ClientMessage>("client", ProtocolCodec.ParseClientMessage, options);
    }

    /// <summary>Pushes arbitrary bytes and returns each complete client message.</summary>
    public IReadOnlyList<ClientMessage> Push(ReadOnlySpan<byte> chunk) => _decoder.Push(chunk);

    /// <summary>Ends the stream and rejects a truncated final frame.</summary>
    public void End() => _decoder.End();
}

/// <summary>Incrementally decodes and validates framed server messages.</summary>
public sealed class ServerMessageDecoder
{
    private readonly ValidatedMessageDecoder<ServerMessage> _decoder;

    /// <summary>Initializes a server message decoder.</summary>
    public ServerMessageDecoder(FrameDecoderOptions? options = null)
    {
        _decoder = new ValidatedMessageDecoder<ServerMessage>("server", ProtocolCodec.ParseServerMessage, options);
    }

    /// <summary>Pushes arbitrary bytes and returns each complete server message.</summary>
    public IReadOnlyList<ServerMessage> Push(ReadOnlySpan<byte> chunk) => _decoder.Push(chunk);

    /// <summary>Ends the stream and rejects a truncated final frame.</summary>
    public void End() => _decoder.End();
}

/// <summary>Factory methods for incremental protocol message decoders.</summary>
public static class MessageDecoders
{
    /// <summary>Creates an incremental client message decoder.</summary>
    public static ClientMessageDecoder CreateClientMessageDecoder(FrameDecoderOptions? options = null) => new(options);

    /// <summary>Creates an incremental server message decoder.</summary>
    public static ServerMessageDecoder CreateServerMessageDecoder(FrameDecoderOptions? options = null) => new(options);
}

/// <summary>Protocol constants and version negotiation helpers.</summary>
public static class Protocol
{
    /// <summary>The current Pi protocol version.</summary>
    public const int ProtocolVersion = ProtocolConstants.ProtocolVersion;

    /// <summary>Returns whether a numeric version is the supported protocol version.</summary>
    public static bool IsSupportedProtocolVersion(double version) =>
        double.IsFinite(version) && Math.Truncate(version) == version && version == ProtocolVersion;
}

internal sealed class ValidatedMessageDecoder<TMessage>
{
    private readonly FrameDecoder _frames;
    private readonly string _kind;
    private readonly uint _maxFrameLength;
    private readonly Func<object?, TMessage> _parse;
    private bool _failed;

    public ValidatedMessageDecoder(string kind, Func<object?, TMessage> parse, FrameDecoderOptions? options)
    {
        _frames = new FrameDecoder(options);
        _kind = kind;
        _maxFrameLength = Framing.ResolveMaxFrameLength(options);
        _parse = parse;
    }

    public IReadOnlyList<TMessage> Push(ReadOnlySpan<byte> chunk)
    {
        if (_failed)
        {
            throw new ProtocolValidationError($"{_kind} message decoder has failed");
        }

        try
        {
            List<TMessage> messages = [];
            foreach (byte[] frame in _frames.Push(chunk))
            {
                messages.Add(_parse(CborDecoder.DecodeCbor(frame, new CborOptions { MaxByteLength = _maxFrameLength })));
            }

            return messages;
        }
        catch (ProtocolValidationError)
        {
            _failed = true;
            throw;
        }
        catch (Exception exception)
        {
            _failed = true;
            throw new ProtocolValidationError(
                $"Invalid {_kind} protocol frame: {BoundedErrorMessage(exception)}", exception);
        }
    }

    public void End()
    {
        if (_failed)
        {
            throw new ProtocolValidationError($"{_kind} message decoder has failed");
        }

        try
        {
            _frames.End();
        }
        catch (Exception exception)
        {
            _failed = true;
            throw new ProtocolValidationError(
                $"Invalid {_kind} protocol framing: {BoundedErrorMessage(exception)}", exception);
        }
    }

    private static string BoundedErrorMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 500 ? message : $"{message[..497]}...";
    }
}
