using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionLaunchTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("own-duplicate")]
    public async Task LaunchAppendsOnlyItsHookToEffectiveProfileEnvironment(string profileMode)
    {
        const string hooksVariable = "DOTNET_STARTUP_HOOKS";
        const string inheritedVariable = "WPFSTUDIO_TEST_INHERITED_VALUE";
        var oldHooks = Environment.GetEnvironmentVariable(hooksVariable);
        var oldInherited = Environment.GetEnvironmentVariable(inheritedVariable);
        try
        {
            var ownHook = InspectionAgentUnderTest.HookPath;
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Inspection", "WpfStudio.Inspection.StartupHook.dll"), InspectionLaunch.FindStartupHook());
            Environment.SetEnvironmentVariable(hooksVariable, "Inherited.Hook.dll;Shared.Hook.dll");
            Environment.SetEnvironmentVariable(inheritedVariable, "inherited value");
            await using var session = new InspectionSession();
            var profile = new Dictionary<string, string>
            {
                ["WPFSTUDIO_TEST_PROFILE_VALUE"] = "profile value",
                [InspectionProtocol.TokenVariable] = "stale profile token"
            };
            string[] expected;
            if (profileMode == "replace")
            {
                profile[hooksVariable] = "Profile.Hook.dll";
                expected = ["Profile.Hook.dll", ownHook];
            }
            else if (profileMode == "clear") { profile[hooksVariable] = ""; expected = [ownHook]; }
            else if (profileMode == "own-duplicate")
            {
                profile[hooksVariable] = ownHook + ";" + ownHook.ToUpperInvariant();
                expected = [ownHook];
            }
            else expected = ["Inherited.Hook.dll", "Shared.Hook.dll", ownHook];
            var environment = InspectionAgentUnderTest.CreateEnvironment(profile, session);
            Assert.Equal(expected,
                environment[hooksVariable].Split(Path.PathSeparator));
            Assert.Equal("inherited value", environment[inheritedVariable]);
            Assert.Equal("profile value", environment["WPFSTUDIO_TEST_PROFILE_VALUE"]);
            Assert.Equal(session.Token, environment[InspectionProtocol.TokenVariable]);
            Assert.Equal(session.PipeName, environment[InspectionProtocol.PipeVariable]);
            Assert.Equal(Environment.ProcessId.ToString(), environment[InspectionProtocol.OwnerVariable]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hooksVariable, oldHooks);
            Environment.SetEnvironmentVariable(inheritedVariable, oldInherited);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectLaunchKeepsArgumentsAndProfileEnvironmentForDllAndApphost(bool appHost)
    {
        var program = Path.Combine(AppContext.BaseDirectory, "Fixtures", "net10.0-windows",
            "WpfStudio.InspectionFixture." + (appHost ? "exe" : "dll"));
        string[] arguments = ["directory with spaces", "quote\"literal", "trailing\\"];
        var start = InspectionLaunch.CreateStartInfo(new ResolvedLaunch(program, Path.GetDirectoryName(program)!, arguments,
            new Dictionary<string, string> { ["PROFILE_ONLY"] = "preserve", ["OVERRIDE"] = "old" }),
            new Dictionary<string, string> { ["OVERRIDE"] = "new" });
        Assert.Equal(appHost ? program : "dotnet", start.FileName);
        Assert.Equal(appHost ? arguments : new[] { program }.Concat(arguments), start.ArgumentList);
        Assert.Equal("preserve", start.Environment["PROFILE_ONLY"]);
        Assert.Equal("new", start.Environment["OVERRIDE"]);
        Assert.False(start.UseShellExecute);
    }
}
