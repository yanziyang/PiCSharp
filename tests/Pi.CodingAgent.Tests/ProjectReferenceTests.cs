using System.Reflection;

using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Verifies the project reference chain for this test project resolves at runtime.
/// A mis-wired scaffold fails here rather than surfacing as a confusing error in
/// the first ported test. Keep this: it is cheap and it guards the wiring.
/// </summary>
public sealed class ProjectReferenceTests
{
    [Fact]
    public void PiCodingAgent_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.CodingAgent");
        Assert.Equal("Pi.CodingAgent", assembly.GetName().Name);
    }

    [Fact]
    public void PiAiTesting_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.Ai.Testing");
        Assert.Equal("Pi.Ai.Testing", assembly.GetName().Name);
    }
}
