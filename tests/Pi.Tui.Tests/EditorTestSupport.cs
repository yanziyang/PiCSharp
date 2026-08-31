using Pi.Tui;

namespace Pi.Tui.Tests;

internal static class EditorTestSupport
{
    internal static readonly EditorTheme DefaultTheme = new()
    {
        BorderColor = static text => text,
        SelectList = new SelectListTheme
        {
            SelectedPrefix = static text => text,
            SelectedText = static text => text,
            Description = static text => text,
            ScrollInfo = static text => text,
            NoMatch = static text => text,
        },
    };

    internal static TuiMainScreen CreateTestTui(int columns = 80, int rows = 24) =>
        new(new MemoryTerminal(columns, rows));

    internal static Editor CreateEditor(int columns = 80, int rows = 24, EditorTheme? theme = null) =>
        new(CreateTestTui(columns, rows), theme ?? DefaultTheme);

    internal static AutocompleteCompletion ApplyCompletion(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteItem item,
        string prefix)
    {
        var line = lines[cursorLine];
        var before = line[..(cursorCol - prefix.Length)];
        var after = line[cursorCol..];
        var newLines = lines.ToArray();
        newLines[cursorLine] = before + item.Value + after;
        return new AutocompleteCompletion(
            newLines,
            cursorLine,
            cursorCol - prefix.Length + item.Value.Length);
    }

    internal static async Task WaitForConditionAsync(
        Func<bool> condition,
        int timeoutMilliseconds = 2000,
        CancellationToken cancellationToken = default)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.TickCount64 - started >= timeoutMilliseconds)
            {
                throw new TimeoutException("Condition was not satisfied before the test timeout.");
            }

            await Task.Delay(5, cancellationToken);
        }
    }
}

internal sealed class TestAutocompleteProvider : IAutocompleteProvider
{
    public IReadOnlyList<string>? TriggerCharacters { get; set; }

    public required Func<
        IReadOnlyList<string>,
        int,
        int,
        AutocompleteOptions,
        ValueTask<AutocompleteSuggestions?>> GetSuggestionsHandler
    { get; init; }

    public Func<
        IReadOnlyList<string>,
        int,
        int,
        AutocompleteItem,
        string,
        AutocompleteCompletion> ApplyCompletionHandler
    { get; init; } = EditorTestSupport.ApplyCompletion;

    public Func<IReadOnlyList<string>, int, int, bool>? ShouldTriggerFileCompletionHandler { get; init; }

    public ValueTask<AutocompleteSuggestions?> GetSuggestions(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteOptions options) =>
        GetSuggestionsHandler(lines, cursorLine, cursorCol, options);

    public AutocompleteCompletion ApplyCompletion(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteItem item,
        string prefix) =>
        ApplyCompletionHandler(lines, cursorLine, cursorCol, item, prefix);

    public bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol) =>
        ShouldTriggerFileCompletionHandler?.Invoke(lines, cursorLine, cursorCol) ?? true;
}
