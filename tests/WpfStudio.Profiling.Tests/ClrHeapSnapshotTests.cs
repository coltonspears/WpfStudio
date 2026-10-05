using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WpfStudio.Contracts.Profiling;
using WpfStudio.Profiling;

namespace WpfStudio.Profiling.Tests;

public sealed class ClrHeapSnapshotTests
{
    [Theory]
    [InlineData("net10.0", ".NET 10")]
    [InlineData("net48", ".NET Framework")]
    public async Task LiveSnapshotsAndFullDumpsExplainRealStaticAndEventRetention(string framework, string expectedRuntime)
    {
        using var lease = await StartFixture(framework);
        var fixture = lease.Process;
        var request = new HeapCaptureRequest(ProcessId: fixture.Id, ProcessStartTimeUtcTicks: fixture.StartTime.ToUniversalTime().Ticks);
        using var baseline = ClrHeapSnapshot.Open(request);
        Assert.Contains(expectedRuntime, baseline.Summary.Runtime);
        Assert.True(baseline.Summary.IsComplete, string.Join("; ", baseline.Summary.CoverageNotes));
        var pageType = Assert.Single(baseline.Summary.Types, t => t.Name == "WpfStudio.RetentionFixture.RetainedPageModel");
        Assert.Equal(1, pageType.Count);
        var page = Assert.Single(baseline.Analysis.GetObjects(new(pageType.Key)).Objects);
        var detail = baseline.Inspect(page.Id);
        Assert.Contains(detail.Fields, f => f.Name == "CustomerId" && f.Value == "42");
        Assert.Contains(detail.Fields, f => f.Name == "Name" && f.Value.Contains("Closed customer page"));
        Assert.Contains(detail.RootPaths.SelectMany(p => p.References), r => r.Label.Contains("Static "));
        Assert.Contains(detail.Evidence, e => e.Contains("delegate"));
        var release = baseline.Analysis.EstimateRelease(new(page.Id));
        Assert.True(release.ReclaimableBytes >= 65_536);
        Assert.DoesNotContain(release.Types, t => t.Type.Contains("Shared"));
        var sharedId = detail.Fields.Single(f => f.Name == "Shared").ObjectId!.Value;
        Assert.DoesNotContain(sharedId, release.ObjectSample);
        if (framework == "net10.0")
        {
            var literalId = detail.Fields.Single(f => f.Name == "Name").ObjectId!.Value;
            Assert.Equal("Frozen", baseline.Analysis.Describe(literalId).Generation);
            Assert.Equal(0, baseline.Analysis.Describe(literalId).RetainedBytes);
            Assert.DoesNotContain(literalId, release.ObjectSample);
            Assert.Equal(0, baseline.Analysis.EstimateRelease(new(literalId)).ReclaimableBytes);
        }
        // A cache slot alone cannot release a model that is also subscribed to a static event.
        var cacheReference = detail.Incoming.First(r => !r.IsRoot && r.Owner.Contains("[]"));
        var oneSlot = baseline.Analysis.EstimateRelease(new(page.Id, cacheReference.Id));
        Assert.True(oneSlot.SelectedObjectRemainsReachable);
        Assert.Equal(0, oneSlot.ReclaimableBytes);
        Assert.NotNull(oneSlot.RemainingRootPath);

        await Command(fixture, "grow");
        using var grown = ClrHeapSnapshot.Open(request);
        Assert.Equal(2, grown.Summary.Types.Single(t => t.Key == pageType.Key).Count);
        // Both pages are kept by the same static path; their payloads are identical zero-filled arrays.
        Assert.Equal(2, Assert.Single(grown.GetInstanceGroups(new(pageType.Key)).Groups).Count);
        var bytesKey = grown.Summary.Types.Single(t => t.Name == "System.Byte[]").Key;
        var payloads = Assert.Single(grown.GetInstanceGroups(new(bytesKey, "Value")).Groups, g => g.Kind == "Value" && g.Samples.All(s => s.ShallowBytes >= 65_536));
        Assert.Equal(2, payloads.Count); Assert.True(payloads.WastedBytes >= 65_536);
        Assert.Equal("Unique", Assert.Single(grown.GetInstanceGroups(new(pageType.Key, "Value")).Groups).Kind);
        await Command(fixture, "clear");
        using var cleared = ClrHeapSnapshot.Open(request);
        Assert.DoesNotContain(cleared.Summary.Types, t => t.Key == pageType.Key);

        await Command(fixture, "grow");
        var dump = Path.Combine(Path.GetTempPath(), "WpfStudio-Memory-" + Guid.NewGuid().ToString("N") + ".dmp");
        try
        {
            WriteDump(fixture, dump);
            using var loaded = ClrHeapSnapshot.Open(new(DumpPath: dump));
            Assert.Contains(expectedRuntime, loaded.Summary.Runtime);
            Assert.True(loaded.Summary.IsComplete, string.Join("; ", loaded.Summary.CoverageNotes));
            var loadedPage = Assert.Single(loaded.Analysis.GetObjects(new(pageType.Key)).Objects);
            Assert.Contains(loaded.Inspect(loadedPage.Id).Fields, f => f.Name == "CustomerId" && f.Value == "42");
            Assert.True(loaded.Analysis.EstimateRelease(new(loadedPage.Id)).ReclaimableBytes >= 65_536);
        }
        finally
        {
            var absolute = Path.GetFullPath(dump);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(absolute), ignoreCase: true);
            File.Delete(absolute);
            if (!fixture.HasExited) fixture.Kill();
        }
    }

    [Fact]
    public async Task StaleProcessIdentityCannotCaptureAReusedSelection()
    {
        using var lease = await StartFixture("net10.0");
        var fixture = lease.Process;
        try { Assert.Throws<InvalidOperationException>(() => ClrHeapSnapshot.Open(new(ProcessId: fixture.Id, ProcessStartTimeUtcTicks: 1))); }
        finally { if (!fixture.HasExited) fixture.Kill(); }
    }

    private static async Task<FixtureLease> StartFixture(string framework)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WpfStudio.sln"))) root = root.Parent;
        var configuration = typeof(ClrHeapSnapshotTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        var file = Path.Combine(root!.FullName, "tests", "WpfStudio.Profiling.Tests", "Fixtures", "RetentionFixture", "bin", configuration,
            framework, "WpfStudio.RetentionFixture." + (framework == "net48" ? "exe" : "dll"));
        var start = new ProcessStartInfo(framework == "net48" ? file : "dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (framework != "net48") start.ArgumentList.Add(file);
        var process = Process.Start(start)!;
        try { Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))); return new(process); }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
    }
    private static async Task Command(Process process, string command)
    { await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync(); Assert.Equal("DONE", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))); }
    private static void WriteDump(Process process, string path)
    {
        using var file = File.Create(path);
        if (!MiniDumpWriteDump(process.Handle, process.Id, file.SafeFileHandle, 0x2 | 0x4 | 0x800 | 0x1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int processId, SafeFileHandle file, int type,
        IntPtr exception, IntPtr userStream, IntPtr callback);
    private sealed class FixtureLease(Process process) : IDisposable
    {
        public Process Process => process;
        public void Dispose() { if (!process.HasExited) process.Kill(); process.Dispose(); }
    }
}
