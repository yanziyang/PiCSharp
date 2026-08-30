using Xunit;

namespace Pi.Tui.Tests;

public sealed class SelectListTests
{
    private static readonly SelectListTheme _theme = new()
    {
        SelectedPrefix = static text => text,
        SelectedText = static text => text,
        Description = static text => text,
        ScrollInfo = static text => text,
        NoMatch = static text => text,
    };

    [Fact(DisplayName = "normalizes multiline descriptions to single line")]
    public void Normalizes_multiline_descriptions_to_single_line()
    {
        var list = new SelectList(
            [new SelectItem { Value = "test", Label = "test", Description = "Line one\nLine two\nLine three" }],
            5,
            _theme);

        var rendered = list.Render(100);

        Assert.NotEmpty(rendered);
        Assert.DoesNotContain('\n', rendered[0]);
        Assert.Contains("Line one Line two Line three", rendered[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "keeps descriptions aligned when the primary text is truncated")]
    public void Keeps_descriptions_aligned_when_the_primary_text_is_truncated()
    {
        var list = new SelectList(
            [
                new SelectItem { Value = "short", Label = "short", Description = "short description" },
                new SelectItem
                {
                    Value = "very-long-command-name-that-needs-truncation",
                    Label = "very-long-command-name-that-needs-truncation",
                    Description = "long description",
                },
            ],
            5,
            _theme);

        var rendered = list.Render(80);

        Assert.Equal(
            VisibleIndexOf(rendered[0], "short description"),
            VisibleIndexOf(rendered[1], "long description"));
    }

    [Fact(DisplayName = "uses the configured minimum primary column width")]
    public void Uses_the_configured_minimum_primary_column_width()
    {
        var list = new SelectList(
            [
                new SelectItem { Value = "a", Label = "a", Description = "first" },
                new SelectItem { Value = "bb", Label = "bb", Description = "second" },
            ],
            5,
            _theme,
            new SelectListLayoutOptions { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 20 });

        var rendered = list.Render(80);

        Assert.Equal(14, rendered[0].IndexOf("first", StringComparison.Ordinal));
        Assert.Equal(14, rendered[1].IndexOf("second", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "uses the configured maximum primary column width")]
    public void Uses_the_configured_maximum_primary_column_width()
    {
        var list = new SelectList(
            [
                new SelectItem
                {
                    Value = "very-long-command-name-that-needs-truncation",
                    Label = "very-long-command-name-that-needs-truncation",
                    Description = "first",
                },
                new SelectItem { Value = "short", Label = "short", Description = "second" },
            ],
            5,
            _theme,
            new SelectListLayoutOptions { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 20 });

        var rendered = list.Render(80);

        Assert.Equal(22, VisibleIndexOf(rendered[0], "first"));
        Assert.Equal(22, VisibleIndexOf(rendered[1], "second"));
    }

    [Fact(DisplayName = "allows overriding primary truncation while preserving description alignment")]
    public void Allows_overriding_primary_truncation_while_preserving_description_alignment()
    {
        var list = new SelectList(
            [
                new SelectItem
                {
                    Value = "very-long-command-name-that-needs-truncation",
                    Label = "very-long-command-name-that-needs-truncation",
                    Description = "first",
                },
                new SelectItem { Value = "short", Label = "short", Description = "second" },
            ],
            5,
            _theme,
            new SelectListLayoutOptions
            {
                MinPrimaryColumnWidth = 12,
                MaxPrimaryColumnWidth = 12,
                TruncatePrimary = static context => context.Text.Length <= context.MaxWidth
                    ? context.Text
                    : context.Text[..Math.Max(0, context.MaxWidth - 1)] + "…",
            });

        var rendered = list.Render(80);

        Assert.Contains("…", rendered[0], StringComparison.Ordinal);
        Assert.Equal(VisibleIndexOf(rendered[0], "first"), VisibleIndexOf(rendered[1], "second"));
    }

    private static int VisibleIndexOf(string line, string text)
    {
        var index = line.IndexOf(text, StringComparison.Ordinal);
        Assert.NotEqual(-1, index);
        return TextMeasurement.VisibleWidth(line[..index]);
    }
}
