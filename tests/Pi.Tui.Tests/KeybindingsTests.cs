using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream default, override, and conflict-resolution cases.</summary>
public sealed class KeybindingsTests
{
    private static readonly string[] _submitKeys = ["enter", "ctrl+enter"];
    private static readonly string[] _selectorUpKeys = ["up", "ctrl+p"];

    [Fact(DisplayName = "binds Ctrl+J as a default newline alias")]
    public void Binds_ctrl_j_as_default_newline_alias()
    {
        var keybindings = new KeybindingsManager(TuiKeybindings.Definitions);

        Assert.Equal(["shift+enter", "ctrl+j"], keybindings.GetKeys("tui.input.newLine"));
        Assert.True(keybindings.Matches("\n", "tui.input.newLine"));
        Assert.True(keybindings.Matches("\x1b[106;5u", "tui.input.newLine"));
    }

    [Fact(DisplayName = "binds modified and unmodified editor viewport navigation")]
    public void Binds_modified_and_unmodified_editor_viewport_navigation()
    {
        var keybindings = new KeybindingsManager(TuiKeybindings.Definitions);

        Assert.Equal(["home", "ctrl+home", "ctrl+a"], keybindings.GetKeys("tui.editor.cursorLineStart"));
        Assert.Equal(["end", "ctrl+end", "ctrl+e"], keybindings.GetKeys("tui.editor.cursorLineEnd"));
        Assert.Equal(["pageUp", "ctrl+pageUp"], keybindings.GetKeys("tui.editor.pageUp"));
        Assert.Equal(["pageDown", "ctrl+pageDown"], keybindings.GetKeys("tui.editor.pageDown"));
    }

    [Fact(DisplayName = "leaves dedicated prompt history navigation unbound by default")]
    public void Leaves_prompt_history_navigation_unbound_by_default()
    {
        var keybindings = new KeybindingsManager(TuiKeybindings.Definitions);

        Assert.Empty(keybindings.GetKeys("tui.editor.historyPrevious"));
        Assert.Empty(keybindings.GetKeys("tui.editor.historyNext"));
    }

    [Fact(DisplayName = "binds unmodified terminal viewport shortcuts to alternate-screen navigation")]
    public void Binds_unmodified_terminal_viewport_shortcuts_to_alternate_screen_navigation()
    {
        var keybindings = new KeybindingsManager(TuiKeybindings.Definitions);

        Assert.Equal(["pageUp"], keybindings.GetKeys("tui.altScreen.pageUp"));
        Assert.Equal(["pageDown"], keybindings.GetKeys("tui.altScreen.pageDown"));
        Assert.Empty(keybindings.GetKeys("tui.altScreen.halfPageUp"));
        Assert.Empty(keybindings.GetKeys("tui.altScreen.halfPageDown"));
        Assert.Empty(keybindings.GetKeys("tui.altScreen.lineUp"));
        Assert.Empty(keybindings.GetKeys("tui.altScreen.lineDown"));
        Assert.Equal(["ctrl+shift+up", "ctrl+up"], keybindings.GetKeys("tui.altScreen.previousPrompt"));
        Assert.Equal(["ctrl+shift+down", "ctrl+down"], keybindings.GetKeys("tui.altScreen.nextPrompt"));
        Assert.Equal(["ctrl+shift+f"], keybindings.GetKeys("tui.altScreen.search"));
        Assert.Equal(["enter", "ctrl+g"], keybindings.GetKeys("tui.altScreen.searchNext"));
        Assert.Equal(["shift+enter", "ctrl+shift+g"], keybindings.GetKeys("tui.altScreen.searchPrevious"));
        Assert.Equal(["escape"], keybindings.GetKeys("tui.altScreen.searchClose"));
        Assert.Equal(["home"], keybindings.GetKeys("tui.altScreen.top"));
        Assert.Equal(["end"], keybindings.GetKeys("tui.altScreen.bottom"));
    }

    [Fact(DisplayName = "does not evict selector confirm when input submit is rebound")]
    public void Does_not_evict_selector_confirm_when_input_submit_is_rebound()
    {
        var keybindings = new KeybindingsManager(
            TuiKeybindings.Definitions,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tui.input.submit"] = _submitKeys,
            });

        Assert.Equal(["enter", "ctrl+enter"], keybindings.GetKeys("tui.input.submit"));
        Assert.Equal(["enter"], keybindings.GetKeys("tui.select.confirm"));
    }

    [Fact(DisplayName = "does not evict cursor bindings when another action reuses the same key")]
    public void Does_not_evict_cursor_bindings_when_another_action_reuses_the_same_key()
    {
        var keybindings = new KeybindingsManager(
            TuiKeybindings.Definitions,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tui.select.up"] = _selectorUpKeys,
            });

        Assert.Equal(["up", "ctrl+p"], keybindings.GetKeys("tui.select.up"));
        Assert.Equal(["up"], keybindings.GetKeys("tui.editor.cursorUp"));
    }

    [Fact(DisplayName = "still reports direct user binding conflicts without evicting defaults")]
    public void Reports_direct_user_binding_conflicts_without_evicting_defaults()
    {
        var keybindings = new KeybindingsManager(
            TuiKeybindings.Definitions,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tui.input.submit"] = "ctrl+x",
                ["tui.select.confirm"] = "ctrl+x",
            });

        var conflict = Assert.Single(keybindings.GetConflicts());
        Assert.Equal("ctrl+x", conflict.Key);
        Assert.Equal(["tui.input.submit", "tui.select.confirm"], conflict.Keybindings);
        Assert.Equal(["left", "ctrl+b"], keybindings.GetKeys("tui.editor.cursorLeft"));
    }
}
