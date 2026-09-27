using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.ViewModels;

public partial class SchemaNodeViewModel : ObservableObject
{
    private readonly Func<SchemaItem, CancellationToken, Task<IReadOnlyList<SchemaItem>>> _load;
    private bool _loaded;
    public SchemaItem Item { get; }
    public string Title => Item.Kind is SchemaNodeKind.Table or SchemaNodeKind.View or SchemaNodeKind.Procedure ? $"{Item.Schema}.{Item.Name}" : Item.Name;
    public string Detail => Item.Detail ?? Item.Kind.ToString();
    public ObservableCollection<SchemaNodeViewModel> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    public SchemaNodeViewModel(SchemaItem item, Func<SchemaItem, CancellationToken, Task<IReadOnlyList<SchemaItem>>> load)
    {
        Item = item; _load = load;
        if (item.CanExpand) Children.Add(new SchemaNodeViewModel(new SchemaItem("Expand to load…", SchemaNodeKind.Column, item.Database), load));
    }
    partial void OnIsExpandedChanged(bool value) { if (value && !_loaded && Item.CanExpand) LoadCommand.Execute(null); }

    internal IReadOnlyList<Task> CancelPendingLoads()
    {
        var pending = new List<Task>();
        LoadCommand.Cancel();
        if (LoadCommand.ExecutionTask is { IsCompleted: false } task) pending.Add(task);
        foreach (var child in Children) pending.AddRange(child.CancelPendingLoads());
        return pending;
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ErrorMessage = null;
            var items = await _load(Item, cancellationToken);
            Children.Clear();
            foreach (SchemaItem item in items) Children.Add(new SchemaNodeViewModel(item, _load));
            _loaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            Children.Clear();
            Children.Add(new SchemaNodeViewModel(new SchemaItem("Could not load. Collapse and expand to retry.", SchemaNodeKind.Column, Item.Database), _load));
        }
    }
}
