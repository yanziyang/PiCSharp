using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the deterministic viewport-layout coverage from the Pi TUI package.</summary>
public sealed class LayoutTests
{
    [Fact]
    public void AllocatesVerticalGrowSpaceDeterministically()
    {
        var top = new Text("top", 0, 0);
        var body = new Text("body", 0, 0);
        var root = new VStack(
        [
            new StackChild(top, new StackEntryOptions { Basis = 1, Shrink = 0 }),
            new StackChild(body, new StackEntryOptions { Basis = 0, Grow = 1 }),
        ]);

        var frame = LayoutEngine.RenderLayoutFrame(root, 10, 4, static () => { });

        Assert.Equal([1, 3], frame.Root.Children.Select(child => child.Rect.Height));
        Assert.Equal(["top", "body", "", ""], VisibleLines(frame.Lines));
    }

    [Fact]
    public void ShrinksEntriesToTheirMinimumSizes()
    {
        var first = new Text("a1\na2\na3", 0, 0);
        var second = new Text("b1\nb2\nb3", 0, 0);
        var root = new VStack(
        [
            new StackChild(first, new StackEntryOptions { Shrink = 1, MinSize = 1 }),
            new StackChild(second, new StackEntryOptions { Shrink = 0 }),
        ]);

        var frame = LayoutEngine.RenderLayoutFrame(root, 10, 4, static () => { });

        Assert.Equal([1, 3], frame.Root.Children.Select(child => child.Rect.Height));
        Assert.Equal(["a1", "b1", "b2", "b3"], VisibleLines(frame.Lines));
    }

    [Fact]
    public void IncludesNestedMinimumSizesInIntrinsicStackMeasurement()
    {
        var dock = new VStack(
        [
            new StackChild(new Text("top1\ntop2\ntop3", 0, 0)),
            new StackChild(new Text("selector", 0, 0), new StackEntryOptions { MinSize = 3 }),
            new StackChild(new Text("below", 0, 0)),
            new StackChild(new Text("footer", 0, 0), new StackEntryOptions { MinSize = 1 }),
        ]);
        var root = new VStack(
        [
            new StackChild(new Text("body", 0, 0), new StackEntryOptions { Basis = 0, Grow = 1, MinSize = 1 }),
            new StackChild(dock, new StackEntryOptions { MinSize = 1 }),
        ]);

        var frame = LayoutEngine.RenderLayoutFrame(root, 10, 9, static () => { });

        Assert.Equal(
            ["body", "top1", "top2", "top3", "selector", "", "", "below", "footer"],
            VisibleLines(frame.Lines));
    }

    [Fact]
    public void OmitsGapsAroundInvisibleEntries()
    {
        var root = new VStack(
            [
                new StackChild(new Text("one", 0, 0)),
                new StackChild(new Text("hidden", 0, 0), new StackEntryOptions { Visible = static _ => false }),
                new StackChild(new Text("two", 0, 0)),
            ],
            new StackOptions { Gap = 1 });

        Assert.Equal(["one", "", "two"], VisibleLines(root.Render(10)));
    }

    [Fact]
    public void ComposesHorizontalChildrenAtAllocatedWidths()
    {
        var left = new Text("left", 0, 0);
        var right = new Text("right", 0, 0);
        var root = new HStack(
        [
            new StackChild(left, new StackEntryOptions { Basis = 6, Shrink = 0 }),
            new StackChild(right, new StackEntryOptions { Basis = 6, Shrink = 0 }),
        ]);

        var frame = LayoutEngine.RenderLayoutFrame(root, 12, 1, static () => { });

        Assert.Equal(["left  right"], VisibleLines(frame.Lines));
    }

    [Fact]
    public void DoesNotPaintZeroWidthHorizontalChildren()
    {
        var root = new HStack(
        [
            new StackChild(new Text("hidden", 0, 0), new StackEntryOptions { Basis = 0, Shrink = 0 }),
            new StackChild(new Text("shown", 0, 0), new StackEntryOptions { Basis = 0, Grow = 1 }),
        ]);

        var frame = LayoutEngine.RenderLayoutFrame(root, 5, 1, static () => { });

        Assert.Equal(["shown"], VisibleLines(frame.Lines));
    }

    [Fact]
    public void PreservesLayoutTreeParentsAndClipsNestedChildren()
    {
        var child = new Text("child", 0, 0);
        var inner = new VStack([new StackChild(child)]);
        var root = new VStack([new StackChild(inner, new StackEntryOptions { Basis = 1 })]);

        var frame = LayoutEngine.RenderLayoutFrame(root, 5, 1, static () => { });
        var rootChild = Assert.Single(frame.Root.Children);
        var innerChild = Assert.Single(rootChild.Children);

        Assert.Same(frame.Root, rootChild.Parent);
        Assert.Same(rootChild, innerChild.Parent);
        Assert.Equal(new LayoutRect(0, 0, 5, 1), rootChild.Rect);
        Assert.Equal(new LayoutRect(0, 0, 5, 1), innerChild.Clip);
    }

    [Fact]
    public void CachesLeafRenderingForTheLayoutPass()
    {
        var component = new CountingComponent("content");
        var root = new HStack(
        [
            new StackChild(component, new StackEntryOptions { Basis = 7 }),
        ]);

        LayoutEngine.RenderLayoutFrame(root, 7, 1, static () => { });

        Assert.Equal(1, component.RenderCount);
    }

    [Fact]
    public void BasicComponentsMatchTheirViewportContracts()
    {
        var text = new Text("hello", paddingX: 1, paddingY: 1);
        Assert.Equal(["       ", " hello ", "       "], text.Render(7));

        var box = new Box(1, 1);
        box.AddChild(new Text("x", 0, 0));
        Assert.Equal(["    ", " x  ", "    "], box.Render(4));

        var truncated = new TruncatedText("abcdef", 1, 1);
        Assert.Equal(["      ", " abcd ", "      "], truncated.Render(6));

        var spacer = new Spacer(2);
        Assert.Equal(["", ""], spacer.Render(4));
    }

    [Fact]
    public void UsesCursorMarkerToKeepFocusedContentVisible()
    {
        var component = new CursorComponent();
        var frame = LayoutEngine.RenderLayoutFrame(component, 8, 2, static () => { });

        Assert.Equal(1, frame.Root.LineOffset);
        Assert.Equal(["second", "cursor"], VisibleLines(frame.Lines));
    }

    private static string[] VisibleLines(IEnumerable<string> lines) =>
        lines.Select(line => line.Replace(TuiConstants.CursorMarker, string.Empty, StringComparison.Ordinal).TrimEnd())
            .ToArray();

    private sealed class CountingComponent(string line) : IComponent
    {
        public int RenderCount { get; private set; }

        public IReadOnlyList<string> Render(int width)
        {
            RenderCount++;
            return [line.PadRight(Math.Max(1, width))];
        }

        public void Invalidate() { }
    }

    private sealed class CursorComponent : IComponent
    {
        public IReadOnlyList<string> Render(int width) =>
        [
            "first ".PadRight(width),
            "second ".PadRight(width),
            TuiConstants.CursorMarker + "cursor".PadRight(Math.Max(0, width - TuiConstants.CursorMarker.Length)),
        ];

        public void Invalidate() { }
    }
}
