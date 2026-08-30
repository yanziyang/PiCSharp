using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pi.Tui;

/// <summary>Captured renderer state for a main-screen TUI.</summary>
public sealed record TuiMainScreenRenderState
{
    /// <summary>Lines emitted by the previous render.</summary>
    public required IReadOnlyList<string> PreviousLines { get; init; }

    /// <summary>Terminal width observed by the previous render.</summary>
    public required int PreviousWidth { get; init; }

    /// <summary>Terminal height observed by the previous render.</summary>
    public required int PreviousHeight { get; init; }

    /// <summary>Logical row at the end of the previous content.</summary>
    public required int CursorRow { get; init; }

    /// <summary>Actual terminal cursor row after the previous render.</summary>
    public required int HardwareCursorRow { get; init; }

    /// <summary>Largest working-area height rendered since the last clear.</summary>
    public required int MaxLinesRendered { get; init; }

    /// <summary>Top row of the viewport after the previous render.</summary>
    public required int PreviousViewportTop { get; init; }
}

/// <summary>TUI implementation that renders into the terminal's main screen and scrollback.</summary>
public class TuiMainScreen : TuiBase
{
    private const string _kittySequencePrefix = "\x1b_G";
    private readonly bool _isTermuxSession;
    private string[] _previousLines = [];
    private List<uint> _previousKittyImageIds = [];
    private int _previousWidth;
    private int _previousHeight;
    private int _cursorRow;
    private int _hardwareCursorRow;
    private int _maxLinesRendered;
    private int _previousViewportTop;

    /// <summary>Initializes a main-screen TUI around a terminal.</summary>
    public TuiMainScreen(
        ITerminal terminal,
        bool? showHardwareCursor = null,
        string? logDirectory = null,
        ITerminalImageSeam? imageSeam = null)
        : base(terminal, showHardwareCursor, logDirectory, imageSeam)
    {
        _isTermuxSession = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERMUX_VERSION"));
    }

    /// <inheritdoc />
    public override TuiMode Mode => TuiMode.Regular;

    /// <summary>Captures the renderer state so another main-screen TUI can resume it.</summary>
    public TuiMainScreenRenderState CaptureRenderState() => new()
    {
        PreviousLines = [.. _previousLines],
        PreviousWidth = _previousWidth,
        PreviousHeight = _previousHeight,
        CursorRow = _cursorRow,
        HardwareCursorRow = _hardwareCursorRow,
        MaxLinesRendered = _maxLinesRendered,
        PreviousViewportTop = _previousViewportTop,
    };

    /// <summary>Restores renderer state captured from another main-screen TUI.</summary>
    public void RestoreRenderState(TuiMainScreenRenderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _previousLines = state.PreviousLines
            .Select(static line => TerminalImage.IsImageLine(line) ? string.Empty : line)
            .ToArray();
        _previousKittyImageIds = [];
        _previousWidth = state.PreviousWidth;
        _previousHeight = state.PreviousHeight;
        _cursorRow = state.CursorRow;
        _hardwareCursorRow = state.HardwareCursorRow;
        _maxLinesRendered = state.MaxLinesRendered;
        _previousViewportTop = state.PreviousViewportTop;
    }

    /// <inheritdoc />
    protected override void ResetRenderState()
    {
        _previousLines = [];
        _previousKittyImageIds = [];
        _previousWidth = -1;
        _previousHeight = -1;
        _cursorRow = 0;
        _hardwareCursorRow = 0;
        _maxLinesRendered = 0;
        _previousViewportTop = 0;
    }

    /// <inheritdoc />
    protected override void BeforeTerminalStop(TuiStopOptions options)
    {
        if (options.PreserveScreen || _previousLines.Length == 0)
        {
            return;
        }

        Terminal.Write(" ");
        var targetRow = _previousLines.Length;
        var lineDiff = targetRow - _hardwareCursorRow;
        if (lineDiff > 0)
        {
            Terminal.Write($"\x1b[{lineDiff}B");
        }
        else if (lineDiff < 0)
        {
            Terminal.Write($"\x1b[{-lineDiff}A");
        }

        Terminal.Write("\r\n");
    }

    /// <inheritdoc />
    protected override void DoRender()
    {
        if (stopped)
        {
            return;
        }

        var width = Terminal.Columns;
        var height = Terminal.Rows;
        var widthChanged = _previousWidth != 0 && _previousWidth != width;
        var heightChanged = _previousHeight != 0 && _previousHeight != height;
        var previousBufferLength = _previousHeight > 0 ? _previousViewportTop + _previousHeight : height;
        var previousViewportTop = heightChanged ? Math.Max(0, previousBufferLength - height) : _previousViewportTop;
        var viewportTop = previousViewportTop;
        var hardwareCursorRow = _hardwareCursorRow;

        int ComputeLineDiff(int targetRow)
        {
            var currentScreenRow = hardwareCursorRow - previousViewportTop;
            var targetScreenRow = targetRow - viewportTop;
            return targetScreenRow - currentScreenRow;
        }

        var newLines = RenderMountedChildren().ToList();
        if (HasOverlayEntries)
        {
            newLines = CompositeOverlays(newLines, width, height).ToList();
        }

        var cursorPosition = ExtractCursorPosition(newLines, height);
        var resetLines = ApplyLineResets(newLines);

        void FullRender(bool clear)
        {
            fullRedrawCount++;
            var output = new TerminalOutputWriter(Terminal.Write);
            output.Append("\x1b[?2026h");
            if (clear)
            {
                output.Append(DeleteKittyImages(_previousKittyImageIds));
                output.Append("\x1b[2J\x1b[H\x1b[3J");
            }

            for (var index = 0; index < resetLines.Length; index++)
            {
                if (index > 0)
                {
                    output.Append("\r\n");
                }

                var line = resetLines[index];
                var isImage = TerminalImage.IsImageLine(line);
                var imageReservedRows = isImage ? GetKittyImageReservedRows(resetLines, index) : 1;
                if (imageReservedRows > 1 && imageReservedRows <= height)
                {
                    for (var row = 1; row < imageReservedRows; row++)
                    {
                        output.Append("\r\n");
                    }

                    output.Append($"\x1b[{imageReservedRows - 1}A");
                    output.Append(line);
                    output.Append($"\x1b[{imageReservedRows - 1}B");
                    index += imageReservedRows - 1;
                    continue;
                }

                output.Append(line);
            }

            output.Append("\x1b[?2026l");
            output.Flush();
            _cursorRow = Math.Max(0, resetLines.Length - 1);
            _hardwareCursorRow = _cursorRow;
            _maxLinesRendered = clear ? resetLines.Length : Math.Max(_maxLinesRendered, resetLines.Length);
            var bufferLength = Math.Max(height, resetLines.Length);
            _previousViewportTop = Math.Max(0, bufferLength - height);
            PositionHardwareCursor(cursorPosition, resetLines.Length);
            _previousLines = resetLines;
            _previousKittyImageIds = CollectKittyImageIds(resetLines);
            _previousWidth = width;
            _previousHeight = height;
        }

        var debugRedraw = Environment.GetEnvironmentVariable("PI_DEBUG_REDRAW") == "1";
        void LogRedraw(string reason)
        {
            if (!debugRedraw)
            {
                return;
            }

            var logPath = Path.Combine(logDirectory, "pi-debug.log");
            var message =
                $"[{FormatTimestamp()}] fullRender: {reason} (prev={_previousLines.Length}, new={resetLines.Length}, height={height})\n";
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, message);
        }

        if (_previousLines.Length == 0 && !widthChanged && !heightChanged)
        {
            LogRedraw("first render");
            FullRender(clear: false);
            return;
        }

        if (widthChanged)
        {
            LogRedraw($"terminal width changed ({_previousWidth} -> {width})");
            FullRender(clear: true);
            return;
        }

        if (heightChanged && !_isTermuxSession)
        {
            LogRedraw($"terminal height changed ({_previousHeight} -> {height})");
            FullRender(clear: true);
            return;
        }

        if (GetClearOnShrink() && resetLines.Length < _maxLinesRendered && !HasOverlayEntries)
        {
            LogRedraw($"clearOnShrink (maxLinesRendered={_maxLinesRendered})");
            FullRender(clear: true);
            return;
        }

        var firstChanged = -1;
        var lastChanged = -1;
        var maxLines = Math.Max(resetLines.Length, _previousLines.Length);
        for (var index = 0; index < maxLines; index++)
        {
            var oldLine = index < _previousLines.Length ? _previousLines[index] : string.Empty;
            var newLine = index < resetLines.Length ? resetLines[index] : string.Empty;
            if (!string.Equals(oldLine, newLine, StringComparison.Ordinal))
            {
                if (firstChanged == -1)
                {
                    firstChanged = index;
                }

                lastChanged = index;
            }
        }

        var appendedLines = resetLines.Length > _previousLines.Length;
        if (appendedLines)
        {
            if (firstChanged == -1)
            {
                firstChanged = _previousLines.Length;
            }

            lastChanged = resetLines.Length - 1;
        }

        if (firstChanged != -1)
        {
            (firstChanged, lastChanged) = ExpandChangedRangeForKittyImages(firstChanged, lastChanged, resetLines);
        }

        var appendStart = appendedLines && firstChanged == _previousLines.Length && firstChanged > 0;
        if (firstChanged == -1)
        {
            PositionHardwareCursor(cursorPosition, resetLines.Length);
            _previousViewportTop = previousViewportTop;
            _previousHeight = height;
            return;
        }

        if (firstChanged >= resetLines.Length)
        {
            if (_previousLines.Length > resetLines.Length)
            {
                var output = new TerminalOutputWriter(Terminal.Write);
                output.Append("\x1b[?2026h");
                output.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
                var targetRow = Math.Max(0, resetLines.Length - 1);
                if (targetRow < previousViewportTop)
                {
                    LogRedraw($"deleted lines moved viewport up ({targetRow} < {previousViewportTop})");
                    FullRender(clear: true);
                    return;
                }

                var lineDiff = ComputeLineDiff(targetRow);
                if (lineDiff > 0)
                {
                    output.Append($"\x1b[{lineDiff}B");
                }
                else if (lineDiff < 0)
                {
                    output.Append($"\x1b[{-lineDiff}A");
                }

                output.Append("\r");
                var extraLines = _previousLines.Length - resetLines.Length;
                if (extraLines > height)
                {
                    LogRedraw($"extraLines > height ({extraLines} > {height})");
                    FullRender(clear: true);
                    return;
                }

                var clearStartOffset = resetLines.Length == 0 ? 0 : 1;
                if (extraLines > 0 && clearStartOffset > 0)
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

                output.Append("\x1b[?2026l");
                output.Flush();
                _cursorRow = targetRow;
                _hardwareCursorRow = targetRow;
            }

            PositionHardwareCursor(cursorPosition, resetLines.Length);
            _previousLines = resetLines;
            _previousKittyImageIds = CollectKittyImageIds(resetLines);
            _previousWidth = width;
            _previousHeight = height;
            _previousViewportTop = previousViewportTop;
            return;
        }

        if (firstChanged < previousViewportTop)
        {
            LogRedraw($"firstChanged < viewportTop ({firstChanged} < {previousViewportTop})");
            FullRender(clear: true);
            return;
        }

        var differentialOutput = new TerminalOutputWriter(Terminal.Write);
        differentialOutput.Append("\x1b[?2026h");
        differentialOutput.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
        var previousViewportBottom = previousViewportTop + height - 1;
        var moveTargetRow = appendStart ? firstChanged - 1 : firstChanged;
        if (moveTargetRow > previousViewportBottom)
        {
            var currentScreenRow = Math.Max(0, Math.Min(height - 1, hardwareCursorRow - previousViewportTop));
            var moveToBottom = height - 1 - currentScreenRow;
            if (moveToBottom > 0)
            {
                differentialOutput.Append($"\x1b[{moveToBottom}B");
            }

            var scroll = moveTargetRow - previousViewportBottom;
            differentialOutput.Append(Repeat("\r\n", scroll));
            previousViewportTop += scroll;
            viewportTop += scroll;
            hardwareCursorRow = moveTargetRow;
        }

        var differentialLineDiff = ComputeLineDiff(moveTargetRow);
        if (differentialLineDiff > 0)
        {
            differentialOutput.Append($"\x1b[{differentialLineDiff}B");
        }
        else if (differentialLineDiff < 0)
        {
            differentialOutput.Append($"\x1b[{-differentialLineDiff}A");
        }

        differentialOutput.Append(appendStart ? "\r\n" : "\r");
        var renderEnd = Math.Min(lastChanged, resetLines.Length - 1);
        for (var index = firstChanged; index <= renderEnd; index++)
        {
            if (index > firstChanged)
            {
                differentialOutput.Append("\r\n");
            }

            var line = resetLines[index];
            var isImage = TerminalImage.IsImageLine(line);
            var imageReservedRows = isImage ? GetKittyImageReservedRows(resetLines, index, renderEnd) : 1;
            if (imageReservedRows > 1)
            {
                var imageStartScreenRow = index - viewportTop;
                if (imageStartScreenRow < 0 || imageStartScreenRow + imageReservedRows > height)
                {
                    LogRedraw($"kitty image pre-clear would scroll ({imageStartScreenRow} + {imageReservedRows} > {height})");
                    FullRender(clear: true);
                    return;
                }

                differentialOutput.Append("\x1b[2K");
                for (var row = 1; row < imageReservedRows; row++)
                {
                    differentialOutput.Append("\r\n\x1b[2K");
                }

                differentialOutput.Append($"\x1b[{imageReservedRows - 1}A");
                differentialOutput.Append(line);
                differentialOutput.Append($"\x1b[{imageReservedRows - 1}B");
                index += imageReservedRows - 1;
                continue;
            }

            differentialOutput.Append("\x1b[2K");
            var visibleWidth = TextMeasurement.VisibleWidth(line);
            if (!isImage && visibleWidth > width)
            {
                var crashLogPath = Path.Combine(logDirectory, "pi-crash.log");
                var crashLines = new List<string>
                {
                    $"Crash at {FormatTimestamp()}",
                    $"Terminal width: {width}",
                    $"Line {index} visible width: {visibleWidth}",
                    string.Empty,
                    "=== All rendered lines ===",
                };
                crashLines.AddRange(resetLines.Select((renderedLine, lineIndex) =>
                    $"[{lineIndex}] (w={TextMeasurement.VisibleWidth(renderedLine)}) {renderedLine}"));
                crashLines.Add(string.Empty);
                Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!);
                File.WriteAllText(crashLogPath, string.Join('\n', crashLines));
                Stop();
                throw new InvalidOperationException(string.Join('\n',
                [
                    $"Rendered line {index} exceeds terminal width ({visibleWidth} > {width}).",
                    string.Empty,
                    "This is likely caused by a custom TUI component not truncating its output.",
                    "Use visibleWidth() to measure and truncateToWidth() to truncate lines.",
                    string.Empty,
                    $"Debug log written to: {crashLogPath}",
                ]));
            }

            differentialOutput.Append(line);
        }

        var finalCursorRow = renderEnd;
        if (_previousLines.Length > resetLines.Length)
        {
            if (renderEnd < resetLines.Length - 1)
            {
                var moveDown = resetLines.Length - 1 - renderEnd;
                differentialOutput.Append($"\x1b[{moveDown}B");
                finalCursorRow = resetLines.Length - 1;
            }

            var extraLines = _previousLines.Length - resetLines.Length;
            for (var index = resetLines.Length; index < _previousLines.Length; index++)
            {
                differentialOutput.Append("\r\n\x1b[2K");
            }

            differentialOutput.Append($"\x1b[{extraLines}A");
        }

        differentialOutput.Append("\x1b[?2026l");
        if (Environment.GetEnvironmentVariable("PI_TUI_DEBUG") == "1")
        {
            const string debugDirectory = "/tmp/tui";
            Directory.CreateDirectory(debugDirectory);
            var debugPath = Path.Combine(
                debugDirectory,
                $"render-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.log");
            var cursorData = cursorPosition is null
                ? "null"
                : $"{{\"row\":{cursorPosition.Value.Row},\"col\":{cursorPosition.Value.Column}}}";
            var debugData = string.Join('\n',
            [
                $"firstChanged: {firstChanged}",
                $"viewportTop: {viewportTop}",
                $"cursorRow: {_cursorRow}",
                $"height: {height}",
                $"lineDiff: {differentialLineDiff}",
                $"hardwareCursorRow: {hardwareCursorRow}",
                $"renderEnd: {renderEnd}",
                $"finalCursorRow: {finalCursorRow}",
                $"cursorPos: {cursorData}",
                $"newLines.length: {resetLines.Length}",
                $"previousLines.length: {_previousLines.Length}",
                string.Empty,
                "=== newLines ===",
                SerializeLines(resetLines),
                string.Empty,
                "=== previousLines ===",
                SerializeLines(_previousLines),
                string.Empty,
                "=== buffer ===",
                $"[{differentialOutput.Length} chars written in bounded chunks]",
            ]);
            File.WriteAllText(debugPath, debugData);
        }

        differentialOutput.Flush();
        _cursorRow = Math.Max(0, resetLines.Length - 1);
        _hardwareCursorRow = finalCursorRow;
        _maxLinesRendered = Math.Max(_maxLinesRendered, resetLines.Length);
        _previousViewportTop = Math.Max(previousViewportTop, finalCursorRow - height + 1);
        PositionHardwareCursor(cursorPosition, resetLines.Length);
        _previousLines = resetLines;
        _previousKittyImageIds = CollectKittyImageIds(resetLines);
        _previousWidth = width;
        _previousHeight = height;
    }

    private static KittyImageHeader? ParseKittyImageHeader(string line)
    {
        var sequenceStart = line.IndexOf(_kittySequencePrefix, StringComparison.Ordinal);
        if (sequenceStart == -1)
        {
            return null;
        }

        var parametersStart = sequenceStart + _kittySequencePrefix.Length;
        var parametersEnd = line.IndexOf(';', parametersStart);
        if (parametersEnd == -1)
        {
            return null;
        }

        var ids = new List<uint>();
        uint rows = 1;
        foreach (var parameter in line[parametersStart..parametersEnd].Split(','))
        {
            var parts = parameter.Split('=', 2);
            if (parts.Length < 2 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue) ||
                !double.IsInteger(numberValue) ||
                numberValue <= 0 ||
                numberValue > uint.MaxValue)
            {
                continue;
            }

            var unsignedValue = (uint)numberValue;
            if (parts[0] == "i")
            {
                ids.Add(unsignedValue);
            }
            else if (parts[0] == "r")
            {
                rows = unsignedValue;
            }
        }

        return new KittyImageHeader(ids, rows);
    }

    private static IReadOnlyList<uint> ExtractKittyImageIds(string line) =>
        ParseKittyImageHeader(line)?.Ids ?? [];

    private static uint ExtractKittyImageRows(string line) => ParseKittyImageHeader(line)?.Rows ?? 1;

    private static List<uint> CollectKittyImageIds(IEnumerable<string> lines)
    {
        var ids = new List<uint>();
        var seen = new HashSet<uint>();
        foreach (var line in lines)
        {
            foreach (var id in ExtractKittyImageIds(line))
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static string DeleteKittyImages(IEnumerable<uint> ids)
    {
        var buffer = new StringBuilder();
        foreach (var id in ids)
        {
            buffer.Append(TerminalImage.DeleteKittyImage(id));
        }

        return buffer.ToString();
    }

    private static int GetKittyImageReservedRows(
        IReadOnlyList<string> lines,
        int index,
        int? maximumIndex = null)
    {
        var rows = ExtractKittyImageRows(index >= 0 && index < lines.Count ? lines[index] : string.Empty);
        if (rows <= 1)
        {
            return 1;
        }

        var finalIndex = maximumIndex ?? lines.Count - 1;
        var maxRows = (int)Math.Min(rows, (uint)Math.Max(0, Math.Min(finalIndex - index + 1, lines.Count - index)));
        var reservedRows = 1;
        while (reservedRows < maxRows)
        {
            var line = lines[index + reservedRows];
            if (TerminalImage.IsImageLine(line) || TextMeasurement.VisibleWidth(line) > 0)
            {
                break;
            }

            reservedRows++;
        }

        return reservedRows;
    }

    private (int FirstChanged, int LastChanged) ExpandChangedRangeForKittyImages(
        int firstChanged,
        int lastChanged,
        IReadOnlyList<string> newLines)
    {
        var expandedFirstChanged = firstChanged;
        var expandedLastChanged = lastChanged;

        void ExpandForLines(IReadOnlyList<string> lines)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (ExtractKittyImageIds(lines[index]).Count == 0)
                {
                    continue;
                }

                var blockEnd = index + GetKittyImageReservedRows(lines, index) - 1;
                if (index >= firstChanged || index <= lastChanged && blockEnd >= firstChanged)
                {
                    expandedFirstChanged = Math.Min(expandedFirstChanged, index);
                    expandedLastChanged = Math.Max(expandedLastChanged, blockEnd);
                }
            }
        }

        ExpandForLines(_previousLines);
        ExpandForLines(newLines);
        return (expandedFirstChanged, expandedLastChanged);
    }

    private string DeleteChangedKittyImages(int firstChanged, int lastChanged)
    {
        if (firstChanged < 0 || lastChanged < firstChanged)
        {
            return string.Empty;
        }

        var ids = new List<uint>();
        var seen = new HashSet<uint>();
        var maximumLine = Math.Min(lastChanged, _previousLines.Length - 1);
        for (var index = firstChanged; index <= maximumLine; index++)
        {
            foreach (var id in ExtractKittyImageIds(_previousLines[index]))
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return DeleteKittyImages(ids);
    }

    private void PositionHardwareCursor(CursorPosition? cursorPosition, int totalLines)
    {
        if (cursorPosition is null || totalLines <= 0)
        {
            Terminal.HideCursor();
            return;
        }

        var targetRow = Math.Max(0, Math.Min(cursorPosition.Value.Row, totalLines - 1));
        var targetColumn = Math.Max(0, cursorPosition.Value.Column);
        var rowDelta = targetRow - _hardwareCursorRow;
        var buffer = new StringBuilder();
        if (rowDelta > 0)
        {
            buffer.Append(CultureInfo.InvariantCulture, $"\x1b[{rowDelta}B");
        }
        else if (rowDelta < 0)
        {
            buffer.Append(CultureInfo.InvariantCulture, $"\x1b[{-rowDelta}A");
        }

        buffer.Append(CultureInfo.InvariantCulture, $"\x1b[{targetColumn + 1}G");
        if (buffer.Length > 0)
        {
            Terminal.Write(buffer.ToString());
        }

        _hardwareCursorRow = targetRow;
        if (GetShowHardwareCursor())
        {
            Terminal.ShowCursor();
        }
        else
        {
            Terminal.HideCursor();
        }
    }

    private static string Repeat(string value, int count)
    {
        if (count <= 0 || value.Length == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length * count);
        for (var index = 0; index < count; index++)
        {
            result.Append(value);
        }

        return result.ToString();
    }

    private static string FormatTimestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string SerializeLines(IReadOnlyList<string> lines)
    {
        var values = lines.Select(static line => $"  \"{JsonEncodedText.Encode(line)}\"");
        return $"[\n{string.Join(",\n", values)}\n]";
    }

    private sealed record KittyImageHeader(IReadOnlyList<uint> Ids, uint Rows);
}
