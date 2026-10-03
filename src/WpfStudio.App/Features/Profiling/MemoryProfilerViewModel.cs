using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Memory investigations are snapshot based. The target is never mutated by an estimate or field inspection.</summary>
public sealed partial class MemoryProfilerViewModel(IMemoryProfiler profiler, IFileDialogService files) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private IMemorySession? _session;
    private CancellationTokenSource? _query, _inspection;
    private HeapSummary? _baseline;
    private readonly Stack<int> _history = new();
    private long _revision, _queryRevision, _inspectionRevision;
    private bool _disposed, _updating;
    private int _skip;
    public ObservableCollection<ProfileProcess> Processes { get; } = [];
    public ObservableCollection<MemoryTypeRow> Types { get; } = [];
    public ObservableCollection<MemoryObjectInfo> Objects { get; } = [];
    public ObservableCollection<RootPathRow> RootPaths { get; } = [];
    public IReadOnlyList<string> TypeSortOptions { get; } = ["Total managed bytes", "Largest retained object", "Growth since baseline"];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CaptureCommand))] public partial ProfileProcess? SelectedProcess { get; set; }
    [ObservableProperty] public partial MemoryTypeRow? SelectedType { get; set; }
    [ObservableProperty] public partial MemoryObjectInfo? SelectedObject { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimateReferenceCommand))] public partial MemoryReferenceInfo? SelectedReference { get; set; }
    [ObservableProperty] public partial MemoryReferenceInfo? SelectedIncomingReference { get; set; }
    [ObservableProperty] public partial MemoryReferenceInfo? SelectedOutgoingReference { get; set; }
    [ObservableProperty] public partial HeapSummary? Summary { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimateObjectCommand), nameof(EstimateReferenceCommand))] public partial MemoryObjectDetails? Details { get; set; }
    [ObservableProperty] public partial MemoryGraph? Graph { get; set; }
    [ObservableProperty] public partial MemoryReleaseEstimate? ReleaseEstimate { get; set; }
    [ObservableProperty] public partial IReadOnlyList<int> ReleasedObjects { get; set; } = [];
    [ObservableProperty] public partial string TypeFilter { get; set; } = "";
    [ObservableProperty] public partial string ObjectFilter { get; set; } = "";
    [ObservableProperty] public partial string TypeSort { get; set; } = "Total managed bytes";
    [ObservableProperty] public partial string Status { get; set; } = "Open a full dump or capture a running .NET application to investigate memory.";
    [ObservableProperty] public partial string BusyMessage { get; set; } = "";
    [ObservableProperty] public partial string ComparisonDescription { get; set; } = "Set a baseline, repeat a workload, then open or capture another snapshot to compare type growth.";
    [ObservableProperty] public partial string ObjectPageDescription { get; set; } = "Choose a type to explore its objects.";
    [ObservableProperty] public partial string ReleaseDescription { get; set; } = "Choose an incoming or outgoing reference to model removing that slot, or estimate releasing all owners of this object.";
    [ObservableProperty] public partial string DacPath { get; set; } = "";
    [ObservableProperty] public partial int RuntimeIndex { get; set; }
    [ObservableProperty] public partial int MaxObjects { get; set; } = 1_000_000;
    [ObservableProperty] public partial int MaxReferences { get; set; } = 6_000_000;
    [ObservableProperty] public partial int InspectorTab { get; set; }
    [ObservableProperty] public partial bool ShowOptions { get; set; }
    [ObservableProperty] public partial bool ShowOverview { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GraphFocusLabel))] public partial bool FocusGraph { get; set; }
    [ObservableProperty] public partial bool IsInspecting { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimateObjectCommand), nameof(EstimateReferenceCommand))] public partial bool IsEstimating { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyCanExecuteChangedFor(nameof(OpenDumpCommand), nameof(CaptureCommand),
        nameof(RefreshProcessesCommand), nameof(SetBaselineCommand), nameof(ClearBaselineCommand), nameof(CloseCaptureCommand),
        nameof(EstimateObjectCommand), nameof(EstimateReferenceCommand), nameof(ExportReportCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviousObjectsCommand), nameof(NextObjectsCommand))] public partial int TotalObjects { get; set; }
    public bool IsIdle => !IsBusy;
    public string GraphFocusLabel => FocusGraph ? "Show inspector" : "Focus map";
    public bool HasCapture => Summary is not null;
    public bool HasBaseline => _baseline is not null;
    public string SourceDescription => Summary is { } s ? $"{s.Runtime} · {s.Architecture} · loaded {s.CapturedAt.LocalDateTime:t}" + (s.IsComplete ? "" : " · incomplete data") : "Managed memory investigations";
    public string CoverageDescription => Summary is { } s
        ? (s.IsComplete ? "Captured heap graph" : "Incomplete heap graph — sizes and estimates are provisional") +
            (s.CoverageNotes.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, s.CoverageNotes) : "") : "";
    public string ManagedSize => Summary is { } s ? MemorySize.Format(s.ManagedBytes) : "—";
    public string OverviewHeading => Summary is { } s ? $"{ManagedSize} managed · {s.ObjectCount:N0} objects · {s.RootCount:N0} roots" +
        (s.IsComplete ? "" : " · incomplete coverage") + (HasBaseline ? " · baseline set" : "") : "Heap overview";
    public string ReachableSize => Summary is { } s ? MemorySize.Format(s.ReachableBytes) : "—";
    public string UnreachableSize => Summary is { } s ? MemorySize.Format(s.UnreachableBytes) : "—";
    public string OwnSize => Details is { } d ? MemorySize.Format(d.Object.ShallowBytes) : "—";
    public string RetainedSize => Details is { } d ? MemorySize.Format(d.Object.RetainedBytes) : "—";
    public string ObjectState => Details is { } d
        ? (d.Object.IsReachable ? "Rooted" : Summary?.IsComplete == true ? "No captured root" : "Reachability unknown") +
            (d.Object.IsPinned ? " · pinned" : "") + " · " + d.Object.Generation : "";
    public string ReferenceCoverage => Details is { } d ? $"{d.IncomingCount:N0} incoming · {d.OutgoingCount:N0} outgoing. Lists show up to 200 slots; root examples show up to 8 paths." : "";
    public string EstimateSize => ReleaseEstimate is { } e ? MemorySize.Format(e.ReclaimableBytes) : "—";
    public string EstimateHeading => ReleaseEstimate is { IsComplete: false } ? "Provisional eligible managed bytes" : "Eligible managed bytes";

    partial void OnSummaryChanged(HeapSummary? value)
    {
        foreach (var name in new[] { nameof(HasCapture), nameof(SourceDescription), nameof(CoverageDescription), nameof(ManagedSize), nameof(OverviewHeading), nameof(ReachableSize), nameof(UnreachableSize) }) OnPropertyChanged(name);
        SetBaselineCommand.NotifyCanExecuteChanged(); CloseCaptureCommand.NotifyCanExecuteChanged(); ExportReportCommand.NotifyCanExecuteChanged();
    }
    partial void OnDetailsChanged(MemoryObjectDetails? value)
    {
        foreach (var name in new[] { nameof(OwnSize), nameof(RetainedSize), nameof(ObjectState), nameof(ReferenceCoverage) }) OnPropertyChanged(name);
        RootPaths.Clear(); if (value is not null) foreach (var path in value.RootPaths) RootPaths.Add(RootPathRow.Create(path));
    }
    partial void OnReleaseEstimateChanged(MemoryReleaseEstimate? value)
    { OnPropertyChanged(nameof(EstimateSize)); OnPropertyChanged(nameof(EstimateHeading)); }
    partial void OnSelectedTypeChanged(MemoryTypeRow? value) { if (!_updating) { _skip = 0; QueueQuery(); } }
    partial void OnObjectFilterChanged(string value) { if (!_updating) { _skip = 0; QueueQuery(); } }
    partial void OnTypeFilterChanged(string value) => RefreshTypes();
    partial void OnTypeSortChanged(string value) => RefreshTypes();
    partial void OnSelectedObjectChanged(MemoryObjectInfo? value) { if (!_updating && value is not null) _ = InspectObjectAsync(value.Id, _lifetime.Token); }
    partial void OnSelectedIncomingReferenceChanged(MemoryReferenceInfo? value) { if (value is not null) SelectedReference = value; }
    partial void OnSelectedOutgoingReferenceChanged(MemoryReferenceInfo? value) { if (value is not null) SelectedReference = value; }
    private bool CanLoad() => !_disposed && !IsBusy;
    private bool CanCapture() => CanLoad() && SelectedProcess is not null;
    private bool CanUseCapture() => CanLoad() && _session is not null;
    private bool CanClearBaseline() => CanLoad() && HasBaseline;
    private bool CanEstimate() => CanUseCapture() && Details is not null && !IsInspecting && !IsEstimating;
    private bool CanEstimateReference() => CanEstimate() && SelectedReference is { IsPermanent: false };

    [RelayCommand(CanExecute = nameof(CanLoad), IncludeCancelCommand = true)]
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
        IsBusy = true; BusyMessage = request.DumpPath is null ? "Capturing an immutable heap snapshot…" : "Reading objects, references, and GC roots…";
        Status = "Analyzing memory in an isolated worker. Cancel stops this analysis.";
        try
        {
            pending = await profiler.OpenAsync(request, operation.Token);
            if (_disposed) return;
            var old = _session; _session = pending; pending = null;
            _revision++; CancelSelection(); _history.Clear(); _skip = 0;
            _updating = true;
            try { Objects.Clear(); SelectedObject = null; SelectedType = null; Details = null; Graph = null; ResetEstimate(); Summary = _session.Summary; }
            finally { _updating = false; }
            if (old is not null) await old.DisposeAsync();
            RefreshTypes(); UpdateComparison();
            Status = Summary!.IsComplete ? $"{Summary.ObjectCount:N0} objects and {Summary.RootCount:N0} roots analyzed. Select a type or explore a large retained object."
                : "Analysis completed with incomplete heap coverage. Read the coverage notes before interpreting estimates.";
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Memory capture cancelled. The previous capture remains available."; }
        catch (Exception ex) { if (!_disposed) Status = "Memory capture failed: " + ex.Message; }
        finally
        {
            if (pending is not null) await pending.DisposeAsync();
            IsBusy = false; BusyMessage = "";
        }
        if (_session is not null && !_disposed && Summary is not null && SelectedType is null)
        {
            _updating = true; SelectedType = Types.FirstOrDefault(); _updating = false;
            await QueryObjectsAsync(_lifetime.Token);
        }
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
    [RelayCommand] private async Task ChooseDacAsync() { var path = await files.OpenFileAsync("Choose matching runtime DAC", "Runtime DAC|mscordacwks*.dll;mscordaccore*.dll|DLL files|*.dll"); if (path is not null) DacPath = path; }

    [RelayCommand(CanExecute = nameof(CanUseCapture))]
    private void SetBaseline()
    {
        _baseline = Summary; RefreshTypes(); UpdateComparison();
        OnPropertyChanged(nameof(HasBaseline)); OnPropertyChanged(nameof(OverviewHeading)); ClearBaselineCommand.NotifyCanExecuteChanged();
        Status = "Baseline set. Repeat the workload, then capture again or open a second dump. Comparison uses type counts and bytes, not object addresses.";
    }
    [RelayCommand(CanExecute = nameof(CanClearBaseline))]
    private void ClearBaseline()
    { _baseline = null; RefreshTypes(); UpdateComparison(); OnPropertyChanged(nameof(HasBaseline)); OnPropertyChanged(nameof(OverviewHeading)); ClearBaselineCommand.NotifyCanExecuteChanged(); }
    private void UpdateComparison()
    {
        ComparisonDescription = _baseline is null ? "Set a baseline, repeat a workload, then capture again to compare type growth."
            : $"Baseline: {_baseline.Source} ({_baseline.CapturedAt.LocalDateTime:t}). " +
                (Summary?.Source != _baseline.Source ? "Different sources are being compared. " : "") +
                (!_baseline.IsComplete || Summary?.IsComplete == false ? "Coverage is incomplete. " : "") +
                "Growth highlights candidates; it does not establish a leak or track individual objects across collections.";
    }
    private void RefreshTypes()
    {
        if (_disposed) return;
        var baseline = _baseline?.Types.ToDictionary(t => t.Key, StringComparer.Ordinal);
        // Include types present only in the baseline so objects that disappeared are visible as negative deltas.
        var current = Summary?.Types.ToDictionary(t => t.Key, StringComparer.Ordinal) ?? [];
        var keys = current.Keys.Concat(baseline?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal);
        var rows = keys.Select(key =>
        {
            MemoryTypeSummary? previous = null;
            if (baseline is not null) baseline.TryGetValue(key, out previous);
            current.TryGetValue(key, out var type);
            type ??= new(key, previous!.Name, previous.Module, 0, 0, 0, 0);
            return new MemoryTypeRow(type, baseline is null ? null : previous?.Count ?? 0, baseline is null ? null : previous?.Bytes ?? 0);
        }).Where(row => (row.Name + " " + row.Module).Contains(TypeFilter, StringComparison.OrdinalIgnoreCase));
        rows = TypeSort switch { "Largest retained object" => rows.OrderByDescending(r => r.LargestRetainedBytes),
            "Growth since baseline" => rows.OrderByDescending(r => r.BytesDelta ?? 0), _ => rows.OrderByDescending(r => r.Bytes) };
        var selectedKey = SelectedType?.Key;
        _updating = true;
        try { Types.Clear(); foreach (var row in rows.Take(5000)) Types.Add(row); SelectedType = Types.FirstOrDefault(r => r.Key == selectedKey); }
        finally { _updating = false; }
        if (selectedKey is not null && SelectedType is null) { _skip = 0; QueueQuery(); }
    }

    private void QueueQuery() { _ = QueryObjectsAsync(_lifetime.Token); }
    private async Task QueryObjectsAsync(CancellationToken token)
    {
        if (_session is not { } session || _disposed || IsBusy) return;
        _query?.Cancel(); _query?.Dispose(); _query = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var queryToken = _query.Token; var revision = _revision; var queryRevision = ++_queryRevision;
        CancelInspection();
        Details = null; Graph = null; SelectedReference = null; SelectedIncomingReference = null; SelectedOutgoingReference = null; ResetEstimate(); NotifyEstimates();
        try
        {
            var page = await session.GetObjectsAsync(new(SelectedType?.Key, ObjectFilter, _skip), queryToken);
            if (_disposed || revision != _revision || queryRevision != _queryRevision) return;
            _updating = true;
            try { Objects.Clear(); foreach (var obj in page.Objects) Objects.Add(obj); TotalObjects = page.TotalCount; SelectedObject = null; }
            finally { _updating = false; }
            ObjectPageDescription = page.Objects.Count == 0 ? "No matching objects in this capture."
                : $"{_skip + 1:N0}–{_skip + page.Objects.Count:N0} of {page.TotalCount:N0} objects · sorted by retained bytes";
            PreviousObjectsCommand.NotifyCanExecuteChanged(); NextObjectsCommand.NotifyCanExecuteChanged();
            SelectedObject = Objects.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _revision && queryRevision == _queryRevision) Status = "Object search failed: " + ex.Message; }
    }
    private bool CanPrevious() => CanUseCapture() && _skip > 0;
    private bool CanNext() => CanUseCapture() && _skip + 200 < TotalObjects;
    [RelayCommand(CanExecute = nameof(CanPrevious))] private async Task PreviousObjectsAsync() { _skip = Math.Max(0, _skip - 200); await QueryObjectsAsync(_lifetime.Token); }
    [RelayCommand(CanExecute = nameof(CanNext))] private async Task NextObjectsAsync() { _skip += 200; await QueryObjectsAsync(_lifetime.Token); }
    [RelayCommand] private void ShowAllTypes() { _updating = true; SelectedType = null; ObjectFilter = ""; _updating = false; _skip = 0; QueueQuery(); }

    [RelayCommand]
    private async Task InspectObjectAsync(int objectId, CancellationToken token)
    {
        if (_session is not { } session || _disposed || IsBusy) return;
        if (Details?.Object.Id is int previous && previous != objectId) _history.Push(previous);
        CancelInspection();
        _inspection = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var operationToken = _inspection.Token; var revision = _revision; var selection = ++_inspectionRevision;
        IsInspecting = true; Details = null; Graph = null; SelectedReference = null; SelectedIncomingReference = null; SelectedOutgoingReference = null; ResetEstimate();
        try
        {
            var detailTask = session.InspectAsync(objectId, operationToken);
            var graphTask = session.GetGraphAsync(new(objectId), operationToken);
            await Task.WhenAll(detailTask, graphTask);
            if (_disposed || revision != _revision || selection != _inspectionRevision) return;
            Details = await detailTask; Graph = await graphTask;
            Status = "Object inspected from captured memory. Fields are read without invoking application getters.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _revision && selection == _inspectionRevision) Status = "Object inspection failed: " + ex.Message; }
        finally { if (selection == _inspectionRevision) { IsInspecting = false; NotifyEstimates(); } }
    }
    [RelayCommand] private async Task BackAsync() { if (_history.TryPop(out var id)) { Details = null; await InspectObjectAsync(id, _lifetime.Token); } }
    [RelayCommand] private Task OpenReferenceOwnerAsync(MemoryReferenceInfo? reference) => reference?.FromId is int id ? InspectObjectAsync(id, _lifetime.Token) : Task.CompletedTask;
    [RelayCommand] private Task OpenReferenceTargetAsync(MemoryReferenceInfo? reference) => reference is null ? Task.CompletedTask : InspectObjectAsync(reference.ToId, _lifetime.Token);
    [RelayCommand] private Task OpenFieldObjectAsync(MemoryFieldInfo? field) => field?.ObjectId is int id ? InspectObjectAsync(id, _lifetime.Token) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEstimate), IncludeCancelCommand = true)] private Task EstimateObjectAsync(CancellationToken token) => EstimateAsync(null, token);
    [RelayCommand(CanExecute = nameof(CanEstimateReference), IncludeCancelCommand = true)] private Task EstimateReferenceAsync(CancellationToken token) => EstimateAsync(SelectedReference?.Id, token);
    private async Task EstimateAsync(int? referenceId, CancellationToken token)
    {
        if (_session is not { } session || Details is not { } detail) return;
        var revision = _revision; var selection = _inspectionRevision;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _inspection?.Token ?? _lifetime.Token);
        IsEstimating = true; InspectorTab = 3;
        try
        {
            var estimate = await session.EstimateReleaseAsync(new(detail.Object.Id, referenceId), operation.Token);
            if (_disposed || revision != _revision || selection != _inspectionRevision) return;
            ReleaseEstimate = estimate; ReleasedObjects = estimate.ObjectSample;
            ReleaseDescription = (referenceId is null ? "All owners removed: " : $"Reference slot {referenceId} removed: ") + estimate.Explanation;
            Status = estimate.SelectedObjectRemainsReachable ? "The object remains rooted. A surviving root path is shown in the estimate." : $"{estimate.ReclaimableObjects:N0} objects become eligible in the modeled graph.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _revision && selection == _inspectionRevision) Status = "Reference-removal estimate failed: " + ex.Message; }
        finally { IsEstimating = false; }
    }
    private void ResetEstimate() { ReleaseEstimate = null; ReleasedObjects = []; ReleaseDescription = "Choose a reference slot, or estimate removing all owners of the selected object. This does not change the running app."; }
    private void NotifyEstimates() { EstimateObjectCommand.NotifyCanExecuteChanged(); EstimateReferenceCommand.NotifyCanExecuteChanged(); }
    private void CancelInspection() { _inspectionRevision++; _inspection?.Cancel(); _inspection?.Dispose(); _inspection = null; IsInspecting = false; }
    private void CancelSelection() { _queryRevision++; _query?.Cancel(); _query?.Dispose(); _query = null; CancelInspection(); }

    [RelayCommand(CanExecute = nameof(CanUseCapture))]
    private async Task ExportReportAsync()
    {
        var path = await files.SaveFileAsync("Export memory investigation", "JSON reports|*.json", "memory-investigation.json");
        if (path is null) return;
        try
        {
            var report = new { Version = 1, Summary, Baseline = _baseline, Object = Details, Graph, Estimate = ReleaseEstimate };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), _lifetime.Token);
            Status = "Memory investigation exported to " + path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Report export failed: " + ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanUseCapture))]
    private async Task CloseCaptureAsync()
    {
        _revision++; CancelSelection();
        var session = _session; _session = null;
        _updating = true;
        try { Summary = null; Types.Clear(); Objects.Clear(); SelectedType = null; SelectedObject = null; SelectedReference = null; SelectedIncomingReference = null; SelectedOutgoingReference = null; Details = null; Graph = null; ResetEstimate(); TotalObjects = 0; }
        finally { _updating = false; }
        if (session is not null) await session.DisposeAsync();
        Status = "Memory capture closed. The target application keeps running.";
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _revision++; _lifetime.Cancel(); CancelSelection();
        if (_session is not null) await _session.DisposeAsync();
        _session = null; _lifetime.Dispose();
    }
}
