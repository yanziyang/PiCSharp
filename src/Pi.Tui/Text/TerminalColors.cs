using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>One RGB terminal color with channels in the range 0 through 255.</summary>
public readonly record struct RgbColor(int R, int G, int B);

/// <summary>The terminal's reported light/dark color scheme.</summary>
public enum TerminalColorScheme
{
    /// <summary>Dark terminal background.</summary>
    Dark,

    /// <summary>Light terminal background.</summary>
    Light,
}

/// <summary>Parsers for terminal background-color and color-scheme reports.</summary>
public static partial class TerminalColors
{
    private static readonly Regex _osc11Pattern = new(
        "^\\x1b\\]11;([^\\x07\\x1b]*)(?:\\x07|\\x1b\\\\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex _colorSchemePattern = new(
        "^(?:\\x1b\\[\\?997;(1|2)n)+$",
        RegexOptions.CultureInvariant);

    /// <summary>Checks whether data is a strict OSC 11 background-color response.</summary>
    public static bool IsOsc11BackgroundColorResponse(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _osc11Pattern.IsMatch(data);
    }

    /// <summary>Parses an OSC 11 response in six-/twelve-digit or rgb channel form.</summary>
    public static RgbColor? ParseOsc11BackgroundColor(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var match = _osc11Pattern.Match(data);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 6 && IsHex(hex))
            {
                return new RgbColor(
                    ParseHex(hex[..2]),
                    ParseHex(hex[2..4]),
                    ParseHex(hex[4..6]));
            }

            if (hex.Length == 12 && IsHex(hex))
            {
                return new RgbColor(
                    ParseOscHexChannel(hex[..4]),
                    ParseOscHexChannel(hex[4..8]),
                    ParseOscHexChannel(hex[8..12]));
            }

            return null;
        }

        var channelValue = Regex.Replace(value, "^rgba?:", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var channels = channelValue.Split('/');
        if (channels.Length < 3)
        {
            return null;
        }

        if (!TryParseOscHexChannel(channels[0], out var red) ||
            !TryParseOscHexChannel(channels[1], out var green) ||
            !TryParseOscHexChannel(channels[2], out var blue))
        {
            return null;
        }

        return new RgbColor(red, green, blue);
    }

    /// <summary>Parses a terminal color-scheme report, returning <see langword="null"/> when invalid.</summary>
    public static TerminalColorScheme? ParseTerminalColorSchemeReport(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var match = _colorSchemePattern.Match(data);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value == "2" ? TerminalColorScheme.Light : TerminalColorScheme.Dark;
    }

    private static bool TryParseOscHexChannel(string value, out int result)
    {
        if (!IsHex(value) || value.Length == 0)
        {
            result = 0;
            return false;
        }

        var max = Math.Pow(16, value.Length) - 1;
        result = (int)Math.Round(int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / max * 255, MidpointRounding.AwayFromZero);
        return true;
    }

    private static int ParseOscHexChannel(string value)
    {
        return TryParseOscHexChannel(value, out var result) ? result : 0;
    }

    private static int ParseHex(string value) => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static bool IsHex(string value) => value.Length > 0 && value.All(character => Uri.IsHexDigit(character));
}
