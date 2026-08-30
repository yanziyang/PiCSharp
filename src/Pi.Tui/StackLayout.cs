namespace Pi.Tui;

#pragma warning disable CA1711 // These public names mirror the upstream Pi TUI API.

/// <summary>Optional constraints for one stack child.</summary>
public sealed class StackEntryOptions
{
    /// <summary>Fixed main-axis basis; null means auto.</summary>
    public double? Basis { get; init; }

    /// <summary>Relative weight for extra main-axis space.</summary>
    public double? Grow { get; init; }

    /// <summary>Relative weight for removed main-axis space.</summary>
    public double? Shrink { get; init; }

    /// <summary>Minimum main-axis size.</summary>
    public double? MinSize { get; init; }

    /// <summary>Maximum main-axis size.</summary>
    public double? MaxSize { get; init; }

    /// <summary>Optional viewport visibility predicate.</summary>
    public Func<LayoutViewport, bool>? Visible { get; init; }
}

/// <summary>One component supplied to a stack, optionally with layout constraints.</summary>
public sealed class StackChild
{
    /// <summary>Initializes an unconstrained stack child.</summary>
    public StackChild(IComponent component) : this(component, null) { }

    /// <summary>Initializes a stack child with layout constraints.</summary>
    public StackChild(IComponent component, StackEntryOptions? options)
    {
        Component = component ?? throw new ArgumentNullException(nameof(component));
        Options = options;
    }

    /// <summary>Child component.</summary>
    public IComponent Component { get; }

    /// <summary>Optional allocation constraints.</summary>
    public StackEntryOptions? Options { get; }

}

/// <summary>Common stack options.</summary>
public sealed class StackOptions
{
    /// <summary>Number of empty rows or columns between children.</summary>
    public double? Gap { get; init; }

    /// <summary>Cross-axis alignment.</summary>
    public StackAlign Align { get; init; } = StackAlign.Stretch;
}

/// <summary>Base class for vertical and horizontal component stacks.</summary>
public abstract class Stack : Container, ILayoutComponent
{
    private readonly List<StackLayoutEntry> _entries = [];

    /// <summary>Configured stack entries in insertion order.</summary>
    protected IReadOnlyList<StackLayoutEntry> Entries => _entries;

    /// <summary>Configured inter-child gap.</summary>
    protected int Gap { get; }

    /// <summary>Configured cross-axis alignment.</summary>
    protected StackAlign Align { get; }

    /// <summary>Stack orientation supplied to the layout engine.</summary>
    protected abstract StackLayoutType LayoutType { get; }

    /// <summary>Initializes a stack.</summary>
    protected Stack(IEnumerable<StackChild>? children = null, StackOptions? options = null)
    {
        options ??= new StackOptions();
        Gap = NormalizeSize(options.Gap, 0);
        Align = options.Align;
        if (children is not null)
        {
            foreach (var child in children)
            {
                AddChild(child.Component, child.Options);
            }
        }
    }

    /// <inheritdoc />
    public override void AddChild(IComponent component)
    {
        AddChild(component, null);
    }

    /// <summary>Adds a child with optional stack constraints.</summary>
    public void AddChild(IComponent component, StackEntryOptions? options)
    {
        base.AddChild(component);
        _entries.Add(new StackLayoutEntry
        {
            Component = component,
            Basis = options?.Basis,
            Grow = NormalizeSize(options?.Grow, 0),
            Shrink = NormalizeSize(options?.Shrink, 1),
            MinSize = options?.MinSize is null ? null : NormalizeSize(options.MinSize, 0),
            MaxSize = options?.MaxSize is null ? null : NormalizeSize(options.MaxSize, int.MaxValue),
            Visible = options?.Visible,
        });
    }

    /// <inheritdoc />
    public override void RemoveChild(IComponent component)
    {
        base.RemoveChild(component);
        var index = _entries.FindIndex(entry => ReferenceEquals(entry.Component, component));
        if (index >= 0)
        {
            _entries.RemoveAt(index);
        }
    }

    /// <inheritdoc />
    public override void Clear()
    {
        base.Clear();
        _entries.Clear();
    }

    /// <inheritdoc />
    public LayoutNode GetLayoutNode() => new StackLayoutNode(LayoutType, _entries, Gap, Align);

    /// <inheritdoc />
    public abstract override IReadOnlyList<string> Render(int width);

    private static int NormalizeSize(double? value, int fallback)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return fallback;
        }

        return Math.Max(0, (int)Math.Min(int.MaxValue, Math.Floor(value.Value)));
    }
}

/// <summary>Horizontal stack component.</summary>
public sealed class HStack : Stack
{
    /// <summary>Initializes a horizontal stack.</summary>
    public HStack(IEnumerable<StackChild>? children = null, StackOptions? options = null)
        : base(children, options) { }

    /// <inheritdoc />
    protected override StackLayoutType LayoutType => StackLayoutType.HStack;

    /// <inheritdoc />
    public override IReadOnlyList<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var entries = StackLayoutMath.VisibleStackEntries(Entries, new LayoutViewport(safeWidth, int.MaxValue));
        if (entries.Length == 0)
        {
            return [];
        }

        var intrinsicWidths = entries
            .Select(entry => entry.Component.Render(safeWidth).DefaultIfEmpty(string.Empty).Max(TextMeasurement.VisibleWidth))
            .ToArray();
        var widths = StackLayoutMath.AllocateStackSizes(entries, intrinsicWidths, safeWidth, Gap);
        var rendered = entries
            .Select((entry, index) => widths[index] == 0 ? (IReadOnlyList<string>)[] : entry.Component.Render(widths[index]))
            .ToArray();
        var height = rendered.Select(lines => lines.Count).DefaultIfEmpty(0).Max();
        var result = Enumerable.Repeat(string.Empty, height).ToArray();
        var x = 0;
        for (var index = 0; index < rendered.Length; index++)
        {
            var lines = rendered[index];
            var childWidth = widths[index];
            var offset = Align switch
            {
                StackAlign.Center => (height - lines.Count) / 2,
                StackAlign.End => height - lines.Count,
                _ => 0,
            };
            for (var row = 0; row < lines.Count; row++)
            {
                var target = row + offset;
                if (target >= 0 && target < result.Length)
                {
                    result[target] = LineLayout.Composite(
                        result[target],
                        lines[row],
                        x,
                        childWidth,
                        safeWidth);
                }
            }

            x += childWidth + Gap;
        }

        return result;
    }
}

/// <summary>Vertical stack component.</summary>
public sealed class VStack : Stack
{
    /// <summary>Initializes a vertical stack.</summary>
    public VStack(IEnumerable<StackChild>? children = null, StackOptions? options = null)
        : base(children, options) { }

    /// <inheritdoc />
    protected override StackLayoutType LayoutType => StackLayoutType.VStack;

    /// <inheritdoc />
    public override IReadOnlyList<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var entries = StackLayoutMath.VisibleStackEntries(Entries, new LayoutViewport(safeWidth, int.MaxValue));
        var rendered = entries.Select(entry => entry.Component.Render(safeWidth)).ToArray();
        var sizes = StackLayoutMath.AllocateStackSizes(
            entries,
            rendered.Select(lines => lines.Count).ToArray(),
            null,
            Gap);
        var lines = new List<string>();
        for (var index = 0; index < entries.Length; index++)
        {
            if (index > 0)
            {
                lines.AddRange(Enumerable.Repeat(string.Empty, Gap));
            }

            var childLines = rendered[index].Take(sizes[index]).ToArray();
            lines.AddRange(childLines);
            lines.AddRange(Enumerable.Repeat(string.Empty, sizes[index] - childLines.Length));
        }

        return lines;
    }
}

internal static class StackLayoutMath
{
    internal static StackLayoutEntry[] VisibleStackEntries(
        IReadOnlyList<StackLayoutEntry> entries,
        LayoutViewport viewport) =>
        entries.Where(entry => entry.Visible?.Invoke(viewport) ?? true).ToArray();

    internal static int[] AllocateStackSizes(
        IReadOnlyList<StackLayoutEntry> entries,
        IReadOnlyList<int> intrinsicSizes,
        int? availableSize,
        int gap)
    {
        var sizes = entries.Select((entry, index) => ClampSize(
            entry.Basis ?? intrinsicSizes.ElementAtOrDefault(index),
            entry)).ToArray();
        if (availableSize is null)
        {
            return sizes;
        }

        var contentSize = Math.Max(0, availableSize.Value - Math.Max(0, entries.Count - 1) * gap);
        var total = sizes.Sum();
        if (total < contentSize)
        {
            Distribute(sizes, entries, contentSize - total, grow: true);
        }
        else if (total > contentSize)
        {
            Distribute(sizes, entries, total - contentSize, grow: false);
        }

        return sizes;
    }

    private static int ClampSize(double? size, StackLayoutEntry entry)
    {
        var minimum = Math.Max(0, ToInt(entry.MinSize, 0));
        var maximum = Math.Max(minimum, ToInt(entry.MaxSize, int.MaxValue));
        var value = ToInt(size, 0);
        return Math.Max(minimum, Math.Min(maximum, value));
    }

    private static void Distribute(
        int[] sizes,
        IReadOnlyList<StackLayoutEntry> entries,
        int amount,
        bool grow)
    {
        var remaining = amount;
        while (remaining > 0)
        {
            var candidates = entries
                .Select((entry, index) => (entry, index))
                .Where(pair => grow
                    ? pair.entry.Grow > 0 && sizes[pair.index] < ToInt(pair.entry.MaxSize, int.MaxValue)
                    : pair.entry.Shrink > 0 && sizes[pair.index] > ToInt(pair.entry.MinSize, 0))
                .ToArray();
            if (candidates.Length == 0)
            {
                return;
            }

            var totalWeight = candidates.Sum(pair => grow
                ? pair.entry.Grow
                : pair.entry.Shrink * Math.Max(1, sizes[pair.index]));
            if (totalWeight <= 0 || !double.IsFinite(totalWeight))
            {
                return;
            }

            var distributed = 0;
            foreach (var (entry, index) in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var weight = grow ? entry.Grow : entry.Shrink * Math.Max(1, sizes[index]);
                var proposed = Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor(remaining * weight / totalWeight)));
                var capacity = grow
                    ? ToInt(entry.MaxSize, int.MaxValue) - sizes[index]
                    : sizes[index] - ToInt(entry.MinSize, 0);
                var delta = Math.Min(remaining, Math.Min(proposed, Math.Max(0, capacity)));
                if (delta <= 0)
                {
                    continue;
                }

                sizes[index] += grow ? delta : -delta;
                remaining -= delta;
                distributed += delta;
            }

            if (distributed == 0)
            {
                return;
            }
        }
    }

    private static int ToInt(double? value, int fallback)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return fallback;
        }

        return (int)Math.Clamp(Math.Floor(value.Value), 0, int.MaxValue);
    }
}

internal static class LineLayout
{
    internal static string Composite(string baseLine, string overlayLine, int start, int overlayWidth, int totalWidth)
        => TextMeasurement.CompositeTuiLine(baseLine, overlayLine, start, overlayWidth, totalWidth);
}

#pragma warning restore CA1711
