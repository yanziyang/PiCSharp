using System.Security.Cryptography;

namespace Pi.Ai;

/// <summary>Generates time-ordered identifiers used by Pi request and response records.</summary>
public static class UuidUtilities
{
    private static readonly object _gate = new();
    private static long _lastTimestamp = long.MinValue;
    private static uint _sequence;

    /// <summary>Generates a time-ordered UUIDv7 using the RFC 9562 layout.</summary>
    public static string UuidV7()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            if (timestamp > _lastTimestamp)
            {
                _sequence = ((uint)random[6] << 24) |
                            ((uint)random[7] << 16) |
                            ((uint)random[8] << 8) |
                            random[9];
                _lastTimestamp = timestamp;
            }
            else
            {
                _sequence = unchecked(_sequence + 1);
                if (_sequence == 0)
                {
                    _lastTimestamp++;
                }
            }

            Span<byte> bytes = stackalloc byte[16];
            var monotonicTimestamp = _lastTimestamp;
            bytes[0] = (byte)(monotonicTimestamp >> 40);
            bytes[1] = (byte)(monotonicTimestamp >> 32);
            bytes[2] = (byte)(monotonicTimestamp >> 24);
            bytes[3] = (byte)(monotonicTimestamp >> 16);
            bytes[4] = (byte)(monotonicTimestamp >> 8);
            bytes[5] = (byte)monotonicTimestamp;
            bytes[6] = (byte)(0x70 | ((_sequence >> 28) & 0x0F));
            bytes[7] = (byte)(_sequence >> 20);
            bytes[8] = (byte)(0x80 | ((_sequence >> 14) & 0x3F));
            bytes[9] = (byte)(_sequence >> 6);
            bytes[10] = (byte)(((_sequence & 0x3F) << 2) | ((uint)random[10] & 0x03u));
            random[11..].CopyTo(bytes[11..]);

            return string.Create(36, bytes.ToArray(), static (destination, source) =>
            {
                const string hex = "0123456789abcdef";
                var outputIndex = 0;
                for (var index = 0; index < source.Length; index++)
                {
                    if (index is 4 or 6 or 8 or 10)
                    {
                        destination[outputIndex++] = '-';
                    }

                    var value = source[index];
                    destination[outputIndex++] = hex[value >> 4];
                    destination[outputIndex++] = hex[value & 0x0F];
                }
            });
        }
    }
}
