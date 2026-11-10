using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WpfStudio.App.Services;

/// <summary>An observable collection that can swap its contents with one Reset notification, so a list of thousands of
/// rows is re-bound once instead of once per item.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
