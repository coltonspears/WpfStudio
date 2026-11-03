using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    /// <summary>Builds a result grid's columns from its table. A column name is data, but the DataGrid's own generator parses it
    /// as a binding path: "Total.Count" shows empty cells, and "a]b" throws during layout before any event can intervene.</summary>
    public static readonly DependencyProperty ResultColumnsProperty = DependencyProperty.RegisterAttached("ResultColumns", typeof(DataTable), typeof(DatabaseViewBehaviors), new PropertyMetadata(null, OnResultColumns));
    public static DataTable? GetResultColumns(DependencyObject value) => (DataTable?)value.GetValue(ResultColumnsProperty);
    public static void SetResultColumns(DependencyObject value, DataTable? table) => value.SetValue(ResultColumnsProperty, table);
    private static void OnResultColumns(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not DataGrid grid) return;
        grid.Columns.Clear();
        if (args.NewValue is not DataTable table) return;
        var properties = ((ITypedList)table.DefaultView).GetItemProperties(null);
        foreach (DataColumn column in table.Columns)
            grid.Columns.Add(new DataGridTextColumn { Header = column.ColumnName, SortMemberPath = column.ColumnName, CanUserSort = CanSortBy(column.ColumnName), Binding = new Binding { Path = new PropertyPath("(0)", properties[column.ColumnName]), Mode = BindingMode.OneWay } });
    }
    /// <summary>A sort has to go by name: the collection view parses it as a binding path, and DataView.Sort splits it at commas.</summary>
    private static bool CanSortBy(string column)
    {
        if (column.Contains(',')) return false;
        try { _ = new PropertyPath(column); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(DatabaseViewBehaviors), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPassword));
    public static string? GetPassword(DependencyObject value) => (string?)value.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject value, string? password) => value.SetValue(PasswordProperty, password);
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
