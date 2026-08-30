using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;
using Pi.AgentCore.Harness.Session.Testing;
using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

public sealed class ConformanceTests
{
    [Fact(DisplayName = "InMemorySessionRepo conformance")]
    public Task InMemorySessionRepoConformance() =>
        SessionBackendConformance.RunAllAsync(() => Task.FromResult<ISessionBackendFixture<SessionMetadata>>(new InMemorySessionBackendFixture()));

    [Fact(DisplayName = "JsonlSessionRepo conformance")]
    public Task JsonlSessionRepoConformance() =>
        SessionBackendConformance.RunAllAsync(() => Task.FromResult<ISessionBackendFixture<JsonlSessionMetadata>>(new JsonlSessionBackendFixture()));
}
