using System.Globalization;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

internal sealed class TestTui : TuiMainScreen
{
    public TestTui(
        ITerminal terminal,
        bool? showHardwareCursor = null,
        string? logDirectory = null,
        ITerminalImageSeam? imageSeam = null)
        : base(terminal, showHardwareCursor, logDirectory, imageSeam) { }
}

internal class TestComponent : IComponent
{
    public IReadOnlyList<string> Lines { get; set; } = [];
    public int InvalidationCount { get; private set; }

    public virtual IReadOnlyList<string> Render(int width) => Lines;

    public virtual void HandleInput(string data) { }

    public virtual void Invalidate() => InvalidationCount++;
}

internal sealed class InputComponent : TestComponent
{
    public int RenderCount { get; private set; }

    public override IReadOnlyList<string> Render(int width)
    {
        RenderCount++;
        return base.Render(width);
    }

    public override void HandleInput(string data) => Lines = [data];
}

internal sealed class BoundedWriteTerminal : ITerminal
{
    public List<string> Writes { get; } = [];

    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;
    public bool KittyProtocolActive => false;

    public void Start(Action<string> onInput, Action onResize) { }

    public void Stop() { }

    public ValueTask DrainInputAsync(int maxMs = 1000, int idleMs = 50, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public void Write(string data) => Writes.Add(data);

    public void MoveBy(int lines) { }

    public void HideCursor() { }

    public void ShowCursor() { }

    public void ClearLine() { }

    public void ClearFromCursor() { }

    public void ClearScreen() { }

    public void SetTitle(string title) { }

    public void SetProgress(bool active) { }
}

internal sealed class MemoryTerminal : ITerminal
{
    private readonly List<Cell[]> _screen;
    private Action<string>? _inputHandler;
    private Action? _resizeHandler;
    private int _cursorRow;
    private int _cursorColumn;
    private bool _italic;
    private int _completedRenderCount;
    private int _lastWaitedRenderCount;

    public MemoryTerminal(int columns = 80, int rows = 24)
    {
        Columns = columns;
        Rows = rows;
        _screen = CreateScreen(columns, rows);
    }

    public List<string> Writes { get; } = [];

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public bool KittyProtocolActive => true;

    public void Start(Action<string> onInput, Action onResize)
    {
        _inputHandler = onInput;
        _resizeHandler = onResize;
    }

    public void Stop()
    {
        _inputHandler = null;
        _resizeHandler = null;
    }

    public ValueTask DrainInputAsync(int maxMs = 1000, int idleMs = 50, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public void Write(string data)
    {
        Writes.Add(data);
        ProcessOutput(data);
        var searchStart = 0;
        while ((searchStart = data.IndexOf("\x1b[?2026l", searchStart, StringComparison.Ordinal)) >= 0)
        {
            Interlocked.Increment(ref _completedRenderCount);
            searchStart += "\x1b[?2026l".Length;
        }
    }

    public void MoveBy(int lines) => Write(lines > 0 ? $"\x1b[{lines}B" : lines < 0 ? $"\x1b[{-lines}A" : string.Empty);

    public void HideCursor() => Write("\x1b[?25l");

    public void ShowCursor() => Write("\x1b[?25h");

    public void ClearLine() => Write("\x1b[K");

    public void ClearFromCursor() => Write("\x1b[J");

    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    public void SetTitle(string title) => Write($"\x1b]0;{title}\x07");

    public void SetProgress(bool active) { }

    public void SendInput(string data) => _inputHandler?.Invoke(data);

    public void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        var resized = CreateScreen(columns, rows);
        var sourceStart = Math.Max(0, _screen.Count - resized.Count);
        var destinationStart = Math.Max(0, resized.Count - _screen.Count);
        for (var row = sourceStart; row < _screen.Count && destinationStart < resized.Count; row++, destinationStart++)
        {
            Array.Copy(_screen[row], resized[destinationStart], Math.Min(_screen[row].Length, resized[destinationStart].Length));
        }

        _screen.Clear();
        _screen.AddRange(resized);
        _cursorRow = Math.Clamp(_cursorRow, 0, Math.Max(0, rows - 1));
        _cursorColumn = Math.Clamp(_cursorColumn, 0, Math.Max(0, columns - 1));
        _resizeHandler?.Invoke();
    }

    public IReadOnlyList<string> GetViewport() => _screen.Select(static row =>
    {
        var lastWritten = Array.FindLastIndex(row, static cell => cell.Written);
        return lastWritten < 0
            ? string.Empty
            : new string(row.Take(lastWritten + 1).Select(static cell => cell.Character).ToArray());
    }).ToArray();

    public bool GetCellItalic(int row, int column) => _screen[row][column].Italic;

    public void ClearWrites() => Writes.Clear();

    /// <summary>
    /// Waits for a render to reach this terminal and settle.
    /// </summary>
    /// <remarks>
    /// Renders are throttled to a 16 ms minimum interval and a single frame can arrive as several
    /// writes. This waits for a synchronized-output end marker, then for output to stop arriving.
    /// Cursor setup performed by <see cref="TuiBase.Start"/> therefore cannot be mistaken for the
    /// requested frame. Under load the wait extends itself instead of expiring early.
    /// </remarks>
    public async Task WaitForRenderAsync(int timeoutMs = 5000, int quietMs = 60)
    {
        await Task.Yield();

        var deadline = Environment.TickCount64 + timeoutMs;
        var previousRenderCount = Volatile.Read(ref _lastWaitedRenderCount);
        var seen = -1;
        var quietUntil = Environment.TickCount64 + quietMs;
        var completedFrameSeen = false;

        while (Environment.TickCount64 < deadline)
        {
            var completedRenderCount = Volatile.Read(ref _completedRenderCount);
            completedFrameSeen |= completedRenderCount > previousRenderCount;
            var count = Writes.Count;
            if (count != seen)
            {
                seen = count;
                quietUntil = Environment.TickCount64 + quietMs;
            }
            else if (completedFrameSeen && Environment.TickCount64 >= quietUntil)
            {
                Volatile.Write(ref _lastWaitedRenderCount, completedRenderCount);
                return;
            }

            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Timed out waiting for a completed TUI render frame.");
    }

    private static List<Cell[]> CreateScreen(int columns, int rows) =>
        Enumerable.Range(0, Math.Max(1, rows))
            .Select(_ => Enumerable.Range(0, Math.Max(1, columns)).Select(static _ => new Cell()).ToArray())
            .ToList();

    private void ProcessOutput(string data)
    {
        var index = 0;
        while (index < data.Length)
        {
            if (data[index] != '\x1b')
            {
                ProcessCharacter(data[index]);
                index++;
                continue;
            }

            if (index + 1 >= data.Length)
            {
                index++;
                continue;
            }

            var next = data[index + 1];
            if (next == '[')
            {
                var final = index + 2;
                while (final < data.Length && !IsCsiFinal(data[final]))
                {
                    final++;
                }

                if (final >= data.Length)
                {
                    return;
                }

                ApplyCsi(data[(index + 2)..final], data[final]);
                index = final + 1;
                continue;
            }

            if (next is ']' or '_' or 'P')
            {
                var end = FindControlEnd(data, index + 2);
                index = end < 0 ? data.Length : end;
                continue;
            }

            index += 2;
        }
    }

    private static bool IsCsiFinal(char value) => value is >= '@' and <= '~';

    private static int FindControlEnd(string data, int start)
    {
        for (var index = start; index < data.Length; index++)
        {
            if (data[index] == '\x07')
            {
                return index + 1;
            }

            if (data[index] == '\x1b' && index + 1 < data.Length && data[index + 1] == '\\')
            {
                return index + 2;
            }
        }

        return -1;
    }

    private void ApplyCsi(string payload, char final)
    {
        var parameters = payload.TrimStart('?').Split(';', StringSplitOptions.None);
        var first = ParseParameter(parameters, 0, 1);
        switch (final)
        {
            case 'A':
                _cursorRow = Math.Max(0, _cursorRow - first);
                break;
            case 'B':
                _cursorRow = Math.Min(_screen.Count - 1, _cursorRow + first);
                break;
            case 'G':
                _cursorColumn = Math.Clamp(first - 1, 0, _screen[0].Length - 1);
                break;
            case 'H' or 'f':
                _cursorRow = Math.Clamp(ParseParameter(parameters, 0, 1) - 1, 0, _screen.Count - 1);
                _cursorColumn = Math.Clamp(ParseParameter(parameters, 1, 1) - 1, 0, _screen[0].Length - 1);
                break;
            case 'J':
                if (first is 2 or 3)
                {
                    ClearScreenBuffer();
                }
                else if (first == 0)
                {
                    ClearFromCursorBuffer();
                }

                break;
            case 'K':
                ClearLineBuffer(first);
                break;
            case 'm':
                ApplySgr(parameters);
                break;
        }
    }

    private static int ParseParameter(string[] parameters, int index, int fallback)
    {
        if (index >= parameters.Length || parameters[index].Length == 0 || parameters[index] == "?")
        {
            return fallback;
        }

        return int.TryParse(parameters[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private void ApplySgr(string[] parameters)
    {
        foreach (var parameter in parameters)
        {
            var code = int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            if (code == 0 || code == 23)
            {
                _italic = false;
            }
            else if (code == 3)
            {
                _italic = true;
            }
        }
    }

    private void ProcessCharacter(char value)
    {
        switch (value)
        {
            case '\r':
                _cursorColumn = 0;
                return;
            case '\n':
                AdvanceLine();
                return;
            case '\0':
                return;
        }

        if (_cursorColumn >= _screen[0].Length)
        {
            _cursorColumn = 0;
            AdvanceLine();
        }

        _screen[_cursorRow][_cursorColumn] = new Cell(value, _italic, true);
        _cursorColumn++;
    }

    private void AdvanceLine()
    {
        _cursorRow++;
        if (_cursorRow < _screen.Count)
        {
            return;
        }

        _screen.RemoveAt(0);
        _screen.Add(Enumerable.Range(0, _screen[0].Length).Select(static _ => new Cell()).ToArray());
        _cursorRow = _screen.Count - 1;
    }

    private void ClearScreenBuffer()
    {
        for (var row = 0; row < _screen.Count; row++)
        {
            for (var column = 0; column < _screen[row].Length; column++)
            {
                _screen[row][column] = new Cell();
            }
        }
    }

    private void ClearFromCursorBuffer()
    {
        for (var row = _cursorRow; row < _screen.Count; row++)
        {
            var start = row == _cursorRow ? _cursorColumn : 0;
            for (var column = start; column < _screen[row].Length; column++)
            {
                _screen[row][column] = new Cell();
            }
        }
    }

    private void ClearLineBuffer(int mode)
    {
        var start = mode == 1 ? 0 : _cursorColumn;
        var end = mode == 1 ? _cursorColumn : _screen[_cursorRow].Length - 1;
        if (mode == 2)
        {
            start = 0;
            end = _screen[_cursorRow].Length - 1;
        }

        for (var column = start; column <= end; column++)
        {
            _screen[_cursorRow][column] = new Cell();
        }
    }

    private sealed record Cell(char Character = ' ', bool Italic = false, bool Written = false);
}
