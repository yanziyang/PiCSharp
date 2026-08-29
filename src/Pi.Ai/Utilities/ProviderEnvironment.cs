namespace Pi.Ai;

/// <summary>Resolves provider-scoped environment overrides.</summary>
public static class ProviderEnvironmentUtilities
{
    /// <summary>
    /// Resolves a value from scoped overrides first, then the process environment. Empty values
    /// follow JavaScript truthiness and do not mask a later value.
    /// </summary>
    public static string? GetProviderEnvValue(
        string name,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (environment is not null && environment.TryGetValue(name, out var scoped) && !string.IsNullOrEmpty(scoped))
        {
            return scoped;
        }

        var processValue = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(processValue) ? null : processValue;
    }
}
