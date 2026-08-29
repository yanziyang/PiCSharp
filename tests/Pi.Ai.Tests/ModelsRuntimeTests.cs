using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class ModelsRuntimeTests
{
    [Fact]
    public void Calculates_request_wide_tiered_costs_and_long_cache_writes()
    {
        var model = TestModel("openai", "gpt-5.6-sol") with
        {
            Cost = new ModelCost
            {
                Input = 5,
                Output = 30,
                CacheRead = 0.5,
                CacheWrite = 6.25,
                Tiers =
                [
                    new ModelCostTier
                    {
                        InputTokensAbove = 272_000,
                        Input = 10,
                        Output = 45,
                        CacheRead = 1,
                        CacheWrite = 12.5,
                    },
                ],
            },
        };
        var shortUsage = Usage(200_000, 100_000, 72_000, 0);
        var shortCost = ModelUtilities.CalculateCost(model, shortUsage);
        Assert.Equal(1, shortCost.Input);
        Assert.Equal(3, shortCost.Output);
        Assert.Equal(0.036, shortCost.CacheRead);
        Assert.Equal(0, shortCost.CacheWrite);

        var longCost = ModelUtilities.CalculateCost(model, Usage(200_000, 100_000, 72_000, 1));
        Assert.Equal(2, longCost.Input);
        Assert.Equal(4.5, longCost.Output);
        Assert.Equal(0.072, longCost.CacheRead);
        Assert.Equal(0.0000125, longCost.CacheWrite);
    }

    [Fact]
    public void Registers_replaces_deletes_and_lists_providers_and_models()
    {
        var models = ModelsFactory.CreateModels();
        models.SetProvider(TestProvider("p1"));
        models.SetProvider(TestProvider("p2"));
        Assert.Equal(["p1", "p2"], models.GetProviders().Select(provider => provider.Id));

        var replacement = TestProvider("p1");
        models.SetProvider(replacement);
        Assert.Same(replacement, models.GetProvider("p1"));
        Assert.Equal(2, models.GetProviders().Count);

        models.DeleteProvider("p1");
        Assert.Null(models.GetProvider("p1"));
        models.ClearProviders();
        Assert.Empty(models.GetProviders());
    }

    [Fact]
    public void Lists_models_per_provider_and_uses_exact_api_matching()
    {
        var models = ModelsFactory.CreateModels();
        models.SetProvider(TestProvider("p1", [TestModel("p1", "m1"), TestModel("p1", "m2")]));
        models.SetProvider(TestProvider("p2", [TestModel("p2", "m3")]));

        Assert.Equal(["m1", "m2", "m3"], models.GetModels().Select(model => model.Id));
        Assert.Equal(["m1", "m2"], models.GetModels("p1").Select(model => model.Id));
        Assert.Empty(models.GetModels("nope"));
        Assert.Equal("m3", models.GetModel("p2", "m3")?.Id);
        Assert.Null(models.GetModel("p2", "missing"));
        var found = models.GetModel("p2", "m3");
        Assert.False(found is not null && ModelUtilities.HasApi(found, ApiNames.OpenAiCompletions));
        Assert.True(found is not null && ModelUtilities.HasApi(found, "test-api"));
    }

    [Fact]
    public void Swallows_provider_catalog_failures_when_listing_models()
    {
        var streams = new TestStreams((model, _, _) => DoneStream(model));
        var models = ModelsFactory.CreateModels();
        models.SetProvider(new Provider(
            "broken",
            "broken",
            _ambientAuth,
            () => throw new InvalidOperationException("boom"),
            streams));
        models.SetProvider(TestProvider("ok", [TestModel("ok", "m1")]));

        Assert.Equal(["m1"], models.GetModels().Select(model => model.Id));
        Assert.Empty(models.GetModels("broken"));
        Assert.Throws<InvalidOperationException>(() => models.GetProvider("broken")?.GetModels());
    }

    [Fact]
    public async Task Refreshes_dynamic_catalogs_and_reports_provider_failures()
    {
        var current = new[] { TestModel("dynamic", "before") };
        var refreshes = 0;
        var models = ModelsFactory.CreateModels();
        models.SetProvider(TestProvider(
            "dynamic",
            current,
            getModels: () => current,
            refresh: context =>
            {
                if (!context.AllowNetwork)
                {
                    return Task.CompletedTask;
                }

                refreshes++;
                return context.Publish(new ModelsPublication
                {
                    Update = () => current = [TestModel("dynamic", "after")],
                });
            }));
        models.SetProvider(TestProvider(
            "static",
            [TestModel("static", "s1")]));

        var first = await models.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(first.Errors);
        Assert.Equal(1, refreshes);
        Assert.NotNull(models.GetModel("dynamic", "after"));
        Assert.Null(models.GetModel("dynamic", "before"));

        models.SetProvider(TestProvider(
            "flaky",
            refresh: context => context.AllowNetwork
                ? Task.FromException(new InvalidOperationException("fetch failed"))
                : Task.CompletedTask));
        var second = await models.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("fetch failed", second.Errors["flaky"].Message);
    }

    [Fact]
    public async Task Restricts_refresh_to_selected_providers_and_runs_cache_then_network_phases()
    {
        var calls = new List<string>();
        var models = ModelsFactory.CreateModels();
        foreach (var id in new[] { "one", "two" })
        {
            models.SetProvider(TestProvider(
                id,
                refresh: context =>
                {
                    calls.Add($"{id}:{(context.AllowNetwork ? "network" : "cache")}");
                    return Task.CompletedTask;
                }));
        }

        var result = await models.RefreshAsync(
            new ModelsRefreshOptions { Providers = ["two", "unknown"] },
            TestContext.Current.CancellationToken);
        Assert.Empty(result.Errors);
        Assert.Equal(["two:cache", "two:network"], calls);
    }

    [Fact]
    public async Task Persists_dynamic_catalogs_and_restores_them_without_network_access()
    {
        var credentials = new InMemoryCredentialStore();
        var store = new InMemoryModelsStore();
        await credentials.ModifyAsync(
            "dynamic",
            _ => Task.FromResult<Credential?>(new ApiKeyCredential { Key = "key" }),
            TestContext.Current.CancellationToken);
        var online = ModelsFactory.CreateModels(new CreateModelsOptions
        {
            Credentials = credentials,
            ModelsStore = store,
        });
        online.SetProvider(CreateDynamicProvider(_ =>
            Task.FromResult<IReadOnlyList<Model>>([TestModel("dynamic", "fetched")])));
        Assert.Empty((await online.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Errors);

        var offline = ModelsFactory.CreateModels(new CreateModelsOptions
        {
            Credentials = credentials,
            ModelsStore = store,
        });
        offline.SetProvider(CreateDynamicProvider(_ =>
            Task.FromException<IReadOnlyList<Model>>(new InvalidOperationException("must not fetch"))));
        var result = await offline.RefreshAsync(
            new ModelsRefreshOptions { AllowNetwork = false },
            TestContext.Current.CancellationToken);
        Assert.Empty(result.Errors);
        Assert.NotNull(offline.GetModel("dynamic", "fetched"));
    }

    [Fact]
    public async Task Passes_effective_api_key_and_force_flag_while_skipping_unconfigured_dynamic_providers()
    {
        Credential? effective = null;
        bool? force = null;
        var unconfiguredRefreshes = 0;
        var models = ModelsFactory.CreateModels(new CreateModelsOptions
        {
            AuthContext = new TestAuthContext(new Dictionary<string, string?>()),
        });
        models.SetProvider(TestProvider(
            "configured",
            auth: EnvKeyAuth("ambient-key"),
            refresh: context =>
            {
                if (context.AllowNetwork)
                {
                    effective = context.Credential;
                    force = context.Force;
                }

                return Task.CompletedTask;
            }));
        models.SetProvider(TestProvider(
            "unconfigured",
            auth: EnvKeyAuth(null),
            refresh: context =>
            {
                if (context.AllowNetwork)
                {
                    unconfiguredRefreshes++;
                }

                return Task.CompletedTask;
            }));

        await models.RefreshAsync(new ModelsRefreshOptions { Force = true }, TestContext.Current.CancellationToken);
        var configured = Assert.IsType<ApiKeyCredential>(effective);
        Assert.Equal("ambient-key", configured.Key);
        Assert.True(force);
        Assert.Equal(0, unconfiguredRefreshes);
    }

    [Fact]
    public async Task Resolves_model_auth_and_merges_request_headers_and_environment()
    {
        var calls = new List<(Model Model, StreamOptions? Options)>();
        var apiKey = new ApiKeyAuth
        {
            Name = "Scoped",
            Resolve = input =>
            {
                var account = input.Credential?.Env?.GetValueOrDefault("ACCOUNT_ID");
                return Task.FromResult<AuthResult?>(
                    input.Credential?.Key is null || account is null
                        ? null
                        : new AuthResult
                        {
                            Auth = new ModelAuth
                            {
                                ApiKey = input.Credential.Key,
                                BaseUrl = $"https://example.test/{account}",
                            },
                            Env = new ProviderEnvironment { ["ACCOUNT_ID"] = account },
                        });
            },
        };
        var models = ModelsFactory.CreateModels();
        models.SetProvider(TestProvider("p1", auth: new ProviderAuth { ApiKey = apiKey }, calls: calls));
        var model = TestModel("p1", "model-a");
        var context = CreateContext();

        var result = await models.CompleteSimpleAsync(
            model,
            context,
            new ModelsSimpleStreamOptions
            {
                ApiKey = "explicit-key",
                Environment = new Dictionary<string, string> { ["ACCOUNT_ID"] = "acct" },
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(StopReasons.Stop, result.StopReason);
        Assert.Equal("https://example.test/acct", calls[0].Model.BaseUrl);
        Assert.Equal("explicit-key", calls[0].Options?.ApiKey);
        Assert.Equal("acct", calls[0].Options?.Environment?["ACCOUNT_ID"]);
    }

    [Fact]
    public async Task Explicit_headers_override_auth_case_insensitively_and_transform_once()
    {
        var calls = new List<(Model Model, StreamOptions? Options)>();
        var models = ModelsFactory.CreateModels();
        models.SetProvider(TestProvider(
            "p1",
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "Test",
                    Resolve = _ => Task.FromResult<AuthResult?>(new AuthResult
                    {
                        Auth = new ModelAuth
                        {
                            ApiKey = "resolved-key",
                            Headers = new ProviderHeaders
                            {
                                ["Authorization"] = "Bearer resolved-key",
                                ["x-a"] = "auth",
                                ["x-b"] = "auth",
                            },
                            BaseUrl = "https://auth.test/v1",
                        },
                    }),
                },
            },
            calls: calls));
        var transforms = 0;
        var model = TestModel("p1", "model-a");
        await models.CompleteSimpleAsync(
            model,
            CreateContext(),
            new ModelsSimpleStreamOptions
            {
                ApiKey = "explicit-key",
                Headers = new Dictionary<string, string?>
                {
                    ["authorization"] = "Explicit token",
                    ["x-b"] = "explicit",
                },
                TransformHeaders = headers =>
                {
                    transforms++;
                    Assert.Equal("Explicit token", headers["authorization"]);
                    Assert.Equal("auth", headers["x-a"]);
                    Assert.Equal("explicit", headers["x-b"]);
                    headers["x-transformed"] = "yes";
                    return Task.FromResult(headers);
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, transforms);
        Assert.Equal("explicit-key", calls[0].Options?.ApiKey);
        Assert.Equal("https://auth.test/v1", calls[0].Model.BaseUrl);
        Assert.Equal("yes", calls[0].Options?.Headers?["x-transformed"]);

        await models.CompleteSimpleAsync(
            model,
            CreateContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("resolved-key", calls[1].Options?.ApiKey);
    }

    [Fact]
    public async Task Unknown_provider_is_a_lazy_terminal_error_stream()
    {
        var models = ModelsFactory.CreateModels();
        var result = await models.CompleteSimpleAsync(
            TestModel("ghost", "model-a"),
            CreateContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StopReasons.Error, result.StopReason);
        Assert.Contains("Unknown provider: ghost", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streams_provider_events_in_order_and_reports_deferred_capability_errors()
    {
        var models = ModelsFactory.CreateModels();
        models.SetProvider(TestProvider("p1"));
        var stream = models.StreamSimple(TestModel("p1", "model-a"), CreateContext());
        var events = new List<string>();
        await foreach (var @event in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(@event.Type);
        }

        Assert.Equal(["start", "done"], events);
        Assert.Equal(StopReasons.Stop, (await stream.Result).StopReason);

        var deferred = await models.FetchDeferredAsync(
            TestModel("p1", "model-a"),
            new DeferredHandle { Provider = "p1", ModelId = "model-a", Api = "test-api", Id = "h1" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StopReasons.Error, deferred.StopReason);
        Assert.Contains("does not support deferred responses", deferred.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_late_publication_from_a_superseded_refresh_generation()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var state = "initial";
        var store = new InMemoryModelsStore();
        var models = ModelsFactory.CreateModels(new CreateModelsOptions { ModelsStore = store });
        models.SetProvider(TestProvider(
            "dynamic",
            refresh: async context =>
            {
                if (!context.AllowNetwork)
                {
                    return;
                }

                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.SetResult();
                    await finishFirst.Task;
                }

                var value = $"generation-{call}";
                await context.Publish(new ModelsPublication
                {
                    PersistSpecified = true,
                    Persist = new ModelsStoreEntry { Models = [TestModel("dynamic", value)] },
                    Update = () => state = value,
                });
            }));

        var first = models.RefreshAsync(new ModelsRefreshOptions { Providers = ["dynamic"] }, TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = models.RefreshAsync(new ModelsRefreshOptions { Providers = ["dynamic"] }, TestContext.Current.CancellationToken);
        await second;
        finishFirst.SetResult();
        await first;

        Assert.Equal("generation-2", state);
        Assert.Equal("generation-2", (await store.ReadAsync("dynamic", TestContext.Current.CancellationToken))?.Models[0].Id);
    }

    private static Provider CreateDynamicProvider(
        Func<RefreshModelsContext, Task<IReadOnlyList<Model>>> fetchModels)
    {
        return ProviderFactory.CreateProvider(new CreateProviderOptions
        {
            Id = "dynamic",
            Auth = EnvKeyAuth("key"),
            FetchModels = fetchModels,
            Api = new TestStreams((model, _, _) => DoneStream(model)),
        });
    }

    private static Provider TestProvider(
        string id,
        IReadOnlyList<Model>? models = null,
        ProviderAuth? auth = null,
        Func<IReadOnlyList<Model>>? getModels = null,
        Func<RefreshModelsContext, Task>? refresh = null,
        List<(Model Model, StreamOptions? Options)>? calls = null)
    {
        var catalog = models ?? [TestModel(id, "model-a")];
        var streams = new TestStreams((model, _, options) =>
        {
            calls?.Add((model, options));
            return DoneStream(model);
        });
        return new Provider(
            id,
            id,
            auth ?? _ambientAuth,
            getModels ?? (() => catalog),
            streams,
            refreshModels: refresh);
    }

    private static ProviderAuth EnvKeyAuth(string? key)
    {
        return new ProviderAuth
        {
            ApiKey = new ApiKeyAuth
            {
                Name = "Test API key",
                Resolve = input => Task.FromResult<AuthResult?>(
                    key is null && input.Credential?.Key is null
                        ? null
                        : new AuthResult
                        {
                            Auth = new ModelAuth { ApiKey = input.Credential?.Key ?? key },
                            Source = input.Credential is null ? "env" : "stored",
                        }),
            },
        };
    }

    private static readonly ProviderAuth _ambientAuth = new()
    {
        ApiKey = new ApiKeyAuth
        {
            Name = "Ambient",
            Resolve = _ => Task.FromResult<AuthResult?>(new AuthResult { Auth = new ModelAuth() }),
        },
    };

    private static Model TestModel(string provider, string id) => new()
    {
        Id = id,
        Name = id,
        Api = "test-api",
        Provider = provider,
        BaseUrl = "https://example.test/v1",
        Input = ["text"],
        Cost = new ModelCost(),
        ContextWindow = 10_000,
        MaxTokens = 1_000,
    };

    private static Usage Usage(int input, int output, int cacheRead, int cacheWrite) => new()
    {
        Input = input,
        Output = output,
        CacheRead = cacheRead,
        CacheWrite = cacheWrite,
        TotalTokens = input + output + cacheRead + cacheWrite,
    };

    private static Context CreateContext() => new()
    {
        Messages = [UserMessage.Text("hi", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
    };

    private static AssistantMessageEventStream DoneStream(Model model)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = [new TextContent("ok")],
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReasons.Stop,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        stream.Push(new StreamStartEvent(message));
        stream.Push(new StreamDoneEvent(StopReasons.Stop, message));
        stream.End(message);
        return stream;
    }

    private sealed class TestStreams(
        Func<Model, Context, StreamOptions?, AssistantMessageEventStream> respond) : ProviderStreams
    {
        public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
            respond(model, context, options);

        public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
            respond(model, context, options);
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
