using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Object browser input: double-click or Enter opens a referenced object; Alt+Left/Right and the mouse's
/// back/forward buttons walk the navigation history.</summary>
public partial class MemoryObjectBrowser : UserControl
{
    public MemoryObjectBrowser()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt || Model is not { } model) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Left && model.CanGoBack) { model.BackCommand.Execute(null); e.Handled = true; }
            else if (key == Key.Right && model.CanGoForward) { model.ForwardCommand.Execute(null); e.Handled = true; }
        };
        PreviewMouseDown += (_, e) =>
        {
            if (Model is not { } model) return;
            if (e.ChangedButton == MouseButton.XButton1 && model.CanGoBack) { model.BackCommand.Execute(null); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2 && model.CanGoForward) { model.ForwardCommand.Execute(null); e.Handled = true; }
        };
    }

    private MemoryProfilerViewModel? Model => DataContext as MemoryProfilerViewModel;

    private void FieldDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: ObjectNodeViewModel node } item || !item.IsSelected) return;
        e.Handled = true;
        Open(node);
    }

    private void FieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TreeViewItem { DataContext: ObjectNodeViewModel node }) return;
        e.Handled = true;
        Open(node);
    }

    private void Open(ObjectNodeViewModel node)
    {
        if (Model is not { } model) return;
        if (node.IsMore) { model.LoadMoreCommand.Execute(node); return; }
        if (node.ObjectId is not null && !node.IsRaw) model.OpenNodeCommand.Execute(node);
    }
}
