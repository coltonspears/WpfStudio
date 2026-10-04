using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Routes TreeView selection and double-click (which TreeView cannot bind) to the view model.</summary>
public partial class MemoryRetentionView : UserControl
{
    public MemoryRetentionView() => InitializeComponent();

    private MemoryProfilerViewModel? Model => DataContext as MemoryProfilerViewModel;

    private void TreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Model is { } model && e.NewValue is DominatorNodeViewModel node) model.SelectedDominator = node;
    }

    private void TreeItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: DominatorNodeViewModel node } item || !item.IsSelected || Model is not { } model) return;
        e.Handled = true;
        if (model.FocusDominatorCommand.CanExecute(node)) model.FocusDominatorCommand.Execute(node);
    }
}
