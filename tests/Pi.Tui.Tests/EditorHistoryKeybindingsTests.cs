using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Records the upstream editor-history integration case for T5.3b.</summary>
public sealed class EditorHistoryKeybindingsTests
{
    [Fact(
        DisplayName = "browses history directly without first moving the cursor",
        Skip = "Pi.Tui.Editor is T5.3b and is outside the T5.3a input-layer source scope.")]
    public void Browses_history_directly_without_first_moving_the_cursor()
    {
    }
}

