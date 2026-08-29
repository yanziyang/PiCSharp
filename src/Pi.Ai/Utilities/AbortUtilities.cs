namespace Pi.Ai;

/// <summary>Cancellation helpers for provider operations.</summary>
public static class AbortUtilities
{
    /// <summary>Returns the supplied operation token, using the non-cancelable token when absent.</summary>
    public static CancellationToken OperationSignal(CancellationToken signal = default) => signal;

    /// <summary>
    /// Stops awaiting an operation when the token is canceled while observing a later fault from
    /// the abandoned task so it cannot become an unobserved exception.
    /// </summary>
    public static async Task<T> RaceWithAbortSignal<T>(Task<T> operation, CancellationToken signal)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObserveAbandonedFault(operation);
        return await operation.WaitAsync(signal).ConfigureAwait(false);
    }

    private static void ObserveAbandonedFault(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
