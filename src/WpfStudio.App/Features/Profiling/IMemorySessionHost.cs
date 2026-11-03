using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>
/// What the memory profiler's views share: the open capture and how to tell whether an answer still belongs to it. Each
/// view is a view model of its own that asks through this contract instead of sharing the profiler's fields as a partial.
/// </summary>
public interface IMemorySessionHost
{
    IMemorySession? Session { get; }
    /// <summary>Changes whenever the capture does; an answer read under another revision is stale.</summary>
    long Revision { get; }
    HeapSummary? Summary { get; }
    bool IsDisposed { get; }
    /// <summary>Cancelled when the profiler is disposed.</summary>
    CancellationToken Lifetime { get; }
    void ReportStatus(string status);
    /// <summary>For a load nobody awaits. Safe on any thread.</summary>
    void ReportFault(Exception fault);
    /// <summary>Opens an object in the browser without leaving the current view.</summary>
    void ShowObject(int objectId);
}

/// <summary>What the object browser needs beyond the session: the graph beside it and the rest of the profiler's state.</summary>
public interface IObjectBrowserHost : IMemorySessionHost
{
    /// <summary>A capture is loading, so nothing else may start.</summary>
    bool IsBusy { get; }
    bool CanUseCapture { get; }
    IReadOnlyList<string>? HiddenRootKinds { get; }
    /// <summary>The type of an object listed elsewhere in the profiler, to name it before it has been inspected.</summary>
    string? KnownTypeOf(int objectId);
    void ClearGraph();
    void ShowGraph(int objectId, MemoryGraph graph);
    void OpenType(string typeKey);
}
