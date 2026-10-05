using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Memory investigations are snapshot based. The target is never mutated by an estimate or field inspection.
/// This file owns capture lifetime; the Overview, Types, Retention, Graph and Browser partials own their views.</summary>
public sealed partial class MemoryProfilerViewModel(IMemoryProfiler profiler, IFileDialogService files, IProcessMemorySampler? sampler = null) : ObservableObject, IAsyncDisposable
{
    private readonly IProcessMemorySampler _sampler = sampler ?? new ProcessMemorySampler();
    public const int OverviewView = 0, TypesView = 1, RetentionView = 2, GraphView = 3;
    public const int FieldsTab = 0, RootsTab = 1, KeepsAliveTab = 2, ReferencesTab = 3;
    private readonly CancellationTokenSource _lifetime = new();
    private IMemorySession? _session;
    private HeapSummary? _baseline;
    private long _revision;
    private bool _disposed, _updating;
    public ObservableCollection<ProfileProcess> Processes { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CaptureCommand))] public partial ProfileProcess? SelectedProcess { get; set; }
    [ObservableProperty] public partial HeapSummary? Summary { get; set; }
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
        nameof(EstimateObjectCommand), nameof(EstimateReferenceCommand), nameof(ExportReportCommand))]
    public partial bool IsBusy { get; set; }
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

    partial void OnSummaryChanged(HeapSummary? value)
    {
        foreach (var name in new[] { nameof(HasCapture), nameof(SourceDescription), nameof(SourceName), nameof(CoverageDescription), nameof(IsIncomplete),
            nameof(ManagedSize), nameof(ReachableSize), nameof(UnreachableSize), nameof(IsBrowserVisible) }) OnPropertyChanged(name);
        SetBaselineCommand.NotifyCanExecuteChanged(); CloseCaptureCommand.NotifyCanExecuteChanged(); ExportReportCommand.NotifyCanExecuteChanged();
        RefreshOverview();
    }
    partial void OnFocusGraphChanged(bool value) { if (value) SelectedView = GraphView; }

    private bool CanLoad() => !_disposed && !IsBusy;
    private bool CanCapture() => CanLoad() && SelectedProcess is not null;
    private bool CanUseCapture() => CanLoad() && _session is not null;
    private bool CanClearBaseline() => CanLoad() && HasBaseline;

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
        PrepareSnapshotSource(request);
        Status = "Analyzing memory in an isolated worker. Cancel stops this analysis.";
        try
        {
            pending = await profiler.OpenAsync(request, operation.Token);
            if (_disposed) return;
            var old = _session; _session = pending; pending = null;
            _revision++; CancelSelection(); ResetHistory(); _skip = 0;
            _updating = true;
            try
            {
                Objects.Clear(); SelectedObject = null; SelectedType = null; Details = null; Graph = null; Flow = null; ResetEstimate(); ResetBrowser();
                DominatorRoots.Clear(); RetentionMapItems = []; RetentionTrail.Clear(); Sunburst = null; ResetGroups(); Summary = _session.Summary;
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
        if (_session is not null && !_disposed && Summary is not null && SelectedType is null)
        {
            _ = LoadDominatorRootsAsync(_lifetime.Token);
            _updating = true; SelectedType = Types.FirstOrDefault(); _updating = false;
            if (SelectedType is not null) _ = LoadFlowAsync(SelectedType.Key, _lifetime.Token);
            if (IsGrouped) _ = LoadGroupsAsync(_lifetime.Token);
            await QueryObjectsAsync(_lifetime.Token);
            // Start the browser on the biggest owner rather than an arbitrary instance of the largest type.
            // Prefer an application type: a runtime cache is rarely what someone opening a snapshot wants to see first.
            if (Summary?.TopRetainers is { Count: > 0 } owners && _session is not null)
                await NavigateAsync((owners.FirstOrDefault(o => !MemoryLabels.IsFrameworkModule(o.Module)) ?? owners[0]).Id, Navigation.Replace, _lifetime.Token);
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
                    Comparison = ComparisonRows, Object = Details, Graph, Estimate = ReleaseEstimate, Retained = RetainedComposition, Flow, Groups = InstanceGroups };
                content = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
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
        _revision++; CancelSelection(); ResetHistory();
        var session = _session; _session = null;
        _updating = true;
        try
        {
            Summary = null; Types.Clear(); Objects.Clear(); SelectedType = null; SelectedObject = null; SelectedReference = null; SelectedIncomingReference = null;
            SelectedOutgoingReference = null; Details = null; Graph = null; Flow = null; ResetEstimate(); ResetBrowser(); TotalObjects = 0;
            DominatorRoots.Clear(); RetentionMapItems = []; RetentionTrail.Clear(); Sunburst = null; ResetGroups();
        }
        finally { _updating = false; }
        CurrentSnapshot = null; RefreshSnapshotCards(); RefreshComparison();
        if (session is not null) await session.DisposeAsync();
        Status = "Memory capture closed. The target application keeps running.";
    }

    private void CancelSelection() { CancelQuery(); CancelInspection(); CancelFlow(); CancelGraph(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _revision++; StopMonitor(); _lifetime.Cancel(); CancelSelection();
        if (_sampler is IDisposable owned && sampler is null) owned.Dispose();
        if (_session is not null) await _session.DisposeAsync();
        _session = null; _lifetime.Dispose();
    }
}
