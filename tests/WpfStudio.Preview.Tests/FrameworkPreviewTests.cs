using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using WpfStudio.Contracts;
using WpfStudio.Hosting;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

/// <summary>Real .NET Framework builds, through the real client and the real .NET Framework hosts it chooses for them.</summary>
public sealed class FrameworkPreviewTests
{
    private const string Presentation = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
    private const string Controls = "clr-namespace:WpfStudio.PreviewFrameworkControls;assembly=WpfStudio.PreviewFrameworkControls";
    private const string ProbeXaml = $"<UserControl {Presentation} xmlns:c='{Controls}'><c:ProbeControl/></UserControl>";
    private static readonly string Output = Path.Combine(AppContext.BaseDirectory, "FrameworkFixture");
    private static readonly string Output32 = Path.Combine(AppContext.BaseDirectory, "FrameworkFixture32");
    private static readonly string Application = Path.Combine(Output, "WpfStudio.PreviewFrameworkFixture.exe");
    private static readonly string Application32 = Path.Combine(Output32, "WpfStudio.PreviewFrameworkFixture32.exe");
    private static readonly string Library = Path.Combine(Output, "WpfStudio.PreviewFrameworkControls.dll");

    private static string FrameworkHost => WorkerHostLocator.Find("WpfStudio.PreviewHost.Framework", "PreviewHost.Framework", "net48",
        [typeof(FrameworkPreviewTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration], [".exe"], []);

    private static PreviewRequest Source(string assembly, string xaml = ProbeXaml, long version = 1) =>
        new("C:/project/View.xaml", xaml, version, 400, 300, assembly, Path.GetDirectoryName(assembly));

    private static async Task<Dictionary<string, string>> ReadProbeAsync(PreviewClient client, PreviewSnapshot snapshot)
    {
        var probe = snapshot.Nodes.First(node => node.Type.EndsWith(".ProbeControl", StringComparison.Ordinal));
        var inspection = await client.InspectAsync(new(snapshot.Version, probe.Id));
        string text = inspection.Properties.First(property => property.Name == "Text").Value;
        return text.Split('|').Select(fact => fact.Split(['='], 2)).ToDictionary(pair => pair[0], pair => pair[1]);
    }

    [Fact]
    public void TheBuiltAssemblyChoosesItsHost()
    {
        Assert.Equal(PreviewRuntime.NetFramework, PreviewHostSelection.Select(Application));
        Assert.Equal(PreviewRuntime.NetFramework, PreviewHostSelection.Select(Library));
        Assert.Equal(PreviewRuntime.NetFramework32, PreviewHostSelection.Select(Application32));
        Assert.Equal(PreviewRuntime.DotNet, PreviewHostSelection.Select(typeof(FrameworkPreviewTests).Assembly.Location));
        Assert.Equal(PreviewRuntime.DotNet, PreviewHostSelection.Select(Path.Combine(Output, "missing.dll")));
        Assert.Equal(PreviewRuntime.DotNet, PreviewHostSelection.Select(null));
        // Not an assembly at all, such as a native apphost.
        Assert.Equal(PreviewRuntime.DotNet, PreviewHostSelection.Select(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
    }

    [Fact]
    public async Task ProjectCodeRunsAsItWouldInItsOwnProcess()
    {
        await using var client = new PreviewClient();
        var snapshot = await client.RenderAsync(Source(Application));

        Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var facts = await ReadProbeAsync(client, snapshot);
        Assert.StartsWith(".NET Framework", facts["runtime"], StringComparison.Ordinal);
        Assert.Equal("64", facts["bits"]);
        // The call that fails on the .NET host, whatever it returns.
        Assert.Contains("PreviewAppDomainManager", facts["manager"], StringComparison.Ordinal);
        Assert.Contains(Path.GetTempPath(), facts["base"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("from-app-config", facts["config"]);
        Assert.Equal("WpfStudio.PreviewFrameworkFixture", facts["entry"]);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Information" && diagnostic.Message.Contains(".NET Framework", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheProjectsLibrariesNeverCompeteWithTheHosts()
    {
        await using var client = new PreviewClient();
        var snapshot = await client.RenderAsync(Source(Application));
        Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var facts = await ReadProbeAsync(client, snapshot);
        Assert.Empty(facts["transport"]);
        // The project's older copy, while the engine answering these requests binds its own at the same time.
        Assert.StartsWith("12.0.0.0", facts["json"], StringComparison.Ordinal);
        Assert.EndsWith("{\"a\":1}", facts["json"], StringComparison.Ordinal);
    }

    /// <summary>The library was compiled against 12.0.0.0, the output has 13.0.0.0, and nothing redirects it. Each reference still gets the build's copy.</summary>
    [Fact]
    public async Task AVersionNoRedirectCoversStillBindsToTheBuildsCopy()
    {
        string project = Directory.CreateTempSubdirectory("WpfStudio.FrameworkRedirect.").FullName;
        CopyDirectory(Output, project);
        try
        {
            File.Copy(Path.Combine(Path.GetDirectoryName(FrameworkHost)!, "Newtonsoft.Json.dll"), Path.Combine(project, "Newtonsoft.Json.dll"), overwrite: true);
            await using var client = new PreviewClient();
            var snapshot = await client.RenderAsync(Source(Path.Combine(project, Path.GetFileName(Application))));

            Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.StartsWith("13.0.0.0", (await ReadProbeAsync(client, snapshot))["json"], StringComparison.Ordinal);
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    [Fact]
    public async Task CompiledPreviewLoadsAnApplicationWithItsResourcesWithoutStartingIt()
    {
        await using var client = new PreviewClient();
        var snapshot = await client.RenderAsync(new("C:/project/FixtureView.xaml", "<InvalidUnfinishedBuffer", 1, 400, 300, Application, Output,
            PreviewMode.Compiled, "WpfStudio.PreviewFrameworkFixture.FixtureView"));

        Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(Application, snapshot.Build!.AssemblyPath, ignoreCase: true);
        Assert.Equal("WpfStudio.PreviewFrameworkFixture.FixtureView", snapshot.Build.ViewTypeName);
        var greeting = snapshot.Nodes.First(node => node.Name == "Greeting");
        var inspection = await client.InspectAsync(new(snapshot.Version, greeting.Id));
        Assert.Contains(inspection.Properties, property => property.Name == "Foreground" && property.Value == "#FF008080");
    }

    [Fact]
    public async Task APrefer32BitApplicationPreviewsInA32BitProcess()
    {
        await using var client = new PreviewClient();
        var snapshot = await client.RenderAsync(Source(Application32));

        Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal("32", (await ReadProbeAsync(client, snapshot))["bits"]);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("32-bit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectionAndEditsReachTheEngineAcrossTheDomainBoundary()
    {
        await using var client = new PreviewClient();
        const string xaml = $"<Grid {Presentation} Background='White'><TextBlock x:Name='Target' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Text='Original' Margin='20'/></Grid>";
        var snapshot = await client.RenderAsync(Source(Application, xaml));
        Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var target = snapshot.Nodes.First(node => node.Name == "Target");

        var picked = await client.PickAsync(new(snapshot.Version, target.Bounds!.X + 2, target.Bounds.Y + 2));
        Assert.Equal(target.Id, picked.Node?.Id);

        var inspection = await client.InspectAsync(new(snapshot.Version, target.Id));
        var text = inspection.Properties.First(property => property.Name == "Text");
        Assert.Equal("Original", text.Value);
        Assert.True((await client.ValidatePropertyAsync(new(snapshot.Version, target.Id, "Text", "Changed", OwnerType: text.OwnerType, OwnerAssembly: text.OwnerAssembly))).Success);

        var edit = await client.SetPropertyAsync(new(snapshot.Version, target.Id, "Text", "Changed", OwnerType: text.OwnerType, OwnerAssembly: text.OwnerAssembly));
        Assert.True(edit.Success, edit.Error);
        Assert.Equal("Changed", edit.Inspection.Properties.First(property => property.Name == "Text").Value);
        Assert.NotNull(edit.Snapshot.PngBytes);

        var reset = await client.SetPropertyAsync(new(snapshot.Version, target.Id, "Text", null, Reset: true, OwnerType: text.OwnerType, OwnerAssembly: text.OwnerAssembly));
        Assert.Equal("Original", reset.Inspection.Properties.First(property => property.Name == "Text").Value);

        var captured = await client.CaptureAsync(new(snapshot.Version));
        Assert.True(captured.Success);
        Assert.NotNull(captured.PngBytes);
    }

    [Fact]
    public async Task BindingsAreReadWithoutTheEventOnlyDotNetHas()
    {
        await using var client = new PreviewClient();
        const string xaml = "<Grid " + Presentation + " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:s='clr-namespace:System;assembly=mscorlib'>" +
            "<Grid.DataContext><x:Static Member='s:Environment.NewLine'/></Grid.DataContext><TextBlock x:Name='Target' Text='{Binding NoSuchMember}'/></Grid>";
        var snapshot = await client.RenderAsync(Source(Application, xaml));
        Assert.True(snapshot.Success, string.Join(Environment.NewLine, snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("NoSuchMember", StringComparison.Ordinal));

        var inspection = await client.InspectAsync(new(snapshot.Version, snapshot.Nodes.First(node => node.Name == "Target").Id));
        var text = inspection.Properties.First(property => property.Name == "Text");
        Assert.Equal("NoSuchMember", text.BindingPath);
        Assert.False(string.IsNullOrEmpty(text.BindingStatus));
    }

    [Fact]
    public async Task EditingSourceReusesTheHostAndARebuildReplacesIt()
    {
        string project = Path.Combine(Path.GetTempPath(), "WpfStudio.FrameworkPreviewTest." + Guid.NewGuid().ToString("N"));
        CopyDirectory(Output, project);
        try
        {
            string assembly = Path.Combine(project, Path.GetFileName(Application));
            await using var client = new PreviewClient();
            Assert.True((await client.RenderAsync(Source(assembly))).Success);
            int? first = client.ProcessId;

            Assert.True((await client.RenderAsync(Source(assembly, $"<Grid {Presentation}/>", 2))).Success);
            Assert.Equal(first, client.ProcessId);

            File.SetLastWriteTimeUtc(assembly, DateTime.UtcNow.AddMinutes(1));
            Assert.True((await client.RenderAsync(Source(assembly, version: 3))).Success);
            Assert.NotEqual(first, client.ProcessId);
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    [Fact]
    public async Task ChangingFromAFrameworkProjectToAModernOneChangesHost()
    {
        await using var client = new PreviewClient();
        Assert.True((await client.RenderAsync(Source(Application))).Success);
        int? framework = client.ProcessId;

        var loose = await client.RenderAsync(new("C:/project/View.xaml", $"<Grid {Presentation}/>", 2, 400, 300));
        Assert.True(loose.Success);
        Assert.NotEqual(framework, client.ProcessId);
        Assert.DoesNotContain(loose.Diagnostics, diagnostic => diagnostic.Message.Contains(".NET Framework", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingAssemblyIsReportedInsteadOfEndingTheHost()
    {
        await using var client = new PreviewClient();
        var failed = await client.RenderAsync(Source(Application, $"<Grid {Presentation} xmlns:m='clr-namespace:Nope;assembly=No.Such.Assembly'><m:Thing/></Grid>"));

        Assert.False(failed.Success);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Message.Contains("No.Such.Assembly", StringComparison.Ordinal));
        Assert.True((await client.RenderAsync(Source(Application, version: 2))).Success);
    }

    [Fact]
    public async Task TheHostEndsWithItsClient()
    {
        var client = new PreviewClient();
        Assert.True((await client.RenderAsync(Source(Application))).Success);
        using var process = Process.GetProcessById(client.ProcessId!.Value);

        await client.DisposeAsync();
        Assert.True(process.WaitForExit(10_000));
    }

    [Fact]
    public async Task NativeInteractionAttachesEditsAndDetachesTheSameView()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync(FrameworkHost);
        const string xaml = $"<Grid {Presentation} xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><TextBox x:Name='Input' Text='Original'/></Grid>";
        var first = await host.Rpc.RenderAsync(Source(Application, xaml), default);
        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var identity = Assert.IsType<PreviewSurfaceIdentity>(first.Surface);
        Assert.Equal(host.SessionId, identity.SessionId);

        var attach = bridge.Request(identity, 1);
        var attached = await host.Rpc.UpdateSurfaceAsync(attach, default);
        Assert.True(attached.Success, attached.Status);
        nint hwnd = bridge.ChildFor(host.Process.Id);
        Assert.NotEqual(nint.Zero, hwnd);
        Assert.Equal(bridge.Handle, HostNativeTestMethods.GetParent(hwnd));

        string grant = bridge.GrantFocus();
        var focused = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 2, Action = PreviewSurfaceAction.FocusFirst, FocusToken = grant }, default);
        bridge.RevokeFocus(grant);
        Assert.True(focused.Success, focused.Status);
        Assert.True((await host.Rpc.HeartbeatSurfaceAsync(new(identity), default)).Available);

        var input = first.Nodes.Single(node => node.Name == "Input");
        var edited = await host.Rpc.SetPropertyAsync(new(first.Version, input.Id, "Text", "Kept"), default);
        Assert.True(edited.Success, edited.Error);
        Assert.Equal(identity, edited.Snapshot.Surface);

        var detached = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 3, Action = PreviewSurfaceAction.Detach }, default);
        Assert.True(detached.Success, detached.Status);
        Assert.Equal(nint.Zero, HostNativeTestMethods.GetParent(hwnd));
        var captured = await host.Rpc.CaptureAsync(new(first.Version), default);
        Assert.True(captured.Success);
        Assert.Equal(identity, captured.Surface);
    }

    [Fact]
    public async Task TheHostEndsWhenItsClientDisconnectsEvenWhileProjectCodeBlocksTheDispatcher()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.FrameworkEof.").FullName;
        try
        {
            await using var host = await RawNativePreviewHost.StartAsync(FrameworkHost);
            string marker = Path.Combine(directory, "entered.txt");
            var pending = host.Rpc.RenderAsync(Source(Application, HangingXaml(marker)), default);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(marker)) await Task.Delay(20, wait.Token);

            host.Disconnect();
            await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            await Assert.ThrowsAnyAsync<Exception>(() => pending);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ARenderThatNeverReturnsTimesOutAndTheNextOneStartsAFreshHost()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.FrameworkTimeout.").FullName;
        try
        {
            await using var client = new PreviewClient(requestTimeout: TimeSpan.FromSeconds(8));
            await Assert.ThrowsAsync<TimeoutException>(() => client.RenderAsync(Source(Application, HangingXaml(Path.Combine(directory, "entered.txt")))));
            Assert.True((await client.RenderAsync(Source(Application, version: 2))).Success);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string HangingXaml(string marker) =>
        $"<UserControl {Presentation} xmlns:c='{Controls}'><c:HangingControl MarkerPath='{SecurityElement.Escape(marker)}'/></UserControl>";

    /// <summary>The failure this host exists to prevent, still readable where it can still happen: a Framework library on the .NET host.</summary>
    [Fact]
    public async Task ARuntimeMismatchNamesTheCodeThatCausedIt()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var failed = await client.RenderAsync(Source(Library));

        Assert.False(failed.Success);
        var error = Assert.Single(failed.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        Assert.Contains("AppDomainManagerType", error.Message, StringComparison.Ordinal);
        Assert.Contains("Called from WpfStudio.PreviewFrameworkControls.ProbeControl in WpfStudio.PreviewFrameworkControls", error.Message, StringComparison.Ordinal);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
