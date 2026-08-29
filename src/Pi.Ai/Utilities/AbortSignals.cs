namespace Pi.Ai;

/// <summary>A linked cancellation token and the resources required to release it.</summary>
public sealed class CombinedAbortSignal : IDisposable
{
    private readonly CancellationTokenSource? _source;
    private int _disposed;

    internal CombinedAbortSignal(CancellationToken? signal, CancellationTokenSource? source)
    {
        Signal = signal;
        _source = source;
    }

    /// <summary>The combined token, or null when no input token was active.</summary>
    public CancellationToken? Signal { get; }

    /// <summary>Releases the linked cancellation source. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source?.Dispose();
        }
    }
}

/// <summary>Combines the active cancellation tokens used by one provider operation.</summary>
public static class AbortSignalUtilities
{
    /// <summary>
    /// Returns the sole active token unchanged, or creates a linked token when multiple active
    /// tokens can cancel the operation. The returned object must be disposed after the operation.
    /// </summary>
    public static CombinedAbortSignal CombineAbortSignals(IReadOnlyList<CancellationToken> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var activeSignals = signals.Where(static signal => signal.CanBeCanceled).ToArray();
        if (activeSignals.Length == 0)
        {
            return new CombinedAbortSignal(null, null);
        }

        if (activeSignals.Length == 1)
        {
            return new CombinedAbortSignal(activeSignals[0], null);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(activeSignals);
        return new CombinedAbortSignal(source.Token, source);
    }
}
