using System.Globalization;
using System.Text;

namespace Pi.Tui;

#pragma warning disable CA1715 // Compatibility interfaces retain upstream names.

/// <summary>Terminal operations used by the Pi TUI.</summary>
public interface ITerminal
{
    /// <summary>Starts terminal input processing.</summary>
    void Start(Action<string> onInput, Action onResize);

    /// <summary>Stops terminal input processing and restores terminal state.</summary>
    void Stop();

    /// <summary>Waits for terminal input to become idle or for the maximum wait to elapse.</summary>
    ValueTask DrainInputAsync(
        int maxMs = 1000,
        int idleMs = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Writes terminal control data.</summary>
    void Write(string data);

    /// <summary>Current terminal width in columns.</summary>
    int Columns { get; }

    /// <summary>Current terminal height in rows.</summary>
    int Rows { get; }

    /// <summary>Whether Kitty keyboard protocol is active.</summary>
    bool KittyProtocolActive { get; }

    /// <summary>Moves the cursor vertically by the requested number of lines.</summary>
    void MoveBy(int lines);

    /// <summary>Hides the hardware cursor.</summary>
    void HideCursor();

    /// <summary>Shows the hardware cursor.</summary>
    void ShowCursor();

    /// <summary>Clears from the cursor to the end of the current line.</summary>
    void ClearLine();

    /// <summary>Clears from the cursor to the end of the screen.</summary>
    void ClearFromCursor();

    /// <summary>Clears the screen and moves the cursor home.</summary>
    void ClearScreen();

    /// <summary>Sets the terminal window title.</summary>
    void SetTitle(string title);

    /// <summary>Sets the terminal progress indicator state.</summary>
    void SetProgress(bool active);
}

/// <summary>Compatibility name matching the upstream terminal contract.</summary>
public interface Terminal : ITerminal
{
}

/// <summary>
/// Host operations needed by <see cref="ProcessTerminal"/>. The production host uses the
/// console; tests can supply an in-memory host without touching the process console.
/// </summary>
public interface ITerminalHost
{
    /// <summary>Raised when UTF-8 input is available.</summary>
    event Action<string>? Input;

    /// <summary>Raised when terminal dimensions change.</summary>
    event Action? Resize;

    /// <summary>Reported terminal width, when available.</summary>
    int? Columns { get; }

    /// <summary>Reported terminal height, when available.</summary>
    int? Rows { get; }

    /// <summary>Whether the host believes input is currently raw.</summary>
    bool IsRaw { get; }

    /// <summary>Changes the host's raw-input state.</summary>
    void SetRawMode(bool enabled);

    /// <summary>Starts receiving input.</summary>
    void Resume();

    /// <summary>Stops receiving input.</summary>
    void Pause();

    /// <summary>Writes data to the terminal output.</summary>
    void Write(string data);
}

/// <summary>
/// Native terminal operations used at the two platform-specific seams in the upstream module.
/// The default implementation deliberately reports no pressed modifiers and performs no VT
/// input-mode setup.
/// </summary>
public interface INativeTerminalSupport
{
    /// <summary>Returns whether a native modifier is currently pressed.</summary>
    bool IsModifierPressed(string modifier);

    /// <summary>Enables Windows VT input mode when a native implementation is available.</summary>
    void EnableWindowsVtInput();
}

/// <summary>Faithful degraded native-terminal implementation used by default.</summary>
public sealed class NoopNativeTerminalSupport : INativeTerminalSupport
{
    /// <inheritdoc />
    public bool IsModifierPressed(string modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        return false;
    }

    /// <inheritdoc />
    public void EnableWindowsVtInput() { }
}

/// <summary>Keyboard response parsed during Kitty protocol negotiation.</summary>
public abstract record KeyboardProtocolNegotiationSequence
{
    private KeyboardProtocolNegotiationSequence() { }

    /// <summary>Kitty keyboard protocol flags reported by the terminal.</summary>
    public sealed record KittyFlags(int Flags) : KeyboardProtocolNegotiationSequence;

    /// <summary>Device-attributes response reported by a terminal without Kitty flags.</summary>
    public sealed record DeviceAttributes : KeyboardProtocolNegotiationSequence;
}

/// <summary>Process-backed terminal implementation matching Pi's terminal writer behavior.</summary>
public sealed class ProcessTerminal : Terminal, IDisposable
{
    private const string _bracketedPasteEnable = "\x1b[?2004h";
    private const string _bracketedPasteDisable = "\x1b[?2004l";
    private const string _kittyQuery = "\x1b[>7u\x1b[?u\x1b[c";
    private const string _kittyDisable = "\x1b[<u";
    private const string _modifyOtherKeysEnable = "\x1b[>4;2m";
    private const string _modifyOtherKeysDisable = "\x1b[>4;0m";
    private const string _progressActive = "\x1b]9;4;3\x07";
    private const string _progressClear = "\x1b]9;4;0\x07";
    private const string _nativeShiftEnter = "\x1b[13;2u";
    private const int _kittyResponseTimeoutMilliseconds = 150;
    private const int _progressKeepAliveMilliseconds = 1000;

    private readonly ITerminalHost _host;
    private readonly INativeTerminalSupport _nativeSupport;
    private readonly IReadOnlyDictionary<string, string?>? _environment;
    private readonly object _stateGate = new();
    private readonly string? _writeLogPath;
    private StdinBuffer? _stdinBuffer;
    private Action<string>? _inputHandler;
    private Action? _resizeHandler;
    private Action<string>? _hostInputHandler;
    private Action? _hostResizeHandler;
    private Timer? _negotiationTimer;
    private Timer? _progressTimer;
    private string _keyboardProtocolNegotiationBuffer = string.Empty;
    private bool _keyboardProtocolPushed;
    private bool _modifyOtherKeysActive;
    private bool _kittyProtocolActive;
    private bool _previousRawMode;
    private bool _progressActiveState;

    /// <summary>Initializes a process terminal with the real console host by default.</summary>
    public ProcessTerminal(
        ITerminalHost? host = null,
        INativeTerminalSupport? nativeSupport = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _host = host ?? new ConsoleTerminalHost();
        _nativeSupport = nativeSupport ?? new NoopNativeTerminalSupport();
        _environment = environment;
        _writeLogPath = ResolveWriteLogPath(ReadEnvironment("PI_TUI_WRITE_LOG"));
    }

    /// <summary>Whether Kitty keyboard protocol is active.</summary>
    public bool KittyProtocolActive => _kittyProtocolActive;

    /// <summary>Whether the modifyOtherKeys fallback is active.</summary>
    public bool ModifyOtherKeysActive => _modifyOtherKeysActive;

    /// <summary>Whether this terminal pushed a keyboard protocol mode that needs cleanup.</summary>
    public bool KeyboardProtocolPushed => _keyboardProtocolPushed;

    /// <inheritdoc />
    public int Columns => PositiveDimension(_host.Columns) ?? PositiveEnvironmentDimension("COLUMNS") ?? 80;

    /// <inheritdoc />
    public int Rows => PositiveDimension(_host.Rows) ?? PositiveEnvironmentDimension("LINES") ?? 24;

    /// <summary>Resolves the upstream escape timeout from an environment mapping.</summary>
    public static int ResolveEscapeTimeoutMs(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return ResolveEscapeTimeoutMs(key => environment.TryGetValue(key, out var value) ? value : null);
    }

    /// <summary>Resolves the upstream escape timeout from the current process environment.</summary>
    public static int ResolveEscapeTimeoutMs() => ResolveEscapeTimeoutMs(Environment.GetEnvironmentVariable);

    /// <summary>Rewrites a Return byte when native Shift detection says Shift is pressed.</summary>
    public static string NormalizeNativeShiftEnterInput(string data, bool shouldDetect, bool isShiftPressed)
    {
        ArgumentNullException.ThrowIfNull(data);
        return shouldDetect && data == "\r" && isShiftPressed ? _nativeShiftEnter : data;
    }

    /// <summary>Applies the Apple Terminal Return normalization rule.</summary>
    public static string NormalizeAppleTerminalInput(string data, bool shouldDetect, bool isShiftPressed) =>
        NormalizeNativeShiftEnterInput(data, shouldDetect, isShiftPressed);

    /// <summary>Parses a complete Kitty keyboard or device-attributes response.</summary>
    public static KeyboardProtocolNegotiationSequence? ParseKeyboardProtocolNegotiationSequence(string sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.StartsWith("\x1b[?", StringComparison.Ordinal) && sequence.EndsWith('u'))
        {
            var flagsText = sequence[3..^1];
            if (flagsText.Length > 0 && flagsText.All(static character => character is >= '0' and <= '9') &&
                int.TryParse(flagsText, NumberStyles.None, CultureInfo.InvariantCulture, out var flags))
            {
                return new KeyboardProtocolNegotiationSequence.KittyFlags(flags);
            }
        }

        if (sequence.StartsWith("\x1b[?", StringComparison.Ordinal) && sequence.EndsWith('c'))
        {
            var attributes = sequence[3..^1];
            if (attributes.All(static character => character is >= '0' and <= '9' or ';'))
            {
                return new KeyboardProtocolNegotiationSequence.DeviceAttributes();
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Start(Action<string> onInput, Action onResize)
    {
        ArgumentNullException.ThrowIfNull(onInput);
        ArgumentNullException.ThrowIfNull(onResize);
        _inputHandler = onInput;
        _resizeHandler = onResize;
        _previousRawMode = _host.IsRaw;
        _host.SetRawMode(true);
        _hostInputHandler = HandleHostInput;
        _hostResizeHandler = HandleHostResize;
        _host.Input += _hostInputHandler;
        _host.Resize += _hostResizeHandler;
        _host.Resume();
        Write(_bracketedPasteEnable);
        _nativeSupport.EnableWindowsVtInput();
        QueryAndEnableKittyProtocol();
    }

    /// <summary>
    /// Starts Kitty negotiation and installs the buffered input path. This public entry point is
    /// also useful to hosts that own their input loop and need to negotiate independently of
    /// <see cref="Start"/>.
    /// </summary>
    public void QueryAndEnableKittyProtocol()
    {
        SetupStdinBuffer();
        SubscribeHostInput();
        _keyboardProtocolPushed = true;
        ClearKeyboardProtocolNegotiationBuffer();
        Write(_kittyQuery);
    }

    /// <inheritdoc />
    public async ValueTask DrainInputAsync(
        int maxMs = 1000,
        int idleMs = 50,
        CancellationToken cancellationToken = default)
    {
        maxMs = Math.Max(0, maxMs);
        idleMs = Math.Max(0, idleMs);
        var shouldDisableKitty = _keyboardProtocolPushed || _kittyProtocolActive;
        ClearKeyboardProtocolNegotiationBuffer();
        if (shouldDisableKitty)
        {
            Write(_kittyDisable);
            _keyboardProtocolPushed = false;
            _kittyProtocolActive = false;
            Keys.SetKittyProtocolActive(false);
        }

        DisableModifyOtherKeys();
        var previousInputHandler = _inputHandler;
        _inputHandler = null;
        var lastDataTime = Environment.TickCount64;
        Action<string> observeInput = _ => Interlocked.Exchange(ref lastDataTime, Environment.TickCount64);
        _host.Input += observeInput;
        try
        {
            var endTime = Environment.TickCount64 + maxMs;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = Environment.TickCount64;
                var timeLeft = endTime - now;
                if (timeLeft <= 0 || now - Interlocked.Read(ref lastDataTime) >= idleMs)
                {
                    break;
                }

                var delay = (int)Math.Min(Math.Max(1, idleMs), timeLeft);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _host.Input -= observeInput;
            _inputHandler = previousInputHandler;
        }
    }

    /// <summary>Compatibility alias for callers using the upstream method name.</summary>
    public ValueTask DrainInput(int maxMs = 1000, int idleMs = 50, CancellationToken cancellationToken = default) =>
        DrainInputAsync(maxMs, idleMs, cancellationToken);

    /// <inheritdoc />
    public void Stop()
    {
        StopProgressTimer();
        if (_progressActiveState)
        {
            Write(_progressClear);
            _progressActiveState = false;
        }
        Write(_bracketedPasteDisable);

        if (_keyboardProtocolPushed || _kittyProtocolActive)
        {
            Write(_kittyDisable);
            _keyboardProtocolPushed = false;
            _kittyProtocolActive = false;
            Keys.SetKittyProtocolActive(false);
        }

        DisableModifyOtherKeys();
        ClearKeyboardProtocolNegotiationBuffer();
        _stdinBuffer?.Destroy();
        _stdinBuffer = null;
        UnsubscribeHostInput();
        _inputHandler = null;
        if (_hostResizeHandler is not null)
        {
            _host.Resize -= _hostResizeHandler;
            _hostResizeHandler = null;
        }

        _host.Pause();
        _host.SetRawMode(_previousRawMode);
        _resizeHandler = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        if (_host is IDisposable disposableHost)
        {
            disposableHost.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Write(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _host.Write(data);
        if (_writeLogPath is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_writeLogPath, data, Encoding.UTF8);
        }
        catch
        {
            // Upstream logging is best-effort and must never affect terminal output.
        }
    }

    /// <inheritdoc />
    public void MoveBy(int lines)
    {
        if (lines > 0)
        {
            Write($"\x1b[{lines}B");
        }
        else if (lines < 0)
        {
            Write($"\x1b[{-lines}A");
        }
    }

    /// <inheritdoc />
    public void HideCursor() => Write("\x1b[?25l");

    /// <inheritdoc />
    public void ShowCursor() => Write("\x1b[?25h");

    /// <inheritdoc />
    public void ClearLine() => Write("\x1b[K");

    /// <inheritdoc />
    public void ClearFromCursor() => Write("\x1b[J");

    /// <inheritdoc />
    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    /// <inheritdoc />
    public void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Write($"\x1b]0;{title}\x07");
    }

    /// <inheritdoc />
    public void SetProgress(bool active)
    {
        if (!active)
        {
            StopProgressTimer();
            _progressActiveState = false;
            Write(_progressClear);
            return;
        }

        _progressActiveState = true;
        Write(_progressActive);
        StopProgressTimer();
        _progressTimer = new Timer(
            static state => ((ProcessTerminal)state!).Write(_progressActive),
            this,
            _progressKeepAliveMilliseconds,
            _progressKeepAliveMilliseconds);
    }

    private static int ResolveEscapeTimeoutMs(Func<string, string?> getEnvironment)
    {
        var configured = getEnvironment("PI_TUI_ESC_TIMEOUT");
        if (double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed > 0)
        {
            return (int)parsed;
        }

        return getEnvironment("SSH_CONNECTION") is not null || getEnvironment("SSH_TTY") is not null ? 100 : 10;
    }

    private static string? ResolveWriteLogPath(string? configured)
    {
        if (string.IsNullOrEmpty(configured))
        {
            return null;
        }

        if (!Directory.Exists(configured))
        {
            return configured;
        }

        return Path.Combine(
            configured,
            $"tui-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Environment.ProcessId}.log");
    }

    private string? ReadEnvironment(string key)
    {
        if (_environment is not null)
        {
            return _environment.TryGetValue(key, out var value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(key);
    }

    private int? PositiveEnvironmentDimension(string key)
    {
        var value = ReadEnvironment(key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static int? PositiveDimension(int? dimension) => dimension is > 0 ? dimension : null;

    private void SetupStdinBuffer()
    {
        _stdinBuffer?.Destroy();
        _stdinBuffer = new StdinBuffer(new StdinBufferOptions
        {
            EscapeTimeout = _environment is null ? ResolveEscapeTimeoutMs() : ResolveEscapeTimeoutMs(_environment),
        });
        _stdinBuffer.Data += HandleBufferedInput;
        _stdinBuffer.Paste += content => ForwardInputSequence($"\x1b[200~{content}\x1b[201~");
    }

    private void SubscribeHostInput()
    {
        if (_hostInputHandler is null)
        {
            _hostInputHandler = HandleHostInput;
        }

        _host.Input -= _hostInputHandler;
        _host.Input += _hostInputHandler;
    }

    private void UnsubscribeHostInput()
    {
        if (_hostInputHandler is not null)
        {
            _host.Input -= _hostInputHandler;
        }
    }

    private void HandleHostInput(string data) => _stdinBuffer?.Process(data);

    private void HandleHostResize() => _resizeHandler?.Invoke();

    private void HandleBufferedInput(string sequence)
    {
        var parsed = ReadKeyboardProtocolNegotiationSequence(sequence, out var pending);
        if (pending)
        {
            return;
        }

        if (parsed is not null && HandleKeyboardProtocolNegotiationSequence(parsed))
        {
            return;
        }

        ForwardInputSequence(sequence);
    }

    private KeyboardProtocolNegotiationSequence? ReadKeyboardProtocolNegotiationSequence(
        string sequence,
        out bool pending)
    {
        pending = false;
        lock (_stateGate)
        {
            if (_keyboardProtocolNegotiationBuffer.Length > 0)
            {
                var combined = _keyboardProtocolNegotiationBuffer + sequence;
                var combinedParsed = ParseKeyboardProtocolNegotiationSequence(combined);
                if (combinedParsed is not null)
                {
                    ClearKeyboardProtocolNegotiationBuffer();
                    return combinedParsed;
                }

                if (IsKeyboardProtocolNegotiationPrefix(combined))
                {
                    _keyboardProtocolNegotiationBuffer = combined;
                    ScheduleKeyboardProtocolNegotiationFlush();
                    pending = true;
                    return null;
                }

                var buffered = _keyboardProtocolNegotiationBuffer;
                ClearKeyboardProtocolNegotiationBuffer();
                ForwardInputSequence(buffered);
            }

            var parsed = ParseKeyboardProtocolNegotiationSequence(sequence);
            if (parsed is not null)
            {
                return parsed;
            }

            if (IsKeyboardProtocolNegotiationPrefix(sequence))
            {
                _keyboardProtocolNegotiationBuffer = sequence;
                ScheduleKeyboardProtocolNegotiationFlush();
                pending = true;
            }

            return null;
        }
    }

    private bool HandleKeyboardProtocolNegotiationSequence(KeyboardProtocolNegotiationSequence sequence)
    {
        ClearKeyboardProtocolNegotiationBuffer();
        switch (sequence)
        {
            case KeyboardProtocolNegotiationSequence.KittyFlags { Flags: not 0 }:
                DisableModifyOtherKeys();
                if (!_kittyProtocolActive)
                {
                    _kittyProtocolActive = true;
                    Keys.SetKittyProtocolActive(true);
                }

                return true;
            case KeyboardProtocolNegotiationSequence.KittyFlags { Flags: 0 }:
                EnableModifyOtherKeys();
                return true;
            case KeyboardProtocolNegotiationSequence.DeviceAttributes:
                if (!_kittyProtocolActive)
                {
                    EnableModifyOtherKeys();
                }

                return true;
            default:
                return false;
        }
    }

    private static bool IsKeyboardProtocolNegotiationPrefix(string sequence) =>
        sequence == "\x1b[" ||
        sequence.StartsWith("\x1b[?", StringComparison.Ordinal) &&
        sequence[3..].All(static character => character is >= '0' and <= '9' or ';');

    private void ScheduleKeyboardProtocolNegotiationFlush()
    {
        if (_negotiationTimer is not null)
        {
            return;
        }

        _negotiationTimer = new Timer(
            static state => ((ProcessTerminal)state!).FlushKeyboardProtocolNegotiationBuffer(),
            this,
            _kittyResponseTimeoutMilliseconds,
            Timeout.Infinite);
    }

    private void FlushKeyboardProtocolNegotiationBuffer()
    {
        string buffered;
        lock (_stateGate)
        {
            _negotiationTimer?.Dispose();
            _negotiationTimer = null;
            buffered = _keyboardProtocolNegotiationBuffer;
            _keyboardProtocolNegotiationBuffer = string.Empty;
        }

        if (buffered.Length > 0)
        {
            ForwardInputSequence(buffered);
        }
    }

    private void ClearKeyboardProtocolNegotiationBuffer()
    {
        lock (_stateGate)
        {
            _negotiationTimer?.Dispose();
            _negotiationTimer = null;
            _keyboardProtocolNegotiationBuffer = string.Empty;
        }
    }

    private void ForwardInputSequence(string sequence)
    {
        var handler = _inputHandler;
        if (handler is null)
        {
            return;
        }

        var shouldDetect = sequence == "\r" && (IsAppleTerminalSession() || OperatingSystem.IsWindows());
        var normalized = NormalizeNativeShiftEnterInput(
            sequence,
            shouldDetect,
            _nativeSupport.IsModifierPressed("shift"));
        handler(normalized);
    }

    private bool IsAppleTerminalSession() =>
        OperatingSystem.IsMacOS() && string.Equals(ReadEnvironment("TERM_PROGRAM"), "Apple_Terminal", StringComparison.Ordinal);

    private void EnableModifyOtherKeys()
    {
        if (_kittyProtocolActive || _modifyOtherKeysActive)
        {
            return;
        }

        Write(_modifyOtherKeysEnable);
        _modifyOtherKeysActive = true;
    }

    private void DisableModifyOtherKeys()
    {
        if (!_modifyOtherKeysActive)
        {
            return;
        }

        Write(_modifyOtherKeysDisable);
        _modifyOtherKeysActive = false;
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Dispose();
        _progressTimer = null;
    }
}

/// <summary>Minimal console-backed host for <see cref="ProcessTerminal"/>.</summary>
public sealed class ConsoleTerminalHost : ITerminalHost, IDisposable
{
    private readonly object _writeGate = new();
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private bool _raw;

    /// <inheritdoc />
    public event Action<string>? Input;

    /// <inheritdoc />
    public event Action? Resize
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public int? Columns
    {
        get
        {
            try
            {
                return Console.IsOutputRedirected ? null : Console.WindowWidth;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public int? Rows
    {
        get
        {
            try
            {
                return Console.IsOutputRedirected ? null : Console.WindowHeight;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsRaw => _raw;

    /// <inheritdoc />
    public void SetRawMode(bool enabled) => _raw = enabled;

    /// <inheritdoc />
    public void Resume()
    {
        if (_readerTask is { IsCompleted: false })
        {
            return;
        }

        _readerCancellation?.Dispose();
        _readerCancellation = new CancellationTokenSource();
        var cancellationToken = _readerCancellation.Token;
        _readerTask = Task.Run(async () =>
        {
            try
            {
                using var input = Console.OpenStandardInput();
                var buffer = new byte[4096];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    Input?.Invoke(Encoding.UTF8.GetString(buffer, 0, count));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Pause()
    {
        _readerCancellation?.Cancel();
        _readerCancellation = null;
    }

    /// <inheritdoc />
    public void Write(string data)
    {
        lock (_writeGate)
        {
            Console.Out.Write(data);
            Console.Out.Flush();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Pause();
        GC.SuppressFinalize(this);
    }
}

#pragma warning restore CA1715
