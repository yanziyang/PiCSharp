namespace Pi.Tui;

/// <summary>Viewport dimensions supplied to visibility predicates.</summary>
public readonly record struct LayoutViewport(int Width, int Height);

/// <summary>Orientation of a stack layout node.</summary>
public enum StackLayoutType
{
    /// <summary>Children are placed from top to bottom.</summary>
    VStack,

    /// <summary>Children are placed from left to right.</summary>
    HStack,
}

/// <summary>Cross-axis alignment for horizontal stack children.</summary>
public enum StackAlign
{
    /// <summary>Stretch each child to the allocated cross-axis size.</summary>
    Stretch,

    /// <summary>Place each child at the start of the cross-axis.</summary>
    Start,

    /// <summary>Center each child on the cross-axis.</summary>
    Center,

    /// <summary>Place each child at the end of the cross-axis.</summary>
    End,
}

/// <summary>One component and its main-axis allocation constraints.</summary>
public sealed class StackLayoutEntry
{
    /// <summary>The component to place.</summary>
    public required IComponent Component { get; init; }

    /// <summary>Fixed main-axis basis; null means measure the component.</summary>
    public double? Basis { get; init; }

    /// <summary>Relative weight used when distributing extra space.</summary>
    public double Grow { get; init; }

    /// <summary>Relative weight used when removing space.</summary>
    public double Shrink { get; init; } = 1;

    /// <summary>Minimum main-axis size.</summary>
    public double? MinSize { get; init; }

    /// <summary>Maximum main-axis size.</summary>
    public double? MaxSize { get; init; }

    /// <summary>Optional viewport predicate.</summary>
    public Func<LayoutViewport, bool>? Visible { get; init; }
}

/// <summary>State supplied by a scroll container to the layout engine.</summary>
public interface IScrollLayoutState
{
    /// <summary>Current vertical content offset.</summary>
    int ScrollTop { get; }

    /// <summary>Whether this is the primary scroll surface.</summary>
    bool Primary { get; }

    /// <summary>Returns the width available to scroll content.</summary>
    int GetContentWidth(int width);

    /// <summary>Updates content and viewport geometry.</summary>
    void UpdateLayout(int contentHeight, int viewportHeight, Action requestRender);
}

/// <summary>Base layout node produced by a component.</summary>
public abstract record LayoutNode;

/// <summary>Stack layout node.</summary>
public sealed record StackLayoutNode(
    StackLayoutType Type,
    IReadOnlyList<StackLayoutEntry> Entries,
    int Gap,
    StackAlign Align) : LayoutNode;

/// <summary>Scroll layout node.</summary>
public sealed record ScrollLayoutNode(IComponent Component, IScrollLayoutState State) : LayoutNode;

/// <summary>Component that exposes an explicit layout node.</summary>
public interface ILayoutComponent : IComponent
{
    /// <summary>Returns the node used to lay out this component.</summary>
    LayoutNode GetLayoutNode();
}

/// <summary>Layout-node lookup helpers.</summary>
public static class LayoutNodes
{
    /// <summary>Returns a component's layout node when it exposes one.</summary>
    public static LayoutNode? GetLayoutNode(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component is ILayoutComponent layoutComponent ? layoutComponent.GetLayoutNode() : null;
    }
}
