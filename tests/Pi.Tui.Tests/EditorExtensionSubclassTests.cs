using Xunit;
using static Pi.Tui.Tests.EditorTestSupport;

namespace Pi.Tui.Tests;

public sealed class EditorExtensionSubclassTests
{
    [Fact(DisplayName = "extension subclass overrides editor hooks and calls base implementations")]
    public void Extension_subclass_overrides_editor_hooks_and_calls_base_implementations()
    {
        var editor = new ExtensionEditor(CreateTestTui(), DefaultTheme);

        editor.HandleInput("ported");
        var rendered = editor.Render(40);
        editor.Invalidate();

        Assert.Equal("ported", editor.GetText());
        Assert.True(editor.BaseInputComposed);
        Assert.True(editor.BaseRenderComposed);
        Assert.True(editor.BaseInvalidateComposed);
        Assert.EndsWith(" extension", rendered[0], StringComparison.Ordinal);
    }

    private sealed class ExtensionEditor(TUI tui, EditorTheme theme) : Editor(tui, theme)
    {
        public bool BaseInputComposed { get; private set; }

        public bool BaseRenderComposed { get; private set; }

        public bool BaseInvalidateComposed { get; private set; }

        public override void HandleInput(string data)
        {
            base.HandleInput(data);
            BaseInputComposed = GetText().Contains(data, StringComparison.Ordinal);
        }

        public override IReadOnlyList<string> Render(int width)
        {
            var baseLines = base.Render(width);
            BaseRenderComposed = baseLines.Count > 0;
            return [baseLines[0] + " extension", .. baseLines.Skip(1)];
        }

        public override void Invalidate()
        {
            base.Invalidate();
            BaseInvalidateComposed = true;
        }
    }
}
