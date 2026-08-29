using System.Reflection;

using Xunit;

namespace Pi.Protocol.Tests;

/// <summary>
/// Verifies the project reference chain for this test project resolves at runtime.
/// A mis-wired scaffold fails here rather than surfacing as a confusing error in
/// the first ported test. Keep this: it is cheap and it guards the wiring.
/// </summary>
public sealed class ProjectReferenceTests
{
    [Fact]
    public void PiProtocol_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.Protocol");
        Assert.Equal("Pi.Protocol", assembly.GetName().Name);
    }
}
