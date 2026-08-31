using Pi.Tui;

namespace Pi.Tui;

/// <summary>Styling callbacks used by the image component fallback.</summary>
public sealed class ImageTheme
{
    /// <summary>Styles fallback text when the terminal has no image protocol.</summary>
    public required Func<string, string> FallbackColor { get; init; }
}

/// <summary>Optional sizing and fallback settings for an image component.</summary>
public sealed class ImageOptions
{
    /// <summary>Maximum image width in terminal cells.</summary>
    public int? MaxWidthCells { get; init; }

    /// <summary>Maximum image height in terminal cells.</summary>
    public int? MaxHeightCells { get; init; }

    /// <summary>Path displayed in fallback text and used for file hyperlinks.</summary>
    public string? Filename { get; init; }

    /// <summary>Kitty image ID to reuse for animation or updates.</summary>
    public uint? ImageId { get; init; }
}

/// <summary>Inline image component with Kitty, iTerm2, and text fallback rendering.</summary>
public class Image : IComponent
{
    private readonly string _base64Data;
    private readonly string _mimeType;
    private readonly ImageTheme _theme;
    private readonly ImageOptions _options;
    private readonly ITerminalImageSeam _imageSeam;
    private readonly ImageDimensions _dimensions;
    private uint? _imageId;
    private IReadOnlyList<string>? _cachedLines;
    private int? _cachedWidth;

    /// <summary>Creates an image component.</summary>
    public Image(
        string base64Data,
        string mimeType,
        ImageTheme theme,
        ImageOptions? options = null,
        ImageDimensions? dimensions = null,
        ITerminalImageSeam? imageSeam = null)
    {
        ArgumentNullException.ThrowIfNull(base64Data);
        ArgumentNullException.ThrowIfNull(mimeType);
        ArgumentNullException.ThrowIfNull(theme);
        _base64Data = base64Data;
        _mimeType = mimeType;
        _theme = theme;
        _options = options ?? new ImageOptions();
        _dimensions = dimensions ?? TerminalImage.GetImageDimensions(base64Data, mimeType) ?? new ImageDimensions(800, 600);
        _imageId = _options.ImageId;
        _imageSeam = imageSeam ?? TerminalImage.DefaultSeam;
    }

    /// <summary>Gets the Kitty image ID assigned to this component, when applicable.</summary>
    public uint? GetImageId() => _imageId;

    /// <inheritdoc />
    public void Invalidate()
    {
        _cachedLines = null;
        _cachedWidth = null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width)
    {
        if (_cachedLines is not null && _cachedWidth == width)
        {
            return _cachedLines;
        }

        var maxWidth = Math.Max(1, Math.Min(width - 2, _options.MaxWidthCells ?? 60));
        var cellDimensions = TerminalImage.GetCellDimensions(_imageSeam);
        var defaultMaxHeight = Math.Max(
            1,
            (int)Math.Ceiling((double)maxWidth * cellDimensions.WidthPx / cellDimensions.HeightPx));
        var maxHeight = _options.MaxHeightCells ?? defaultMaxHeight;
        var capabilities = TerminalImage.GetCapabilities(_imageSeam);
        IReadOnlyList<string> lines;

        if (capabilities.Images is not null)
        {
            if (capabilities.Images == ImageProtocol.Kitty && !_imageId.HasValue)
            {
                _imageId = TerminalImage.AllocateImageId();
            }

            var result = TerminalImage.RenderImage(
                _base64Data,
                _dimensions,
                new ImageRenderOptions
                {
                    MaxWidthCells = maxWidth,
                    MaxHeightCells = maxHeight,
                    ImageId = _imageId,
                    MoveCursor = false,
                },
                _imageSeam);

            if (result is not null)
            {
                if (result.ImageId.HasValue)
                {
                    _imageId = result.ImageId;
                }

                if (capabilities.Images == ImageProtocol.Kitty)
                {
                    var kittyLines = new List<string> { result.Sequence };
                    for (var index = 0; index < result.Rows - 1; index++)
                    {
                        kittyLines.Add(string.Empty);
                    }

                    lines = kittyLines;
                }
                else
                {
                    var itermLines = new List<string>();
                    for (var index = 0; index < result.Rows - 1; index++)
                    {
                        itermLines.Add(string.Empty);
                    }

                    var rowOffset = result.Rows - 1;
                    var moveUp = rowOffset > 0 ? $"\x1b[{rowOffset}A" : string.Empty;
                    itermLines.Add(moveUp + result.Sequence);
                    lines = itermLines;
                }
            }
            else
            {
                lines = [Fallback(width)];
            }
        }
        else
        {
            lines = [Fallback(width)];
        }

        _cachedLines = lines;
        _cachedWidth = width;
        return lines;
    }

    private string Fallback(int width)
    {
        var fallback = TerminalImage.ImageFallback(_mimeType, _dimensions, _options.Filename, _imageSeam);
        return TextMeasurement.TruncateToWidth(_theme.FallbackColor(fallback), width);
    }
}
