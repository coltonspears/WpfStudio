using System.Runtime.CompilerServices;

namespace WpfStudio.Tests;

/// <summary>
/// Keeps test workspaces out of the saved project models of the person running the tests. Every fixture has its own
/// folder, so one run fills the cache's slots and evicts each real solution, whose next open is then a full MSBuild load.
/// Workers inherit the variable from the test process.
/// </summary>
internal static class IsolatedWorkspaceCache
{
    // A test assembly has no earlier place to run: a test's first load can come from any class, in any order.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Apply()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE")))
            Environment.SetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WpfStudio-TestWorkspaceCache"));
    }
}
