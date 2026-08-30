using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class OverlayNonCapturingTests
{
    [Fact(DisplayName = "non-capturing overlay preserves focus on creation")]
    public async Task Non_capturing_overlay_preserves_focus_on_creation()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();

        tui.ShowOverlay(overlay, NonCapturingOptions());
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(overlay.Focused);
    }

    [Fact(DisplayName = "focus() transfers focus to the overlay")]
    public async Task Focus_transfers_focus_to_the_overlay()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.Focus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.False(editor.Focused);
        Assert.True(overlay.Focused);
        Assert.True(handle.IsFocused());
    }

    [Fact(DisplayName = "unfocus() restores previous focus")]
    public async Task Unfocus_restores_previous_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.Focus();
        handle.Unfocus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(overlay.Focused);
        Assert.False(handle.IsFocused());
    }

    [Fact(DisplayName = "setHidden(false) on non-capturing overlay does not auto-focus")]
    public async Task Set_hidden_false_on_non_capturing_overlay_does_not_auto_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.SetHidden(true);
        handle.SetHidden(false);
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(overlay.Focused);
    }

    [Fact(DisplayName = "hide() when overlay is not focused does not change focus")]
    public async Task Hide_when_overlay_is_not_focused_does_not_change_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();

        tui.ShowOverlay(overlay, NonCapturingOptions()).Hide();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
    }

    [Fact(DisplayName = "hide() when focused restores focus correctly")]
    public async Task Hide_when_focused_restores_focus_correctly()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.Focus();
        handle.Hide();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(overlay.Focused);
    }

    [Fact(DisplayName = "capturing overlay removed with non-capturing below restores focus to editor")]
    public async Task Capturing_overlay_removed_with_non_capturing_below_restores_focus_to_editor()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var nonCapturing = new FocusableOverlay(["NC"]);
        var capturing = new FocusableOverlay(["CAP"]);
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(nonCapturing, NonCapturingOptions());
        var handle = tui.ShowOverlay(capturing);
        Assert.True(capturing.Focused);

        handle.Hide();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(nonCapturing.Focused);
    }

    [Fact(DisplayName = "sub-overlay cleanup then hideOverlay restores focus and input to editor")]
    public async Task Sub_overlay_cleanup_then_hide_overlay_restores_focus_and_input_to_editor()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var timer = new FocusableOverlay(["TIMER"]);
        var controller = new FocusableOverlay(["CTRL"]);
        tui.SetFocus(editor);
        tui.Start();
        var timerHandle = tui.ShowOverlay(timer, NonCapturingOptions());
        tui.ShowOverlay(controller);
        Assert.True(controller.Focused);
        Assert.False(editor.Focused);

        timerHandle.Hide();
        tui.HideOverlay();
        await RenderAndFlushAsync(tui, terminal);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(controller.Focused);
        Assert.False(timer.Focused);
        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(controller.Inputs);
        Assert.Empty(timer.Inputs);
    }

    [Fact(DisplayName = "removed focused child overlay does not become parent overlay fallback")]
    public async Task Removed_focused_child_overlay_does_not_become_parent_overlay_fallback()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var child = new FocusableOverlay(["CHILD"]);
        var parent = new FocusableOverlay(["PARENT"]);
        tui.SetFocus(editor);
        tui.Start();
        var childHandle = tui.ShowOverlay(child, NonCapturingOptions());
        childHandle.Focus();
        var parentHandle = tui.ShowOverlay(parent);
        Assert.True(parent.Focused);

        childHandle.Hide();
        parentHandle.Hide();
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(child.Inputs);
        Assert.Empty(parent.Inputs);
        Assert.True(editor.Focused);
    }

    [Fact(DisplayName = "microtask-deferred sub-overlay pattern (showExtensionCustom simulation) restores focus")]
    public async Task Microtask_deferred_sub_overlay_pattern_restores_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var timer = new FocusableOverlay(["TIMER"]);
        var controller = new FocusableOverlay(["CTRL"]);
        tui.SetFocus(editor);
        tui.Start();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerHandle = tui.ShowOverlay(timer, NonCapturingOptions());
        await Task.Yield();
        tui.ShowOverlay(controller);
        await RenderAndFlushAsync(tui, terminal);
        Assert.True(controller.Focused);
        Assert.False(editor.Focused);

        timerHandle.Hide();
        tui.HideOverlay();
        completion.SetResult();
        await completion.Task;
        await RenderAndFlushAsync(tui, terminal);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(controller.Focused);
        Assert.False(timer.Focused);
        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(controller.Inputs);
    }

    [Fact(DisplayName = "handleInput redirection skips non-capturing overlays when focused overlay becomes invisible")]
    public async Task Handle_input_redirection_skips_non_capturing_overlays_when_focused_overlay_becomes_invisible()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var fallbackCapturing = new FocusableOverlay(["FALLBACK"]);
        var nonCapturing = new FocusableOverlay(["NC"]);
        var primary = new FocusableOverlay(["PRIMARY"]);
        var visible = true;
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(fallbackCapturing);
        tui.ShowOverlay(nonCapturing, NonCapturingOptions());
        tui.ShowOverlay(primary, new OverlayOptions { Visible = (_, _) => visible });
        Assert.True(primary.Focused);

        visible = false;
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Empty(primary.Inputs);
        Assert.Empty(nonCapturing.Inputs);
        Assert.Equal(["x"], fallbackCapturing.Inputs);
        Assert.True(fallbackCapturing.Focused);
    }

    [Fact(DisplayName = "active base focus replacement receives close input before overlay restore")]
    public async Task Active_base_focus_replacement_receives_close_input_before_overlay_restore()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var replacement = new FocusableOverlay(["REPLACEMENT"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        overlay.OnInput = data =>
        {
            if (data == "b")
            {
                tui.SetFocus(replacement);
            }
        };
        replacement.OnInput = data =>
        {
            if (data == "\r")
            {
                tui.SetFocus(editor);
            }
        };
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(overlay);
        Assert.True(overlay.Focused);

        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        Assert.True(replacement.Focused);
        terminal.SendInput("\r");
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal(["\r"], replacement.Inputs);
        Assert.Equal(["b"], overlay.Inputs);
        Assert.True(overlay.Focused);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal(["b", "x"], overlay.Inputs);
    }

    [Fact(DisplayName = "active replacement still receives input when it is another overlay preFocus")]
    public async Task Active_replacement_still_receives_input_when_it_is_another_overlay_pre_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var replacement = new FocusableOverlay(["REPLACEMENT"]);
        var passive = new FocusableOverlay(["PASSIVE"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        overlay.OnInput = data =>
        {
            if (data == "b")
            {
                tui.SetFocus(replacement);
            }
        };
        replacement.OnInput = data =>
        {
            if (data == "\r")
            {
                tui.SetFocus(editor);
            }
        };
        tui.SetFocus(editor);
        tui.Start();
        tui.SetFocus(replacement);
        tui.ShowOverlay(passive, NonCapturingOptions());
        tui.SetFocus(editor);
        tui.ShowOverlay(overlay);

        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        Assert.True(replacement.Focused);
        terminal.SendInput("1");
        terminal.SendInput("\r");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["1", "\r"], replacement.Inputs);
        Assert.Equal(["b"], overlay.Inputs);
        Assert.True(overlay.Focused);
    }

    [Fact(DisplayName = "blocked replacement can move focus internally before overlay restore")]
    public async Task Blocked_replacement_can_move_focus_internally_before_overlay_restore()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = new TuiMainScreen(terminal);
        var baseContainer = new Container();
        var editor = new FocusableOverlay(["EDITOR"]);
        var firstReplacement = new FocusableOverlay(["FIRST"]);
        var secondReplacement = new FocusableOverlay(["SECOND"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        overlay.OnInput = data =>
        {
            if (data == "b")
            {
                tui.SetFocus(firstReplacement);
            }
        };
        firstReplacement.OnInput = data =>
        {
            if (data == "n")
            {
                tui.SetFocus(secondReplacement);
            }
        };
        secondReplacement.OnInput = data =>
        {
            if (data == "\r")
            {
                baseContainer.Clear();
                baseContainer.AddChild(editor);
                tui.SetFocus(editor);
            }
        };
        baseContainer.AddChild(editor);
        baseContainer.AddChild(firstReplacement);
        baseContainer.AddChild(secondReplacement);
        tui.AddChild(baseContainer);
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(overlay);

        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        terminal.SendInput("n");
        await RenderAndFlushAsync(tui, terminal);
        terminal.SendInput("2");
        terminal.SendInput("\r");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["b"], overlay.Inputs);
        Assert.Equal(["n"], firstReplacement.Inputs);
        Assert.Equal(["2", "\r"], secondReplacement.Inputs);
        Assert.True(overlay.Focused);
    }

    [Fact(DisplayName = "removed replacement restores overlay even when overlay preFocus differs from next focus")]
    public async Task Removed_replacement_restores_overlay_even_when_overlay_pre_focus_differs_from_next_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = new TuiMainScreen(terminal);
        var baseContainer = new Container();
        var editor = new FocusableOverlay(["EDITOR"]);
        var palette = new FocusableOverlay(["PALETTE"]);
        var replacement = new FocusableOverlay(["REPLACEMENT"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        overlay.OnInput = data =>
        {
            if (data == "b")
            {
                tui.SetFocus(replacement);
            }
        };
        replacement.OnInput = data =>
        {
            if (data == "\r")
            {
                baseContainer.Clear();
                baseContainer.AddChild(editor);
                tui.SetFocus(editor);
            }
        };
        baseContainer.AddChild(editor);
        baseContainer.AddChild(palette);
        baseContainer.AddChild(replacement);
        tui.AddChild(baseContainer);
        tui.SetFocus(palette);
        tui.Start();
        tui.ShowOverlay(overlay);

        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        terminal.SendInput("\r");
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["b", "x"], overlay.Inputs);
        Assert.Equal(["\r"], replacement.Inputs);
        Assert.Empty(editor.Inputs);
        Assert.True(overlay.Focused);
    }

    [Fact(DisplayName = "unfocus target releases a blocked overlay while replacement remains focused")]
    public async Task Unfocus_target_releases_a_blocked_overlay_while_replacement_remains_focused()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var fallback = new FocusableOverlay(["FALLBACK"]);
        var target = new FocusableOverlay(["TARGET"]);
        var replacement = new FocusableOverlay(["REPLACEMENT"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        replacement.OnInput = data =>
        {
            if (data == "\r")
            {
                tui.SetFocus(fallback);
            }
        };
        tui.Start();
        OverlayHandle? handle = null;
        overlay.OnInput = data =>
        {
            if (data == "b")
            {
                tui.SetFocus(replacement);
                handle!.Unfocus(new OverlayUnfocusOptions { Target = target });
            }
        };
        handle = tui.ShowOverlay(overlay);

        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        Assert.True(replacement.Focused);
        terminal.SendInput("\r");
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["b"], overlay.Inputs);
        Assert.Equal(["\r"], replacement.Inputs);
        Assert.Empty(fallback.Inputs);
        Assert.Equal(["x"], target.Inputs);
    }

    [Fact(DisplayName = "handleInput restores focus to a visible focused overlay after base focus steal")]
    public async Task Handle_input_restores_focus_to_a_visible_focused_overlay_after_base_focus_steal()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var replacement = new FocusableOverlay(["REPLACEMENT"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(overlay);
        Assert.True(overlay.Focused);

        tui.SetFocus(replacement);
        tui.SetFocus(editor);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], overlay.Inputs);
        Assert.Empty(editor.Inputs);
        Assert.True(overlay.Focused);
    }

    [Fact(DisplayName = "handleInput restores focus to explicitly focused raw sub-overlay after base focus steal")]
    public async Task Handle_input_restores_focus_to_explicitly_focused_raw_sub_overlay_after_base_focus_steal()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var controller = new FocusableOverlay(["CONTROLLER"]);
        var subOverlay = new FocusableOverlay(["SUB"]);
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(controller);
        var subHandle = tui.ShowOverlay(subOverlay, NonCapturingOptions());
        subHandle.Focus();

        tui.SetFocus(editor);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], subOverlay.Inputs);
        Assert.Empty(controller.Inputs);
        Assert.Empty(editor.Inputs);
    }

    [Fact(DisplayName = "passive non-capturing overlay does not regain input after base focus")]
    public async Task Passive_non_capturing_overlay_does_not_regain_input_after_base_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var passive = new FocusableOverlay(["PASSIVE"]);
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(passive, NonCapturingOptions());

        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(passive.Inputs);
        Assert.True(editor.Focused);
    }

    [Fact(DisplayName = "explicitly focused non-capturing overlay regains input after base focus steal")]
    public async Task Explicitly_focused_non_capturing_overlay_regains_input_after_base_focus_steal()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["NC"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());
        handle.Focus();

        tui.SetFocus(editor);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], overlay.Inputs);
        Assert.Empty(editor.Inputs);
    }

    [Fact(DisplayName = "unfocus() prevents visible overlay from regaining input")]
    public async Task Unfocus_prevents_visible_overlay_from_regaining_input()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay);

        handle.Unfocus();
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(overlay.Inputs);
        Assert.True(editor.Focused);
    }

    [Fact(DisplayName = "setFocus(null) explicitly clears visible overlay restore")]
    public async Task Set_focus_null_explicitly_clears_visible_overlay_restore()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.Start();
        tui.ShowOverlay(overlay);

        tui.SetFocus(null);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Empty(overlay.Inputs);
        Assert.False(overlay.Focused);
    }

    [Fact(DisplayName = "blocked replacement setFocus(null) resumes the visible overlay")]
    public async Task Blocked_replacement_set_focus_null_resumes_the_visible_overlay()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var replacement = new FocusableOverlay(["REPLACEMENT"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        replacement.OnInput = data =>
        {
            if (data == "\r")
            {
                tui.SetFocus(null);
            }
        };
        overlay.OnInput = data =>
        {
            if (data == "b")
            {
                tui.SetFocus(replacement);
            }
        };
        tui.Start();
        tui.ShowOverlay(overlay);

        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        terminal.SendInput("\r");
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["\r"], replacement.Inputs);
        Assert.Equal(["b", "x"], overlay.Inputs);
        Assert.True(overlay.Focused);
    }

    [Fact(DisplayName = "temporarily invisible focused overlay falls back without losing restore eligibility")]
    public async Task Temporarily_invisible_focused_overlay_falls_back_without_losing_restore_eligibility()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        var visible = true;
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(overlay, new OverlayOptions { Visible = (_, _) => visible });
        tui.SetFocus(editor);

        visible = false;
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(overlay.Inputs);
        visible = true;
        terminal.SendInput("y");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], editor.Inputs);
        Assert.Equal(["y"], overlay.Inputs);
    }

    [Fact(DisplayName = "temporarily invisible focused overlay with null preFocus restores when visible again")]
    public async Task Temporarily_invisible_focused_overlay_with_null_pre_focus_restores_when_visible_again()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        var visible = true;
        tui.Start();
        tui.ShowOverlay(overlay, new OverlayOptions { Visible = (_, _) => visible });

        visible = false;
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);
        Assert.Empty(overlay.Inputs);
        visible = true;
        terminal.SendInput("y");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["y"], overlay.Inputs);
    }

    [Fact(DisplayName = "cyclic overlay preFocus ancestry does not hang focus changes")]
    public async Task Cyclic_overlay_pre_focus_ancestry_does_not_hang_focus_changes()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(overlay);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.Focus();
        tui.SetFocus(editor);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], editor.Inputs);
        Assert.Empty(overlay.Inputs);
    }

    [Fact(DisplayName = "handleInput restores the focus-order top overlay after base focus steal")]
    public async Task Handle_input_restores_the_focus_order_top_overlay_after_base_focus_steal()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var lower = new FocusableOverlay(["LOWER"]);
        var upper = new FocusableOverlay(["UPPER"]);
        tui.SetFocus(editor);
        tui.Start();
        var lowerHandle = tui.ShowOverlay(lower);
        tui.ShowOverlay(upper);
        lowerHandle.Focus();

        tui.SetFocus(editor);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], lower.Inputs);
        Assert.Empty(upper.Inputs);
        Assert.Empty(editor.Inputs);
    }

    [Fact(DisplayName = "hideOverlay() does not reassign focus when topmost overlay is non-capturing")]
    public async Task Hide_overlay_does_not_reassign_focus_when_topmost_overlay_is_non_capturing()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var capturing = new FocusableOverlay(["CAP"]);
        var nonCapturing = new FocusableOverlay(["NC"]);
        tui.SetFocus(editor);
        tui.Start();
        tui.ShowOverlay(capturing);
        tui.ShowOverlay(nonCapturing, NonCapturingOptions());
        Assert.True(capturing.Focused);

        tui.HideOverlay();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(capturing.Focused);
    }

    [Fact(DisplayName = "multiple capturing and non-capturing overlays restore focus through removals")]
    public async Task Multiple_capturing_and_non_capturing_overlays_restore_focus_through_removals()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var c1 = new FocusableOverlay(["C1"]);
        var n1 = new FocusableOverlay(["N1"]);
        var c2 = new FocusableOverlay(["C2"]);
        var n2 = new FocusableOverlay(["N2"]);
        tui.SetFocus(editor);
        tui.Start();
        var c1Handle = tui.ShowOverlay(c1);
        tui.ShowOverlay(n1, NonCapturingOptions());
        var c2Handle = tui.ShowOverlay(c2);
        tui.ShowOverlay(n2, NonCapturingOptions());
        Assert.True(c2.Focused);

        c2Handle.Hide();
        await RenderAndFlushAsync(tui, terminal);
        Assert.True(c1.Focused);
        c1Handle.Hide();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
    }

    [Fact(DisplayName = "capturing overlay unfocus() on topmost capturing overlay falls back to preFocus")]
    public async Task Capturing_overlay_unfocus_on_topmost_capturing_overlay_falls_back_to_pre_focus()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var capturing = new FocusableOverlay(["CAP"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(capturing);
        Assert.True(capturing.Focused);

        handle.Unfocus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(capturing.Focused);
    }

    [Fact(DisplayName = "focus() on hidden overlay is a no-op")]
    public async Task Focus_on_hidden_overlay_is_a_no_op()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.SetHidden(true);
        handle.Focus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(handle.IsFocused());
    }

    [Fact(DisplayName = "focus() after hide() is a no-op")]
    public async Task Focus_after_hide_is_a_no_op()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.Hide();
        handle.Focus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(handle.IsFocused());
    }

    [Fact(DisplayName = "unfocus() when overlay does not have focus is a no-op")]
    public async Task Unfocus_when_overlay_does_not_have_focus_is_a_no_op()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.SetFocus(editor);
        tui.Start();
        var handle = tui.ShowOverlay(overlay, NonCapturingOptions());

        handle.Unfocus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(overlay.Focused);
    }

    [Fact(DisplayName = "unfocus() with null preFocus clears focus and does not route input back to overlay")]
    public async Task Unfocus_with_null_pre_focus_clears_focus_and_does_not_route_input_back_to_overlay()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.Start();
        var handle = tui.ShowOverlay(overlay);
        Assert.True(overlay.Focused);

        handle.Unfocus();
        Assert.False(overlay.Focused);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Empty(overlay.Inputs);
        Assert.False(handle.IsFocused());
    }

    [Fact(DisplayName = "toggle focus between non-capturing overlays then unfocus returns to editor")]
    public async Task Toggle_focus_between_non_capturing_overlays_then_unfocus_returns_to_editor()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var a = new FocusableOverlay(["A"]);
        var b = new FocusableOverlay(["B"]);
        tui.SetFocus(editor);
        tui.Start();
        var aHandle = tui.ShowOverlay(a, NonCapturingOptions());
        var bHandle = tui.ShowOverlay(b, NonCapturingOptions());

        aHandle.Focus();
        bHandle.Focus();
        aHandle.Focus();
        aHandle.Unfocus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.True(editor.Focused);
        Assert.False(a.Focused);
        Assert.False(b.Focused);
    }

    [Fact(DisplayName = "explicit unfocus target supports cycling between three overlays and editor")]
    public async Task Explicit_unfocus_target_supports_cycling_between_three_overlays_and_editor()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var a = new FocusableOverlay(["A"]);
        var b = new FocusableOverlay(["B"]);
        var c = new FocusableOverlay(["C"]);
        tui.SetFocus(editor);
        tui.Start();
        var aHandle = tui.ShowOverlay(a);
        var bHandle = tui.ShowOverlay(b);
        var cHandle = tui.ShowOverlay(c);

        aHandle.Focus();
        terminal.SendInput("a");
        await RenderAndFlushAsync(tui, terminal);
        bHandle.Focus();
        terminal.SendInput("b");
        await RenderAndFlushAsync(tui, terminal);
        cHandle.Focus();
        terminal.SendInput("c");
        await RenderAndFlushAsync(tui, terminal);
        cHandle.Unfocus(new OverlayUnfocusOptions { Target = editor });
        terminal.SendInput("e");
        await RenderAndFlushAsync(tui, terminal);
        aHandle.Focus();
        terminal.SendInput("A");
        await RenderAndFlushAsync(tui, terminal);
        aHandle.Unfocus(new OverlayUnfocusOptions { Target = editor });
        terminal.SendInput("E");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["a", "A"], a.Inputs);
        Assert.Equal(["b"], b.Inputs);
        Assert.Equal(["c"], c.Inputs);
        Assert.Equal(["e", "E"], editor.Inputs);
        Assert.True(editor.Focused);
    }

    [Fact(DisplayName = "explicit null unfocus target clears focus without restoring overlays")]
    public async Task Explicit_null_unfocus_target_clears_focus_without_restoring_overlays()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var overlay = new FocusableOverlay(["OVERLAY"]);
        tui.Start();
        var handle = tui.ShowOverlay(overlay);

        handle.Unfocus(new OverlayUnfocusOptions { Target = null });
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Empty(overlay.Inputs);
        Assert.False(handle.IsFocused());
    }

    [Fact(DisplayName = "hiding focused overlay falls back to next visual-frontmost overlay")]
    public async Task Hiding_focused_overlay_falls_back_to_next_visual_frontmost_overlay()
    {
        var terminal = new MemoryTerminal(80, 24);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        var a = new FocusableOverlay(["A"]);
        var b = new FocusableOverlay(["B"]);
        var c = new FocusableOverlay(["C"]);
        tui.SetFocus(editor);
        tui.Start();
        var aHandle = tui.ShowOverlay(a);
        var bHandle = tui.ShowOverlay(b);
        tui.ShowOverlay(c);

        aHandle.Focus();
        bHandle.Focus();
        bHandle.SetHidden(true);
        terminal.SendInput("x");
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal(["x"], a.Inputs);
        Assert.Empty(c.Inputs);
        Assert.True(a.Focused);
    }

    [Fact(DisplayName = "focus() on already-focused overlay bumps visual order")]
    public async Task Focus_on_already_focused_overlay_bumps_visual_order()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        tui.SetFocus(editor);
        tui.Start();
        var aHandle = tui.ShowOverlay(new StaticOverlay(["A"]), PositionedNonCapturingOptions());
        tui.ShowOverlay(new StaticOverlay(["B"]), PositionedNonCapturingOptions());
        aHandle.Focus();
        tui.ShowOverlay(new StaticOverlay(["C"]), PositionedNonCapturingOptions());

        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('C', terminal.GetViewport()[0][0]);
        aHandle.Focus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal('A', terminal.GetViewport()[0][0]);
        Assert.True(aHandle.IsFocused());
    }

    [Fact(DisplayName = "default rendering order for overlapping overlays follows creation order")]
    public async Task Default_rendering_order_for_overlapping_overlays_follows_creation_order()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = CreateTui(terminal);
        tui.Start();
        tui.ShowOverlay(new StaticOverlay(["A"]), PositionedNonCapturingOptions());
        tui.ShowOverlay(new StaticOverlay(["B"]), PositionedNonCapturingOptions());

        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal('B', terminal.GetViewport()[0][0]);
    }

    [Fact(DisplayName = "focus() on lower overlay renders it on top")]
    public async Task Focus_on_lower_overlay_renders_it_on_top()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = CreateTui(terminal);
        tui.Start();
        var lower = tui.ShowOverlay(new StaticOverlay(["A"]), PositionedNonCapturingOptions());
        tui.ShowOverlay(new StaticOverlay(["B"]), PositionedNonCapturingOptions());
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('B', terminal.GetViewport()[0][0]);

        lower.Focus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal('A', terminal.GetViewport()[0][0]);
    }

    [Fact(DisplayName = "focusing middle overlay places it on top while preserving others relative order")]
    public async Task Focusing_middle_overlay_places_it_on_top_while_preserving_others_relative_order()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = CreateTui(terminal);
        tui.Start();
        tui.ShowOverlay(new StaticOverlay(["A"]), PositionedNonCapturingOptions());
        var middle = tui.ShowOverlay(new StaticOverlay(["B"]), PositionedNonCapturingOptions());
        var top = tui.ShowOverlay(new StaticOverlay(["C"]), PositionedNonCapturingOptions());
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('C', terminal.GetViewport()[0][0]);

        middle.Focus();
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('B', terminal.GetViewport()[0][0]);
        middle.Hide();
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('C', terminal.GetViewport()[0][0]);
        top.Hide();
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal('A', terminal.GetViewport()[0][0]);
    }

    [Fact(DisplayName = "capturing overlay hidden and shown again renders on top after unhide")]
    public async Task Capturing_overlay_hidden_and_shown_again_renders_on_top_after_unhide()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = CreateTui(terminal);
        tui.Start();
        tui.ShowOverlay(new StaticOverlay(["A"]), PositionedNonCapturingOptions());
        var capturing = tui.ShowOverlay(
            new StaticOverlay(["B"]),
            new OverlayOptions { Row = 0, Col = 0, Width = 1 });
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('B', terminal.GetViewport()[0][0]);

        capturing.SetHidden(true);
        tui.ShowOverlay(new StaticOverlay(["C"]), PositionedNonCapturingOptions());
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('C', terminal.GetViewport()[0][0]);
        capturing.SetHidden(false);
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal('B', terminal.GetViewport()[0][0]);
    }

    [Fact(DisplayName = "unfocus() does not change visual order until another overlay is focused")]
    public async Task Unfocus_does_not_change_visual_order_until_another_overlay_is_focused()
    {
        var terminal = new MemoryTerminal(20, 6);
        using var tui = CreateTui(terminal);
        var editor = new FocusableOverlay(["EDITOR"]);
        tui.SetFocus(editor);
        tui.Start();
        var a = tui.ShowOverlay(new StaticOverlay(["A"]), PositionedNonCapturingOptions());
        var b = tui.ShowOverlay(new StaticOverlay(["B"]), PositionedNonCapturingOptions());
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('B', terminal.GetViewport()[0][0]);

        a.Focus();
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('A', terminal.GetViewport()[0][0]);
        a.Unfocus();
        await RenderAndFlushAsync(tui, terminal);
        Assert.Equal('A', terminal.GetViewport()[0][0]);
        b.Focus();
        await RenderAndFlushAsync(tui, terminal);

        Assert.Equal('B', terminal.GetViewport()[0][0]);
    }

    private static TuiMainScreen CreateTui(MemoryTerminal terminal)
    {
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(new EmptyContent());
        return tui;
    }

    private static OverlayOptions NonCapturingOptions() => new() { NonCapturing = true };

    private static OverlayOptions PositionedNonCapturingOptions() => new()
    {
        Row = 0,
        Col = 0,
        Width = 1,
        NonCapturing = true,
    };

    private static async Task RenderAndFlushAsync(TuiMainScreen tui, MemoryTerminal terminal)
    {
        tui.RequestRender(force: true);
        await terminal.WaitForRenderAsync();
    }

    private sealed class StaticOverlay(IReadOnlyList<string> lines) : IComponent
    {
        public IReadOnlyList<string> Render(int width) => lines;

        public void Invalidate() { }
    }

    private sealed class EmptyContent : IComponent
    {
        public IReadOnlyList<string> Render(int width) => [];

        public void Invalidate() { }
    }

    private sealed class FocusableOverlay(IReadOnlyList<string> lines) : IComponent, IFocusable
    {
        internal List<string> Inputs { get; } = [];

        internal Action<string>? OnInput { get; set; }

        public bool Focused { get; set; }

        public void HandleInput(string data)
        {
            Inputs.Add(data);
            OnInput?.Invoke(data);
        }

        public IReadOnlyList<string> Render(int width) => lines;

        public void Invalidate() { }
    }
}
