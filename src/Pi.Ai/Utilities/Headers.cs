using System.Net.Http.Headers;

namespace Pi.Ai;

/// <summary>Conversions between .NET and provider header representations.</summary>
public static class HeaderUtilities
{
    /// <summary>Copies HTTP headers into a string-valued ordinal dictionary.</summary>
    public static Dictionary<string, string> HeadersToRecord(HttpHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            result[pair.Key] = string.Join(", ", pair.Value);
        }

        return result;
    }

    /// <summary>
    /// Removes null-valued provider header overrides and returns null when no headers remain.
    /// </summary>
    public static Dictionary<string, string>? ProviderHeadersToRecord(
        IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            if (pair.Value is not null)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result.Count == 0 ? null : result;
    }
}
