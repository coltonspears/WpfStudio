using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>The object browser: navigation history, an expandable field tree read from captured memory, why the
/// object is alive, what it keeps alive, and reference-removal estimates.</summary>
public sealed partial class MemoryProfilerViewModel
{
    private CancellationTokenSource? _inspection;
    private long _inspectionRevision;
    private readonly List<int> _back = [], _forward = [];
    private readonly Dictionary<int, string> _labels = [];
    public ObservableCollection<RootPathRow> RootPaths { get; } = [];
    public ObservableCollection<ObjectNodeViewModel> FieldNodes { get; } = [];
    public ObservableCollection<TrailItem> Trail { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimateReferenceCommand))] public partial MemoryReferenceInfo? SelectedReference { get; set; }
    [ObservableProperty] public partial MemoryReferenceInfo? SelectedIncomingReference { get; set; }
    [ObservableProperty] public partial MemoryReferenceInfo? SelectedOutgoingReference { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimateObjectCommand), nameof(EstimateReferenceCommand))] public partial MemoryObjectDetails? Details { get; set; }
    [ObservableProperty] public partial MemoryReleaseEstimate? ReleaseEstimate { get; set; }
    [ObservableProperty] public partial IReadOnlyList<int> ReleasedObjects { get; set; } = [];
    [ObservableProperty] public partial string ReleaseDescription { get; set; } = "Choose an incoming or outgoing reference to model removing that slot, or estimate releasing all owners of this object.";
    [ObservableProperty] public partial int InspectorTab { get; set; }
    [ObservableProperty] public partial bool IsInspecting { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimateObjectCommand), nameof(EstimateReferenceCommand))] public partial bool IsEstimating { get; set; }
    [ObservableProperty] public partial MemoryRetainedComposition? RetainedComposition { get; set; }
    [ObservableProperty] public partial IReadOnlyList<RetainedTypeRow> RetainedTypes { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<RetainedTypeRow> ReleasedTypes { get; set; } = [];
    [ObservableProperty] public partial string ObjectPreview { get; set; } = "";
    [ObservableProperty] public partial string ObjectShape { get; set; } = "";
    public string OwnSize => Details is { } d ? MemorySize.Format(d.Object.ShallowBytes) : "—";
    public string RetainedSize => Details is { } d ? MemorySize.Format(d.Object.RetainedBytes) : "—";
    public string ObjectTitle => Details is { } d ? MemoryLabels.ShortType(d.Object.Type) : "";
    public string ObjectNamespace => Details is { } d ? MemoryLabels.Namespace(d.Object.Type) + " · " + d.Object.Module : "";
    public string ObjectState => Details is { } d
        ? (d.Object.IsReachable ? "Rooted" : Summary?.IsComplete == true ? "No captured root" : "Reachability unknown") +
            (d.Object.IsPinned ? " · pinned" : "") + " · " + d.Object.Generation : "";
    public string ReachabilityBadge => Details is { } d ? d.Object.IsReachable ? "Alive" : Summary?.IsComplete == true ? "Collectible" : "Unknown" : "";
    public string ReachabilityTone => Details is { } d ? d.Object.IsReachable ? "Accent" : "Success" : "Neutral";
    public string GenerationBadge => Details is { } d ? MemorySize.GenerationShort(d.Object.Generation) : "";
    public bool IsPinnedObject => Details?.Object.IsPinned == true;
    public double RetainedShare => Details is { } d && Summary is { ManagedBytes: > 0 } s ? (double)d.Object.RetainedBytes / s.ManagedBytes : 0;
    public string RetainedShareText => Details is { } d && Summary is { ManagedBytes: > 0 } s
        ? $"{RetainedShare.ToString(RetainedShare >= 0.001 ? "P1" : "P2", CultureInfo.CurrentCulture)} of the heap · keeps {d.Object.RetainedCount:N0} object{(d.Object.RetainedCount == 1 ? "" : "s")} alive" : "";
    public string ReferenceCoverage => Details is { } d ? $"{d.IncomingCount:N0} incoming · {d.OutgoingCount:N0} outgoing. Lists show up to 200 slots; root examples show up to 8 paths." : "";
    public string WhyAliveSummary => Details is not { } d ? "" : !d.Object.IsReachable
        ? (Summary?.IsComplete == true ? "Nothing roots this object. It is garbage that the next collection can free." : "No root path was captured; the capture is incomplete.")
        : RootPaths.Count == 0 ? "Rooted, but no example path was found within the traversal budget."
        : RootPaths.All(p => !p.IsLongLived) ? $"Only short-lived roots ({string.Join(", ", RootPaths.Select(p => p.KindText).Distinct())}) hold this object. It may be released soon."
        : $"Held by {string.Join(" and ", RootPaths.Where(p => p.IsLongLived).Select(p => (p.IsStatic ? "static " : "") + p.Title).Distinct().Take(3))}." +
          (RootPaths.Count(p => p.IsLongLived) > 1 ? " Every path must be broken before it can be collected." : " Break the chain at the field you don't expect to own it.");
    public string EstimateSize => ReleaseEstimate is { } e ? MemorySize.Format(e.ReclaimableBytes) : "—";
    public string EstimateHeading => ReleaseEstimate is { IsComplete: false } ? "Provisional eligible managed bytes" : "Eligible managed bytes";
    public string RetainedHeading => RetainedComposition is { } r ? $"{MemorySize.Format(r.Bytes)} in {r.Count:N0} object{(r.Count == 1 ? "" : "s")}" : "";
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    partial void OnDetailsChanged(MemoryObjectDetails? value)
    {
        foreach (var name in new[] { nameof(OwnSize), nameof(RetainedSize), nameof(ObjectState), nameof(ReferenceCoverage), nameof(ObjectTitle), nameof(ObjectNamespace),
            nameof(ReachabilityBadge), nameof(ReachabilityTone), nameof(GenerationBadge), nameof(IsPinnedObject), nameof(RetainedShare), nameof(RetainedShareText) }) OnPropertyChanged(name);
        RootPaths.Clear(); if (value is not null) foreach (var path in value.RootPaths) RootPaths.Add(RootPathRow.Create(path));
        OnPropertyChanged(nameof(WhyAliveSummary));
        if (value is not null)
        {
            _labels[value.Object.Id] = MemoryLabels.ShortType(value.Object.Type);
            for (var i = 0; i < Trail.Count; i++)
                if (Trail[i].Id == value.Object.Id && Trail[i].Label != _labels[value.Object.Id]) Trail[i] = Trail[i] with { Label = _labels[value.Object.Id] };
        }
    }
    partial void OnReleaseEstimateChanged(MemoryReleaseEstimate? value)
    {
        OnPropertyChanged(nameof(EstimateSize)); OnPropertyChanged(nameof(EstimateHeading));
        var max = value is { Types.Count: > 0 } ? Math.Max(1, value.Types.Max(t => t.Bytes)) : 1;
        ReleasedTypes = value?.Types.Take(30).Select(t => new RetainedTypeRow(t, (double)t.Bytes / max)).ToArray() ?? [];
    }
    partial void OnRetainedCompositionChanged(MemoryRetainedComposition? value)
    {
        OnPropertyChanged(nameof(RetainedHeading));
        var max = value is { Types.Count: > 0 } ? Math.Max(1, value.Types.Max(t => t.Bytes)) : 1;
        RetainedTypes = value?.Types.Take(30).Select(t => new RetainedTypeRow(t, (double)t.Bytes / max)).ToArray() ?? [];
    }
    partial void OnSelectedIncomingReferenceChanged(MemoryReferenceInfo? value) { if (value is not null) SelectedReference = value; }
    partial void OnSelectedOutgoingReferenceChanged(MemoryReferenceInfo? value) { if (value is not null) SelectedReference = value; }

    private bool CanEstimate() => CanUseCapture() && Details is not null && !IsInspecting && !IsEstimating;
    private bool CanEstimateReference() => CanEstimate() && SelectedReference is { IsPermanent: false };

    [RelayCommand] private Task InspectObjectAsync(int objectId, CancellationToken token) => NavigateAsync(objectId, Navigation.New, token);
    [RelayCommand] private async Task BackAsync() { if (_back.Count > 0) await NavigateAsync(_back[^1], Navigation.Back, _lifetime.Token); }
    [RelayCommand] private async Task ForwardAsync() { if (_forward.Count > 0) await NavigateAsync(_forward[^1], Navigation.Forward, _lifetime.Token); }
    [RelayCommand] private Task NavigateTrailAsync(TrailItem? item) => item is null || item.IsCurrent ? Task.CompletedTask : NavigateAsync(item.Id, Navigation.New, _lifetime.Token);
    [RelayCommand] private Task OpenReferenceOwnerAsync(MemoryReferenceInfo? reference) => reference?.FromId is int id ? InspectObjectAsync(id, _lifetime.Token) : Task.CompletedTask;
    [RelayCommand] private Task OpenReferenceTargetAsync(MemoryReferenceInfo? reference) => reference is null ? Task.CompletedTask : InspectObjectAsync(reference.ToId, _lifetime.Token);
    [RelayCommand] private Task OpenFieldObjectAsync(MemoryFieldInfo? field) => field?.ObjectId is int id ? InspectObjectAsync(id, _lifetime.Token) : Task.CompletedTask;
    [RelayCommand] private Task OpenNodeAsync(ObjectNodeViewModel? node) => node?.ObjectId is int id ? InspectObjectAsync(id, _lifetime.Token) : Task.CompletedTask;
    [RelayCommand] private Task OpenPathStepAsync(PathStep? step) => step is null ? Task.CompletedTask : InspectObjectAsync(step.TargetId, _lifetime.Token);
    [RelayCommand] private Task OpenRetainedTypeAsync(RetainedTypeRow? row) { if (row?.Type.TypeKey is string key) OpenType(key); return Task.CompletedTask; }
    [RelayCommand] private void SelectPathStep(PathStep? step) { if (step is not null && step.Reference.ToId == Details?.Object.Id) SelectedReference = step.Reference; }

    private enum Navigation { New, Back, Forward, Replace }

    private async Task NavigateAsync(int objectId, Navigation navigation, CancellationToken token)
    {
        if (_session is not { } session || _disposed || IsBusy) return;
        var current = Details?.Object.Id ?? _pendingObject;
        if (current is int previous && previous != objectId)
        {
            switch (navigation)
            {
                case Navigation.New: _back.Add(previous); _forward.Clear(); break;
                case Navigation.Back: _back.RemoveAt(_back.Count - 1); _forward.Add(previous); break;
                case Navigation.Forward: _forward.RemoveAt(_forward.Count - 1); _back.Add(previous); break;
            }
            if (_back.Count > 50) _back.RemoveAt(0);
        }
        else if (navigation == Navigation.Back && _back.Count > 0) _back.RemoveAt(_back.Count - 1);
        else if (navigation == Navigation.Forward && _forward.Count > 0) _forward.RemoveAt(_forward.Count - 1);
        UpdateTrail(objectId);
        CancelInspection(); CancelGraph();
        _inspection = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var operationToken = _inspection.Token; var revision = _revision; var selection = ++_inspectionRevision;
        _pendingObject = objectId;
        IsInspecting = true; Details = null; Graph = null; SelectedReference = null; SelectedIncomingReference = null; SelectedOutgoingReference = null; ResetEstimate(); ResetBrowserContent();
        try
        {
            var detailTask = session.InspectAsync(objectId, operationToken);
            var graphTask = session.GetGraphAsync(new(objectId, HiddenRootKinds: HiddenRootKinds), operationToken);
            var childrenTask = session.GetChildrenAsync(new(objectId), operationToken);
            var retainedTask = session.GetRetainedAsync(objectId, operationToken);
            await Task.WhenAll(detailTask, graphTask);
            if (_disposed || revision != _revision || selection != _inspectionRevision) return;
            Details = await detailTask; Graph = await graphTask; _graphObject = objectId;
            Status = "Object inspected from captured memory. Fields are read without invoking application getters.";
            try
            {
                var children = await childrenTask;
                if (selection == _inspectionRevision) ApplyRootChildren(children);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { if (selection == _inspectionRevision) ObjectPreview = "Fields are unavailable: " + ex.Message; }
            try
            {
                var retained = await retainedTask;
                if (selection == _inspectionRevision) RetainedComposition = retained;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _revision && selection == _inspectionRevision) Status = "Object inspection failed: " + ex.Message; }
        finally
        {
            if (selection == _inspectionRevision) { IsInspecting = false; _pendingObject = null; NotifyEstimates(); }
            OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward));
        }
    }
    private int? _pendingObject;

    private void UpdateTrail(int current)
    {
        if (Details is { } d) _labels[d.Object.Id] = MemoryLabels.ShortType(d.Object.Type);
        if (!_labels.ContainsKey(current))
            _labels[current] = Objects.FirstOrDefault(o => o.Id == current) is { } known ? MemoryLabels.ShortType(known.Type)
                : Graph?.Nodes.FirstOrDefault(n => n.Object.Id == current)?.Object is { } node ? MemoryLabels.ShortType(node.Type) : "Object";
        var ids = _back.TakeLast(4).Append(current).Concat(_forward.AsEnumerable().Reverse().Take(2)).ToArray();
        Trail.Clear();
        foreach (var id in ids) Trail.Add(new(id, _labels.GetValueOrDefault(id, "Object"), id == current));
        OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward));
    }

    private void ResetHistory() { _back.Clear(); _forward.Clear(); _labels.Clear(); Trail.Clear(); _pendingObject = null; OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward)); }
    private void ResetBrowser() { ResetBrowserContent(); }
    private void ResetBrowserContent() { FieldNodes.Clear(); RetainedComposition = null; ObjectPreview = ""; ObjectShape = ""; }

    private void ApplyRootChildren(MemoryObjectChildren children)
    {
        ObjectPreview = children.Preview; ObjectShape = children.Shape;
        FieldNodes.Clear();
        foreach (var node in NodesFor(children, 0, null)) FieldNodes.Add(node);
    }

    private IEnumerable<ObjectNodeViewModel> NodesFor(MemoryObjectChildren children, int depth, ObjectNodeViewModel? parent)
    {
        foreach (var item in children.Items) yield return new ObjectNodeViewModel(item, LoadNodeChildrenAsync, depth);
        var shown = children.Skip + children.Items.Count(i => i.Kind != "Raw");
        if (children.TotalCount > shown)
            yield return new ObjectNodeViewModel(new MemoryChildItem($"Show more ({children.TotalCount - shown:N0} remaining)", "", "", "More", children.ObjectId), null, depth)
                { NextSkip = shown, MoreParent = parent };
    }

    private async Task LoadNodeChildrenAsync(ObjectNodeViewModel node)
    {
        if (_session is not { } session || node.ObjectId is not int id) return;
        var selection = _inspectionRevision;
        var children = await session.GetChildrenAsync(new(id, Raw: node.IsRaw), _inspection?.Token ?? _lifetime.Token);
        if (selection != _inspectionRevision) return;
        node.ReplaceChildren(NodesFor(children, node.Depth + 1, node));
    }

    [RelayCommand]
    private async Task LoadMoreAsync(ObjectNodeViewModel? more)
    {
        if (more is not { IsMore: true, ObjectId: int id } || _session is not { } session) return;
        var selection = _inspectionRevision;
        var raw = more.MoreParent?.IsRaw == true;
        var children = await session.GetChildrenAsync(new(id, more.NextSkip, Raw: raw), _inspection?.Token ?? _lifetime.Token);
        if (selection != _inspectionRevision) return;
        var target = more.MoreParent?.Children ?? FieldNodes;
        var index = target.IndexOf(more);
        if (index < 0) return;
        target.RemoveAt(index);
        foreach (var node in NodesFor(children, more.Depth, more.MoreParent)) target.Insert(index++, node);
    }

    [RelayCommand(CanExecute = nameof(CanEstimate), IncludeCancelCommand = true)] private Task EstimateObjectAsync(CancellationToken token) => EstimateAsync(null, token);
    [RelayCommand(CanExecute = nameof(CanEstimateReference), IncludeCancelCommand = true)] private Task EstimateReferenceAsync(CancellationToken token) => EstimateAsync(SelectedReference?.Id, token);
    private async Task EstimateAsync(int? referenceId, CancellationToken token)
    {
        if (_session is not { } session || Details is not { } detail) return;
        var revision = _revision; var selection = _inspectionRevision;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _inspection?.Token ?? _lifetime.Token);
        IsEstimating = true; InspectorTab = KeepsAliveTab;
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

    [RelayCommand]
    private void CopyAddress()
    {
        if (Details is not { } d) return;
        try { System.Windows.Clipboard.SetText(d.Object.Address); Status = $"Copied {d.Object.Address}."; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

}
