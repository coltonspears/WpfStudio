using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Text;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class EditorSnippetTests
{
    [Fact]
    public Task TabExpandsAShortcutIntoOneUndoableSnippet() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        const string source = "public partial class ShellViewModel\n{\n    obsprop\n}";
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Snippet.cs"), source);
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = source.IndexOf("obsprop", StringComparison.Ordinal) + "obsprop".Length;
        Assert.True(editor.TryExpandSnippet());
        Assert.Contains("    [ObservableProperty]\n    public partial string Title { get; set; }", state.Content.Replace("\r\n", "\n"));
        Assert.DoesNotContain("obsprop", state.Content);
        editor.Undo();
        Assert.Equal(source, state.Content.Replace("\r\n", "\n"));
    });

    [Fact]
    public Task MirrorsAndClassNameAreFilledAndUnknownWordsKeepTab() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        const string source = "public class Gauge : Control\n{\n    dp\n    notasnippet\n}";
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Gauge.cs"), source);
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = source.IndexOf("notasnippet", StringComparison.Ordinal) + "notasnippet".Length;
        Assert.False(editor.TryExpandSnippet());
        editor.CaretOffset = source.IndexOf("dp", StringComparison.Ordinal) + 2;
        Assert.True(editor.TryExpandSnippet());
        Assert.Contains("DependencyProperty.Register(nameof(Value), typeof(string), typeof(Gauge), new PropertyMetadata(null));", state.Content);
        Assert.Contains("set => SetValue(ValueProperty, value);", state.Content);
    });

    [Fact]
    public Task RequestedSnippetsWrapTheSelection() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        const string source = "<Window>\n    <TextBlock />\n</Window>";
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Wrap.xaml"), source);
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        var start = source.IndexOf("<TextBlock", StringComparison.Ordinal);
        editor.Select(start, "<TextBlock />".Length);
        model.InsertSnippet(SnippetCatalog.Find("stack", ".xaml")!);
        Assert.Contains("<StackPanel Orientation=\"Vertical\">\n        <TextBlock />\n    </StackPanel>", state.Content.Replace("\r\n", "\n"));
    });

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
