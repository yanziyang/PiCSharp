using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pi.Ai;

/// <summary>Request authentication for a single model request.</summary>
public sealed record ModelAuth
{
    /// <summary>Resolved API key, when the provider uses one.</summary>
    [JsonPropertyName("apiKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; init; }

    /// <summary>Resolved request headers.</summary>
    [JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderHeaders? Headers { get; init; }

    /// <summary>Resolved provider endpoint.</summary>
    [JsonPropertyName("baseUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; init; }
}

/// <summary>Type discriminator values used by Pi's credential records.</summary>
public static class CredentialTypes
{
    /// <summary>API-key credential discriminator.</summary>
    public const string ApiKey = "api_key";

    /// <summary>OAuth credential discriminator.</summary>
    public const string OAuth = "oauth";
}

/// <summary>A stored, type-tagged provider credential.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApiKeyCredential), CredentialTypes.ApiKey)]
[JsonDerivedType(typeof(OAuthCredential), CredentialTypes.OAuth)]
public abstract record Credential
{
    /// <summary>Upstream credential discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>Stored API-key credential and provider-scoped environment values.</summary>
public sealed record ApiKeyCredential : Credential
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => CredentialTypes.ApiKey;

    /// <summary>Provider API key.</summary>
    [JsonPropertyName("key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    /// <summary>Provider-scoped environment/configuration values.</summary>
    [JsonPropertyName("env"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderEnvironment? Env { get; init; }
}

/// <summary>Stored OAuth token data with provider-specific JSON fields preserved.</summary>
public sealed record OAuthCredential : Credential
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => CredentialTypes.OAuth;

    /// <summary>OAuth refresh token.</summary>
    [JsonPropertyName("refresh")]
    public required string Refresh { get; init; }

    /// <summary>OAuth access token.</summary>
    [JsonPropertyName("access")]
    public required string Access { get; init; }

    /// <summary>Unix expiration timestamp in milliseconds.</summary>
    [JsonPropertyName("expires")]
    public long Expires { get; init; }

    /// <summary>
    /// Provider-specific OAuth fields. Json extension data keeps these fields at the credential
    /// object level instead of dropping them during an auth.json round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Non-secret credential metadata used by status and account displays.</summary>
public sealed record CredentialInfo(
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("type")] string Type);

/// <summary>Optional cancellation for public auth and credential operations.</summary>
public sealed record AuthOperationOptions
{
    /// <summary>Cancellation requested by the caller.</summary>
    [JsonIgnore]
    public CancellationToken Signal { get; init; }
}

/// <summary>Injectable environment and filesystem access used during auth resolution.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi auth contract name.")]
public interface AuthContext
{
    /// <summary>Reads a non-empty environment value.</summary>
    Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Checks whether a file or directory exists.</summary>
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Resolved request auth and its human-readable source label.</summary>
public sealed record AuthResult
{
    /// <summary>Resolved request authentication.</summary>
    public required ModelAuth Auth { get; init; }

    /// <summary>Provider-scoped values resolved along with the credential.</summary>
    public ProviderEnvironment? Env { get; init; }

    /// <summary>Status label such as an environment variable or OAuth.</summary>
    public string? Source { get; init; }
}

/// <summary>Successful provider auth availability check.</summary>
public sealed record AuthCheck
{
    /// <summary>Human-readable auth source.</summary>
    public string? Source { get; init; }

    /// <summary>Configured auth kind.</summary>
    public required string Type { get; init; }
}

/// <summary>Auth implementation kinds supported by the provider contract.</summary>
public static class AuthTypes
{
    /// <summary>API-key auth.</summary>
    public const string ApiKey = CredentialTypes.ApiKey;

    /// <summary>OAuth auth.</summary>
    public const string OAuth = CredentialTypes.OAuth;
}

/// <summary>One selectable option in an interactive auth prompt.</summary>
public sealed record AuthSelectOption
{
    /// <summary>Stable option identifier returned by the prompt.</summary>
    public required string Id { get; init; }

    /// <summary>Displayed option label.</summary>
    public required string Label { get; init; }

    /// <summary>Optional displayed description.</summary>
    public string? Description { get; init; }
}

/// <summary>Base for interactive auth prompts.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextAuthPrompt), "text")]
[JsonDerivedType(typeof(SecretAuthPrompt), "secret")]
[JsonDerivedType(typeof(SelectAuthPrompt), "select")]
[JsonDerivedType(typeof(ManualCodeAuthPrompt), "manual_code")]
public abstract record AuthPrompt
{
    /// <summary>Upstream prompt discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>Prompt text.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Optional input placeholder.</summary>
    [JsonPropertyName("placeholder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; init; }

    /// <summary>Per-prompt cancellation signal, not serialized.</summary>
    [JsonIgnore]
    public CancellationToken Signal { get; init; }
}

/// <summary>Plain-text auth prompt.</summary>
public sealed record TextAuthPrompt : AuthPrompt
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "text";
}

/// <summary>Secret-value auth prompt.</summary>
public sealed record SecretAuthPrompt : AuthPrompt
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "secret";
}

/// <summary>Selection auth prompt.</summary>
public sealed record SelectAuthPrompt : AuthPrompt
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "select";

    /// <summary>Selectable auth options.</summary>
    [JsonPropertyName("options")]
    public IReadOnlyList<AuthSelectOption> Options { get; init; } = [];
}

/// <summary>Manual-code auth prompt used when a callback cannot be received.</summary>
public sealed record ManualCodeAuthPrompt : AuthPrompt
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "manual_code";
}

/// <summary>Link displayed by an auth information event.</summary>
public sealed record AuthInfoLink
{
    /// <summary>Target URL.</summary>
    public required string Url { get; init; }

    /// <summary>Optional link label.</summary>
    public string? Label { get; init; }
}

/// <summary>Base for informational events emitted by auth flows.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AuthInfoEvent), "info")]
[JsonDerivedType(typeof(AuthUrlEvent), "auth_url")]
[JsonDerivedType(typeof(AuthDeviceCodeEvent), "device_code")]
[JsonDerivedType(typeof(AuthProgressEvent), "progress")]
public abstract record AuthEvent
{
    /// <summary>Upstream auth event discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>General informational auth event.</summary>
public sealed record AuthInfoEvent : AuthEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "info";

    /// <summary>Information text.</summary>
    public required string Message { get; init; }

    /// <summary>Optional related links.</summary>
    public IReadOnlyList<AuthInfoLink>? Links { get; init; }
}

/// <summary>Auth URL event.</summary>
public sealed record AuthUrlEvent : AuthEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "auth_url";

    /// <summary>URL the user should open.</summary>
    public required string Url { get; init; }

    /// <summary>Optional instructions.</summary>
    public string? Instructions { get; init; }
}

/// <summary>OAuth device-code event.</summary>
public sealed record AuthDeviceCodeEvent : AuthEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "device_code";

    /// <summary>Code entered by the user.</summary>
    public required string UserCode { get; init; }

    /// <summary>Verification URL.</summary>
    public required string VerificationUri { get; init; }

    /// <summary>Polling interval in seconds.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>Code lifetime in seconds.</summary>
    public int? ExpiresInSeconds { get; init; }
}

/// <summary>Auth progress event.</summary>
public sealed record AuthProgressEvent : AuthEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "progress";

    /// <summary>Progress text.</summary>
    public required string Message { get; init; }
}

/// <summary>Interactive callbacks shared by API-key and OAuth logins.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi auth contract name.")]
public interface AuthInteraction
{
    /// <summary>Cancellation for the whole interaction.</summary>
    CancellationToken Signal { get; }

    /// <summary>Prompts the user and returns text or a selected option identifier.</summary>
    Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Emits an informational auth event.</summary>
    void Notify(AuthEvent @event);
}

/// <summary>Normalized interaction passed to provider auth implementations.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi auth contract name.")]
public interface ProviderAuthInteraction : AuthInteraction
{
}

/// <summary>Input passed to an API-key availability check.</summary>
public sealed record ApiKeyAuthCheckInput
{
    /// <summary>Injectable auth environment.</summary>
    public required AuthContext Context { get; init; }

    /// <summary>Stored credential, when present.</summary>
    public ApiKeyCredential? Credential { get; init; }

    /// <summary>Operation cancellation.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>Input passed to API-key auth resolution.</summary>
public sealed record ApiKeyAuthResolveInput
{
    /// <summary>Injectable auth environment.</summary>
    public required AuthContext Context { get; init; }

    /// <summary>Stored credential, when present.</summary>
    public ApiKeyCredential? Credential { get; init; }

    /// <summary>Operation cancellation.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>API-key provider auth implementation.</summary>
public sealed class ApiKeyAuth
{
    /// <summary>Display name shown by login/status UI.</summary>
    public required string Name { get; init; }

    /// <summary>Optional interactive setup flow.</summary>
    public Func<ProviderAuthInteraction, CancellationToken, Task<ApiKeyCredential>>? Login { get; init; }

    /// <summary>Optional side-effect-free availability check.</summary>
    public Func<ApiKeyAuthCheckInput, Task<AuthCheck?>>? Check { get; init; }

    /// <summary>Resolves stored and ambient auth.</summary>
    public required Func<ApiKeyAuthResolveInput, Task<AuthResult?>> Resolve { get; init; }
}

/// <summary>Input passed to OAuth token refresh.</summary>
public sealed record OAuthRefreshInput
{
    /// <summary>Stored OAuth credential.</summary>
    public required OAuthCredential Credential { get; init; }

    /// <summary>Operation cancellation.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>OAuth provider auth implementation.</summary>
public sealed class OAuthAuth
{
    /// <summary>Display name shown by login/status UI.</summary>
    public required string Name { get; init; }

    /// <summary>Whether access is backed by a provider subscription.</summary>
    public bool IsSubscription { get; init; }

    /// <summary>Selector label for this login option.</summary>
    public string? LoginLabel { get; init; }

    /// <summary>Interactive OAuth login flow.</summary>
    public required Func<ProviderAuthInteraction, CancellationToken, Task<OAuthCredential>> Login { get; init; }

    /// <summary>Refreshes a stored OAuth credential.</summary>
    public required Func<OAuthRefreshInput, Task<OAuthCredential>> Refresh { get; init; }

    /// <summary>Derives request authentication from a valid OAuth credential.</summary>
    public required Func<OAuthCredential, CancellationToken, Task<ModelAuth>> ToAuth { get; init; }
}

/// <summary>Provider authentication implementations.</summary>
public sealed record ProviderAuth
{
    /// <summary>API-key auth implementation.</summary>
    public ApiKeyAuth? ApiKey { get; init; }

    /// <summary>OAuth auth implementation.</summary>
    public OAuthAuth? OAuth { get; init; }
}

/// <summary>Application-owned credential storage keyed by provider id.</summary>
[SuppressMessage("Naming", "CA1715", Justification = "Preserves the upstream Pi auth contract name.")]
public interface CredentialStore
{
    /// <summary>Reads a stored credential without resolving or refreshing it.</summary>
    Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Lists credential metadata without exposing secrets.</summary>
    Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a serialized read-modify-write for one provider.</summary>
    Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> updater,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a credential, serialized against modifications.</summary>
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>Overrides for one provider-auth resolution operation.</summary>
public sealed record AuthResolutionOverrides
{
    /// <summary>Explicit API key, taking precedence over stored/ambient values.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Per-request provider environment overrides.</summary>
    public ProviderEnvironment? Environment { get; init; }

    /// <summary>Required remaining OAuth validity; normal resolution defaults to five minutes.</summary>
    public long? MinOAuthValidityMs { get; init; }

    /// <summary>Operation cancellation.</summary>
    public CancellationToken Signal { get; init; }
}
