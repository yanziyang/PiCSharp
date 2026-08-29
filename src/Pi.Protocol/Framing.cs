using System.Buffers.Binary;
using System.Globalization;

namespace Pi.Protocol;

/// <summary>Options controlling length-prefixed frame decoding.</summary>
public sealed record FrameDecoderOptions
{
    /// <summary>Maximum permitted payload length.</summary>
    public double? MaxFrameLength { get; init; }
}

/// <summary>Raised when a length-prefixed frame is malformed or truncated.</summary>
public sealed class FrameError : PiException
{
    /// <summary>Initializes a frame error.</summary>
    public FrameError(string message) : base(message) { }

    /// <summary>Initializes a frame error with an inner exception.</summary>
    public FrameError(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Binary framing helpers for the Pi protocol.</summary>
public static class Framing
{
    private const int _frameHeaderLength = 4;

    /// <summary>Default upper bound for one framed CBOR payload.</summary>
    public const uint DefaultMaxFrameLength = 16 * 1024 * 1024;

    /// <summary>Prefixes a payload with its unsigned 32-bit big-endian byte length.</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload)
    {
        if ((ulong)payload.Length > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Frame payload exceeds the unsigned 32-bit length limit");
        }

        byte[] frame = new byte[_frameHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, _frameHeaderLength), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(_frameHeaderLength));
        return frame;
    }

    /// <summary>Validates that bytes contain exactly one complete bounded frame.</summary>
    public static void AssertCompleteFrame(ReadOnlySpan<byte> frame, FrameDecoderOptions? options = null)
    {
        if (frame.Length < _frameHeaderLength)
        {
            throw new FrameError("Frame does not contain a complete length prefix");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(frame[.._frameHeaderLength]);
        uint maxFrameLength = ResolveMaxFrameLength(options);
        if (length > maxFrameLength)
        {
            throw new FrameError(
                $"Frame length {length.ToString(CultureInfo.InvariantCulture)} exceeds configured limit of {maxFrameLength.ToString(CultureInfo.InvariantCulture)}");
        }

        if ((ulong)frame.Length != (ulong)_frameHeaderLength + length)
        {
            throw new FrameError("Frame must contain exactly one complete payload");
        }
    }

    internal static uint ResolveMaxFrameLength(FrameDecoderOptions? options)
    {
        double value = options?.MaxFrameLength ?? DefaultMaxFrameLength;
        if (!double.IsFinite(value) || value < 0 || value > uint.MaxValue || Math.Truncate(value) != value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                value,
                $"maxFrameLength must be an integer between 0 and {uint.MaxValue.ToString(CultureInfo.InvariantCulture)}");
        }

        return (uint)value;
    }
}

/// <summary>Incrementally splits arbitrary byte chunks into framed payloads.</summary>
public sealed class FrameDecoder
{
    private const int _frameHeaderLength = 4;
    private const int _payloadBlockSize = 64 * 1024;

    private readonly byte[] _header = new byte[_frameHeaderLength];
    private readonly uint _maxFrameLength;
    private readonly List<byte[]> _payloadBlocks = [];
    private int _headerLength;
    private byte[]? _currentPayloadBlock;
    private int _currentPayloadBlockLength;
    private uint? _expectedPayloadLength;
    private uint _payloadLength;
    private DecoderState _state = DecoderState.Open;

    /// <summary>Initializes a frame decoder.</summary>
    public FrameDecoder(FrameDecoderOptions? options = null)
    {
        _maxFrameLength = Framing.ResolveMaxFrameLength(options);
    }

    /// <summary>Pushes a chunk and returns every complete payload now available.</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> chunk)
    {
        EnsureOpen();
        List<byte[]> frames = [];
        int chunkOffset = 0;

        while (chunkOffset < chunk.Length)
        {
            if (_expectedPayloadLength is null)
            {
                int headerBytes = Math.Min(_frameHeaderLength - _headerLength, chunk.Length - chunkOffset);
                chunk.Slice(chunkOffset, headerBytes).CopyTo(_header.AsSpan(_headerLength));
                _headerLength += headerBytes;
                chunkOffset += headerBytes;
                if (_headerLength < _frameHeaderLength)
                {
                    continue;
                }

                uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(_header);
                _headerLength = 0;
                if (frameLength > _maxFrameLength)
                {
                    Fail(
                        $"Frame length {frameLength.ToString(CultureInfo.InvariantCulture)} exceeds configured limit of {_maxFrameLength.ToString(CultureInfo.InvariantCulture)}");
                }

                if (frameLength == 0)
                {
                    frames.Add([]);
                    continue;
                }

                _expectedPayloadLength = frameLength;
                _payloadBlocks.Clear();
                _currentPayloadBlock = null;
                _currentPayloadBlockLength = 0;
                _payloadLength = 0;
            }

            uint expected = _expectedPayloadLength.GetValueOrDefault();
            while (chunkOffset < chunk.Length && _payloadLength < expected)
            {
                byte[]? block = _currentPayloadBlock;
                if (block is null || _currentPayloadBlockLength == block.Length)
                {
                    int blockLength = checked((int)Math.Min(_payloadBlockSize, expected - _payloadLength));
                    block = new byte[blockLength];
                    _payloadBlocks.Add(block);
                    _currentPayloadBlock = block;
                    _currentPayloadBlockLength = 0;
                }

                int payloadBytes = Math.Min(block.Length - _currentPayloadBlockLength, chunk.Length - chunkOffset);
                chunk.Slice(chunkOffset, payloadBytes).CopyTo(block.AsSpan(_currentPayloadBlockLength));
                _currentPayloadBlockLength += payloadBytes;
                _payloadLength += (uint)payloadBytes;
                chunkOffset += payloadBytes;
            }

            if (_payloadLength == expected)
            {
                if (_payloadBlocks.Count == 1)
                {
                    frames.Add(_payloadBlocks[0]);
                }
                else
                {
                    byte[] payload = new byte[checked((int)expected)];
                    int offset = 0;
                    foreach (byte[] payloadBlock in _payloadBlocks)
                    {
                        payloadBlock.CopyTo(payload.AsSpan(offset));
                        offset += payloadBlock.Length;
                    }

                    frames.Add(payload);
                }

                _payloadBlocks.Clear();
                _currentPayloadBlock = null;
                _currentPayloadBlockLength = 0;
                _expectedPayloadLength = null;
                _payloadLength = 0;
            }
        }

        return frames;
    }

    /// <summary>Marks the stream complete and rejects a partial header or payload.</summary>
    public void End()
    {
        if (_state == DecoderState.Ended)
        {
            throw new FrameError("Frame decoder has ended");
        }

        if (_state == DecoderState.Failed)
        {
            throw new FrameError("Frame decoder has failed");
        }

        if (_headerLength != 0 || _expectedPayloadLength is not null)
        {
            Fail("Truncated frame at end of stream");
        }

        _state = DecoderState.Ended;
    }

    private void EnsureOpen()
    {
        if (_state == DecoderState.Ended)
        {
            throw new FrameError("Frame decoder has ended");
        }

        if (_state == DecoderState.Failed)
        {
            throw new FrameError("Frame decoder has failed");
        }
    }

    private void Fail(string message)
    {
        _state = DecoderState.Failed;
        _headerLength = 0;
        _payloadBlocks.Clear();
        _currentPayloadBlock = null;
        _currentPayloadBlockLength = 0;
        _expectedPayloadLength = null;
        _payloadLength = 0;
        throw new FrameError(message);
    }

    private enum DecoderState
    {
        Open,
        Ended,
        Failed,
    }
}
