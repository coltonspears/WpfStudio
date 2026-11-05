using System.Windows.Documents;
using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Controls.Editor;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
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
    public Task ASelectionWithItsLineBreakMovesBelowALastLineThatHasNone() => WithEditorAsync("Last.cs", "a\nb\nc", (editor, _, _) =>
    {
        // The break selected with "b" stays between the lines, so at the end of the document the selection is the line alone.
        editor.Select(2, 2);
        editor.MoveLines(up: false);
        Assert.Equal("a\nc\nb", Text(editor));
        Assert.Equal("b", editor.SelectedText);
        Assert.Equal(editor.Document.TextLength, editor.CaretOffset);
        editor.MoveLines(up: false); // Already last.
        editor.MoveLines(up: true);
        Assert.Equal("a\nb\nc", Text(editor));
        Assert.Equal("b", editor.SelectedText);

        // The same with the caret at the selection's start.
        editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(editor.TextArea, 4, 2);
        editor.CaretOffset = 2;
        editor.MoveLines(up: false);
        Assert.Equal("a\nc\nb", Text(editor));
        Assert.Equal("b", editor.SelectedText);
        Assert.Equal(4, editor.CaretOffset);
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

    [Fact]
    public Task ReplaceStepsThroughMatchesShowingEachBeforeItChanges() => WithEditorAsync("Replace.cs", "|int count = Count + count;\nreturn count;", (editor, model, _) =>
    {
        var panel = editor.FindPanel;
        Assert.True(EditorReplace.GetIsAvailable(panel));
        panel.SearchPattern = "count";
        panel.MatchCase = true;
        EditorReplace.SetText(panel, "total");

        // The first press only selects what would be replaced.
        var shown = EditorReplace.Next(panel, editor.TextArea);
        Assert.Equal(0, shown.Replaced);
        Assert.Equal("count", editor.SelectedText);
        Assert.Equal(4, editor.SelectionStart);
        Assert.Equal("int count = Count + count;\nreturn count;", Text(editor));

        // The next replaces it and moves on, leaving the differently cased name alone.
        Assert.Equal(1, EditorReplace.Next(panel, editor.TextArea).Replaced);
        Assert.Equal("int total = Count + count;\nreturn count;", Text(editor));
        Assert.Equal("int total = Count + ".Length, editor.SelectionStart);
        Assert.Equal("int total = Count + count;\nreturn count;", model.State.Content.Replace("\r\n", "\n"));

        // A selection that is not a match is not replaced: the caret was moved elsewhere in between.
        editor.Select(0, 3);
        Assert.Equal(0, EditorReplace.Next(panel, editor.TextArea).Replaced);
        Assert.Equal("int total = Count + count;\nreturn count;", Text(editor));

        Assert.Equal(1, EditorReplace.Next(panel, editor.TextArea).Replaced);
        var last = EditorReplace.Next(panel, editor.TextArea);
        Assert.Equal((1, "Replaced the last occurrence"), (last.Replaced, last.Status));
        Assert.Equal("int total = Count + total;\nreturn total;", Text(editor));
        Assert.Equal(0, EditorReplace.Next(panel, editor.TextArea).Replaced);
    });

    [Fact]
    public Task ReplaceAllIsOneUndoAndFollowsTheFindPanelsSwitches() => WithEditorAsync("ReplaceAll.cs", "|var cost = Cost(costs, $cost);\nvar discount = cost;", (editor, _, _) =>
    {
        var panel = editor.FindPanel;
        var original = Text(editor);
        panel.SearchPattern = "cost";
        panel.WholeWords = true;
        // Plain text goes in as typed: a dollar sign is not a group reference.
        EditorReplace.SetText(panel, "$1");
        var all = EditorReplace.All(panel, editor.TextArea);
        Assert.Equal((4, "Replaced 4 occurrences"), (all.Replaced, all.Status));
        Assert.Equal("var $1 = $1(costs, $$1);\nvar discount = $1;", Text(editor));
        editor.Undo();
        Assert.Equal(original, Text(editor));

        // A regular expression's replacement can use what it captured.
        panel.WholeWords = false;
        panel.UseRegex = true;
        panel.MatchCase = true;
        panel.SearchPattern = @"var (\w+)";
        EditorReplace.SetText(panel, "decimal $1Total");
        Assert.Equal(2, EditorReplace.All(panel, editor.TextArea).Replaced);
        Assert.Equal("decimal costTotal = Cost(costs, $cost);\ndecimal discountTotal = cost;", Text(editor));

        // A pattern that cannot be read is said so, and nothing changes. The panel keeps such a pattern as typed: its
        // text box's binding swallows the complaint that a bare assignment like this one gets to see.
        Assert.Throws<ICSharpCode.AvalonEdit.Search.SearchPatternException>(() => panel.SearchPattern = "(");
        Assert.Equal("(", panel.SearchPattern);
        var invalid = EditorReplace.All(panel, editor.TextArea);
        Assert.Equal(0, invalid.Replaced);
        Assert.StartsWith("The search pattern is not valid", invalid.Status);
        Assert.Equal("decimal costTotal = Cost(costs, $cost);\ndecimal discountTotal = cost;", Text(editor));
        panel.UseRegex = false;
        panel.SearchPattern = "";
        Assert.Equal("Type what to find first", EditorReplace.Next(panel, editor.TextArea).Status);
    });

    [Fact]
    public void SignatureHelpPicksTheParameterAnArgumentGoesTo()
    {
        var plain = new SignatureEntry("M(int a, string b)", null, [new("a", "int", null, "int a"), new("b", "string", null, "string b")]);
        var open = new SignatureEntry("M(string format, params object[] values)", null, [new("format", "string", null, "string format"), new("values", "object[]", null, "params object[] values")]);
        Assert.Equal(0, EditorSignatureHelp.ActiveParameter(plain, 0));
        Assert.Equal(1, EditorSignatureHelp.ActiveParameter(plain, 1));
        // One argument too many has no parameter; with a params array every further argument is one of its elements.
        Assert.Equal(-1, EditorSignatureHelp.ActiveParameter(plain, 2));
        Assert.Equal(1, EditorSignatureHelp.ActiveParameter(open, 4));
        Assert.Equal(-1, EditorSignatureHelp.ActiveParameter(new SignatureEntry("M()", null, []), 0));
    }

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
