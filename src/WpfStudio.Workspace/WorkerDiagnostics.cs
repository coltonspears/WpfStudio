using System.Diagnostics;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using WpfStudio.Contracts.Diagnostics;

namespace WpfStudio.Workspace;

/// <summary>
/// A connection that times every request it serves, from the call into the method that serves it to that method's
/// answer. The time a request stood behind others before that, and the time its answer took to write and send, are not
/// in it: they are the difference between this and what the caller measured for the same request. Each measurement
/// says what the garbage collector did meanwhile, because a blocking collection stops every request at once.
/// </summary>
public sealed class TimedJsonRpc(Stream stream, PerfMonitor perf, string area = PerfAreas.Worker) : JsonRpc(stream)
{
    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(JsonRpcRequest request, TargetMethod targetMethod, CancellationToken cancellationToken)
    {
        // Protocol housekeeping such as $/cancelRequest is not work anyone waits for.
        if (request.Method is not { Length: > 0 } method || method.StartsWith('$')) return await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
        long started = Stopwatch.GetTimestamp();
        var collector = GcSnapshot.Take();
        try { return await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false); }
        finally { perf.Record(area, method, Stopwatch.GetElapsedTime(started), collector.Since); }
    }
}

/// <summary>
/// The worker's side of <see cref="IWorkerDiagnosticsRpc"/>: it passes on what its monitor reports as slow, and watches
/// the thread pool, whose starvation delays every request without making any one of them slow to compute.
/// </summary>
public sealed class WorkerDiagnostics : IWorkerDiagnosticsRpc, IDisposable
{
    private readonly PerfMonitor _perf;
    private readonly ResponsivenessProbe _pool;

    public WorkerDiagnostics(PerfMonitor perf)
    {
        _perf = perf;
        _perf.Slow += Report;
        _pool = ResponsivenessProbe.ForThreadPool(perf);
        _pool.Start();
    }

    public event EventHandler<SlowOperation>? SlowOperationReported;

    public Task<string> GetPerformanceReportAsync(bool reset, CancellationToken cancellationToken)
    {
        string report = PerfReport.Format(_perf, "Language worker");
        if (reset) _perf.Reset();
        return Task.FromResult(report);
    }

    private void Report(SlowOperation operation) => SlowOperationReported?.Invoke(this, operation);

    public void Dispose()
    {
        _perf.Slow -= Report;
        _pool.Dispose();
    }
}
