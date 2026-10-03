using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WpfStudio.Contracts.Profiling;
using WpfStudio.Runtime.Profiling;

namespace WpfStudio.Shell.Tests;

public sealed class MemoryProfilerClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedWorkerSupportsModern64BitAndFramework32BitTargets(bool framework32)
    {
        var root = FindRoot(); var configuration = GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var directory = framework32 ? "RetentionFixture32" : "RetentionFixture";
        var name = framework32 ? "WpfStudio.RetentionFixture32.exe" : "WpfStudio.RetentionFixture.dll";
        var path = Path.Combine(root, "tests", "WpfStudio.Profiling.Tests", "Fixtures", directory, "bin", configuration,
            framework32 ? "net48" : "net10.0", name);
        var start = new ProcessStartInfo(framework32 ? path : "dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!framework32) start.ArgumentList.Add(path);
        using var fixture = Process.Start(start)!;
        try
        {
            Assert.Equal("READY", await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            var client = new MemoryProfilerClient(Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_PROFILING_HOST") ??
                Path.Combine(root, "src", "WpfStudio.App", "bin", configuration, "net10.0-windows", "ProfilingHost", "WpfStudio.ProfilingHost.dll"));
            var processes = await client.GetProcessesAsync();
            Assert.Contains(processes, p => p.Id == fixture.Id);
            await using var session = await client.OpenAsync(new(ProcessId: fixture.Id, ProcessStartTimeUtcTicks: fixture.StartTime.ToUniversalTime().Ticks));
            Assert.Equal(framework32 ? "X86" : "X64", session.Summary.Architecture);
            Assert.True(session.Summary.IsComplete, string.Join("; ", session.Summary.CoverageNotes));
            var type = session.Summary.Types.Single(t => t.Name == "WpfStudio.RetentionFixture.RetainedPageModel");
            var page = Assert.Single((await session.GetObjectsAsync(new(type.Key))).Objects);
            var details = await session.InspectAsync(page.Id);
            Assert.Contains(details.Fields, f => f.Name == "CustomerId" && f.Value == "42");
            var graph = await session.GetGraphAsync(new(page.Id)); Assert.Contains(graph.Nodes, n => n.IsFocus);
            var estimate = await session.EstimateReleaseAsync(new(page.Id)); Assert.True(estimate.ReclaimableBytes > 65_536);
            await session.DisposeAsync();
            Assert.False(fixture.HasExited);
            var dump = Path.Combine(Path.GetTempPath(), "WpfStudio-WorkerDump-" + Guid.NewGuid().ToString("N") + ".dmp");
            try
            {
                using (var file = File.Create(dump))
                    if (!MiniDumpWriteDump(fixture.Handle, fixture.Id, file.SafeFileHandle, 0x2 | 0x4 | 0x800 | 0x1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (framework32)
                {
                    var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync(new(DumpPath: dump)));
                    Assert.Contains("WOW64", unsupported.Message);
                    Assert.Contains("ProcDump -ma", unsupported.Message);
                    // A same-bitness writer captures x86 thread contexts instead of the WOW64 subsystem.
                    var writerStart = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
                    writerStart.ArgumentList.Add("--dump"); writerStart.ArgumentList.Add(fixture.Id.ToString()); writerStart.ArgumentList.Add(dump);
                    using var writer = Process.Start(writerStart)!;
                    await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(0, writer.ExitCode);
                }
                await using var loaded = await client.OpenAsync(new(DumpPath: dump));
                Assert.Equal(framework32 ? "X86" : "X64", loaded.Summary.Architecture);
                Assert.True(loaded.Summary.IsComplete, string.Join("; ", loaded.Summary.CoverageNotes));
                var loadedPage = Assert.Single((await loaded.GetObjectsAsync(new(type.Key))).Objects);
                Assert.True((await loaded.EstimateReleaseAsync(new(loadedPage.Id))).ReclaimableBytes > 65_536);
            }
            finally
            {
                Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(Path.GetFullPath(dump)), ignoreCase: true);
                File.Delete(dump);
            }
        }
        finally { if (!fixture.HasExited) fixture.Kill(); }
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WpfStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int processId, SafeFileHandle file, int type,
        IntPtr exception, IntPtr userStream, IntPtr callback);
}
