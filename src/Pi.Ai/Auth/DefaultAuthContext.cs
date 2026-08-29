namespace Pi.Ai;

/// <summary>Factory for the default process environment and local filesystem auth context.</summary>
public static class AuthContextFactory
{
    /// <summary>
    /// Creates an auth context backed by process environment variables and the local filesystem.
    /// </summary>
    public static AuthContext CreateDefaultProviderContext() => new DefaultAuthContext();

    private sealed class DefaultAuthContext : AuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            cancellationToken.ThrowIfCancellationRequested();
            var value = Environment.GetEnvironmentVariable(name);
            return Task.FromResult(string.IsNullOrWhiteSpace(value) ? null : value);
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = path.StartsWith('~')
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
                : path;
            return Task.FromResult(File.Exists(resolved) || Directory.Exists(resolved));
        }
    }
}
