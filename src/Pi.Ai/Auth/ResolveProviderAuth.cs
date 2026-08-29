using System.Diagnostics.CodeAnalysis;

namespace Pi.Ai;

/// <summary>Stable error-code values emitted by the models/auth runtime.</summary>
public static class ModelsErrorCodes
{
    /// <summary>Dynamic model source failure.</summary>
    public const string ModelSource = "model_source";

    /// <summary>Dynamic model validation failure.</summary>
    public const string ModelValidation = "model_validation";

    /// <summary>Provider capability or dispatch failure.</summary>
    public const string Provider = "provider";

    /// <summary>Provider stream failure.</summary>
    public const string Stream = "stream";

    /// <summary>Credential or API-key auth failure.</summary>
    public const string Auth = "auth";

    /// <summary>OAuth refresh or derivation failure.</summary>
    public const string OAuth = "oauth";
}

/// <summary>Typed error raised by model and provider-auth operations.</summary>
[SuppressMessage("Design", "CA1710", Justification = "ModelsError is the public error name used by the upstream package.")]
public sealed class ModelsError : Exception
{
    /// <summary>Stable upstream error code.</summary>
    public string Code { get; }

    /// <summary>Creates a models error and retains the underlying exception as its inner cause.</summary>
    public ModelsError(string code, string message, object? cause = null)
        : base(WithCauseDetail(message, cause), cause as Exception)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(message);
        Code = code;
    }

    private static string WithCauseDetail(string message, object? cause)
    {
        if (cause is null)
        {
            return message;
        }

        var detail = DiagnosticUtilities.FormatThrownValue(cause).Trim();
        return string.IsNullOrEmpty(detail) || message.Contains(detail, StringComparison.Ordinal)
            ? message
            : $"{message}: {detail}";
    }
}

/// <summary>Provider-shaped input accepted by the shared auth resolver.</summary>
public sealed record AuthProviderDescriptor
{
    /// <summary>Provider identifier used for credential storage and error messages.</summary>
    public required string Id { get; init; }

    /// <summary>Provider auth implementations.</summary>
    public required ProviderAuth Auth { get; init; }
}

/// <summary>Shared provider authentication resolution routines.</summary>
public static class ProviderAuthResolver
{
    private const long _defaultOAuthMinimumValidityMs = 5 * 60 * 1000;
    private const int _defaultOAuthRefreshTimeoutMs = 15_000;

    /// <summary>
    /// Resolves stored or ambient provider authentication. A stored credential owns the provider;
    /// ambient values are consulted only when no credential is stored.
    /// </summary>
    public static Task<AuthResult?> ResolveAsync(
        AuthProviderDescriptor provider,
        CredentialStore credentials,
        AuthContext authContext,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(authContext);

        return ResolveWithSignalAsync(provider, credentials, authContext, overrides, CombineSignals(overrides?.Signal ?? default, cancellationToken));
    }

    private static async Task<AuthResult?> ResolveWithSignalAsync(
        AuthProviderDescriptor provider,
        CredentialStore credentials,
        AuthContext authContext,
        AuthResolutionOverrides? overrides,
        CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var requestAuthContext = overrides?.Environment is not null
            ? new OverlayAuthContext(authContext, overrides.Environment)
            : authContext;

        if (overrides?.ApiKey is not null && provider.Auth.ApiKey is not null)
        {
            return await ResolveApiKeyAsync(
                    requestAuthContext,
                    provider.Auth.ApiKey,
                    provider.Id,
                    new ApiKeyCredential { Key = overrides.ApiKey, Env = overrides.Environment },
                    signal)
                .ConfigureAwait(false);
        }

        var stored = await ReadCredentialAsync(credentials, provider.Id, signal).ConfigureAwait(false);
        if (stored is not null)
        {
            if (stored is OAuthCredential oauth && provider.Auth.OAuth is not null)
            {
                return await ResolveStoredOAuthAsync(
                        credentials,
                        provider.Id,
                        provider.Auth.OAuth,
                        oauth,
                        overrides?.MinOAuthValidityMs,
                        signal)
                    .ConfigureAwait(false);
            }

            if (stored is ApiKeyCredential apiKey && provider.Auth.ApiKey is not null)
            {
                var credential = overrides?.Environment is null
                    ? apiKey
                    : apiKey with { Env = MergeEnvironment(apiKey.Env, overrides.Environment) };
                return await ResolveApiKeyAsync(requestAuthContext, provider.Auth.ApiKey, provider.Id, credential, signal)
                    .ConfigureAwait(false);
            }

            return null;
        }

        return provider.Auth.ApiKey is null
            ? null
            : await ResolveApiKeyAsync(requestAuthContext, provider.Auth.ApiKey, provider.Id, null, signal)
                .ConfigureAwait(false);
    }

    private static async Task<AuthResult?> ResolveStoredOAuthAsync(
        CredentialStore credentials,
        string providerId,
        OAuthAuth oauth,
        OAuthCredential stored,
        long? minOAuthValidityMs,
        CancellationToken signal)
    {
        var minimumValidityMs = Math.Max(_defaultOAuthMinimumValidityMs, minOAuthValidityMs ?? 0);
        static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool ExpiresSoon(OAuthCredential credential) => NowMilliseconds() + minimumValidityMs >= credential.Expires;

        var credential = stored;
        if (ExpiresSoon(credential))
        {
            Credential? post;
            try
            {
                post = await credentials
                    .ModifyAsync(
                        providerId,
                        async current =>
                        {
                            if (current is not OAuthCredential currentOAuth || !ExpiresSoon(currentOAuth))
                            {
                                return null;
                            }

                            try
                            {
                                using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(signal);
                                refreshTimeout.CancelAfter(_defaultOAuthRefreshTimeoutMs);
                                return await oauth.Refresh(
                                        new OAuthRefreshInput { Credential = currentOAuth, Signal = refreshTimeout.Token })
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (signal.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception error)
                            {
                                throw new ModelsError(
                                    ModelsErrorCodes.OAuth,
                                    $"OAuth refresh failed for {providerId}",
                                    error);
                            }
                        },
                        signal)
                    .ConfigureAwait(false);
            }
            catch (ModelsError)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ModelsError(ModelsErrorCodes.Auth, $"Credential store modify failed for {providerId}", error);
            }

            if (post is not OAuthCredential refreshed)
            {
                return null;
            }

            credential = refreshed;
            if (minOAuthValidityMs is not null && ExpiresSoon(credential))
            {
                throw new ModelsError(
                    ModelsErrorCodes.OAuth,
                    $"OAuth refresh returned a token that expires too soon for {providerId}");
            }
        }

        try
        {
            return new AuthResult
            {
                Auth = await oauth.ToAuth(credential, signal).ConfigureAwait(false),
                Source = "OAuth",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCodes.OAuth, $"OAuth auth derivation failed for {providerId}", error);
        }
    }

    private static async Task<AuthResult?> ResolveApiKeyAsync(
        AuthContext authContext,
        ApiKeyAuth apiKey,
        string providerId,
        ApiKeyCredential? credential,
        CancellationToken signal)
    {
        try
        {
            return await apiKey.Resolve(
                    new ApiKeyAuthResolveInput
                    {
                        Context = authContext,
                        Credential = credential,
                        Signal = signal,
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCodes.Auth, $"API key auth failed for provider {providerId}", error);
        }
    }

    private static async Task<Credential?> ReadCredentialAsync(
        CredentialStore credentials,
        string providerId,
        CancellationToken signal)
    {
        try
        {
            return await credentials.ReadAsync(providerId, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCodes.Auth, $"Credential store read failed for {providerId}", error);
        }
    }

    private static ProviderEnvironment MergeEnvironment(
        ProviderEnvironment? current,
        ProviderEnvironment overrides)
    {
        var merged = new ProviderEnvironment();
        if (current is not null)
        {
            foreach (var pair in current)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in overrides)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static CancellationToken CombineSignals(CancellationToken first, CancellationToken second)
    {
        if (!first.CanBeCanceled)
        {
            return second;
        }

        if (!second.CanBeCanceled || first == second)
        {
            return first;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(first, second).Token;
    }

    private sealed class OverlayAuthContext(AuthContext baseContext, ProviderEnvironment environment) : AuthContext
    {
        public async Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            return await baseContext.EnvAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
            baseContext.FileExistsAsync(path, cancellationToken);
    }
}
