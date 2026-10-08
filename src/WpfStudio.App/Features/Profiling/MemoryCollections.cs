using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WpfStudio.App.Features.Profiling;

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

/// <summary>Runs a refresh at once, then at most once per interval while requests keep arriving (typing in a filter box),
/// always finishing with a refresh for the latest request. Continues on the caller's synchronization context.</summary>
internal sealed class RefreshThrottle(Action refresh, int intervalMilliseconds = 200)
{
    private long _last = long.MinValue / 2;
    private bool _pending;

    public void Request()
    {
        if (_pending) return;
        var wait = _last + intervalMilliseconds - Environment.TickCount64;
        if (wait <= 0) { Run(); return; }
        _pending = true;
        _ = RunLaterAsync((int)wait);
    }

    private async Task RunLaterAsync(int wait)
    {
        await Task.Delay(wait);
        _pending = false;
        Run();
    }

    private void Run() { _last = Environment.TickCount64; refresh(); }
}
