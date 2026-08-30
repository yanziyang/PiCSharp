using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.Tui;

#pragma warning disable CA1708, CA1711, CA1715 // Compatibility names match the upstream public API.

/// <summary>Optional result returned by a TUI input listener.</summary>
public sealed record TuiInputListenerResult
{
    /// <summary>Stops input propagation when true.</summary>
    public bool Consume { get; init; }

    /// <summary>Replacement input passed to the next listener or focused component.</summary>
    public string? Data { get; init; }

    /// <summary>Initializes an input-listener result.</summary>
    public TuiInputListenerResult(bool consume = false, string? data = null)
    {
        Consume = consume;
        Data = data;
    }
}

/// <summary>Input listener invoked before TUI key dispatch.</summary>
public delegate TuiInputListenerResult? TuiInputListener(string data);

/// <summary>Component focus state maintained by the TUI.</summary>
public interface IFocusable
{
    /// <summary>Whether this component currently owns focus.</summary>
    bool Focused { get; set; }
}

/// <summary>Compatibility name matching the upstream focusable component contract.</summary>
public interface Focusable : IFocusable
{
}

/// <summary>Overlay anchor value. String conversion preserves the upstream string API.</summary>
public readonly record struct OverlayAnchor(string Value)
{
    /// <summary>Centered in both axes.</summary>
    public static OverlayAnchor Center => new("center");

    /// <summary>Top-left anchor.</summary>
    public static OverlayAnchor TopLeft => new("top-left");

    /// <summary>Top-right anchor.</summary>
    public static OverlayAnchor TopRight => new("top-right");

    /// <summary>Bottom-left anchor.</summary>
    public static OverlayAnchor BottomLeft => new("bottom-left");

    /// <summary>Bottom-right anchor.</summary>
    public static OverlayAnchor BottomRight => new("bottom-right");

    /// <summary>Top-center anchor.</summary>
    public static OverlayAnchor TopCenter => new("top-center");

    /// <summary>Bottom-center anchor.</summary>
    public static OverlayAnchor BottomCenter => new("bottom-center");

    /// <summary>Left-center anchor.</summary>
    public static OverlayAnchor LeftCenter => new("left-center");

    /// <summary>Right-center anchor.</summary>
    public static OverlayAnchor RightCenter => new("right-center");

    /// <summary>Converts an upstream anchor string.</summary>
    public static implicit operator OverlayAnchor(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new OverlayAnchor(value);
    }

    /// <summary>Returns the upstream anchor string.</summary>
    public static implicit operator string(OverlayAnchor value) => value.Value;
}

/// <summary>Absolute or percentage size used by overlay layout.</summary>
public readonly record struct SizeValue
{
    /// <summary>Absolute numeric value, when supplied.</summary>
    public double? Absolute { get; }

    /// <summary>Percentage text, when supplied.</summary>
    public string? Percentage { get; }

    /// <summary>Initializes an absolute size.</summary>
    public SizeValue(double absolute)
    {
        Absolute = absolute;
        Percentage = null;
    }

    /// <summary>Initializes a percentage or other upstream size string.</summary>
    public SizeValue(string percentage)
    {
        ArgumentNullException.ThrowIfNull(percentage);
        Absolute = null;
        Percentage = percentage;
    }

    /// <summary>Converts an integer size.</summary>
    public static implicit operator SizeValue(int value) => new(value);

    /// <summary>Converts a numeric size.</summary>
    public static implicit operator SizeValue(double value) => new(value);

    /// <summary>Converts a percentage size string.</summary>
    public static implicit operator SizeValue(string value) => new(value);
}

/// <summary>Optional margins around an overlay.</summary>
public sealed class OverlayMargin
{
    /// <summary>Top margin in rows.</summary>
    public int? Top { get; init; }

    /// <summary>Right margin in columns.</summary>
    public int? Right { get; init; }

    /// <summary>Bottom margin in rows.</summary>
    public int? Bottom { get; init; }

    /// <summary>Left margin in columns.</summary>
    public int? Left { get; init; }
}

/// <summary>Options controlling an overlay's size, position, visibility, and focus capture.</summary>
public sealed class OverlayOptions
{
    /// <summary>Width in columns or percentage of terminal width.</summary>
    public SizeValue? Width { get; init; }

    /// <summary>Minimum width in columns.</summary>
    public int? MinWidth { get; init; }

    /// <summary>Maximum height in rows or percentage of terminal height.</summary>
    public SizeValue? MaxHeight { get; init; }

    /// <summary>Anchor used when row and column are not explicit.</summary>
    public OverlayAnchor? Anchor { get; init; }

    /// <summary>Horizontal offset from the resolved anchor.</summary>
    public int? OffsetX { get; init; }

    /// <summary>Vertical offset from the resolved anchor.</summary>
    public int? OffsetY { get; init; }

    /// <summary>Absolute or percentage row position.</summary>
    public SizeValue? Row { get; init; }

    /// <summary>Absolute or percentage column position.</summary>
    public SizeValue? Col { get; init; }

    /// <summary>Uniform numeric margin or an <see cref="OverlayMargin"/>.</summary>
    public object? Margin { get; init; }

    /// <summary>Visibility predicate evaluated against the current terminal dimensions.</summary>
    public Func<int, int, bool>? Visible { get; init; }

    /// <summary>Prevents the overlay from capturing keyboard focus.</summary>
    public bool NonCapturing { get; init; }
}

/// <summary>Explicit focus target used when an overlay releases focus.</summary>
public sealed class OverlayUnfocusOptions
{
    /// <summary>Component to focus after releasing the overlay.</summary>
    public IComponent? Target { get; init; }
}

/// <summary>Controls one overlay entry returned by <see cref="TuiBase.ShowOverlay"/>.</summary>
public interface OverlayHandle
{
    /// <summary>Permanently removes the overlay.</summary>
    void Hide();

    /// <summary>Temporarily hides or shows the overlay.</summary>
    void SetHidden(bool hidden);

    /// <summary>Returns whether the overlay is temporarily hidden.</summary>
    bool IsHidden();

    /// <summary>Focuses the overlay and moves it to the visual front.</summary>
    void Focus();

    /// <summary>Releases focus to the next eligible overlay or prior target.</summary>
    void Unfocus(OverlayUnfocusOptions? options = null);

    /// <summary>Returns whether this overlay currently owns focus.</summary>
    bool IsFocused();
}

/// <summary>Regular or alternate-screen TUI mode.</summary>
public enum TuiMode
{
    /// <summary>Inline terminal mode.</summary>
    Regular,

    /// <summary>Full-screen terminal mode.</summary>
    Fullscreen,
}

/// <summary>Options controlling whether a TUI stop leaves terminal output in place.</summary>
public sealed class TuiStopOptions
{
    /// <summary>Leaves renderer output for another TUI to reuse.</summary>
    public bool PreserveScreen { get; init; }
}

/// <summary>Options controlling a terminal capability query timeout.</summary>
public sealed class TuiQueryOptions
{
    /// <summary>Maximum wait in milliseconds.</summary>
    public int TimeoutMs { get; init; }
}

/// <summary>Common TUI surface exposed to components and application code.</summary>
public interface ITui : IComponent
{
    /// <summary>Current TUI mode.</summary>
    TuiMode Mode { get; }

    /// <summary>Terminal used for rendering and input.</summary>
    ITerminal Terminal { get; }

    /// <summary>Mounted child components in insertion order.</summary>
    IReadOnlyList<IComponent> Children { get; }

    /// <summary>Optional Shift+Ctrl+D debug callback.</summary>
    Action? OnDebug { get; set; }

    /// <summary>Number of full redraws performed by the renderer.</summary>
    int FullRedraws { get; }

    /// <summary>Gets whether hardware-cursor placement is enabled.</summary>
    bool GetShowHardwareCursor();

    /// <summary>Enables or disables hardware-cursor placement.</summary>
    void SetShowHardwareCursor(bool enabled);

    /// <summary>Gets whether content shrink clears stale rows.</summary>
    bool GetClearOnShrink();

    /// <summary>Sets whether content shrink clears stale rows.</summary>
    void SetClearOnShrink(bool enabled);

    /// <summary>Adds a mounted child component.</summary>
    void AddChild(IComponent component);

    /// <summary>Removes a mounted child component.</summary>
    void RemoveChild(IComponent component);

    /// <summary>Removes all mounted children.</summary>
    void Clear();

    /// <summary>Sets the focused component.</summary>
    void SetFocus(IComponent? component);

    /// <summary>Shows an overlay.</summary>
    OverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null);

    /// <summary>Hides the topmost overlay.</summary>
    void HideOverlay();

    /// <summary>Returns whether a visible overlay exists.</summary>
    bool HasOverlay();

    /// <summary>Starts terminal input and rendering.</summary>
    void Start();

    /// <summary>Stops terminal input and rendering.</summary>
    void Stop(TuiStopOptions? options = null);

    /// <summary>Renders immediately.</summary>
    void RenderNow(bool force = false);

    /// <summary>Requests a throttled render.</summary>
    void RequestRender(bool force = false);

    /// <summary>Adds an input listener and returns an unsubscribe action.</summary>
    Action AddInputListener(TuiInputListener listener);

    /// <summary>Removes an input listener.</summary>
    void RemoveInputListener(TuiInputListener listener);

    /// <summary>Adds a terminal color-scheme listener.</summary>
    Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener);

    /// <summary>Enables or disables terminal color-scheme notifications.</summary>
    void SetTerminalColorSchemeNotifications(bool enabled);

    /// <summary>Queries the terminal's default background.</summary>
    Task<RgbColor?> QueryTerminalBackgroundColor(int timeoutMs);

    /// <summary>Queries the terminal's default background with options.</summary>
    Task<RgbColor?> QueryTerminalBackgroundColor(TuiQueryOptions options);

    /// <summary>Queries the terminal's color scheme.</summary>
    Task<TerminalColorScheme?> QueryTerminalColorScheme(int timeoutMs);

    /// <summary>Queries the terminal's color scheme with options.</summary>
    Task<TerminalColorScheme?> QueryTerminalColorScheme(TuiQueryOptions options);
}

/// <summary>Compatibility interface name matching upstream's TUI type.</summary>
public interface TUI : ITui
{
}

/// <summary>Marker interface for a TUI that exposes an alternate layout root.</summary>
public interface IViewportTui : ITui
{
    /// <summary>Whether this TUI carries the viewport marker.</summary>
    bool IsViewportTui { get; }

    /// <summary>Sets the optional layout root.</summary>
    void SetLayoutRoot(IComponent? component);
}

/// <summary>Compatibility name matching the upstream viewport TUI contract.</summary>
public interface ViewportTUI : IViewportTui
{
}

/// <summary>Viewport-TUI type and marker helpers.</summary>
public static class ViewportTuiUtilities
{
    /// <summary>Stable textual equivalent of the upstream global symbol marker.</summary>
    public const string Marker = "@earendil-works/pi-tui/viewport";

    /// <summary>Returns true when the supplied TUI exposes the viewport marker.</summary>
    public static bool IsViewportTUI(ITui tui)
    {
        ArgumentNullException.ThrowIfNull(tui);
        return tui is IViewportTui { IsViewportTui: true };
    }
}

/// <summary>Static helpers from the upstream TUI module.</summary>
public static class TuiUtilities
{
    /// <summary>Cursor marker emitted by focused components.</summary>
    public const string CursorMarker = TuiConstants.CursorMarker;

    /// <summary>Returns visible terminal-cell width.</summary>
    public static int VisibleWidth(string text) => TextMeasurement.VisibleWidth(text);

    /// <summary>Returns whether a component exposes mutable focus state.</summary>
    public static bool IsFocusable(IComponent? component) => component is IFocusable;

    /// <summary>Composites an overlay line into a base line at a terminal-cell column.</summary>
    public static string CompositeTuiLine(
        string baseLine,
        string overlayLine,
        int startColumn,
        int overlayWidth,
        int totalWidth)
    {
        ArgumentNullException.ThrowIfNull(baseLine);
        ArgumentNullException.ThrowIfNull(overlayLine);
        return TerminalImage.IsImageLine(baseLine)
            ? baseLine
            : TextMeasurement.CompositeTuiLine(baseLine, overlayLine, startColumn, overlayWidth, totalWidth);
    }
}

/// <summary>Core TUI implementation with focus, overlays, input dispatch, and render scheduling.</summary>
public abstract class TuiBase : Container, TUI, IDisposable
{
    private const string _segmentReset = "\x1b[0m\x1b]8;;\x07";
    private const int _minimumRenderIntervalMilliseconds = 16;
    private static readonly Regex _sizePercentage = new(
        "^(\\d+(?:\\.\\d+)?)%$",
        RegexOptions.CultureInvariant);
    private readonly object _renderGate = new();
    private readonly object _renderExecutionGate = new();
    private readonly List<TuiInputListener> _inputListeners = [];
    private readonly List<Action<TerminalColorScheme>> _terminalColorSchemeListeners = [];
    private readonly List<OverlayEntry> _overlayStack = [];
    private readonly ITerminalImageSeam _imageSeam;
    private readonly DifferentialRenderer _differentialRenderer;
    /// <summary>Directory receiving optional redraw diagnostics.</summary>
    protected readonly string logDirectory;
    private readonly List<PendingOsc11Query> _pendingOsc11Queries = [];
    private IComponent? _focusedComponent;
    private Timer? _renderTimer;
    private bool _renderRequested;
    private bool _immediateRenderScheduled;
    private bool _forceFullRedraw;
    private long _lastRenderTimestamp;
    private long _focusOrderCounter;
    /// <summary>Number of full redraws emitted by the screen renderer.</summary>
    protected int fullRedrawCount;
    private int _pendingOsc11BackgroundReplies;
    private bool _showHardwareCursor;
    private bool _clearOnShrink;
    private bool _terminalColorSchemeNotificationsEnabled;
    private OverlayFocusRestore? _overlayFocusRestore;

    /// <summary>Initializes the TUI around a terminal.</summary>
    protected TuiBase(
        ITerminal terminal,
        bool? showHardwareCursor = null,
        string? logDirectory = null,
        ITerminalImageSeam? imageSeam = null)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _imageSeam = imageSeam ?? new NoImageTerminalImageSeam();
        this.logDirectory = logDirectory ?? Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
        _showHardwareCursor = showHardwareCursor ?? Environment.GetEnvironmentVariable("PI_HARDWARE_CURSOR") == "1";
        _clearOnShrink = Environment.GetEnvironmentVariable("PI_CLEAR_ON_SHRINK") == "1";
        _differentialRenderer = new DifferentialRenderer(Terminal.Write);
        _lastRenderTimestamp = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public abstract TuiMode Mode { get; }

    /// <inheritdoc />
    public ITerminal Terminal { get; }

    /// <inheritdoc />
    public Action? OnDebug { get; set; }

    /// <summary>Whether the overlay stack contains entries, including hidden entries.</summary>
    public bool HasOverlayEntries => _overlayStack.Count > 0;

    /// <inheritdoc />
    public int FullRedraws => fullRedrawCount;

    /// <summary>Gets the currently focused component.</summary>
    public IComponent? GetFocusedComponent() => _focusedComponent;

    /// <summary>Gets whether hardware-cursor placement is enabled.</summary>
    public bool GetShowHardwareCursor() => _showHardwareCursor;

    /// <summary>Enables or disables hardware-cursor placement.</summary>
    public void SetShowHardwareCursor(bool enabled)
    {
        if (_showHardwareCursor == enabled)
        {
            return;
        }

        _showHardwareCursor = enabled;
        if (!enabled)
        {
            Terminal.HideCursor();
        }

        RequestRender();
    }

    /// <summary>Gets whether a content shrink triggers a full clearing redraw.</summary>
    public bool GetClearOnShrink() => _clearOnShrink;

    /// <summary>Sets whether a content shrink triggers a full clearing redraw.</summary>
    public void SetClearOnShrink(bool enabled) => _clearOnShrink = enabled;

    /// <inheritdoc />
    public override void AddChild(IComponent component)
    {
        lock (_renderExecutionGate)
        {
            base.AddChild(component);
        }
    }

    /// <inheritdoc />
    public override void RemoveChild(IComponent component)
    {
        lock (_renderExecutionGate)
        {
            base.RemoveChild(component);
        }
    }

    /// <inheritdoc />
    public override void Clear()
    {
        lock (_renderExecutionGate)
        {
            base.Clear();
        }
    }

    /// <summary>Hook implemented by the screen-specific TUI to produce one render frame.</summary>
    protected abstract void DoRender();

    /// <summary>Resets renderer state before a forced render.</summary>
    protected virtual void ResetRenderState()
    {
        _differentialRenderer.Reset();
        _forceFullRedraw = true;
    }

    /// <summary>Hook before terminal start.</summary>
    protected virtual void BeforeTerminalStart() { }

    /// <summary>Hook after terminal start.</summary>
    protected virtual void AfterTerminalStart() { }

    /// <summary>Hook before terminal stop.</summary>
    protected virtual void BeforeTerminalStop(TuiStopOptions options) { }

    /// <summary>Hook after terminal stop.</summary>
    protected virtual void AfterTerminalStop(TuiStopOptions options) { }

    /// <summary>Returns mounted roots used by focus restoration and rendering.</summary>
    protected virtual IReadOnlyList<IComponent> GetMountedRoots() => Children;

    private bool IsComponentMounted(IComponent component) =>
        GetMountedRoots().Any(root => ContainsComponent(root, component));

    private static bool ContainsComponent(IComponent root, IComponent target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return root is Container container &&
            container.Children.Any(child => ContainsComponent(child, target));
    }

    /// <summary>Renders mounted children, composites overlays, and uses the differential renderer.</summary>
    protected DifferentialRenderResult RenderFrame(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var composed = CompositeOverlays(lines, Terminal.Columns, Terminal.Rows).ToArray();
        var resetLines = ApplyLineResets(composed);
        var options = new DifferentialRenderOptions
        {
            ForceFullRedraw = _forceFullRedraw,
            ClearOnShrink = _clearOnShrink,
            ShowHardwareCursor = _showHardwareCursor,
        };
        var result = _differentialRenderer.Render(resetLines, Terminal.Columns, Terminal.Rows, options);
        _forceFullRedraw = false;
        fullRedrawCount = _differentialRenderer.FullRedrawCount;
        WriteDebugLog(result);
        return result;
    }

    /// <summary>Returns the flattened render lines for mounted child components.</summary>
    protected IReadOnlyList<string> RenderMountedChildren()
    {
        var lines = new List<string>();
        foreach (var child in GetMountedRoots())
        {
            lines.AddRange(child.Render(Terminal.Columns));
        }

        return lines;
    }

    /// <summary>Sets focus and applies overlay focus restoration policy.</summary>
    public void SetFocus(IComponent? component) =>
        SetFocusInternal(component, OverlayFocusRestorePolicy.Clear);

    private void SetFocusInternal(IComponent? component, OverlayFocusRestorePolicy policy)
    {
        var previousFocus = _focusedComponent;
        var nextFocus = component;
        var previousFocusedOverlay = previousFocus is null
            ? null
            : _overlayStack.FirstOrDefault(entry => ReferenceEquals(entry.Component, previousFocus) && IsOverlayVisible(entry));
        var nextFocusIsOverlay = nextFocus is not null &&
            _overlayStack.Any(entry => ReferenceEquals(entry.Component, nextFocus));
        var restoreState = GetVisibleOverlayFocusRestore();

        if (nextFocus is not null && !nextFocusIsOverlay)
        {
            if (restoreState is { IsBlocked: true } && ReferenceEquals(restoreState.BlockedBy, previousFocus))
            {
                if (restoreState.ResumeKind == ResumeKind.FocusTarget || !IsComponentMounted(restoreState.BlockedBy!))
                {
                    nextFocus = ResolveBlockedOverlayFocusResume(restoreState);
                }
                else
                {
                    _overlayFocusRestore = restoreState with { BlockedBy = nextFocus };
                }
            }
            else if (previousFocusedOverlay is not null &&
                     restoreState is not null &&
                     ReferenceEquals(restoreState.Overlay, previousFocusedOverlay) &&
                     !IsOverlayFocusAncestor(previousFocusedOverlay, nextFocus))
            {
                _overlayFocusRestore = new OverlayFocusRestore(
                    previousFocusedOverlay,
                    true,
                    nextFocus,
                    ResumeKind.RestoreOverlay,
                    null);
            }
        }
        else if (nextFocus is null)
        {
            if (restoreState is { IsBlocked: true } && ReferenceEquals(restoreState.BlockedBy, previousFocus))
            {
                nextFocus = ResolveBlockedOverlayFocusResume(restoreState);
            }
            else if (policy == OverlayFocusRestorePolicy.Clear)
            {
                ClearOverlayFocusRestore();
            }
        }

        if (_focusedComponent is IFocusable previousFocusable)
        {
            previousFocusable.Focused = false;
        }

        _focusedComponent = nextFocus;
        if (_focusedComponent is IFocusable nextFocusable)
        {
            nextFocusable.Focused = true;
        }

        var focusedOverlay = nextFocus is null
            ? null
            : _overlayStack.FirstOrDefault(entry => ReferenceEquals(entry.Component, nextFocus) && IsOverlayVisible(entry));
        if (focusedOverlay is not null)
        {
            _overlayFocusRestore = new OverlayFocusRestore(focusedOverlay, false, null, ResumeKind.RestoreOverlay, null);
        }
    }

    private void ClearOverlayFocusRestore() => _overlayFocusRestore = null;

    private void ClearOverlayFocusRestoreFor(OverlayEntry overlay)
    {
        if (_overlayFocusRestore is not null && ReferenceEquals(_overlayFocusRestore.Overlay, overlay))
        {
            ClearOverlayFocusRestore();
        }
    }

    private IComponent? ResolveBlockedOverlayFocusResume(OverlayFocusRestore restoreState)
    {
        if (restoreState.ResumeKind == ResumeKind.RestoreOverlay)
        {
            return restoreState.Overlay.Component;
        }

        ClearOverlayFocusRestore();
        return restoreState.ResumeTarget;
    }

    private OverlayFocusRestore? GetVisibleOverlayFocusRestore()
    {
        var restoreState = _overlayFocusRestore;
        if (restoreState is null || !_overlayStack.Contains(restoreState.Overlay) || !IsOverlayVisible(restoreState.Overlay))
        {
            return null;
        }

        return restoreState;
    }

    private bool IsOverlayFocusAncestor(OverlayEntry entry, IComponent component)
    {
        var visited = new HashSet<IComponent>(ReferenceEqualityComparer.Instance);
        var current = entry.PreFocus;
        while (current is not null && visited.Add(current))
        {
            if (ReferenceEquals(current, component))
            {
                return true;
            }

            current = _overlayStack.FirstOrDefault(overlay => ReferenceEquals(overlay.Component, current))?.PreFocus;
        }

        return false;
    }

    private void RetargetOverlayPreFocus(OverlayEntry removed)
    {
        foreach (var overlay in _overlayStack)
        {
            if (!ReferenceEquals(overlay, removed) && ReferenceEquals(overlay.PreFocus, removed.Component))
            {
                overlay.PreFocus = removed.PreFocus;
            }
        }
    }

    /// <inheritdoc />
    public OverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var entry = new OverlayEntry
        {
            Component = component,
            Options = options,
            PreFocus = _focusedComponent,
            FocusOrder = ++_focusOrderCounter,
        };
        _overlayStack.Add(entry);

        if (options?.NonCapturing != true && IsOverlayVisible(entry))
        {
            SetFocus(component);
        }

        Terminal.HideCursor();
        RequestRender();

        return new DelegateOverlayHandle(
            hide: () =>
            {
                var index = _overlayStack.IndexOf(entry);
                if (index < 0)
                {
                    return;
                }

                ClearOverlayFocusRestoreFor(entry);
                RetargetOverlayPreFocus(entry);
                _overlayStack.RemoveAt(index);
                if (ReferenceEquals(_focusedComponent, component))
                {
                    var topVisible = GetTopmostVisibleOverlay();
                    SetFocus(topVisible?.Component ?? entry.PreFocus);
                }

                if (_overlayStack.Count == 0)
                {
                    Terminal.HideCursor();
                }

                RequestRender();
            },
            setHidden: hidden =>
            {
                if (entry.Hidden == hidden)
                {
                    return;
                }

                entry.Hidden = hidden;
                if (hidden)
                {
                    ClearOverlayFocusRestoreFor(entry);
                    if (ReferenceEquals(_focusedComponent, component))
                    {
                        var topVisible = GetTopmostVisibleOverlay();
                        SetFocus(topVisible?.Component ?? entry.PreFocus);
                    }
                }
                else if (options?.NonCapturing != true && IsOverlayVisible(entry))
                {
                    entry.FocusOrder = ++_focusOrderCounter;
                    SetFocus(component);
                }

                RequestRender();
            },
            isHidden: () => entry.Hidden,
            focus: () =>
            {
                if (!_overlayStack.Contains(entry) || !IsOverlayVisible(entry))
                {
                    return;
                }

                entry.FocusOrder = ++_focusOrderCounter;
                SetFocus(component);
                RequestRender();
            },
            unfocus: optionsToUse =>
            {
                var isFocused = ReferenceEquals(_focusedComponent, component);
                var restoreState = _overlayFocusRestore;
                var hasPendingRestore = restoreState is not null && ReferenceEquals(restoreState.Overlay, entry);
                if (!isFocused && !hasPendingRestore)
                {
                    return;
                }

                if (restoreState is { IsBlocked: true } &&
                    ReferenceEquals(restoreState.Overlay, entry) &&
                    ReferenceEquals(_focusedComponent, restoreState.BlockedBy))
                {
                    if (optionsToUse is not null)
                    {
                        _overlayFocusRestore = restoreState with
                        {
                            ResumeKind = ResumeKind.FocusTarget,
                            ResumeTarget = optionsToUse.Target,
                        };
                    }
                    else
                    {
                        ClearOverlayFocusRestore();
                    }

                    RequestRender();
                    return;
                }

                ClearOverlayFocusRestoreFor(entry);
                if (isFocused || optionsToUse is not null)
                {
                    var topVisible = GetTopmostVisibleOverlay();
                    var fallback = topVisible is not null && !ReferenceEquals(topVisible, entry)
                        ? topVisible.Component
                        : entry.PreFocus;
                    SetFocus(optionsToUse?.Target ?? fallback);
                }

                RequestRender();
            },
            isFocused: () => ReferenceEquals(_focusedComponent, component));
    }

    /// <inheritdoc />
    public void HideOverlay()
    {
        if (_overlayStack.Count == 0)
        {
            return;
        }

        var overlay = _overlayStack[^1];
        ClearOverlayFocusRestoreFor(overlay);
        RetargetOverlayPreFocus(overlay);
        _overlayStack.RemoveAt(_overlayStack.Count - 1);
        if (ReferenceEquals(_focusedComponent, overlay.Component))
        {
            var topVisible = GetTopmostVisibleOverlay();
            SetFocus(topVisible?.Component ?? overlay.PreFocus);
        }

        if (_overlayStack.Count == 0)
        {
            Terminal.HideCursor();
        }

        RequestRender();
    }

    /// <inheritdoc />
    public bool HasOverlay() => _overlayStack.Any(IsOverlayVisible);

    /// <summary>Returns whether the focused component is a visible overlay.</summary>
    protected bool IsOverlayFocused() =>
        _overlayStack.Any(entry => ReferenceEquals(entry.Component, _focusedComponent) && IsOverlayVisible(entry));

    private bool IsOverlayVisible(OverlayEntry entry)
    {
        if (entry.Hidden)
        {
            return false;
        }

        return entry.Options?.Visible?.Invoke(Terminal.Columns, Terminal.Rows) ?? true;
    }

    private OverlayEntry? GetTopmostVisibleOverlay()
    {
        OverlayEntry? topmost = null;
        foreach (var overlay in _overlayStack)
        {
            if (overlay.Options?.NonCapturing == true || !IsOverlayVisible(overlay))
            {
                continue;
            }

            if (topmost is null || overlay.FocusOrder > topmost.FocusOrder)
            {
                topmost = overlay;
            }
        }

        return topmost;
    }

    /// <inheritdoc />
    public override void Invalidate()
    {
        foreach (var root in GetMountedRoots())
        {
            root.Invalidate();
        }

        foreach (var overlay in _overlayStack)
        {
            overlay.Component.Invalidate();
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        stopped = false;
        BeforeTerminalStart();
        Terminal.Start(HandleTerminalInput, () => RequestRender());
        AfterTerminalStart();
        Terminal.HideCursor();
        if (_terminalColorSchemeNotificationsEnabled)
        {
            Terminal.Write("\x1b[?2031h");
        }

        QueryCellSize();
        RequestRender();
    }

    /// <inheritdoc />
    public Action AddInputListener(TuiInputListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!_inputListeners.Contains(listener))
        {
            _inputListeners.Add(listener);
        }

        return () => _inputListeners.Remove(listener);
    }

    /// <inheritdoc />
    public void RemoveInputListener(TuiInputListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _inputListeners.Remove(listener);
    }

    /// <inheritdoc />
    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!_terminalColorSchemeListeners.Contains(listener))
        {
            _terminalColorSchemeListeners.Add(listener);
        }

        return () => _terminalColorSchemeListeners.Remove(listener);
    }

    /// <inheritdoc />
    public void SetTerminalColorSchemeNotifications(bool enabled)
    {
        if (_terminalColorSchemeNotificationsEnabled == enabled)
        {
            return;
        }

        _terminalColorSchemeNotificationsEnabled = enabled;
        if (!stopped)
        {
            Terminal.Write(enabled ? "\x1b[?2031h" : "\x1b[?2031l");
        }
    }

    /// <inheritdoc />
    public void Stop(TuiStopOptions? options = null)
    {
        options ??= new TuiStopOptions();
        stopped = true;
        CancelRenderTimer();
        lock (_renderExecutionGate)
        {
            if (_terminalColorSchemeNotificationsEnabled)
            {
                Terminal.Write("\x1b[?2031l");
            }

            BeforeTerminalStop(options);
            Terminal.ShowCursor();
            Terminal.Stop();
            AfterTerminalStop(options);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void RenderNow(bool force = false)
    {
        if (force)
        {
            ResetRenderState();
        }

        lock (_renderGate)
        {
            _renderRequested = false;
            CancelRenderTimerLocked();
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
        }

        ExecuteRender();
    }

    /// <inheritdoc />
    public void RequestRender(bool force = false)
    {
        if (force)
        {
            ResetRenderState();
            RequestImmediateRender();
            return;
        }

        lock (_renderGate)
        {
            if (_renderRequested)
            {
                return;
            }

            _renderRequested = true;
        }

        ThreadPool.QueueUserWorkItem(static state => ((TuiBase)state!).ScheduleRender(), this);
    }

    /// <inheritdoc />
    public Task<RgbColor?> QueryTerminalBackgroundColor(int timeoutMs)
    {
        var query = new PendingOsc11Query();
        query.Timer = new Timer(
            OnBackgroundQueryTimer,
            query,
            Math.Max(0, timeoutMs),
            Timeout.Infinite);
        lock (_renderGate)
        {
            _pendingOsc11Queries.Add(query);
            _pendingOsc11BackgroundReplies++;
        }

        Terminal.Write("\x1b]11;?\x07");
        return query.Completion.Task;
    }

    /// <inheritdoc />
    public Task<RgbColor?> QueryTerminalBackgroundColor(TuiQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return QueryTerminalBackgroundColor(options.TimeoutMs);
    }

    /// <inheritdoc />
    public Task<TerminalColorScheme?> QueryTerminalColorScheme(int timeoutMs)
    {
        var completion = new TaskCompletionSource<TerminalColorScheme?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = 0;
        Action? unsubscribe = null;
        void Settle(TerminalColorScheme? scheme)
        {
            if (Interlocked.Exchange(ref settled, 1) != 0)
            {
                return;
            }

            unsubscribe?.Invoke();
            completion.TrySetResult(scheme);
        }

        unsubscribe = OnTerminalColorSchemeChange(scheme => Settle(scheme));
        _ = new Timer(
            static state => ((Action)state!).Invoke(),
            (Action)(() => Settle(null)),
            Math.Max(0, timeoutMs),
            Timeout.Infinite);
        Terminal.Write("\x1b[?996n");
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<TerminalColorScheme?> QueryTerminalColorScheme(TuiQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return QueryTerminalColorScheme(options.TimeoutMs);
    }

    /// <summary>Returns the current visual cursor marker position and removes it from a line list.</summary>
    protected static CursorPosition? ExtractCursorPosition(IList<string> lines, int height)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var viewportTop = Math.Max(0, lines.Count - Math.Max(0, height));
        for (var row = lines.Count - 1; row >= viewportTop; row--)
        {
            var line = lines[row];
            var markerIndex = line.IndexOf(TuiConstants.CursorMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var beforeMarker = line[..markerIndex];
            lines[row] = string.Concat(
                line.AsSpan(0, markerIndex),
                line.AsSpan(markerIndex + TuiConstants.CursorMarker.Length));
            return new CursorPosition(row, TextMeasurement.VisibleWidth(beforeMarker));
        }

        return null;
    }

    /// <summary>Composites all visible overlays into a line buffer.</summary>
    protected IReadOnlyList<string> CompositeOverlays(
        IReadOnlyList<string> lines,
        int terminalWidth,
        int terminalHeight)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_overlayStack.Count == 0)
        {
            return lines;
        }

        var result = lines.ToList();
        var rendered = new List<RenderedOverlay>();
        var minLinesNeeded = result.Count;
        var visibleEntries = _overlayStack.Where(IsOverlayVisible).OrderBy(entry => entry.FocusOrder).ToArray();
        foreach (var entry in visibleEntries)
        {
            var (width, _, _, maxHeight) = ResolveOverlayLayout(entry.Options, 0, terminalWidth, terminalHeight);
            var overlayLines = entry.Component.Render(width).ToList();
            if (maxHeight is not null && overlayLines.Count > maxHeight.Value)
            {
                overlayLines = overlayLines.Take(maxHeight.Value).ToList();
            }

            var (resolvedWidth, row, col, _) = ResolveOverlayLayout(
                entry.Options,
                overlayLines.Count,
                terminalWidth,
                terminalHeight);
            rendered.Add(new RenderedOverlay(overlayLines, row, col, resolvedWidth));
            minLinesNeeded = Math.Max(minLinesNeeded, row + overlayLines.Count);
        }

        var workingHeight = Math.Max(Math.Max(result.Count, terminalHeight), minLinesNeeded);
        while (result.Count < workingHeight)
        {
            result.Add(string.Empty);
        }

        var viewportStart = Math.Max(0, workingHeight - terminalHeight);
        foreach (var overlay in rendered)
        {
            for (var index = 0; index < overlay.Lines.Count; index++)
            {
                var targetIndex = viewportStart + overlay.Row + index;
                if (targetIndex < 0 || targetIndex >= result.Count)
                {
                    continue;
                }

                var overlayLine = overlay.Lines[index];
                if (TextMeasurement.VisibleWidth(overlayLine) > overlay.Width)
                {
                    overlayLine = TextMeasurement.SliceByColumn(overlayLine, 0, overlay.Width, strict: true);
                }

                result[targetIndex] = TuiUtilities.CompositeTuiLine(
                    result[targetIndex],
                    overlayLine,
                    overlay.Column,
                    overlay.Width,
                    terminalWidth);
            }
        }

        return result;
    }

    /// <summary>Normalizes every non-image line and appends Pi's segment reset.</summary>
    protected static string[] ApplyLineResets(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = lines.ToArray();
        for (var index = 0; index < result.Length; index++)
        {
            if (!TerminalImage.IsImageLine(result[index]))
            {
                result[index] = TextMeasurement.NormalizeTerminalOutput(result[index]) + _segmentReset;
            }
        }

        return result;
    }

    /// <summary>Schedules a delayed render respecting the upstream 16ms minimum interval.</summary>
    private void ScheduleRender()
    {
        lock (_renderGate)
        {
            if (stopped || _renderTimer is not null || !_renderRequested)
            {
                return;
            }

            var elapsed = ElapsedMilliseconds(_lastRenderTimestamp);
            var delay = (int)Math.Max(0, Math.Ceiling(_minimumRenderIntervalMilliseconds - elapsed));
            _renderTimer = new Timer(static state => ((TuiBase)state!).RenderTimerFired(), this, delay, Timeout.Infinite);
        }
    }

    private void RequestImmediateRender()
    {
        lock (_renderGate)
        {
            CancelRenderTimerLocked();
            _renderRequested = true;
            if (_immediateRenderScheduled)
            {
                return;
            }

            _immediateRenderScheduled = true;
        }

        ThreadPool.QueueUserWorkItem(static state => ((TuiBase)state!).ImmediateRenderCallback(), this);
    }

    private void ImmediateRenderCallback()
    {
        lock (_renderGate)
        {
            _immediateRenderScheduled = false;
            if (stopped || !_renderRequested)
            {
                return;
            }

            CancelRenderTimerLocked();
            _renderRequested = false;
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
        }

        ExecuteRender();
    }

    private void RenderTimerFired()
    {
        lock (_renderGate)
        {
            _renderTimer?.Dispose();
            _renderTimer = null;
            if (stopped || !_renderRequested)
            {
                return;
            }

            _renderRequested = false;
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
        }

        ExecuteRender();
        lock (_renderGate)
        {
            if (!stopped && _renderRequested)
            {
                ScheduleRender();
            }
        }
    }

    private void CancelRenderTimer()
    {
        lock (_renderGate)
        {
            CancelRenderTimerLocked();
        }
    }

    private void ExecuteRender()
    {
        // Node's event loop never overlaps render callbacks. The .NET timer and immediate-input
        // paths use separate thread-pool work items, so serialize them to preserve that contract.
        lock (_renderExecutionGate)
        {
            DoRender();
        }
    }

    private void CancelRenderTimerLocked()
    {
        _renderTimer?.Dispose();
        _renderTimer = null;
    }

    private void HandleTerminalInput(string data)
    {
        lock (_renderExecutionGate)
        {
            HandleTerminalInputCore(data);
        }
    }

    private void HandleTerminalInputCore(string data)
    {
        if (ConsumeOsc11BackgroundResponse(data) || ConsumeTerminalColorSchemeReport(data))
        {
            return;
        }

        if (_inputListeners.Count > 0)
        {
            var current = data;
            foreach (var listener in _inputListeners.ToArray())
            {
                var listenerResult = listener(current);
                if (listenerResult?.Consume == true)
                {
                    return;
                }

                if (listenerResult?.Data is not null)
                {
                    current = listenerResult.Data;
                }
            }

            if (current.Length == 0)
            {
                return;
            }

            data = current;
        }

        if (ConsumeCellSizeResponse(data))
        {
            return;
        }

        if (Keys.MatchesKey(data, "shift+ctrl+d") && OnDebug is not null)
        {
            OnDebug();
            return;
        }

        var focusedOverlay = _overlayStack.FirstOrDefault(entry => ReferenceEquals(entry.Component, _focusedComponent));
        if (focusedOverlay is not null && !IsOverlayVisible(focusedOverlay))
        {
            var topVisible = GetTopmostVisibleOverlay();
            if (topVisible is not null)
            {
                SetFocus(topVisible.Component);
            }
            else
            {
                SetFocusInternal(focusedOverlay.PreFocus, OverlayFocusRestorePolicy.Preserve);
            }
        }

        var focusIsOverlay = _overlayStack.Any(entry => ReferenceEquals(entry.Component, _focusedComponent));
        if (!focusIsOverlay)
        {
            var restoreState = GetVisibleOverlayFocusRestore();
            if (restoreState is { IsBlocked: false })
            {
                SetFocus(restoreState.Overlay.Component);
            }
            else if (restoreState is { IsBlocked: true } &&
                     !ReferenceEquals(restoreState.BlockedBy, _focusedComponent))
            {
                if (restoreState.ResumeKind == ResumeKind.RestoreOverlay)
                {
                    SetFocus(restoreState.Overlay.Component);
                }
                else
                {
                    ClearOverlayFocusRestore();
                    SetFocus(restoreState.ResumeTarget);
                }
            }
        }

        if (_focusedComponent is null)
        {
            return;
        }

        if (Keys.IsKeyRelease(data) && !_focusedComponent.WantsKeyRelease)
        {
            return;
        }

        _focusedComponent.HandleInput(data);
        RequestImmediateRender();
    }

    private bool ConsumeOsc11BackgroundResponse(string data)
    {
        if (_pendingOsc11BackgroundReplies <= 0 || !TerminalColors.IsOsc11BackgroundColorResponse(data))
        {
            return false;
        }

        var rgb = TerminalColors.ParseOsc11BackgroundColor(data);
        _pendingOsc11BackgroundReplies--;
        PendingOsc11Query? query;
        lock (_renderGate)
        {
            query = _pendingOsc11Queries.Count == 0 ? null : _pendingOsc11Queries[0];
            if (query is not null)
            {
                _pendingOsc11Queries.RemoveAt(0);
            }
        }

        if (query is not null && Interlocked.Exchange(ref query.Settled, 1) == 0)
        {
            query.Timer?.Dispose();
            query.Completion.TrySetResult(rgb);
        }

        return true;
    }

    private bool ConsumeTerminalColorSchemeReport(string data)
    {
        var scheme = TerminalColors.ParseTerminalColorSchemeReport(data);
        if (scheme is null)
        {
            return false;
        }

        foreach (var listener in _terminalColorSchemeListeners.ToArray())
        {
            listener(scheme.Value);
        }

        return true;
    }

    private bool ConsumeCellSizeResponse(string data)
    {
        var match = Regex.Match(data, "^\\x1b\\[6;(\\d+);(\\d+)t$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var heightPx) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var widthPx))
        {
            return true;
        }

        if (heightPx <= 0 || widthPx <= 0)
        {
            return true;
        }

        _imageSeam.SetCellDimensions(new CellDimensions(widthPx, heightPx));
        Invalidate();
        RequestRender();
        return true;
    }

    private void QueryCellSize()
    {
        if (_imageSeam.GetCapabilities().Images is null)
        {
            return;
        }

        Terminal.Write("\x1b[16t");
    }

    private static void OnBackgroundQueryTimer(object? state)
    {
        TimeoutBackgroundQuery((PendingOsc11Query)state!);
    }

    private static void TimeoutBackgroundQuery(PendingOsc11Query query)
    {
        if (Interlocked.Exchange(ref query.Settled, 1) != 0)
        {
            return;
        }

        query.Timer = null;
        query.Completion.TrySetResult(null);
    }

    private static double ElapsedMilliseconds(long timestamp)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - timestamp;
        return elapsedTicks * 1000d / Stopwatch.Frequency;
    }

    private static (int Width, int Row, int Column, int? MaxHeight) ResolveOverlayLayout(
        OverlayOptions? options,
        int overlayHeight,
        int terminalWidth,
        int terminalHeight)
    {
        options ??= new OverlayOptions();
        var margin = ResolveMargin(options.Margin);
        var marginTop = Math.Max(0, margin.Top);
        var marginRight = Math.Max(0, margin.Right);
        var marginBottom = Math.Max(0, margin.Bottom);
        var marginLeft = Math.Max(0, margin.Left);
        var availableWidth = Math.Max(1, terminalWidth - marginLeft - marginRight);
        var availableHeight = Math.Max(1, terminalHeight - marginTop - marginBottom);

        var width = ToLayoutInteger(ParseSizeValue(options.Width, terminalWidth) ?? Math.Min(80, availableWidth));
        if (options.MinWidth is not null)
        {
            width = Math.Max(width, options.MinWidth.Value);
        }

        width = Math.Max(1, Math.Min(width, availableWidth));
        var parsedMaxHeight = ParseSizeValue(options.MaxHeight, terminalHeight);
        int? maxHeight = null;
        if (parsedMaxHeight is not null)
        {
            maxHeight = Math.Max(1, Math.Min(ToLayoutInteger(parsedMaxHeight.Value), availableHeight));
        }

        var effectiveHeight = maxHeight is null
            ? Math.Max(0, overlayHeight)
            : Math.Min(Math.Max(0, overlayHeight), maxHeight.Value);
        var anchor = options.Anchor ?? OverlayAnchor.Center;
        var row = ResolvePosition(
            options.Row,
            anchor,
            isRow: true,
            effectiveHeight,
            availableHeight,
            marginTop);
        var column = ResolvePosition(
            options.Col,
            anchor,
            isRow: false,
            width,
            availableWidth,
            marginLeft);

        row += options.OffsetY ?? 0;
        column += options.OffsetX ?? 0;
        row = Math.Max(marginTop, Math.Min(row, terminalHeight - marginBottom - effectiveHeight));
        column = Math.Max(marginLeft, Math.Min(column, terminalWidth - marginRight - width));
        return (width, row, column, maxHeight);
    }

    private static int ResolvePosition(
        SizeValue? explicitPosition,
        OverlayAnchor anchor,
        bool isRow,
        int size,
        int availableSize,
        int marginStart)
    {
        if (explicitPosition is not null)
        {
            var value = explicitPosition.Value;
            if (value.Percentage is not null)
            {
                var match = _sizePercentage.Match(value.Percentage);
                if (match.Success &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    var maxPosition = Math.Max(0, availableSize - size);
                    return marginStart + ToLayoutInteger(maxPosition * percent / 100d);
                }
            }
            else if (value.Absolute is not null)
            {
                return ToLayoutInteger(value.Absolute.Value);
            }

            return ResolveAnchorPosition(anchor, isRow, size, availableSize, marginStart);
        }

        return ResolveAnchorPosition(anchor, isRow, size, availableSize, marginStart);
    }

    private static int ResolveAnchorPosition(
        OverlayAnchor anchor,
        bool isRow,
        int size,
        int availableSize,
        int marginStart)
    {
        var value = anchor.Value;
        if (isRow)
        {
            return value switch
            {
                "top-left" or "top-center" or "top-right" => marginStart,
                "bottom-left" or "bottom-center" or "bottom-right" => marginStart + availableSize - size,
                _ => marginStart + (availableSize - size) / 2,
            };
        }

        return value switch
        {
            "top-left" or "left-center" or "bottom-left" => marginStart,
            "top-right" or "right-center" or "bottom-right" => marginStart + availableSize - size,
            _ => marginStart + (availableSize - size) / 2,
        };
    }

    private static double? ParseSizeValue(SizeValue? value, int referenceSize)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Value.Absolute is not null)
        {
            return value.Value.Absolute;
        }

        if (value.Value.Percentage is not null)
        {
            var match = _sizePercentage.Match(value.Value.Percentage);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return Math.Floor(referenceSize * percent / 100d);
            }
        }

        return null;
    }

    private static MarginValues ResolveMargin(object? value)
    {
        if (value is OverlayMargin margin)
        {
            return new MarginValues(margin.Top ?? 0, margin.Right ?? 0, margin.Bottom ?? 0, margin.Left ?? 0);
        }

        if (value is int integer)
        {
            return new MarginValues(integer, integer, integer, integer);
        }

        if (value is long longValue)
        {
            var integerValue = ToLayoutInteger(longValue);
            return new MarginValues(integerValue, integerValue, integerValue, integerValue);
        }

        if (value is double numeric)
        {
            var integerValue = ToLayoutInteger(numeric);
            return new MarginValues(integerValue, integerValue, integerValue, integerValue);
        }

        return new MarginValues(0, 0, 0, 0);
    }

    private static int ToLayoutInteger(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Floor(value), int.MinValue, int.MaxValue);
    }

    private void WriteDebugLog(DifferentialRenderResult result)
    {
        if (Environment.GetEnvironmentVariable("PI_DEBUG_REDRAW") != "1")
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(logDirectory);
            var reason = result.FullRedraw && fullRedrawCount == 1 ? "first render" :
                result.FullRedraw ? "full redraw" : "differential render";
            File.AppendAllText(
                Path.Combine(logDirectory, "pi-debug.log"),
                $"fullRender: {reason}{Environment.NewLine}");
        }
        catch
        {
            // Debug logging is best-effort and must never affect rendering.
        }
    }

    private sealed class OverlayEntry
    {
        internal required IComponent Component { get; init; }
        internal OverlayOptions? Options { get; init; }
        internal IComponent? PreFocus { get; set; }
        internal bool Hidden { get; set; }
        internal long FocusOrder { get; set; }
    }

    private sealed record OverlayFocusRestore(
        OverlayEntry Overlay,
        bool IsBlocked,
        IComponent? BlockedBy,
        ResumeKind ResumeKind,
        IComponent? ResumeTarget);

    private sealed record RenderedOverlay(
        IReadOnlyList<string> Lines,
        int Row,
        int Column,
        int Width);

    private sealed class PendingOsc11Query
    {
        internal int Settled;
        internal Timer? Timer { get; set; }
        internal TaskCompletionSource<RgbColor?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DelegateOverlayHandle(
        Action hide,
        Action<bool> setHidden,
        Func<bool> isHidden,
        Action focus,
        Action<OverlayUnfocusOptions?> unfocus,
        Func<bool> isFocused) : OverlayHandle
    {
        public void Hide() => hide();

        public void SetHidden(bool hidden) => setHidden(hidden);

        public bool IsHidden() => isHidden();

        public void Focus() => focus();

        public void Unfocus(OverlayUnfocusOptions? options = null) => unfocus(options);

        public bool IsFocused() => isFocused();
    }

    private readonly record struct MarginValues(int Top, int Right, int Bottom, int Left);

    private enum ResumeKind
    {
        RestoreOverlay,
        FocusTarget,
    }

    private enum OverlayFocusRestorePolicy
    {
        Clear,
        Preserve,
    }

    /// <summary>Whether the TUI has been stopped.</summary>
    protected bool stopped;
}

#pragma warning restore CA1711
