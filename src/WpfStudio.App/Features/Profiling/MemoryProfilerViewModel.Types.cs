using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>The Types view: a grouped, sortable type list, the selected type's retention paths (Sankey) and its instances.</summary>
public sealed partial class MemoryProfilerViewModel
{
    private CancellationTokenSource? _query, _flow;
    private long _queryRevision, _flowRevision;
    private int _skip;
    public ObservableCollection<MemoryTypeRow> Types { get; } = [];
    public ObservableCollection<MemoryObjectInfo> Objects { get; } = [];
    public IReadOnlyList<string> TypeSortOptions { get; } = ["Total managed bytes", "Retained bytes", "Largest retained object", "Object count", "Growth since baseline"];
    public IReadOnlyList<string> TypeGroupings { get; } = ["No grouping", "Namespace", "Assembly"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedType), nameof(SelectedTypeTitle), nameof(SelectedTypeDetail))] public partial MemoryTypeRow? SelectedType { get; set; }
    [ObservableProperty] public partial MemoryObjectInfo? SelectedObject { get; set; }
    [ObservableProperty] public partial string TypeFilter { get; set; } = "";
    [ObservableProperty] public partial string ObjectFilter { get; set; } = "";
    [ObservableProperty] public partial string TypeSort { get; set; } = "Total managed bytes";
    [ObservableProperty] public partial string TypeGrouping { get; set; } = "No grouping";
    [ObservableProperty] public partial bool OnlyApplicationTypes { get; set; }
    [ObservableProperty] public partial string ObjectPageDescription { get; set; } = "Choose a type to explore its objects.";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviousObjectsCommand), nameof(NextObjectsCommand))] public partial int TotalObjects { get; set; }
    [ObservableProperty] public partial MemoryRetentionFlow? Flow { get; set; }
    [ObservableProperty] public partial bool FlowUsesBytes { get; set; }
    [ObservableProperty] public partial bool IsLoadingFlow { get; set; }
    [ObservableProperty] public partial string FlowDescription { get; set; } = "";
    /// <summary>Retention paths (0) or instances (1) for the selected type.</summary>
    [ObservableProperty] public partial int TypeDetailTab { get; set; }
    public bool HasSelectedType => SelectedType is not null;
    public string SelectedTypeTitle => SelectedType?.ShortName ?? "";
    public string SelectedTypeDetail => SelectedType is { } t
        ? $"{t.Namespace} · {t.Module} · {t.Count:N0} objects ({t.UnreachableCount:N0} collectible) · own {t.BytesText} · retains {t.RetainedText}" + (t.HasBaseline ? $" · {t.Growth} since baseline" : "")
        : "";
    public long MaxObjectRetained => Objects.Count == 0 ? 1 : Math.Max(1, Objects.Max(o => o.RetainedBytes));

    partial void OnSelectedTypeChanged(MemoryTypeRow? value)
    {
        if (_updating) return;
        _skip = 0; QueueQuery();
        if (value is not null) _ = LoadFlowAsync(value.Key, _lifetime.Token); else { CancelFlow(); Flow = null; }
    }
    partial void OnObjectFilterChanged(string value) { if (!_updating) { _skip = 0; QueueQuery(); } }
    partial void OnTypeFilterChanged(string value) => RefreshTypes();
    partial void OnTypeSortChanged(string value) => RefreshTypes();
    partial void OnTypeGroupingChanged(string value) => RefreshTypes();
    partial void OnOnlyApplicationTypesChanged(bool value) => RefreshTypes();
    partial void OnFlowUsesBytesChanged(bool value) => DescribeFlow();
    partial void OnSelectedObjectChanged(MemoryObjectInfo? value) { if (!_updating && value is not null) _ = InspectObjectAsync(value.Id, _lifetime.Token); }

    private void RefreshTypes()
    {
        if (_disposed) return;
        var baseline = _baseline?.Types.ToDictionary(t => t.Key, StringComparer.Ordinal);
        // Include types present only in the baseline so objects that disappeared are visible as negative deltas.
        var current = Summary?.Types.ToDictionary(t => t.Key, StringComparer.Ordinal) ?? [];
        var keys = current.Keys.Concat(baseline?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal);
        var maxBytes = current.Count == 0 ? 0 : current.Values.Max(t => t.Bytes);
        var maxRetained = current.Count == 0 ? 0 : current.Values.Max(t => t.RetainedBytes);
        var summaries = keys.Select(key =>
        {
            MemoryTypeSummary? previous = null;
            if (baseline is not null) baseline.TryGetValue(key, out previous);
            current.TryGetValue(key, out var type);
            type ??= new(key, previous!.Name, previous.Module, 0, 0, 0, 0);
            return (Type: type, Previous: previous);
        }).Where(x => (x.Type.Name + " " + x.Type.Module).Contains(TypeFilter, StringComparison.OrdinalIgnoreCase))
          .Where(x => !OnlyApplicationTypes || !MemoryLabels.IsFrameworkModule(x.Type.Module)).ToArray();
        var maxDelta = baseline is null || summaries.Length == 0 ? 0 : summaries.Max(x => Math.Abs(x.Type.Bytes - (x.Previous?.Bytes ?? 0)));
        var rows = summaries.Select(x => new MemoryTypeRow(x.Type, baseline is null ? null : x.Previous?.Count ?? 0, baseline is null ? null : x.Previous?.Bytes ?? 0,
            maxBytes, maxRetained, maxDelta, TypeGrouping switch { "Namespace" => MemoryLabels.Namespace(x.Type.Name), "Assembly" => x.Type.Module, _ => "" }));
        rows = TypeSort switch
        {
            "Retained bytes" => rows.OrderByDescending(r => r.RetainedBytes),
            "Largest retained object" => rows.OrderByDescending(r => r.LargestRetainedBytes),
            "Object count" => rows.OrderByDescending(r => r.Count),
            "Growth since baseline" => rows.OrderByDescending(r => r.BytesDelta ?? 0),
            _ => rows.OrderByDescending(r => r.Bytes)
        };
        if (TypeGrouping != "No grouping")
        {
            // Groups are ordered by their total so the biggest namespace or assembly comes first.
            var list = rows.ToList();
            var totals = list.GroupBy(r => r.Group).ToDictionary(g => g.Key, g => g.Sum(r => r.Bytes));
            rows = list.Select((row, index) => (Row: row, Index: index)).OrderByDescending(x => totals[x.Row.Group])
                .ThenBy(x => x.Row.Group, StringComparer.Ordinal).ThenBy(x => x.Index).Select(x => x.Row);
        }
        var selectedKey = SelectedType?.Key;
        _updating = true;
        try { Types.Clear(); foreach (var row in rows.Take(5000)) Types.Add(row); SelectedType = Types.FirstOrDefault(r => r.Key == selectedKey); }
        finally { _updating = false; }
        if (selectedKey is not null && SelectedType is null) { _skip = 0; QueueQuery(); CancelFlow(); Flow = null; }
        OnPropertyChanged(nameof(SelectedTypeDetail)); OnPropertyChanged(nameof(HasSelectedType)); OnPropertyChanged(nameof(SelectedTypeTitle));
        TypesRefreshed?.Invoke();
    }

    /// <summary>Raised after the type list is rebuilt, so the view can re-apply grouping.</summary>
    public event Action? TypesRefreshed;

    private void QueueQuery() { _ = QueryObjectsAsync(_lifetime.Token); }
    private void CancelQuery() { _queryRevision++; _query?.Cancel(); _query?.Dispose(); _query = null; }

    private async Task QueryObjectsAsync(CancellationToken token)
    {
        if (_session is not { } session || _disposed || IsBusy) return;
        _query?.Cancel(); _query?.Dispose(); _query = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var queryToken = _query.Token; var revision = _revision; var queryRevision = ++_queryRevision;
        CancelInspection();
        Details = null; Graph = null; SelectedReference = null; SelectedIncomingReference = null; SelectedOutgoingReference = null; ResetEstimate(); ResetBrowserContent(); NotifyEstimates();
        try
        {
            var page = await session.GetObjectsAsync(new(SelectedType?.Key, ObjectFilter, _skip), queryToken);
            if (_disposed || revision != _revision || queryRevision != _queryRevision) return;
            _updating = true;
            try { Objects.Clear(); foreach (var obj in page.Objects) Objects.Add(obj); TotalObjects = page.TotalCount; SelectedObject = null; }
            finally { _updating = false; }
            OnPropertyChanged(nameof(MaxObjectRetained));
            ObjectPageDescription = page.Objects.Count == 0 ? "No matching objects in this capture."
                : $"{_skip + 1:N0}–{_skip + page.Objects.Count:N0} of {page.TotalCount:N0} objects · largest retained first";
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
    [RelayCommand] private void ShowAllTypes() { _updating = true; SelectedType = null; ObjectFilter = ""; _updating = false; _skip = 0; CancelFlow(); Flow = null; QueueQuery(); }

    private void CancelFlow() { _flowRevision++; _flow?.Cancel(); _flow?.Dispose(); _flow = null; IsLoadingFlow = false; }

    private async Task LoadFlowAsync(string typeKey, CancellationToken token)
    {
        if (_session is not { } session || _disposed) return;
        CancelFlow();
        _flow = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var flowToken = _flow.Token; var revision = _revision; var flowRevision = _flowRevision;
        IsLoadingFlow = true;
        try
        {
            var flow = await session.GetRetentionFlowAsync(new(TypeKey: typeKey, HiddenRootKinds: HiddenRootKinds), flowToken);
            if (_disposed || revision != _revision || flowRevision != _flowRevision) return;
            Flow = flow; DescribeFlow();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _revision && flowRevision == _flowRevision) { Flow = null; FlowDescription = "Retention paths are unavailable: " + ex.Message; } }
        finally { if (flowRevision == _flowRevision) IsLoadingFlow = false; }
    }

    private void DescribeFlow()
    {
        if (Flow is not { } flow) { FlowDescription = ""; return; }
        var roots = flow.Nodes.Where(n => n.Kind is "Root" or "Static").OrderByDescending(n => n.Count).ThenByDescending(n => n.Bytes).ToArray();
        string Name(MemoryFlowNode n) => (n.Kind == "Static" ? "static field " : "") + n.Label;
        string Share(MemoryFlowNode n) => n.Count == flow.Sampled ? (flow.Sampled == 1 ? "" : $" (all {flow.Sampled:N0})") : $" ({n.Count:N0} of {flow.Sampled:N0})";
        FlowDescription = flow.Nodes.Count <= 1 ? "No retention paths." :
            (roots.Length == 0 ? $"None of the {flow.Instances:N0} instance(s) has a GC root path."
                : "Held by " + string.Join(roots.Length == 2 ? " and " : ", ", roots.Take(3).Select(n => Name(n) + Share(n))) + (roots.Length > 3 ? $" and {roots.Length - 3} more roots" : "") + ".") +
            (roots.Length > 1 && roots.Sum(r => r.Count) > flow.Sampled ? " Instances held through several roots appear on each path; all of them must let go." : "") +
            (flow.Unrooted > 0 ? $" {flow.Unrooted:N0} {(flow.Unrooted == 1 ? "is" : "are")} already collectible." : "") +
            (flow.Sampled < flow.Instances ? $" Showing the {flow.Sampled:N0} largest instances." : "");
    }

    [RelayCommand]
    private void SelectFlowNode(MemoryFlowNode? node)
    {
        if (node?.SampleObjectId is int id) ShowObject(id);
    }

    [RelayCommand]
    private void OpenFlowNode(MemoryFlowNode? node)
    {
        if (node is null) return;
        if (node.Kind == "Owner" && node.TypeKey is string key && key != SelectedType?.Key) { OpenType(key); return; }
        if (node.SampleObjectId is int id) { ShowObject(id); SelectedView = GraphView; }
    }
}
