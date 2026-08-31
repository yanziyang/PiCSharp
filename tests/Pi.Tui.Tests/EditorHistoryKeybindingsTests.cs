using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorHistoryKeybindingsTests
{
    [Fact(DisplayName = "browses history directly without first moving the cursor")]
    public void Browses_history_directly_without_first_moving_the_cursor()
    {
        KeybindingsManager.SetKeybindings(
            new KeybindingsManager(
                TuiKeybindings.Definitions,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tui.editor.historyPrevious"] = "ctrl+p",
                    ["tui.editor.historyNext"] = "ctrl+n",
                }));

        try
        {
            var editor = CreateEditor();
            editor.AddToHistory("older prompt");
            editor.AddToHistory("newer\nmultiline prompt");
            editor.SetText("draft");
            editor.HandleInput("\x1b[D");
            editor.HandleInput("\x1b[D");

            editor.HandleInput("\x10");
            Assert.Equal("newer\nmultiline prompt", editor.GetText());
            Assert.Equal((0, 0), editor.GetCursor());

            editor.HandleInput("\x10");
            Assert.Equal("older prompt", editor.GetText());

            editor.HandleInput("\x0e");
            Assert.Equal("newer\nmultiline prompt", editor.GetText());
            Assert.Equal((1, 16), editor.GetCursor());

            editor.HandleInput("\x0e");
            Assert.Equal("draft", editor.GetText());
            Assert.Equal((0, 3), editor.GetCursor());
        }
        finally
        {
            KeybindingsManager.SetKeybindings(new KeybindingsManager(TuiKeybindings.Definitions));
        }
    }
}
