using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Behaviors;

/// <summary>Reusable view-only event to command bridges; application decisions stay in view models.</summary>
public static class Interaction
{
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
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached("FocusWhenVisible", typeof(bool), typeof(Interaction), new PropertyMetadata(false, FocusChanged));
    public static void SetFocusWhenVisible(DependencyObject target, bool value) => target.SetValue(FocusWhenVisibleProperty, value);
    public static bool GetFocusWhenVisible(DependencyObject target) => (bool)target.GetValue(FocusWhenVisibleProperty);
    private static void FocusChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not UIElement element) return;
        element.IsVisibleChanged += (_, _) => { if (element.IsVisible) element.Dispatcher.BeginInvoke(() => { element.Focus(); if (element is TextBox box) box.SelectAll(); }); };
    }
}
