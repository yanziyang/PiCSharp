namespace Pi.Tui;

/// <summary>Definition of one named keybinding and its default key identifiers.</summary>
public sealed class KeybindingDefinition
{
    /// <summary>Creates a definition from one or more default keys.</summary>
    public KeybindingDefinition(IEnumerable<string> defaultKeys, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(defaultKeys);
        DefaultKeys = defaultKeys.ToArray();
        Description = description;
    }

    /// <summary>Creates a definition from one default key.</summary>
    public KeybindingDefinition(string defaultKey, string? description = null)
        : this([defaultKey], description)
    {
    }

    /// <summary>The default key identifiers in their configured order.</summary>
    public IReadOnlyList<string> DefaultKeys { get; }

    /// <summary>Human-readable description of the action, when supplied.</summary>
    public string? Description { get; }
}

/// <summary>Runtime key conflict reported for user-defined bindings.</summary>
public sealed record KeybindingConflict(string Key, IReadOnlyList<string> Keybindings);

/// <summary>Configuration values accepted by <see cref="KeybindingsManager"/>.</summary>
public sealed class KeybindingsConfig : Dictionary<string, object?>
{
    /// <summary>Creates an empty keybinding configuration.</summary>
    public KeybindingsConfig()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>Creates a keybinding configuration from existing entries.</summary>
    public KeybindingsConfig(IEnumerable<KeyValuePair<string, object?>> entries)
        : base(entries, StringComparer.Ordinal)
    {
    }
}

/// <summary>Pi's default keybinding definitions.</summary>
public static class TuiKeybindings
{
    /// <summary>All default TUI action definitions in upstream declaration order.</summary>
    public static IReadOnlyDictionary<string, KeybindingDefinition> Definitions { get; } =
        new Dictionary<string, KeybindingDefinition>(StringComparer.Ordinal)
        {
            ["tui.editor.cursorUp"] = new("up", "Move cursor up"),
            ["tui.editor.cursorDown"] = new("down", "Move cursor down"),
            ["tui.editor.historyPrevious"] = new([], "Select previous prompt history entry"),
            ["tui.editor.historyNext"] = new([], "Select next prompt history entry"),
            ["tui.editor.cursorLeft"] = new(["left", "ctrl+b"], "Move cursor left"),
            ["tui.editor.cursorRight"] = new(["right", "ctrl+f"], "Move cursor right"),
            ["tui.editor.cursorWordLeft"] = new(["alt+left", "ctrl+left", "alt+b"], "Move cursor word left"),
            ["tui.editor.cursorWordRight"] = new(["alt+right", "ctrl+right", "alt+f"], "Move cursor word right"),
            ["tui.editor.cursorLineStart"] = new(["home", "ctrl+home", "ctrl+a"], "Move to line start"),
            ["tui.editor.cursorLineEnd"] = new(["end", "ctrl+end", "ctrl+e"], "Move to line end"),
            ["tui.editor.jumpForward"] = new("ctrl+]", "Jump forward to character"),
            ["tui.editor.jumpBackward"] = new("ctrl+alt+]", "Jump backward to character"),
            ["tui.editor.pageUp"] = new(["pageUp", "ctrl+pageUp"], "Page up"),
            ["tui.editor.pageDown"] = new(["pageDown", "ctrl+pageDown"], "Page down"),
            ["tui.editor.deleteCharBackward"] = new("backspace", "Delete character backward"),
            ["tui.editor.deleteCharForward"] = new(["delete", "ctrl+d"], "Delete character forward"),
            ["tui.editor.deleteWordBackward"] = new(["ctrl+w", "alt+backspace"], "Delete word backward"),
            ["tui.editor.deleteWordForward"] = new(["alt+d", "alt+delete"], "Delete word forward"),
            ["tui.editor.deleteToLineStart"] = new("ctrl+u", "Delete to line start"),
            ["tui.editor.deleteToLineEnd"] = new("ctrl+k", "Delete to line end"),
            ["tui.editor.yank"] = new("ctrl+y", "Yank"),
            ["tui.editor.yankPop"] = new("alt+y", "Yank pop"),
            ["tui.editor.undo"] = new("ctrl+-", "Undo"),
            ["tui.input.newLine"] = new(["shift+enter", "ctrl+j"], "Insert newline"),
            ["tui.input.submit"] = new("enter", "Submit input"),
            ["tui.input.tab"] = new("tab", "Tab / autocomplete"),
            ["tui.input.copy"] = new("ctrl+c", "Copy selection"),
            ["tui.select.up"] = new("up", "Move selection up"),
            ["tui.select.down"] = new("down", "Move selection down"),
            ["tui.select.pageUp"] = new("pageUp", "Selection page up"),
            ["tui.select.pageDown"] = new("pageDown", "Selection page down"),
            ["tui.select.confirm"] = new("enter", "Confirm selection"),
            ["tui.select.cancel"] = new(["escape", "ctrl+c"], "Cancel selection"),
            ["tui.altScreen.pageUp"] = new("pageUp", "Scroll viewport up one page"),
            ["tui.altScreen.pageDown"] = new("pageDown", "Scroll viewport down one page"),
            ["tui.altScreen.halfPageUp"] = new([], "Scroll viewport up half a page"),
            ["tui.altScreen.halfPageDown"] = new([], "Scroll viewport down half a page"),
            ["tui.altScreen.lineUp"] = new([], "Scroll viewport up one line"),
            ["tui.altScreen.lineDown"] = new([], "Scroll viewport down one line"),
            ["tui.altScreen.previousPrompt"] = new(["ctrl+shift+up", "ctrl+up"], "Jump to previous semantic prompt"),
            ["tui.altScreen.nextPrompt"] = new(["ctrl+shift+down", "ctrl+down"], "Jump to next semantic prompt"),
            ["tui.altScreen.search"] = new("ctrl+shift+f", "Search the primary scroll view"),
            ["tui.altScreen.searchNext"] = new(["enter", "ctrl+g"], "Select the next search match"),
            ["tui.altScreen.searchPrevious"] = new(["shift+enter", "ctrl+shift+g"], "Select the previous search match"),
            ["tui.altScreen.searchClose"] = new("escape", "Close transcript search"),
            ["tui.altScreen.top"] = new("home", "Scroll viewport to top"),
            ["tui.altScreen.bottom"] = new("end", "Scroll viewport to bottom"),
        };
}

/// <summary>Resolves named TUI actions to user and default key identifiers.</summary>
public sealed class KeybindingsManager
{
    private readonly IReadOnlyDictionary<string, KeybindingDefinition> _definitions;
    private IReadOnlyDictionary<string, object?> _userBindings;
    private readonly Dictionary<string, List<string>> _keysById = new(StringComparer.Ordinal);
    private readonly List<KeybindingConflict> _conflicts = [];
    private static KeybindingsManager? _globalKeybindings;

    /// <summary>Creates a manager using definitions and optional user overrides.</summary>
    public KeybindingsManager(
        IReadOnlyDictionary<string, KeybindingDefinition> definitions,
        IReadOnlyDictionary<string, object?>? userBindings = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
        _userBindings = userBindings ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        Rebuild();
    }

    /// <summary>Tests raw terminal data against one named action.</summary>
    public bool Matches(string data, string keybinding)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(keybinding);
        return _keysById.TryGetValue(keybinding, out var keys) && keys.Any(key => Keys.MatchesKey(data, key));
    }

    /// <summary>Returns the resolved key identifiers for one named action.</summary>
    public IReadOnlyList<string> GetKeys(string keybinding)
    {
        ArgumentNullException.ThrowIfNull(keybinding);
        return _keysById.TryGetValue(keybinding, out var keys) ? [.. keys] : [];
    }

    /// <summary>Returns the original definition for one named action.</summary>
    public KeybindingDefinition GetDefinition(string keybinding)
    {
        ArgumentNullException.ThrowIfNull(keybinding);
        return _definitions[keybinding];
    }

    /// <summary>Returns direct user-binding conflicts without evicting defaults.</summary>
    public IReadOnlyList<KeybindingConflict> GetConflicts() =>
        _conflicts.Select(static conflict => new KeybindingConflict(conflict.Key, [.. conflict.Keybindings])).ToArray();

    /// <summary>Replaces user overrides and rebuilds the resolved binding map.</summary>
    public void SetUserBindings(IReadOnlyDictionary<string, object?> userBindings)
    {
        ArgumentNullException.ThrowIfNull(userBindings);
        _userBindings = userBindings;
        Rebuild();
    }

    /// <summary>Returns a shallow copy of the configured user overrides.</summary>
    public IReadOnlyDictionary<string, object?> GetUserBindings() =>
        new Dictionary<string, object?>(_userBindings, StringComparer.Ordinal);

    /// <summary>Returns all resolved bindings using strings for single keys and arrays otherwise.</summary>
    public IReadOnlyDictionary<string, object?> GetResolvedBindings()
    {
        var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var id in _definitions.Keys)
        {
            var keys = _keysById.TryGetValue(id, out var configured) ? configured : [];
            resolved[id] = keys.Count == 1 ? keys[0] : (string[])[.. keys];
        }

        return resolved;
    }

    /// <summary>Sets the process-wide keybinding manager used by TUI components.</summary>
    public static void SetKeybindings(KeybindingsManager keybindings)
    {
        ArgumentNullException.ThrowIfNull(keybindings);
        Interlocked.Exchange(ref _globalKeybindings, keybindings);
    }

    /// <summary>Returns the process-wide keybinding manager, creating the defaults lazily.</summary>
    public static KeybindingsManager GetKeybindings()
    {
        var current = Volatile.Read(ref _globalKeybindings);
        if (current is not null) return current;

        var created = new KeybindingsManager(TuiKeybindings.Definitions);
        return Interlocked.CompareExchange(ref _globalKeybindings, created, null) ?? created;
    }

    private void Rebuild()
    {
        _keysById.Clear();
        _conflicts.Clear();

        var userClaims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in _userBindings)
        {
            if (!_definitions.ContainsKey(entry.Key)) continue;
            foreach (var key in NormalizeKeys(entry.Value))
            {
                if (!userClaims.TryGetValue(key, out var claimants))
                {
                    claimants = [];
                    userClaims[key] = claimants;
                }

                if (!claimants.Contains(entry.Key, StringComparer.Ordinal))
                {
                    claimants.Add(entry.Key);
                }
            }
        }

        foreach (var entry in userClaims)
        {
            if (entry.Value.Count > 1)
            {
                _conflicts.Add(new KeybindingConflict(entry.Key, [.. entry.Value]));
            }
        }

        foreach (var definition in _definitions)
        {
            var keys = _userBindings.TryGetValue(definition.Key, out var userKeys)
                ? NormalizeKeys(userKeys)
                : NormalizeKeys(definition.Value.DefaultKeys);
            _keysById[definition.Key] = keys;
        }
    }

    private static List<string> NormalizeKeys(object? value)
    {
        var values = value switch
        {
            null => [],
            string key => [key],
            KeyId key => [key.Value],
            IEnumerable<string> keys => keys,
            IEnumerable<KeyId> keyIds => keyIds.Select(static key => key.Value),
            _ => throw new ArgumentException("Keybinding values must be strings or string sequences.", nameof(value)),
        };

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var valueToValidate in values)
        {
            if (!KeyId.TryParse(valueToValidate, out _))
            {
                throw new ArgumentException($"Invalid key identifier: {valueToValidate}", nameof(value));
            }

            if (seen.Add(valueToValidate))
            {
                result.Add(valueToValidate);
            }
        }

        return result;
    }
}
