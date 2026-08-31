using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Pixel dimensions of an image.</summary>
public readonly record struct ImageDimensions(int WidthPx, int HeightPx);

/// <summary>Options controlling Kitty or iTerm2 image encoding.</summary>
public sealed class ImageRenderOptions
{
    /// <summary>Maximum rendered width in terminal cells.</summary>
    public int? MaxWidthCells { get; init; }

    /// <summary>Maximum rendered height in terminal cells.</summary>
    public int? MaxHeightCells { get; init; }

    /// <summary>Whether iTerm2 should preserve the image aspect ratio.</summary>
    public bool? PreserveAspectRatio { get; init; }

    /// <summary>Kitty image ID to reuse or replace.</summary>
    public uint? ImageId { get; init; }

    /// <summary>Whether Kitty should perform its default cursor movement after placement.</summary>
    public bool? MoveCursor { get; init; }
}

/// <summary>Terminal-cell dimensions occupied by a rendered image.</summary>
public readonly record struct ImageCellSize(int Columns, int Rows);

/// <summary>Registered Kitty image metadata used for placement bookkeeping.</summary>
public readonly record struct KittyImageMetadata(
    uint ImageId,
    int Columns,
    int Rows,
    int WidthPx,
    int HeightPx);

/// <summary>Placement-only Kitty sequence derived from an uploaded image line.</summary>
public sealed record KittyImagePlacement
{
    /// <summary>Registered image identifier.</summary>
    public required uint ImageId { get; init; }

    /// <summary>Transmission generation that uploaded the image.</summary>
    public required long TransmissionGeneration { get; init; }

    /// <summary>UTF-16 length of the complete Kitty transmission.</summary>
    public required int TransmissionBytes { get; init; }

    /// <summary>Estimated decoded RGBA storage size.</summary>
    public required long EstimatedDecodedBytes { get; init; }

    /// <summary>Placement-only Kitty sequence.</summary>
    public required string Sequence { get; init; }

    /// <summary>Original line with the transmission replaced by <see cref="Sequence" />.</summary>
    public required string ReplacementLine { get; init; }
}

/// <summary>
/// Real terminal-image capability seam. Capability and cell-size state belongs to this instance,
/// allowing each TUI to be configured independently while preserving deterministic test overrides.
/// </summary>
public sealed class TerminalImageSeam : ITerminalImageSeam
{
    private readonly object _gate = new();
    private CellDimensions _cellDimensions = new(9, 18);
    private TerminalCapabilities? _capabilities;
    private CapabilityOverrideState _overrides;

    /// <inheritdoc />
    public TerminalCapabilities GetCapabilities()
    {
        lock (_gate)
        {
            if (_capabilities is null)
            {
                _capabilities = TerminalImage.DetectCapabilities(
                    _overrides.Hyperlinks.HasValue ? () => _overrides.Hyperlinks.Value : null);
                _capabilities = ApplyOverrides(_capabilities.Value, _overrides);
            }

            return _capabilities.Value;
        }
    }

    /// <inheritdoc />
    public void SetCellDimensions(CellDimensions dimensions)
    {
        lock (_gate)
        {
            _cellDimensions = dimensions;
        }
    }

    /// <inheritdoc />
    public CellDimensions GetCellDimensions()
    {
        lock (_gate)
        {
            return _cellDimensions;
        }
    }

    /// <inheritdoc />
    public void ResetCapabilitiesCache()
    {
        lock (_gate)
        {
            _capabilities = null;
        }
    }

    /// <inheritdoc />
    public void SetCapabilities(TerminalCapabilities capabilities)
    {
        lock (_gate)
        {
            _capabilities = capabilities;
        }
    }

    /// <summary>Overrides selected detected capabilities and invalidates the cache.</summary>
    /// <param name="overrides">Keys are <c>images</c>, <c>trueColor</c>, and <c>hyperlinks</c>.</param>
    public void SetCapabilityOverrides(IReadOnlyDictionary<string, object?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var next = CapabilityOverrideState.From(overrides);
        lock (_gate)
        {
            if (_overrides == next)
            {
                return;
            }

            _overrides = next;
            _capabilities = null;
        }
    }

    private static TerminalCapabilities ApplyOverrides(
        TerminalCapabilities detected,
        CapabilityOverrideState overrides) =>
        new(
            overrides.ImagesSpecified ? overrides.Images : detected.Images,
            overrides.TrueColor ?? detected.TrueColor,
            overrides.Hyperlinks ?? detected.Hyperlinks);

    private readonly record struct CapabilityOverrideState(
        bool ImagesSpecified,
        ImageProtocol? Images,
        bool? TrueColor,
        bool? Hyperlinks)
    {
        public static CapabilityOverrideState From(IReadOnlyDictionary<string, object?> values)
        {
            var imagesSpecified = values.ContainsKey("images");
            ImageProtocol? images = null;
            if (imagesSpecified && values["images"] is { } rawImages)
            {
                images = ParseImageProtocol(rawImages);
            }

            return new(
                imagesSpecified,
                images,
                ParseNullableBoolean(values, "trueColor"),
                ParseNullableBoolean(values, "hyperlinks"));
        }

        private static ImageProtocol ParseImageProtocol(object value) => value switch
        {
            ImageProtocol protocol => protocol,
            string text when text.Equals("kitty", StringComparison.OrdinalIgnoreCase) => ImageProtocol.Kitty,
            string text when text.Equals("iterm2", StringComparison.OrdinalIgnoreCase) => ImageProtocol.Iterm2,
            _ => throw new ArgumentException("images override must be kitty, iterm2, or null.", nameof(value)),
        };

        private static bool? ParseNullableBoolean(IReadOnlyDictionary<string, object?> values, string key)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            return value switch
            {
                bool boolean => boolean,
                string text when text == "1" => true,
                string text when text == "0" => false,
                _ => throw new ArgumentException($"{key} override must be a Boolean, 1, 0, or null.", nameof(values)),
            };
        }
    }
}

/// <summary>Terminal-image protocol encoders, capability detection, and format probes.</summary>
public static partial class TerminalImage
{
    private const int _kittyChunkSize = 4096;
    private const string _kittyPrefix = "\x1b_G";
    private const string _iterm2Prefix = "\x1b]1337;File=";
    private static readonly object _metadataGate = new();
    private static readonly Dictionary<uint, RegisteredKittyImageMetadata> _kittyImageMetadata = [];
    private static readonly Queue<uint> _kittyMetadataOrder = [];
    private static long _kittyTransmissionGeneration;
    private static readonly TerminalImageSeam _defaultSeam = new();
    private static readonly HashSet<string> _kittyPlacementControlKeys = new(StringComparer.Ordinal)
    {
        "i", "p", "x", "y", "w", "h", "X", "Y", "c", "r", "C", "U", "z", "P", "Q", "H", "V",
    };

    private static readonly Regex _kittyControlRegex = new(
        "\\x1b_G([^;]*);",
        RegexOptions.CultureInvariant);

    /// <summary>Gets the default real seam used by standalone image components.</summary>
    internal static ITerminalImageSeam DefaultSeam => _defaultSeam;

    /// <summary>Returns the current cell dimensions from the selected seam.</summary>
    public static CellDimensions GetCellDimensions(ITerminalImageSeam? seam = null) =>
        (seam ?? _defaultSeam).GetCellDimensions();

    /// <summary>Stores cell dimensions on the selected seam.</summary>
    public static void SetCellDimensions(CellDimensions dimensions, ITerminalImageSeam? seam = null) =>
        (seam ?? _defaultSeam).SetCellDimensions(dimensions);

    /// <summary>Returns detected capabilities from the selected seam.</summary>
    public static TerminalCapabilities GetCapabilities(ITerminalImageSeam? seam = null) =>
        (seam ?? _defaultSeam).GetCapabilities();

    /// <summary>Clears the selected seam's capability cache.</summary>
    public static void ResetCapabilitiesCache(ITerminalImageSeam? seam = null) =>
        (seam ?? _defaultSeam).ResetCapabilitiesCache();

    /// <summary>Overrides the selected seam's cached capabilities.</summary>
    public static void SetCapabilities(TerminalCapabilities capabilities, ITerminalImageSeam? seam = null) =>
        (seam ?? _defaultSeam).SetCapabilities(capabilities);

    /// <summary>Overrides selected detected capabilities on the selected real seam.</summary>
    public static void SetCapabilityOverrides(
        IReadOnlyDictionary<string, object?> overrides,
        ITerminalImageSeam? seam = null)
    {
        if (seam is TerminalImageSeam realSeam)
        {
            realSeam.SetCapabilityOverrides(overrides);
            return;
        }

        if (seam is null)
        {
            _defaultSeam.SetCapabilityOverrides(overrides);
            return;
        }

        throw new ArgumentException("Capability overrides require a TerminalImageSeam instance.", nameof(seam));
    }

    /// <summary>Detects terminal capabilities from environment variables.</summary>
    public static TerminalCapabilities DetectCapabilities(Func<bool>? tmuxForwardsHyperlink = null)
    {
        var hyperlinkOverride = ParseBooleanCapabilityOverride(Environment.GetEnvironmentVariable("PI_HYPERLINKS"));
        var detected = DetectCapabilitiesFromEnvironment(
            hyperlinkOverride.HasValue ? () => hyperlinkOverride.Value : tmuxForwardsHyperlink ?? ProbeTmuxHyperlinks);
        var imageProtocol = Environment.GetEnvironmentVariable("PI_IMAGE_PROTOCOL")?.ToLowerInvariant();
        var images = imageProtocol switch
        {
            "kitty" => (ImageProtocol?)ImageProtocol.Kitty,
            "iterm2" => ImageProtocol.Iterm2,
            "none" or "0" => null,
            _ => detected.Images,
        };
        var trueColorOverride = ParseBooleanCapabilityOverride(Environment.GetEnvironmentVariable("PI_TRUE_COLOR"));
        return new(
            images,
            trueColorOverride ?? detected.TrueColor,
            hyperlinkOverride ?? detected.Hyperlinks);
    }

    private static TerminalCapabilities DetectCapabilitiesFromEnvironment(Func<bool> tmuxForwardsHyperlink)
    {
        var termProgram = (Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? string.Empty).ToLowerInvariant();
        var terminalEmulator = (Environment.GetEnvironmentVariable("TERMINAL_EMULATOR") ?? string.Empty).ToLowerInvariant();
        var term = (Environment.GetEnvironmentVariable("TERM") ?? string.Empty).ToLowerInvariant();
        var colorTerm = (Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty).ToLowerInvariant();
        var hasTrueColorHint = colorTerm is "truecolor" or "24bit";

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX")) || term.StartsWith("tmux", StringComparison.Ordinal))
        {
            return new(null, hasTrueColorHint, tmuxForwardsHyperlink());
        }

        if (term.StartsWith("screen", StringComparison.Ordinal))
        {
            return new(null, hasTrueColorHint, false);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")) || termProgram == "kitty")
        {
            return new(ImageProtocol.Kitty, true, true);
        }

        if (termProgram == "ghostty" || term.Contains("ghostty", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")))
        {
            return new(ImageProtocol.Kitty, true, true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEZTERM_PANE")) || termProgram == "wezterm")
        {
            return new(ImageProtocol.Kitty, true, true);
        }

        if (termProgram == "warpterminal" ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WARP_SESSION_ID")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WARP_TERMINAL_SESSION_UUID")))
        {
            return new(ImageProtocol.Kitty, true, true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ITERM_SESSION_ID")) || termProgram == "iterm.app")
        {
            return new(ImageProtocol.Iterm2, true, true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
        {
            return new(null, true, true);
        }

        if (termProgram == "vscode" || termProgram == "alacritty")
        {
            return new(null, true, true);
        }

        if (terminalEmulator == "jetbrains-jediterm")
        {
            return new(null, true, false);
        }

        if (OperatingSystem.IsWindows())
        {
            return new(null, true, false);
        }

        return new(null, hasTrueColorHint, false);
    }

    private static bool? ParseBooleanCapabilityOverride(string? value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    private static bool ProbeTmuxHyperlinks()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tmux",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("display-message");
            process.StartInfo.ArgumentList.Add("-p");
            process.StartInfo.ArgumentList.Add("#{client_termfeatures}");
            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit(250))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(250);
                }
                catch
                {
                    // A failed cleanup still means the probe did not complete in time.
                }

                return false;
            }

            var termFeatures = process.StandardOutput.ReadToEnd();
            return termFeatures
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static feature => feature.Trim())
                .Contains("hyperlinks", StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Allocates a random nonzero Kitty image identifier.</summary>
    public static uint AllocateImageId()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value % 0xfffffffeu + 1;
    }

    /// <summary>Encodes base64 data using Kitty's graphics protocol.</summary>
    public static string EncodeKitty(string base64Data, KittyEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(base64Data);
        options ??= new KittyEncodeOptions();
        var parameters = new List<string> { "a=T", "f=100", "q=2" };
        if (options.MoveCursor == false) parameters.Add("C=1");
        if (options.Columns is { } columns && columns != 0)
        {
            parameters.Add($"c={columns.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.Rows is { } rows && rows != 0)
        {
            parameters.Add($"r={rows.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.ImageId is { } imageId && imageId != 0)
        {
            parameters.Add($"i={imageId.ToString(CultureInfo.InvariantCulture)}");
        }

        if (base64Data.Length <= _kittyChunkSize)
        {
            return $"{_kittyPrefix}{string.Join(',', parameters)};{base64Data}\x1b\\";
        }

        var chunks = new StringBuilder();
        var offset = 0;
        var first = true;
        while (offset < base64Data.Length)
        {
            var chunkLength = Math.Min(_kittyChunkSize, base64Data.Length - offset);
            var chunk = base64Data.Substring(offset, chunkLength);
            var isLast = offset + chunkLength >= base64Data.Length;
            if (first)
            {
                chunks.Append(_kittyPrefix).Append(string.Join(',', parameters)).Append(",m=1;").Append(chunk).Append("\x1b\\");
                first = false;
            }
            else if (isLast)
            {
                chunks.Append(_kittyPrefix).Append("m=0;").Append(chunk).Append("\x1b\\");
            }
            else
            {
                chunks.Append(_kittyPrefix).Append("m=1;").Append(chunk).Append("\x1b\\");
            }

            offset += chunkLength;
        }

        return chunks.ToString();
    }

    /// <summary>Convenience overload for Kitty encoding options.</summary>
    public static string EncodeKitty(
        string base64Data,
        int? columns = null,
        int? rows = null,
        uint? imageId = null,
        bool? moveCursor = null) =>
        EncodeKitty(base64Data, new KittyEncodeOptions
        {
            Columns = columns,
            Rows = rows,
            ImageId = imageId,
            MoveCursor = moveCursor,
        });

    /// <summary>Deletes every visible Kitty image and its uploaded data.</summary>
    public static string DeleteAllKittyImages() => "\x1b_Ga=d,d=A,q=2\x1b\\";

    /// <summary>Deletes every visible Kitty placement while retaining uploaded data.</summary>
    public static string DeleteAllKittyPlacements() => "\x1b_Ga=d,d=a,q=2\x1b\\";

    /// <summary>Encodes base64 data using iTerm2's inline-image protocol.</summary>
    public static string EncodeITerm2(string base64Data, Iterm2EncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(base64Data);
        options ??= new Iterm2EncodeOptions();
        var parameters = new List<string>
        {
            $"inline={(options.Inline == false ? 0 : 1)}",
            $"size={DecodedBase64Length(base64Data).ToString(CultureInfo.InvariantCulture)}",
        };
        if (options.Width is not null)
        {
            parameters.Add($"width={FormatProtocolValue(options.Width)}");
        }

        if (options.Height is not null)
        {
            parameters.Add($"height={FormatProtocolValue(options.Height)}");
        }
        if (!string.IsNullOrEmpty(options.Name))
        {
            parameters.Add($"name={Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Name))}");
        }

        if (options.PreserveAspectRatio == false) parameters.Add("preserveAspectRatio=0");
        return $"{_iterm2Prefix}{string.Join(';', parameters)}:{base64Data}\x07";
    }

    /// <summary>Convenience overload for iTerm2 encoding options.</summary>
    public static string EncodeITerm2(
        string base64Data,
        object? width = null,
        object? height = null,
        string? name = null,
        bool? preserveAspectRatio = null,
        bool? inline = null) =>
        EncodeITerm2(base64Data, new Iterm2EncodeOptions
        {
            Width = width,
            Height = height,
            Name = name,
            PreserveAspectRatio = preserveAspectRatio,
            Inline = inline,
        });

    private static int DecodedBase64Length(string base64Data)
    {
        try
        {
            return Convert.FromBase64String(base64Data).Length;
        }
        catch (FormatException)
        {
            var padding = base64Data.EndsWith("==", StringComparison.Ordinal) ? 2 :
                base64Data.EndsWith('=') ? 1 : 0;
            return Math.Max(0, (base64Data.Length * 3 / 4) - padding);
        }
    }

    private static string FormatProtocolValue(object value) => value switch
    {
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Registers metadata for a Kitty transmission.</summary>
    public static void RegisterKittyImageMetadata(KittyImageMetadata metadata)
    {
        lock (_metadataGate)
        {
            _kittyTransmissionGeneration++;
            _kittyImageMetadata.Remove(metadata.ImageId);
            var retainedMetadataIds = _kittyMetadataOrder.Where(id => id != metadata.ImageId).ToArray();
            _kittyMetadataOrder.Clear();
            foreach (var retainedMetadataId in retainedMetadataIds)
            {
                _kittyMetadataOrder.Enqueue(retainedMetadataId);
            }
            _kittyMetadataOrder.Enqueue(metadata.ImageId);
            _kittyImageMetadata[metadata.ImageId] = new RegisteredKittyImageMetadata(
                metadata,
                _kittyTransmissionGeneration);
            while (_kittyImageMetadata.Count > 1000 && _kittyMetadataOrder.Count > 0)
            {
                var oldest = _kittyMetadataOrder.Dequeue();
                _kittyImageMetadata.Remove(oldest);
            }
        }
    }

    /// <summary>Returns registered metadata for the first Kitty transmission in a line.</summary>
    public static KittyImageMetadata? GetKittyImageMetadata(string line)
    {
        var registered = GetRegisteredKittyImageMetadata(line);
        return registered?.Metadata;
    }

    /// <summary>Builds a placement-only Kitty command from an uploaded image line.</summary>
    public static KittyImagePlacement? GetKittyImagePlacement(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = _kittyControlRegex.Match(line);
        var metadata = GetRegisteredKittyImageMetadata(line);
        if (!match.Success || metadata is null)
        {
            return null;
        }

        var commandStart = match.Index;
        var commandControls = match.Groups[1].Value;
        var firstCommandControls = commandControls;
        var transmissionEnd = 0;
        while (true)
        {
            var terminator = line.IndexOf("\x1b\\", commandStart + _kittyPrefix.Length, StringComparison.Ordinal);
            if (terminator < 0)
            {
                return null;
            }

            transmissionEnd = terminator + 2;
            if (!ContainsControl(commandControls, "m", "1"))
            {
                break;
            }

            commandStart = transmissionEnd;
            if (!line.AsSpan(commandStart).StartsWith(_kittyPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var controlsStart = commandStart + _kittyPrefix.Length;
            var controlsEnd = line.IndexOf(';', controlsStart);
            if (controlsEnd < 0)
            {
                return null;
            }

            commandControls = line[controlsStart..controlsEnd];
        }

        var controls = firstCommandControls
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(control => _kittyPlacementControlKeys.Contains(ControlKey(control)))
            .ToArray();
        var sequence = $"{_kittyPrefix}a=p,q=2,{string.Join(',', controls)}\x1b\\";
        return new KittyImagePlacement
        {
            ImageId = metadata.Value.Metadata.ImageId,
            TransmissionGeneration = metadata.Value.TransmissionGeneration,
            TransmissionBytes = transmissionEnd - match.Index,
            EstimatedDecodedBytes = (long)metadata.Value.Metadata.WidthPx * metadata.Value.Metadata.HeightPx * 4,
            Sequence = sequence,
            ReplacementLine = line[..match.Index] + sequence + line[transmissionEnd..],
        };
    }

    /// <summary>Crops a Kitty image line to a visible source-row range.</summary>
    public static string CropKittyImageLine(string line, int hiddenRows, int visibleRows)
    {
        ArgumentNullException.ThrowIfNull(line);
        var metadata = GetKittyImageMetadata(line);
        var match = _kittyControlRegex.Match(line);
        if (metadata is null || !match.Success || hiddenRows < 0 || hiddenRows >= metadata.Value.Rows || visibleRows <= 0)
        {
            return line;
        }

        var croppedRows = Math.Min(visibleRows, metadata.Value.Rows - hiddenRows);
        if (hiddenRows == 0 && croppedRows == metadata.Value.Rows)
        {
            return line;
        }

        var sourceY = (int)Math.Floor((double)metadata.Value.HeightPx * hiddenRows / metadata.Value.Rows);
        var sourceEnd = (int)Math.Ceiling((double)metadata.Value.HeightPx * (hiddenRows + croppedRows) / metadata.Value.Rows);
        var sourceHeight = Math.Max(1, Math.Min(metadata.Value.HeightPx, sourceEnd) - sourceY);
        var controls = match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(control => !Regex.IsMatch(control, "^[yhr]=", RegexOptions.CultureInvariant))
            .ToList();
        controls.Add($"y={sourceY}");
        controls.Add($"h={sourceHeight}");
        controls.Add($"r={croppedRows}");
        return line[..match.Index] + _kittyPrefix + string.Join(',', controls) + ";" +
            line[(match.Index + match.Length)..];
    }

    /// <summary>Calculates terminal-cell size for an image while preserving aspect ratio.</summary>
    public static ImageCellSize CalculateImageCellSize(
        ImageDimensions imageDimensions,
        int maxWidthCells,
        int? maxHeightCells = null,
        CellDimensions? cellDimensions = null)
    {
        var cells = cellDimensions ?? new CellDimensions(9, 18);
        var maxWidth = Math.Max(1, maxWidthCells);
        var maxHeight = maxHeightCells.HasValue ? Math.Max(1, maxHeightCells.Value) : (int?)null;
        var imageWidth = Math.Max(1, imageDimensions.WidthPx);
        var imageHeight = Math.Max(1, imageDimensions.HeightPx);
        var widthScale = (double)maxWidth * cells.WidthPx / imageWidth;
        var heightScale = maxHeight is null ? widthScale : (double)maxHeight.Value * cells.HeightPx / imageHeight;
        var scale = Math.Min(widthScale, heightScale);
        var scaledWidthPx = imageWidth * scale;
        var scaledHeightPx = imageHeight * scale;
        var columns = (int)Math.Ceiling(scaledWidthPx / cells.WidthPx);
        var rows = (int)Math.Ceiling(scaledHeightPx / cells.HeightPx);
        return new(
            Math.Max(1, Math.Min(maxWidth, columns)),
            Math.Max(1, maxHeight is null ? rows : Math.Min(maxHeight.Value, rows)));
    }

    /// <summary>Calculates the number of rows for an image at a target width.</summary>
    public static int CalculateImageRows(
        ImageDimensions imageDimensions,
        int targetWidthCells,
        CellDimensions? cellDimensions = null) =>
        CalculateImageCellSize(imageDimensions, targetWidthCells, null, cellDimensions).Rows;

    /// <summary>Returns PNG dimensions from a base64-encoded PNG header.</summary>
    public static ImageDimensions? GetPngDimensions(string base64Data)
    {
        if (!TryDecodeBase64(base64Data, 24, out var buffer) ||
            buffer[0] != 0x89 || buffer[1] != 0x50 || buffer[2] != 0x4e || buffer[3] != 0x47)
        {
            return null;
        }

        try
        {
            return new(
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4))),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(20, 4))));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Returns JPEG dimensions from a base64-encoded JPEG header.</summary>
    public static ImageDimensions? GetJpegDimensions(string base64Data)
    {
        if (!TryDecodeBase64(base64Data, 2, out var buffer) || buffer[0] != 0xff || buffer[1] != 0xd8)
        {
            return null;
        }

        var offset = 2;
        while (offset < buffer.Length - 9)
        {
            if (buffer[offset] != 0xff)
            {
                offset++;
                continue;
            }

            var marker = buffer[offset + 1];
            if (marker is >= 0xc0 and <= 0xc2)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 7, 2));
                return new(width, height);
            }

            if (offset + 3 >= buffer.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
            if (length < 2)
            {
                return null;
            }

            offset += 2 + length;
        }

        return null;
    }

    /// <summary>Returns GIF dimensions from a base64-encoded GIF header.</summary>
    public static ImageDimensions? GetGifDimensions(string base64Data)
    {
        if (!TryDecodeBase64(base64Data, 10, out var buffer))
        {
            return null;
        }

        var signature = Encoding.ASCII.GetString(buffer, 0, 6);
        if (signature is not ("GIF87a" or "GIF89a"))
        {
            return null;
        }

        return new(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8, 2)));
    }

    /// <summary>Returns WebP dimensions from a base64-encoded WebP header.</summary>
    public static ImageDimensions? GetWebpDimensions(string base64Data)
    {
        if (!TryDecodeBase64(base64Data, 30, out var buffer) ||
            Encoding.ASCII.GetString(buffer, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(buffer, 8, 4) != "WEBP")
        {
            return null;
        }

        var chunk = Encoding.ASCII.GetString(buffer, 12, 4);
        if (chunk == "VP8 ")
        {
            if (buffer.Length < 30) return null;
            var width = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(26, 2)) & 0x3fff;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28, 2)) & 0x3fff;
            return new(width, height);
        }

        if (chunk == "VP8L")
        {
            if (buffer.Length < 25) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(21, 4));
            return new((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }

        if (chunk == "VP8X")
        {
            if (buffer.Length < 30) return null;
            var width = buffer[24] | buffer[25] << 8 | buffer[26] << 16;
            var height = buffer[27] | buffer[28] << 8 | buffer[29] << 16;
            return new(width + 1, height + 1);
        }

        return null;
    }

    /// <summary>Probes the image dimensions for a supported MIME type.</summary>
    public static ImageDimensions? GetImageDimensions(string base64Data, string mimeType) => mimeType switch
    {
        "image/png" => GetPngDimensions(base64Data),
        "image/jpeg" => GetJpegDimensions(base64Data),
        "image/gif" => GetGifDimensions(base64Data),
        "image/webp" => GetWebpDimensions(base64Data),
        _ => null,
    };

    /// <summary>Renders an image sequence using the selected seam's capabilities.</summary>
    public static ImageRenderResult? RenderImage(
        string base64Data,
        ImageDimensions imageDimensions,
        ImageRenderOptions? options = null,
        ITerminalImageSeam? seam = null)
    {
        ArgumentNullException.ThrowIfNull(base64Data);
        options ??= new ImageRenderOptions();
        var caps = GetCapabilities(seam);
        if (caps.Images is null)
        {
            return null;
        }

        var maxWidth = options.MaxWidthCells ?? 80;
        var size = CalculateImageCellSize(
            imageDimensions,
            maxWidth,
            options.MaxHeightCells,
            GetCellDimensions(seam));
        if (caps.Images == ImageProtocol.Kitty)
        {
            if (options.ImageId.HasValue)
            {
                RegisterKittyImageMetadata(new KittyImageMetadata(
                    options.ImageId.Value,
                    size.Columns,
                    size.Rows,
                    imageDimensions.WidthPx,
                    imageDimensions.HeightPx));
            }

            var sequence = EncodeKitty(base64Data, new KittyEncodeOptions
            {
                Columns = size.Columns,
                Rows = size.Rows,
                ImageId = options.ImageId,
                MoveCursor = options.MoveCursor,
            });
            return new(sequence, size.Columns, size.Rows, options.ImageId);
        }

        if (caps.Images == ImageProtocol.Iterm2)
        {
            var sequence = EncodeITerm2(base64Data, new Iterm2EncodeOptions
            {
                Width = size.Columns,
                Height = "auto",
                PreserveAspectRatio = options.PreserveAspectRatio ?? true,
            });
            return new(sequence, size.Columns, size.Rows, null);
        }

        return null;
    }

    /// <summary>Wraps visible text in an OSC 8 hyperlink sequence.</summary>
    public static string Hyperlink(string text, string url) =>
        $"\x1b]8;;{url}\x1b\\{text}\x1b]8;;\x1b\\";

    /// <summary>Returns fallback text for terminals without inline-image support.</summary>
    public static string ImageFallback(
        string mimeType,
        ImageDimensions? dimensions = null,
        string? filename = null,
        ITerminalImageSeam? seam = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(filename))
        {
            var display = ShortenImagePath(filename);
            if (GetCapabilities(seam).Hyperlinks && Path.IsPathFullyQualified(filename))
            {
                parts.Add(Hyperlink(display, new Uri(Path.GetFullPath(filename)).AbsoluteUri));
            }
            else
            {
                parts.Add(display);
            }
        }

        parts.Add($"[{mimeType}]");
        if (dimensions.HasValue)
        {
            parts.Add($"{dimensions.Value.WidthPx}x{dimensions.Value.HeightPx}");
        }

        return $"[Image: {string.Join(' ', parts)}]";
    }

    private static string ShortenImagePath(string filename)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) &&
            (filename == home || filename.StartsWith(home + "/", StringComparison.Ordinal) ||
             filename.StartsWith(home + "\\", StringComparison.Ordinal)))
        {
            return "~" + filename[home.Length..];
        }

        return filename;
    }

    private static RegisteredKittyImageMetadata? GetRegisteredKittyImageMetadata(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = _kittyControlRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var imageId = ParseControlValue(match.Groups[1].Value, "i");
        if (!uint.TryParse(imageId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId))
        {
            return null;
        }

        lock (_metadataGate)
        {
            return _kittyImageMetadata.TryGetValue(parsedId, out var metadata) ? metadata : null;
        }
    }

    private static bool ContainsControl(string controls, string key, string value) =>
        controls.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(control =>
            {
                var pair = control.Split('=', 2);
                return pair.Length == 2 && pair[0] == key && pair[1] == value;
            });

    private static string? ParseControlValue(string controls, string key) =>
        controls.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(control => control.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == key)
            .Select(pair => pair[1])
            .FirstOrDefault();

    private static string ControlKey(string control) => control.Split('=', 2)[0];

    private static bool TryDecodeBase64(string base64Data, int minimumLength, out byte[] buffer)
    {
        try
        {
            buffer = Convert.FromBase64String(base64Data);
            return buffer.Length >= minimumLength;
        }
        catch (FormatException)
        {
            buffer = [];
            return false;
        }
    }

    private readonly record struct RegisteredKittyImageMetadata(
        KittyImageMetadata Metadata,
        long TransmissionGeneration);
}

/// <summary>Options for Kitty base64 transmission.</summary>
public sealed class KittyEncodeOptions
{
    /// <summary>Image width in terminal cells.</summary>
    public int? Columns { get; init; }

    /// <summary>Image height in terminal cells.</summary>
    public int? Rows { get; init; }

    /// <summary>Optional image identifier.</summary>
    public uint? ImageId { get; init; }

    /// <summary>Whether the terminal moves its cursor after placement.</summary>
    public bool? MoveCursor { get; init; }
}

/// <summary>Options for iTerm2 base64 transmission.</summary>
public sealed class Iterm2EncodeOptions
{
    /// <summary>Width metadata as a cell count or protocol string.</summary>
    public object? Width { get; init; }

    /// <summary>Height metadata as a cell count or protocol string.</summary>
    public object? Height { get; init; }

    /// <summary>Optional display name.</summary>
    public string? Name { get; init; }

    /// <summary>Whether iTerm2 preserves aspect ratio.</summary>
    public bool? PreserveAspectRatio { get; init; }

    /// <summary>Whether the image is inline.</summary>
    public bool? Inline { get; init; }
}

/// <summary>Result returned when an image sequence is rendered.</summary>
public sealed record ImageRenderResult(
    string Sequence,
    int Columns,
    int Rows,
    uint? ImageId);
