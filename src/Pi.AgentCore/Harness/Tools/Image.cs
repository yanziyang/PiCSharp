namespace Pi.AgentCore.Harness.Tools;

/// <summary>Image signature detection and base64 helpers used by the read tool.</summary>
public static class ImageUtilities
{
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns a supported image MIME type when the content is a valid static image.</summary>
    public static string? DetectSupportedImageMimeType(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (StartsWith(buffer, [0xFF, 0xD8, 0xFF]))
        {
            return buffer.Length > 3 && buffer[3] == 0xF7 ? null : "image/jpeg";
        }

        if (StartsWith(buffer, _pngSignature))
        {
            return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        }

        if (StartsWithAscii(buffer, 0, "GIF"))
        {
            return "image/gif";
        }

        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP"))
        {
            return "image/webp";
        }

        return StartsWithAscii(buffer, 0, "BM") && IsBmp(buffer) ? "image/bmp" : null;
    }

    /// <summary>Encodes bytes using standard base64.</summary>
    public static string EncodeBase64(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static bool IsPng(byte[] buffer) =>
        buffer.Length >= 16 && ReadUInt32BigEndian(buffer, _pngSignature.Length) == 13 && StartsWithAscii(buffer, 12, "IHDR");

    private static bool IsAnimatedPng(byte[] buffer)
    {
        var offset = _pngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = ReadUInt32BigEndian(buffer, offset);
            var chunkTypeOffset = offset + 4;
            if (StartsWithAscii(buffer, chunkTypeOffset, "acTL"))
            {
                return true;
            }

            if (StartsWithAscii(buffer, chunkTypeOffset, "IDAT"))
            {
                return false;
            }

            var nextOffset = offset + 8L + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > buffer.Length)
            {
                return false;
            }

            offset = (int)nextOffset;
        }

        return false;
    }

    private static bool IsBmp(byte[] buffer)
    {
        if (buffer.Length < 26)
        {
            return false;
        }

        var declaredFileSize = ReadUInt32LittleEndian(buffer, 2);
        var pixelDataOffset = ReadUInt32LittleEndian(buffer, 10);
        var dibHeaderSize = ReadUInt32LittleEndian(buffer, 14);
        if (declaredFileSize != 0 && declaredFileSize < 26)
        {
            return false;
        }

        if (pixelDataOffset < 14 + dibHeaderSize)
        {
            return false;
        }

        if (declaredFileSize != 0 && pixelDataOffset >= declaredFileSize)
        {
            return false;
        }

        int colorPlanes;
        int bitsPerPixel;
        if (dibHeaderSize == 12)
        {
            colorPlanes = ReadUInt16LittleEndian(buffer, 22);
            bitsPerPixel = ReadUInt16LittleEndian(buffer, 24);
        }
        else if (dibHeaderSize is >= 40 and <= 124)
        {
            if (buffer.Length < 30)
            {
                return false;
            }

            colorPlanes = ReadUInt16LittleEndian(buffer, 26);
            bitsPerPixel = ReadUInt16LittleEndian(buffer, 28);
        }
        else
        {
            return false;
        }

        return colorPlanes == 1 && bitsPerPixel is 1 or 4 or 8 or 16 or 24 or 32;
    }

    private static int ReadUInt16LittleEndian(byte[] buffer, int offset) =>
        (buffer[offset] - 0) + (buffer[offset + 1] << 8);

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) |
        ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) |
        buffer[offset + 3];

    private static uint ReadUInt32LittleEndian(byte[] buffer, int offset) =>
        buffer[offset] |
        ((uint)buffer[offset + 1] << 8) |
        ((uint)buffer[offset + 2] << 16) |
        ((uint)buffer[offset + 3] << 24);

    private static bool StartsWith(byte[] buffer, byte[] bytes)
    {
        if (buffer.Length < bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            if (buffer[index] != bytes[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithAscii(byte[] buffer, int offset, string text)
    {
        if (offset < 0 || buffer.Length < offset + text.Length)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (buffer[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }
}
