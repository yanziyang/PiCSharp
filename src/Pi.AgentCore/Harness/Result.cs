namespace Pi.AgentCore.Harness;

/// <summary>Additional helpers for the harness result contract.</summary>
public static partial class Result
{
    /// <summary>Returns the success value or throws the failure value.</summary>
    public static TValue GetOrThrow<TValue, TError>(Result<TValue, TError> result)
    {
        if (result.Ok)
        {
            return result.Value!;
        }

        if (result.Error is Exception exception)
        {
            throw exception;
        }

        throw new InvalidOperationException(result.Error?.ToString() ?? "Result failed without an error value.");
    }

    /// <summary>Returns the success value or null for a failed result.</summary>
    public static TValue? GetOrUndefined<TValue, TError>(Result<TValue, TError> result) where TValue : class =>
        result.Ok ? result.Value : null;

    /// <summary>Normalizes an arbitrary thrown value into an exception.</summary>
    public static Exception ToError(object? error) => ResultHelpers.ToError(error);

    /// <summary>Dispatches a tagged error using its tag.</summary>
    public static TValue MatchError<TValue>(
        TaggedErrorValue error,
        IReadOnlyDictionary<string, Func<TaggedErrorValue, TValue>> matchers) =>
        TaggedError.Match(error, matchers);
}
