using System.Text.RegularExpressions;
using Xunit;

namespace Pi.Tui.Tests;

public sealed class TerminalImageTests
{
    private static readonly string[] _environmentKeys =
    [
        "TERM",
        "TERM_PROGRAM",
        "TERMINAL_EMULATOR",
        "COLORTERM",
        "TMUX",
        "KITTY_WINDOW_ID",
        "GHOSTTY_RESOURCES_DIR",
        "WEZTERM_PANE",
        "ITERM_SESSION_ID",
        "WT_SESSION",
        "CMUX_WORKSPACE_ID",
        "WARP_SESSION_ID",
        "WARP_TERMINAL_SESSION_UUID",
        "PI_HYPERLINKS",
        "PI_IMAGE_PROTOCOL",
        "PI_TRUE_COLOR",
        "PATH",
    ];

    [Fact(DisplayName = "should detect iTerm2 image escape sequence at start of line")]
    public void Should_detect_iTerm2_image_escape_sequence_at_start_of_line()
    {
        const string line = "\x1b]1337;File=size=100,100;inline=1:base64encodeddata==\x07";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "should detect iTerm2 image escape sequence with text before it")]
    public void Should_detect_iTerm2_image_escape_sequence_with_text_before_it()
    {
        const string line = "Some text \x1b]1337;File=size=100,100;inline=1:base64data==\x07 more text";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "should detect iTerm2 image escape sequence in middle of long line")]
    public void Should_detect_iTerm2_image_escape_sequence_in_middle_of_long_line()
    {
        const string line = "Text before image...\x1b]1337;File=inline=1:verylongbase64data==...text after";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "should detect iTerm2 image escape sequence at end of line")]
    public void Should_detect_iTerm2_image_escape_sequence_at_end_of_line()
    {
        const string line = "Regular text ending with \x1b]1337;File=inline=1:base64data==\x07";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "should detect minimal iTerm2 image escape sequence")]
    public void Should_detect_minimal_iTerm2_image_escape_sequence()
    {
        Assert.True(TerminalImage.IsImageLine("\x1b]1337;File=:\x07"));
    }

    [Fact(DisplayName = "should detect Kitty image escape sequence at start of line")]
    public void Should_detect_Kitty_image_escape_sequence_at_start_of_line()
    {
        Assert.True(TerminalImage.IsImageLine("\x1b_Ga=T,f=100,t=f,d=base64data...\x1b\\\x1b_Gm=i=1;\x1b\\"));
    }

    [Fact(DisplayName = "should detect Kitty image escape sequence with text before it")]
    public void Should_detect_Kitty_image_escape_sequence_with_text_before_it()
    {
        Assert.True(TerminalImage.IsImageLine("Output: \x1b_Ga=T,f=100;data...\x1b\\\x1b_Gm=i=1;\x1b\\"));
    }

    [Fact(DisplayName = "should detect Kitty image escape sequence with padding")]
    public void Should_detect_Kitty_image_escape_sequence_with_padding()
    {
        Assert.True(TerminalImage.IsImageLine("  \x1b_Ga=T,f=100...\x1b\\\x1b_Gm=i=1;\x1b\\  "));
    }

    [Fact(DisplayName = "should detect image sequences in very long lines (304k+ chars)")]
    public void Should_detect_image_sequences_in_very_long_lines_304k_chars()
    {
        var base64Char = new string('A', 100);
        var imageSequence = "\x1b]1337;File=size=800,600;inline=1:";
        var longLine = "Text prefix " + imageSequence + string.Concat(Enumerable.Repeat(base64Char, 3000)) + " suffix";
        Assert.True(longLine.Length > 300000);
        Assert.True(TerminalImage.IsImageLine(longLine));
    }

    [Fact(DisplayName = "should detect image sequences when terminal doesn't support images")]
    public void Should_detect_image_sequences_when_terminal_does_not_support_images()
    {
        Assert.True(TerminalImage.IsImageLine("Read image file [image/jpeg]\x1b]1337;File=inline=1:base64data==\x07"));
    }

    [Fact(DisplayName = "should detect image sequences with ANSI codes before them")]
    public void Should_detect_image_sequences_with_ANSI_codes_before_them()
    {
        Assert.True(TerminalImage.IsImageLine("\x1b[31mError output \x1b]1337;File=inline=1:image==\x07"));
    }

    [Fact(DisplayName = "should detect image sequences with ANSI codes after them")]
    public void Should_detect_image_sequences_with_ANSI_codes_after_them()
    {
        Assert.True(TerminalImage.IsImageLine("\x1b_Ga=T,f=100:data...\x1b\\\x1b_Gm=i=1;\x1b\\\x1b[0m reset"));
    }

    [Fact(DisplayName = "should not detect images in plain text lines")]
    public void Should_not_detect_images_in_plain_text_lines()
    {
        Assert.False(TerminalImage.IsImageLine("This is just a regular text line without any escape sequences"));
    }

    [Fact(DisplayName = "should not detect images in lines with only ANSI codes")]
    public void Should_not_detect_images_in_lines_with_only_ANSI_codes()
    {
        Assert.False(TerminalImage.IsImageLine("\x1b[31mRed text\x1b[0m and \x1b[32mgreen text\x1b[0m"));
    }

    [Fact(DisplayName = "should not detect images in lines with cursor movement codes")]
    public void Should_not_detect_images_in_lines_with_cursor_movement_codes()
    {
        Assert.False(TerminalImage.IsImageLine("\x1b[1A\x1b[2KLine cleared and moved up"));
    }

    [Fact(DisplayName = "should not detect images in lines with partial iTerm2 sequences")]
    public void Should_not_detect_images_in_lines_with_partial_iTerm2_sequences()
    {
        Assert.False(TerminalImage.IsImageLine("Some text with ]1337;File but missing ESC at start"));
    }

    [Fact(DisplayName = "should not detect images in lines with partial Kitty sequences")]
    public void Should_not_detect_images_in_lines_with_partial_Kitty_sequences()
    {
        Assert.False(TerminalImage.IsImageLine("Some text with _G but missing ESC at start"));
    }

    [Fact(DisplayName = "should not detect images in empty lines")]
    public void Should_not_detect_images_in_empty_lines()
    {
        Assert.False(TerminalImage.IsImageLine(string.Empty));
    }

    [Fact(DisplayName = "should not detect images in lines with newlines only")]
    public void Should_not_detect_images_in_lines_with_newlines_only()
    {
        Assert.False(TerminalImage.IsImageLine("\n"));
        Assert.False(TerminalImage.IsImageLine("\n\n"));
    }

    [Fact(DisplayName = "should detect images when line has both Kitty and iTerm2 sequences")]
    public void Should_detect_images_when_line_has_both_Kitty_and_iTerm2_sequences()
    {
        Assert.True(TerminalImage.IsImageLine("Kitty: \x1b_Ga=T...\x1b\\\x1b_Gm=i=1;\x1b\\ iTerm2: \x1b]1337;File=inline=1:data==\x07"));
    }

    [Fact(DisplayName = "should detect image in line with multiple text and image segments")]
    public void Should_detect_image_in_line_with_multiple_text_and_image_segments()
    {
        Assert.True(TerminalImage.IsImageLine("Start \x1b]1337;File=img1==\x07 middle \x1b]1337;File=img2==\x07 end"));
    }

    [Fact(DisplayName = "should not falsely detect image in line with file path containing keywords")]
    public void Should_not_falsely_detect_image_in_line_with_file_path_containing_keywords()
    {
        Assert.False(TerminalImage.IsImageLine("/path/to/File_1337_backup/image.jpg"));
    }

    [Fact(DisplayName = "defaults to hyperlinks: false for unknown terminals")]
    public void Defaults_to_hyperlinks_false_for_unknown_terminals()
    {
        var capabilities = WithEnvironment(new Dictionary<string, string?>(), () => TerminalImage.DetectCapabilities());
        Assert.False(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "applies environment overrides")]
    public void Applies_environment_overrides()
    {
        var first = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["PI_HYPERLINKS"] = "1",
                ["PI_IMAGE_PROTOCOL"] = "kitty",
                ["PI_TRUE_COLOR"] = "1",
            },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(new TerminalCapabilities(ImageProtocol.Kitty, true, true), first);

        var second = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERM_PROGRAM"] = "iterm.app",
                ["PI_HYPERLINKS"] = "0",
                ["PI_IMAGE_PROTOCOL"] = "none",
                ["PI_TRUE_COLOR"] = "0",
            },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(new TerminalCapabilities(null, false, false), second);
    }

    [Fact(DisplayName = "preserves auto-detection for auto environment overrides")]
    public void Preserves_auto_detection_for_auto_environment_overrides()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERM_PROGRAM"] = "ghostty",
                ["PI_HYPERLINKS"] = "auto",
                ["PI_IMAGE_PROTOCOL"] = "auto",
                ["PI_TRUE_COLOR"] = "auto",
            },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(new TerminalCapabilities(ImageProtocol.Kitty, true, true), capabilities);
    }

    [Fact(DisplayName = "applies and clears programmatic overrides")]
    public void Applies_and_clears_programmatic_overrides()
    {
        WithEnvironment(
            new Dictionary<string, string?>
            {
                ["PI_HYPERLINKS"] = "1",
                ["PI_IMAGE_PROTOCOL"] = "kitty",
                ["PI_TRUE_COLOR"] = "1",
            },
            () =>
            {
                TerminalImage.SetCapabilityOverrides(
                    new Dictionary<string, object?>
                    {
                        ["images"] = null,
                        ["trueColor"] = false,
                        ["hyperlinks"] = false,
                    });
                try
                {
                    Assert.Equal(new TerminalCapabilities(null, false, false), TerminalImage.GetCapabilities());
                    TerminalImage.SetCapabilityOverrides(new Dictionary<string, object?>());
                    Assert.Equal(new TerminalCapabilities(ImageProtocol.Kitty, true, true), TerminalImage.GetCapabilities());
                }
                finally
                {
                    TerminalImage.SetCapabilityOverrides(new Dictionary<string, object?>());
                    TerminalImage.ResetCapabilitiesCache();
                }
            });
    }

    [Fact(DisplayName = "bypasses the tmux probe when hyperlinks are overridden")]
    public void Bypasses_the_tmux_probe_when_hyperlinks_are_overridden()
    {
        var probed = false;
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TMUX"] = "/tmp/tmux-1000/default,1234,0",
                ["PI_HYPERLINKS"] = "1",
                ["PI_IMAGE_PROTOCOL"] = "kitty",
            },
            () => TerminalImage.DetectCapabilities(() =>
            {
                probed = true;
                return false;
            }));
        Assert.False(probed);
        Assert.Equal(ImageProtocol.Kitty, capabilities.Images);
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables hyperlinks under tmux when the client forwards them")]
    public void Enables_hyperlinks_under_tmux_when_the_client_forwards_them()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TMUX"] = "/tmp/tmux-1000/default,1234,0",
                ["TERM_PROGRAM"] = "ghostty",
            },
            () => TerminalImage.DetectCapabilities(() => true));
        Assert.True(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "disables hyperlinks under tmux when the client does not forward them")]
    public void Disables_hyperlinks_under_tmux_when_the_client_does_not_forward_them()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TMUX"] = "/tmp/tmux-1000/default,1234,0",
                ["TERM_PROGRAM"] = "ghostty",
            },
            () => TerminalImage.DetectCapabilities(() => false));
        Assert.False(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "tmux probe falls back to false when the command is absent")]
    public void Tmux_probe_falls_back_to_false_when_the_command_is_absent()
    {
        var commandDirectory = Path.Combine(Path.GetTempPath(), $"pi-tui-no-tmux-{Guid.NewGuid():N}");
        Directory.CreateDirectory(commandDirectory);
        try
        {
            var capabilities = WithEnvironment(
                new Dictionary<string, string?> { ["TMUX"] = "test-tmux-session" },
                () =>
                {
                    var savedPath = Environment.GetEnvironmentVariable("PATH");
                    Environment.SetEnvironmentVariable("PATH", commandDirectory);
                    try
                    {
                        return TerminalImage.DetectCapabilities();
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("PATH", savedPath);
                    }
                });
            Assert.False(capabilities.Hyperlinks);
            Assert.Null(capabilities.Images);
        }
        finally
        {
            Directory.Delete(commandDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "checks tmux capability when TERM starts with 'tmux'")]
    public void Checks_tmux_capability_when_TERM_starts_with_tmux()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERM"] = "tmux-256color",
                ["TERM_PROGRAM"] = "iterm.app",
            },
            () => TerminalImage.DetectCapabilities(() => true));
        Assert.True(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
        var capabilities2 = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERM"] = "tmux-256color",
                ["TERM_PROGRAM"] = "iterm.app",
            },
            () => TerminalImage.DetectCapabilities(() => false));
        Assert.False(capabilities2.Hyperlinks);
    }

    [Fact(DisplayName = "forces hyperlinks: false when TERM starts with 'screen'")]
    public void Forces_hyperlinks_false_when_TERM_starts_with_screen()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["TERM"] = "screen-256color" },
            () => TerminalImage.DetectCapabilities());
        Assert.False(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "enables hyperlinks for Ghostty")]
    public void Enables_hyperlinks_for_Ghostty()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["TERM_PROGRAM"] = "ghostty" },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "does not disable Ghostty images solely because cmux is present")]
    public void Does_not_disable_Ghostty_images_solely_because_cmux_is_present()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERM_PROGRAM"] = "ghostty",
                ["CMUX_WORKSPACE_ID"] = "workspace",
            },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(ImageProtocol.Kitty, capabilities.Images);
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables hyperlinks for Kitty")]
    public void Enables_hyperlinks_for_Kitty()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["KITTY_WINDOW_ID"] = "1" },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables hyperlinks for WezTerm")]
    public void Enables_hyperlinks_for_WezTerm()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["WEZTERM_PANE"] = "0" },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables images and hyperlinks for Warp via TERM_PROGRAM")]
    public void Enables_images_and_hyperlinks_for_Warp_via_TERM_PROGRAM()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["TERM_PROGRAM"] = "WarpTerminal" },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(ImageProtocol.Kitty, capabilities.Images);
        Assert.True(capabilities.TrueColor);
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables images and hyperlinks for Warp via WARP_SESSION_ID")]
    public void Enables_images_and_hyperlinks_for_Warp_via_WARP_SESSION_ID()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["WARP_SESSION_ID"] = "some-session-id" },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(ImageProtocol.Kitty, capabilities.Images);
        Assert.True(capabilities.TrueColor);
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables images and hyperlinks for Warp via WARP_TERMINAL_SESSION_UUID")]
    public void Enables_images_and_hyperlinks_for_Warp_via_WARP_TERMINAL_SESSION_UUID()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["WARP_TERMINAL_SESSION_UUID"] = "d0e1a2e5-7ca7-44cd-9037-ac7222011161" },
            () => TerminalImage.DetectCapabilities());
        Assert.Equal(ImageProtocol.Kitty, capabilities.Images);
        Assert.True(capabilities.TrueColor);
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "disables images for Warp inside tmux")]
    public void Disables_images_for_Warp_inside_tmux()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERM_PROGRAM"] = "WarpTerminal",
                ["TMUX"] = "/tmp/tmux-1000/default,1234,0",
                ["TERM"] = "tmux-256color",
            },
            () => TerminalImage.DetectCapabilities(() => true));
        Assert.Null(capabilities.Images);
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables hyperlinks for iTerm2")]
    public void Enables_hyperlinks_for_iTerm2()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["TERM_PROGRAM"] = "iterm.app" },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables hyperlinks for VSCode")]
    public void Enables_hyperlinks_for_VSCode()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["TERM_PROGRAM"] = "vscode" },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.Hyperlinks);
    }

    [Fact(DisplayName = "enables truecolor and hyperlinks for Windows Terminal outside multiplexers")]
    public void Enables_truecolor_and_hyperlinks_for_Windows_Terminal_outside_multiplexers()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?> { ["WT_SESSION"] = "session", ["TERM"] = "xterm-256color" },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.TrueColor);
        Assert.True(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "enables truecolor without hyperlinks for JetBrains terminal")]
    public void Enables_truecolor_without_hyperlinks_for_JetBrains_terminal()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["TERMINAL_EMULATOR"] = "JetBrains-JediTerm",
                ["TERM"] = "xterm-256color",
            },
            () => TerminalImage.DetectCapabilities());
        Assert.True(capabilities.TrueColor);
        Assert.False(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "does not inherit Windows Terminal truecolor through tmux")]
    public void Does_not_inherit_Windows_Terminal_truecolor_through_tmux()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["WT_SESSION"] = "session",
                ["TMUX"] = "/tmp/tmux-1000/default,1234,0",
                ["TERM"] = "tmux-256color",
            },
            () => TerminalImage.DetectCapabilities(() => false));
        Assert.False(capabilities.TrueColor);
        Assert.False(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "trusts explicit truecolor hints through tmux")]
    public void Trusts_explicit_truecolor_hints_through_tmux()
    {
        var capabilities = WithEnvironment(
            new Dictionary<string, string?>
            {
                ["COLORTERM"] = "truecolor",
                ["TMUX"] = "/tmp/tmux-1000/default,1234,0",
                ["TERM"] = "tmux-256color",
            },
            () => TerminalImage.DetectCapabilities(() => false));
        Assert.True(capabilities.TrueColor);
        Assert.False(capabilities.Hyperlinks);
        Assert.Null(capabilities.Images);
    }

    [Fact(DisplayName = "includes the decoded payload size in OSC 1337 metadata")]
    public void Includes_the_decoded_payload_size_in_OSC_1337_metadata()
    {
        var sequence = TerminalImage.EncodeITerm2("AAAA", new Iterm2EncodeOptions { Width = 2, Height = "auto" });
        Assert.Equal("\x1b]1337;File=inline=1;size=3;width=2;height=auto:AAAA\x07", sequence);
    }

    [Fact(DisplayName = "can request no terminal-side cursor movement")]
    public void Can_request_no_terminal_side_cursor_movement()
    {
        var sequence = TerminalImage.EncodeKitty("AAAA", new KittyEncodeOptions { Columns = 2, Rows = 2, MoveCursor = false });
        Assert.StartsWith("\x1b_Ga=T,f=100,q=2,C=1,c=2,r=2;", sequence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "suppresses Kitty replies for delete commands")]
    public void Suppresses_Kitty_replies_for_delete_commands()
    {
        Assert.Equal("\x1b_Ga=d,d=I,i=42,q=2\x1b\\", TerminalImage.DeleteKittyImage(42));
        Assert.Equal("\x1b_Ga=d,d=A,q=2\x1b\\", TerminalImage.DeleteAllKittyImages());
        Assert.Equal("\x1b_Ga=d,d=a,q=2\x1b\\", TerminalImage.DeleteAllKittyPlacements());
    }

    [Fact(DisplayName = "preserves renderImage's default terminal-side cursor movement")]
    public void Preserves_renderImages_default_terminal_side_cursor_movement()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 10));
        try
        {
            var result = TerminalImage.RenderImage("AAAA", new ImageDimensions(20, 20), new ImageRenderOptions { MaxWidthCells = 2 });
            Assert.NotNull(result);
            Assert.DoesNotContain(",C=1,", result!.Sequence, StringComparison.Ordinal);
            Assert.Equal(2, result.Rows);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "can opt renderImage into no terminal-side cursor movement")]
    public void Can_opt_renderImage_into_no_terminal_side_cursor_movement()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 10));
        try
        {
            var result = TerminalImage.RenderImage(
                "AAAA",
                new ImageDimensions(20, 20),
                new ImageRenderOptions { MaxWidthCells = 2, MoveCursor = false });
            Assert.NotNull(result);
            Assert.Contains(",C=1,", result!.Sequence, StringComparison.Ordinal);
            Assert.Equal(2, result.Rows);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "registers metadata and crops a partially visible placement")]
    public void Registers_metadata_and_crops_a_partially_visible_placement()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 10));
        try
        {
            var result = TerminalImage.RenderImage(
                "AAAA",
                new ImageDimensions(100, 100),
                new ImageRenderOptions { MaxWidthCells = 3, ImageId = 42, MoveCursor = false });
            Assert.NotNull(result);
            Assert.Equal(new KittyImageMetadata(42, 3, 3, 100, 100), TerminalImage.GetKittyImageMetadata(result!.Sequence));
            Assert.Contains("y=66,h=34,r=1", TerminalImage.CropKittyImageLine(result.Sequence, 2, 1), StringComparison.Ordinal);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "creates placement-only commands for uploaded and cropped images")]
    public void Creates_placement_only_commands_for_uploaded_and_cropped_images()
    {
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(42, 3, 3, 100, 100));
        var transmission = TerminalImage.EncodeKitty(
            new string('A', 8192),
            new KittyEncodeOptions { Columns = 3, Rows = 3, ImageId = 42, MoveCursor = false });
        var line = $"left {TerminalImage.CropKittyImageLine(transmission, 2, 1)} right";
        var placement = TerminalImage.GetKittyImagePlacement(line);
        Assert.NotNull(placement);
        Assert.Equal(line.Length - "left ".Length - " right".Length, placement!.TransmissionBytes);
        Assert.Equal(100 * 100 * 4L, placement.EstimatedDecodedBytes);
        Assert.Equal("\x1b_Ga=p,q=2,C=1,c=3,i=42,y=66,h=34,r=1\x1b\\", placement.Sequence);
        Assert.Equal($"left {placement.Sequence} right", placement.ReplacementLine);
        Assert.DoesNotContain("AAAA", placement.ReplacementLine, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "honors maxHeightCells by reducing rendered width")]
    public void Honors_maxHeightCells_by_reducing_rendered_width()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 10));
        try
        {
            var result = TerminalImage.RenderImage(
                "AAAA",
                new ImageDimensions(10, 100),
                new ImageRenderOptions { MaxWidthCells = 10, MaxHeightCells = 5 });
            Assert.NotNull(result);
            Assert.Equal(5, result!.Rows);
            Assert.Contains(",c=1,r=5", result.Sequence, StringComparison.Ordinal);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "caps Image component height to a square pixel box by default")]
    public void Caps_Image_component_height_to_a_square_pixel_box_by_default()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 20));
        try
        {
            var image = new Image(
                "AAAA",
                "image/png",
                new ImageTheme { FallbackColor = static value => value },
                new ImageOptions { MaxWidthCells = 10 },
                new ImageDimensions(10, 100));
            var lines = image.Render(12);
            Assert.Equal(5, lines.Count);
            Assert.Contains(",c=1,r=5", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "places image sequence on first line with empty padding rows")]
    public void Places_image_sequence_on_first_line_with_empty_padding_rows()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 10));
        try
        {
            var image = new Image(
                "AAAA",
                "image/png",
                new ImageTheme { FallbackColor = static value => value },
                new ImageOptions { MaxWidthCells = 2 },
                new ImageDimensions(20, 20));
            var lines = image.Render(4);
            var imageId = image.GetImageId();
            Assert.True(imageId.HasValue);
            Assert.StartsWith("\x1b_G", lines[0], StringComparison.Ordinal);
            Assert.Contains(",C=1,", lines[0], StringComparison.Ordinal);
            Assert.Contains($",i={imageId.Value}", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("\x1b\\", lines[0], StringComparison.Ordinal);
            Assert.Equal([string.Empty], lines.Skip(1).ToArray());
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "truncates long image fallback lines to render width")]
    public void Truncates_long_image_fallback_lines_to_render_width()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(null, false, false));
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var longPath = Path.Combine(home, "images", new string('x', 160) + ".png");
            var image = new Image(
                "AAAA",
                "image/png",
                new ImageTheme { FallbackColor = static value => $"\x1b[33m{value}\x1b[0m" },
                new ImageOptions { Filename = longPath },
                new ImageDimensions(1280, 720));
            var lines = image.Render(40);
            Assert.Single(lines);
            Assert.True(TextMeasurement.VisibleWidth(lines[0]) <= 40);
            Assert.Contains("...", lines[0], StringComparison.Ordinal);
            Assert.Contains("~", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "shortens home-prefixed absolute paths without hyperlinks")]
    public void Shortens_home_prefixed_absolute_paths_without_hyperlinks()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(null, false, false));
        try
        {
            var abs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "shot.png");
            var result = TerminalImage.ImageFallback("image/png", new ImageDimensions(1280, 720), abs);
            Assert.Equal($"[Image: ~{abs[Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Length..]} [image/png] 1280x720]", result);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "wraps shortened absolute paths in OSC 8 file links when hyperlinks are enabled")]
    public void Wraps_shortened_absolute_paths_in_OSC_8_file_links_when_hyperlinks_are_enabled()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(null, false, true));
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var abs = Path.Combine(home, ".pi", "agent", "shot.png");
            var result = TerminalImage.ImageFallback("image/png", new ImageDimensions(10, 10), abs);
            Assert.Contains("\x1b]8;;file://", result, StringComparison.Ordinal);
            Assert.Contains(new Uri(Path.GetFullPath(abs)).AbsoluteUri, result, StringComparison.Ordinal);
            var visible = Regex.Replace(result, "\x1b\\]8;;.*?\x1b\\\\", string.Empty);
            Assert.Equal($"[Image: ~{abs[home.Length..]} [image/png] 10x10]", visible);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "leaves bare basenames unchanged and does not hyperlink them")]
    public void Leaves_bare_basenames_unchanged_and_does_not_hyperlink_them()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(null, false, true));
        try
        {
            var result = TerminalImage.ImageFallback("image/png", new ImageDimensions(1, 1), "clankolas.png");
            Assert.Equal("[Image: clankolas.png [image/png] 1x1]", result);
            Assert.DoesNotContain("\x1b]8;", result, StringComparison.Ordinal);
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "omits filename segment when not provided")]
    public void Omits_filename_segment_when_not_provided()
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(null, false, false));
        try
        {
            Assert.Equal("[Image: [image/png] 8x6]", TerminalImage.ImageFallback("image/png", new ImageDimensions(8, 6)));
        }
        finally
        {
            ResetDefaultSeam();
        }
    }

    [Fact(DisplayName = "wraps text in OSC 8 open and close sequences")]
    public void Wraps_text_in_OSC_8_open_and_close_sequences()
    {
        Assert.Equal("\x1b]8;;https://example.com\x1b\\click me\x1b]8;;\x1b\\", TerminalImage.Hyperlink("click me", "https://example.com"));
    }

    [Fact(DisplayName = "preserves ANSI styling inside the hyperlink")]
    public void Preserves_ANSI_styling_inside_the_hyperlink()
    {
        const string styled = "\x1b[4m\x1b[34mclick me\x1b[0m";
        var result = TerminalImage.Hyperlink(styled, "https://example.com");
        Assert.StartsWith("\x1b]8;;https://example.com\x1b\\", result, StringComparison.Ordinal);
        Assert.Contains(styled, result, StringComparison.Ordinal);
        Assert.EndsWith("\x1b]8;;\x1b\\", result, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "works with empty text")]
    public void Works_with_empty_text()
    {
        Assert.Equal("\x1b]8;;https://example.com\x1b\\\x1b]8;;\x1b\\", TerminalImage.Hyperlink(string.Empty, "https://example.com"));
    }

    [Fact(DisplayName = "works with file:// URIs")]
    public void Works_with_file_URIs()
    {
        var result = TerminalImage.Hyperlink("README.md", "file:///home/user/README.md");
        Assert.Contains("file:///home/user/README.md", result, StringComparison.Ordinal);
        Assert.Contains("README.md", result, StringComparison.Ordinal);
    }

    private static void ResetDefaultSeam()
    {
        TerminalImage.SetCapabilityOverrides(new Dictionary<string, object?>());
        TerminalImage.ResetCapabilitiesCache();
        TerminalImage.SetCellDimensions(new CellDimensions(9, 18));
    }

    private static T WithEnvironment<T>(IReadOnlyDictionary<string, string?> overrides, Func<T> action)
    {
        var saved = _environmentKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var key in _environmentKeys) Environment.SetEnvironmentVariable(key, null);
            foreach (var pair in overrides) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            return action();
        }
        finally
        {
            foreach (var key in _environmentKeys) Environment.SetEnvironmentVariable(key, saved[key]);
        }
    }

    private static void WithEnvironment(IReadOnlyDictionary<string, string?> overrides, Action action) =>
        WithEnvironment(overrides, () =>
        {
            action();
            return true;
        });
}
