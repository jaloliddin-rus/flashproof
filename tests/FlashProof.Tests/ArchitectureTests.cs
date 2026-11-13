using System.Reflection;
using System.Runtime.Versioning;

namespace FlashProof.Tests;

// Design §4: FlashProof.Core must stay platform-independent so the macOS and Linux
// versions can reuse it. These tests fail if Windows or UI code leaks into it.
public class ArchitectureTests
{
    private static readonly Assembly Core = Assembly.Load("FlashProof.Core");

    [Fact]
    public void Core_does_not_reference_platform_or_UI_assemblies()
    {
        var names = Core.GetReferencedAssemblies().Select(a => a.Name ?? "");

        Assert.DoesNotContain(names, n => n.StartsWith("FlashProof.Windows") || n.StartsWith("Avalonia"));
    }

    [Fact]
    public void Core_is_not_marked_as_Windows_only()
    {
        Assert.Null(Core.GetCustomAttribute<SupportedOSPlatformAttribute>());
    }
}
