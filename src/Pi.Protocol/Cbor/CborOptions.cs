using System.Globalization;

namespace Pi.Protocol;

/// <summary>Base exception for errors raised by the Pi protocol implementation.</summary>
public class PiException : Exception
{
    /// <summary>Initializes an exception with a message.</summary>
    public PiException(string message) : base(message) { }

    /// <summary>Initializes an exception with a message and an inner exception.</summary>
    public PiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when a CBOR value is malformed or outside the configured limits.</summary>
public sealed class CborError : PiException
{
    /// <summary>Initializes a CBOR error.</summary>
    public CborError(string message) : base(message) { }

    /// <summary>Initializes a CBOR error with an inner exception.</summary>
    public CborError(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Options controlling the strict CBOR subset used by the protocol.</summary>
public sealed record CborOptions
{
    /// <summary>Maximum encoded input/output bytes and maximum byte/text string length.</summary>
    public double? MaxByteLength { get; init; }

    /// <summary>Maximum number of elements in an array or entries in a map.</summary>
    public double? MaxContainerLength { get; init; }

    /// <summary>Maximum recursive item depth.</summary>
    public double? MaxDepth { get; init; }
}

/// <summary>Default limits resolved from caller-supplied CBOR options.</summary>
public sealed record ResolvedCborOptions(uint MaxByteLength, uint MaxContainerLength, uint MaxDepth);

/// <summary>CBOR constants and option resolution helpers.</summary>
public static class CborLimits
{
    /// <summary>2 to the power of 32.</summary>
    public const ulong Uint32Base = 0x1_0000_0000UL;

    /// <summary>The largest unsigned 32-bit value.</summary>
    public const uint MaxUint32 = uint.MaxValue;

    /// <summary>Safe default maximum encoded CBOR byte length.</summary>
    public const uint DefaultMaxCborByteLength = 16 * 1024 * 1024;

    /// <summary>Safe default maximum array/map length.</summary>
    public const uint DefaultMaxCborContainerLength = 1_000_000;

    /// <summary>Safe default maximum recursive CBOR depth.</summary>
    public const uint DefaultMaxCborDepth = 64;

    private const uint _maxConfiguredDepth = 512;

    /// <summary>Validates and resolves optional CBOR limits.</summary>
    public static ResolvedCborOptions ResolveOptions(CborOptions? options)
    {
        return new ResolvedCborOptions(
            ResolveLimit("maxByteLength", options?.MaxByteLength ?? DefaultMaxCborByteLength, MaxUint32),
            ResolveLimit("maxContainerLength", options?.MaxContainerLength ?? DefaultMaxCborContainerLength, MaxUint32),
            ResolveLimit("maxDepth", options?.MaxDepth ?? DefaultMaxCborDepth, _maxConfiguredDepth));
    }

    private static uint ResolveLimit(string name, double value, uint maximum)
    {
        if (!double.IsFinite(value) || value < 0 || value > maximum || Math.Truncate(value) != value)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"{name} must be an integer between 0 and {maximum.ToString(CultureInfo.InvariantCulture)}");
        }

        return (uint)value;
    }
}
