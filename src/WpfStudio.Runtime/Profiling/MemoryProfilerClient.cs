using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using StreamJsonRpc;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;
using WpfStudio.Hosting;

namespace WpfStudio.Runtime.Profiling;

/// <summary>Starts owned, isolated workers. Replacing/cancelling a capture never kills the target application.</summary>
public sealed class MemoryProfilerClient(string? hostPath = null) : IMemoryProfiler
{
    private readonly ConcurrentDictionary<int, long> _ownedWorkers = new();
    public async Task<IMemorySession> OpenAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var path = hostPath ?? Path.Combine(AppContext.BaseDirectory, "ProfilingHost", "WpfStudio.ProfilingHost.dll");
        var session = await WorkerSession.StartAsync(path, null, _ownedWorkers, cancellationToken).ConfigureAwait(false);
        try
        {
            var architecture = await session.CallAsync(p => p.GetArchitectureAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (architecture == "X86")
            {
                await session.DisposeAsync().ConfigureAwait(false);
                var packaged = Path.Combine(Path.GetDirectoryName(path)!, "x86", "WpfStudio.ProfilingHost.exe");
                var x86Dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe");
                if (File.Exists(packaged)) session = await WorkerSession.StartAsync(packaged, null, _ownedWorkers, cancellationToken).ConfigureAwait(false);
                else if (File.Exists(x86Dotnet)) session = await WorkerSession.StartAsync(path, x86Dotnet, _ownedWorkers, cancellationToken).ConfigureAwait(false);
                else throw new InvalidOperationException("This is a 32-bit capture. Install the .NET 10 x86 runtime for the profiling worker, or use a portable build containing ProfilingHost/x86. The target's .NET Framework version does not need to change.");
            }
            else if (architecture != "X64") throw new PlatformNotSupportedException($"Memory analysis currently supports X64 and X86 captures; this capture is {architecture}.");
            session.Summary = await session.CallAsync(p => p.LoadAsync(request, cancellationToken), cancellationToken,
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task<IReadOnlyList<ProfileProcess>> GetProcessesAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<ProfileProcess>>(() =>
    {
        var result = new List<ProfileProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == Environment.ProcessId || process.ProcessName.Contains("WpfStudio.ProfilingHost", StringComparison.OrdinalIgnoreCase) ||
                        _ownedWorkers.TryGetValue(process.Id, out var workerStart) && process.StartTime.ToUniversalTime().Ticks == workerStart) continue;
                    var modules = process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToArray();
                    var runtime = modules.Any(m => m.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)) ? ".NET"
                        : modules.Any(m => m.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) || m.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase)) ? ".NET Framework" : null;
                    if (runtime is null) continue;
                    result.Add(new(process.Id, process.ProcessName, process.StartTime.ToUniversalTime().Ticks, process.WorkingSet64, runtime));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToArray();
    }, cancellationToken);

    private sealed class WorkerSession : IMemorySession
    {
        private readonly WorkerProcessHost _worker;
        private readonly ConcurrentDictionary<int, long> _owned;
        private readonly JsonRpc _rpc;
        private readonly IMemoryProfilerRpc _proxy;
        private readonly object _lifetime = new();
        private bool _disposed;
        private Task? _disposeTask;
        public HeapSummary Summary { get; internal set; } = null!;
        private WorkerSession(WorkerProcessHost worker, ConcurrentDictionary<int, long> owned, JsonRpc rpc, IMemoryProfilerRpc proxy)
        { _worker = worker; _owned = owned; _rpc = rpc; _proxy = proxy; }

        internal static async Task<WorkerSession> StartAsync(string path, string? dotnetPath, ConcurrentDictionary<int, long> owned, CancellationToken token)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The profiling worker is missing. Build WpfStudio or reinstall the portable package.", path);
            // Prefer the apphost so a self-contained portable worker does not depend on dotnet on PATH.
            // An explicit x86 dotnet host must still launch the AnyCPU DLL in source builds.
            if (dotnetPath is null && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(path, ".exe")))
                path = Path.ChangeExtension(path, ".exe");
            var worker = new WorkerProcessHost(new(path, "WpfStudio-Profiling-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(20)) { DotNetPath = dotnetPath });
            worker.Start();
            try
            {
                owned[worker.ProcessId] = worker.StartTimeUtc.Ticks;
                await worker.ConnectAsync(token).ConfigureAwait(false);
                // Must match the worker's formatter (WpfStudio.ProfilingHost).
                var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(worker.Pipe, worker.Pipe, new SystemTextJsonFormatter()));
                var proxy = rpc.Attach<IMemoryProfilerRpc>(); rpc.StartListening();
                return new(worker, owned, rpc, proxy);
            }
            catch (WorkerConnectionException ex)
            {
                await StopAsync(worker, owned, TimeSpan.Zero).ConfigureAwait(false);
                throw new InvalidOperationException(ex.ExitCode is { } code ? $"The profiling worker exited with code {code} before connecting. {ex.ErrorTail}" : "The profiling worker did not connect. " + ex.ErrorTail, ex);
            }
            catch { await StopAsync(worker, owned, TimeSpan.Zero).ConfigureAwait(false); throw; }
        }

        internal async Task<T> CallAsync<T>(Func<IMemoryProfilerRpc, Task<T>> call, CancellationToken token, TimeSpan? timeout = null)
        {
            lock (_lifetime) ObjectDisposedException.ThrowIf(_disposed, this);
            try { return await call(_proxy).WaitAsync(timeout ?? TimeSpan.FromSeconds(60), token).ConfigureAwait(false); }
            catch (TimeoutException) { await DisposeAsync().ConfigureAwait(false); throw new TimeoutException("Memory analysis timed out. Reopen the capture; the target application keeps running."); }
            catch (RemoteInvocationException ex) { throw new InvalidOperationException(ex.Message, ex); }
            catch (ConnectionLostException ex) { throw new InvalidOperationException("The profiling worker stopped. Reopen the memory capture. " + ex.Message, ex); }
        }
        public Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default) => CallAsync(p => p.GetObjectsAsync(query, cancellationToken), cancellationToken);
        public Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default) => CallAsync(p => p.InspectAsync(objectId, cancellationToken), cancellationToken);
        public Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.GetGraphAsync(request, cancellationToken), cancellationToken);
        public Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.EstimateReleaseAsync(request, cancellationToken), cancellationToken);
        public Task<MemoryGraph> GetNeighborsAsync(MemoryNeighborRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.GetNeighborsAsync(request, cancellationToken), cancellationToken);
        public Task<MemoryDominatorPage> GetDominatorsAsync(MemoryDominatorQuery query, CancellationToken cancellationToken = default) => CallAsync(p => p.GetDominatorsAsync(query, cancellationToken), cancellationToken);
        public Task<MemoryRetainedComposition> GetRetainedAsync(int objectId, CancellationToken cancellationToken = default) => CallAsync(p => p.GetRetainedAsync(objectId, cancellationToken), cancellationToken);
        public Task<MemoryRetentionFlow> GetRetentionFlowAsync(MemoryFlowRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.GetRetentionFlowAsync(request, cancellationToken), cancellationToken);
        public Task<MemoryObjectChildren> GetChildrenAsync(MemoryChildrenRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.GetChildrenAsync(request, cancellationToken), cancellationToken);
        public Task<MemoryInstanceGroups> GetInstanceGroupsAsync(MemoryGroupRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.GetInstanceGroupsAsync(request, cancellationToken), cancellationToken);
        public Task<MemorySunburst> GetDominatorTreeAsync(MemoryDominatorTreeRequest request, CancellationToken cancellationToken = default) => CallAsync(p => p.GetDominatorTreeAsync(request, cancellationToken), cancellationToken);
        public ValueTask DisposeAsync()
        {
            lock (_lifetime)
            {
                if (_disposeTask is null)
                {
                    _disposed = true; _rpc.Dispose();
                    // Disconnecting lets the worker dispose its DAC and PSS snapshot, including the VA clone.
                    // Killing first can strand a clone with open handles to the target's assemblies.
                    _disposeTask = StopAsync(_worker, _owned, TimeSpan.FromSeconds(5));
                }
                return new(_disposeTask);
            }
        }
        // Do not return while the worker still owns the dump file or snapshot.
        private static async Task StopAsync(WorkerProcessHost worker, ConcurrentDictionary<int, long> owned, TimeSpan grace)
        {
            var id = worker.ProcessId;
            try { await worker.StopAsync(grace).ConfigureAwait(false); }
            finally { owned.TryRemove(id, out _); }
        }
    }
}
