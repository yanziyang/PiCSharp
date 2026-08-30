using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream legacy, xterm, and Kitty keyboard cases.</summary>
public sealed class KeysTests
{
    [Fact(DisplayName = "should match Ctrl+c when pressing Ctrl+С (Cyrillic) with base layout key")]
    public void Matches_cyrillic_ctrl_c_with_base_layout_key()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[1089::99;5u", "ctrl+c")));
    }

    [Fact(DisplayName = "should match Ctrl+d when pressing Ctrl+В (Cyrillic) with base layout key")]
    public void Matches_cyrillic_ctrl_d_with_base_layout_key()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[1074::100;5u", "ctrl+d")));
    }

    [Fact(DisplayName = "should match Ctrl+z when pressing Ctrl+Я (Cyrillic) with base layout key")]
    public void Matches_cyrillic_ctrl_z_with_base_layout_key()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[1103::122;5u", "ctrl+z")));
    }

    [Fact(DisplayName = "should match Ctrl+Shift+p with base layout key")]
    public void Matches_cyrillic_ctrl_shift_p_with_base_layout_key()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[1079::112;6u", "ctrl+shift+p")));
    }

    [Fact(DisplayName = "should still match direct codepoint when no base layout key")]
    public void Matches_direct_codepoint_without_base_layout_key()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[99;5u", "ctrl+c")));
    }

    [Fact(DisplayName = "should match super-modified Kitty bindings, including combined modifiers")]
    public void Matches_super_modified_kitty_bindings()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[107;9u", "super+k"));
            Assert.True(Keys.MatchesKey("\x1b[13;9u", "super+enter"));
            Assert.True(Keys.MatchesKey("\x1b[107;13u", Key.CtrlSuper("k")));
            Assert.True(Keys.MatchesKey("\x1b[107;13u", "ctrl+super+k"));
            Assert.True(Keys.MatchesKey("\x1b[107;14u", "ctrl+shift+super+k"));
            Assert.False(Keys.MatchesKey("\x1b[107;13u", "super+k"));
            Assert.Equal("super+k", Keys.ParseKey("\x1b[107;9u"));
            Assert.Equal("super+enter", Keys.ParseKey("\x1b[13;9u"));
            Assert.Equal("ctrl+super+k", Keys.ParseKey("\x1b[107;13u"));
            Assert.Equal("shift+ctrl+super+k", Keys.ParseKey("\x1b[107;14u"));
        });
    }

    [Fact(DisplayName = "should match digit bindings via Kitty CSI-u")]
    public void Matches_digit_bindings_via_kitty_csi_u()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[49u", "1"));
            Assert.True(Keys.MatchesKey("\x1b[49;5u", "ctrl+1"));
            Assert.False(Keys.MatchesKey("\x1b[49;5u", "ctrl+2"));
            Assert.Equal("1", Keys.ParseKey("\x1b[49u"));
            Assert.Equal("ctrl+1", Keys.ParseKey("\x1b[49;5u"));
        });
    }

    [Fact(DisplayName = "should normalize Kitty keypad functional keys to logical digits, symbols, and navigation")]
    public void Normalizes_kitty_keypad_functional_keys()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[57400u", "1"));
            Assert.True(Keys.MatchesKey("\x1b[57410u", "/"));
            Assert.True(Keys.MatchesKey("\x1b[57417u", "left"));
            Assert.True(Keys.MatchesKey("\x1b[57426u", "delete"));
            Assert.Equal("0", Keys.ParseKey("\x1b[57399u"));
            Assert.Equal(".", Keys.ParseKey("\x1b[57409u"));
            Assert.Equal("+", Keys.ParseKey("\x1b[57413u"));
            Assert.Equal(",", Keys.ParseKey("\x1b[57416u"));
            Assert.Equal("left", Keys.ParseKey("\x1b[57417u"));
            Assert.Equal("right", Keys.ParseKey("\x1b[57418u"));
            Assert.Equal("up", Keys.ParseKey("\x1b[57419u"));
            Assert.Equal("down", Keys.ParseKey("\x1b[57420u"));
            Assert.Equal("pageUp", Keys.ParseKey("\x1b[57421u"));
            Assert.Equal("pageDown", Keys.ParseKey("\x1b[57422u"));
            Assert.Equal("home", Keys.ParseKey("\x1b[57423u"));
            Assert.Equal("end", Keys.ParseKey("\x1b[57424u"));
            Assert.Equal("insert", Keys.ParseKey("\x1b[57425u"));
            Assert.Equal("delete", Keys.ParseKey("\x1b[57426u"));
        });
    }

    [Fact(DisplayName = "should handle shifted key in format")]
    public void Handles_shifted_key_format()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[99:67:99;2u", "shift+c")));
    }

    [Fact(DisplayName = "should handle event type in format")]
    public void Handles_event_type_format()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[1089::99;5:3u", "ctrl+c")));
    }

    [Fact(DisplayName = "should handle full format with shifted key, base key, and event type")]
    public void Handles_full_kitty_format()
    {
        WithKitty(true, () => Assert.True(Keys.MatchesKey("\x1b[1089:1057:99;6:2u", "ctrl+shift+c")));
    }

    [Fact(DisplayName = "should prefer codepoint for Latin letters even when base layout differs")]
    public void Prefers_codepoint_for_latin_letters()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[107::118;5u", "ctrl+k"));
            Assert.False(Keys.MatchesKey("\x1b[107::118;5u", "ctrl+v"));
        });
    }

    [Fact(DisplayName = "should prefer codepoint for symbol keys even when base layout differs")]
    public void Prefers_codepoint_for_symbols()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[47::91;5u", "ctrl+/"));
            Assert.False(Keys.MatchesKey("\x1b[47::91;5u", "ctrl+["));
        });
    }

    [Fact(DisplayName = "should not match wrong key even with base layout")]
    public void Rejects_wrong_key_with_base_layout()
    {
        WithKitty(true, () => Assert.False(Keys.MatchesKey("\x1b[1089::99;5u", "ctrl+d")));
    }

    [Fact(DisplayName = "should not match wrong modifiers even with base layout")]
    public void Rejects_wrong_modifiers_with_base_layout()
    {
        WithKitty(true, () => Assert.False(Keys.MatchesKey("\x1b[1089::99;5u", "ctrl+shift+c")));
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Ctrl+c")]
    public void Matches_modify_other_keys_ctrl_c()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;5;99~", "ctrl+c"));
            Assert.Equal("ctrl+c", Keys.ParseKey("\x1b[27;5;99~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Ctrl+d")]
    public void Matches_modify_other_keys_ctrl_d()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;5;100~", "ctrl+d"));
            Assert.Equal("ctrl+d", Keys.ParseKey("\x1b[27;5;100~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Ctrl+z")]
    public void Matches_modify_other_keys_ctrl_z()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;5;122~", "ctrl+z"));
            Assert.Equal("ctrl+z", Keys.ParseKey("\x1b[27;5;122~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Enter variants")]
    public void Matches_modify_other_keys_enter_variants()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;5;13~", "ctrl+enter"));
            Assert.True(Keys.MatchesKey("\x1b[27;2;13~", "shift+enter"));
            Assert.True(Keys.MatchesKey("\x1b[27;3;13~", "alt+enter"));
            Assert.Equal("ctrl+enter", Keys.ParseKey("\x1b[27;5;13~"));
            Assert.Equal("shift+enter", Keys.ParseKey("\x1b[27;2;13~"));
            Assert.Equal("alt+enter", Keys.ParseKey("\x1b[27;3;13~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Tab variants")]
    public void Matches_modify_other_keys_tab_variants()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;2;9~", "shift+tab"));
            Assert.True(Keys.MatchesKey("\x1b[27;5;9~", "ctrl+tab"));
            Assert.True(Keys.MatchesKey("\x1b[27;3;9~", "alt+tab"));
            Assert.Equal("shift+tab", Keys.ParseKey("\x1b[27;2;9~"));
            Assert.Equal("ctrl+tab", Keys.ParseKey("\x1b[27;5;9~"));
            Assert.Equal("alt+tab", Keys.ParseKey("\x1b[27;3;9~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Backspace variants")]
    public void Matches_modify_other_keys_backspace_variants()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;1;127~", "backspace"));
            Assert.True(Keys.MatchesKey("\x1b[27;5;127~", "ctrl+backspace"));
            Assert.True(Keys.MatchesKey("\x1b[27;3;127~", "alt+backspace"));
            Assert.Equal("backspace", Keys.ParseKey("\x1b[27;1;127~"));
            Assert.Equal("ctrl+backspace", Keys.ParseKey("\x1b[27;5;127~"));
            Assert.Equal("alt+backspace", Keys.ParseKey("\x1b[27;3;127~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Escape")]
    public void Matches_modify_other_keys_escape()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;1;27~", "escape"));
            Assert.Equal("escape", Keys.ParseKey("\x1b[27;1;27~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys Space variants")]
    public void Matches_modify_other_keys_space_variants()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;1;32~", "space"));
            Assert.True(Keys.MatchesKey("\x1b[27;5;32~", "ctrl+space"));
            Assert.Equal("space", Keys.ParseKey("\x1b[27;1;32~"));
            Assert.Equal("ctrl+space", Keys.ParseKey("\x1b[27;5;32~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys symbol combos")]
    public void Matches_modify_other_keys_symbol_combos()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;5;47~", "ctrl+/"));
            Assert.Equal("ctrl+/", Keys.ParseKey("\x1b[27;5;47~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys digit combos")]
    public void Matches_modify_other_keys_digit_combos()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;5;49~", "ctrl+1"));
            Assert.True(Keys.MatchesKey("\x1b[27;2;49~", "shift+1"));
            Assert.Equal("ctrl+1", Keys.ParseKey("\x1b[27;5;49~"));
            Assert.Equal("shift+1", Keys.ParseKey("\x1b[27;2;49~"));
        });
    }

    [Fact(DisplayName = "should match xterm modifyOtherKeys shifted uppercase letters")]
    public void Matches_modify_other_keys_shifted_uppercase_letters()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;2;69~", "shift+e"));
            Assert.True(Keys.MatchesKey("\x1b[27;6;69~", "ctrl+shift+e"));
            Assert.Equal("shift+e", Keys.ParseKey("\x1b[27;2;69~"));
            Assert.Equal("shift+ctrl+e", Keys.ParseKey("\x1b[27;6;69~"));
        });
    }

    [Fact(DisplayName = "should match Ctrl+Alt+letter via CSI-u when kitty inactive")]
    public void Matches_ctrl_alt_letter_via_csi_u_when_kitty_inactive()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[104;7u", "ctrl+alt+h"));
            Assert.Equal("ctrl+alt+h", Keys.ParseKey("\x1b[104;7u"));
        });
    }

    [Fact(DisplayName = "should match Ctrl+Alt+letter via xterm modifyOtherKeys")]
    public void Matches_ctrl_alt_letter_via_modify_other_keys()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[27;7;104~", "ctrl+alt+h"));
            Assert.Equal("ctrl+alt+h", Keys.ParseKey("\x1b[27;7;104~"));
        });
    }

    [Fact(DisplayName = "should match legacy Ctrl+c")]
    public void Matches_legacy_ctrl_c()
    {
        WithKitty(false, () => Assert.True(Keys.MatchesKey("\x03", "ctrl+c")));
    }

    [Fact(DisplayName = "should match legacy Ctrl+d")]
    public void Matches_legacy_ctrl_d()
    {
        WithKitty(false, () => Assert.True(Keys.MatchesKey("\x04", "ctrl+d")));
    }

    [Fact(DisplayName = "should match escape key")]
    public void Matches_escape_key()
    {
        Assert.True(Keys.MatchesKey("\x1b", "escape"));
    }

    [Fact(DisplayName = "should match legacy linefeed as enter")]
    public void Matches_legacy_linefeed_as_enter()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\n", "enter"));
            Assert.Equal("enter", Keys.ParseKey("\n"));
        });
    }

    [Fact(DisplayName = "should treat linefeed as shift+enter when kitty active")]
    public void Treats_linefeed_as_shift_enter_when_kitty_active()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\n", "shift+enter"));
            Assert.False(Keys.MatchesKey("\n", "enter"));
            Assert.Equal("shift+enter", Keys.ParseKey("\n"));
        });
    }

    [Fact(DisplayName = "should parse ctrl+space")]
    public void Parses_ctrl_space()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x00", "ctrl+space"));
            Assert.Equal("ctrl+space", Keys.ParseKey("\x00"));
        });
    }

    [Fact(DisplayName = "should match legacy Ctrl+symbol")]
    public void Matches_legacy_ctrl_symbol()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1c", "ctrl+\\"));
            Assert.Equal("ctrl+\\", Keys.ParseKey("\x1c"));
            Assert.True(Keys.MatchesKey("\x1d", "ctrl+]"));
            Assert.Equal("ctrl+]", Keys.ParseKey("\x1d"));
            Assert.True(Keys.MatchesKey("\x1f", "ctrl+_"));
            Assert.True(Keys.MatchesKey("\x1f", "ctrl+-"));
            Assert.Equal("ctrl+-", Keys.ParseKey("\x1f"));
        });
    }

    [Fact(DisplayName = "should match legacy Ctrl+Alt+symbol")]
    public void Matches_legacy_ctrl_alt_symbol()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b\x1b", "ctrl+alt+["));
            Assert.Equal("ctrl+alt+[", Keys.ParseKey("\x1b\x1b"));
            Assert.True(Keys.MatchesKey("\x1b\x1c", "ctrl+alt+\\"));
            Assert.Equal("ctrl+alt+\\", Keys.ParseKey("\x1b\x1c"));
            Assert.True(Keys.MatchesKey("\x1b\x1d", "ctrl+alt+]"));
            Assert.Equal("ctrl+alt+]", Keys.ParseKey("\x1b\x1d"));
            Assert.True(Keys.MatchesKey("\x1b\x1f", "ctrl+alt+_"));
            Assert.True(Keys.MatchesKey("\x1b\x1f", "ctrl+alt+-"));
            Assert.Equal("ctrl+alt+-", Keys.ParseKey("\x1b\x1f"));
        });
    }

    [Fact(DisplayName = "should treat raw 0x08 as plain backspace outside Windows Terminal")]
    public void Treats_raw_backspace_as_plain_outside_windows_terminal()
    {
        WithKitty(false, () => WithEnvironment(
            new Dictionary<string, string?> { ["WT_SESSION"] = null },
            () =>
            {
                Assert.True(Keys.MatchesKey("\x7f", "backspace"));
                Assert.False(Keys.MatchesKey("\x7f", "ctrl+backspace"));
                Assert.Equal("backspace", Keys.ParseKey("\x7f"));
                Assert.True(Keys.MatchesKey("\x08", "backspace"));
                Assert.False(Keys.MatchesKey("\x08", "ctrl+backspace"));
                Assert.Equal("backspace", Keys.ParseKey("\x08"));
                Assert.True(Keys.MatchesKey("\x08", "ctrl+h"));
            }));
    }

    [Fact(DisplayName = "should treat raw 0x08 as ctrl+backspace in local Windows Terminal")]
    public void Treats_raw_backspace_as_ctrl_backspace_in_local_windows_terminal()
    {
        WithKitty(false, () => WithEnvironment(
            new Dictionary<string, string?>
            {
                ["WT_SESSION"] = "test-session",
                ["SSH_CONNECTION"] = null,
                ["SSH_CLIENT"] = null,
                ["SSH_TTY"] = null,
            },
            () =>
            {
                Assert.True(Keys.MatchesKey("\x08", "ctrl+backspace"));
                Assert.False(Keys.MatchesKey("\x08", "backspace"));
                Assert.Equal("ctrl+backspace", Keys.ParseKey("\x08"));
                Assert.True(Keys.MatchesKey("\x08", "ctrl+h"));
            }));
    }

    [Fact(DisplayName = "should treat raw 0x08 as plain backspace in Windows Terminal over SSH")]
    public void Treats_raw_backspace_as_plain_over_ssh()
    {
        WithKitty(false, () => WithEnvironment(
            new Dictionary<string, string?>
            {
                ["WT_SESSION"] = "test-session",
                ["SSH_CONNECTION"] = "1 2 3 4",
                ["SSH_CLIENT"] = "1 2 3",
                ["SSH_TTY"] = "/dev/pts/1",
            },
            () =>
            {
                Assert.False(Keys.MatchesKey("\x08", "ctrl+backspace"));
                Assert.True(Keys.MatchesKey("\x08", "backspace"));
                Assert.Equal("backspace", Keys.ParseKey("\x08"));
                Assert.True(Keys.MatchesKey("\x08", "ctrl+h"));
            }));
    }

    [Fact(DisplayName = "should parse legacy alt-prefixed sequences when kitty inactive")]
    public void Parses_legacy_alt_prefixed_sequences_by_protocol_mode()
    {
        WithKitty(false, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b ", "alt+space"));
            Assert.Equal("alt+space", Keys.ParseKey("\x1b "));
            Assert.True(Keys.MatchesKey("\x1b\b", "alt+backspace"));
            Assert.Equal("alt+backspace", Keys.ParseKey("\x1b\b"));
            Assert.True(Keys.MatchesKey("\x1b\x03", "ctrl+alt+c"));
            Assert.Equal("ctrl+alt+c", Keys.ParseKey("\x1b\x03"));
            Assert.True(Keys.MatchesKey("\u001bB", "alt+left"));
            Assert.Equal("alt+left", Keys.ParseKey("\u001bB"));
            Assert.True(Keys.MatchesKey("\u001bF", "alt+right"));
            Assert.Equal("alt+right", Keys.ParseKey("\u001bF"));
            Assert.True(Keys.MatchesKey("\u001ba", "alt+a"));
            Assert.Equal("alt+a", Keys.ParseKey("\u001ba"));
            Assert.True(Keys.MatchesKey("\u001b1", "alt+1"));
            Assert.Equal("alt+1", Keys.ParseKey("\u001b1"));
            Assert.True(Keys.MatchesKey("\x1b,", "alt+,"));
            Assert.Equal("alt+,", Keys.ParseKey("\x1b,"));
            Assert.True(Keys.MatchesKey("\x1b.", "alt+."));
            Assert.Equal("alt+.", Keys.ParseKey("\x1b."));
            Assert.True(Keys.MatchesKey("\x1by", "alt+y"));
            Assert.Equal("alt+y", Keys.ParseKey("\x1by"));
            Assert.True(Keys.MatchesKey("\x1bz", "alt+z"));
            Assert.Equal("alt+z", Keys.ParseKey("\x1bz"));
        });

        WithKitty(true, () =>
        {
            Assert.False(Keys.MatchesKey("\x1b ", "alt+space"));
            Assert.Null(Keys.ParseKey("\x1b "));
            Assert.True(Keys.MatchesKey("\x1b\b", "alt+backspace"));
            Assert.Equal("alt+backspace", Keys.ParseKey("\x1b\b"));
            Assert.False(Keys.MatchesKey("\x1b\x03", "ctrl+alt+c"));
            Assert.Null(Keys.ParseKey("\x1b\x03"));
            Assert.False(Keys.MatchesKey("\u001bB", "alt+left"));
            Assert.Null(Keys.ParseKey("\u001bB"));
            Assert.False(Keys.MatchesKey("\u001bF", "alt+right"));
            Assert.Null(Keys.ParseKey("\u001bF"));
            Assert.False(Keys.MatchesKey("\u001ba", "alt+a"));
            Assert.Null(Keys.ParseKey("\u001ba"));
            Assert.False(Keys.MatchesKey("\u001b1", "alt+1"));
            Assert.Null(Keys.ParseKey("\u001b1"));
            Assert.False(Keys.MatchesKey("\x1b,", "alt+,"));
            Assert.Null(Keys.ParseKey("\x1b,"));
            Assert.False(Keys.MatchesKey("\x1b.", "alt+."));
            Assert.Null(Keys.ParseKey("\x1b."));
            Assert.False(Keys.MatchesKey("\x1by", "alt+y"));
            Assert.Null(Keys.ParseKey("\x1by"));
        });
    }

    [Fact(DisplayName = "should match arrow keys")]
    public void Matches_arrow_keys()
    {
        Assert.True(Keys.MatchesKey("\x1b[A", "up"));
        Assert.True(Keys.MatchesKey("\x1b[B", "down"));
        Assert.True(Keys.MatchesKey("\x1b[C", "right"));
        Assert.True(Keys.MatchesKey("\x1b[D", "left"));
    }

    [Fact(DisplayName = "should match SS3 arrows and home/end")]
    public void Matches_ss3_arrows_and_home_end()
    {
        Assert.True(Keys.MatchesKey("\x1bOA", "up"));
        Assert.True(Keys.MatchesKey("\x1bOB", "down"));
        Assert.True(Keys.MatchesKey("\x1bOC", "right"));
        Assert.True(Keys.MatchesKey("\x1bOD", "left"));
        Assert.True(Keys.MatchesKey("\x1bOH", "home"));
        Assert.True(Keys.MatchesKey("\x1bOF", "end"));
    }

    [Fact(DisplayName = "should match xterm Ctrl-modified viewport navigation")]
    public void Matches_xterm_ctrl_modified_viewport_navigation()
    {
        Assert.True(Keys.MatchesKey("\x1b[1;5H", "ctrl+home"));
        Assert.True(Keys.MatchesKey("\x1b[1;5F", "ctrl+end"));
        Assert.True(Keys.MatchesKey("\x1b[5;5~", "ctrl+pageUp"));
        Assert.True(Keys.MatchesKey("\x1b[6;5~", "ctrl+pageDown"));
        Assert.Equal("ctrl+home", Keys.ParseKey("\x1b[1;5H"));
        Assert.Equal("ctrl+end", Keys.ParseKey("\x1b[1;5F"));
        Assert.Equal("ctrl+pageUp", Keys.ParseKey("\x1b[5;5~"));
        Assert.Equal("ctrl+pageDown", Keys.ParseKey("\x1b[6;5~"));
    }

    [Fact(DisplayName = "should match legacy function keys and clear")]
    public void Matches_legacy_function_keys_and_clear()
    {
        Assert.True(Keys.MatchesKey("\x1bOP", "f1"));
        Assert.True(Keys.MatchesKey("\x1b[24~", "f12"));
        Assert.True(Keys.MatchesKey("\x1b[E", "clear"));
    }

    [Fact(DisplayName = "should match alt+arrows")]
    public void Matches_alt_arrows()
    {
        Assert.True(Keys.MatchesKey("\x1bp", "alt+up"));
        Assert.False(Keys.MatchesKey("\x1bp", "up"));
    }

    [Fact(DisplayName = "should match rxvt modifier sequences")]
    public void Matches_rxvt_modifier_sequences()
    {
        Assert.True(Keys.MatchesKey("\x1b[a", "shift+up"));
        Assert.True(Keys.MatchesKey("\x1bOa", "ctrl+up"));
        Assert.True(Keys.MatchesKey("\x1b[2$", "shift+insert"));
        Assert.True(Keys.MatchesKey("\x1b[2^", "ctrl+insert"));
        Assert.True(Keys.MatchesKey("\x1b[7$", "shift+home"));
    }

    [Fact(DisplayName = "should decode Kitty keypad functional keys to printable characters")]
    public void Decodes_kitty_keypad_functional_keys()
    {
        Assert.Equal("0", Keys.DecodeKittyPrintable("\x1b[57399u"));
        Assert.Equal("1", Keys.DecodeKittyPrintable("\x1b[57400u"));
        Assert.Equal(".", Keys.DecodeKittyPrintable("\x1b[57409u"));
        Assert.Equal("/", Keys.DecodeKittyPrintable("\x1b[57410u"));
        Assert.Equal("*", Keys.DecodeKittyPrintable("\x1b[57411u"));
        Assert.Equal("-", Keys.DecodeKittyPrintable("\x1b[57412u"));
        Assert.Equal("+", Keys.DecodeKittyPrintable("\x1b[57413u"));
        Assert.Equal("=", Keys.DecodeKittyPrintable("\x1b[57415u"));
        Assert.Equal(",", Keys.DecodeKittyPrintable("\x1b[57416u"));
        Assert.Null(Keys.DecodeKittyPrintable("\x1b[57417u"));
    }

    [Fact(DisplayName = "should decode printable xterm modifyOtherKeys sequences")]
    public void Decodes_printable_modify_other_keys_sequences()
    {
        Assert.Equal("E", Keys.DecodePrintableKey("\x1b[27;2;69~"));
        Assert.Equal("Ä", Keys.DecodePrintableKey("\x1b[27;2;196~"));
        Assert.Equal(" ", Keys.DecodePrintableKey("\x1b[27;2;32~"));
        Assert.Null(Keys.DecodePrintableKey("\x1b[27;2;13~"));
        Assert.Null(Keys.DecodePrintableKey("\x1b[27;6;69~"));
    }

    [Fact(DisplayName = "should return Latin key name when base layout key is present")]
    public void Parses_latin_key_name_from_base_layout_key()
    {
        WithKitty(true, () => Assert.Equal("ctrl+c", Keys.ParseKey("\x1b[1089::99;5u")));
    }

    [Fact(DisplayName = "should prefer codepoint for Latin letters when base layout differs")]
    public void Parses_codepoint_for_latin_letters()
    {
        WithKitty(true, () => Assert.Equal("ctrl+k", Keys.ParseKey("\x1b[107::118;5u")));
    }

    [Fact(DisplayName = "should prefer codepoint for symbol keys when base layout differs")]
    public void Parses_codepoint_for_symbols()
    {
        WithKitty(true, () => Assert.Equal("ctrl+/", Keys.ParseKey("\x1b[47::91;5u")));
    }

    [Fact(DisplayName = "should return key name from codepoint when no base layout")]
    public void Parses_codepoint_without_base_layout_key()
    {
        WithKitty(true, () => Assert.Equal("ctrl+c", Keys.ParseKey("\x1b[99;5u")));
    }

    [Fact(DisplayName = "should parse shifted uppercase CSI-u letters as shift+letter")]
    public void Parses_shifted_uppercase_csi_u_letters()
    {
        WithKitty(true, () =>
        {
            Assert.True(Keys.MatchesKey("\x1b[69;2u", "shift+e"));
            Assert.Equal("shift+e", Keys.ParseKey("\x1b[69;2u"));
        });
    }

    [Fact(DisplayName = "should ignore Kitty CSI-u with unsupported modifiers")]
    public void Ignores_kitty_csi_u_with_unsupported_modifiers()
    {
        WithKitty(true, () => Assert.Null(Keys.ParseKey("\x1b[99;17u")));
    }

    [Fact(DisplayName = "should parse legacy Ctrl+letter")]
    public void Parses_legacy_ctrl_letter()
    {
        WithKitty(false, () =>
        {
            Assert.Equal("ctrl+c", Keys.ParseKey("\x03"));
            Assert.Equal("ctrl+d", Keys.ParseKey("\x04"));
        });
    }

    [Fact(DisplayName = "should parse special keys")]
    public void Parses_special_keys()
    {
        WithKitty(false, () =>
        {
            Assert.Equal("escape", Keys.ParseKey("\x1b"));
            Assert.Equal("tab", Keys.ParseKey("\t"));
            Assert.Equal("enter", Keys.ParseKey("\r"));
            Assert.Equal("enter", Keys.ParseKey("\n"));
            Assert.Equal("ctrl+space", Keys.ParseKey("\x00"));
            Assert.Equal("space", Keys.ParseKey(" "));
            Assert.Equal("1", Keys.ParseKey("1"));
            Assert.True(Keys.MatchesKey("1", "1"));
        });
    }

    [Fact(DisplayName = "should parse arrow keys")]
    public void Parses_arrow_keys()
    {
        Assert.Equal("up", Keys.ParseKey("\x1b[A"));
        Assert.Equal("down", Keys.ParseKey("\x1b[B"));
        Assert.Equal("right", Keys.ParseKey("\x1b[C"));
        Assert.Equal("left", Keys.ParseKey("\x1b[D"));
    }

    [Fact(DisplayName = "should parse SS3 arrows and home/end")]
    public void Parses_ss3_arrows_and_home_end()
    {
        Assert.Equal("up", Keys.ParseKey("\x1bOA"));
        Assert.Equal("down", Keys.ParseKey("\x1bOB"));
        Assert.Equal("right", Keys.ParseKey("\x1bOC"));
        Assert.Equal("left", Keys.ParseKey("\x1bOD"));
        Assert.Equal("home", Keys.ParseKey("\x1bOH"));
        Assert.Equal("end", Keys.ParseKey("\x1bOF"));
    }

    [Fact(DisplayName = "should parse legacy function and modifier sequences")]
    public void Parses_legacy_function_and_modifier_sequences()
    {
        Assert.Equal("f1", Keys.ParseKey("\x1bOP"));
        Assert.Equal("f12", Keys.ParseKey("\x1b[24~"));
        Assert.Equal("clear", Keys.ParseKey("\x1b[E"));
        Assert.Equal("ctrl+insert", Keys.ParseKey("\x1b[2^"));
        Assert.Equal("alt+up", Keys.ParseKey("\x1bp"));
    }

    [Fact(DisplayName = "should parse double bracket pageUp")]
    public void Parses_double_bracket_page_up()
    {
        Assert.Equal("pageUp", Keys.ParseKey("\x1b[[5~"));
    }

    [Fact(DisplayName = "rejects an unrecognized KeyId at runtime")]
    public void Rejects_unrecognized_key_id_at_runtime()
    {
        Assert.False(KeyId.TryParse("ctrl+not-a-key", out _));
        Assert.False(KeyId.TryParse("++", out _));
        Assert.False(KeyId.TryParse("+a", out _));
        Assert.Throws<ArgumentException>(() => KeyId.Parse("ctrl+not-a-key"));
        Assert.False(Keys.MatchesKey("\x03", "ctrl+not-a-key"));
    }

    private static void WithKitty(bool active, Action action)
    {
        Keys.SetKittyProtocolActive(active);
        try
        {
            action();
        }
        finally
        {
            Keys.SetKittyProtocolActive(false);
        }
    }

    private static void WithEnvironment(IReadOnlyDictionary<string, string?> values, Action action)
    {
        var previous = values.ToDictionary(
            static pair => pair.Key,
            static pair => Environment.GetEnvironmentVariable(pair.Key),
            StringComparer.Ordinal);
        try
        {
            foreach (var value in values)
            {
                Environment.SetEnvironmentVariable(value.Key, value.Value);
            }

            action();
        }
        finally
        {
            foreach (var value in previous)
            {
                Environment.SetEnvironmentVariable(value.Key, value.Value);
            }
        }
    }
}
