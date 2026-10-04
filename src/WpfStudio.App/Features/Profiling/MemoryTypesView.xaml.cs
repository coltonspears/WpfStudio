using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Types view layout: applies the chosen grouping to the type list's collection view.</summary>
public partial class MemoryTypesView : UserControl
{
    private MemoryProfilerViewModel? _model;

    public MemoryTypesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_model is not null) { _model.PropertyChanged -= ModelChanged; _model.TypesRefreshed -= ApplyGrouping; }
            _model = e.NewValue as MemoryProfilerViewModel;
            if (_model is not null) { _model.PropertyChanged += ModelChanged; _model.TypesRefreshed += ApplyGrouping; }
            ApplyGrouping();
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryProfilerViewModel.TypeGrouping)) Dispatcher.Invoke(ApplyGrouping);
    }

    private void ApplyGrouping()
    {
        if (_model is null) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyGrouping); return; }
        var view = CollectionViewSource.GetDefaultView(_model.Types);
        var grouped = _model.TypeGrouping != "No grouping";
        if (grouped == view.GroupDescriptions.Count > 0) return;
        view.GroupDescriptions.Clear();
        if (grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MemoryTypeRow.Group)));
    }

    private void CountChecked(object sender, RoutedEventArgs e) { if (_model is not null) _model.FlowUsesBytes = false; }
    private void BytesChecked(object sender, RoutedEventArgs e) { if (_model is not null) _model.FlowUsesBytes = true; }
}
