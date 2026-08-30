namespace Pi.Tui;

/// <summary>Zero-based hardware cursor location in the rendered line buffer.</summary>
public readonly record struct CursorPosition(int Row, int Column);

/// <summary>Options controlling one differential-render pass.</summary>
public sealed record DifferentialRenderOptions
{
    /// <summary>Requests a clear/full redraw even when dimensions are unchanged.</summary>
    public bool ForceFullRedraw { get; init; }

    /// <summary>Clears the screen when the rendered content becomes shorter.</summary>
    public bool ClearOnShrink { get; init; }

    /// <summary>Shows the hardware cursor when a cursor marker is present.</summary>
    public bool ShowHardwareCursor { get; init; }
}

/// <summary>Result metadata for one differential-render pass.</summary>
public sealed class DifferentialRenderResult
{
    internal DifferentialRenderResult(
        IReadOnlyList<string> lines,
        bool fullRedraw,
        int firstChangedLine,
        int lastChangedLine,
        CursorPosition? cursor,
        long outputCharacters)
    {
        Lines = lines;
        FullRedraw = fullRedraw;
        FirstChangedLine = firstChangedLine;
        LastChangedLine = lastChangedLine;
        Cursor = cursor;
        OutputCharacters = outputCharacters;
    }

    /// <summary>Lines after cursor-marker extraction and before terminal output.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Whether the pass repainted the complete rendered buffer.</summary>
    public bool FullRedraw { get; }

    /// <summary>First changed line, or -1 when the line buffer did not change.</summary>
    public int FirstChangedLine { get; }

    /// <summary>Last changed line, or -1 when the line buffer did not change.</summary>
    public int LastChangedLine { get; }

    /// <summary>Cursor location extracted from the cursor marker, if present.</summary>
    public CursorPosition? Cursor { get; }

    /// <summary>Total UTF-16 code units emitted by the pass.</summary>
    public long OutputCharacters { get; }
}

/// <summary>
/// Main-screen differential renderer for Pi's line-buffer component contract.
/// </summary>
public sealed class DifferentialRenderer
{
    private const string _beginSynchronizedOutput = "\x1b[?2026h";
    private const string _endSynchronizedOutput = "\x1b[?2026l";
    private const string _hideCursor = "\x1b[?25l";
    private const string _showCursor = "\x1b[?25h";

    private readonly Action<string> _write;
    private string[] _previousLines = [];
    private int _previousWidth;
    private int _previousHeight;
    private int _cursorRow;
    private int _hardwareCursorRow;
    private int _maxLinesRendered;
    private int _previousViewportTop;

    /// <summary>Initializes a renderer around a terminal write delegate.</summary>
    public DifferentialRenderer(Action<string> write)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <summary>Number of clear/full redraws emitted by this renderer.</summary>
    public int FullRedrawCount { get; private set; }

    /// <summary>Current line buffer retained for the next differential comparison.</summary>
    public IReadOnlyList<string> PreviousLines => _previousLines;

    /// <summary>Resets the retained terminal geometry and line buffer.</summary>
    public void Reset()
    {
        _previousLines = [];
        _previousWidth = -1;
        _previousHeight = -1;
        _cursorRow = 0;
        _hardwareCursorRow = 0;
        _maxLinesRendered = 0;
        _previousViewportTop = 0;
    }

    /// <summary>Renders a line buffer using the smallest safe terminal update.</summary>
    public DifferentialRenderResult Render(
        IReadOnlyList<string> lines,
        int width,
        int height,
        DifferentialRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        options ??= new DifferentialRenderOptions();

        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);
        var newLines = ExtractCursor(lines, safeHeight, out var cursor);
        var widthChanged = _previousWidth != 0 && _previousWidth != safeWidth;
        var heightChanged = _previousHeight != 0 && _previousHeight != safeHeight;
        var previousBufferLength = _previousHeight > 0
            ? _previousViewportTop + _previousHeight
            : safeHeight;
        var previousViewportTop = heightChanged
            ? Math.Max(0, previousBufferLength - safeHeight)
            : _previousViewportTop;
        var viewportTop = previousViewportTop;
        var hardwareCursorRow = _hardwareCursorRow;

        if (_previousLines.Length == 0 && !widthChanged && !heightChanged && !options.ForceFullRedraw)
        {
            return FullRender(newLines, safeWidth, safeHeight, cursor, clear: false, options.ShowHardwareCursor);
        }

        if (options.ForceFullRedraw || widthChanged || heightChanged ||
            (options.ClearOnShrink && newLines.Length < _maxLinesRendered))
        {
            return FullRender(newLines, safeWidth, safeHeight, cursor, clear: true, options.ShowHardwareCursor);
        }

        var (firstChanged, lastChanged) = FindChangedRange(_previousLines, newLines);
        var appendedLines = newLines.Length > _previousLines.Length;
        if (appendedLines)
        {
            if (firstChanged == -1)
            {
                firstChanged = _previousLines.Length;
            }

            lastChanged = newLines.Length - 1;
        }

        if (firstChanged == -1)
        {
            var output = new TerminalOutputWriter(_write);
            PositionHardwareCursor(output, cursor, newLines.Length, options.ShowHardwareCursor);
            output.Flush();
            _previousViewportTop = previousViewportTop;
            _previousHeight = safeHeight;
            return new DifferentialRenderResult(
                newLines,
                fullRedraw: false,
                firstChanged,
                lastChanged,
                cursor,
                output.Length);
        }

        var appendStart = appendedLines && firstChanged == _previousLines.Length && firstChanged > 0;
        if (firstChanged >= newLines.Length)
        {
            if (_previousLines.Length > newLines.Length)
            {
                var targetRow = Math.Max(0, newLines.Length - 1);
                var extraLines = _previousLines.Length - newLines.Length;
                if (targetRow < previousViewportTop || extraLines > safeHeight)
                {
                    return FullRender(newLines, safeWidth, safeHeight, cursor, clear: true, options.ShowHardwareCursor);
                }

                var output = new TerminalOutputWriter(_write);
                output.Append(_beginSynchronizedOutput);
                var lineDiff = ComputeLineDiff(targetRow, hardwareCursorRow, previousViewportTop, viewportTop);
                AppendVerticalMovement(output, lineDiff);
                output.Append("\r");

                var clearStartOffset = newLines.Length == 0 ? 0 : 1;
                if (clearStartOffset > 0)
                {
                    output.Append($"\x1b[{clearStartOffset}B");
                }

                for (var index = 0; index < extraLines; index++)
                {
                    output.Append("\r\x1b[2K");
                    if (index < extraLines - 1)
                    {
                        output.Append("\x1b[1B");
                    }
                }

                var moveBack = Math.Max(0, extraLines - 1 + clearStartOffset);
                if (moveBack > 0)
                {
                    output.Append($"\x1b[{moveBack}A");
                }

                output.Append(_endSynchronizedOutput);
                _cursorRow = targetRow;
                _hardwareCursorRow = targetRow;
                PositionHardwareCursor(output, cursor, newLines.Length, options.ShowHardwareCursor);
                output.Flush();
                UpdateState(newLines, safeWidth, safeHeight, previousViewportTop, _hardwareCursorRow);
                return new DifferentialRenderResult(
                    newLines,
                    fullRedraw: false,
                    firstChanged,
                    lastChanged,
                    cursor,
                    output.Length);
            }

            return new DifferentialRenderResult(newLines, false, firstChanged, lastChanged, cursor, 0);
        }

        if (firstChanged < previousViewportTop)
        {
            return FullRender(newLines, safeWidth, safeHeight, cursor, clear: true, options.ShowHardwareCursor);
        }

        var renderOutput = new TerminalOutputWriter(_write);
        renderOutput.Append(_beginSynchronizedOutput);
        var previousViewportBottom = previousViewportTop + safeHeight - 1;
        var moveTargetRow = appendStart ? firstChanged - 1 : firstChanged;
        if (moveTargetRow > previousViewportBottom)
        {
            var currentScreenRow = Math.Clamp(hardwareCursorRow - previousViewportTop, 0, safeHeight - 1);
            var moveToBottom = safeHeight - 1 - currentScreenRow;
            if (moveToBottom > 0)
            {
                renderOutput.Append($"\x1b[{moveToBottom}B");
            }

            var scroll = moveTargetRow - previousViewportBottom;
            renderOutput.Append(string.Concat(Enumerable.Repeat("\r\n", scroll)));
            previousViewportTop += scroll;
            viewportTop += scroll;
            hardwareCursorRow = moveTargetRow;
        }

        var movement = ComputeLineDiff(moveTargetRow, hardwareCursorRow, previousViewportTop, viewportTop);
        AppendVerticalMovement(renderOutput, movement);
        renderOutput.Append(appendStart ? "\r\n" : "\r");

        var renderEnd = Math.Min(lastChanged, newLines.Length - 1);
        for (var index = firstChanged; index <= renderEnd; index++)
        {
            if (index > firstChanged)
            {
                renderOutput.Append("\r\n");
            }

            renderOutput.Append("\x1b[2K");
            renderOutput.Append(newLines[index]);
        }

        var finalCursorRow = renderEnd;
        if (_previousLines.Length > newLines.Length)
        {
            if (renderEnd < newLines.Length - 1)
            {
                var moveDown = newLines.Length - 1 - renderEnd;
                renderOutput.Append($"\x1b[{moveDown}B");
                finalCursorRow = newLines.Length - 1;
            }

            var extraLines = _previousLines.Length - newLines.Length;
            for (var index = newLines.Length; index < _previousLines.Length; index++)
            {
                renderOutput.Append("\r\n\x1b[2K");
            }

            renderOutput.Append($"\x1b[{extraLines}A");
        }

        renderOutput.Append(_endSynchronizedOutput);
        _cursorRow = Math.Max(0, newLines.Length - 1);
        _hardwareCursorRow = finalCursorRow;
        _maxLinesRendered = Math.Max(_maxLinesRendered, newLines.Length);
        _previousViewportTop = Math.Max(previousViewportTop, finalCursorRow - safeHeight + 1);
        PositionHardwareCursor(renderOutput, cursor, newLines.Length, options.ShowHardwareCursor);
        renderOutput.Flush();
        UpdateState(newLines, safeWidth, safeHeight, _previousViewportTop, _hardwareCursorRow);
        return new DifferentialRenderResult(
            newLines,
            fullRedraw: false,
            firstChanged,
            lastChanged,
            cursor,
            renderOutput.Length);
    }

    private DifferentialRenderResult FullRender(
        string[] lines,
        int width,
        int height,
        CursorPosition? cursor,
        bool clear,
        bool showHardwareCursor)
    {
        FullRedrawCount++;
        var output = new TerminalOutputWriter(_write);
        output.Append(_beginSynchronizedOutput);
        if (clear)
        {
            output.Append("\x1b[2J\x1b[H\x1b[3J");
        }

        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                output.Append("\r\n");
            }

            output.Append(lines[index]);
        }

        output.Append(_endSynchronizedOutput);
        _cursorRow = Math.Max(0, lines.Length - 1);
        _hardwareCursorRow = _cursorRow;
        _maxLinesRendered = clear ? lines.Length : Math.Max(_maxLinesRendered, lines.Length);
        var viewportTop = Math.Max(0, Math.Max(height, lines.Length) - height);
        _previousViewportTop = viewportTop;
        PositionHardwareCursor(output, cursor, lines.Length, showHardwareCursor);
        output.Flush();
        UpdateState(lines, width, height, viewportTop, _hardwareCursorRow);
        return new DifferentialRenderResult(lines, true, -1, -1, cursor, output.Length);
    }

    private void UpdateState(string[] lines, int width, int height, int viewportTop, int hardwareCursorRow)
    {
        _previousLines = lines;
        _previousWidth = width;
        _previousHeight = height;
        _previousViewportTop = viewportTop;
        _hardwareCursorRow = hardwareCursorRow;
    }

    private void PositionHardwareCursor(
        TerminalOutputWriter output,
        CursorPosition? cursor,
        int totalLines,
        bool showHardwareCursor)
    {
        if (cursor is null || totalLines <= 0)
        {
            output.Append(_hideCursor);
            return;
        }

        var targetRow = Math.Clamp(cursor.Value.Row, 0, totalLines - 1);
        var targetColumn = Math.Max(0, cursor.Value.Column);
        var rowDelta = targetRow - _hardwareCursorRow;
        AppendVerticalMovement(output, rowDelta);
        output.Append($"\x1b[{targetColumn + 1}G");
        output.Append(showHardwareCursor ? _showCursor : _hideCursor);
        _hardwareCursorRow = targetRow;
    }

    private static int ComputeLineDiff(int targetRow, int hardwareCursorRow, int previousViewportTop, int viewportTop) =>
        (targetRow - viewportTop) - (hardwareCursorRow - previousViewportTop);

    private static void AppendVerticalMovement(TerminalOutputWriter output, int movement)
    {
        if (movement > 0)
        {
            output.Append($"\x1b[{movement}B");
        }
        else if (movement < 0)
        {
            output.Append($"\x1b[{-movement}A");
        }
    }

    private static (int First, int Last) FindChangedRange(
        string[] previous,
        string[] current)
    {
        var first = -1;
        var last = -1;
        var count = Math.Max(previous.Length, current.Length);
        for (var index = 0; index < count; index++)
        {
            var oldLine = index < previous.Length ? previous[index] : string.Empty;
            var newLine = index < current.Length ? current[index] : string.Empty;
            if (oldLine == newLine)
            {
                continue;
            }

            first = first == -1 ? index : first;
            last = index;
        }

        return (first, last);
    }

    private static string[] ExtractCursor(
        IReadOnlyList<string> source,
        int height,
        out CursorPosition? cursor)
    {
        var lines = source.ToArray();
        cursor = null;
        var viewportTop = Math.Max(0, lines.Length - height);
        for (var row = lines.Length - 1; row >= viewportTop; row--)
        {
            var line = lines[row];
            var markerIndex = line.IndexOf(TuiConstants.CursorMarker, StringComparison.Ordinal);
            if (markerIndex == -1)
            {
                continue;
            }

            var beforeMarker = line[..markerIndex];
            cursor = new CursorPosition(row, TextMeasurement.VisibleWidth(beforeMarker));
            lines[row] = string.Concat(
                line.AsSpan(0, markerIndex),
                line.AsSpan(markerIndex + TuiConstants.CursorMarker.Length));
            break;
        }

        return lines;
    }
}
