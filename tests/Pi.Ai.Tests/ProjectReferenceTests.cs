using System.Reflection;

using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Verifies the project reference chain for this test project resolves at runtime.
/// A mis-wired scaffold fails here rather than surfacing as a confusing error in
/// the first ported test. Keep this: it is cheap and it guards the wiring.
/// </summary>
public sealed class ProjectReferenceTests
{
    [Fact]
    public void PiAi_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.Ai");
        Assert.Equal("Pi.Ai", assembly.GetName().Name);
    }

    [Fact]
    public void PiAiAbstractions_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.Ai.Abstractions");
        Assert.Equal("Pi.Ai.Abstractions", assembly.GetName().Name);
    }

    [Fact]
    public void PiAiTesting_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.Ai.Testing");
        Assert.Equal("Pi.Ai.Testing", assembly.GetName().Name);
    }
}
