namespace Pi.Ai;

/// <summary>Interruptible delay helpers.</summary>
public static class SleepUtilities
{
    /// <summary>Delays for the requested duration and throws when the token is canceled.</summary>
    public static Task SleepAsync(int milliseconds, CancellationToken signal)
    {
        return Task.Delay(milliseconds, signal);
    }
}
