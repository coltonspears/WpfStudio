using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.App.Features.Git;

public partial class GitPane : UserControl
{
    public GitPane() => InitializeComponent();

    private GitViewModel? Model => DataContext as GitViewModel;

    private void OpenChangeOnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (ItemUnderMouse<GitChange>(args) is { } change && Model?.OpenFileCommand.CanExecute(change) == true) Model.OpenFileCommand.Execute(change);
    }

    private void OpenCommitFileOnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (ItemUnderMouse<GitCommitFile>(args) is { } file && Model?.OpenCommitFileCommand.CanExecute(file) == true) Model.OpenCommitFileCommand.Execute(file);
    }

    private void SwitchOnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (ItemUnderMouse<string>(args) is null || Model is not { } model || !model.SwitchBranchCommand.CanExecute(null)) return;
        BranchPicker.IsChecked = false;
        model.SwitchBranchCommand.Execute(null);
    }

    private void CloseBranchPicker(object sender, RoutedEventArgs args) => BranchPicker.IsChecked = false;

    private double _commitFilesWidth = -1;

    /// <summary>
    /// Puts a commit's file list beside its diff when both fit, and above it (one third of the height) otherwise.
    /// The layout changes only when the mode changes, so a splitter position the reader chose survives resizing.
    /// </summary>
    private void LayoutCommitBody(object sender, SizeChangedEventArgs args)
    {
        var width = CommitBody.ActualWidth;
        var filesWidth = width >= 1000 ? 280 : width >= 700 ? 230 : 0;
        if (filesWidth == _commitFilesWidth) return;
        _commitFilesWidth = filesWidth;
        var wide = filesWidth > 0;
        var columns = CommitBody.ColumnDefinitions;
        var rows = CommitBody.RowDefinitions;
        columns[0].Width = wide ? new GridLength(filesWidth) : new GridLength(1, GridUnitType.Star);
        columns[1].Width = new GridLength(wide ? 5 : 0);
        columns[2].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        rows[0].Height = new GridLength(1, GridUnitType.Star);
        rows[1].Height = new GridLength(wide ? 0 : 5);
        rows[2].Height = wide ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        CommitFilesPanel.MaxHeight = wide ? double.PositiveInfinity : 240;
        foreach (var element in new FrameworkElement[] { CommitSplitter, CommitDivider })
        {
            Grid.SetRow(element, wide ? 0 : 1);
            Grid.SetColumn(element, wide ? 1 : 0);
        }
        CommitSplitter.ResizeDirection = wide ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        CommitDivider.Width = wide ? 1 : double.NaN;
        CommitDivider.Height = wide ? double.NaN : 1;
        CommitDivider.HorizontalAlignment = wide ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        CommitDivider.VerticalAlignment = wide ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        Grid.SetRow(CommitDiffView, wide ? 0 : 2);
        Grid.SetColumn(CommitDiffView, wide ? 2 : 0);
    }

    /// <summary>Copies its string parameter. A command rather than Click handlers, which WPF cannot connect inside style-defined context menus.</summary>
    public static ICommand CopyCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand<string>(Copy, text => !string.IsNullOrEmpty(text));

    /// <summary>A double-click on the list's empty area or scroll bar must not act on the selected row.</summary>
    private static T? ItemUnderMouse<T>(MouseButtonEventArgs args) where T : class =>
        FindContainer(args.OriginalSource as DependencyObject)?.DataContext as T;

    private static ListBoxItem? FindContainer(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
}
