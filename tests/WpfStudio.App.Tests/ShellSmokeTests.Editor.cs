using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using WpfStudio.App.Controls;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    /// <summary>
    /// Navigation and symbol queries in the real editor against a real worker, at the moments they used to be turned
    /// away: just after a file was saved, and after a file was added to the project. Also the Git history popup, which
    /// must open on a click and stay open, and definitions that lead to authored code rather than generated code.
    /// </summary>
    private async Task VerifyEditorNavigationAsync(string root, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        var sample = Path.Combine(root, "samples/CounterApp");
        var viewModelPath = Path.Combine(sample, "CounterViewModel.cs");
        await shell.LoadWorkspaceAsync(Path.Combine(sample, "CounterApp.csproj"));
        await shell.OpenDocumentAsync(viewModelPath);
        await Idle();
        var document = shell.ActiveDocument!;
        await UntilAsync(() => ReferenceEquals(manager.ActiveContent, document) && !manager.Layout.Descendents().OfType<LayoutDocument>().Any(item => item.ContentId == "welcome"), "the document tab");
        await SettleAsync(document);
        var editor = Descendants<EditorSurface>(window).Single(item => ReferenceEquals(item.ViewModel, document));
        Keyboard.Focus(editor.TextArea);
        var view = editor.TextArea.TextView;
        await UntilAsync(() => document.CodeLenses.Count > 0, "the reference counts");

        // --- The Git history popup opens when its link is clicked, not while the button is held, and a Git refresh that changes nothing leaves it open.
        TextBlock? History() => Descendants<TextBlock>(view).FirstOrDefault(block => block.ToolTip is string tip && tip.StartsWith("Show who changed", StringComparison.Ordinal));
        await UntilAsync(() => document.BlameVersion == document.State.Version && History() is not null, "Git history above the declarations");
        var link = History()!;
        link.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Assert.False(editor.IsHistoryPopupOpen, "The popup opened on the press; the release would close it again.");
        link.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        await Idle();
        Assert.True(editor.IsHistoryPopupOpen, "A click on the Git history link opens its popup.");
        await document.RefreshGitAsync(shell.Features.GitService);
        await Task.Delay(1500);
        await Idle();
        Assert.True(editor.IsHistoryPopupOpen, "A Git refresh with the same history must not close the popup.");
        editor.CloseAnnotationPopups();

        // --- What the editor looks like with its guides: a bracket pair, the selected word's other occurrences and indentation lines.
        int count = document.State.Content.IndexOf("Count++", StringComparison.Ordinal);
        editor.Select(count, "Count".Length);
        await Idle();
        Screenshot(editor, Path.Combine(root, "artifacts/screenshots/editor-guides.png"));
        editor.Select(0, 0);
        editor.CaretOffset = document.State.Content.IndexOf("Increment()", StringComparison.Ordinal) + "Increment(".Length;
        await Idle();
        Screenshot(editor, Path.Combine(root, "artifacts/screenshots/editor-brackets.png"));

        // --- From XAML, a binding to a command the Toolkit generates goes to the method that was written, not to generated code.
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        async Task GoFromXamlAsync(string reason)
        {
            await shell.OpenDocumentAsync(xamlPath);
            var xaml = shell.ActiveDocument!;
            xaml.Navigate(xaml.State.Content.IndexOf("IncrementCommand", StringComparison.Ordinal) + 3);
            await shell.GoToDefinitionCommand.ExecuteAsync(null);
            Assert.True(ReferenceEquals(shell.ActiveDocument, document), $"{reason}: expected to arrive in CounterViewModel.cs, status was '{shell.Status}'.");
            Assert.Equal(document.State.Content.IndexOf("Increment()", StringComparison.Ordinal), document.State.CaretOffset);
        }
        await UntilAsync(() => shell.XamlAnalysis.XamlContextsAvailable && shell.XamlAnalysis.Status.StartsWith("XAML checked", StringComparison.Ordinal), "the project's XAML to be checked");
        await GoFromXamlAsync("Go to definition from XAML");

        // --- A save a moment ago is still being read. Neither a definition nor the references of a clicked count may be turned away for it.
        async Task TouchAsync()
        {
            File.SetLastWriteTimeUtc(viewModelPath, DateTime.UtcNow);
            await UntilAsync(() => shell.XamlAnalysis.ProjectTypesPending, "the watcher to report the changed file");
        }
        await TouchAsync();
        await GoFromXamlAsync("Go to definition just after a save");
        await TouchAsync();
        var lens = document.CodeLenses.First(entry => entry.Name == "Count");
        shell.SearchResults.Clear();
        document.RequestCodeLens(lens, lens.Position);
        await UntilAsync(() => shell.Status.Contains("symbol reference", StringComparison.Ordinal) || shell.Status.Contains("Project sources changed", StringComparison.Ordinal), "the clicked count's references");
        Assert.Contains("symbol reference", shell.Status);
        Assert.NotEmpty(shell.SearchResults);

        // --- The references are listed as the lines of code they are on, grouped under their files, with the name marked.
        Assert.All(shell.SearchResults, result => Assert.True(result.MatchStart >= 0 && result.Text.Substring(result.MatchStart, result.MatchLength).Contains("Count", StringComparison.Ordinal),
            $"A reference row shows its line with the name marked, not '{result.Text}'."));
        Assert.Contains("references to", shell.SearchResultsTitle);
        await UntilAsync(() => Descendants<Views.Shell.SearchPane>(window).Any(pane => pane.IsVisible && pane.ActualHeight > 0), "the Search pane with the references");
        var searchPane = Descendants<Views.Shell.SearchPane>(window).First(pane => pane.IsVisible);
        await Idle();
        // Rows scrolled out of view are not drawn, so some, not all.
        Assert.NotEmpty(Descendants<MatchTextBlock>(searchPane));
        Screenshot(searchPane, Path.Combine(root, "artifacts/screenshots/search-references.png"));

        // --- Find in workspace with its switches: whole words only, whatever their case.
        shell.SearchQuery = "count";
        shell.SearchWholeWord = true;
        await shell.SearchCommand.ExecuteAsync(null);
        Assert.Contains(shell.SearchResults, result => result.FileName == "CounterViewModel.cs" && result.Text.StartsWith("Count++;", StringComparison.Ordinal));
        Assert.All(shell.SearchResults, result => Assert.Equal("count", result.Text.Substring(result.MatchStart, result.MatchLength), ignoreCase: true));
        Assert.DoesNotContain(shell.SearchResults, result => result.Text == "namespace CounterApp;");
        Assert.StartsWith(shell.SearchResults.Count + " matches for “count” in ", shell.SearchResultsTitle);
        await Idle();
        Screenshot(searchPane, Path.Combine(root, "artifacts/screenshots/search-text.png"));
        shell.SearchWholeWord = false;

        // --- Signature help: a card for the call the caret is in, following it from argument to argument and closing outside the call.
        Keyboard.Focus(editor.TextArea);
        var content = document.State.Content;
        int call = content.IndexOf("Task.Delay(", StringComparison.Ordinal) + "Task.Delay(".Length;
        string CardText() => string.Join("\n", Descendants<TextBlock>(editor.SignatureHelp.Card).Select(block => string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text)) is { Length: > 0 } runs ? runs : block.Text));
        string ActiveParameter() => string.Concat(Descendants<TextBlock>(editor.SignatureHelp.Card).SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Run>()).Where(run => run.FontWeight == FontWeights.SemiBold).Select(run => run.Text));
        editor.CaretOffset = call + "200, ".Length;
        editor.SignatureHelp.Trigger();
        await UntilAsync(() => editor.SignatureHelp.IsOpen, "signature help for Task.Delay; focus within the editor: " + editor.IsKeyboardFocusWithin);
        await Idle();
        Assert.Contains("Task.Delay(", CardText());
        Assert.Contains("overloads", CardText());
        Assert.Equal("CancellationToken cancellationToken", ActiveParameter());
        Screenshot(editor.SignatureHelp.Card, Path.Combine(root, "artifacts/screenshots/signature-help.png"));
        editor.CaretOffset = call + 1;
        await UntilAsync(() => ActiveParameter().EndsWith(" millisecondsDelay", StringComparison.Ordinal) || ActiveParameter().EndsWith(" delay", StringComparison.Ordinal), "the card to follow the caret to the first argument, showing " + ActiveParameter());
        var before = CardText();
        Assert.True(editor.SignatureHelp.Cycle(1), "Down shows the next overload.");
        Assert.NotEqual(before, CardText());
        editor.CaretOffset = content.IndexOf("Count++", StringComparison.Ordinal);
        await UntilAsync(() => !editor.SignatureHelp.IsOpen, "signature help to close outside the call");

        // --- Replace in the file: Ctrl+H opens the find panel with its replace row.
        editor.ShowReplace();
        await Idle();
        var find = editor.FindPanel;
        Assert.False(find.IsClosed);
        Assert.True(Controls.Editor.EditorReplace.GetIsVisible(find));
        find.SearchPattern = "Count";
        Controls.Editor.EditorReplace.SetText(find, "Total");
        await Idle();
        var replaceBox = Descendants<TextBox>(find).Single(box => Controls.Editor.EditorReplace.GetIsReplaceBox(box));
        Assert.True(replaceBox.IsVisible);
        Assert.Equal("Total", replaceBox.Text);
        Screenshot(find, Path.Combine(root, "artifacts/screenshots/find-replace.png"));
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/editor-search-window.png"));
        find.Close();
        Controls.Editor.EditorReplace.SetIsVisible(find, false);
        Keyboard.Focus(editor.TextArea);

        // --- A file added to the project: the count still answers, saying what it may not cover, and the project model refreshes itself.
        var copy = Path.Combine(Path.GetTempPath(), "WpfStudio-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(sample)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(sample).Where(folder => Path.GetFileName(folder) is not ("bin" or "obj")))
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var project = Path.Combine(copy, "CounterApp.csproj");
        Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
        try
        {
            shell.ProjectRefreshPause = TimeSpan.FromMilliseconds(300);
            await shell.LoadWorkspaceAsync(project);
            var copiedViewModel = Path.Combine(copy, "CounterViewModel.cs");
            await shell.OpenDocumentAsync(copiedViewModel);
            var copied = shell.ActiveDocument!;
            await UntilAsync(() => copied.CodeLenses.Any(entry => entry.Name == "Count"), "the copy's reference counts");
            // The saved model's own refresh is a load too; wait for it so the one under test is the one a new file causes.
            await UntilAsync(() => shell.Workspace is { Cached: false } && !shell.XamlAnalysis.ProjectReloadRequired, "the project model of the copy");
            var loaded = shell.Workspace;

            var added = Path.Combine(copy, "Added.cs");
            await File.WriteAllTextAsync(added, "namespace CounterApp;\n\npublic class Added\n{\n    public int Read(CounterViewModel model) => model.Count;\n}\n");
            await UntilAsync(() => shell.XamlAnalysis.ProjectReloadRequired || !ReferenceEquals(shell.Workspace, loaded), "the watcher to report the new file");
            var counted = copied.CodeLenses.First(entry => entry.Name == "Count");
            shell.SearchResults.Clear();
            copied.RequestCodeLens(counted, counted.Position);
            await UntilAsync(() => shell.Status.Contains("symbol reference", StringComparison.Ordinal) || shell.Status.Contains("Project sources changed", StringComparison.Ordinal), "references while the project is out of date");
            Assert.Contains("symbol reference", shell.Status);

            // Nobody chose Reload workspace.
            await UntilAsync(() => !ReferenceEquals(shell.Workspace, loaded) && !shell.XamlAnalysis.ProjectReloadRequired
                && shell.Workspace!.Projects.SelectMany(item => item.Files).Any(file => file.Path.Equals(added, StringComparison.OrdinalIgnoreCase)), "the project model to refresh itself");
            await SettleAsync(copied);
            await UntilAsync(() => copied.CodeLensVersion == copied.State.Version && copied.CodeLenses.Any(entry => entry.Name == "Count"), "the counts after the refresh");
            counted = copied.CodeLenses.First(entry => entry.Name == "Count");
            shell.SearchResults.Clear();
            shell.ActiveDocument = copied;
            copied.RequestCodeLens(counted, counted.Position);
            await UntilAsync(() => shell.SearchResults.Any(result => result.Path.Equals(added, StringComparison.OrdinalIgnoreCase)), "the new file's use of Count among the references");
            Assert.DoesNotContain("Partial coverage", shell.Status);

            // From the new file, the property leads back to the view model.
            await shell.OpenDocumentAsync(added);
            var addedDocument = shell.ActiveDocument!;
            addedDocument.Navigate(addedDocument.State.Content.IndexOf("model.Count", StringComparison.Ordinal) + "model.Co".Length);
            await shell.GoToDefinitionCommand.ExecuteAsync(null);
            Assert.True(ReferenceEquals(shell.ActiveDocument, copied), "Expected the definition of Count in the view model; status was '" + shell.Status + "'.");
            Assert.Equal(copied.State.Content.IndexOf("Count { get; set; }", StringComparison.Ordinal), copied.State.CaretOffset);
        }
        finally
        {
            foreach (var open in shell.Documents.ToArray()) await shell.CloseDocumentAsync(open, discardChanges: true);
            try { Directory.Delete(copy, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The worker's folder; it goes with the temp directory. */ }
        }
    }
}
