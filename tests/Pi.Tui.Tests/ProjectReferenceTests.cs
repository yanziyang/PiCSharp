using System.Reflection;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Verifies the project reference chain for this test project resolves at runtime.
/// A mis-wired scaffold fails here rather than surfacing as a confusing error in
/// the first ported test. Keep this: it is cheap and it guards the wiring.
/// </summary>
public sealed class ProjectReferenceTests
{
    [Fact]
    public void PiTui_IsReferencedAndLoadable()
    {
        Assembly assembly = Assembly.Load("Pi.Tui");
        Assert.Equal("Pi.Tui", assembly.GetName().Name);
    }
}
