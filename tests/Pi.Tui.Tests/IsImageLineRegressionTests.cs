using Pi.Tui;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the regression coverage for image escapes embedded inside text lines.</summary>
public sealed class IsImageLineRegressionTests
{
    [Fact(DisplayName = "old implementation would return false, causing crash")]
    public void Old_implementation_would_return_false_causing_crash()
    {
        static bool OldIsImageLine(string line, string? imageEscapePrefix) =>
            imageEscapePrefix is not null && line.StartsWith(imageEscapePrefix, StringComparison.Ordinal);

        const string lineWithImageSequence =
            "Read image file [image/jpeg]\x1b]1337;File=size=800,600;inline=1:base64data...\x07";
        Assert.False(OldIsImageLine(lineWithImageSequence, null));
    }

    [Fact(DisplayName = "new implementation returns true correctly")]
    public void New_implementation_returns_true_correctly()
    {
        const string line = "Read image file [image/jpeg]\x1b]1337;File=size=800,600;inline=1:base64data...\x07";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "new implementation detects Kitty sequences in any position")]
    public void New_implementation_detects_kitty_sequences_in_any_position()
    {
        var scenarios = new[]
        {
            "At start: \x1b_Ga=T,f=100,data...\x1b\\",
            "Prefix \x1b_Ga=T,data...\x1b\\",
            "Suffix text \x1b_Ga=T,data...\x1b\\ suffix",
            "Middle \x1b_Ga=T,data...\x1b\\ more text",
            $"Text before \x1b_Ga=T,f=100{new string('A', 300000)} text after",
        };

        Assert.All(scenarios, line => Assert.True(TerminalImage.IsImageLine(line)));
    }

    [Fact(DisplayName = "new implementation detects iTerm2 sequences in any position")]
    public void New_implementation_detects_i_term2_sequences_in_any_position()
    {
        var scenarios = new[]
        {
            "At start: \x1b]1337;File=size=100,100:base64...\x07",
            "Prefix \x1b]1337;File=inline=1:data==\x07",
            "Suffix text \x1b]1337;File=inline=1:data==\x07 suffix",
            "Middle \x1b]1337;File=inline=1:data==\x07 more text",
            $"Text before \x1b]1337;File=size=800,600;inline=1:{new string('B', 300000)} text after",
        };

        Assert.All(scenarios, line => Assert.True(TerminalImage.IsImageLine(line)));
    }

    [Fact(DisplayName = "detects image sequences in read tool output")]
    public void Detects_image_sequences_in_read_tool_output()
    {
        const string line = "Read image file [image/jpeg]\x1b]1337;File=size=800,600;inline=1:base64image...\x07";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "detects Kitty sequences from Image component")]
    public void Detects_kitty_sequences_from_image_component()
    {
        const string line = "\x1b_Ga=T,f=100,t=f,d=base64data...\x1b\\\x1b_Gm=i=1;\x1b\\";
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "handles ANSI codes before image sequences")]
    public void Handles_ansi_codes_before_image_sequences()
    {
        var lines = new[]
        {
            "\x1b[31mError\x1b[0m: \x1b]1337;File=inline=1:base64==\x07",
            "\x1b[33mWarning\x1b[0m: \x1b_Ga=T,data...\x1b\\",
            "\x1b[1mBold\x1b[0m \x1b]1337;File=:base64==\x07\x1b[0m",
        };

        Assert.All(lines, line => Assert.True(TerminalImage.IsImageLine(line)));
    }

    [Fact(DisplayName = "does NOT crash on very long lines with image sequences")]
    public void Does_not_crash_on_very_long_lines_with_image_sequences()
    {
        var crashLine = "Output: \x1b]1337;File=size=800,600;inline=1:" + new string('A', 100 * 3040) + " end of output";
        Assert.True(crashLine.Length > 300000);
        Assert.True(TerminalImage.IsImageLine(crashLine));
    }

    [Fact(DisplayName = "handles lines exactly matching crash log dimensions")]
    public void Handles_lines_exactly_matching_crash_log_dimensions()
    {
        const int targetWidth = 58649;
        const string prefix = "Text";
        const string sequence = "\x1b_Ga=T,f=100";
        const string suffix = "End";
        var line = prefix + sequence + new string('A', targetWidth - prefix.Length - sequence.Length - suffix.Length) + suffix;

        Assert.Equal(targetWidth, line.Length);
        Assert.True(TerminalImage.IsImageLine(line));
    }

    [Fact(DisplayName = "does not detect images in regular long text")]
    public void Does_not_detect_images_in_regular_long_text() =>
        Assert.False(TerminalImage.IsImageLine(new string('A', 100000)));

    [Fact(DisplayName = "does not detect images in lines with file paths")]
    public void Does_not_detect_images_in_lines_with_file_paths()
    {
        var filePaths = new[]
        {
            "/path/to/1337/image.jpg",
            "/usr/local/bin/File_converter",
            "~/Documents/1337File_backup.png",
            "./_G_test_file.txt",
        };

        Assert.All(filePaths, path => Assert.False(TerminalImage.IsImageLine(path)));
    }
}
