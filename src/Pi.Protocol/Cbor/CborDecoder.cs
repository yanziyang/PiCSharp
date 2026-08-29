using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Pi.Protocol;

internal sealed class CborReader
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly ResolvedCborOptions _options;
    private int _offset;

    public CborReader(ReadOnlyMemory<byte> bytes, ResolvedCborOptions options)
    {
        _bytes = bytes;
        _options = options;
    }

    public object? Decode()
    {
        object? value = ReadItem(0);
        if (_offset != _bytes.Length)
        {
            throw new CborError("CBOR payload contains trailing data");
        }

        return value;
    }

    private object? ReadItem(uint depth)
    {
        if (depth > _options.MaxDepth)
        {
            throw new CborError(
                $"CBOR nesting depth exceeds configured limit of {_options.MaxDepth.ToString(CultureInfo.InvariantCulture)}");
        }

        byte initial = ReadByte();
        int majorType = initial >> 5;
        int additionalInformation = initial & 0x1f;
        return majorType switch
        {
            0 => ReadArgument(additionalInformation),
            1 => ReadNegativeInteger(additionalInformation),
            2 => ReadByteString(additionalInformation),
            3 => ReadTextString(additionalInformation),
            4 => ReadArray(additionalInformation, depth),
            5 => ReadMap(additionalInformation, depth),
            6 => throw new CborError("CBOR tags are not supported"),
            7 => ReadSimple(additionalInformation),
            _ => throw new CborError("Malformed CBOR major type"),
        };
    }

    private long ReadArgument(int additionalInformation)
    {
        ulong value = ReadArgumentUnsigned(additionalInformation);
        if (value > 9_007_199_254_740_991UL)
        {
            throw new CborError("Decoded CBOR integer or length is outside the safe range");
        }

        return checked((long)value);
    }

    private object ReadNegativeInteger(int additionalInformation)
    {
        ulong encoded = ReadArgumentUnsigned(additionalInformation);
        if (encoded >= 9_007_199_254_740_991UL)
        {
            throw new CborError("Decoded CBOR integer is outside the safe range");
        }

        return checked(-1L - (long)encoded);
    }

    private byte[] ReadByteString(int additionalInformation)
    {
        int length = ReadLength(additionalInformation, "byte string", _options.MaxByteLength);
        return ReadBytes(length).ToArray();
    }

    private string ReadTextString(int additionalInformation)
    {
        int length = ReadLength(additionalInformation, "text string", _options.MaxByteLength);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(ReadBytes(length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new CborError("CBOR text string contains invalid UTF-8", exception);
        }
    }

    private List<object?> ReadArray(int additionalInformation, uint depth)
    {
        int length = ReadLength(additionalInformation, "array", _options.MaxContainerLength);
        List<object?> result = new(length);
        for (int index = 0; index < length; index++)
        {
            result.Add(ReadItem(depth + 1));
        }

        return result;
    }

    private Dictionary<string, object?> ReadMap(int additionalInformation, uint depth)
    {
        int length = ReadLength(additionalInformation, "map", _options.MaxContainerLength);
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        for (int index = 0; index < length; index++)
        {
            object? key = ReadItem(depth + 1);
            if (key is not string stringKey)
            {
                throw new CborError("CBOR map keys must be strings");
            }

            if (!result.TryAdd(stringKey, ReadItem(depth + 1)))
            {
                throw new CborError("CBOR map contains a duplicate key");
            }
        }

        return result;
    }

    private object ReadSimple(int additionalInformation)
    {
        return additionalInformation switch
        {
            20 => false,
            21 => true,
            22 => null!,
            27 => ReadFloat64(),
            31 => throw new CborError("CBOR break marker is not supported"),
            _ => throw new CborError("Unsupported CBOR simple value or floating-point width"),
        };
    }

    private double ReadFloat64()
    {
        ReadOnlySpan<byte> bytes = ReadBytes(8);
        double value = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64BigEndian(bytes));
        if (!double.IsFinite(value))
        {
            throw new CborError("Decoded CBOR number must be finite");
        }

        if (Math.Truncate(value) == value && Math.Abs(value) > 9_007_199_254_740_991d)
        {
            throw new CborError("Decoded CBOR integer is outside the safe range");
        }

        return value;
    }

    private int ReadLength(int additionalInformation, string kind, uint limit)
    {
        if (additionalInformation == 31)
        {
            throw new CborError($"Indefinite-length CBOR {kind}s are not supported");
        }

        ulong length = ReadArgumentUnsigned(additionalInformation);
        if (length > limit)
        {
            throw new CborError(
                $"CBOR {kind} length exceeds configured limit of {limit.ToString(CultureInfo.InvariantCulture)}");
        }

        if (length > int.MaxValue)
        {
            throw new CborError($"CBOR {kind} length is too large for this runtime");
        }

        return (int)length;
    }

    private ulong ReadArgumentUnsigned(int additionalInformation)
    {
        if (additionalInformation < 24)
        {
            return (ulong)additionalInformation;
        }

        return additionalInformation switch
        {
            24 => ReadByte(),
            25 => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2)),
            26 => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4)),
            27 => ReadSafeUInt64(),
            31 => throw new CborError("Indefinite-length CBOR items are not supported"),
            _ => throw new CborError("Malformed CBOR additional information"),
        };
    }

    private ulong ReadSafeUInt64()
    {
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));
        if (value > 9_007_199_254_740_991UL)
        {
            throw new CborError("Decoded CBOR integer or length is outside the safe range");
        }

        return value;
    }

    private byte ReadByte()
    {
        if (_offset >= _bytes.Length)
        {
            throw new CborError("Truncated CBOR payload");
        }

        return _bytes.Span[_offset++];
    }

    private ReadOnlySpan<byte> ReadBytes(int length)
    {
        if (length > _bytes.Length - _offset)
        {
            throw new CborError("Truncated CBOR payload");
        }

        ReadOnlySpan<byte> value = _bytes.Span.Slice(_offset, length);
        _offset += length;
        return value;
    }
}

/// <summary>Decodes exactly one item from the protocol's strict RFC 8949 subset.</summary>
public static class CborDecoder
{
    /// <summary>Decodes exactly one strict CBOR item.</summary>
    public static object? DecodeCbor(ReadOnlySpan<byte> bytes, CborOptions? options = null)
    {
        ResolvedCborOptions resolved = CborLimits.ResolveOptions(options);
        if ((ulong)bytes.Length > resolved.MaxByteLength)
        {
            throw new CborError(
                $"CBOR byte length exceeds configured limit of {resolved.MaxByteLength.ToString(CultureInfo.InvariantCulture)}");
        }

        return new CborReader(bytes.ToArray(), resolved).Decode();
    }
}
