using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using WpfStudio.Contracts;
using WpfStudio.Database.Services;
using WpfStudio.Database.ViewModels;
using WpfStudio.Database.Views;

namespace WpfStudio.Database.Tests;

public sealed class DatabaseViewTests
{
    [Fact]
    public async Task PaneCreatesAndSqlEditorSynchronizesTextAndSelection()
    {
        await OnStaThread(() =>
        {
            var model = new DatabasePaneViewModel(new SqlDatabaseService(), new ConnectionProfileStore(), new NullDialogs());
            var pane = new DatabasePane { DataContext = model };
            pane.Measure(new Size(1000, 800));
            pane.Arrange(new Rect(0, 0, 1000, 800));
            pane.UpdateLayout();
            TextEditor editor = Descendants(pane).OfType<TextEditor>().Single();
            Assert.Equal(model.SelectedDocument!.SqlText, editor.Text);
            editor.AppendText("SELECT 123;");
            Assert.EndsWith("SELECT 123;", model.SelectedDocument.SqlText);
            editor.Select(editor.Text.Length - 11, 11);
            Assert.Equal("SELECT 123;", model.SelectedDocument.SelectedText.Trim());
            Assert.True(model.SelectedDocument.SelectionStartLine > 1);
        });
    }

    [Fact]
    public async Task SqlSyntaxColorsFollowThemeResourcesAndReadableStandaloneFallbacks()
    {
        await OnStaThread(() =>
        {
            var editor = new TextEditor { Background = Brushes.White };
            SqlEditorBehavior.SetText(editor, "SELECT 'value'; -- comment");
            var keyword = editor.SyntaxHighlighting.GetNamedColor("Keyword").Foreground;
            Assert.Same(Brushes.RoyalBlue, keyword.GetBrush(null!));
            editor.Background = Brushes.Black;
            Assert.Same(Brushes.LightSkyBlue, keyword.GetBrush(null!));
            editor.Resources["SyntaxKeyword"] = Brushes.MidnightBlue;
            editor.Resources["SyntaxString"] = Brushes.Brown;
            editor.Resources["SyntaxComment"] = Brushes.ForestGreen;
            Assert.Same(Brushes.MidnightBlue, keyword.GetBrush(null!));
            Assert.Same(Brushes.Brown, editor.SyntaxHighlighting.GetNamedColor("String").Foreground.GetBrush(null!));
            Assert.Same(Brushes.ForestGreen, editor.SyntaxHighlighting.GetNamedColor("Comment").Foreground.GetBrush(null!));
            editor.Resources["SyntaxKeyword"] = Brushes.LightBlue;
            Assert.Same(Brushes.LightBlue, keyword.GetBrush(null!));
        });
    }

    [Fact]
    public async Task EmptyPasswordAndNullTreeSelectionBindOnFirstEdit()
    {
        await OnStaThread(() =>
        {
            var model = new DatabasePaneViewModel(new SqlDatabaseService(), new ConnectionProfileStore(), new NullDialogs());
            var password = new PasswordBox();
            BindingOperations.SetBinding(password, DatabaseViewBehaviors.PasswordProperty, new Binding(nameof(model.Password)) { Source = model, Mode = BindingMode.TwoWay });
            password.Password = "first edit";
            Assert.Equal("first edit", model.Password);
            var tree = new TreeView();
            DatabaseViewBehaviors.SetTrackSelection(tree, true);
            BindingOperations.SetBinding(tree, DatabaseViewBehaviors.SelectedNodeProperty, new Binding(nameof(model.SelectedSchema)) { Source = model, Mode = BindingMode.TwoWay });
            var node = new SchemaNodeViewModel(new("master", Models.SchemaNodeKind.Database, "master"), (_, _) => Task.FromResult<IReadOnlyList<Models.SchemaItem>>([]));
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            Assert.Same(node, model.SelectedSchema);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
    private static Task OnStaThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    private sealed class NullDialogs : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
    }
}
