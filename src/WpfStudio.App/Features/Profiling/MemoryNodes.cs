using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>A row in the object browser: a field, element, entry or a "load more" row. Children load the first time
/// the row is expanded, so the tree can walk arbitrarily deep object graphs.</summary>
public sealed partial class ObjectNodeViewModel : ObservableObject
{
    private readonly Func<ObjectNodeViewModel, Task>? _load;
    private bool _loaded;

    public ObjectNodeViewModel(MemoryChildItem item, Func<ObjectNodeViewModel, Task>? load, int depth = 0)
    {
        Item = item; _load = load; Depth = depth;
        if (item.HasChildren && load is not null) Children.Add(Placeholder);
    }

    private ObjectNodeViewModel(string text) { Item = new(text, "", "", "Loading"); }
    private static ObjectNodeViewModel Placeholder => new("Loading…");

    public MemoryChildItem Item { get; }
    public int Depth { get; }
    public int? ObjectId => Item.ObjectId;
    public string Name => Item.Name;
    public string Value => Item.Value;
    public string TypeName => Item.Type.Length == 0 ? "" : MemoryLabels.ShortType(Item.Type);
    public string Kind => Item.Kind;
    public bool IsNull => Item.IsNull;
    public bool IsReference => Item.ObjectId is not null;
    public bool IsMore => Item.Kind == "More";
    public bool IsRaw => Item.Kind == "Raw";
    public bool IsPlaceholder => Item.Kind == "Loading";
    public bool IsString => Item.Value.StartsWith('"');
    public bool IsNumeric => Item.Value.Length > 0 && (char.IsDigit(Item.Value[0]) || Item.Value[0] == '-' && Item.Value.Length > 1 && char.IsDigit(Item.Value[1]));
    public bool IsKeyword => Item.Value is "true" or "false" or "null";
    public string RetainedText => Item.ObjectId is null || Item.RetainedBytes <= 0 ? "" : MemorySize.Format(Item.RetainedBytes);
    public string ToolTipText => $"{Item.Name}: {Item.Type}\n{Item.Value}" + (Item.ObjectId is null ? "" : $"\nOwn {MemorySize.Format(Item.ShallowBytes)} · retains {MemorySize.Format(Item.RetainedBytes)}\nDouble-click or use → to open this object");
    public ObservableCollection<ObjectNodeViewModel> Children { get; } = [];
    /// <summary>Paging state when this row is a collection with more children than one page.</summary>
    public int NextSkip { get; set; }
    public ObjectNodeViewModel? MoreParent { get; init; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    async partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || _load is null) return;
        _loaded = true; IsLoading = true;
        try { await _load(this); }
        catch { Children.Clear(); _loaded = false; }
        finally { IsLoading = false; }
    }

    internal void ReplaceChildren(IEnumerable<ObjectNodeViewModel> children)
    {
        Children.Clear();
        foreach (var child in children) Children.Add(child);
    }
}

/// <summary>A dominator-tree row: one object, or a group of sibling instances of one type that share a dominator.</summary>
public sealed partial class DominatorNodeViewModel : ObservableObject
{
    private readonly Func<DominatorNodeViewModel, Task>? _load;
    private bool _loaded;

    public DominatorNodeViewModel(MemoryDominatorNode node, int? parentId, long heapBytes, Func<DominatorNodeViewModel, Task>? load, DominatorNodeViewModel? parent = null)
    {
        Node = node; ParentId = parentId; HeapBytes = heapBytes; _load = load; Parent = parent;
        if (HasChildren && load is not null) Children.Add(new DominatorNodeViewModel("Loading…"));
    }

    private DominatorNodeViewModel(string text)
    {
        Node = new("", text, 0, 0, 0, 0, null); IsPlaceholder = true;
    }

    public static DominatorNodeViewModel More(int count, long bytes, long heapBytes) =>
        new(new MemoryDominatorNode("", $"{count:N0} more", count, bytes, 0, 0, null), null, heapBytes, null) { IsMoreRow = true };

    public MemoryDominatorNode Node { get; }
    public DominatorNodeViewModel? Parent { get; }
    public int? ParentId { get; }
    public long HeapBytes { get; }
    public bool IsPlaceholder { get; }
    public bool IsMoreRow { get; init; }
    public MemoryObjectInfo? Object => Node.Object;
    public bool IsGroup => Node.Object is null && Node.Count > 1 && !IsMoreRow;
    public bool HasChildren => !IsMoreRow && !IsPlaceholder && (IsGroup || Node.ChildCount > 0);
    public string Key => IsGroup ? $"g{ParentId}|{Node.TypeKey}" : Node.Object is { } obj ? "o" + obj.Id : "m" + Node.Type;
    public string Title => IsMoreRow || IsPlaceholder ? Node.Type : IsGroup ? $"{Node.Count:N0} × {MemoryLabels.ShortType(Node.Type)}" : MemoryLabels.ShortType(Node.Type);
    public string Namespace => IsMoreRow || IsPlaceholder ? "" : MemoryLabels.Namespace(Node.Type);
    public string Via => Node.Via is { Length: > 0 } via ? via.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal) ? via : "via " + via : "";
    public string RetainedText => MemorySize.Format(Node.RetainedBytes);
    public double Ratio => HeapBytes <= 0 ? 0 : (double)Node.RetainedBytes / HeapBytes;
    public string PercentText => HeapBytes <= 0 ? "" : (Ratio >= 0.001 ? Ratio.ToString("P1", System.Globalization.CultureInfo.CurrentCulture) : "<0.1%");
    public string Detail => IsMoreRow ? MemorySize.Format(Node.RetainedBytes) : IsGroup ? $"{Node.Count:N0} objects · own {MemorySize.Format(Node.ShallowBytes)}"
        : Node.Object is { } obj ? $"{obj.Address} · own {MemorySize.Format(obj.ShallowBytes)}" + (Node.ChildCount > 0 ? $" · dominates {Node.ChildCount:N0}" : "") : "";
    public ObservableCollection<DominatorNodeViewModel> Children { get; } = [];
    public bool IsLoaded => _loaded;
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    async partial void OnIsExpandedChanged(bool value)
    {
        if (value) await EnsureLoadedAsync();
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _load is null || !HasChildren) return;
        _loaded = true;
        try { await _load(this); }
        catch { _loaded = false; }
    }

    internal void ReplaceChildren(IEnumerable<DominatorNodeViewModel> children)
    {
        Children.Clear();
        foreach (var child in children) Children.Add(child);
    }
}
