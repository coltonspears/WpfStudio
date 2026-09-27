using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Behaviors;

/// <summary>Reusable view-only event to command bridges; application decisions stay in view models.</summary>
public static class Interaction
{
    public static readonly DependencyProperty SelectOnRightClickProperty = DependencyProperty.RegisterAttached("SelectOnRightClick", typeof(bool), typeof(Interaction), new PropertyMetadata(false, RightClickChanged));
    public static void SetSelectOnRightClick(DependencyObject target, bool value) => target.SetValue(SelectOnRightClickProperty, value);
    public static bool GetSelectOnRightClick(DependencyObject target) => (bool)target.GetValue(SelectOnRightClickProperty);
    private static void RightClickChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        element.PreviewMouseRightButtonDown -= SelectRightClick;
        if ((bool)args.NewValue) element.PreviewMouseRightButtonDown += SelectRightClick;
    }
    private static void SelectRightClick(object sender, MouseButtonEventArgs args)
    {
        // Select the row under the pointer before the popup captures focus.
        for (var item = args.OriginalSource as DependencyObject; item != null; item = item is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
        {
            if (item is TreeViewItem tree) { tree.IsSelected = true; tree.Focus(); break; }
            if (item is ListBoxItem list) { list.IsSelected = true; list.Focus(); break; }
            if (item is DataGridRow row) { row.IsSelected = true; row.Focus(); break; }
            if (ReferenceEquals(item, sender)) break;
        }
    }
    private static readonly HashSet<Window> ClosingWindows = [];
    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.RegisterAttached("ActivateCommand", typeof(ICommand), typeof(Interaction), new PropertyMetadata(null, ActivateChanged));
    public static void SetActivateCommand(DependencyObject target, ICommand value) => target.SetValue(ActivateCommandProperty, value);
    public static ICommand? GetActivateCommand(DependencyObject target) => (ICommand?)target.GetValue(ActivateCommandProperty);
    private static void ActivateChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is Control control) { control.MouseDoubleClick -= Activate; control.KeyDown -= KeyActivate; if (args.NewValue != null) { control.MouseDoubleClick += Activate; control.KeyDown += KeyActivate; } }
    }
    private static object? Selected(object source) => source switch { TreeView tree => tree.SelectedItem, System.Windows.Controls.Primitives.Selector selector => selector.SelectedItem, FrameworkElement element => element.DataContext, _ => null };
    private static void Activate(object sender, MouseButtonEventArgs args) { if (GetActivateCommand((DependencyObject)sender) is { } command && command.CanExecute(Selected(sender))) command.Execute(Selected(sender)); }
    private static void KeyActivate(object sender, KeyEventArgs args) { if (args.Key == Key.Enter && GetActivateCommand((DependencyObject)sender) is { } command && command.CanExecute(Selected(sender))) { command.Execute(Selected(sender)); args.Handled = true; } }
    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.RegisterAttached("SelectedNode", typeof(object), typeof(Interaction), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, SelectedNodeChanged));
    public static void SetSelectedNode(DependencyObject target, object value) => target.SetValue(SelectedNodeProperty, value);
    public static object GetSelectedNode(DependencyObject target) => target.GetValue(SelectedNodeProperty);
    private static void SelectedNodeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) { if (owner is TreeView tree) { tree.SelectedItemChanged -= TreeSelection; tree.SelectedItemChanged += TreeSelection; } }
    private static void TreeSelection(object sender, RoutedPropertyChangedEventArgs<object> args) => ((TreeView)sender).SetCurrentValue(SelectedNodeProperty, args.NewValue);
    public static readonly DependencyProperty CloseGuardProperty = DependencyProperty.RegisterAttached("CloseGuard", typeof(ShellViewModel), typeof(Interaction), new PropertyMetadata(null, CloseGuardChanged));
    public static void SetCloseGuard(DependencyObject target, ShellViewModel value) => target.SetValue(CloseGuardProperty, value);
    public static ShellViewModel? GetCloseGuard(DependencyObject target) => (ShellViewModel?)target.GetValue(CloseGuardProperty);
    private static void CloseGuardChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not Window window) return;
        window.Closing -= Closing; window.Activated -= Activated;
        if (args.NewValue != null) { window.Closing += Closing; window.Activated += Activated; }
    }
    private static async void Closing(object? sender, CancelEventArgs args)
    {
        if (sender is not Window window || GetCloseGuard(window) is not { } vm) return;
        args.Cancel = true;
        if (!ClosingWindows.Add(window)) return;
        window.IsEnabled = false;
        try { if (await vm.TryCloseAsync()) { window.Closing -= Closing; _ = window.Dispatcher.BeginInvoke(window.Close); } }
        catch (Exception ex) { vm.Status = "Could not close safely: " + ex.Message; vm.AppendOutput(ex.ToString()); }
        finally { window.IsEnabled = true; ClosingWindows.Remove(window); }
    }
    private static async void Activated(object? sender, EventArgs args) { if (sender is Window window && GetCloseGuard(window) is { } vm) await vm.CheckExternalChangesAsync(); }
    /// <summary>Runs <see cref="ActivateCommandProperty"/> on a single click of a list item (palette-style lists).</summary>
    public static readonly DependencyProperty ActivateOnClickProperty = DependencyProperty.RegisterAttached("ActivateOnClick", typeof(bool), typeof(Interaction), new PropertyMetadata(false, ActivateOnClickChanged));
    public static void SetActivateOnClick(DependencyObject target, bool value) => target.SetValue(ActivateOnClickProperty, value);
    public static bool GetActivateOnClick(DependencyObject target) => (bool)target.GetValue(ActivateOnClickProperty);
    private static void ActivateOnClickChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ListBox list) return;
        list.PreviewMouseLeftButtonUp -= ClickActivate;
        if ((bool)args.NewValue) list.PreviewMouseLeftButtonUp += ClickActivate;
    }
    private static void ClickActivate(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ListBox list) return;
        for (var item = args.OriginalSource as DependencyObject; item != null && !ReferenceEquals(item, list); item = item is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
        {
            if (item is not ListBoxItem container) continue;
            list.SelectedItem = container.DataContext;
            if (GetActivateCommand(list) is { } command && command.CanExecute(container.DataContext)) { command.Execute(container.DataContext); args.Handled = true; }
            return;
        }
    }
    /// <summary>Keeps the selected item of a list visible, e.g. while arrowing through palette results.</summary>
    public static readonly DependencyProperty FollowSelectionProperty = DependencyProperty.RegisterAttached("FollowSelection", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FollowSelectionChanged));
    public static void SetFollowSelection(DependencyObject target, bool value) => target.SetValue(FollowSelectionProperty, value);
    public static bool GetFollowSelection(DependencyObject target) => (bool)target.GetValue(FollowSelectionProperty);
    private static void FollowSelectionChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ListBox list) return;
        list.SelectionChanged -= ScrollSelection;
        if ((bool)args.NewValue) list.SelectionChanged += ScrollSelection;
    }
    private static void ScrollSelection(object sender, SelectionChangedEventArgs args) { if (sender is ListBox { SelectedItem: { } selected } list) list.ScrollIntoView(selected); }
    /// <summary>Focuses a text box when it becomes visible and places the caret after any prefilled text.</summary>
    public static readonly DependencyProperty FocusCaretEndProperty = DependencyProperty.RegisterAttached("FocusCaretEnd", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FocusCaretEndChanged));
    public static void SetFocusCaretEnd(DependencyObject target, bool value) => target.SetValue(FocusCaretEndProperty, value);
    public static bool GetFocusCaretEnd(DependencyObject target) => (bool)target.GetValue(FocusCaretEndProperty);
    private static void FocusCaretEndChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not TextBox box || !(bool)args.NewValue) return;
        box.IsVisibleChanged += (_, _) => { if (box.IsVisible) box.Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }); };
        // A prefix such as ">" set while the box is focused must not leave the caret before it.
        box.TextChanged += (_, _) => { if (box.IsKeyboardFocused && box.CaretIndex == 0 && box.Text.Length == 1 && !char.IsLetterOrDigit(box.Text[0])) box.CaretIndex = 1; };
    }
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached("FocusWhenVisible", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FocusChanged));
    public static void SetFocusWhenVisible(DependencyObject target, bool value) => target.SetValue(FocusWhenVisibleProperty, value);
    public static bool GetFocusWhenVisible(DependencyObject target) => (bool)target.GetValue(FocusWhenVisibleProperty);
    private static void FocusChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not UIElement element) return;
        element.IsVisibleChanged += (_, _) => { if (element.IsVisible) element.Dispatcher.BeginInvoke(() => { element.Focus(); if (element is TextBox box) box.SelectAll(); }); };
    }
}
