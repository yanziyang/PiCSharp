namespace Pi.Tui;

/// <summary>Integer rectangle in terminal cells.</summary>
public readonly record struct LayoutRect(int X, int Y, int Width, int Height);

/// <summary>One component's laid-out geometry and clipped rendered content.</summary>
public sealed class LayoutBox
{
    private readonly List<LayoutBox> _children = [];

    internal LayoutBox(IComponent component, LayoutRect rect, LayoutRect clip)
    {
        Component = component;
        Rect = rect;
        Clip = clip;
    }

    /// <summary>Component represented by this box.</summary>
    public IComponent Component { get; }

    /// <summary>Allocated rectangle.</summary>
    public LayoutRect Rect { get; internal set; }

    /// <summary>Visible intersection with ancestor clipping.</summary>
    public LayoutRect Clip { get; internal set; }

    /// <summary>Child boxes in paint order.</summary>
    public IReadOnlyList<LayoutBox> Children => _children;

    /// <summary>Parent box, or null for the root.</summary>
    public LayoutBox? Parent { get; internal set; }

    /// <summary>Cached leaf lines, when this box renders directly.</summary>
    public IReadOnlyList<string>? Lines { get; internal set; }

    /// <summary>First cached line painted into the allocated rectangle.</summary>
    public int LineOffset { get; internal set; }

    /// <summary>Rendering layer for future overlay composition.</summary>
    public int Layer { get; internal set; }

    /// <summary>Scroll state when this box represents a scroll node.</summary>
    public IScrollLayoutState? ScrollView { get; internal set; }

    /// <summary>Cached scroll-content lines, when available.</summary>
    public IReadOnlyList<string>? ScrollContentLines { get; internal set; }

    internal void AddChild(LayoutBox child)
    {
        child.Parent = this;
        _children.Add(child);
    }
}

/// <summary>Complete layout geometry and painted terminal lines for one viewport.</summary>
public sealed class LayoutFrame
{
    internal LayoutFrame(LayoutBox root, int width, int height, IReadOnlyList<string> lines, IScrollLayoutState? primary)
    {
        Root = root;
        Width = width;
        Height = height;
        Lines = lines;
        PrimaryScrollView = primary;
    }

    /// <summary>Root geometry box.</summary>
    public LayoutBox Root { get; }

    /// <summary>Safe viewport width.</summary>
    public int Width { get; }

    /// <summary>Safe viewport height.</summary>
    public int Height { get; }

    /// <summary>Painted lines, one per viewport row.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Primary scroll surface selected during layout.</summary>
    public IScrollLayoutState? PrimaryScrollView { get; }
}

/// <summary>Layout engine for component trees and stack containers.</summary>
public static class LayoutEngine
{
    private sealed class LayoutContext
    {
        internal required LayoutViewport Viewport { get; init; }
        internal Dictionary<IComponent, Dictionary<int, IReadOnlyList<string>>> RenderCache { get; } =
            new(ReferenceEqualityComparer.Instance);
        internal required Action RequestRender { get; init; }
        internal IScrollLayoutState? PrimaryScrollView { get; set; }
    }

    /// <summary>Lays out and paints a component tree into a terminal viewport.</summary>
    public static LayoutFrame RenderLayoutFrame(
        IComponent root,
        int width,
        int height,
        Action requestRender)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(requestRender);
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);
        var context = new LayoutContext
        {
            Viewport = new LayoutViewport(safeWidth, safeHeight),
            RequestRender = requestRender,
        };
        var rootBox = LayoutComponent(
            context,
            root,
            0,
            0,
            safeWidth,
            safeHeight,
            new LayoutRect(0, 0, safeWidth, safeHeight));
        var lines = Enumerable.Repeat(string.Empty, safeHeight).ToArray();
        PaintBox(rootBox, lines, safeWidth);
        return new LayoutFrame(rootBox, safeWidth, safeHeight, lines, context.PrimaryScrollView);
    }

    /// <summary>Finds the geometry box associated with a scroll state.</summary>
    public static LayoutBox? GetScrollViewBox(LayoutFrame frame, IScrollLayoutState scrollView)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(scrollView);
        return Visit(frame.Root, box => ReferenceEquals(box.ScrollView, scrollView));
    }

    /// <summary>Returns scroll states under a terminal cell, deepest first.</summary>
    public static IReadOnlyList<IScrollLayoutState> GetScrollViewsAt(LayoutFrame frame, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var result = new List<(IScrollLayoutState ScrollView, int Depth)>();
        Visit(frame.Root, 0);
        result.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));
        return result.Select(static item => item.ScrollView).ToArray();

        void Visit(LayoutBox box, int depth)
        {
            if (!Contains(box.Clip, x, y))
            {
                return;
            }

            if (box.ScrollView is not null && Contains(box.Rect, x, y))
            {
                result.Add((box.ScrollView, depth));
            }

            foreach (var child in box.Children)
            {
                Visit(child, depth + 1);
            }
        }
    }

    private static LayoutBox? Visit(LayoutBox box, Func<LayoutBox, bool> predicate)
    {
        if (predicate(box))
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            var match = Visit(child, predicate);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static LayoutBox LayoutComponent(
        LayoutContext context,
        IComponent component,
        int x,
        int y,
        int width,
        int? height,
        LayoutRect clip)
    {
        var safeWidth = Math.Max(1, width);
        var node = LayoutNodes.GetLayoutNode(component);
        if (node is null)
        {
            var lines = RenderCached(context, component, safeWidth);
            var allocatedHeight = height is null ? lines.Count : Math.Max(0, height.Value);
            var lineOffset = 0;
            if (lines.Count > allocatedHeight && allocatedHeight > 0)
            {
                var cursorLine = lines
                    .Select((line, index) => (line, index))
                    .FirstOrDefault(item => item.line.Contains(TuiConstants.CursorMarker, StringComparison.Ordinal));
                if (cursorLine.line is not null && cursorLine.index >= allocatedHeight)
                {
                    lineOffset = cursorLine.index - allocatedHeight + 1;
                }
            }

            var rect = new LayoutRect(x, y, safeWidth, allocatedHeight);
            return new LayoutBox(component, rect, Intersect(clip, rect))
            {
                Lines = lines,
                LineOffset = lineOffset,
            };
        }

        if (node is ScrollLayoutNode scroll)
        {
            var previousScrollTop = Math.Max(0, scroll.State.ScrollTop);
            var contentWidth = Math.Max(1, scroll.State.GetContentWidth(safeWidth));
            var childBox = LayoutComponent(
                context,
                scroll.Component,
                x,
                y - previousScrollTop,
                contentWidth,
                null,
                clip);
            var contentHeight = childBox.Rect.Height;
            var viewportHeight = height is null ? contentHeight : Math.Max(0, height.Value);
            scroll.State.UpdateLayout(contentHeight, viewportHeight, context.RequestRender);
            TranslateBox(childBox, previousScrollTop - Math.Max(0, scroll.State.ScrollTop));
            if (scroll.State.Primary || context.PrimaryScrollView is null)
            {
                context.PrimaryScrollView = scroll.State;
            }

            var rect = new LayoutRect(x, y, safeWidth, viewportHeight);
            var box = new LayoutBox(component, rect, Intersect(clip, rect))
            {
                ScrollView = scroll.State,
                ScrollContentLines = RenderCached(context, scroll.Component, contentWidth),
            };
            box.AddChild(childBox);
            UpdateClips(childBox, box.Clip);
            return box;
        }

        var stack = (StackLayoutNode)node;
        var entries = StackLayoutMath.VisibleStackEntries(stack.Entries, context.Viewport);
        var gapTotal = Math.Max(0, entries.Length - 1) * stack.Gap;
        if (stack.Type == StackLayoutType.VStack)
        {
            var intrinsicHeights = entries
                .Select(entry => entry.Basis is not null
                    ? ToSize(entry.Basis)
                    : RenderCached(context, entry.Component, safeWidth).Count)
                .ToArray();
            var sizes = StackLayoutMath.AllocateStackSizes(entries, intrinsicHeights, height, stack.Gap);
            var naturalHeight = sizes.Sum() + gapTotal;
            var allocatedHeight = height is null ? naturalHeight : Math.Max(0, height.Value);
            var rect = new LayoutRect(x, y, safeWidth, allocatedHeight);
            var box = new LayoutBox(component, rect, Intersect(clip, rect));
            var childY = y;
            for (var index = 0; index < entries.Length; index++)
            {
                box.AddChild(LayoutComponent(
                    context,
                    entries[index].Component,
                    x,
                    childY,
                    safeWidth,
                    sizes[index],
                    box.Clip));
                childY += sizes[index] + stack.Gap;
            }

            return box;
        }

        var intrinsicWidths = entries
            .Select(entry => entry.Basis is not null
                ? ToSize(entry.Basis)
                : RenderCached(context, entry.Component, safeWidth).DefaultIfEmpty(string.Empty).Max(TextMeasurement.VisibleWidth))
            .ToArray();
        var widths = StackLayoutMath.AllocateStackSizes(entries, intrinsicWidths, safeWidth, stack.Gap);
        var crossIntrinsicHeights = entries
            .Select((entry, index) => RenderCached(context, entry.Component, Math.Max(1, widths[index])).Count)
            .ToArray();
        var allocatedCrossHeight = height is null ? crossIntrinsicHeights.DefaultIfEmpty(0).Max() : Math.Max(0, height.Value);
        var horizontalRect = new LayoutRect(x, y, safeWidth, allocatedCrossHeight);
        var horizontalBox = new LayoutBox(component, horizontalRect, Intersect(clip, horizontalRect));
        var childX = x;
        for (var index = 0; index < entries.Length; index++)
        {
            var naturalHeight = crossIntrinsicHeights[index];
            var childHeight = stack.Align == StackAlign.Stretch
                ? allocatedCrossHeight
                : Math.Min(allocatedCrossHeight, naturalHeight);
            var childY = stack.Align switch
            {
                StackAlign.Center => y + (allocatedCrossHeight - childHeight) / 2,
                StackAlign.End => y + allocatedCrossHeight - childHeight,
                _ => y,
            };
            if (widths[index] == 0)
            {
                horizontalBox.AddChild(new LayoutBox(
                    entries[index].Component,
                    new LayoutRect(childX, childY, 0, childHeight),
                    new LayoutRect(childX, childY, 0, 0)));
            }
            else
            {
                horizontalBox.AddChild(LayoutComponent(
                    context,
                    entries[index].Component,
                    childX,
                    childY,
                    widths[index],
                    childHeight,
                    horizontalBox.Clip));
            }

            childX += widths[index] + stack.Gap;
        }

        return horizontalBox;
    }

    private static IReadOnlyList<string> RenderCached(LayoutContext context, IComponent component, int width)
    {
        var safeWidth = Math.Max(1, width);
        if (!context.RenderCache.TryGetValue(component, out var widths))
        {
            widths = new Dictionary<int, IReadOnlyList<string>>();
            context.RenderCache.Add(component, widths);
        }

        if (!widths.TryGetValue(safeWidth, out var lines))
        {
            lines = component.Render(safeWidth);
            widths.Add(safeWidth, lines);
        }

        return lines;
    }

    private static void PaintBox(LayoutBox box, string[] screen, int totalWidth)
    {
        if (box.Lines is not null)
        {
            var firstRow = Math.Max(Math.Max(box.Rect.Y, box.Clip.Y), 0);
            var lastRow = Math.Min(Math.Min(box.Rect.Y + box.Rect.Height, box.Clip.Y + box.Clip.Height), screen.Length);
            for (var row = firstRow; row < lastRow; row++)
            {
                var sourceLine = box.Lines.ElementAtOrDefault(box.LineOffset + row - box.Rect.Y);
                if (sourceLine is null)
                {
                    continue;
                }

                screen[row] = LineLayout.Composite(
                    screen[row],
                    sourceLine,
                    box.Rect.X,
                    box.Rect.Width,
                    totalWidth);
            }
        }

        foreach (var child in box.Children)
        {
            PaintBox(child, screen, totalWidth);
        }
    }

    private static void TranslateBox(LayoutBox box, int deltaY)
    {
        box.Rect = box.Rect with { Y = box.Rect.Y + deltaY };
        foreach (var child in box.Children)
        {
            TranslateBox(child, deltaY);
        }
    }

    private static void UpdateClips(LayoutBox box, LayoutRect parentClip)
    {
        box.Clip = Intersect(parentClip, box.Rect);
        foreach (var child in box.Children)
        {
            UpdateClips(child, box.Clip);
        }
    }

    private static LayoutRect Intersect(LayoutRect first, LayoutRect second)
    {
        var x = Math.Max(first.X, second.X);
        var y = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return new LayoutRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static bool Contains(LayoutRect rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

    private static int ToSize(double? value) =>
        value is null || !double.IsFinite(value.Value)
            ? 0
            : (int)Math.Clamp(Math.Floor(value.Value), 0, int.MaxValue);
}

/// <summary>Short alias for the layout engine's main rendering entry point.</summary>
public static class Layout
{
    /// <summary>Lays out and paints a component tree into a terminal viewport.</summary>
    public static LayoutFrame RenderLayoutFrame(IComponent root, int width, int height, Action requestRender) =>
        LayoutEngine.RenderLayoutFrame(root, width, height, requestRender);
}
