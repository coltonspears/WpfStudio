using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit.CodeCompletion;
using WpfStudio.App.Controls;
using WpfStudio.App.Controls.Editor;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    /// <summary>
    /// The completion popup in the real editor, opened on a typed letter: every name fits, each row says where it comes
    /// from, and the selected entry is described beside the list. Punctuation still passes by what was only selected for that.
    /// Typing a comment leaves the rows above declarations and the project's XAML results alone.
    /// </summary>
    private static async Task VerifyCompletionPopupAsync(string root, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        string shots = Path.Combine(root, "artifacts/screenshots");
        await shell.LoadWorkspaceAsync(Path.Combine(root, "samples/CounterApp/CounterApp.csproj"));
        await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/CounterViewModel.cs"));
        await Idle();
        var document = shell.ActiveDocument!;
        await UntilAsync(() => ReferenceEquals(manager.ActiveContent, document) && !manager.Layout.Descendents().OfType<LayoutDocument>().Any(item => item.ContentId == "welcome"), "the document tab");
        await SettleAsync(document);
        var editor = Descendants<EditorSurface>(window).Single(item => ReferenceEquals(item.ViewModel, document));
        Keyboard.Focus(editor.TextArea);
        Assert.True(editor.IsKeyboardFocusWithin, "The editor needs keyboard focus for its popups.");
        try
        {
            // Blame waits for a pause in typing. Until then the rows above declarations keep their Git history.
            var view = editor.TextArea.TextView;
            int History() => Descendants<TextBlock>(view).Count(block => block.ToolTip is string tip && tip.StartsWith("Show who changed", StringComparison.Ordinal));
            await UntilAsync(() => document.BlameVersion == document.State.Version && History() > 0, "Git history above the declarations");
            int shown = History();
            await UntilAsync(() => shell.XamlAnalysis.Status.StartsWith("XAML checked", StringComparison.Ordinal), "the project's XAML to be checked");
            string checkedXaml = shell.XamlAnalysis.Status;
            int comment = document.State.Content.IndexOf("Set a breakpoint here", StringComparison.Ordinal);
            editor.CaretOffset = comment;
            editor.TextArea.PerformTextInput("x");
            await Idle();
            Assert.NotEqual(document.BlameVersion, document.State.Version);
            Assert.Equal(shown, History());
            // A comment cannot matter to XAML, so its results stand and no scan starts after the usual pause.
            Assert.Equal(checkedXaml, shell.XamlAnalysis.Status);
            await Task.Delay(1000);
            Assert.Equal(checkedXaml, shell.XamlAnalysis.Status);
            editor.Document.Remove(comment, 1);
            await SettleAsync(document);

            int at = document.State.Content.IndexOf("[RelayCommand]", StringComparison.Ordinal);
            editor.Document.Insert(at, "[\n    ");
            editor.CaretOffset = at + 1;
            // A semantic change follows the first request after a load, and cancels that request before its popup opens.
            await document.CompleteAsync(editor.CaretOffset);
            await SettleAsync(document);

            editor.TextArea.PerformTextInput("O");
            var popup = await PopupAsync();
            await UntilAsync(() => Descendants<CompletionLabel>(popup).Any(), "the rows");
            await Idle();

            // Opened with something selected, so there is something to read, and no name is cut off.
            var selected = Assert.IsAssignableFrom<ICompletionData>(popup.CompletionList.SelectedItem);
            Assert.StartsWith("O", selected.Text, StringComparison.Ordinal);
            var labels = Descendants<CompletionLabel>(popup).ToArray();
            Assert.All(labels, label =>
                Assert.True(label.TranslatePoint(new Point(label.ActualWidth, 0), popup).X <= popup.ActualWidth, $"{label.Label} is cut off in a popup {popup.ActualWidth} wide."));

            // Each row says what it is and where it comes from.
            var obsolete = labels.Single(label => label.Label == "Obsolete");
            var row = Ancestor<ListBoxItem>(obsolete);
            var texts = Descendants<TextBlock>(row).Select(block => block.Text).ToArray();
            Assert.Contains("System", texts);
            Assert.Equal("SymbolClass", Descendants<Glyph>(row).Single().Kind);

            // The selected entry is described, in a card that fills in once the worker has answered.
            var card = Assert.IsAssignableFrom<FrameworkElement>(selected.Description);
            await UntilAsync(() => Descendants<TextBlock>(card).Any(block => new TextRange(block.ContentStart, block.ContentEnd).Text.Contains("ObsoleteAttribute", StringComparison.Ordinal)), "the entry's details");
            await Idle();
            Screenshot((FrameworkElement)popup.Content, Path.Combine(shots, "completion-popup.png"));
            if (card.IsLoaded) Screenshot(card, Path.Combine(shots, "completion-details.png"));

            // Only selected so its details show: punctuation passes it by.
            editor.TextArea.PerformTextInput("]");
            await UntilAsync(() => !Application.Current.Windows.OfType<CompletionWindow>().Any(), "the popup to close");
            Assert.Contains("[O]", document.State.Content, StringComparison.Ordinal);
            editor.Document.Remove(at + 1, "O]".Length);
            editor.CaretOffset = at + 1;
            await SettleAsync(document);

            // Typing on narrows the list and says which entry is meant, so punctuation takes that one.
            editor.TextArea.PerformTextInput("O");
            popup = await PopupAsync();
            editor.TextArea.PerformTextInput("b");
            await Idle();
            Assert.Equal("Obsolete", ((ICompletionData)popup.CompletionList.SelectedItem).Text);
            editor.TextArea.PerformTextInput("]");
            await UntilAsync(() => document.State.Content.Contains("[Obsolete]", StringComparison.Ordinal), "the committed entry");

            // A list that is not code completion, such as the SQL editor's, keeps showing its own entries.
            var other = new CompletionWindow(editor.TextArea);
            other.CompletionList.CompletionData.Add(new PlainCompletion());
            other.Show();
            await Idle();
            Assert.Contains(Descendants<TextBlock>(other), block => block.Text == PlainCompletion.Name);
            other.Close();

            // A method body elsewhere can use this file's symbols. Its counts wait until its tab is showing again.
            await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/MainWindow.xaml.cs"));
            var window2 = shell.ActiveDocument!;
            await UntilAsync(() => window2.CodeLensVersion == window2.State.Version, "the counts for the other file");
            // The first update of a newly opened file counts as a change to the model, so let that recount finish.
            await Task.Delay(3000);
            await UntilAsync(() => document.CodeLensVersion == document.State.Version, "the counts for this text");
            int counted = document.CodeLenses.Single(entry => entry.Name == "Count").References;
            window2.State.Content = window2.State.Content.Replace("InitializeComponent()", "_ = new CounterViewModel().Count", StringComparison.Ordinal);
            await UntilAsync(() => window2.CodeLensVersion == window2.State.Version, "the other file's edit to reach the worker");
            Assert.Equal(counted, document.CodeLenses.Single(entry => entry.Name == "Count").References);
            shell.ActiveDocument = document;
            await UntilAsync(() => document.CodeLenses.Single(entry => entry.Name == "Count").References == counted + 1, "the count to catch up when its tab shows");
            await shell.CloseDocumentAsync(window2, discardChanges: true);
        }
        finally
        {
            foreach (var open in Application.Current.Windows.OfType<CompletionWindow>().ToArray()) open.Close();
            await shell.CloseDocumentAsync(document, discardChanges: true);
        }
    }

    private sealed class PlainCompletion : ICompletionData
    {
        public const string Name = "plain entry";
        public System.Windows.Media.ImageSource? Image => null;
        public string Text => Name;
        public object Content => Name;
        public object Description => Name;
        public double Priority => 0;
        public void Complete(ICSharpCode.AvalonEdit.Editing.TextArea textArea, ICSharpCode.AvalonEdit.Document.ISegment completionSegment, EventArgs insertionRequestEventArgs) { }
    }

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T found) return found;
        throw new InvalidOperationException($"No {typeof(T).Name} above {element}.");
    }
}
