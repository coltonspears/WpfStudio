using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

internal static class InspectionAgentUnderTest
{
    public static string HookPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST");
            var path = configured ?? Path.Combine(AppContext.BaseDirectory, "Inspection", "WpfStudio.Inspection.StartupHook.dll");
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException("WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST must name an absolute startup hook path.");
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path)!;
            foreach (var required in new[] { path, Path.Combine(directory, "WpfStudio.Inspection.Agent.dll"),
                         Path.Combine(directory, "WpfStudio.Inspection.Protocol.dll"), Path.Combine(directory, "WpfStudio.Wpf.PropertyEditing.dll"),
                         Path.Combine(directory, "WpfStudio.Wpf.Diagnostics.dll") })
                if (!File.Exists(required)) throw new FileNotFoundException("The selected inspection package is incomplete; tests will not fall back to development binaries.", required);
            return path;
        }
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironment(IReadOnlyDictionary<string, string> profile,
        InspectionSession session) => InspectionLaunch.CreateEnvironment(profile, session, HookPath);

    public static void AssertLoadedAgent(FixtureReady ready)
    {
        Assert.False(string.IsNullOrEmpty(ready.AgentPath), "The application did not report a loaded inspection agent.");
        var expected = Path.Combine(Path.GetDirectoryName(HookPath)!, "WpfStudio.Inspection.Agent.dll");
        Assert.Equal(expected, Path.GetFullPath(ready.AgentPath!), ignoreCase: true);
    }
}
