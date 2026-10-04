using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>The retention graph: the browser's object with its root paths, owners and references, expanded on demand.
/// Short-lived roots (finalizer queue, stacks) can be hidden so static fields and handles stand out.</summary>
public sealed partial class MemoryProfilerViewModel
{
    private CancellationTokenSource? _graphLoad;
    private long _graphRevision;
    private int? _graphObject;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GraphSummary))] public partial MemoryGraph? Graph { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GraphSummary))] public partial bool ShowFinalizerRoots { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GraphSummary))] public partial bool ShowStackRoots { get; set; } = true;
    [ObservableProperty] public partial bool ShowMinimap { get; set; } = true;
    [ObservableProperty] public partial int? GraphSelectedObjectId { get; set; }
    [ObservableProperty] public partial bool IsExpandingGraph { get; set; }

    public IReadOnlyList<string>? HiddenRootKinds
    {
        get
        {
            var hidden = new List<string>();
            if (!ShowFinalizerRoots) hidden.Add("FinalizerQueue");
            if (!ShowStackRoots) hidden.Add("Stack");
            return hidden.Count == 0 ? null : hidden;
        }
    }

    public string GraphSummary => Graph is not { } graph ? "" :
        $"{graph.Nodes.Count:N0} objects · {graph.References.Count:N0} references" +
        (HiddenRootKinds is { } hidden ? $" · {string.Join(" and ", hidden.Select(MemoryLabels.RootKindName).Select(k => k.ToLowerInvariant()))} roots hidden" : "") +
        (graph.IsTruncated ? " · more neighbours available" : "");

    partial void OnShowFinalizerRootsChanged(bool value) => ReloadForRootFilter();
    partial void OnShowStackRootsChanged(bool value) => ReloadForRootFilter();

    private void ReloadForRootFilter()
    {
        if (_session is null) return;
        if (Details?.Object.Id is int id) _ = ReloadGraphAsync(id, _lifetime.Token);
        if (SelectedType is { } type) _ = LoadFlowAsync(type.Key, _lifetime.Token);
    }

    private void CancelGraph() { _graphRevision++; _graphLoad?.Cancel(); _graphLoad?.Dispose(); _graphLoad = null; IsExpandingGraph = false; }

    [RelayCommand]
    private Task ResetGraphAsync() => Details?.Object.Id is int id ? ReloadGraphAsync(id, _lifetime.Token) : Task.CompletedTask;

    private async Task ReloadGraphAsync(int objectId, CancellationToken token)
    {
        if (_session is not { } session || _disposed) return;
        CancelGraph();
        _graphLoad = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var revision = _revision; var graphRevision = _graphRevision;
        try
        {
            var graph = await session.GetGraphAsync(new(objectId, HiddenRootKinds: HiddenRootKinds), _graphLoad.Token);
            if (_disposed || revision != _revision || graphRevision != _graphRevision || Details?.Object.Id != objectId) return;
            Graph = graph; _graphObject = objectId;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && graphRevision == _graphRevision) Status = "Graph update failed: " + ex.Message; }
    }

    /// <summary>Adds one hop of owners or referenced objects to the displayed graph, keeping the current layout anchor.</summary>
    [RelayCommand]
    private async Task ExpandGraphAsync(GraphExpandRequest? request)
    {
        if (request is null || _session is not { } session || Graph is not { } current) return;
        var revision = _revision; var graphRevision = _graphRevision; var anchor = _graphObject;
        IsExpandingGraph = true;
        try
        {
            var more = await session.GetNeighborsAsync(new(request.ObjectId, request.Incoming), _graphLoad?.Token ?? _lifetime.Token);
            if (_disposed || revision != _revision || graphRevision != _graphRevision || anchor != _graphObject || !ReferenceEquals(Graph, current)) return;
            Graph = Merge(current, more);
            Status = more.Description;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Status = "Graph expansion failed: " + ex.Message; }
        finally { IsExpandingGraph = false; }
    }

    public static MemoryGraph Merge(MemoryGraph current, MemoryGraph more)
    {
        var nodes = current.Nodes.ToDictionary(n => n.Object.Id);
        foreach (var node in more.Nodes) nodes.TryAdd(node.Object.Id, node with { IsFocus = false });
        var references = current.References.ToDictionary(r => r.Id);
        foreach (var reference in more.References)
            if (nodes.ContainsKey(reference.ToId) && (reference.FromId is not int from || nodes.ContainsKey(from))) references.TryAdd(reference.Id, reference);
        return current with { Nodes = nodes.Values.ToArray(), References = references.Values.ToArray(), IsTruncated = current.IsTruncated || more.IsTruncated };
    }

    [RelayCommand] private void InspectGraphSelection() { if (GraphSelectedObjectId is int id) _ = InspectObjectAsync(id, _lifetime.Token); }
}
