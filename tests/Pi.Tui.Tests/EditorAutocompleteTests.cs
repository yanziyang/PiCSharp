using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorAutocompleteTests
{
    private static readonly string[] _models = ["gpt-4o", "gpt-4o-mini", "claude-sonnet"];

    [Fact(DisplayName = "auto-applies single force-file suggestion without showing menu")]
    public async Task Auto_applies_single_force_file_suggestion_without_showing_menu()
    {
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, options) =>
        {
            var prefix = lines[0][..cursorColumn];
            return options.Force && prefix == "Work"
                ? Suggestions(prefix, ("Workspace/", "Workspace/"))
                : null;
        }));
        Type(editor, "Work"); Assert.Equal("Work", editor.GetText()); editor.HandleInput("\t");
        await WaitAsync(() => editor.GetText() == "Workspace/");
        Assert.False(editor.IsShowingAutocomplete()); editor.HandleInput("\x1b[45;5u"); Assert.Equal("Work", editor.GetText());
    }

    [Fact(DisplayName = "shows menu when force-file has multiple suggestions")]
    public async Task Shows_menu_when_force_file_has_multiple_suggestions()
    {
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, options) =>
        {
            var prefix = lines[0][..cursorColumn];
            return options.Force && prefix == "src"
                ? Suggestions(prefix, ("src/", "src/"), ("src.txt", "src.txt"))
                : null;
        }));
        Type(editor, "src"); editor.HandleInput("\t"); await WaitAsync(editor.IsShowingAutocomplete);
        Assert.Equal("src", editor.GetText()); editor.HandleInput("\t");
        Assert.Equal("src/", editor.GetText()); Assert.False(editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "keeps suggestions open when typing in force mode (Tab-triggered)")]
    public async Task Keeps_suggestions_open_when_typing_in_force_mode_Tab_triggered()
    {
        var allFiles = new[]
        {
            new AutocompleteItem { Value = "readme.md", Label = "readme.md" },
            new AutocompleteItem { Value = "package.json", Label = "package.json" },
            new AutocompleteItem { Value = "src/", Label = "src/" },
            new AutocompleteItem { Value = "dist/", Label = "dist/" },
        };
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, options) =>
        {
            var prefix = lines[0][..cursorColumn];
            if (!options.Force && !prefix.Contains('/') && !prefix.StartsWith('.')) return null;
            var items = allFiles.Where(item => item.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return items.Length == 0 ? null : new AutocompleteSuggestions { Items = items, Prefix = prefix };
        }));
        editor.HandleInput("\t"); await WaitAsync(editor.IsShowingAutocomplete);
        editor.HandleInput("r"); await WaitAsync(() => editor.GetText() == "r" && editor.IsShowingAutocomplete());
        editor.HandleInput("e"); await WaitAsync(() => editor.GetText() == "re" && editor.IsShowingAutocomplete());
        editor.HandleInput("\t"); Assert.Equal("readme.md", editor.GetText()); Assert.False(editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "debounces @ autocomplete while typing")]
    public async Task Debounces_at_autocomplete_while_typing()
    {
        var calls = 0; var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, _) =>
        {
            calls++; var prefix = lines[0][..cursorColumn]; return Suggestions(prefix, ("@main.ts", "main.ts"));
        }));
        Type(editor, "@mai"); Assert.Equal(0, calls); Assert.False(editor.IsShowingAutocomplete());
        await WaitAsync(() => calls == 1 && editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "re-queries the autocomplete picker when the cursor moves back into the command name")]
    public async Task Re_queries_the_autocomplete_picker_when_the_cursor_moves_back_into_the_command_name()
    {
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, _) =>
        {
            var before = lines[0][..cursorColumn];
            if (!before.StartsWith('/')) return null;
            return before.Contains(' ')
                ? Suggestions(before[(before.IndexOf(' ') + 1)..], ("repo", "repo"), ("message", "message"), ("help", "help"))
                : Suggestions(before, ("cmd", "cmd"));
        }));
        foreach (var character in "/cmd ")
        {
            editor.HandleInput(character.ToString());
            await Task.Yield();
        }
        await WaitAsync(editor.IsShowingAutocomplete);
        var atArgument = RenderPlain(editor);
        Assert.Contains("repo", atArgument, StringComparison.Ordinal);
        editor.HandleInput("\x1b[D");
        await WaitAsync(() => !RenderPlain(editor).Contains("repo", StringComparison.Ordinal));
        var afterMove = RenderPlain(editor);
        Assert.DoesNotContain("repo", afterMove, StringComparison.Ordinal);
        Assert.DoesNotContain("message", afterMove, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "debounces # autocomplete while typing")]
    public async Task Debounces_hash_autocomplete_while_typing()
    {
        var calls = 0; var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, _) =>
        {
            calls++; return Suggestions(lines[0][..cursorColumn], ("#2983", "#2983"));
        }));
        Type(editor, "#298"); Assert.Equal(0, calls); Assert.False(editor.IsShowingAutocomplete());
        await WaitAsync(() => calls == 1 && editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "debounces custom triggerCharacters autocomplete while typing")]
    public async Task Debounces_custom_triggerCharacters_autocomplete_while_typing()
    {
        var calls = 0; var editor = CreateEditor();
        var provider = Provider((lines, _, cursorColumn, _) =>
        {
            calls++; return Suggestions(lines[0][..cursorColumn], ("$skill-name", "skill-name"));
        });
        provider.TriggerCharacters = ["$"];
        editor.SetAutocompleteProvider(provider); Type(editor, "$sk"); Assert.Equal(0, calls);
        await WaitAsync(() => calls == 1 && editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "resets custom triggerCharacters when provider changes")]
    public async Task Resets_custom_triggerCharacters_when_provider_changes()
    {
        var editor = CreateEditor();
        var first = Provider((_, _, _, _) => Suggestions("$", ("$skill-name", "skill-name")));
        first.TriggerCharacters = ["$"];
        editor.SetAutocompleteProvider(first);
        var calls = 0;
        editor.SetAutocompleteProvider(Provider((_, _, _, _) => { calls++; return Suggestions("$", ("$skill-name", "skill-name")); }));
        Type(editor, "$s");
        // The debounce window is itself the behavior under test: the removed trigger must not schedule a query.
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls); Assert.False(editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "aborts active @ autocomplete when typing continues")]
    public async Task Aborts_active_at_autocomplete_when_typing_continues()
    {
        var aborts = 0; var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(new TestAutocompleteProvider
        {
            GetSuggestionsHandler = async (_, _, _, options) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(500, options.Signal);
                    return Suggestions("@main", ("@main.ts", "main.ts"));
                }
                catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
                {
                    Interlocked.Increment(ref aborts);
                    return null;
                }
            },
        });
        Type(editor, "@mai"); await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        editor.HandleInput("n"); await WaitAsync(() => Volatile.Read(ref aborts) == 1);
    }

    [Fact(DisplayName = "hides autocomplete when backspacing slash command to empty")]
    public async Task Hides_autocomplete_when_backspacing_slash_command_to_empty()
    {
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, _) =>
        {
            var prefix = lines[0][..cursorColumn];
            if (!prefix.StartsWith('/')) return null;
            var query = prefix[1..];
            var items = new[]
            {
                new AutocompleteItem { Value = "/model", Label = "model", Description = "Change model" },
                new AutocompleteItem { Value = "/help", Label = "help", Description = "Show help" },
            }.Where(item => item.Value.StartsWith(query, StringComparison.Ordinal)).ToArray();
            return items.Length == 0 ? null : new AutocompleteSuggestions { Items = items, Prefix = prefix };
        }));
        editor.HandleInput("/"); await WaitAsync(editor.IsShowingAutocomplete); Assert.Equal("/", editor.GetText());
        editor.HandleInput("\x7f"); await WaitAsync(() => !editor.IsShowingAutocomplete()); Assert.Equal(string.Empty, editor.GetText());
    }

    [Fact(DisplayName = "applies exact typed slash-argument value on Enter even when first item is highlighted")]
    public async Task Applies_exact_typed_slash_argument_value_on_Enter_even_when_first_item_is_highlighted()
    {
        var editor = CreateEditor(); editor.SetAutocompleteProvider(ArgumentProvider(["one", "two", "three"], filter: true));
        Type(editor, "/argtest two"); await WaitAsync(editor.IsShowingAutocomplete); editor.HandleInput("\r");
        Assert.Equal("/argtest two", editor.GetText());
    }

    [Fact(DisplayName = "selects first prefix match on Enter when typed arg is not exact match")]
    public async Task Selects_first_prefix_match_on_Enter_when_typed_arg_is_not_exact_match()
    {
        var editor = CreateEditor(); editor.SetAutocompleteProvider(ArgumentProvider(["two", "three", "twelve"], filter: true));
        Type(editor, "/argtest t"); await WaitAsync(editor.IsShowingAutocomplete); editor.HandleInput("\r");
        Assert.Equal("/argtest two", editor.GetText());
    }

    [Fact(DisplayName = "highlights unique prefix match as user types (before full exact match)")]
    public async Task Highlights_unique_prefix_match_as_user_types_before_full_exact_match()
    {
        var editor = CreateEditor(); editor.SetAutocompleteProvider(ArgumentProvider(["one", "two", "three"], filter: false));
        Type(editor, "/argtest tw"); await WaitAsync(editor.IsShowingAutocomplete); editor.HandleInput("\r");
        Assert.Equal("/argtest two", editor.GetText());
    }

    [Fact(DisplayName = "selects first prefix match when multiple items match")]
    public async Task Selects_first_prefix_match_when_multiple_items_match()
    {
        var editor = CreateEditor(); editor.SetAutocompleteProvider(ArgumentProvider(["one", "two", "three"], filter: false));
        Type(editor, "/argtest t"); await WaitAsync(editor.IsShowingAutocomplete); editor.HandleInput("\r");
        Assert.Equal("/argtest two", editor.GetText());
    }

    [Fact(DisplayName = "works for built-in-style command argument completion path (model-like)")]
    public async Task Works_for_built_in_style_command_argument_completion_path_model_like()
    {
        var editor = CreateEditor();
        editor.SetAutocompleteProvider(Provider((lines, _, cursorColumn, _) =>
        {
            var before = lines[0][..cursorColumn]; const string command = "/model ";
            if (!before.StartsWith(command, StringComparison.Ordinal) || before.Length == command.Length) return null;
            var prefix = before[command.Length..];
            var items = _models
                .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
                .Select(value => new AutocompleteItem { Value = value, Label = value }).ToArray();
            return items.Length == 0 ? null : new AutocompleteSuggestions { Items = items, Prefix = prefix };
        }));
        Type(editor, "/model gpt-4o-mini"); await WaitAsync(editor.IsShowingAutocomplete); editor.HandleInput("\r");
        Assert.Equal("/model gpt-4o-mini", editor.GetText());
    }

    [Fact(DisplayName = "awaits async slash command argument completions")]
    public async Task Awaits_async_slash_command_argument_completions()
    {
        var editor = CreateEditor();
        var provider = new CombinedAutocompleteProvider(
        [
            new SlashCommand
            {
                Name = "load-skills",
                Description = "Load skills",
                GetArgumentCompletions = prefix => ValueTask.FromResult<AutocompleteItem[]?>(
                    prefix.StartsWith('s') ? [new AutocompleteItem { Value = "skill-a", Label = "skill-a" }] : null),
            },
        ], Environment.CurrentDirectory);
        editor.SetAutocompleteProvider(provider); editor.SetText("/load-skills "); editor.HandleInput("s");
        await WaitAsync(editor.IsShowingAutocomplete); editor.HandleInput("\t");
        Assert.Equal("/load-skills skill-a", editor.GetText()); Assert.False(editor.IsShowingAutocomplete());
    }

    [Fact(DisplayName = "ignores invalid slash command argument completion results")]
    public async Task Ignores_invalid_slash_command_argument_completion_results()
    {
        var editor = CreateEditor();
        var completionCalls = 0;
        var provider = new CombinedAutocompleteProvider(
        [
            new SlashCommand
            {
                Name = "load-skills",
                Description = "Load skills",
                GetArgumentCompletions = _ =>
                {
                    completionCalls++;
                    return ValueTask.FromResult<AutocompleteItem[]?>(null);
                },
            },
        ], Environment.CurrentDirectory);
        editor.SetAutocompleteProvider(provider); editor.SetText("/load-skills "); editor.HandleInput("s");
        // The typed C# callback cannot produce the upstream invalid non-array value; null is the rejected-result path.
        await WaitAsync(() => completionCalls == 1);
        Assert.False(editor.IsShowingAutocomplete()); Assert.Equal("/load-skills s", editor.GetText());
    }

    [Fact(DisplayName = "does not show argument completions when command has no argument completer")]
    public async Task Does_not_show_argument_completions_when_command_has_no_argument_completer()
    {
        var editor = CreateEditor();
        var provider = new CombinedAutocompleteProvider(
        [
            new SlashCommand { Name = "help", Description = "Show help" },
            new SlashCommand
            {
                Name = "model",
                Description = "Switch model",
                GetArgumentCompletions = _ => ValueTask.FromResult<AutocompleteItem[]?>(
                    [new AutocompleteItem { Value = "claude-opus", Label = "claude-opus" }]),
            },
        ], Environment.CurrentDirectory);
        editor.SetAutocompleteProvider(provider); Type(editor, "/he"); await WaitAsync(editor.IsShowingAutocomplete);
        editor.HandleInput("\t"); Assert.Equal("/help ", editor.GetText()); Assert.False(editor.IsShowingAutocomplete());
    }

    private static TestAutocompleteProvider Provider(
        Func<IReadOnlyList<string>, int, int, AutocompleteOptions, AutocompleteSuggestions?> getSuggestions) => new()
        {
            GetSuggestionsHandler = (lines, cursorLine, cursorColumn, options) =>
                ValueTask.FromResult(getSuggestions(lines, cursorLine, cursorColumn, options)),
        };

    private static TestAutocompleteProvider ArgumentProvider(IReadOnlyList<string> values, bool filter) =>
        Provider((lines, _, cursorColumn, _) =>
        {
            var before = lines[0][..cursorColumn]; const string command = "/argtest ";
            if (!before.StartsWith(command, StringComparison.Ordinal) || before.Length == command.Length) return null;
            var prefix = before[command.Length..];
            var selected = filter ? values.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)) : values;
            return new AutocompleteSuggestions
            {
                Items = selected.Select(value => new AutocompleteItem { Value = value, Label = value }).ToArray(),
                Prefix = prefix,
            };
        });

    private static AutocompleteSuggestions Suggestions(string prefix, params (string Value, string Label)[] items) => new()
    {
        Items = items.Select(item => new AutocompleteItem { Value = item.Value, Label = item.Label }).ToArray(),
        Prefix = prefix,
    };

    private static void Type(Editor editor, string text)
    {
        foreach (var character in text) editor.HandleInput(character.ToString());
    }

    private static string RenderPlain(Editor editor) =>
        string.Join('\n', editor.Render(80).Select(TextMeasurement.StripTerminalSequences));

    private static Task WaitAsync(Func<bool> condition) =>
        WaitForConditionAsync(condition, cancellationToken: TestContext.Current.CancellationToken);
}
