using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Hosts the five profiler views and the docked object browser. The browser column is handled here because
/// it remembers the width the reader dragged it to; all state lives in <see cref="MemoryProfilerViewModel"/>.</summary>
public partial class MemoryProfilerPane : UserControl
{
    private MemoryProfilerViewModel? _model;
    private GridLength _browserWidth = new(360);

    public MemoryProfilerPane()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MemoryProfilerViewModel);
        Loaded += (_, _) => { Attach(DataContext as MemoryProfilerViewModel); UpdateBrowserColumn(); };
        // Live memory sampling runs only while the profiler is on screen.
        IsVisibleChanged += (_, _) => { if (_model is not null) _model.IsMonitorActive = IsVisible; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.G && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && _model?.HasCapture == true)
            { _model.FocusGoToCommand.Execute(null); e.Handled = true; }
        };
        SizeChanged += (_, e) =>
        {
            // In narrow docked layouts keep the views usable: the browser takes at most 45% of the width.
            if (BrowserColumn.Width.IsAbsolute && BrowserColumn.Width.Value > e.NewSize.Width * 0.45 && e.NewSize.Width > 0)
                BrowserColumn.Width = new GridLength(Math.Max(280, e.NewSize.Width * 0.45));
        };
    }

    private void Attach(MemoryProfilerViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) { _model.PropertyChanged -= ModelChanged; _model.IsMonitorActive = false; }
        _model = model;
        if (_model is not null) { _model.PropertyChanged += ModelChanged; _model.IsMonitorActive = IsVisible; }
        UpdateBrowserColumn();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryProfilerViewModel.IsBrowserVisible)) OnUiThread(UpdateBrowserColumn);
    }

    // On the UI thread this runs at once, as before; a change raised elsewhere queues instead of blocking that thread.
    private void OnUiThread(Action action) { if (Dispatcher.CheckAccess()) action(); else _ = Dispatcher.BeginInvoke(action); }

    private void UpdateBrowserColumn()
    {
        if (_model is null) return;
        var showBrowser = _model.IsBrowserVisible;
        if (showBrowser && BrowserColumn.Width.Value == 0) BrowserColumn.Width = _browserWidth;
        else if (!showBrowser && BrowserColumn.Width.Value > 0) { _browserWidth = BrowserColumn.Width; BrowserColumn.Width = new GridLength(0); }
    }
}
