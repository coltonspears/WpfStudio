using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Memory investigations are snapshot based. The target is never mutated by an estimate or field inspection.
/// This file owns capture lifetime; the Overview, Types and Graph partials own their views, and the browser, retention view
/// and timeline are view models of their own.</summary>
public sealed partial class MemoryProfilerViewModel(IMemoryProfiler profiler, IFileDialogService files, IClipboardService clipboard, IProcessMemorySampler sampler) : ObservableObject, IAsyncDisposable, IObjectBrowserHost
{
    public const int OverviewView = 0, TypesView = 1, RetentionView = 2, GraphView = 3;
    public const int FieldsTab = 0, RootsTab = 1, KeepsAliveTab = 2, ReferencesTab = 3;
    private static readonly JsonSerializerOptions IndentedReport = new() { WriteIndented = true };
    private readonly CancellationTokenSource _lifetime = new();
    private IMemorySession? _session;
    private HeapSummary? _baseline;
    private long _revision;
    private bool _disposed, _updating;
    public ObservableCollection<ProfileProcess> Processes { get; } = [];
    public ObjectBrowserViewModel Browser => field ??= new(this, clipboard);
    public RetentionViewModel Retention => field ??= new(this);
    public ProcessMemoryTimeline Timeline => field ??= new(sampler, this, RefreshTimelineMarkers);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLiveSource)), NotifyCanExecuteChangedFor(nameof(CaptureCommand))] public partial ProfileProcess? SelectedProcess { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCapture), nameof(SourceDescription), nameof(SourceName), nameof(CoverageDescription), nameof(IsIncomplete),
        nameof(ManagedSize), nameof(ReachableSize), nameof(UnreachableSize), nameof(IsBrowserVisible)),
        NotifyCanExecuteChangedFor(nameof(SetBaselineCommand), nameof(CloseCaptureCommand), nameof(ExportReportCommand))]
    public partial HeapSummary? Summary { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Open a full dump or capture a running .NET application to investigate memory.";
    [ObservableProperty] public partial string BusyMessage { get; set; } = "";
    [ObservableProperty] public partial string ComparisonDescription { get; set; } = "Set a baseline, repeat a workload, then open or capture another snapshot to compare type growth.";
    [ObservableProperty] public partial string DacPath { get; set; } = "";
    [ObservableProperty] public partial int RuntimeIndex { get; set; }
    [ObservableProperty] public partial int MaxObjects { get; set; } = 1_000_000;
    [ObservableProperty] public partial int MaxReferences { get; set; } = 6_000_000;
    [ObservableProperty] public partial bool ShowOptions { get; set; }
    /// <summary>Overview, Types, Retention, Graph or Snapshots.</summary>
    [ObservableProperty] public partial int SelectedView { get; set; }
    /// <summary>Gives the graph the whole workbench by hiding the object browser.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GraphFocusLabel), nameof(IsBrowserVisible))] public partial bool FocusGraph { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBrowserVisible))] public partial bool ShowBrowser { get; set; } = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyCanExecuteChangedFor(nameof(OpenDumpCommand), nameof(CaptureCommand),
        nameof(RefreshProcessesCommand), nameof(SetBaselineCommand), nameof(ClearBaselineCommand), nameof(CloseCaptureCommand),
        nameof(ExportReportCommand), nameof(OpenTypeCommand))]
    public partial bool IsBusy { get; set; }
    /// <summary>A new capture is open and filling its first views. Another load has to wait for them.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenDumpCommand), nameof(CaptureCommand))] public partial bool IsLoadingFirstViews { get; set; }
    public bool IsIdle => !IsBusy;
    public bool IsBrowserVisible => ShowBrowser && !FocusGraph && HasCapture;
    public string GraphFocusLabel => FocusGraph ? "Show inspector" : "Focus graph";
    public bool HasCapture => Summary is not null;
    public bool HasBaseline => _baseline is not null;
    public HeapSummary? Baseline => _baseline;
    public string SourceDescription => Summary is { } s ? $"{s.Runtime} · {s.Architecture} · loaded {s.CapturedAt.LocalDateTime:t}" + (s.IsComplete ? "" : " · incomplete data") : "Managed memory investigations";
    public string SourceName => Summary is { } s ? (s.Source.Contains(Path.DirectorySeparatorChar) ? Path.GetFileName(s.Source) : s.Source) : "";
    public string CoverageDescription => Summary is { } s
        ? (s.IsComplete ? "Captured heap graph" : "Incomplete heap graph — sizes and estimates are provisional") +
            (s.CoverageNotes.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, s.CoverageNotes) : "") : "";
    public bool IsIncomplete => Summary is { IsComplete: false };
    public string ManagedSize => Summary is { } s ? MemorySize.Format(s.ManagedBytes) : "—";
    public string ReachableSize => Summary is { } s ? MemorySize.Format(s.ReachableBytes) : "—";
    public string UnreachableSize => Summary is { } s ? MemorySize.Format(s.UnreachableBytes) : "—";
    public string BaselineDescription => BaselineSnapshot is { } b
        ? ReferenceEquals(b, CurrentSnapshot) ? $"Baseline {b.Title} (this snapshot)" : $"vs {b.Title}: {MemorySize.Signed((Summary?.ManagedBytes ?? 0) - b.Summary.ManagedBytes)}"
        : _baseline is null ? "No baseline" : $"Baseline {MemorySize.Format(_baseline.ManagedBytes)} · {_baseline.CapturedAt.LocalDateTime:t}";

    partial void OnSummaryChanged(HeapSummary? value) => RefreshOverview();
    partial void OnIsBusyChanged(bool value) => Browser.NotifyEstimates();
    partial void OnFocusGraphChanged(bool value) { if (value) SelectedView = GraphView; }

    private bool CanLoad() => !_disposed && !IsBusy;
    private bool CanStartLoad() => CanLoad() && !IsLoadingFirstViews;
    private bool CanCapture() => CanStartLoad() && SelectedProcess is not null;
    private bool CanUseCapture() => CanLoad() && _session is not null;
    private bool CanClearBaseline() => CanLoad() && HasBaseline;

    [RelayCommand(CanExecute = nameof(CanStartLoad), IncludeCancelCommand = true)]
    private async Task OpenDumpAsync(CancellationToken token)
    {
        var path = await files.OpenFileAsync("Open managed memory dump", "Memory dumps|*.dmp;*.dump;*.core|All files|*.*");
        if (path is not null) await LoadAsync(new(path, RuntimeIndex: RuntimeIndex, MaxObjects: MaxObjects, MaxReferences: MaxReferences, DacPath: NullDac()), token);
    }
    [RelayCommand(CanExecute = nameof(CanCapture), IncludeCancelCommand = true)]
    private Task CaptureAsync(CancellationToken token)
    {
        var process = SelectedProcess!;
        return LoadAsync(new(ProcessId: process.Id, ProcessStartTimeUtcTicks: process.StartTimeUtcTicks,
            RuntimeIndex: RuntimeIndex, MaxObjects: MaxObjects, MaxReferences: MaxReferences, DacPath: NullDac()), token);
    }
    private string? NullDac() => string.IsNullOrWhiteSpace(DacPath) ? null : DacPath.Trim();

    private async Task LoadAsync(HeapCaptureRequest request, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        IMemorySession? pending = null;
        var opened = false;
        IsBusy = true; BusyMessage = request.DumpPath is null ? "Capturing an immutable heap snapshot…" : "Reading objects, references, and GC roots…";
        PrepareSnapshotSource(request);
        Status = "Analyzing memory in an isolated worker. Cancel stops this analysis.";
        try
        {
            pending = await profiler.OpenAsync(request, operation.Token);
            if (_disposed) return;
            var old = _session; _session = pending; pending = null; opened = true;
            _revision++; CancelSelection(); Browser.ResetHistory(); _skip = 0;
            _updating = true;
            try
            {
                Objects.Clear(); SelectedObject = null; SelectedType = null; Browser.ClearObject(); Graph = null; Flow = null;
                Retention.Reset(); ResetGroups(); Summary = _session.Summary;
            }
            finally { _updating = false; }
            if (old is not null) await old.DisposeAsync();
            RecordSnapshot(Summary!);
            Status = Summary!.IsComplete ? $"{Summary.ObjectCount:N0} objects and {Summary.RootCount:N0} roots analyzed." +
                (Summary.Insights is { Count: > 0 } found ? $" {found.Count} finding{(found.Count == 1 ? "" : "s")} to review." : " Select a type or explore what retains the most memory.")
                : "Analysis completed with incomplete heap coverage. Read the coverage notes before interpreting estimates.";
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Memory capture cancelled. The previous capture remains available."; }
        catch (Exception ex) { if (!_disposed) Status = "Memory capture failed: " + ex.Message; }
        finally
        {
            if (pending is not null) await pending.DisposeAsync();
            IsBusy = false; BusyMessage = "";
        }
        if (opened && !_disposed) await LoadFirstViewsAsync(operation.Token);
    }

    /// <summary>Runs after IsBusy clears: the capture is usable by now, and the guards on IsBusy would ignore what the user clicks
    /// in it. Cancel still stops this, because the load's command stays running until it ends.</summary>
    private async Task LoadFirstViewsAsync(CancellationToken token)
    {
        IsLoadingFirstViews = true;
        List<Task> background = [];
        void Failed(Exception ex) { if (!_disposed) Status = "The capture is open, but its first views failed to load: " + ex.Message; }
        try
        {
            background.Add(Retention.LoadDominatorRootsAsync(token));
            _updating = true;
            try { SelectedType = Types.FirstOrDefault(); }
            finally { _updating = false; }
            if (SelectedType is not null) background.Add(LoadFlowAsync(SelectedType.Key, token));
            if (IsGrouped) background.Add(LoadGroupsAsync(token));
            await QueryObjectsAsync(token);
            // Start the browser on the biggest owner rather than an arbitrary instance of the largest type.
            // Prefer an application type: a runtime cache is rarely what someone opening a snapshot wants to see first.
            if (Summary?.TopRetainers is { Count: > 0 } owners && _session is not null)
                await Browser.OpenFirstAsync((owners.FirstOrDefault(o => !MemoryLabels.IsFrameworkModule(o.Module)) ?? owners[0]).Id, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed(ex); }
        // Separate from the steps above: these keep running if one of those failed, and a fault here must still be observed.
        try { await Task.WhenAll(background); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed(ex); }
        finally { IsLoadingFirstViews = false; }
        if (token.IsCancellationRequested && !_disposed) Status = "The capture is open. Loading its first views was cancelled; choose a type or object to continue.";
    }

    [RelayCommand(CanExecute = nameof(CanLoad), IncludeCancelCommand = true)]
    private async Task RefreshProcessesAsync(CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            var processes = await profiler.GetProcessesAsync(operation.Token);
            if (_disposed) return;
            var selected = SelectedProcess;
            Processes.Clear(); foreach (var process in processes) Processes.Add(process);
            SelectedProcess = processes.FirstOrDefault(p => p.Id == selected?.Id && p.StartTimeUtcTicks == selected.StartTimeUtcTicks) ?? processes.FirstOrDefault();
            Status = processes.Count == 0 ? "No accessible managed processes found. Start the application or open a full dump." : $"{processes.Count} accessible .NET process(es). Capturing briefly snapshots process memory without forcing a GC.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Status = "Process discovery failed: " + ex.Message; }
    }
    [RelayCommand] private void ToggleOptions() => ShowOptions = !ShowOptions;
    [RelayCommand] private void ToggleGraphFocus() => FocusGraph = !FocusGraph;
    [RelayCommand] private void ToggleBrowser() { if (FocusGraph) FocusGraph = false; else ShowBrowser = !ShowBrowser; }
    [RelayCommand] private void ShowView(string? view) { if (int.TryParse(view, out var index)) SelectedView = Math.Clamp(index, 0, SnapshotsView); }
    [ObservableProperty] public partial string GoToText { get; set; } = "";

    /// <summary>Jumps to an object by hexadecimal address, or filters the type list by name.</summary>
    [RelayCommand]
    private async Task GoToAsync()
    {
        var text = GoToText.Trim();
        if (text.Length == 0 || _session is not { } session) return;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.All(Uri.IsHexDigit) && text.Length >= 8)
        {
            var address = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text : "0x" + text;
            try
            {
                var page = await session.GetObjectsAsync(new(null, address, 0, 5), _lifetime.Token);
                var match = page.Objects.FirstOrDefault(o => o.Address.Equals(address, StringComparison.OrdinalIgnoreCase)) ?? page.Objects.FirstOrDefault();
                if (match is null) { Status = $"No object at {address} in this capture."; return; }
                ShowObject(match.Id); SelectedView = GraphView;
                Status = $"Opened {MemoryLabels.ShortType(match.Type)} at {match.Address}.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status = "Search failed: " + ex.Message; }
            return;
        }
        TypeFilter = text; SelectedView = TypesView;
        if (Types.FirstOrDefault() is { } first) SelectedType = first;
    }

    [RelayCommand] private async Task ChooseDacAsync() { var path = await files.OpenFileAsync("Choose matching runtime DAC", "Runtime DAC|mscordacwks*.dll;mscordaccore*.dll|DLL files|*.dll"); if (path is not null) DacPath = path; }

    [RelayCommand(CanExecute = nameof(CanUseCapture))]
    private void SetBaseline()
    {
        ApplyComparison(CurrentSnapshot, pinned: true);
        Status = "Baseline set. Repeat the workload, then capture again or open a second dump. Comparison uses type counts and bytes, not object addresses.";
    }
    [RelayCommand(CanExecute = nameof(CanClearBaseline))]
    private void ClearBaseline() => ApplyComparison(null, pinned: false);
    private void UpdateComparison()
    {
        ComparisonDescription = _baseline is null ? "Set a baseline, repeat a workload, then capture again to compare type growth."
            : $"Baseline: {_baseline.Source} ({_baseline.CapturedAt.LocalDateTime:t}). " +
                (Summary?.Source != _baseline.Source ? "Different sources are being compared. " : "") +
                (!_baseline.IsComplete || Summary?.IsComplete == false ? "Coverage is incomplete. " : "") +
                "Growth highlights candidates; it does not establish a leak or track individual objects across collections.";
    }

    /// <summary>Exports a shareable HTML report, or the raw investigation as JSON when a .json name is chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanUseCapture))]
    private async Task ExportReportAsync()
    {
        var path = await files.SaveFileAsync("Export memory investigation", "HTML report|*.html|JSON data|*.json", "memory-report.html");
        if (path is null || Summary is not { } summary) return;
        try
        {
            string content;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var report = new { Version = 3, Summary, Baseline = _baseline, Snapshots = Snapshots.Select(s => new { s.Number, s.SourceName, s.Summary.CapturedAt, s.Summary.ManagedBytes, s.Summary.ReachableBytes, s.Summary.ObjectCount }),
                    Comparison = ComparisonRows, Object = Browser.Details, Graph, Estimate = Browser.ReleaseEstimate, Retained = Browser.RetainedComposition, Flow, Groups = InstanceGroups };
                content = JsonSerializer.Serialize(report, IndentedReport);
            }
            else
            {
                var types = Summary.Types.Select(t => new MemoryTypeRow(t, null, null)).ToArray();
                content = MemoryReport.Html(new(summary, CurrentSnapshot?.SourceName ?? SourceName, Findings.ToArray(), types, Snapshots.ToArray(), CurrentSnapshot,
                    HasComparison ? BaselineSnapshot : null, HasComparison ? ComparisonRows : [], ComparisonHeadline));
            }
            await File.WriteAllTextAsync(path, content, _lifetime.Token);
            Status = "Memory investigation exported to " + path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Report export failed: " + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanUseCapture))]
    private async Task CloseCaptureAsync()
    {
        _revision++; CancelSelection(); Browser.ResetHistory();
        var session = _session; _session = null;
        _updating = true;
        try
        {
            Summary = null; Types.Clear(); Objects.Clear(); SelectedType = null; SelectedObject = null;
            Browser.ClearObject(); Graph = null; Flow = null; TotalObjects = 0;
            Retention.Reset(); ResetGroups();
        }
        finally { _updating = false; }
        CurrentSnapshot = null; RefreshSnapshotCards(); RefreshComparison();
        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Memory capture closed, but the profiling host did not shut down cleanly: " + ex.Message; return; }
        }
        Status = "Memory capture closed. The target application keeps running.";
    }

    /// <summary>For a load nobody awaits. Only a scalar changes, so a fault may arrive on any thread.</summary>
    private void ReportFault(Exception fault) { if (!_disposed) Status = fault.Message; }

    private void CancelSelection() { CancelQuery(); Browser.CancelInspection(); CancelFlow(); CancelGraph(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _revision++; Timeline.Stop(); _lifetime.Cancel(); CancelSelection(); ResetGroups();
        if (_session is not null) await _session.DisposeAsync();
        _session = null; _lifetime.Dispose();
    }

    IMemorySession? IMemorySessionHost.Session => _session;
    long IMemorySessionHost.Revision => _revision;
    bool IMemorySessionHost.IsDisposed => _disposed;
    CancellationToken IMemorySessionHost.Lifetime => _lifetime.Token;
    void IMemorySessionHost.ReportStatus(string status) => Status = status;
    void IMemorySessionHost.ReportFault(Exception fault) => ReportFault(fault);
    void IMemorySessionHost.ShowObject(int objectId) => ShowObject(objectId);
    bool IObjectBrowserHost.CanUseCapture => CanUseCapture();
    string? IObjectBrowserHost.KnownTypeOf(int objectId) => Objects.FirstOrDefault(o => o.Id == objectId)?.Type ?? Graph?.Nodes.FirstOrDefault(n => n.Object.Id == objectId)?.Object.Type;
    void IObjectBrowserHost.ClearGraph() { CancelGraph(); Graph = null; }
    void IObjectBrowserHost.ShowGraph(int objectId, MemoryGraph graph) { Graph = graph; _graphObject = objectId; }
    void IObjectBrowserHost.OpenType(string typeKey) => OpenType(typeKey);
}
