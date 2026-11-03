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
    private static void Activate(object sender, MouseButtonEventArgs args)
    {
        var target = Selected(sender);
        // A double-click on a list's empty area or scroll bar must not act on the selected row.
        if (sender is ListBox list)
        {
            if (args.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(list, source) is not { } row) return;
            target = list.ItemContainerGenerator.ItemFromContainer(row);
        }
        if (GetActivateCommand((DependencyObject)sender) is { } command && command.CanExecute(target)) command.Execute(target);
    }
    private static void KeyActivate(object sender, KeyEventArgs args) { if (args.Key == Key.Enter && GetActivateCommand((DependencyObject)sender) is { } command && command.CanExecute(Selected(sender))) { command.Execute(Selected(sender)); args.Handled = true; } }
    /// <summary>Where a tree publishes its selection. It only ever pushes: a tree cannot select an item it has not generated, so selecting from a view model goes through each item's IsSelected.</summary>
    public static readonly DependencyProperty SelectionSourceProperty = DependencyProperty.RegisterAttached("SelectionSource", typeof(object), typeof(Interaction), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static void SetSelectionSource(DependencyObject target, object value) => target.SetValue(SelectionSourceProperty, value);
    public static object GetSelectionSource(DependencyObject target) => target.GetValue(SelectionSourceProperty);
    /// <summary>Turns on <see cref="SelectionSourceProperty"/>. A separate switch because that property's change callback never runs while its source is null, which would leave the tree unobserved.</summary>
    public static readonly DependencyProperty TrackSelectionProperty = DependencyProperty.RegisterAttached("TrackSelection", typeof(bool), typeof(Interaction), new PropertyMetadata(false, TrackSelectionChanged));
    public static void SetTrackSelection(DependencyObject target, bool value) => target.SetValue(TrackSelectionProperty, value);
    public static bool GetTrackSelection(DependencyObject target) => (bool)target.GetValue(TrackSelectionProperty);
    private static void TrackSelectionChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not TreeView tree) return;
        tree.SelectedItemChanged -= TreeSelection;
        if ((bool)args.NewValue) tree.SelectedItemChanged += TreeSelection;
    }
    private static void TreeSelection(object sender, RoutedPropertyChangedEventArgs<object> args) => ((TreeView)sender).SetCurrentValue(SelectionSourceProperty, args.NewValue);
    /// <summary>Copies its string parameter. A command rather than Click handlers, which WPF cannot connect inside style-defined context menus.</summary>
    public static ICommand CopyCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand<string>(text => Services.ClipboardService.Shared.TrySetText(text!), text => !string.IsNullOrEmpty(text));
    public static readonly DependencyProperty CloseGuardProperty = DependencyProperty.RegisterAttached("CloseGuard", typeof(ShellViewModel), typeof(Interaction), new PropertyMetadata(null, CloseGuardChanged));
    public static void SetCloseGuard(DependencyObject target, ShellViewModel value) => target.SetValue(CloseGuardProperty, value);
    public static ShellViewModel? GetCloseGuard(DependencyObject target) => (ShellViewModel?)target.GetValue(CloseGuardProperty);
    private static void CloseGuardChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not Window window) return;
        window.Closing -= Closing; window.Activated -= Activated;
        if (args.NewValue != null) { window.Closing += Closing; window.Activated += Activated; }
    }
#pragma warning disable VSTHRD100 // A WPF event must cancel synchronously; the await below is guarded.
    private static async void Closing(object? sender, CancelEventArgs args)
#pragma warning restore VSTHRD100
    {
        if (sender is not Window window || GetCloseGuard(window) is not { } vm) return;
        args.Cancel = true;
        if (!ClosingWindows.Add(window)) return;
        window.IsEnabled = false;
        try { if (await vm.TryCloseAsync()) { window.Closing -= Closing; _ = window.Dispatcher.BeginInvoke(window.Close); } }
        catch (Exception ex) { vm.Status = "Could not close safely: " + ex.Message; vm.AppendOutput(ex.ToString()); }
        finally { window.IsEnabled = true; ClosingWindows.Remove(window); }
    }
#pragma warning disable VSTHRD100 // A WPF event handler; the await below is guarded.
    private static async void Activated(object? sender, EventArgs args)
#pragma warning restore VSTHRD100
    {
        if (sender is not Window window || GetCloseGuard(window) is not { } vm) return;
        try { await vm.CheckExternalChangesAsync(); }
        catch (Exception ex) { vm.Status = "Could not check for external changes: " + ex.Message; vm.AppendOutput(ex.ToString()); }
    }
    /// <summary>Runs a command when a combo box's list opens, for lists that refresh themselves on demand.</summary>
    public static readonly DependencyProperty OpenedCommandProperty = DependencyProperty.RegisterAttached("OpenedCommand", typeof(ICommand), typeof(Interaction), new PropertyMetadata(null, OpenedCommandChanged));
    public static void SetOpenedCommand(DependencyObject target, ICommand value) => target.SetValue(OpenedCommandProperty, value);
    public static ICommand? GetOpenedCommand(DependencyObject target) => (ICommand?)target.GetValue(OpenedCommandProperty);
    private static void OpenedCommandChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ComboBox box) return;
        box.DropDownOpened -= DropDownOpened;
        if (args.NewValue != null) box.DropDownOpened += DropDownOpened;
    }
    private static void DropDownOpened(object? sender, EventArgs args)
    {
        if (sender is ComboBox box && GetOpenedCommand(box) is { } command && command.CanExecute(null)) command.Execute(null);
    }
    /// <summary>Groups an items control by this property of its items; null shows them ungrouped.</summary>
    public static readonly DependencyProperty GroupByProperty = DependencyProperty.RegisterAttached("GroupBy", typeof(string), typeof(Interaction), new PropertyMetadata(null, GroupByChanged));
    public static void SetGroupBy(DependencyObject target, string? value) => target.SetValue(GroupByProperty, value);
    public static string? GetGroupBy(DependencyObject target) => (string?)target.GetValue(GroupByProperty);
    private static void GroupByChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ItemsControl items) return;
        items.Items.GroupDescriptions.Clear();
        if (args.NewValue is string property) items.Items.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(property));
    }
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
    /// <summary>Keeps the selected list item, tree row or tab header visible.</summary>
    public static readonly DependencyProperty FollowSelectionProperty = DependencyProperty.RegisterAttached("FollowSelection", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FollowSelectionChanged));
    public static void SetFollowSelection(DependencyObject target, bool value) => target.SetValue(FollowSelectionProperty, value);
    public static bool GetFollowSelection(DependencyObject target) => (bool)target.GetValue(FollowSelectionProperty);
    private static void FollowSelectionChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is TreeView tree)
        {
            tree.RemoveHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(ScrollTreeSelection));
            if ((bool)args.NewValue) tree.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(ScrollTreeSelection), true);
            return;
        }
        if (owner is not System.Windows.Controls.Primitives.Selector list) return;
        list.SelectionChanged -= ScrollSelection;
        if ((bool)args.NewValue) list.SelectionChanged += ScrollSelection;
    }
    private static void ScrollTreeSelection(object sender, RoutedEventArgs args)
    {
        if (sender is not TreeView tree || args.OriginalSource is not TreeViewItem item) return;
        _ = tree.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (!GetFollowSelection(tree) || !item.IsSelected || !item.IsLoaded || !item.IsDescendantOf(tree)) return;
            // Bring the selected row's header into view, not its whole expanded subtree.
            item.BringIntoView(new Rect(0, 0, 1, Math.Min(24, item.ActualHeight)));
        });
    }
    private static void ScrollSelection(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListBox { SelectedItem: { } selected } list) list.ScrollIntoView(selected);
        else if (sender is TabControl { SelectedItem: { } selectedTab } tabs && ReferenceEquals(args.OriginalSource, tabs))
            _ = tabs.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (!GetFollowSelection(tabs) || !tabs.IsLoaded || !ReferenceEquals(tabs.SelectedItem, selectedTab) ||
                    tabs.ItemContainerGenerator.ContainerFromItem(selectedTab) is not TabItem header ||
                    !header.IsSelected || !header.IsLoaded || !header.IsDescendantOf(tabs)) return;
                // TabItem's visual is the header; the selected content is hosted
                // separately. Let its enclosing scroll viewer reveal the full label.
                header.BringIntoView();
            });
        else if (sender is DataGrid { SelectedItem: { } row } grid)
            _ = grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (!GetFollowSelection(grid) || !Equals(grid.SelectedItem, row)) return;
                grid.UpdateLayout();
                grid.ScrollIntoView(row);
                grid.UpdateLayout();
            });
    }
    /// <summary>Focuses a text box when it becomes visible and places the caret after any prefilled text.</summary>
    public static readonly DependencyProperty FocusCaretEndProperty = DependencyProperty.RegisterAttached("FocusCaretEnd", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FocusCaretEndChanged));
    public static void SetFocusCaretEnd(DependencyObject target, bool value) => target.SetValue(FocusCaretEndProperty, value);
    public static bool GetFocusCaretEnd(DependencyObject target) => (bool)target.GetValue(FocusCaretEndProperty);
    private static void FocusCaretEndChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not TextBox box) return;
        box.IsVisibleChanged -= FocusCaretEndWhenVisible; box.TextChanged -= KeepCaretAfterPrefix;
        if ((bool)args.NewValue) { box.IsVisibleChanged += FocusCaretEndWhenVisible; box.TextChanged += KeepCaretAfterPrefix; }
    }
    private static void FocusCaretEndWhenVisible(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TextBox { IsVisible: true } box) _ = box.Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; });
    }
    // A prefix such as ">" set while the box is focused must not leave the caret before it.
    private static void KeepCaretAfterPrefix(object sender, TextChangedEventArgs args)
    {
        if (sender is TextBox { IsKeyboardFocused: true, CaretIndex: 0 } box && box.Text.Length == 1 && !char.IsLetterOrDigit(box.Text[0])) box.CaretIndex = 1;
    }
    /// <summary>Focuses a control when its view model asks, then clears the request so the next one is a change again.</summary>
    public static readonly DependencyProperty FocusWhenTrueProperty = DependencyProperty.RegisterAttached("FocusWhenTrue", typeof(bool), typeof(Interaction), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, FocusWhenTrueChanged));
    public static void SetFocusWhenTrue(DependencyObject target, bool value) => target.SetValue(FocusWhenTrueProperty, value);
    public static bool GetFocusWhenTrue(DependencyObject target) => (bool)target.GetValue(FocusWhenTrueProperty);
    private static void FocusWhenTrueChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not UIElement element || args.NewValue is not true) return;
        // After layout: the same request may have just brought this control's pane forward.
        _ = element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            element.SetCurrentValue(FocusWhenTrueProperty, false);
            element.Focus(); Keyboard.Focus(element);
            if (element is TextBox box) box.SelectAll();
        });
    }
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached("FocusWhenVisible", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FocusChanged));
    public static void SetFocusWhenVisible(DependencyObject target, bool value) => target.SetValue(FocusWhenVisibleProperty, value);
    public static bool GetFocusWhenVisible(DependencyObject target) => (bool)target.GetValue(FocusWhenVisibleProperty);
    private static void FocusChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not UIElement element) return;
        element.IsVisibleChanged -= FocusWhenBecomingVisible;
        if ((bool)args.NewValue) element.IsVisibleChanged += FocusWhenBecomingVisible;
    }
    private static void FocusWhenBecomingVisible(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is UIElement { IsVisible: true } element) _ = element.Dispatcher.BeginInvoke(() => { element.Focus(); if (element is TextBox box) box.SelectAll(); });
    }
}
