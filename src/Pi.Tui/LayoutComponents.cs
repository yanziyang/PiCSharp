namespace Pi.Tui;

/// <summary>Component that renders a configurable number of empty rows.</summary>
public sealed class Spacer : IComponent
{
    private int _lines;

    /// <summary>Initializes a spacer.</summary>
    public Spacer(int lines = 1) => _lines = lines;

    /// <summary>Changes the number of rows rendered by the spacer.</summary>
    public void SetLines(int lines) => _lines = lines;

    /// <inheritdoc />
    public void Invalidate() { }

    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width) =>
        Enumerable.Repeat(string.Empty, Math.Max(0, _lines)).ToArray();
}

/// <summary>Component that wraps text into padded terminal lines.</summary>
public sealed class Text : IComponent
{
    private string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _customBackground;
    private string? _cachedText;
    private int? _cachedWidth;
    private IReadOnlyList<string>? _cachedLines;

    /// <summary>Initializes a text component.</summary>
    public Text(
        string text = "",
        int paddingX = 1,
        int paddingY = 1,
        Func<string, string>? customBackground = null)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
        _customBackground = customBackground;
    }

    /// <summary>Replaces the text and invalidates its cached lines.</summary>
    public void SetText(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Invalidate();
    }

    /// <summary>Replaces the optional background styling callback.</summary>
    public void SetCustomBackground(Func<string, string>? customBackground)
    {
        _customBackground = customBackground;
        Invalidate();
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        if (_cachedLines is not null && _cachedText == _text && _cachedWidth == safeWidth)
        {
            return _cachedLines;
        }

        if (string.IsNullOrWhiteSpace(_text))
        {
            _cachedText = _text;
            _cachedWidth = safeWidth;
            _cachedLines = [];
            return _cachedLines;
        }

        var normalized = _text.Replace("\t", "   ", StringComparison.Ordinal);
        var paddingX = Math.Min(_paddingX, Math.Max(0, (safeWidth - 1) / 2));
        var contentWidth = Math.Max(1, safeWidth - paddingX * 2);
        var leftMargin = new string(' ', paddingX);
        var rightMargin = leftMargin;
        var contentLines = new List<string>();
        foreach (var wrapped in WrapText(normalized, contentWidth))
        {
            var line = leftMargin + wrapped + rightMargin;
            contentLines.Add(ApplyBackgroundOrPadding(line, safeWidth));
        }

        var emptyLine = new string(' ', safeWidth);
        var result = new List<string>();
        for (var index = 0; index < _paddingY; index++)
        {
            result.Add(ApplyBackgroundOrPadding(emptyLine, safeWidth));
        }

        result.AddRange(contentLines);
        for (var index = 0; index < _paddingY; index++)
        {
            result.Add(ApplyBackgroundOrPadding(emptyLine, safeWidth));
        }

        _cachedText = _text;
        _cachedWidth = safeWidth;
        _cachedLines = result;
        return result;
    }

    private string ApplyBackgroundOrPadding(string line, int width)
    {
        var padded = line + new string(' ', Math.Max(0, width - LineLayout.VisibleWidth(line)));
        return _customBackground is null ? padded : _customBackground(padded);
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        foreach (var logicalLine in text.Split('\n'))
        {
            if (logicalLine.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var remaining = logicalLine;
            while (remaining.Length > width)
            {
                var breakAt = remaining.LastIndexOf(' ', width - 1);
                if (breakAt <= 0)
                {
                    breakAt = width;
                }

                yield return remaining[..breakAt];
                remaining = remaining[breakAt..].TrimStart();
            }

            yield return remaining;
        }
    }
}

/// <summary>Component that applies padding and optional background styling to children.</summary>
public sealed class Box : Container
{
    private readonly int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _background;

    /// <summary>Initializes a box.</summary>
    public Box(int paddingX = 1, int paddingY = 1, Func<string, string>? background = null)
    {
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
        _background = background;
    }

    /// <summary>Changes the optional background styling callback.</summary>
    public void SetBackground(Func<string, string>? background)
    {
        _background = background;
        Invalidate();
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        if (Children.Count == 0)
        {
            return [];
        }

        var contentWidth = Math.Max(1, safeWidth - _paddingX * 2);
        var leftPadding = new string(' ', _paddingX);
        var childLines = Children
            .SelectMany(child => child.Render(contentWidth))
            .Select(line => leftPadding + line)
            .ToArray();
        if (childLines.Length == 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var index = 0; index < _paddingY; index++)
        {
            result.Add(ApplyBackground(string.Empty, safeWidth));
        }

        result.AddRange(childLines.Select(line => ApplyBackground(line, safeWidth)));
        for (var index = 0; index < _paddingY; index++)
        {
            result.Add(ApplyBackground(string.Empty, safeWidth));
        }

        return result;
    }

    private string ApplyBackground(string line, int width)
    {
        var padded = line + new string(' ', Math.Max(0, width - LineLayout.VisibleWidth(line)));
        return _background is null ? padded : _background(padded);
    }
}

/// <summary>Single-line text component that truncates to its viewport.</summary>
public sealed class TruncatedText : IComponent
{
    private readonly string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;

    /// <summary>Initializes a truncated text component.</summary>
    public TruncatedText(string text, int paddingX = 0, int paddingY = 0)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
    }

    /// <inheritdoc />
    public void Invalidate() { }

    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var emptyLine = new string(' ', safeWidth);
        var result = Enumerable.Repeat(emptyLine, _paddingY).ToList();
        var availableWidth = Math.Max(1, safeWidth - _paddingX * 2);
        var firstLine = _text.Split('\n')[0];
        var display = firstLine[..Math.Min(firstLine.Length, availableWidth)];
        var line = new string(' ', _paddingX) + display + new string(' ', _paddingX);
        result.Add(line + new string(' ', Math.Max(0, safeWidth - LineLayout.VisibleWidth(line))));
        result.AddRange(Enumerable.Repeat(emptyLine, _paddingY));
        return result;
    }
}
