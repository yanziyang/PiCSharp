using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>A positioned text segment accepted by <see cref="Editor.WordWrapLine"/>.</summary>
/// <remarks>
/// This is the C# counterpart of the subset of <c>Intl.SegmentData</c> consumed by the editor.
/// It preserves the optional pre-segmented fast path without exposing a JavaScript-only type.
/// </remarks>
public readonly record struct EditorTextSegment(string Segment, int Index, string Input);

/// <summary>One word-wrapped text chunk and its UTF-16 range in the original line.</summary>
public sealed record TextChunk
{
    /// <summary>Chunk text.</summary>
    public required string Text { get; init; }

    /// <summary>Inclusive UTF-16 start index in the original line.</summary>
    public required int StartIndex { get; init; }

    /// <summary>Exclusive UTF-16 end index in the original line.</summary>
    public required int EndIndex { get; init; }
}

/// <summary>Styling callbacks used by <see cref="Editor"/>.</summary>
public sealed class EditorTheme
{
    /// <summary>Styles the editor's horizontal border.</summary>
    public required Func<string, string> BorderColor { get; init; }

    /// <summary>Styles the autocomplete selection list.</summary>
    public required SelectListTheme SelectList { get; init; }
}

/// <summary>Optional editor layout settings.</summary>
public sealed class EditorOptions
{
    /// <summary>Horizontal padding on each side of the editable content.</summary>
    public int? PaddingX { get; init; }

    /// <summary>Maximum number of autocomplete rows displayed at once.</summary>
    public int? AutocompleteMaxVisible { get; init; }
}

/// <summary>Multi-line terminal editor with history, undo, kill-ring, paste, and autocomplete support.</summary>
/// <remarks>
/// The class and its render/input/invalidation methods remain overridable because editor-replacing
/// extensions subclass this type and compose behavior by calling <c>base</c>.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "Cancellation sources are replaced and disposed by the upstream-equivalent autocomplete lifecycle.")]
public partial class Editor : IComponent, Focusable
{
    private const int _attachmentAutocompleteDebounceMs = 20;
    private static readonly string[] _defaultAutocompleteTriggerCharacters = ["@", "#"];
    private static readonly SelectListLayoutOptions _slashCommandSelectListLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 32,
    };

    private static readonly Regex _pasteMarkerRegex = new(
        @"\[paste #(\d+)( (\+\d+ lines|\d+ chars))?\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex _pasteMarkerSingleRegex = new(
        @"^\[paste #(\d+)( (\+\d+ lines|\d+ chars))?\]$",
        RegexOptions.CultureInvariant);
    private static readonly Regex _wordCharacterRegex = new(@"\w", RegexOptions.CultureInvariant);
    private static readonly Regex _typedAutocompleteCharacterRegex = new(
        @"[a-zA-Z0-9.\-_]",
        RegexOptions.CultureInvariant);
    private static readonly Regex _controlCsiURegex = new(
        "\\x1b\\[(\\d+);5u",
        RegexOptions.CultureInvariant);

    private EditorState _state = new([string.Empty], 0, 0);
    private int _paddingX;
    private int _lastWidth = 80;
    private int _scrollOffset;
    private IAutocompleteProvider? _autocompleteProvider;
    private IReadOnlyList<string> _autocompleteTriggerCharacters = [.. _defaultAutocompleteTriggerCharacters];
    private Regex _autocompleteTriggerPattern = BuildTriggerPattern(_defaultAutocompleteTriggerCharacters);
    private Regex _autocompleteDebouncePattern = BuildDebouncePattern(_defaultAutocompleteTriggerCharacters);
    private SelectList? _autocompleteList;
    private AutocompleteState? _autocompleteState;
    private string _autocompletePrefix = string.Empty;
    private int _autocompleteMaxVisible = 5;
    private CancellationTokenSource? _autocompleteAbort;
    private CancellationTokenSource? _autocompleteDebounce;
    private Task _autocompleteRequestTask = Task.CompletedTask;
    private int _autocompleteStartToken;
    private int _autocompleteRequestId;
    private Dictionary<int, string> _pastes = [];
    private int _pasteCounter;
    private string _pasteBuffer = string.Empty;
    private bool _isInPaste;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private EditorState? _historyDraft;
    private readonly KillRing _killRing = new();
    private LastAction? _lastAction;
    private JumpDirection? _jumpMode;
    private int? _preferredVisualCol;
    private int? _snappedFromCursorCol;
    private readonly UndoStack<EditorSnapshot> _undoStack = new(static snapshot => snapshot.Clone());

    /// <summary>Creates an editor attached to a TUI.</summary>
    public Editor(TUI tui, EditorTheme theme, EditorOptions? options = null)
    {
        Tui = tui ?? throw new ArgumentNullException(nameof(tui));
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        BorderColor = theme.BorderColor;
        options ??= new EditorOptions();
        _paddingX = ClampFiniteOption(options.PaddingX, 0, int.MaxValue, 0);
        _autocompleteMaxVisible = ClampFiniteOption(options.AutocompleteMaxVisible, 3, 20, 5);
    }

    /// <summary>TUI that owns this editor, available to extension subclasses.</summary>
    protected TUI Tui { get; }

    /// <summary>Editor theme, available to extension subclasses.</summary>
    protected EditorTheme Theme { get; }

    /// <inheritdoc />
    public bool Focused { get; set; }

    /// <summary>Current border styling callback.</summary>
    public Func<string, string> BorderColor { get; set; }

    /// <summary>Called with expanded, trimmed text after submission.</summary>
    public Action<string>? OnSubmit { get; set; }

    /// <summary>Called whenever the stored editor text changes.</summary>
    public Action<string>? OnChange { get; set; }

    /// <summary>Prevents submit-key handling while true.</summary>
    public bool DisableSubmit { get; set; }

    /// <summary>Gets horizontal editor padding.</summary>
    public int GetPaddingX() => _paddingX;

    /// <summary>Sets horizontal editor padding and requests a render when it changes.</summary>
    public void SetPaddingX(int padding)
    {
        var newPadding = Math.Max(0, padding);
        if (_paddingX == newPadding)
        {
            return;
        }

        _paddingX = newPadding;
        Tui.RequestRender();
    }

    /// <summary>Gets the maximum visible autocomplete item count.</summary>
    public int GetAutocompleteMaxVisible() => _autocompleteMaxVisible;

    /// <summary>Sets the maximum visible autocomplete item count and requests a render.</summary>
    public void SetAutocompleteMaxVisible(int maxVisible)
    {
        var newMaxVisible = Math.Max(3, Math.Min(20, maxVisible));
        if (_autocompleteMaxVisible == newMaxVisible)
        {
            return;
        }

        _autocompleteMaxVisible = newMaxVisible;
        Tui.RequestRender();
    }

    /// <summary>Sets the provider used for slash-command and file autocomplete.</summary>
    public void SetAutocompleteProvider(IAutocompleteProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        CancelAutocomplete();
        _autocompleteProvider = provider;
        SetAutocompleteTriggerCharacters(provider.TriggerCharacters ?? []);
    }

    /// <summary>Adds one non-empty prompt to the bounded history list.</summary>
    public void AddToHistory(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || _history.Count > 0 && _history[0] == trimmed)
        {
            return;
        }

        _history.Insert(0, trimmed);
        if (_history.Count > 100)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <inheritdoc />
    public virtual void Invalidate()
    {
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Render(int width)
    {
        var maxPadding = Math.Max(0, (int)Math.Floor((width - 1) / 2d));
        var paddingX = Math.Min(_paddingX, maxPadding);
        var contentWidth = Math.Max(1, width - (paddingX * 2));
        var layoutWidth = Math.Max(1, contentWidth - (paddingX != 0 ? 0 : 1));
        _lastWidth = layoutWidth;

        var horizontal = BorderColor("─");
        var layoutLines = LayoutText(layoutWidth);
        var maxVisibleLines = Math.Max(5, (int)Math.Floor(Tui.Terminal.Rows * 0.3d));
        var cursorLineIndex = layoutLines.FindIndex(static line => line.HasCursor);
        if (cursorLineIndex < 0)
        {
            cursorLineIndex = 0;
        }

        if (cursorLineIndex < _scrollOffset)
        {
            _scrollOffset = cursorLineIndex;
        }
        else if (cursorLineIndex >= _scrollOffset + maxVisibleLines)
        {
            _scrollOffset = cursorLineIndex - maxVisibleLines + 1;
        }

        var maxScrollOffset = Math.Max(0, layoutLines.Count - maxVisibleLines);
        _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScrollOffset));
        var visibleLines = layoutLines.Skip(_scrollOffset).Take(maxVisibleLines).ToArray();
        var result = new List<string>();
        var leftPadding = new string(' ', paddingX);
        var rightPadding = leftPadding;

        if (_scrollOffset > 0)
        {
            result.Add(BorderColor(CreateScrollBorder("↑", _scrollOffset, width)));
        }
        else
        {
            result.Add(string.Concat(Enumerable.Repeat(horizontal, width)));
        }

        foreach (var layoutLine in visibleLines)
        {
            var displayText = layoutLine.Text;
            var lineVisibleWidth = TextMeasurement.VisibleWidth(layoutLine.Text);
            var cursorInPadding = false;

            if (layoutLine.HasCursor && layoutLine.CursorPos is { } cursorPos)
            {
                var before = displayText[..cursorPos];
                var after = displayText[cursorPos..];
                var marker = Focused ? TuiConstants.CursorMarker : string.Empty;
                if (after.Length > 0)
                {
                    var graphemes = Segment(after, SegmentMode.Grapheme);
                    var firstGrapheme = graphemes.Count > 0 ? graphemes[0].Segment : string.Empty;
                    var restAfter = after[firstGrapheme.Length..];
                    displayText = before + marker + "\x1b[7m" + firstGrapheme + "\x1b[0m" + restAfter;
                }
                else
                {
                    displayText = before + marker + "\x1b[7m \x1b[0m";
                    lineVisibleWidth++;
                    if (lineVisibleWidth > contentWidth && paddingX > 0)
                    {
                        cursorInPadding = true;
                    }
                }
            }

            var padding = new string(' ', Math.Max(0, contentWidth - lineVisibleWidth));
            var lineRightPadding = cursorInPadding ? rightPadding[1..] : rightPadding;
            result.Add(leftPadding + displayText + padding + lineRightPadding);
        }

        var linesBelow = layoutLines.Count - (_scrollOffset + visibleLines.Length);
        if (linesBelow > 0)
        {
            result.Add(BorderColor(CreateScrollBorder("↓", linesBelow, width)));
        }
        else
        {
            result.Add(string.Concat(Enumerable.Repeat(horizontal, width)));
        }

        if (_autocompleteState is not null && _autocompleteList is not null)
        {
            foreach (var line in _autocompleteList.Render(contentWidth))
            {
                var linePadding = new string(' ', Math.Max(0, contentWidth - TextMeasurement.VisibleWidth(line)));
                result.Add(leftPadding + line + linePadding + rightPadding);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public virtual void HandleInput(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var keybindings = KeybindingsManager.GetKeybindings();

        if (_jumpMode is not null)
        {
            if (keybindings.Matches(data, "tui.editor.jumpForward") ||
                keybindings.Matches(data, "tui.editor.jumpBackward"))
            {
                _jumpMode = null;
                return;
            }

            var printableJump = Keys.DecodePrintableKey(data) ??
                (data.Length > 0 && data[0] >= 32 ? data : null);
            if (printableJump is not null)
            {
                var direction = _jumpMode.Value;
                _jumpMode = null;
                JumpToChar(printableJump, direction);
                return;
            }

            _jumpMode = null;
        }

        if (data.Contains("\x1b[200~", StringComparison.Ordinal))
        {
            _isInPaste = true;
            _pasteBuffer = string.Empty;
            data = data.Replace("\x1b[200~", string.Empty, StringComparison.Ordinal);
        }

        if (_isInPaste)
        {
            _pasteBuffer += data;
            var endIndex = _pasteBuffer.IndexOf("\x1b[201~", StringComparison.Ordinal);
            if (endIndex >= 0)
            {
                var pasteContent = _pasteBuffer[..endIndex];
                if (pasteContent.Length > 0)
                {
                    HandlePaste(pasteContent);
                }

                _isInPaste = false;
                var remaining = _pasteBuffer[(endIndex + 6)..];
                _pasteBuffer = string.Empty;
                if (remaining.Length > 0)
                {
                    HandleInput(remaining);
                }
            }

            return;
        }

        if (keybindings.Matches(data, "tui.input.copy"))
        {
            return;
        }

        if (keybindings.Matches(data, "tui.editor.undo"))
        {
            Undo();
            return;
        }

        if (_autocompleteState is not null && _autocompleteList is not null)
        {
            if (keybindings.Matches(data, "tui.select.cancel"))
            {
                CancelAutocomplete();
                return;
            }

            if (keybindings.Matches(data, "tui.select.up") || keybindings.Matches(data, "tui.select.down"))
            {
                _autocompleteList.HandleInput(data);
                return;
            }

            if (keybindings.Matches(data, "tui.input.tab"))
            {
                ApplySelectedAutocomplete(submitSlashCommand: false);
                return;
            }

            if (keybindings.Matches(data, "tui.select.confirm"))
            {
                var fallThroughToSubmit = ApplySelectedAutocomplete(submitSlashCommand: true);
                if (!fallThroughToSubmit)
                {
                    return;
                }
            }
        }

        if (keybindings.Matches(data, "tui.input.tab") && _autocompleteState is null)
        {
            HandleTabCompletion();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.deleteToLineEnd"))
        {
            DeleteToEndOfLine();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.deleteToLineStart"))
        {
            DeleteToStartOfLine();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.deleteWordBackward"))
        {
            DeleteWordBackwards();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.deleteWordForward"))
        {
            DeleteWordForward();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.deleteCharBackward") || Keys.MatchesKey(data, "shift+backspace"))
        {
            HandleBackspace();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.deleteCharForward") || Keys.MatchesKey(data, "shift+delete"))
        {
            HandleForwardDelete();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.yank"))
        {
            Yank();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.yankPop"))
        {
            YankPop();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.historyPrevious"))
        {
            CancelAutocomplete();
            NavigateHistory(-1);
            return;
        }

        if (keybindings.Matches(data, "tui.editor.historyNext"))
        {
            CancelAutocomplete();
            NavigateHistory(1);
            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorLineStart"))
        {
            MoveToLineStart();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorLineEnd"))
        {
            MoveToLineEnd();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorWordLeft"))
        {
            MoveWordBackwards();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorWordRight"))
        {
            MoveWordForwards();
            return;
        }

        if (IsNewLineInput(data, keybindings))
        {
            if (ShouldSubmitOnBackslashEnter(data, keybindings))
            {
                HandleBackspace();
                SubmitValue();
                return;
            }

            AddNewLine();
            return;
        }

        if (keybindings.Matches(data, "tui.input.submit"))
        {
            if (DisableSubmit)
            {
                return;
            }

            var currentLine = GetCurrentLine();
            if (_state.CursorCol > 0 && currentLine[_state.CursorCol - 1] == '\\')
            {
                HandleBackspace();
                AddNewLine();
                return;
            }

            SubmitValue();
            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorUp"))
        {
            if (IsOnFirstVisualLine() && (IsEditorEmpty() || _historyIndex > -1 || _state.CursorCol == 0))
            {
                NavigateHistory(-1);
            }
            else if (IsOnFirstVisualLine())
            {
                MoveToLineStart();
            }
            else
            {
                MoveCursor(-1, 0);
            }

            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorDown"))
        {
            if (_historyIndex > -1 && IsOnLastVisualLine())
            {
                NavigateHistory(1);
            }
            else if (IsOnLastVisualLine())
            {
                MoveToLineEnd();
            }
            else
            {
                MoveCursor(1, 0);
            }

            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorRight"))
        {
            MoveCursor(0, 1);
            return;
        }

        if (keybindings.Matches(data, "tui.editor.cursorLeft"))
        {
            MoveCursor(0, -1);
            return;
        }

        if (keybindings.Matches(data, "tui.editor.pageUp"))
        {
            PageScroll(-1);
            return;
        }

        if (keybindings.Matches(data, "tui.editor.pageDown"))
        {
            PageScroll(1);
            return;
        }

        if (keybindings.Matches(data, "tui.editor.jumpForward"))
        {
            _jumpMode = JumpDirection.Forward;
            return;
        }

        if (keybindings.Matches(data, "tui.editor.jumpBackward"))
        {
            _jumpMode = JumpDirection.Backward;
            return;
        }

        if (Keys.MatchesKey(data, "shift+space"))
        {
            InsertCharacter(" ");
            return;
        }

        var printable = Keys.DecodePrintableKey(data);
        if (printable is not null)
        {
            InsertCharacter(printable);
            return;
        }

        if (data.Length > 0 && data[0] >= 32)
        {
            InsertCharacter(data);
        }
    }

    /// <summary>Returns editor text with logical lines separated by newline characters.</summary>
    public string GetText() => string.Join('\n', _state.Lines);

    /// <summary>Returns editor text with large-paste markers expanded.</summary>
    public string GetExpandedText() => ExpandPasteMarkers(GetText());

    /// <summary>Returns a defensive copy of logical lines.</summary>
    public IReadOnlyList<string> GetLines() => [.. _state.Lines];

    /// <summary>Returns the logical cursor position.</summary>
    public (int Line, int Col) GetCursor() => (_state.CursorLine, _state.CursorCol);

    /// <summary>Replaces editor text, resets paste/history browsing state, and records undo.</summary>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CancelAutocomplete();
        _lastAction = null;
        ExitHistoryBrowsing();
        var normalized = NormalizeText(text);
        if (GetText() != normalized)
        {
            PushUndoSnapshot();
        }

        _pastes.Clear();
        _pasteCounter = 0;
        SetTextInternal(normalized);
    }

    /// <summary>Atomically inserts programmatic text at the current cursor position.</summary>
    public void InsertTextAtCursor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        CancelAutocomplete();
        PushUndoSnapshot();
        _lastAction = null;
        ExitHistoryBrowsing();
        InsertTextAtCursorInternal(text);
    }

    /// <summary>Returns whether an autocomplete picker is currently visible.</summary>
    public bool IsShowingAutocomplete() => _autocompleteState is not null;

    /// <summary>Splits a line into word-wrapped chunks with optional pre-segmented graphemes.</summary>
    public static IReadOnlyList<TextChunk> WordWrapLine(
        string line,
        int maxWidth,
        IReadOnlyList<EditorTextSegment>? preSegmented = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0 || maxWidth <= 0)
        {
            return [new TextChunk { Text = string.Empty, StartIndex = 0, EndIndex = 0 }];
        }

        if (TextMeasurement.VisibleWidth(line) <= maxWidth)
        {
            return [new TextChunk { Text = line, StartIndex = 0, EndIndex = line.Length }];
        }

        var chunks = new List<TextChunk>();
        var segments = preSegmented ?? SegmentGraphemes(line);
        var currentWidth = 0;
        var chunkStart = 0;
        var wrapOpportunityIndex = -1;
        var wrapOpportunityWidth = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var grapheme = segment.Segment;
            var graphemeWidth = TextMeasurement.VisibleWidth(grapheme);
            var characterIndex = segment.Index;
            var isWhitespace = !IsPasteMarker(grapheme) && TextMeasurement.IsWhitespaceChar(grapheme);

            if (currentWidth + graphemeWidth > maxWidth)
            {
                if (wrapOpportunityIndex >= 0 && currentWidth - wrapOpportunityWidth + graphemeWidth <= maxWidth)
                {
                    chunks.Add(CreateChunk(line, chunkStart, wrapOpportunityIndex));
                    chunkStart = wrapOpportunityIndex;
                    currentWidth -= wrapOpportunityWidth;
                }
                else if (chunkStart < characterIndex)
                {
                    chunks.Add(CreateChunk(line, chunkStart, characterIndex));
                    chunkStart = characterIndex;
                    currentWidth = 0;
                }

                wrapOpportunityIndex = -1;
            }

            if (graphemeWidth > maxWidth)
            {
                var subChunks = WordWrapLine(grapheme, maxWidth);
                for (var subIndex = 0; subIndex < subChunks.Count - 1; subIndex++)
                {
                    var subChunk = subChunks[subIndex];
                    chunks.Add(new TextChunk
                    {
                        Text = subChunk.Text,
                        StartIndex = characterIndex + subChunk.StartIndex,
                        EndIndex = characterIndex + subChunk.EndIndex,
                    });
                }

                var last = subChunks[^1];
                chunkStart = characterIndex + last.StartIndex;
                currentWidth = TextMeasurement.VisibleWidth(last.Text);
                wrapOpportunityIndex = -1;
                continue;
            }

            currentWidth += graphemeWidth;
            var hasNext = index + 1 < segments.Count;
            if (hasNext)
            {
                var next = segments[index + 1];
                if (isWhitespace && (IsPasteMarker(next.Segment) || !TextMeasurement.IsWhitespaceChar(next.Segment)))
                {
                    wrapOpportunityIndex = next.Index;
                    wrapOpportunityWidth = currentWidth;
                }
                else if (!isWhitespace && !TextMeasurement.IsWhitespaceChar(next.Segment) &&
                         (IsCjkBreak(grapheme) || IsCjkBreak(next.Segment)))
                {
                    wrapOpportunityIndex = next.Index;
                    wrapOpportunityWidth = currentWidth;
                }
            }
        }

        chunks.Add(CreateChunk(line, chunkStart, line.Length));
        return chunks;
    }

    private List<LayoutLine> LayoutText(int contentWidth)
    {
        var layoutLines = new List<LayoutLine>();
        if (_state.Lines.Count == 0 || _state.Lines.Count == 1 && _state.Lines[0].Length == 0)
        {
            layoutLines.Add(new LayoutLine(string.Empty, true, 0));
            return layoutLines;
        }

        for (var lineIndex = 0; lineIndex < _state.Lines.Count; lineIndex++)
        {
            var line = _state.Lines[lineIndex];
            var isCurrentLine = lineIndex == _state.CursorLine;
            if (TextMeasurement.VisibleWidth(line) <= contentWidth)
            {
                layoutLines.Add(new LayoutLine(line, isCurrentLine, isCurrentLine ? _state.CursorCol : null));
                continue;
            }

            var chunks = WordWrapLine(line, contentWidth, Segment(line, SegmentMode.Grapheme));
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                var isLastChunk = chunkIndex == chunks.Count - 1;
                var hasCursorInChunk = false;
                var adjustedCursorPosition = 0;
                if (isCurrentLine)
                {
                    if (isLastChunk)
                    {
                        hasCursorInChunk = _state.CursorCol >= chunk.StartIndex;
                        adjustedCursorPosition = _state.CursorCol - chunk.StartIndex;
                    }
                    else
                    {
                        hasCursorInChunk = _state.CursorCol >= chunk.StartIndex && _state.CursorCol < chunk.EndIndex;
                        if (hasCursorInChunk)
                        {
                            adjustedCursorPosition = Math.Min(_state.CursorCol - chunk.StartIndex, chunk.Text.Length);
                        }
                    }
                }

                layoutLines.Add(new LayoutLine(
                    chunk.Text,
                    hasCursorInChunk,
                    hasCursorInChunk ? adjustedCursorPosition : null));
            }
        }

        return layoutLines;
    }

    private bool IsEditorEmpty() => _state.Lines.Count == 1 && _state.Lines[0].Length == 0;

    private bool IsOnFirstVisualLine()
    {
        var visualLines = BuildVisualLineMap(_lastWidth);
        return FindCurrentVisualLine(visualLines) == 0;
    }

    private bool IsOnLastVisualLine()
    {
        var visualLines = BuildVisualLineMap(_lastWidth);
        return FindCurrentVisualLine(visualLines) == visualLines.Count - 1;
    }

    private void NavigateHistory(int direction)
    {
        _lastAction = null;
        if (_history.Count == 0)
        {
            return;
        }

        var newIndex = _historyIndex - direction;
        if (newIndex < -1 || newIndex >= _history.Count)
        {
            return;
        }

        if (_historyIndex == -1 && newIndex >= 0)
        {
            PushUndoSnapshot();
            _historyDraft = _state.Clone();
        }

        _historyIndex = newIndex;
        if (_historyIndex == -1)
        {
            var draft = _historyDraft;
            _historyDraft = null;
            if (draft is not null)
            {
                _state = draft;
                _preferredVisualCol = null;
                _snappedFromCursorCol = null;
                _scrollOffset = 0;
                OnChange?.Invoke(GetText());
            }
            else
            {
                SetTextInternal(string.Empty);
            }
        }
        else
        {
            SetTextInternal(_history[_historyIndex], direction == -1 ? CursorPlacement.Start : CursorPlacement.End);
        }
    }

    private void ExitHistoryBrowsing()
    {
        _historyIndex = -1;
        _historyDraft = null;
    }

    private void SetTextInternal(string text, CursorPlacement cursorPlacement = CursorPlacement.End)
    {
        var lines = text.Split('\n').ToList();
        _state.Lines = lines.Count == 0 ? [string.Empty] : lines;
        _state.CursorLine = cursorPlacement == CursorPlacement.Start ? 0 : _state.Lines.Count - 1;
        SetCursorCol(cursorPlacement == CursorPlacement.Start ? 0 : _state.Lines[_state.CursorLine].Length);
        _scrollOffset = 0;
        OnChange?.Invoke(GetText());
    }

    private string ExpandPasteMarkers(string text)
    {
        var result = text;
        foreach (var (pasteId, pasteContent) in _pastes)
        {
            var markerRegex = new Regex(
                $@"\[paste #{pasteId}( (\+\d+ lines|\d+ chars))?\]",
                RegexOptions.CultureInvariant);
            result = markerRegex.Replace(result, _ => pasteContent);
        }

        return result;
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\t", "    ", StringComparison.Ordinal);

    private void InsertTextAtCursorInternal(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var normalized = NormalizeText(text);
        var insertedLines = normalized.Split('\n');
        var currentLine = GetCurrentLine();
        var beforeCursor = currentLine[.._state.CursorCol];
        var afterCursor = currentLine[_state.CursorCol..];
        if (insertedLines.Length == 1)
        {
            _state.Lines[_state.CursorLine] = beforeCursor + normalized + afterCursor;
            SetCursorCol(_state.CursorCol + normalized.Length);
        }
        else
        {
            var newLines = new List<string>();
            newLines.AddRange(_state.Lines.Take(_state.CursorLine));
            newLines.Add(beforeCursor + insertedLines[0]);
            newLines.AddRange(insertedLines.Skip(1).Take(insertedLines.Length - 2));
            newLines.Add(insertedLines[^1] + afterCursor);
            newLines.AddRange(_state.Lines.Skip(_state.CursorLine + 1));
            _state.Lines = newLines;
            _state.CursorLine += insertedLines.Length - 1;
            SetCursorCol(insertedLines[^1].Length);
        }

        OnChange?.Invoke(GetText());
    }

    private void InsertCharacter(string character, bool skipUndoCoalescing = false)
    {
        ExitHistoryBrowsing();
        if (!skipUndoCoalescing)
        {
            if (TextMeasurement.IsWhitespaceChar(character) || _lastAction != LastAction.TypeWord)
            {
                PushUndoSnapshot();
            }

            _lastAction = LastAction.TypeWord;
        }

        var line = GetCurrentLine();
        _state.Lines[_state.CursorLine] = line[.._state.CursorCol] + character + line[_state.CursorCol..];
        SetCursorCol(_state.CursorCol + character.Length);
        OnChange?.Invoke(GetText());

        if (_autocompleteState is null)
        {
            if (character == "/" && IsAtStartOfMessage())
            {
                TryTriggerAutocomplete();
            }
            else if (_autocompleteTriggerCharacters.Contains(character, StringComparer.Ordinal))
            {
                var currentLine = GetCurrentLine();
                var textBeforeCursor = currentLine[.._state.CursorCol];
                var characterBeforeSymbol = textBeforeCursor.Length >= 2 ? textBeforeCursor[^2] : '\0';
                if (textBeforeCursor.Length == 1 || characterBeforeSymbol is ' ' or '\t')
                {
                    TryTriggerAutocomplete();
                }
            }
            else if (_typedAutocompleteCharacterRegex.IsMatch(character))
            {
                var textBeforeCursor = GetCurrentLine()[.._state.CursorCol];
                if (IsInSlashCommandContext(textBeforeCursor) || _autocompleteTriggerPattern.IsMatch(textBeforeCursor))
                {
                    TryTriggerAutocomplete();
                }
            }
        }
        else
        {
            UpdateAutocomplete();
        }
    }

    private void HandlePaste(string pastedText)
    {
        CancelAutocomplete();
        ExitHistoryBrowsing();
        _lastAction = null;
        PushUndoSnapshot();

        var decodedText = _controlCsiURegex.Replace(pastedText, match =>
        {
            var codePoint = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (codePoint is >= 97 and <= 122)
            {
                return ((char)(codePoint - 96)).ToString();
            }

            return codePoint is >= 65 and <= 90
                ? ((char)(codePoint - 64)).ToString()
                : match.Value;
        });

        var cleanText = NormalizeText(decodedText);
        var filteredBuilder = new StringBuilder(cleanText.Length);
        foreach (var character in cleanText)
        {
            if (character == '\n' || character >= 32)
            {
                filteredBuilder.Append(character);
            }
        }

        var filteredText = filteredBuilder.ToString();
        if (filteredText.Length > 0 && filteredText[0] is '/' or '~' or '.')
        {
            var currentLine = GetCurrentLine();
            var characterBeforeCursor = _state.CursorCol > 0 ? currentLine[_state.CursorCol - 1].ToString() : string.Empty;
            if (characterBeforeCursor.Length > 0 && _wordCharacterRegex.IsMatch(characterBeforeCursor))
            {
                filteredText = " " + filteredText;
            }
        }

        var pastedLines = filteredText.Split('\n');
        if (pastedLines.Length > 10 || filteredText.Length > 1000)
        {
            _pasteCounter++;
            var pasteId = _pasteCounter;
            _pastes[pasteId] = filteredText;
            var marker = pastedLines.Length > 10
                ? $"[paste #{pasteId} +{pastedLines.Length} lines]"
                : $"[paste #{pasteId} {filteredText.Length} chars]";
            InsertTextAtCursorInternal(marker);
            return;
        }

        InsertTextAtCursorInternal(filteredText);
    }

    private void AddNewLine()
    {
        CancelAutocomplete();
        ExitHistoryBrowsing();
        _lastAction = null;
        PushUndoSnapshot();

        var currentLine = GetCurrentLine();
        var before = currentLine[.._state.CursorCol];
        var after = currentLine[_state.CursorCol..];
        _state.Lines[_state.CursorLine] = before;
        _state.Lines.Insert(_state.CursorLine + 1, after);
        _state.CursorLine++;
        SetCursorCol(0);
        OnChange?.Invoke(GetText());
    }

    private bool ShouldSubmitOnBackslashEnter(string data, KeybindingsManager keybindings)
    {
        if (DisableSubmit || !Keys.MatchesKey(data, "enter"))
        {
            return false;
        }

        var submitKeys = keybindings.GetKeys("tui.input.submit");
        if (!submitKeys.Contains("shift+enter", StringComparer.Ordinal) &&
            !submitKeys.Contains("shift+return", StringComparer.Ordinal))
        {
            return false;
        }

        var currentLine = GetCurrentLine();
        return _state.CursorCol > 0 && currentLine[_state.CursorCol - 1] == '\\';
    }

    private void SubmitValue()
    {
        CancelAutocomplete();
        var result = ExpandPasteMarkers(GetText()).Trim();
        _state = new EditorState([string.Empty], 0, 0);
        _pastes.Clear();
        _pasteCounter = 0;
        ExitHistoryBrowsing();
        _scrollOffset = 0;
        _undoStack.Clear();
        _lastAction = null;
        OnChange?.Invoke(string.Empty);
        OnSubmit?.Invoke(result);
    }

    private void HandleBackspace()
    {
        ExitHistoryBrowsing();
        _lastAction = null;
        if (_state.CursorCol > 0)
        {
            PushUndoSnapshot();
            var line = GetCurrentLine();
            var beforeCursor = line[.._state.CursorCol];
            var graphemes = Segment(beforeCursor, SegmentMode.Grapheme);
            var lastGrapheme = graphemes.Count > 0
                ? graphemes[^1]
                : new EditorTextSegment(beforeCursor[^1..], beforeCursor.Length - 1, beforeCursor);
            var markerMatch = _pasteMarkerSingleRegex.Match(lastGrapheme.Segment);
            if (markerMatch.Success)
            {
                var targetId = int.Parse(markerMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                _pastes.Remove(targetId);
                _pasteCounter--;
                foreach (var id in _pastes.Keys.Where(id => id > targetId).Order().ToArray())
                {
                    _pastes[id - 1] = _pastes[id];
                    _pastes.Remove(id);
                }

                for (var index = 0; index < _state.Lines.Count; index++)
                {
                    _state.Lines[index] = _pasteMarkerRegex.Replace(_state.Lines[index], match =>
                    {
                        var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        return id <= targetId
                            ? match.Value
                            : $"[paste #{id - 1}{match.Groups[2].Value}]";
                    });
                }

                line = GetCurrentLine();
            }

            var graphemeLength = lastGrapheme.Segment.Length;
            _state.Lines[_state.CursorLine] =
                line[..(_state.CursorCol - graphemeLength)] + line[_state.CursorCol..];
            SetCursorCol(_state.CursorCol - graphemeLength);
        }
        else if (_state.CursorLine > 0)
        {
            PushUndoSnapshot();
            var currentLine = GetCurrentLine();
            var previousLine = _state.Lines[_state.CursorLine - 1];
            _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
            _state.Lines.RemoveAt(_state.CursorLine);
            _state.CursorLine--;
            SetCursorCol(previousLine.Length);
        }

        OnChange?.Invoke(GetText());
        UpdateAutocompleteAfterDeletion();
    }

    private void SetCursorCol(int column)
    {
        _state.CursorCol = column;
        _preferredVisualCol = null;
        _snappedFromCursorCol = null;
    }

    private void MoveToVisualLine(
        IReadOnlyList<VisualLine> visualLines,
        int currentVisualLine,
        int targetVisualLine)
    {
        if (currentVisualLine < 0 || currentVisualLine >= visualLines.Count ||
            targetVisualLine < 0 || targetVisualLine >= visualLines.Count)
        {
            return;
        }

        var current = visualLines[currentVisualLine];
        var target = visualLines[targetVisualLine];
        int currentVisualColumn;
        if (_snappedFromCursorCol is { } snappedColumn)
        {
            var visualLineIndex = FindVisualLineAt(visualLines, current.LogicalLine, snappedColumn);
            currentVisualColumn = snappedColumn - visualLines[visualLineIndex].StartCol;
        }
        else
        {
            currentVisualColumn = _state.CursorCol - current.StartCol;
        }

        var isLastSourceSegment = currentVisualLine == visualLines.Count - 1 ||
            visualLines[currentVisualLine + 1].LogicalLine != current.LogicalLine;
        var sourceMaxVisualColumn = isLastSourceSegment ? current.Length : Math.Max(0, current.Length - 1);
        var isLastTargetSegment = targetVisualLine == visualLines.Count - 1 ||
            visualLines[targetVisualLine + 1].LogicalLine != target.LogicalLine;
        var targetMaxVisualColumn = isLastTargetSegment ? target.Length : Math.Max(0, target.Length - 1);
        var moveToVisualColumn = ComputeVerticalMoveColumn(
            currentVisualColumn,
            sourceMaxVisualColumn,
            targetMaxVisualColumn);

        _state.CursorLine = target.LogicalLine;
        var targetColumn = target.StartCol + moveToVisualColumn;
        var logicalLine = _state.Lines[target.LogicalLine];
        _state.CursorCol = Math.Min(targetColumn, logicalLine.Length);

        foreach (var segment in Segment(logicalLine, SegmentMode.Grapheme))
        {
            if (segment.Index > _state.CursorCol)
            {
                break;
            }

            if (segment.Segment.Length <= 1 || _state.CursorCol >= segment.Index + segment.Segment.Length)
            {
                continue;
            }

            var isContinuation = segment.Index < target.StartCol;
            var isMovingDown = targetVisualLine > currentVisualLine;
            if (isContinuation && isMovingDown)
            {
                var segmentEnd = segment.Index + segment.Segment.Length;
                var next = targetVisualLine + 1;
                while (next < visualLines.Count &&
                       visualLines[next].LogicalLine == target.LogicalLine &&
                       visualLines[next].StartCol < segmentEnd)
                {
                    next++;
                }

                if (next < visualLines.Count)
                {
                    MoveToVisualLine(visualLines, currentVisualLine, next);
                    return;
                }
            }

            _snappedFromCursorCol = _state.CursorCol;
            _state.CursorCol = segment.Index;
            return;
        }

        _snappedFromCursorCol = null;
    }

    private int ComputeVerticalMoveColumn(
        int currentVisualColumn,
        int sourceMaxVisualColumn,
        int targetMaxVisualColumn)
    {
        var hasPreferred = _preferredVisualCol is not null;
        var cursorInMiddle = currentVisualColumn < sourceMaxVisualColumn;
        var targetTooShort = targetMaxVisualColumn < currentVisualColumn;
        if (!hasPreferred || cursorInMiddle)
        {
            if (targetTooShort)
            {
                _preferredVisualCol = currentVisualColumn;
                return targetMaxVisualColumn;
            }

            _preferredVisualCol = null;
            return currentVisualColumn;
        }

        var targetCannotFitPreferred = targetMaxVisualColumn < _preferredVisualCol!.Value;
        if (targetTooShort || targetCannotFitPreferred)
        {
            return targetMaxVisualColumn;
        }

        var result = _preferredVisualCol.Value;
        _preferredVisualCol = null;
        return result;
    }

    private void MoveToLineStart()
    {
        _lastAction = null;
        SetCursorCol(0);
    }

    private void MoveToLineEnd()
    {
        _lastAction = null;
        SetCursorCol(GetCurrentLine().Length);
    }

    private void DeleteToStartOfLine()
    {
        ExitHistoryBrowsing();
        var currentLine = GetCurrentLine();
        if (_state.CursorCol > 0)
        {
            PushUndoSnapshot();
            var deletedText = currentLine[.._state.CursorCol];
            _killRing.Push(deletedText, prepend: true, accumulate: _lastAction == LastAction.Kill);
            _lastAction = LastAction.Kill;
            _state.Lines[_state.CursorLine] = currentLine[_state.CursorCol..];
            SetCursorCol(0);
        }
        else if (_state.CursorLine > 0)
        {
            PushUndoSnapshot();
            _killRing.Push("\n", prepend: true, accumulate: _lastAction == LastAction.Kill);
            _lastAction = LastAction.Kill;
            var previousLine = _state.Lines[_state.CursorLine - 1];
            _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
            _state.Lines.RemoveAt(_state.CursorLine);
            _state.CursorLine--;
            SetCursorCol(previousLine.Length);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteToEndOfLine()
    {
        ExitHistoryBrowsing();
        var currentLine = GetCurrentLine();
        if (_state.CursorCol < currentLine.Length)
        {
            PushUndoSnapshot();
            var deletedText = currentLine[_state.CursorCol..];
            _killRing.Push(deletedText, prepend: false, accumulate: _lastAction == LastAction.Kill);
            _lastAction = LastAction.Kill;
            _state.Lines[_state.CursorLine] = currentLine[.._state.CursorCol];
        }
        else if (_state.CursorLine < _state.Lines.Count - 1)
        {
            PushUndoSnapshot();
            _killRing.Push("\n", prepend: false, accumulate: _lastAction == LastAction.Kill);
            _lastAction = LastAction.Kill;
            _state.Lines[_state.CursorLine] = currentLine + _state.Lines[_state.CursorLine + 1];
            _state.Lines.RemoveAt(_state.CursorLine + 1);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteWordBackwards()
    {
        ExitHistoryBrowsing();
        var currentLine = GetCurrentLine();
        if (_state.CursorCol == 0)
        {
            if (_state.CursorLine > 0)
            {
                PushUndoSnapshot();
                _killRing.Push("\n", prepend: true, accumulate: _lastAction == LastAction.Kill);
                _lastAction = LastAction.Kill;
                var previousLine = _state.Lines[_state.CursorLine - 1];
                _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
                _state.Lines.RemoveAt(_state.CursorLine);
                _state.CursorLine--;
                SetCursorCol(previousLine.Length);
            }
        }
        else
        {
            PushUndoSnapshot();
            var wasKill = _lastAction == LastAction.Kill;
            var oldCursorColumn = _state.CursorCol;
            MoveWordBackwards();
            var deleteFrom = _state.CursorCol;
            SetCursorCol(oldCursorColumn);
            var deletedText = currentLine[deleteFrom.._state.CursorCol];
            _killRing.Push(deletedText, prepend: true, accumulate: wasKill);
            _lastAction = LastAction.Kill;
            _state.Lines[_state.CursorLine] = currentLine[..deleteFrom] + currentLine[_state.CursorCol..];
            SetCursorCol(deleteFrom);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteWordForward()
    {
        ExitHistoryBrowsing();
        var currentLine = GetCurrentLine();
        if (_state.CursorCol >= currentLine.Length)
        {
            if (_state.CursorLine < _state.Lines.Count - 1)
            {
                PushUndoSnapshot();
                _killRing.Push("\n", prepend: false, accumulate: _lastAction == LastAction.Kill);
                _lastAction = LastAction.Kill;
                _state.Lines[_state.CursorLine] = currentLine + _state.Lines[_state.CursorLine + 1];
                _state.Lines.RemoveAt(_state.CursorLine + 1);
            }
        }
        else
        {
            PushUndoSnapshot();
            var wasKill = _lastAction == LastAction.Kill;
            var oldCursorColumn = _state.CursorCol;
            MoveWordForwards();
            var deleteTo = _state.CursorCol;
            SetCursorCol(oldCursorColumn);
            var deletedText = currentLine[_state.CursorCol..deleteTo];
            _killRing.Push(deletedText, prepend: false, accumulate: wasKill);
            _lastAction = LastAction.Kill;
            _state.Lines[_state.CursorLine] = currentLine[.._state.CursorCol] + currentLine[deleteTo..];
        }

        OnChange?.Invoke(GetText());
    }

    private void HandleForwardDelete()
    {
        ExitHistoryBrowsing();
        _lastAction = null;
        var currentLine = GetCurrentLine();
        if (_state.CursorCol < currentLine.Length)
        {
            PushUndoSnapshot();
            var afterCursor = currentLine[_state.CursorCol..];
            var graphemes = Segment(afterCursor, SegmentMode.Grapheme);
            var grapheme = graphemes.Count > 0 ? graphemes[0].Segment : null;
            var graphemeLength = grapheme?.Length ?? 1;
            _state.Lines[_state.CursorLine] =
                currentLine[.._state.CursorCol] + currentLine[(_state.CursorCol + graphemeLength)..];
        }
        else if (_state.CursorLine < _state.Lines.Count - 1)
        {
            PushUndoSnapshot();
            _state.Lines[_state.CursorLine] = currentLine + _state.Lines[_state.CursorLine + 1];
            _state.Lines.RemoveAt(_state.CursorLine + 1);
        }

        OnChange?.Invoke(GetText());
        UpdateAutocompleteAfterDeletion();
    }

    private List<VisualLine> BuildVisualLineMap(int width)
    {
        var visualLines = new List<VisualLine>();
        for (var index = 0; index < _state.Lines.Count; index++)
        {
            var line = _state.Lines[index];
            if (line.Length == 0)
            {
                visualLines.Add(new VisualLine(index, 0, 0));
            }
            else if (TextMeasurement.VisibleWidth(line) <= width)
            {
                visualLines.Add(new VisualLine(index, 0, line.Length));
            }
            else
            {
                foreach (var chunk in WordWrapLine(line, width, Segment(line, SegmentMode.Grapheme)))
                {
                    visualLines.Add(new VisualLine(index, chunk.StartIndex, chunk.EndIndex - chunk.StartIndex));
                }
            }
        }

        return visualLines;
    }

    private static int FindVisualLineAt(IReadOnlyList<VisualLine> visualLines, int line, int column)
    {
        for (var index = 0; index < visualLines.Count; index++)
        {
            var visualLine = visualLines[index];
            if (visualLine.LogicalLine != line)
            {
                continue;
            }

            var offset = column - visualLine.StartCol;
            var isLastSegmentOfLine = index == visualLines.Count - 1 ||
                visualLines[index + 1].LogicalLine != visualLine.LogicalLine;
            if (offset >= 0 && (offset < visualLine.Length || isLastSegmentOfLine && offset == visualLine.Length))
            {
                return index;
            }
        }

        return visualLines.Count - 1;
    }

    private int FindCurrentVisualLine(IReadOnlyList<VisualLine> visualLines) =>
        FindVisualLineAt(visualLines, _state.CursorLine, _state.CursorCol);

    private void MoveCursor(int deltaLine, int deltaColumn)
    {
        _lastAction = null;
        var visualLines = BuildVisualLineMap(_lastWidth);
        var currentVisualLine = FindCurrentVisualLine(visualLines);
        if (deltaLine != 0)
        {
            var targetVisualLine = currentVisualLine + deltaLine;
            if (targetVisualLine >= 0 && targetVisualLine < visualLines.Count)
            {
                MoveToVisualLine(visualLines, currentVisualLine, targetVisualLine);
            }
        }

        if (deltaColumn != 0)
        {
            var currentLine = GetCurrentLine();
            if (deltaColumn > 0)
            {
                if (_state.CursorCol < currentLine.Length)
                {
                    var afterCursor = currentLine[_state.CursorCol..];
                    var graphemes = Segment(afterCursor, SegmentMode.Grapheme);
                    var grapheme = graphemes.Count > 0 ? graphemes[0].Segment : null;
                    SetCursorCol(_state.CursorCol + (grapheme?.Length ?? 1));
                }
                else if (_state.CursorLine < _state.Lines.Count - 1)
                {
                    _state.CursorLine++;
                    SetCursorCol(0);
                }
                else if (currentVisualLine >= 0 && currentVisualLine < visualLines.Count)
                {
                    _preferredVisualCol = _state.CursorCol - visualLines[currentVisualLine].StartCol;
                }
            }
            else if (_state.CursorCol > 0)
            {
                var beforeCursor = currentLine[.._state.CursorCol];
                var graphemes = Segment(beforeCursor, SegmentMode.Grapheme);
                var graphemeLength = graphemes.Count > 0 ? graphemes[^1].Segment.Length : 1;
                SetCursorCol(_state.CursorCol - graphemeLength);
            }
            else if (_state.CursorLine > 0)
            {
                _state.CursorLine--;
                SetCursorCol(_state.Lines[_state.CursorLine].Length);
            }
        }

        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
        }
    }

    private void PageScroll(int direction)
    {
        _lastAction = null;
        var pageSize = Math.Max(5, (int)Math.Floor(Tui.Terminal.Rows * 0.3d));
        var visualLines = BuildVisualLineMap(_lastWidth);
        var currentVisualLine = FindCurrentVisualLine(visualLines);
        var targetVisualLine = Math.Max(
            0,
            Math.Min(visualLines.Count - 1, currentVisualLine + (direction * pageSize)));
        MoveToVisualLine(visualLines, currentVisualLine, targetVisualLine);
    }

    private void MoveWordBackwards()
    {
        _lastAction = null;
        var currentLine = GetCurrentLine();
        if (_state.CursorCol == 0)
        {
            if (_state.CursorLine > 0)
            {
                _state.CursorLine--;
                SetCursorCol(_state.Lines[_state.CursorLine].Length);
            }

            return;
        }

        SetCursorCol(WordNavigation.FindWordBackward(
            currentLine,
            _state.CursorCol,
            new WordNavigationOptions
            {
                Segment = text => SegmentWords(text),
                IsAtomicSegment = IsPasteMarker,
            }));
    }

    private void Yank()
    {
        if (_killRing.Length == 0)
        {
            return;
        }

        PushUndoSnapshot();
        InsertYankedText(_killRing.Peek()!);
        _lastAction = LastAction.Yank;
    }

    private void YankPop()
    {
        if (_lastAction != LastAction.Yank || _killRing.Length <= 1)
        {
            return;
        }

        PushUndoSnapshot();
        DeleteYankedText();
        _killRing.Rotate();
        InsertYankedText(_killRing.Peek()!);
        _lastAction = LastAction.Yank;
    }

    private void InsertYankedText(string text)
    {
        ExitHistoryBrowsing();
        var lines = text.Split('\n');
        if (lines.Length == 1)
        {
            var currentLine = GetCurrentLine();
            _state.Lines[_state.CursorLine] = currentLine[.._state.CursorCol] + text + currentLine[_state.CursorCol..];
            SetCursorCol(_state.CursorCol + text.Length);
        }
        else
        {
            var currentLine = GetCurrentLine();
            var before = currentLine[.._state.CursorCol];
            var after = currentLine[_state.CursorCol..];
            _state.Lines[_state.CursorLine] = before + lines[0];
            for (var index = 1; index < lines.Length - 1; index++)
            {
                _state.Lines.Insert(_state.CursorLine + index, lines[index]);
            }

            var lastLineIndex = _state.CursorLine + lines.Length - 1;
            _state.Lines.Insert(lastLineIndex, lines[^1] + after);
            _state.CursorLine = lastLineIndex;
            SetCursorCol(lines[^1].Length);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteYankedText()
    {
        var yankedText = _killRing.Peek();
        if (string.IsNullOrEmpty(yankedText))
        {
            return;
        }

        var yankLines = yankedText.Split('\n');
        if (yankLines.Length == 1)
        {
            var currentLine = GetCurrentLine();
            var deleteLength = yankedText.Length;
            _state.Lines[_state.CursorLine] =
                currentLine[..(_state.CursorCol - deleteLength)] + currentLine[_state.CursorCol..];
            SetCursorCol(_state.CursorCol - deleteLength);
        }
        else
        {
            var startLine = _state.CursorLine - (yankLines.Length - 1);
            var startColumn = _state.Lines[startLine].Length - yankLines[0].Length;
            var afterCursor = GetCurrentLine()[_state.CursorCol..];
            var beforeYank = _state.Lines[startLine][..startColumn];
            _state.Lines.RemoveRange(startLine, yankLines.Length);
            _state.Lines.Insert(startLine, beforeYank + afterCursor);
            _state.CursorLine = startLine;
            SetCursorCol(startColumn);
        }

        OnChange?.Invoke(GetText());
    }

    private void PushUndoSnapshot() =>
        _undoStack.Push(new EditorSnapshot(_state, _pastes, _pasteCounter));

    private void Undo()
    {
        ExitHistoryBrowsing();
        var snapshot = _undoStack.Pop();
        if (snapshot is null)
        {
            return;
        }

        _state = snapshot.State;
        _pastes = snapshot.Pastes;
        _pasteCounter = snapshot.PasteCounter;
        _lastAction = null;
        _preferredVisualCol = null;
        OnChange?.Invoke(GetText());
    }

    private void JumpToChar(string character, JumpDirection direction)
    {
        _lastAction = null;
        var isForward = direction == JumpDirection.Forward;
        var end = isForward ? _state.Lines.Count : -1;
        var step = isForward ? 1 : -1;
        for (var lineIndex = _state.CursorLine; lineIndex != end; lineIndex += step)
        {
            var line = _state.Lines[lineIndex];
            var isCurrentLine = lineIndex == _state.CursorLine;
            var searchFrom = isCurrentLine
                ? isForward ? _state.CursorCol + 1 : _state.CursorCol - 1
                : isForward ? 0 : line.Length - 1;
            var foundIndex = isForward
                ? searchFrom > line.Length
                    ? -1
                    : line.IndexOf(character, Math.Max(0, searchFrom), StringComparison.Ordinal)
                : searchFrom < 0 ? -1 : line.LastIndexOf(character, searchFrom, StringComparison.Ordinal);
            if (foundIndex >= 0)
            {
                _state.CursorLine = lineIndex;
                SetCursorCol(foundIndex);
                return;
            }
        }
    }

    private void MoveWordForwards()
    {
        _lastAction = null;
        var currentLine = GetCurrentLine();
        if (_state.CursorCol >= currentLine.Length)
        {
            if (_state.CursorLine < _state.Lines.Count - 1)
            {
                _state.CursorLine++;
                SetCursorCol(0);
            }

            return;
        }

        SetCursorCol(WordNavigation.FindWordForward(
            currentLine,
            _state.CursorCol,
            new WordNavigationOptions
            {
                Segment = text => SegmentWords(text),
                IsAtomicSegment = IsPasteMarker,
            }));
    }

    private bool IsSlashMenuAllowed() => _state.CursorLine == 0;

    private bool IsAtStartOfMessage()
    {
        if (!IsSlashMenuAllowed())
        {
            return false;
        }

        var beforeCursor = GetCurrentLine()[.._state.CursorCol].Trim();
        return beforeCursor.Length == 0 || beforeCursor == "/";
    }

    private bool IsInSlashCommandContext(string textBeforeCursor) =>
        IsSlashMenuAllowed() && textBeforeCursor.TrimStart().StartsWith('/');

    private static int GetBestAutocompleteMatchIndex(IReadOnlyList<AutocompleteItem> items, string prefix)
    {
        if (prefix.Length == 0)
        {
            return -1;
        }

        var firstPrefixIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            var value = items[index].Value;
            if (value == prefix)
            {
                return index;
            }

            if (firstPrefixIndex == -1 && value.StartsWith(prefix, StringComparison.Ordinal))
            {
                firstPrefixIndex = index;
            }
        }

        return firstPrefixIndex;
    }

    private SelectList CreateAutocompleteList(string prefix, IReadOnlyList<AutocompleteItem> items)
    {
        var layout = prefix.StartsWith('/') ? _slashCommandSelectListLayout : null;
        return new SelectList(
            items.Select(static item => new SelectItem
            {
                Value = item.Value,
                Label = item.Label,
                Description = item.Description,
            }).ToArray(),
            _autocompleteMaxVisible,
            Theme.SelectList,
            layout);
    }

    private void TryTriggerAutocomplete(bool explicitTab = false) =>
        RequestAutocomplete(new AutocompleteRequestOptions(false, explicitTab));

    private void HandleTabCompletion()
    {
        if (_autocompleteProvider is null)
        {
            return;
        }

        var beforeCursor = GetCurrentLine()[.._state.CursorCol];
        if (IsInSlashCommandContext(beforeCursor) && !beforeCursor.TrimStart().Contains(' '))
        {
            RequestAutocomplete(new AutocompleteRequestOptions(false, true));
        }
        else
        {
            RequestAutocomplete(new AutocompleteRequestOptions(true, true));
        }
    }

    private void RequestAutocomplete(AutocompleteRequestOptions options)
    {
        if (_autocompleteProvider is null)
        {
            return;
        }

        if (options.Force && !_autocompleteProvider.ShouldTriggerFileCompletion(
                _state.Lines,
                _state.CursorLine,
                _state.CursorCol))
        {
            return;
        }

        CancelAutocompleteRequest();
        var startToken = ++_autocompleteStartToken;
        var debounceMilliseconds = GetAutocompleteDebounceMilliseconds(options);
        if (debounceMilliseconds > 0)
        {
            _autocompleteDebounce = new CancellationTokenSource();
            _ = DebounceAutocompleteAsync(
                startToken,
                options,
                debounceMilliseconds,
                _autocompleteDebounce.Token);
            return;
        }

        StartAutocompleteRequest(startToken, options);
    }

    private async Task DebounceAutocompleteAsync(
        int startToken,
        AutocompleteRequestOptions options,
        int milliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _autocompleteDebounce = null;
        StartAutocompleteRequest(startToken, options);
    }

    private void StartAutocompleteRequest(int startToken, AutocompleteRequestOptions options)
    {
        var previousTask = _autocompleteRequestTask;
        _autocompleteRequestTask = RunQueuedAutocompleteRequestAsync(previousTask, startToken, options);
    }

    private async Task RunQueuedAutocompleteRequestAsync(
        Task previousTask,
        int startToken,
        AutocompleteRequestOptions options)
    {
        await previousTask.ConfigureAwait(false);
        var provider = _autocompleteProvider;
        if (startToken != _autocompleteStartToken || provider is null)
        {
            return;
        }

        var controller = new CancellationTokenSource();
        _autocompleteAbort = controller;
        var requestId = ++_autocompleteRequestId;
        var snapshotText = GetText();
        var snapshotLine = _state.CursorLine;
        var snapshotColumn = _state.CursorCol;
        await RunAutocompleteRequestAsync(
            provider,
            requestId,
            controller,
            snapshotText,
            snapshotLine,
            snapshotColumn,
            options).ConfigureAwait(false);
    }

    private void SetAutocompleteTriggerCharacters(IReadOnlyList<string> triggerCharacters)
    {
        var next = new List<string>(_defaultAutocompleteTriggerCharacters);
        foreach (var character in triggerCharacters)
        {
            if (character.Length != 1 || character == "/" || TextMeasurement.IsWhitespaceChar(character) ||
                next.Contains(character, StringComparer.Ordinal))
            {
                continue;
            }

            next.Add(character);
        }

        _autocompleteTriggerCharacters = next;
        _autocompleteTriggerPattern = BuildTriggerPattern(next);
        _autocompleteDebouncePattern = BuildDebouncePattern(next);
    }

    private int GetAutocompleteDebounceMilliseconds(AutocompleteRequestOptions options)
    {
        if (options.ExplicitTab || options.Force)
        {
            return 0;
        }

        var textBeforeCursor = GetCurrentLine()[.._state.CursorCol];
        return _autocompleteDebouncePattern.IsMatch(textBeforeCursor) ? _attachmentAutocompleteDebounceMs : 0;
    }

    private async Task RunAutocompleteRequestAsync(
        IAutocompleteProvider provider,
        int requestId,
        CancellationTokenSource controller,
        string snapshotText,
        int snapshotLine,
        int snapshotColumn,
        AutocompleteRequestOptions options)
    {
        var suggestions = await provider.GetSuggestions(
            _state.Lines,
            _state.CursorLine,
            _state.CursorCol,
            new AutocompleteOptions { Signal = controller.Token, Force = options.Force }).ConfigureAwait(false);

        if (!IsAutocompleteRequestCurrent(requestId, controller, snapshotText, snapshotLine, snapshotColumn))
        {
            return;
        }

        _autocompleteAbort = null;
        if (suggestions is null || suggestions.Items.Count == 0)
        {
            CancelAutocomplete();
            Tui.RequestRender();
            return;
        }

        if (options.Force && options.ExplicitTab && suggestions.Items.Count == 1)
        {
            PushUndoSnapshot();
            _lastAction = null;
            var completion = provider.ApplyCompletion(
                _state.Lines,
                _state.CursorLine,
                _state.CursorCol,
                suggestions.Items[0],
                suggestions.Prefix);
            _state.Lines = [.. completion.Lines];
            _state.CursorLine = completion.CursorLine;
            SetCursorCol(completion.CursorCol);
            OnChange?.Invoke(GetText());
            Tui.RequestRender();
            return;
        }

        ApplyAutocompleteSuggestions(
            suggestions,
            options.Force ? AutocompleteState.Force : AutocompleteState.Regular);
        Tui.RequestRender();
    }

    private bool IsAutocompleteRequestCurrent(
        int requestId,
        CancellationTokenSource controller,
        string snapshotText,
        int snapshotLine,
        int snapshotColumn) =>
        !controller.IsCancellationRequested &&
        requestId == _autocompleteRequestId &&
        GetText() == snapshotText &&
        _state.CursorLine == snapshotLine &&
        _state.CursorCol == snapshotColumn;

    private void ApplyAutocompleteSuggestions(AutocompleteSuggestions suggestions, AutocompleteState state)
    {
        _autocompletePrefix = suggestions.Prefix;
        _autocompleteList = CreateAutocompleteList(suggestions.Prefix, suggestions.Items);
        var bestMatchIndex = GetBestAutocompleteMatchIndex(suggestions.Items, suggestions.Prefix);
        if (bestMatchIndex >= 0)
        {
            _autocompleteList.SetSelectedIndex(bestMatchIndex);
        }

        _autocompleteState = state;
    }

    private void CancelAutocompleteRequest()
    {
        _autocompleteStartToken++;
        _autocompleteDebounce?.Cancel();
        _autocompleteDebounce?.Dispose();
        _autocompleteDebounce = null;
        _autocompleteAbort?.Cancel();
        _autocompleteAbort?.Dispose();
        _autocompleteAbort = null;
    }

    private void ClearAutocompleteUi()
    {
        _autocompleteState = null;
        _autocompleteList = null;
        _autocompletePrefix = string.Empty;
    }

    private void CancelAutocomplete()
    {
        CancelAutocompleteRequest();
        ClearAutocompleteUi();
    }

    private void UpdateAutocomplete()
    {
        if (_autocompleteState is null || _autocompleteProvider is null)
        {
            return;
        }

        RequestAutocomplete(new AutocompleteRequestOptions(_autocompleteState == AutocompleteState.Force, false));
    }

    private bool ApplySelectedAutocomplete(bool submitSlashCommand)
    {
        var selected = _autocompleteList?.GetSelectedItem();
        var provider = _autocompleteProvider;
        if (selected is null || provider is null)
        {
            return false;
        }

        PushUndoSnapshot();
        _lastAction = null;
        var completion = provider.ApplyCompletion(
            _state.Lines,
            _state.CursorLine,
            _state.CursorCol,
            new AutocompleteItem
            {
                Value = selected.Value,
                Label = selected.Label,
                Description = selected.Description,
            },
            _autocompletePrefix);
        _state.Lines = [.. completion.Lines];
        _state.CursorLine = completion.CursorLine;
        SetCursorCol(completion.CursorCol);
        var isSlashCommand = _autocompletePrefix.StartsWith('/');
        CancelAutocomplete();
        if (submitSlashCommand && isSlashCommand)
        {
            return true;
        }

        OnChange?.Invoke(GetText());
        return false;
    }

    private void UpdateAutocompleteAfterDeletion()
    {
        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
            return;
        }

        var textBeforeCursor = GetCurrentLine()[.._state.CursorCol];
        if (IsInSlashCommandContext(textBeforeCursor) || _autocompleteTriggerPattern.IsMatch(textBeforeCursor))
        {
            TryTriggerAutocomplete();
        }
    }

    private string GetCurrentLine() =>
        _state.CursorLine >= 0 && _state.CursorLine < _state.Lines.Count
            ? _state.Lines[_state.CursorLine]
            : string.Empty;

    private static bool IsNewLineInput(string data, KeybindingsManager keybindings) =>
        keybindings.Matches(data, "tui.input.newLine") ||
        data.Length > 1 && data[0] == '\n' ||
        data == "\x1b\r" ||
        data == "\x1b[13;2~" ||
        data.Length > 1 && data.Contains('\x1b') && data.Contains('\r') ||
        data == "\n";

    private static int ClampFiniteOption(int? value, int min, int max, int fallback) =>
        value is null ? fallback : Math.Max(min, Math.Min(max, value.Value));

    private static string CreateScrollBorder(string direction, int hiddenLineCount, int width)
    {
        var availableWidth = Math.Max(0, width);
        var indicator = $"─── {direction} {hiddenLineCount} more ";
        var remaining = availableWidth - TextMeasurement.VisibleWidth(indicator);
        if (remaining >= 0)
        {
            return indicator + new string('─', remaining);
        }

        var ellipsis = "..."[..Math.Min(3, availableWidth)];
        var indicatorWidth = availableWidth - TextMeasurement.VisibleWidth(ellipsis);
        return TextMeasurement.SliceByColumn(indicator, 0, indicatorWidth, true) + ellipsis;
    }

    private static Regex BuildTriggerPattern(IReadOnlyList<string> triggerCharacters)
    {
        var characterClass = string.Concat(triggerCharacters.Select(Regex.Escape));
        return new Regex($@"(?:^|[\s])[{characterClass}][^\s]*$", RegexOptions.CultureInvariant);
    }

    private static Regex BuildDebouncePattern(IReadOnlyList<string> triggerCharacters)
    {
        var escapedWithoutAt = string.Concat(
            triggerCharacters.Where(static character => character != "@").Select(Regex.Escape));
        return new Regex(
            $"(?:^|[ \\t])(?:@(?:\"[^\"]*|[^\\s]*)|[{escapedWithoutAt}][^\\s]*)$",
            RegexOptions.CultureInvariant);
    }

    private static TextChunk CreateChunk(string line, int start, int end) => new()
    {
        Text = line[start..end],
        StartIndex = start,
        EndIndex = end,
    };

    private static bool IsPasteMarker(string segment) =>
        segment.Length >= 10 && _pasteMarkerSingleRegex.IsMatch(segment);

    private List<EditorTextSegment> Segment(string text, SegmentMode mode)
    {
        var baseSegments = mode == SegmentMode.Grapheme
            ? SegmentGraphemes(text)
            : SegmentWordData(text);
        if (_pastes.Count == 0 || !text.Contains("[paste #", StringComparison.Ordinal))
        {
            return baseSegments;
        }

        var markers = _pasteMarkerRegex.Matches(text)
            .Where(match => _pastes.ContainsKey(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)))
            .Select(match => (Start: match.Index, End: match.Index + match.Length))
            .ToArray();
        if (markers.Length == 0)
        {
            return baseSegments;
        }

        var result = new List<EditorTextSegment>();
        var markerIndex = 0;
        foreach (var segment in baseSegments)
        {
            while (markerIndex < markers.Length && markers[markerIndex].End <= segment.Index)
            {
                markerIndex++;
            }

            var hasMarker = markerIndex < markers.Length;
            var marker = hasMarker ? markers[markerIndex] : default;
            if (hasMarker && segment.Index >= marker.Start && segment.Index < marker.End)
            {
                if (segment.Index == marker.Start)
                {
                    result.Add(new EditorTextSegment(text[marker.Start..marker.End], marker.Start, text));
                }
            }
            else
            {
                result.Add(segment);
            }
        }

        return result;
    }

    private IEnumerable<WordSegment> SegmentWords(string text)
    {
        foreach (var segment in Segment(text, SegmentMode.Word))
        {
            yield return new WordSegment(segment.Segment, IsWordLike(segment.Segment));
        }
    }

    private static List<EditorTextSegment> SegmentGraphemes(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var result = new List<EditorTextSegment>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            result.Add(new EditorTextSegment(enumerator.GetTextElement(), enumerator.ElementIndex, text));
        }

        return result;
    }

    private static List<EditorTextSegment> SegmentWordData(string text)
    {
        var result = new List<EditorTextSegment>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var codePoint = ReadCodePoint(text, index, out var codePointLength);
            if (TextMeasurement.IsWhitespaceChar(text[index..(index + codePointLength)]))
            {
                do
                {
                    index += codePointLength;
                    if (index >= text.Length)
                    {
                        break;
                    }

                    _ = ReadCodePoint(text, index, out codePointLength);
                }
                while (TextMeasurement.IsWhitespaceChar(text[index..(index + codePointLength)]));
            }
            else if (IsHan(codePoint))
            {
                var hanCount = 0;
                while (index < text.Length)
                {
                    codePoint = ReadCodePoint(text, index, out codePointLength);
                    if (!IsHan(codePoint))
                    {
                        break;
                    }

                    index += codePointLength;
                    hanCount++;
                    if (hanCount == 2)
                    {
                        result.Add(new EditorTextSegment(text[start..index], start, text));
                        start = index;
                        hanCount = 0;
                    }
                }

                if (start < index)
                {
                    result.Add(new EditorTextSegment(text[start..index], start, text));
                }

                continue;
            }
            else if (IsWordCodePoint(codePoint))
            {
                index += codePointLength;
                while (index < text.Length)
                {
                    var next = ReadCodePoint(text, index, out var nextLength);
                    if (IsWordCodePoint(next) && !IsHan(next))
                    {
                        index += nextLength;
                        continue;
                    }

                    if (next is '.' or ':' && index + nextLength < text.Length)
                    {
                        var afterPunctuation = ReadCodePoint(text, index + nextLength, out var afterLength);
                        if (IsWordCodePoint(afterPunctuation) && !IsHan(afterPunctuation))
                        {
                            index += nextLength + afterLength;
                            while (index < text.Length)
                            {
                                var following = ReadCodePoint(text, index, out var followingLength);
                                if (!IsWordCodePoint(following) || IsHan(following))
                                {
                                    break;
                                }

                                index += followingLength;
                            }

                            continue;
                        }
                    }

                    break;
                }
            }
            else
            {
                index += codePointLength;
                while (index < text.Length)
                {
                    var next = ReadCodePoint(text, index, out var nextLength);
                    if (TextMeasurement.IsWhitespaceChar(text[index..(index + nextLength)]) ||
                        IsWordCodePoint(next) || IsHan(next))
                    {
                        break;
                    }

                    index += nextLength;
                }
            }

            result.Add(new EditorTextSegment(text[start..index], start, text));
        }

        return result;
    }

    private static bool IsWordLike(string segment)
    {
        if (segment.Length == 0 || TextMeasurement.IsWhitespaceChar(segment))
        {
            return false;
        }

        var codePoint = ReadCodePoint(segment, 0, out _);
        return IsHan(codePoint) || IsWordCodePoint(codePoint);
    }

    private static bool IsWordCodePoint(int codePoint)
    {
        if (codePoint == '_')
        {
            return true;
        }

        if (codePoint is >= 0xd800 and <= 0xdfff)
        {
            return false;
        }

        var category = Rune.GetUnicodeCategory(new Rune(codePoint));
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    private static bool IsHan(int codePoint) =>
        codePoint is >= 0x3400 and <= 0x4dbf ||
        codePoint is >= 0x4e00 and <= 0x9fff ||
        codePoint is >= 0xf900 and <= 0xfaff ||
        codePoint is >= 0x20000 and <= 0x2ffff;

    private static bool IsCjkBreak(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        var codePoint = ReadCodePoint(segment, 0, out _);
        return codePoint is >= 0x3400 and <= 0x4dbf ||
            codePoint is >= 0x4e00 and <= 0x9fff ||
            codePoint is >= 0xf900 and <= 0xfaff ||
            codePoint is >= 0x20000 and <= 0x2ffff ||
            codePoint is >= 0x3040 and <= 0x309f ||
            codePoint is >= 0x30a0 and <= 0x30ff ||
            codePoint is >= 0x31f0 and <= 0x31ff ||
            codePoint is >= 0x1100 and <= 0x11ff ||
            codePoint is >= 0x3130 and <= 0x318f ||
            codePoint is >= 0xa960 and <= 0xa97f ||
            codePoint is >= 0xac00 and <= 0xd7af ||
            codePoint is >= 0x3100 and <= 0x312f ||
            codePoint is >= 0x31a0 and <= 0x31bf;
    }

    private static int ReadCodePoint(string text, int index, out int length)
    {
        var first = text[index];
        if (char.IsHighSurrogate(first) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(first, text[index + 1]);
        }

        length = 1;
        return first;
    }

    private sealed class EditorState(List<string> lines, int cursorLine, int cursorCol)
    {
        public List<string> Lines { get; set; } = lines;

        public int CursorLine { get; set; } = cursorLine;

        public int CursorCol { get; set; } = cursorCol;

        public EditorState Clone() => new([.. Lines], CursorLine, CursorCol);
    }

    private sealed class EditorSnapshot(EditorState state, Dictionary<int, string> pastes, int pasteCounter)
    {
        public EditorState State { get; } = state.Clone();

        public Dictionary<int, string> Pastes { get; } = new(pastes);

        public int PasteCounter { get; } = pasteCounter;

        public EditorSnapshot Clone() => new(State, Pastes, PasteCounter);
    }

    private sealed record LayoutLine(string Text, bool HasCursor, int? CursorPos);

    private readonly record struct VisualLine(int LogicalLine, int StartCol, int Length);

    private readonly record struct AutocompleteRequestOptions(bool Force, bool ExplicitTab);

    private enum CursorPlacement
    {
        Start,
        End,
    }

    private enum SegmentMode
    {
        Word,
        Grapheme,
    }

    private enum AutocompleteState
    {
        Regular,
        Force,
    }

    private enum LastAction
    {
        Kill,
        Yank,
        TypeWord,
    }

    private enum JumpDirection
    {
        Forward,
        Backward,
    }
}
