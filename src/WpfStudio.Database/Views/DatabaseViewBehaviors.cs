using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.Database.Views;

/// <summary>Reusable, presentation-only adapters for WPF controls whose state is not bindable.</summary>
public static class DatabaseViewBehaviors
{
    public static readonly DependencyProperty LoadedCommandProperty = DependencyProperty.RegisterAttached("LoadedCommand", typeof(ICommand), typeof(DatabaseViewBehaviors), new PropertyMetadata(null, OnLoadedCommand));
    public static ICommand? GetLoadedCommand(DependencyObject value) => (ICommand?)value.GetValue(LoadedCommandProperty);
    public static void SetLoadedCommand(DependencyObject value, ICommand? command) => value.SetValue(LoadedCommandProperty, command);
    private static void OnLoadedCommand(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not FrameworkElement element) return;
        element.Loaded -= Loaded;
        element.IsVisibleChanged -= VisibilityChanged;
        if (args.NewValue is not null) { element.Loaded += Loaded; element.IsVisibleChanged += VisibilityChanged; }
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { IsVisible: true }) return;
        ICommand? command = GetLoadedCommand((DependencyObject)sender);
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is true) Loaded(sender, new RoutedEventArgs());
    }

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.RegisterAttached("SelectedNode", typeof(object), typeof(DatabaseViewBehaviors), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty TrackSelectionProperty = DependencyProperty.RegisterAttached("TrackSelection", typeof(bool), typeof(DatabaseViewBehaviors), new PropertyMetadata(false, OnSelectedNode));
    public static bool GetTrackSelection(DependencyObject value) => (bool)value.GetValue(TrackSelectionProperty);
    public static void SetTrackSelection(DependencyObject value, bool track) => value.SetValue(TrackSelectionProperty, track);
    public static object? GetSelectedNode(DependencyObject value) => value.GetValue(SelectedNodeProperty);
    public static void SetSelectedNode(DependencyObject value, object? node) => value.SetValue(SelectedNodeProperty, node);
    private static void OnSelectedNode(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not TreeView tree) return;
        tree.SelectedItemChanged -= SelectedChanged;
        if (args.NewValue is true) tree.SelectedItemChanged += SelectedChanged;
    }
    private static void SelectedChanged(object sender, RoutedPropertyChangedEventArgs<object> args) => ((TreeView)sender).SetCurrentValue(SelectedNodeProperty, args.NewValue);

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(DatabaseViewBehaviors), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPassword));
    public static string GetPassword(DependencyObject value) => (string)value.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject value, string password) => value.SetValue(PasswordProperty, password);
    private static void OnPassword(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not PasswordBox box) return;
        box.PasswordChanged -= PasswordChanged;
        if (box.Password != (string?)args.NewValue) box.Password = (string?)args.NewValue ?? "";
        box.PasswordChanged += PasswordChanged;
    }
    private static void PasswordChanged(object sender, RoutedEventArgs args)
    {
        var box = (PasswordBox)sender;
        box.SetCurrentValue(PasswordProperty, box.Password);
    }
}
