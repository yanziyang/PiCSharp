namespace Pi.Tui;

/// <summary>Inline-image protocol supported by a terminal.</summary>
public enum ImageProtocol
{
    /// <summary>Kitty graphics protocol.</summary>
    Kitty,

    /// <summary>iTerm2 inline-image protocol.</summary>
    Iterm2,
}

/// <summary>Terminal capabilities consumed by the TUI core.</summary>
public readonly record struct TerminalCapabilities(
    ImageProtocol? Images,
    bool TrueColor,
    bool Hyperlinks);

/// <summary>Measured terminal cell dimensions in pixels.</summary>
public readonly record struct CellDimensions(int WidthPx, int HeightPx);

/// <summary>
/// Narrow seam between the TUI core and terminal-image protocol support. T5.2b intentionally
/// supplies only the no-image implementation; protocol encoding and capability detection belong
/// to the later terminal-image port.
/// </summary>
public interface ITerminalImageSeam
{
    /// <summary>Returns the capabilities currently exposed to the TUI.</summary>
    TerminalCapabilities GetCapabilities();

    /// <summary>Stores the pixel dimensions reported for one terminal cell.</summary>
    void SetCellDimensions(CellDimensions dimensions);
}

/// <summary>Default T5.2b image seam with image support deliberately disabled.</summary>
public sealed class NoImageTerminalImageSeam : ITerminalImageSeam
{
    private readonly object _gate = new();
    private CellDimensions _cellDimensions = new(9, 18);

    /// <inheritdoc />
    public TerminalCapabilities GetCapabilities() => new(null, false, false);

    /// <inheritdoc />
    public void SetCellDimensions(CellDimensions dimensions)
    {
        if (dimensions.WidthPx <= 0 || dimensions.HeightPx <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _cellDimensions = dimensions;
        }
    }

    /// <summary>Returns the last valid cell dimensions supplied through the seam.</summary>
    public CellDimensions GetCellDimensions()
    {
        lock (_gate)
        {
            return _cellDimensions;
        }
    }
}

/// <summary>Image-escape helpers required by the TUI without implementing image protocols.</summary>
public static class TerminalImage
{
    /// <summary>Kitty graphics APC prefix.</summary>
    public const string KittyPrefix = "\x1b_G";

    /// <summary>iTerm2 inline-image OSC prefix.</summary>
    public const string Iterm2Prefix = "\x1b]1337;File=";

    /// <summary>
    /// Returns true when a line contains a Kitty or iTerm2 image escape, including when text or a
    /// cursor movement sequence precedes it.
    /// </summary>
    public static bool IsImageLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.StartsWith(KittyPrefix, StringComparison.Ordinal) ||
               line.StartsWith(Iterm2Prefix, StringComparison.Ordinal) ||
               line.Contains(KittyPrefix, StringComparison.Ordinal) ||
               line.Contains(Iterm2Prefix, StringComparison.Ordinal);
    }
}
