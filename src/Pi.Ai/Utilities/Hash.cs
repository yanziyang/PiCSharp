namespace Pi.Ai;

/// <summary>Fast deterministic hashes used to shorten provider identifiers.</summary>
public static class HashUtilities
{
    /// <summary>Returns the pinned Pi two-word base-36 hash for a UTF-16 string.</summary>
    public static string ShortHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        uint h1 = 0xDEADBEEFu;
        uint h2 = 0x41C6CE57u;
        foreach (var character in value)
        {
            h1 = unchecked((h1 ^ character) * 2654435761u);
            h2 = unchecked((h2 ^ character) * 1597334677u);
        }

        h1 = unchecked((h1 ^ (h1 >> 16)) * 2246822507u) ^
             unchecked((h2 ^ (h2 >> 13)) * 3266489909u);
        h2 = unchecked((h2 ^ (h2 >> 16)) * 2246822507u) ^
             unchecked((h1 ^ (h1 >> 13)) * 3266489909u);

        return ToBase36(h2) + ToBase36(h1);
    }

    private static string ToBase36(uint value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return "0";
        }

        Span<char> buffer = stackalloc char[7];
        var index = buffer.Length;
        while (value > 0)
        {
            buffer[--index] = digits[(int)(value % 36)];
            value /= 36;
        }

        return new string(buffer[index..]);
    }
}
