using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Hosts the four profiler views and the docked object browser. View switching and the browser column are
/// handled here because they are pure layout; all state lives in <see cref="MemoryProfilerViewModel"/>.</summary>
public partial class MemoryProfilerPane : UserControl
{
    private MemoryProfilerViewModel? _model;
    private GridLength _browserWidth = new(360);

    public MemoryProfilerPane()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MemoryProfilerViewModel);
        Loaded += (_, _) => { Attach(DataContext as MemoryProfilerViewModel); Apply(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.G && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && _model?.HasCapture == true)
            { GoToBox.Focus(); GoToBox.SelectAll(); e.Handled = true; }
        };
        SizeChanged += (_, e) =>
        {
            // In narrow docked layouts keep the views usable: the browser takes at most 45% of the width.
            if (BrowserColumn.Width.IsAbsolute && BrowserColumn.Width.Value > e.NewSize.Width * 0.45 && e.NewSize.Width > 0)
                BrowserColumn.Width = new GridLength(Math.Max(280, e.NewSize.Width * 0.45));
        };
    }

    private void ProcessesOpened(object? sender, EventArgs e)
    {
        // Keep the list current without a separate click; the selection is preserved by process identity.
        if (_model is { IsBusy: false } model && model.RefreshProcessesCommand.CanExecute(null) && !model.RefreshProcessesCommand.IsRunning)
            model.RefreshProcessesCommand.Execute(null);
    }

    private void Attach(MemoryProfilerViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += ModelChanged;
        Apply();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemoryProfilerViewModel.SelectedView) or nameof(MemoryProfilerViewModel.IsBrowserVisible) or nameof(MemoryProfilerViewModel.HasCapture))
            Dispatcher.Invoke(Apply);
    }

    private void Apply()
    {
        if (_model is null) return;
        var view = _model.SelectedView;
        OverviewView.Visibility = view == MemoryProfilerViewModel.OverviewView ? Visibility.Visible : Visibility.Collapsed;
        TypesView.Visibility = view == MemoryProfilerViewModel.TypesView ? Visibility.Visible : Visibility.Collapsed;
        RetentionView.Visibility = view == MemoryProfilerViewModel.RetentionView ? Visibility.Visible : Visibility.Collapsed;
        GraphView.Visibility = view == MemoryProfilerViewModel.GraphView ? Visibility.Visible : Visibility.Collapsed;
        var showBrowser = _model.IsBrowserVisible;
        if (showBrowser && BrowserColumn.Width.Value == 0) BrowserColumn.Width = _browserWidth;
        else if (!showBrowser && BrowserColumn.Width.Value > 0) { _browserWidth = BrowserColumn.Width; BrowserColumn.Width = new GridLength(0); }
        Browser.Visibility = BrowserSplitter.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
    }
}
