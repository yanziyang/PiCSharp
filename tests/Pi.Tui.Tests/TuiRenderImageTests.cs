using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Records the seven <c>tui-render.test.ts</c> cases that exercise Kitty image placement.
/// </summary>
/// <remarks>
/// T5.2b ports <c>tui.ts</c> against a stubbed image seam
/// (<see cref="Pi.Tui.Images.NoImageTerminalImageSeam"/>), which reports no image protocol. These
/// cases assert image row reservation, id deletion and placement geometry, none of which the stub
/// can produce. They are held here so the debt is counted rather than absent: T5.5 supplies the
/// real seam implementation and un-skips them.
/// </remarks>
public sealed class TuiRenderImageTests
{
    private const string _reason =
        "Kitty image placement needs the real terminal-image seam, which is T5.5.";

    [Fact(DisplayName = "reserves Kitty image rows before drawing during full redraw fallbacks", Skip = _reason)]
    public void Reserves_kitty_image_rows_before_drawing_during_full_redraw_fallbacks()
    {
    }

    [Fact(DisplayName = "clears reserved Kitty image rows before drawing appended image placements", Skip = _reason)]
    public void Clears_reserved_kitty_image_rows_before_drawing_appended_image_placements()
    {
    }

    [Fact(DisplayName = "redraws image lines when an earlier reserved image row changes", Skip = _reason)]
    public void Redraws_image_lines_when_an_earlier_reserved_image_row_changes()
    {
    }

    [Fact(DisplayName = "deletes previously rendered image ids during full redraws", Skip = _reason)]
    public void Deletes_previously_rendered_image_ids_during_full_redraws()
    {
    }

    [Fact(DisplayName = "deletes changed image ids before drawing moved placements", Skip = _reason)]
    public void Deletes_changed_image_ids_before_drawing_moved_placements()
    {
    }

    [Fact(DisplayName = "does not use cursor-up placement for Kitty images taller than the viewport", Skip = _reason)]
    public void Does_not_use_cursor_up_placement_for_kitty_images_taller_than_the_viewport()
    {
    }

    [Fact(DisplayName = "falls back to full redraw when Kitty image pre-clear would scroll", Skip = _reason)]
    public void Falls_back_to_full_redraw_when_kitty_image_pre_clear_would_scroll()
    {
    }
}
