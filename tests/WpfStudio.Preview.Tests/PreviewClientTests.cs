using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class PreviewClientTests
{
    private static string HostPath => PreviewHostUnderTest.ExecutablePath;
    private static PreviewRequest Request(long version = 1) => new("C:/preview/Client.xaml",
        "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Button Name='Subject' Width='100'>Client</Button></Grid>", version, 400, 300);
    private static PreviewRequest WithAssembly(PreviewRequest request) => request with
    {
        AssemblyPath = typeof(HangingPreviewControl).Assembly.Location,
        ProjectDirectory = AppContext.BaseDirectory
    };
    private static PreviewRequest HangingRequest(long version = 2) => WithAssembly(Request(version)) with
    {
        Text = "<probe:HangingPreviewControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>"
    };

    [Fact]
    public async Task CancellationStopsOnlyOwnedProcessAndNextRenderRestarts()
    {
        await using var client = new PreviewClient(HostPath);
        await using var otherClient = new PreviewClient(HostPath);
        var original = await client.RenderAsync(WithAssembly(Request()));
        Assert.True(original.Success);
        Assert.True((await otherClient.RenderAsync(Request())).Success);
        using var process = Process.GetProcessById(client.ProcessId!.Value);
        using var otherProcess = Process.GetProcessById(otherClient.ProcessId!.Value);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RenderAsync(HangingRequest(), cancellation.Token));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(client.ProcessId);
        Assert.False(otherProcess.HasExited);
        var restarted = await client.RenderAsync(Request(3));
        Assert.True(restarted.Success);
        Assert.NotEqual(process.Id, client.ProcessId);
        var staleEdit = await client.SetPropertyAsync(new(original.Version, original.Nodes[0].Id, "Width", "300"));
        Assert.False(staleEdit.Success);
        Assert.Equal(3, staleEdit.Snapshot.Version);
        Assert.True((await otherClient.RenderAsync(Request(2))).Success);
    }

    [Fact]
    public async Task TimeoutTerminatesHungConstructorAndAllowsRecovery()
    {
        await using var client = new PreviewClient(HostPath, TimeSpan.FromSeconds(4));
        Assert.True((await client.RenderAsync(WithAssembly(Request()))).Success);
        using var process = Process.GetProcessById(client.ProcessId!.Value);
        await Assert.ThrowsAsync<TimeoutException>(() => client.RenderAsync(HangingRequest()));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(client.ProcessId);
        Assert.True((await client.RenderAsync(Request(3))).Success);
    }

    [Fact]
    public async Task CrashRaisesDisconnectAndNewRenderReconnects()
    {
        await using var client = new PreviewClient(HostPath);
        Assert.True((await client.RenderAsync(Request())).Success);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, message) => disconnected.TrySetResult(message);
        using var process = Process.GetProcessById(client.ProcessId!.Value);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True((await client.RenderAsync(Request(2))).Success);
        Assert.NotEqual(process.Id, client.ProcessId);
    }

    [Fact]
    public async Task DisposalInterruptsHungRequestWithoutLeavingAnOwnedProcess()
    {
        var client = new PreviewClient(HostPath);
        try
        {
            Assert.True((await client.RenderAsync(WithAssembly(Request()))).Success);
            using var process = Process.GetProcessById(client.ProcessId!.Value);
            var pending = client.RenderAsync(HangingRequest());
            await Task.Delay(150);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(client.ProcessId);
        }
        finally { await client.DisposeAsync(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopRevokesHungAndQueuedRequestsWhileLaterRenderUsesAFreshEpoch(int stops)
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.Preview.StopTest.").FullName;
        await using var client = new PreviewClient(HostPath, TimeSpan.FromMinutes(1));
        await using var otherClient = new PreviewClient(HostPath);
        try
        {
            Assert.True((await client.RenderAsync(WithAssembly(Request()))).Success);
            Assert.True((await otherClient.RenderAsync(Request())).Success);
            using var process = Process.GetProcessById(client.ProcessId!.Value);
            using var otherProcess = Process.GetProcessById(otherClient.ProcessId!.Value);
            _ = process.Handle;
            _ = otherProcess.Handle;
            string marker = Path.Combine(directory, "entered.txt");
            var pending = client.RenderAsync(WithAssembly(Request(2)) with
            {
                Text = "<probe:SignaledHangingPreviewControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' MarkerPath='" +
                    SecurityElement.Escape(marker) + "'/>"
            });
            await WaitForFileAsync(marker);
            var queued = client.RenderAsync(Request(3));
            var stopping = Enumerable.Range(0, stops).Select(_ => client.StopAsync()).ToArray();
            // Issued before Stop completes: it must wait for cleanup, then use a
            // fresh host. A late Stop must not terminate this new render.
            var restart = client.RenderAsync(Request(4));
            await Task.WhenAll(stopping).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var restarted = await restart.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(restarted.Success, string.Join("\n", restarted.Diagnostics.Select(d => d.Message)));
            Assert.Equal(4, restarted.Version);
            Assert.NotEqual(process.Id, client.ProcessId);
            Assert.False(otherProcess.HasExited);
            Assert.NotNull((await client.InspectAsync(new(restarted.Version, restarted.Nodes[0].Id))).Node);
            Assert.True((await otherClient.RenderAsync(Request(2))).Success);
        }
        finally
        {
            await client.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AlreadyCanceledStopDoesNotRevokeTheCurrentPreview()
    {
        await using var client = new PreviewClient(HostPath);
        var snapshot = await client.RenderAsync(Request());
        Assert.True(snapshot.Success);
        int processId = client.ProcessId!.Value;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StopAsync(cancellation.Token));
        Assert.Equal(processId, client.ProcessId);
        Assert.NotNull((await client.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id))).Node);
    }

    [Fact]
    public async Task StopTerminatesOnlyTheLaunchedHostRatherThanItsProcessTree()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.Preview.ChildOwnership.").FullName;
        await using var client = new PreviewClient(HostPath);
        Process? child = null;
        try
        {
            string marker = Path.Combine(directory, "child.txt");
            var snapshot = await client.RenderAsync(WithAssembly(Request()) with
            {
                Text = "<probe:ChildProcessPreviewControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' MarkerPath='" +
                    SecurityElement.Escape(marker) + "'/>"
            });
            Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
            string[] identity = File.ReadAllLines(marker);
            var candidate = Process.GetProcessById(int.Parse(identity[0], CultureInfo.InvariantCulture));
            try
            {
                _ = candidate.Handle;
                Assert.Equal(long.Parse(identity[1], CultureInfo.InvariantCulture), candidate.StartTime.ToUniversalTime().Ticks);
                child = candidate;
            }
            finally { if (child is null) candidate.Dispose(); }
            Assert.False(child.HasExited);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(client.ProcessId);
            Assert.False(child.HasExited);
        }
        finally
        {
            // This fixture owns this exact pinned child. Never reacquire by PID
            // or recursively terminate any further descendants during cleanup.
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited) child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally { child.Dispose(); }
            }
            await client.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RebuiltAssemblyAtSamePathRestartsTheHost()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Preview.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assembly = Path.Combine(directory, "WpfStudio.Contracts.dll");
        File.Copy(typeof(PreviewRequest).Assembly.Location, assembly);
        var request = Request() with { AssemblyPath = assembly, ProjectDirectory = directory };
        var client = new PreviewClient(HostPath);
        Process? current = null;
        try
        {
            Assert.True((await client.RenderAsync(request)).Success);
            using var previous = Process.GetProcessById(client.ProcessId!.Value);
            File.SetLastWriteTimeUtc(assembly, File.GetLastWriteTimeUtc(assembly).AddMinutes(1));
            Assert.True((await client.RenderAsync(request with { Version = 2 })).Success);
            current = Process.GetProcessById(client.ProcessId!.Value);
            Assert.NotEqual(previous.Id, current.Id);
            await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await client.DisposeAsync();
            if (current is not null) { await current.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); current.Dispose(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CustomControlConstructorObservesWpfDesignMode()
    {
        await using var client = new PreviewClient(HostPath);
        var snapshot = await client.RenderAsync(WithAssembly(Request()) with
        {
            Text = "<probe:DesignAwareControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>"
        });
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var inspection = await client.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id));
        Assert.Contains(inspection.Properties, p => p.Name == "Text" && p.Value == "Design");
    }

    [Fact]
    public async Task AutomaticDiscoverySkipsOrphanExecutableWithoutItsRuntimeFiles()
    {
        string orphanDirectory = Path.Combine(AppContext.BaseDirectory, "PreviewHost");
        Assert.False(Directory.Exists(orphanDirectory), "The test requires a clean Preview.Tests output without a packaged PreviewHost directory.");
        Directory.CreateDirectory(orphanDirectory);
        string orphanExecutable = Path.Combine(orphanDirectory, "WpfStudio.PreviewHost.exe");
        File.Copy(HostPath, orphanExecutable);
        try
        {
            await using var client = new PreviewClient();
            var result = await client.RenderAsync(Request());
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            using var process = Process.GetProcessById(client.ProcessId!.Value);
            Assert.NotEqual(orphanExecutable, process.MainModule!.FileName);
        }
        finally
        {
            File.Delete(orphanExecutable);
            Directory.Delete(orphanDirectory);
        }
    }

    [Fact]
    public async Task ShadowCopyLeavesProjectAndDependencyDllsReplaceableAndCleansUpOnExit()
    {
        string project = Directory.CreateTempSubdirectory("WpfStudio.Preview.ProjectTest.").FullName;
        string output = Path.Combine(project, "bin");
        CopyDirectory(AppContext.BaseDirectory, output);
        Directory.CreateDirectory(Path.Combine(output, "Nested", "Assets"));
        File.WriteAllText(Path.Combine(output, "Nested", "Assets", "probe.txt"), "Copied subtree");
        string sourceAssembly = Path.Combine(output, "WpfStudio.Preview.Tests.dll");
        string sourceDependency = Path.Combine(output, "WpfStudio.Runtime.dll");
        var client = new PreviewClient(HostPath);
        string? shadowRoot = null;
        Process? process = null;
        try
        {
            var snapshot = await client.RenderAsync(Request() with
            {
                AssemblyPath = sourceAssembly,
                ProjectDirectory = project,
                Text = "<probe:AssemblyLocationControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>"
            });
            Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
            process = Process.GetProcessById(client.ProcessId!.Value);
            var inspection = await client.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id));
            string[] locations = Assert.Single(inspection.Properties, p => p.Name == "Text").Value.Split('|');
            Assert.Equal(3, locations.Length);
            Assert.NotEqual(sourceAssembly, locations[0]);
            Assert.NotEqual(sourceDependency, locations[1]);
            Assert.Equal("Copied subtree", locations[2]);
            Assert.True(File.Exists(locations[0]));
            Assert.True(File.Exists(locations[1]));
            shadowRoot = Directory.GetParent(Path.GetDirectoryName(locations[0])!)!.FullName;
            Assert.StartsWith("WpfStudio.Preview.", Path.GetFileName(shadowRoot));

            // Opening the actual outputs for replacement would fail on Windows
            // while LoadFromAssemblyPath held their executable-image mappings.
            File.WriteAllBytes(sourceAssembly, File.ReadAllBytes(typeof(AssemblyLocationControl).Assembly.Location));
            File.WriteAllBytes(sourceDependency, File.ReadAllBytes(typeof(PreviewClient).Assembly.Location));
            Assert.False(process.HasExited);
            Assert.NotNull((await client.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id))).Node);

            await client.DisposeAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Directory.Exists(shadowRoot));
            Assert.True(File.Exists(sourceAssembly));
            Assert.True(File.Exists(sourceDependency));
        }
        finally
        {
            await client.DisposeAsync();
            if (process is not null) { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); process.Dispose(); }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(project), ignoreCase: true);
            Directory.Delete(project, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
    }
}

/// <summary>Instantiated exclusively in a child preview host to exercise hung user constructors.</summary>
public sealed class HangingPreviewControl : FrameworkElement
{
    public HangingPreviewControl() => Thread.Sleep(Timeout.Infinite);
}

public sealed class SignaledHangingPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty MarkerPathProperty = DependencyProperty.Register(nameof(MarkerPath), typeof(string),
        typeof(SignaledHangingPreviewControl), new PropertyMetadata(null, (_, args) =>
        {
            File.WriteAllText((string)args.NewValue, "entered");
            Thread.Sleep(Timeout.Infinite);
        }));
    public string MarkerPath { get => (string)GetValue(MarkerPathProperty); set => SetValue(MarkerPathProperty, value); }
}

public sealed class ChildProcessPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty MarkerPathProperty = DependencyProperty.Register(nameof(MarkerPath), typeof(string),
        typeof(ChildProcessPreviewControl), new PropertyMetadata(null, (target, args) =>
        {
            // A second fixture host waits for a deliberately absent pipe client
            // and exits on its own startup deadline if this test is interrupted.
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add("WpfStudio.Preview.OwnershipTest." + Guid.NewGuid().ToString("N"));
            using var child = Process.Start(start)!;
            _ = child.Handle;
            File.WriteAllLines((string)args.NewValue, [child.Id.ToString(CultureInfo.InvariantCulture),
                child.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)]);
        }));
    public string MarkerPath { get => (string)GetValue(MarkerPathProperty); set => SetValue(MarkerPathProperty, value); }
}

public sealed class DesignAwareControl : System.Windows.Controls.TextBlock
{
    public DesignAwareControl() => Text = DesignerProperties.GetIsInDesignMode(this) ? "Design" : "Runtime";
}

public sealed class AssemblyLocationControl : System.Windows.Controls.TextBlock
{
    public AssemblyLocationControl()
    {
        string assembly = typeof(AssemblyLocationControl).Assembly.Location;
        Text = assembly + "|" + typeof(PreviewClient).Assembly.Location + "|" +
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(assembly)!, "Nested", "Assets", "probe.txt"));
    }
}
