using System.Windows.Documents;
using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Text;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

/// <summary>Keys and line commands, through a real editor control: what is typed, what the buffer holds and where the caret ends up.</summary>
public sealed class EditorEditingTests
{
    private static string Text(EditorSurface editor) => editor.Text.Replace("\r\n", "\n");

    /// <summary>The editor's text with | where the caret is.</summary>
    private static string Marked(EditorSurface editor) => editor.Text.Insert(editor.CaretOffset, "|").Replace("\r\n", "\n");

    private static Task WithEditorAsync(string name, string source, Action<EditorSurface, EditorViewModel, EditorPreferences> test) => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), name), source.Replace("|", ""));
        var preferences = new EditorPreferences(new FontCatalog());
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { }) { Preferences = preferences };
        var editor = new EditorSurface { ViewModel = model };
        if (source.Contains('|')) editor.CaretOffset = source.IndexOf('|');
        test(editor, model, preferences);
    });

    [Fact]
    public Task BackspaceOnAnIndentedBlankLineRemovesItAndUndoBringsItBack() => WithEditorAsync("Blank.cs", "class C\n{\n    |\n}", (editor, model, preferences) =>
    {
        EditingCommands.Backspace.Execute(null, editor.TextArea);
        Assert.Equal("class C\n{|\n}", Marked(editor));
        Assert.Equal("class C\n{\n}", model.State.Content.Replace("\r\n", "\n"));
        editor.Undo();
        Assert.Equal("class C\n{\n    \n}", Text(editor));

        // With the setting off, Backspace is the editor's own: one character.
        preferences.SmartBackspace = false;
        editor.CaretOffset = editor.Text.IndexOf("    ", StringComparison.Ordinal) + 4;
        EditingCommands.Backspace.Execute(null, editor.TextArea);
        Assert.Equal("class C\n{\n   |\n}", Marked(editor));
    });

    [Fact]
    public Task BracketsAndQuotesArePairedTypedThroughAndRemovedTogether() => WithEditorAsync("Pairs.cs", "class C\n{\n    void M()\n    {\n        Run|\n    }\n}", (editor, _, preferences) =>
    {
        editor.TextArea.PerformTextInput("(");
        Assert.Contains("        Run(|)\n", Marked(editor));
        editor.TextArea.PerformTextInput("\"");
        Assert.Contains("        Run(\"|\")\n", Marked(editor));
        editor.TextArea.PerformTextInput("a");
        editor.TextArea.PerformTextInput("\"");
        editor.TextArea.PerformTextInput(")");
        Assert.Contains("        Run(\"a\")|\n", Marked(editor));
        EditingCommands.Backspace.Execute(null, editor.TextArea);
        Assert.Contains("        Run(\"a\"|\n", Marked(editor));

        editor.TextArea.PerformTextInput(")");
        editor.TextArea.PerformTextInput("[");
        Assert.Contains("Run(\"a\")[|]", Marked(editor));
        EditingCommands.Backspace.Execute(null, editor.TextArea);
        Assert.Contains("Run(\"a\")|\n", Marked(editor));

        // A selection is wrapped, not replaced.
        int start = editor.Text.IndexOf("Run", StringComparison.Ordinal);
        editor.Select(start, "Run(\"a\")".Length);
        editor.TextArea.PerformTextInput("(");
        Assert.Contains("        (Run(\"a\"))|\n", Marked(editor));

        preferences.AutoCloseBrackets = false;
        editor.TextArea.PerformTextInput("(");
        Assert.Contains("(Run(\"a\"))(|\n", Marked(editor));
    });

    [Fact]
    public Task EnterKeepsStatementsLinedUp() => WithEditorAsync("Enter.cs", "class C\n{\n    void M()\n    {\n        if (ready)|\n    }\n}", (editor, _, _) =>
    {
        editor.TextArea.PerformTextInput("\n");
        foreach (char c in "Run();") editor.TextArea.PerformTextInput(c.ToString());
        Assert.Contains("        if (ready)\n            Run();|\n", Marked(editor));
        editor.TextArea.PerformTextInput("\n");
        Assert.Contains("        if (ready)\n            Run();\n        |\n    }", Marked(editor));
    });

    [Fact]
    public Task LinesAreDuplicatedMovedAndOpened() => WithEditorAsync("Lines.cs", "one\n  t|wo\nthree", (editor, _, _) =>
    {
        editor.DuplicateLines();
        Assert.Equal("one\n  two\n  t|wo\nthree", Marked(editor));
        editor.MoveLines(up: true);
        editor.MoveLines(up: true);
        Assert.Equal("  t|wo\none\n  two\nthree", Marked(editor));
        editor.MoveLines(up: true); // Already first.
        editor.MoveLines(up: false);
        Assert.Equal("one\n  t|wo\n  two\nthree", Marked(editor));

        // A selection moves as whole lines and stays selected, and the last line keeps having no line break.
        int first = editor.Text.IndexOf('t');
        editor.Select(first, editor.Text.IndexOf("three", StringComparison.Ordinal) - 1 - first);
        var selected = editor.SelectedText;
        editor.MoveLines(up: false);
        Assert.Equal("one\nthree\n  two\n  two", Text(editor));
        Assert.Equal(selected, editor.SelectedText);

        editor.Select(0, 0);
        editor.CaretOffset = editor.Text.IndexOf("three", StringComparison.Ordinal) + 2;
        editor.DuplicateLines();
        editor.OpenLine(above: true);
        Assert.Equal("one\nthree\n|\nthree\n  two\n  two", Marked(editor));
        editor.CaretOffset = editor.Text.Length - 1;
        editor.OpenLine(above: false);
        Assert.Equal("one\nthree\n\nthree\n  two\n  two\n  |", Marked(editor));

        // Duplicating a selection inside a line repeats it after itself and selects the copy.
        editor.Select(0, 2);
        editor.DuplicateLines();
        Assert.StartsWith("onone\n", Text(editor));
        Assert.Equal("on", editor.SelectedText);
        Assert.Equal(2, editor.SelectionStart);
    });

    [Fact]
    public Task CommentingTogglesLinesInCSharp() => WithEditorAsync("Comment.cs", "class C\n{\n    void M()\n    {\n        |Run();\n\n        if (a)\n            Stop();\n    }\n}", (editor, _, _) =>
    {
        Assert.True(editor.ToggleComment());
        Assert.Contains("        // Run();\n", Text(editor));
        Assert.True(editor.ToggleComment());
        Assert.Contains("        Run();\n", Text(editor));

        // Several lines take the marker at their shallowest indentation; blank lines are left alone.
        int start = editor.Text.IndexOf("Run();", StringComparison.Ordinal), end = editor.Text.IndexOf("Stop();", StringComparison.Ordinal) + 3;
        editor.Select(start, end - start);
        editor.ToggleComment();
        Assert.Contains("        // Run();\n\n        // if (a)\n        //     Stop();\n", Text(editor));
        editor.ToggleComment();
        Assert.Contains("        Run();\n\n        if (a)\n            Stop();\n", Text(editor));
    });

    [Fact]
    public Task CommentingWrapsXamlAndUnwrapsIt() => WithEditorAsync("Comment.xaml", "<Grid>\n    |<Button Content=\"Go\" />\n</Grid>", (editor, _, _) =>
    {
        Assert.True(editor.ToggleComment());
        Assert.Equal("<Grid>\n    <!-- <Button Content=\"Go\" /> -->\n</Grid>", Text(editor));
        Assert.True(editor.ToggleComment());
        Assert.Equal("<Grid>\n    <Button Content=\"Go\" />\n</Grid>", Text(editor));
    });

    [Fact]
    public Task AMemberSnippetGoesAfterTheCaretsMemberWithItsUsing() => WithEditorAsync("OrderViewModel.cs",
        "using System;\n\nnamespace Demo;\n\npublic partial class OrderViewModel\n{\n    public void Load()\n    {\n        Ru|n();\n    }\n}\n", (editor, model, _) =>
    {
        Assert.True(model.InsertMember(SnippetCatalog.Find("relayasync", ".cs")!, "CommunityToolkit.Mvvm.Input"));
        Assert.Equal("using System;\nusing CommunityToolkit.Mvvm.Input;\n\nnamespace Demo;\n\npublic partial class OrderViewModel\n{\n    public void Load()\n    {\n        Run();\n    }\n\n"
            + "    [RelayCommand]\n    private async Task LoadAsync(CancellationToken cancellationToken)\n    {\n        \n    }\n}\n", Text(editor));
        // One undo takes the member and the using away together.
        editor.Undo();
        Assert.DoesNotContain("RelayCommand", Text(editor));
        Assert.DoesNotContain("CommunityToolkit", Text(editor));
    });

    [Fact]
    public Task SnippetIndentationFollowsTheEditorsOwn() => WithEditorAsync("Tabs.cs", "class C\n{\n\tif|\n}", (editor, _, preferences) =>
    {
        editor.Options.ConvertTabsToSpaces = false;
        Assert.True(editor.TryExpandSnippet());
        Assert.Equal("class C\n{\n\tif (true)\n\t{\n\t\t\n\t}\n}", Text(editor));
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
