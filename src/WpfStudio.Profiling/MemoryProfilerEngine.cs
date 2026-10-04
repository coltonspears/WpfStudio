using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Serializes all DAC and heap access, including read-only queries. One worker owns one capture.</summary>
public sealed class MemoryProfilerEngine : IMemoryProfilerRpc, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClrHeapSnapshot? _snapshot;
    private bool _disposed;
    public Task<string> GetArchitectureAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default) => Run(() =>
    {
        if (request.DumpPath is string path)
        {
            using var target = Microsoft.Diagnostics.Runtime.DataTarget.LoadDump(Path.GetFullPath(path),
                new Microsoft.Diagnostics.Runtime.DataTargetOptions { SkipRuntimeEnumeration = true });
            return target.DataReader.Architecture.ToString();
        }
        using var process = System.Diagnostics.Process.GetProcessById(request.ProcessId ?? throw new ArgumentException("Choose a capture source."));
        _ = process.Handle;
        if (process.StartTime.ToUniversalTime().Ticks != request.ProcessStartTimeUtcTicks)
            throw new InvalidOperationException("The process identity changed. Refresh and select the process again.");
        return ProcessArchitecture.Get(process).ToString();
    }, cancellationToken);
    public Task<HeapSummary> LoadAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default) => Run(() =>
    {
        if (_snapshot is not null) throw new InvalidOperationException("This worker already owns a capture.");
        _snapshot = ClrHeapSnapshot.Open(request, cancellationToken); return _snapshot.Summary;
    }, cancellationToken);
    public Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.GetObjects(query, cancellationToken), cancellationToken);
    public Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Inspect(objectId, cancellationToken), cancellationToken);
    public Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.BuildGraph(request, cancellationToken), cancellationToken);
    public Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.EstimateRelease(request, cancellationToken), cancellationToken);
    public Task<MemoryGraph> GetNeighborsAsync(MemoryNeighborRequest request, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.GetNeighbors(request, cancellationToken), cancellationToken);
    public Task<MemoryDominatorPage> GetDominatorsAsync(MemoryDominatorQuery query, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.GetDominators(query, cancellationToken), cancellationToken);
    public Task<MemoryRetainedComposition> GetRetainedAsync(int objectId, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.GetRetained(objectId, cancellationToken), cancellationToken);
    public Task<MemoryRetentionFlow> GetRetentionFlowAsync(MemoryFlowRequest request, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.Analysis.GetRetentionFlow(request, cancellationToken), cancellationToken);
    public Task<MemoryObjectChildren> GetChildrenAsync(MemoryChildrenRequest request, CancellationToken cancellationToken = default) =>
        Run(() => Snapshot.GetChildren(request, cancellationToken), cancellationToken);
    private ClrHeapSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("Open a memory capture first.");
    private async Task<T> Run<T>(Func<T> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(action, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true; _snapshot?.Dispose(); _snapshot = null;
        }
        // Queued RPC calls can still acquire the gate and observe disposal; never unload a DAC in use.
        finally { _gate.Release(); }
    }
}
