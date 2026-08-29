using System.Collections;
using System.Globalization;
using System.Text;

namespace Pi.Protocol;

internal sealed class OrderedMap : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _entries = [];

    public int Count => _entries.Count;

    public KeyValuePair<string, object?> this[int index] => _entries[index];

    public void Add(string key, object? value) => _entries.Add(new KeyValuePair<string, object?>(key, value));

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class CborWriter
{
    private byte[] _buffer;
    private int _offset;
    private readonly uint _maxByteLength;

    public CborWriter(uint maxByteLength)
    {
        _maxByteLength = maxByteLength;
        _buffer = new byte[Math.Min(256U, maxByteLength)];
    }

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_offset++] = value;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_offset));
        _offset += bytes.Length;
    }

    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        _buffer[_offset++] = (byte)(value >> 8);
        _buffer[_offset++] = (byte)value;
    }

    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        _buffer[_offset++] = (byte)(value >> 24);
        _buffer[_offset++] = (byte)(value >> 16);
        _buffer[_offset++] = (byte)(value >> 8);
        _buffer[_offset++] = (byte)value;
    }

    public void WriteUInt64(ulong value)
    {
        WriteUInt32((uint)(value >> 32));
        WriteUInt32((uint)value);
    }

    public void WriteFloat64(double value)
    {
        EnsureCapacity(9);
        _buffer[_offset++] = 0xfb;
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        WriteUInt64(bits);
    }

    public byte[] Finish() => _buffer[.._offset];

    private void EnsureCapacity(int additionalBytes)
    {
        long required = (long)_offset + additionalBytes;
        if (required > _maxByteLength)
        {
            throw new CborError(
                $"CBOR byte length exceeds configured limit of {_maxByteLength.ToString(CultureInfo.InvariantCulture)}");
        }

        if (required <= _buffer.Length)
        {
            return;
        }

        int capacity = Math.Max(1, _buffer.Length);
        while (capacity < required)
        {
            long doubled = (long)capacity * 2;
            capacity = checked((int)Math.Min(_maxByteLength, Math.Max(required, doubled)));
        }

        byte[] expanded = new byte[capacity];
        _buffer.AsSpan().CopyTo(expanded);
        _buffer = expanded;
    }
}

/// <summary>Encodes the protocol's strict, definite-length RFC 8949 subset.</summary>
public static class CborEncoder
{
    /// <summary>Encodes one value as strict definite-length CBOR.</summary>
    public static byte[] EncodeCbor(object? value, CborOptions? options = null)
    {
        ResolvedCborOptions resolved = CborLimits.ResolveOptions(options);
        CborWriter writer = new(resolved.MaxByteLength);
        EncodeValue(writer, value, resolved, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return writer.Finish();
    }

    private static void WriteArgument(CborWriter writer, int majorType, ulong value)
    {
        byte prefix = (byte)(majorType << 5);
        if (value < 24)
        {
            writer.WriteByte((byte)(prefix | (byte)value));
        }
        else if (value <= byte.MaxValue)
        {
            writer.WriteByte((byte)(prefix | 24));
            writer.WriteByte((byte)value);
        }
        else if (value <= ushort.MaxValue)
        {
            writer.WriteByte((byte)(prefix | 25));
            writer.WriteUInt16((ushort)value);
        }
        else if (value <= uint.MaxValue)
        {
            writer.WriteByte((byte)(prefix | 26));
            writer.WriteUInt32((uint)value);
        }
        else
        {
            writer.WriteByte((byte)(prefix | 27));
            writer.WriteUInt64(value);
        }
    }

    private static void EncodeText(CborWriter writer, string value, ResolvedCborOptions options)
    {
        byte[] bytes;
        try
        {
            bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new CborError("CBOR text strings must contain valid Unicode scalar values", exception);
        }

        if (bytes.Length > options.MaxByteLength)
        {
            throw new CborError(
                $"CBOR text string length exceeds configured limit of {options.MaxByteLength.ToString(CultureInfo.InvariantCulture)}");
        }

        WriteArgument(writer, 3, (ulong)bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static void EncodeValue(
        CborWriter writer,
        object? value,
        ResolvedCborOptions options,
        uint depth,
        HashSet<object> ancestors)
    {
        if (depth > options.MaxDepth)
        {
            throw new CborError(
                $"CBOR nesting depth exceeds configured limit of {options.MaxDepth.ToString(CultureInfo.InvariantCulture)}");
        }

        switch (value)
        {
            case null:
                writer.WriteByte(0xf6);
                return;
            case bool boolean:
                writer.WriteByte(boolean ? (byte)0xf5 : (byte)0xf4);
                return;
            case string text:
                EncodeText(writer, text, options);
                return;
            case byte[] bytes:
                EncodeByteString(writer, bytes, options);
                return;
            case ReadOnlyMemory<byte> memory:
                EncodeByteString(writer, memory.Span, options);
                return;
            case JsonValue jsonValue:
                EncodeValue(writer, jsonValue.ToWireValue(), options, depth, ancestors);
                return;
        }

        if (TryGetNumber(value, out double number, out bool negativeZero))
        {
            if (!double.IsFinite(number))
            {
                throw new CborError("CBOR numbers must be finite");
            }

            if (Math.Truncate(number) == number && !negativeZero)
            {
                if (Math.Abs(number) > 9_007_199_254_740_991d)
                {
                    throw new CborError("CBOR integers must be safe JavaScript integers");
                }

                if (number >= 0)
                {
                    WriteArgument(writer, 0, (ulong)number);
                }
                else
                {
                    WriteArgument(writer, 1, checked((ulong)(-1d - number)));
                }
            }
            else
            {
                writer.WriteFloat64(number);
            }

            return;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            EncodeMap(writer, dictionary, options, depth, ancestors);
            return;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            EncodeMap(writer, readOnlyDictionary, options, depth, ancestors);
            return;
        }

        if (value is OrderedMap orderedMap)
        {
            EncodeMap(writer, orderedMap, options, depth, ancestors);
            return;
        }

        if (value is Array array)
        {
            EncodeArray(writer, array, options, depth, ancestors);
            return;
        }

        if (value is IList list)
        {
            EncodeList(writer, list, options, depth, ancestors);
            return;
        }

        throw new CborError($"Unsupported CBOR value type: {GetJavaScriptTypeName(value)}");
    }

    private static void EncodeByteString(CborWriter writer, ReadOnlySpan<byte> bytes, ResolvedCborOptions options)
    {
        if (bytes.Length > options.MaxByteLength)
        {
            throw new CborError(
                $"CBOR byte string length exceeds configured limit of {options.MaxByteLength.ToString(CultureInfo.InvariantCulture)}");
        }

        WriteArgument(writer, 2, (ulong)bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static void EncodeArray(
        CborWriter writer,
        Array array,
        ResolvedCborOptions options,
        uint depth,
        HashSet<object> ancestors)
    {
        if (!ancestors.Add(array))
        {
            throw new CborError("CBOR values must not contain cycles");
        }

        try
        {
            if (array.Length > options.MaxContainerLength)
            {
                throw new CborError(
                    $"CBOR array length exceeds configured limit of {options.MaxContainerLength.ToString(CultureInfo.InvariantCulture)}");
            }

            WriteArgument(writer, 4, (ulong)array.Length);
            foreach (object? item in array)
            {
                EncodeValue(writer, item, options, depth + 1, ancestors);
            }
        }
        finally
        {
            ancestors.Remove(array);
        }
    }

    private static void EncodeList(
        CborWriter writer,
        IList list,
        ResolvedCborOptions options,
        uint depth,
        HashSet<object> ancestors)
    {
        if (!ancestors.Add(list))
        {
            throw new CborError("CBOR values must not contain cycles");
        }

        try
        {
            if ((uint)list.Count > options.MaxContainerLength)
            {
                throw new CborError(
                    $"CBOR array length exceeds configured limit of {options.MaxContainerLength.ToString(CultureInfo.InvariantCulture)}");
            }

            WriteArgument(writer, 4, (ulong)list.Count);
            foreach (object? item in list)
            {
                EncodeValue(writer, item, options, depth + 1, ancestors);
            }
        }
        finally
        {
            ancestors.Remove(list);
        }
    }

    private static void EncodeMap(
        CborWriter writer,
        IEnumerable<KeyValuePair<string, object?>> map,
        ResolvedCborOptions options,
        uint depth,
        HashSet<object> ancestors)
    {
        object mapReference = map;
        if (!ancestors.Add(mapReference))
        {
            throw new CborError("CBOR values must not contain cycles");
        }

        try
        {
            List<KeyValuePair<string, object?>> entries = [];
            foreach (KeyValuePair<string, object?> entry in map)
            {
                // C# has no JavaScript `undefined` value. Optional protocol fields are
                // omitted by the caller when constructing the map; null is a real CBOR
                // value and must remain present here.
                entries.Add(entry);
            }

            if (entries.Count > options.MaxContainerLength)
            {
                throw new CborError(
                    $"CBOR map length exceeds configured limit of {options.MaxContainerLength.ToString(CultureInfo.InvariantCulture)}");
            }

            WriteArgument(writer, 5, (ulong)entries.Count);
            foreach (KeyValuePair<string, object?> entry in entries)
            {
                EncodeText(writer, entry.Key, options);
                EncodeValue(writer, entry.Value, options, depth + 1, ancestors);
            }
        }
        finally
        {
            ancestors.Remove(mapReference);
        }
    }

    private static bool TryGetNumber(object value, out double number, out bool negativeZero)
    {
        negativeZero = false;
        switch (value)
        {
            case byte v: number = v; return true;
            case sbyte v: number = v; return true;
            case ushort v: number = v; return true;
            case short v: number = v; return true;
            case uint v: number = v; return true;
            case int v: number = v; return true;
            case ulong v: number = v; return true;
            case long v: number = v; return true;
            case float v: number = v; negativeZero = v == 0 && float.IsNegative(v); return true;
            case double v: number = v; negativeZero = v == 0 && double.IsNegative(v); return true;
            case decimal v: number = (double)v; return true;
            default:
                number = 0;
                return false;
        }
    }

    private static string GetJavaScriptTypeName(object value) => value switch
    {
        Delegate => "function",
        System.Numerics.BigInteger => "bigint",
        _ => "object",
    };
}
