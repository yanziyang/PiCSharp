namespace Pi.Tui;

/// <summary>Minimal component contract shared by the Pi terminal UI.</summary>
public interface IComponent
{
    /// <summary>Renders the component into terminal lines for the requested width.</summary>
    IReadOnlyList<string> Render(int width);

    /// <summary>Invalidates cached rendering state.</summary>
    void Invalidate();
}

/// <summary>Zero-width marker emitted by focused components at the hardware cursor position.</summary>
public static class TuiConstants
{
    /// <summary>Pi's cursor marker escape sequence.</summary>
    public const string CursorMarker = "\x1b_pi:c\x07";
}

/// <summary>Base component that owns an ordered list of child components.</summary>
public class Container : IComponent
{
    private readonly List<IComponent> _children = [];

    /// <summary>Children in insertion order.</summary>
    public IReadOnlyList<IComponent> Children => _children;

    /// <summary>Adds a child to the end of the container.</summary>
    public virtual void AddChild(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        _children.Add(component);
    }

    /// <summary>Removes a child by object identity.</summary>
    public virtual void RemoveChild(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var index = _children.FindIndex(child => ReferenceEquals(child, component));
        if (index >= 0)
        {
            _children.RemoveAt(index);
        }
    }

    /// <summary>Removes all children.</summary>
    public virtual void Clear() => _children.Clear();

    /// <inheritdoc />
    public virtual void Invalidate()
    {
        foreach (var child in _children)
        {
            child.Invalidate();
        }
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Render(int width)
    {
        var lines = new List<string>();
        foreach (var child in _children)
        {
            lines.AddRange(child.Render(width));
        }

        return lines;
    }
}
