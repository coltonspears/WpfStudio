using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class EditorXamlTypingTests
{
    [Fact]
    public Task ClosingTagAndEnterHaveOneUndoActionEachAndRestoreCaret() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Typing.xaml"), "<Grid");
        using var model = CreateModel(state, workspace);
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = 5;
        editor.Document.UndoStack.ClearAll();
        editor.TextArea.PerformTextInput(">");
        Assert.Equal("<Grid></Grid>", editor.Text); Assert.Equal(6, editor.CaretOffset);
        Assert.Equal(editor.Text, state.Content); Assert.Equal(6, state.CaretOffset);
        editor.Document.UndoStack.Undo();
        Assert.Equal("<Grid", editor.Text); Assert.Equal(5, editor.CaretOffset);
        editor.Document.UndoStack.Redo();
        Assert.Equal("<Grid></Grid>", editor.Text); Assert.Equal(6, editor.CaretOffset);
        string newline = TextUtilities.GetNewLineFromDocument(editor.Document, editor.TextArea.Caret.Line);
        editor.TextArea.PerformTextInput("\n");
        Assert.Equal("<Grid>" + newline + "    " + newline + "</Grid>", editor.Text);
        Assert.Equal(6 + newline.Length + 4, editor.CaretOffset);
        Assert.Equal(editor.Text, state.Content); Assert.Equal(editor.CaretOffset, state.CaretOffset);
        editor.Document.UndoStack.Undo();
        Assert.Equal("<Grid></Grid>", editor.Text); Assert.Equal(6, editor.CaretOffset);
        editor.Document.UndoStack.Redo();
        Assert.Equal("<Grid>" + newline + "    " + newline + "</Grid>", editor.Text);
        Assert.Equal(6 + newline.Length + 4, editor.CaretOffset);
        Assert.Equal(editor.CaretOffset, state.CaretOffset); Assert.Equal(0, editor.SelectionLength);
    });

    [Fact]
    public Task QuotesPairAndOvertypeWithoutReplacingValues() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Quotes.xaml"), "<Grid Tag=");
        using var model = CreateModel(state, workspace);
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = editor.Document.TextLength; editor.Document.UndoStack.ClearAll();
        editor.TextArea.PerformTextInput("\"");
        Assert.Equal("<Grid Tag=\"\"", editor.Text); Assert.Equal(11, editor.CaretOffset);
        editor.Document.UndoStack.Undo(); Assert.Equal("<Grid Tag=", editor.Text);
        editor.Document.UndoStack.Redo();
        Assert.Equal(11, editor.CaretOffset); Assert.Equal(0, editor.SelectionLength);
        editor.TextArea.PerformTextInput("value");
        long version = state.Version;
        editor.TextArea.PerformTextInput("\"");
        Assert.Equal("<Grid Tag=\"value\"", editor.Text); Assert.Equal(editor.Text.Length, editor.CaretOffset);
        Assert.Equal(version, state.Version);
    });

    [Fact]
    public Task SelectionPasteReadOnlyAndOverstrikeKeepNativeBehavior() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Native.xaml"), "<Grid junk");
        using var model = CreateModel(state, workspace);
        var editor = new EditorSurface { ViewModel = model };
        editor.Select(6, 4); editor.TextArea.PerformTextInput(">");
        Assert.Equal("<Grid >", editor.Text);
        state.Content = "<Grid"; editor.CaretOffset = 5;
        // AvalonEdit's paste handler calls ReplaceSelectionWithText directly.
        editor.TextArea.Selection.ReplaceSelectionWithText(">");
        Assert.Equal("<Grid>", editor.Text);
        state.Content = "<Grid"; editor.CaretOffset = 5;
        editor.TextArea.PerformTextInput("><Border/>");
        Assert.Equal("<Grid><Border/>", editor.Text);
        state.Content = "<Grid"; editor.CaretOffset = 5;
        editor.IsReadOnly = true; editor.TextArea.PerformTextInput(">"); Assert.Equal("<Grid", editor.Text);
        editor.IsReadOnly = false; editor.TextArea.OverstrikeMode = true;
        editor.TextArea.PerformTextInput(">"); Assert.Equal("<Grid>", editor.Text);
    });

    [Fact]
    public Task CompletionPunctuationAndQueuedEnterStillUseTypingAssistance() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "CompletionTyping.xaml"), "<Gri");
        using var model = CreateModel(state, workspace);
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = 4;
        var completion = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long version = state.Version;
        Task commit = editor.CommitCompletionAsync(_ => completion.Task, ">");
        editor.TextArea.PerformTextInput("\n");
        Assert.Equal("<Gri", editor.Text);
        completion.SetResult((version, new(1, 3, "Grid")));
        await commit;
        string newline = TextUtilities.GetNewLineFromDocument(editor.Document, 1);
        Assert.Equal("<Grid>" + newline + "    " + newline + "</Grid>", editor.Text);
        Assert.Equal(editor.Text, state.Content); Assert.Equal(editor.CaretOffset, state.CaretOffset);
        editor.Document.UndoStack.Undo(); Assert.Equal("<Grid></Grid>", editor.Text);
        editor.Document.UndoStack.Undo(); Assert.Equal("<Grid", editor.Text);
    });

    [Fact]
    public Task ReplayDuringTabSwitchUsesTheOriginalLanguageAndSelectionRestoresDirection() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var first = new DocumentState(Path.Combine(Path.GetTempPath(), "Original.cs"), "<Gri");
        var second = new DocumentState(Path.Combine(Path.GetTempPath(), "Next.xaml"), "<Grid/>");
        using var firstModel = CreateModel(first, workspace);
        using var secondModel = CreateModel(second, workspace);
        var editor = new EditorSurface { ViewModel = firstModel };
        editor.CaretOffset = 4;
        var completion = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task commit = editor.CommitCompletionAsync(_ => completion.Task, ">");
        editor.ViewModel = secondModel;
        completion.SetResult((first.Version, new(1, 3, "Grid")));
        await commit;
        Assert.Equal("<Gri>", first.Content); Assert.Equal("<Grid/>", editor.Text);
        secondModel.RestoreSelection(1, 1, 4);
        Assert.Equal(1, editor.CaretOffset); Assert.Equal(1, editor.SelectionStart); Assert.Equal(4, editor.SelectionLength);
        Assert.Equal(1, second.CaretOffset);
        System.Windows.Documents.EditingCommands.SelectLeftByCharacter.Execute(null, editor.TextArea);
        Assert.Equal(0, editor.CaretOffset); Assert.Equal(0, editor.SelectionStart); Assert.Equal(5, editor.SelectionLength);
    });

    private static EditorViewModel CreateModel(DocumentState state, WorkspaceClient workspace) =>
        new(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
