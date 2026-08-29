using System.Text.Json;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class AuthTests
{
    [Fact]
    public void Credential_polymorphism_preserves_discriminators_and_oauth_extensions()
    {
        Credential apiKey = new ApiKeyCredential
        {
            Key = "secret",
            Env = new ProviderEnvironment { ["ACCOUNT_ID"] = "account" },
        };
        var apiKeyJson = JsonSerializer.Serialize(apiKey);
        Assert.Contains("\"type\":\"api_key\"", apiKeyJson, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"secret\"", apiKeyJson, StringComparison.Ordinal);
        Assert.Contains("\"env\":{\"ACCOUNT_ID\":\"account\"}", apiKeyJson, StringComparison.Ordinal);

        var oauthJson = "{\"type\":\"oauth\",\"refresh\":\"r\",\"access\":\"a\",\"expires\":123,\"accountId\":\"acct\"}";
        var oauth = Assert.IsType<OAuthCredential>(JsonSerializer.Deserialize<Credential>(oauthJson));
        Assert.Equal("acct", oauth.AdditionalProperties["accountId"].GetString());
        Assert.Contains("\"accountId\":\"acct\"", JsonSerializer.Serialize<Credential>(oauth), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_memory_store_serializes_modifications_and_hides_secret_values_from_list()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var store = new InMemoryCredentialStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = store.ModifyAsync(
            "provider",
            async current =>
            {
                Assert.Null(current);
                entered.SetResult();
                await release.Task;
                return new ApiKeyCredential { Key = "first" };
            },
            testCancellation);
        await entered.Task;

        var second = store.ModifyAsync(
            "provider",
            current =>
            {
                Assert.Equal("first", Assert.IsType<ApiKeyCredential>(current).Key);
                return Task.FromResult<Credential?>(new ApiKeyCredential { Key = "second" });
            },
            testCancellation);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal("second", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("provider", testCancellation)).Key);
        Assert.Equal([new CredentialInfo("provider", CredentialTypes.ApiKey)], await store.ListAsync(testCancellation));
        await store.DeleteAsync("provider", testCancellation);
        Assert.Null(await store.ReadAsync("provider", testCancellation));
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_interleave_a_queued_provider_mutation()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var store = new InMemoryCredentialStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.ModifyAsync(
            "provider",
            async _ =>
            {
                entered.SetResult();
                await release.Task;
                return new ApiKeyCredential { Key = "first" };
            },
            testCancellation);
        await entered.Task;

        using var cancelled = new CancellationTokenSource();
        var second = store.ModifyAsync(
            "provider",
            _ => Task.FromResult<Credential?>(new ApiKeyCredential { Key = "second" }),
            cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        release.SetResult();
        await first;
        await Task.Delay(25, testCancellation);
        Assert.Equal("first", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("provider", testCancellation)).Key);
    }

    [Fact]
    public async Task Api_key_resolution_prefers_explicit_override_then_stored_then_ambient()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var context = new TestAuthContext(new Dictionary<string, string?> { ["API_KEY"] = "ambient" });
        var auth = new ApiKeyAuth
        {
            Name = "Test API key",
            Resolve = input => Task.FromResult<AuthResult?>(
                input.Credential is null
                    ? new AuthResult { Auth = new ModelAuth { ApiKey = "ambient" }, Source = "ambient" }
                    : new AuthResult
                    {
                        Auth = new ModelAuth { ApiKey = input.Credential.Key },
                        Env = input.Credential.Env,
                        Source = "stored",
                    }),
        };
        var provider = new AuthProviderDescriptor { Id = "test", Auth = new ProviderAuth { ApiKey = auth } };
        var store = new InMemoryCredentialStore();

        var ambient = await ProviderAuthResolver.ResolveAsync(provider, store, context, cancellationToken: testCancellation);
        Assert.Equal("ambient", ambient?.Auth.ApiKey);

        await store.ModifyAsync(
            "test",
            _ => Task.FromResult<Credential?>(new ApiKeyCredential { Key = "stored" }),
            testCancellation);
        var stored = await ProviderAuthResolver.ResolveAsync(provider, store, context, cancellationToken: testCancellation);
        Assert.Equal("stored", stored?.Auth.ApiKey);

        var overridden = await ProviderAuthResolver.ResolveAsync(
            provider,
            store,
            context,
            new AuthResolutionOverrides { ApiKey = "override" },
            testCancellation);
        Assert.Equal("override", overridden?.Auth.ApiKey);
    }

    [Fact]
    public async Task Stored_credential_does_not_fall_back_to_unmatched_auth_handler()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync(
            "oauth-only",
            _ => Task.FromResult<Credential?>(new ApiKeyCredential { Key = "stored" }),
            testCancellation);
        var provider = new AuthProviderDescriptor
        {
            Id = "oauth-only",
            Auth = new ProviderAuth
            {
                OAuth = new OAuthAuth
                {
                    Name = "OAuth",
                    Login = (_, _) => Task.FromResult(new OAuthCredential
                    {
                        Refresh = "r",
                        Access = "a",
                        Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                    }),
                    Refresh = input => Task.FromResult(input.Credential),
                    ToAuth = (_, _) => Task.FromResult(new ModelAuth { ApiKey = "oauth" }),
                },
            },
        };

        Assert.Null(await ProviderAuthResolver.ResolveAsync(
            provider,
            store,
            new TestAuthContext(new Dictionary<string, string?>()),
            cancellationToken: testCancellation));
    }

    [Fact]
    public async Task OAuth_refresh_is_double_checked_under_the_credential_store_lock()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var refreshCount = 0;
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync(
            "oauth",
            _ => Task.FromResult<Credential?>(new OAuthCredential
            {
                Refresh = "refresh",
                Access = "expired",
                Expires = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
            }),
            testCancellation);
        var provider = new AuthProviderDescriptor
        {
            Id = "oauth",
            Auth = new ProviderAuth
            {
                OAuth = new OAuthAuth
                {
                    Name = "OAuth",
                    Login = (_, _) => throw new InvalidOperationException("not used"),
                    Refresh = input =>
                    {
                        Interlocked.Increment(ref refreshCount);
                        return Task.FromResult(input.Credential with
                        {
                            Access = "fresh",
                            Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                        });
                    },
                    ToAuth = (credential, _) => Task.FromResult(new ModelAuth { ApiKey = credential.Access }),
                },
            },
        };

        var results = await Task.WhenAll(
            ProviderAuthResolver.ResolveAsync(
                provider,
                store,
                new TestAuthContext(new Dictionary<string, string?>()),
                cancellationToken: testCancellation),
            ProviderAuthResolver.ResolveAsync(
                provider,
                store,
                new TestAuthContext(new Dictionary<string, string?>()),
                cancellationToken: testCancellation));

        Assert.Equal(1, refreshCount);
        Assert.All(results, result => Assert.Equal("fresh", result?.Auth.ApiKey));
    }

    [Fact]
    public async Task OAuth_refresh_and_auth_derivation_errors_keep_models_error_code_and_message()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync(
            "oauth",
            _ => Task.FromResult<Credential?>(new OAuthCredential
            {
                Refresh = "refresh",
                Access = "expired",
                Expires = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
            }),
            testCancellation);
        var provider = new AuthProviderDescriptor
        {
            Id = "oauth",
            Auth = new ProviderAuth
            {
                OAuth = new OAuthAuth
                {
                    Name = "OAuth",
                    Login = (_, _) => throw new InvalidOperationException("not used"),
                    Refresh = _ => throw new InvalidOperationException("refresh failed"),
                    ToAuth = (_, _) => Task.FromResult(new ModelAuth { ApiKey = "never" }),
                },
            },
        };

        var error = await Assert.ThrowsAsync<ModelsError>(() => ProviderAuthResolver.ResolveAsync(
            provider,
            store,
            new TestAuthContext(new Dictionary<string, string?>()),
            cancellationToken: testCancellation));
        Assert.Equal(ModelsErrorCodes.OAuth, error.Code);
        Assert.Equal("OAuth refresh failed for oauth", error.Message);
    }

    [Fact]
    public async Task Environment_overrides_are_used_before_the_injected_context()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var observed = new List<string?>();
        var auth = new ApiKeyAuth
        {
            Name = "Test",
            Resolve = async input =>
            {
                observed.Add(await input.Context.EnvAsync("VALUE", input.Signal));
                return new AuthResult { Auth = new ModelAuth { ApiKey = "ok" } };
            },
        };
        var provider = new AuthProviderDescriptor { Id = "provider", Auth = new ProviderAuth { ApiKey = auth } };
        await ProviderAuthResolver.ResolveAsync(
            provider,
            new InMemoryCredentialStore(),
            new TestAuthContext(new Dictionary<string, string?> { ["VALUE"] = "base" }),
            new AuthResolutionOverrides { Environment = new ProviderEnvironment { ["VALUE"] = "override" } },
            testCancellation);

        Assert.Equal(["override"], observed);
    }

    private sealed class TestAuthContext(IReadOnlyDictionary<string, string?> values) : AuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.TryGetValue(name, out var value);
            return Task.FromResult(value);
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }
}
