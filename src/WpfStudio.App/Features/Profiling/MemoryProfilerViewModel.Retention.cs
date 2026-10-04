using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Profiling.Visuals;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>The Retention view: the dominator tree ("what keeps what alive") with a drill-down treemap of the selected
/// node's exclusively retained children.</summary>
public sealed partial class MemoryProfilerViewModel
{
    public ObservableCollection<DominatorNodeViewModel> DominatorRoots { get; } = [];
    public ObservableCollection<DominatorNodeViewModel> RetentionTrail { get; } = [];
    [ObservableProperty] public partial IReadOnlyList<TreemapItem> RetentionMapItems { get; set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RetentionMapTitle), nameof(RetentionMapDetail))] public partial DominatorNodeViewModel? RetentionFocus { get; set; }
    [ObservableProperty] public partial DominatorNodeViewModel? SelectedDominator { get; set; }
    [ObservableProperty] public partial string? SelectedRetentionKey { get; set; }
    [ObservableProperty] public partial bool IsLoadingDominators { get; set; }
    public string RetentionMapTitle => RetentionFocus is { } focus ? $"What {focus.Title} keeps alive" : "What the GC roots keep alive";
    public string RetentionMapDetail => RetentionFocus is { } focus ? $"{focus.RetainedText} · {focus.PercentText} of the heap · double-click a rectangle to drill in"
        : Summary is { } s ? $"{MemorySize.Format(s.ReachableBytes)} reachable · each rectangle is memory owned exclusively by one object or group" : "";
    private long HeapBytes => Math.Max(1, Summary?.ManagedBytes ?? 1);

    private async Task LoadDominatorRootsAsync(CancellationToken token)
    {
        if (_session is not { } session) return;
        var revision = _revision;
        IsLoadingDominators = true;
        try
        {
            var page = await session.GetDominatorsAsync(new(Take: 150), token);
            if (_disposed || revision != _revision) return;
            DominatorRoots.Clear();
            foreach (var node in Wrap(page, null)) DominatorRoots.Add(node);
            RetentionFocus = null; RetentionTrail.Clear(); RefreshRetentionMap();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _revision) Status = "The dominator tree is unavailable: " + ex.Message; }
        finally { IsLoadingDominators = false; }
    }

    private IEnumerable<DominatorNodeViewModel> Wrap(MemoryDominatorPage page, DominatorNodeViewModel? parent)
    {
        foreach (var node in page.Nodes) yield return new DominatorNodeViewModel(node, page.ParentId, HeapBytes, LoadDominatorChildrenAsync, parent);
        if (page.OtherCount > 0) yield return DominatorNodeViewModel.More(page.OtherCount, page.OtherBytes, HeapBytes);
    }

    private async Task LoadDominatorChildrenAsync(DominatorNodeViewModel node)
    {
        if (_session is not { } session) return;
        var revision = _revision;
        var query = node.IsGroup ? new MemoryDominatorQuery(node.ParentId, node.Node.TypeKey, Take: 200) : new MemoryDominatorQuery(node.Object!.Id, Take: 150);
        var page = await session.GetDominatorsAsync(query, _lifetime.Token);
        if (revision != _revision) return;
        node.ReplaceChildren(Wrap(page, node));
        if (RetentionFocus == node) RefreshRetentionMap();
    }

    partial void OnSelectedDominatorChanged(DominatorNodeViewModel? value)
    {
        if (value is null || value.IsPlaceholder || value.IsMoreRow) return;
        SelectedRetentionKey = value.Key;
        if (value.Object is { } obj) ShowObject(obj.Id);
    }

    [RelayCommand]
    private async Task FocusDominatorAsync(DominatorNodeViewModel? node)
    {
        if (node is not null && (node.IsPlaceholder || node.IsMoreRow || !node.HasChildren)) return;
        RetentionFocus = node;
        RetentionTrail.Clear();
        for (var at = node; at is not null; at = at.Parent) RetentionTrail.Insert(0, at);
        if (node is not null) { await node.EnsureLoadedAsync(); node.IsExpanded = true; }
        RefreshRetentionMap();
    }

    [RelayCommand] private Task RetentionHomeAsync() => FocusDominatorAsync(null);

    private void RefreshRetentionMap()
    {
        var source = RetentionFocus?.Children ?? DominatorRoots;
        // Colour follows the type, so the same type keeps its hue as you drill in.
        RetentionMapItems = source.Where(n => !n.IsPlaceholder && n.Node.RetainedBytes > 0).Take(120).Select(n => new TreemapItem(n.Key, n.Title,
            (n.IsMoreRow ? "Smaller owners" : n.Node.Type) + (n.Via.Length > 0 ? "\n" + n.Via : "") + "\n" + n.Detail,
            n.Node.RetainedBytes, n.IsMoreRow ? -1 : TypeColor(n.Node.TypeKey), n.RetainedText + (n.PercentText.Length > 0 ? " · " + n.PercentText : ""), null, n)).ToArray();
    }

    private static int TypeColor(string key)
    {
        var hash = 0;
        foreach (var c in key) hash = unchecked(hash * 31 + c);
        return (hash & 0x7fffffff) % 8;
    }

    [RelayCommand]
    private void SelectRetentionItem(TreemapItem? item)
    {
        if (item?.Tag is not DominatorNodeViewModel node || node.IsMoreRow) return;
        SelectedRetentionKey = node.Key;
        if (node.Object is { } obj) ShowObject(obj.Id);
    }

    [RelayCommand]
    private Task OpenRetentionItemAsync(TreemapItem? item) => item?.Tag is DominatorNodeViewModel node ? FocusDominatorAsync(node) : Task.CompletedTask;
}
