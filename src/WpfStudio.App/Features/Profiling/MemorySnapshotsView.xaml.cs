using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Snapshots view: live memory timeline, snapshot history and the type-by-type comparison.</summary>
public partial class MemorySnapshotsView : UserControl
{
    public MemorySnapshotsView() => InitializeComponent();

    private void ComparisonDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void ComparisonKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected(); e.Handled = true;
    }

    private void OpenSelected()
    {
        if (DataContext is MemoryProfilerViewModel model && ComparisonList.SelectedItem is ComparisonRow row && !row.IsGone) model.OpenTypeCommand.Execute(row.Key);
    }
}
