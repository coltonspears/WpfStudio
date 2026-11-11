namespace WpfStudio.Contracts.Diagnostics;

/// <summary>
/// What a worker process says about its own performance, served beside its main protocol on the same connection. The
/// IDE cannot see inside a worker: a request that took a second there looks the same from outside whether the worker
/// computed for a second, waited a second for a collection, or did not get to the request for a second.
/// </summary>
public interface IWorkerDiagnosticsRpc
{
    /// <summary>A measurement in the worker went over its area's limit.</summary>
    event EventHandler<SlowOperation>? SlowOperationReported;
    /// <summary>The worker's measurements as a table. <paramref name="reset"/> starts its counting again afterwards.</summary>
    Task<string> GetPerformanceReportAsync(bool reset, CancellationToken cancellationToken);
}
