using System.Diagnostics.CodeAnalysis;

namespace Pi.Tui;

/// <summary>Generic undo stack that stores detached state snapshots.</summary>
[SuppressMessage("Design", "CA1711", Justification = "UndoStack is the public name of the upstream TUI utility.")]
public sealed class UndoStack<T>
{
    private readonly List<T> _stack = [];
    private readonly Func<T, T> _clone;

    /// <summary>Creates an undo stack with an optional state-cloning function.</summary>
    /// <remarks>
    /// TypeScript's <c>structuredClone</c> has no general AOT-safe .NET equivalent. The default
    /// path handles immutable values, arrays, JSON nodes, and <see cref="ICloneable"/> states;
    /// callers with mutable domain objects can supply the exact clone operation for that state.
    /// </remarks>
    public UndoStack(Func<T, T>? clone = null) => _clone = clone ?? CloneState;

    /// <summary>Pushes a detached snapshot of the supplied state.</summary>
    public void Push(T state) => _stack.Add(_clone(state));

    /// <summary>Pops the most recent snapshot, or the default value when empty.</summary>
    public T? Pop() => _stack.Count == 0 ? default : PopCore();

    /// <summary>Removes all snapshots.</summary>
    public void Clear() => _stack.Clear();

    /// <summary>Returns the number of snapshots currently stored.</summary>
    public int Length => _stack.Count;

    private T PopCore()
    {
        var lastIndex = _stack.Count - 1;
        var state = _stack[lastIndex];
        _stack.RemoveAt(lastIndex);
        return state;
    }

    private static T CloneState(T state)
    {
        if (state is null) return state!;
        if (state is ICloneable cloneable) return (T)cloneable.Clone()!;
        if (state is System.Text.Json.Nodes.JsonNode node) return (T)(object)node.DeepClone()!;
        if (state is Array array) return (T)(object)CloneArray(array);
        return state;
    }

    private static Array CloneArray(Array source)
    {
        var clone = (Array)source.Clone();
        if (!source.GetType().GetElementType()!.IsValueType)
        {
            foreach (var index in EnumerateIndices(source))
            {
                var value = source.GetValue(index);
                if (value is ICloneable cloneable)
                {
                    clone.SetValue(cloneable.Clone(), index);
                }
                else if (value is System.Text.Json.Nodes.JsonNode node)
                {
                    clone.SetValue(node.DeepClone(), index);
                }
            }
        }

        return clone;
    }

    private static IEnumerable<int[]> EnumerateIndices(Array array)
    {
        var ranks = array.Rank;
        var indices = new int[ranks];
        var lengths = new int[ranks];
        for (var rank = 0; rank < ranks; rank++)
        {
            lengths[rank] = array.GetLength(rank);
            indices[rank] = array.GetLowerBound(rank);
        }

        while (true)
        {
            yield return [.. indices];
            var dimension = ranks - 1;
            while (dimension >= 0)
            {
                indices[dimension]++;
                if (indices[dimension] < array.GetLowerBound(dimension) + lengths[dimension]) break;
                indices[dimension] = array.GetLowerBound(dimension);
                dimension--;
            }

            if (dimension < 0) yield break;
        }
    }
}
