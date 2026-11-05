using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit.CodeCompletion;
using WpfStudio.App.Controls;
using WpfStudio.App.ViewModels;
using WpfStudio.App.Views;
using WpfStudio.Core;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    /// <summary>
    /// Experimental Code Assist through the real settings page, popup and editor: off and unloaded until asked for, then
    /// recommendations from Roslyn's real list, committed by the real edit path, and a line suggestion taken with Tab.
    /// </summary>
    private static async Task VerifyCodeAssistAsync(string root, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        var engine = shell.CodeAssist.Engine;
        string shots = Path.Combine(root, "artifacts/screenshots");
        Assert.False(shell.CodeAssist.Enabled);
        Assert.False(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), module => module.ModuleName.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase));

        await shell.OpenSettingsCommand.ExecuteAsync(ShellViewModel.ExperimentalPage);
        await Idle();
        var settings = Descendants<SettingsView>(window).Single();
        var enabled = Bound<CheckBox>(settings, CheckBox.IsCheckedProperty, "CodeAssist.Enabled");
        var lines = Bound<CheckBox>(settings, CheckBox.IsCheckedProperty, "CodeAssist.LineSuggestions");
        var memberVersion = Bound<ComboBox>(settings, ComboBox.SelectedValueProperty, "CodeAssist.MemberModelVersion");
        Assert.True(enabled.IsVisible);
        Assert.False(enabled.IsChecked);
        Assert.False(lines.IsEnabled);
        Assert.False(memberVersion.IsEnabled);
        Screenshot((FrameworkElement)window.Content, Path.Combine(shots, "code-assist-settings-off.png"));

        // Anyone deciding whether to turn a model on can read about every version first, and reading selects nothing.
        var about = Descendants<Expander>(settings).Single(expander => AutomationProperties.GetName(expander) == "About the line suggestion model versions");
        Assert.True(about.IsEnabled);
        about.IsExpanded = true;
        await Idle();
        var versions = Descendants<Expander>(about).ToList();
        Assert.Equal(shell.CodeAssist.LineModelVersions.Count, versions.Count);
        versions[0].IsExpanded = true;
        await Idle();
        Assert.Contains(Descendants<TextBlock>(versions[0]), text => text.IsVisible && text.Text == "How it scored");
        Screenshot((FrameworkElement)window.Content, Path.Combine(shots, "code-assist-settings-versions.png"));
        Assert.Equal(CodeAssistSettings.LatestVersion, shell.CodeAssist.LineModelVersion);
        Assert.False(engine.SuggestsLines);
        about.IsExpanded = false;
        await Idle();

        enabled.IsChecked = true;
        await Idle();
        Assert.True(shell.CodeAssist.Enabled);
        Assert.True(lines.IsEnabled);
        Assert.False(lines.IsChecked);
        Assert.Equal(3, memberVersion.Items.Count);
        Assert.Equal("latest", memberVersion.SelectedValue);
        await UntilAsync(() => shell.CodeAssist.Status.Contains("code-assist-members 2.0.0 is loaded", StringComparison.Ordinal), "the newest model");
        memberVersion.SelectedValue = "1.0.0";
        await UntilAsync(() => shell.CodeAssist.Status.Contains("code-assist-members 1.0.0 is loaded", StringComparison.Ordinal), "the chosen version");
        memberVersion.SelectedValue = "latest";
        await UntilAsync(() => shell.CodeAssist.Status.Contains("code-assist-members 2.0.0 is loaded", StringComparison.Ordinal), "the newest model again");
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(shots, "code-assist-settings-on.png"));
        shell.CloseSettingsCommand.Execute(null);

        await shell.LoadWorkspaceAsync(Path.Combine(root, "samples/CounterApp/CounterApp.csproj"));
        await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/CounterViewModel.cs"));
        await Idle();
        var document = shell.ActiveDocument!;
        // Docking moves focus while it replaces the welcome tab, which would close a popup opened before it finishes.
        await UntilAsync(() => ReferenceEquals(manager.ActiveContent, document) && !manager.Layout.Descendents().OfType<LayoutDocument>().Any(item => item.ContentId == "Welcome"), "the editor to become the active tab");
        await SettleAsync(document);
        var editor = Descendants<EditorSurface>(window).Single(item => ReferenceEquals(item.ViewModel, document));
        Keyboard.Focus(editor.TextArea);
        Assert.True(editor.IsKeyboardFocusWithin, "The editor needs keyboard focus for its popups.");
        try
        {
            // A recommendation is selected, and takes a confirmed key, but typed punctuation passes it by.
            const string declaration = "var names = new System.Collections.Generic.List<string>();\n        // Clear out the list\n        ";
            int at = document.State.Content.IndexOf("Count++;", StringComparison.Ordinal);
            editor.Document.Insert(at, declaration + "names\n        ");
            editor.CaretOffset = at + declaration.Length + "names".Length;
            // A semantic change follows the first request after a load, and cancels that request before its popup opens.
            await document.CompleteAsync(editor.CaretOffset);
            await SettleAsync(document);
            editor.TextArea.PerformTextInput(".");
            var popup = await PopupAsync();
            var entries = popup.CompletionList.CompletionData;
            Assert.Equal("Clear", entries[0].Text);
            Assert.Equal("★ Clear", entries[0].Content);
            Assert.Equal(5, entries.Count(entry => entry.Content is string text && text.StartsWith('★')));
            Assert.Same(entries[0], popup.CompletionList.SelectedItem);
            Assert.Contains("code-assist-members 2.0.0", entries[0].Description?.ToString(), StringComparison.Ordinal);
            await Idle();
            Screenshot((FrameworkElement)popup.Content, Path.Combine(shots, "code-assist-popup.png"));
            Screenshot((FrameworkElement)window.Content, Path.Combine(shots, "code-assist-editor.png"));

            editor.TextArea.PerformTextInput(";");
            await UntilAsync(() => !Application.Current.Windows.OfType<CompletionWindow>().Any(), "the popup to close");
            int typed = document.State.Content.IndexOf("names.;", StringComparison.Ordinal);
            Assert.True(typed >= 0, "Punctuation should have passed the recommendation by.");
            editor.Document.Remove(typed + "names".Length, ".;".Length);
            editor.CaretOffset = typed + "names".Length;

            // Filtering keeps the recommendation selected, and the commit goes through Roslyn's own change.
            editor.TextArea.PerformTextInput(".");
            popup = await PopupAsync();
            editor.TextArea.PerformTextInput("C");
            await Idle();
            Assert.Equal("Clear", ((ICompletionData)popup.CompletionList.SelectedItem).Text);
            popup.CompletionList.RequestInsertion(EventArgs.Empty);
            await UntilAsync(() => document.State.Content.Contains("names.Clear", StringComparison.Ordinal), "the committed recommendation");

            // A suggestion waits for a pause, shows without changing the text, and Tab takes it.
            Assert.Null(await document.SuggestLineAsync(editor.CaretOffset));
            shell.CodeAssist.LineSuggestions = true;
            await UntilAsync(() => shell.CodeAssist.Status.Contains("code-assist-lines 4.0.0 is loaded", StringComparison.Ordinal), "the line model");
            const string property = "public string Title { get; ";
            int member = document.State.Content.IndexOf("[RelayCommand]", StringComparison.Ordinal);
            editor.Document.Insert(member, "public string Name { get; set; }\n    \n    ");
            editor.CaretOffset = member + "public string Name { get; set; }\n    ".Length;
            editor.TextArea.PerformTextInput(property);
            await UntilAsync(() => editor.LineSuggestion.IsVisible, "the line suggestion");
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(shots, "code-assist-line-suggestion.png"));
            Press(editor, Key.Tab);
            Assert.False(editor.LineSuggestion.IsVisible);
            Assert.Contains(property + "set; }", document.State.Content, StringComparison.Ordinal);
            Assert.Equal(document.State.Content.IndexOf(property, StringComparison.Ordinal) + property.Length + "set; }".Length, editor.CaretOffset);

            // A long whole line, which is what the newest model is for, and Tab takes all of it at once.
            const string signature = "public object Convert(object value, ";
            const string rest = "Type targetType, object parameter, CultureInfo culture)";
            editor.Document.Insert(editor.CaretOffset, "\n    ");
            editor.TextArea.PerformTextInput(signature);
            await UntilAsync(() => editor.LineSuggestion.IsVisible, "the whole line");
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(shots, "code-assist-whole-line.png"));
            Press(editor, Key.Tab);
            Assert.Contains(signature + rest, document.State.Content, StringComparison.Ordinal);
            Assert.Equal(document.State.Content.IndexOf(signature, StringComparison.Ordinal) + signature.Length + rest.Length, editor.CaretOffset);

            // Escape dismisses a suggestion without touching the text.
            string accepted = document.State.Content;
            editor.LineSuggestion.Show(" // note", editor.CaretOffset);
            Press(editor, Key.Escape);
            Assert.False(editor.LineSuggestion.IsVisible);
            Assert.Equal(accepted, document.State.Content);

            // Off again: the same request shows Roslyn's list, without stars or suggestions.
            enabled.IsChecked = false;
            await Idle();
            Assert.False(engine.RanksMembers);
            Assert.False(document.SuggestsLines);
            Assert.Contains("No model is loaded", shell.CodeAssist.Status, StringComparison.Ordinal);
            var plain = await document.CompleteAsync(document.State.Content.IndexOf("names.Clear", StringComparison.Ordinal) + "names.".Length);
            Assert.NotEmpty(plain.Items);
            Assert.DoesNotContain(plain.Items, item => item.Recommended);
        }
        finally
        {
            foreach (var open in Application.Current.Windows.OfType<CompletionWindow>().ToArray()) open.Close();
            shell.CodeAssist.Enabled = false;
            await shell.CloseDocumentAsync(document, discardChanges: true);
        }
    }

    // Symbols changing close an open popup, and rightly so. Waits for the document to go quiet.
    private static async Task SettleAsync(EditorViewModel document)
    {
        var quiet = Stopwatch.StartNew();
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => quiet.Restart();
        document.PropertyChanged += Changed;
        try { await UntilAsync(() => quiet.Elapsed > TimeSpan.FromSeconds(2), "the document to settle"); }
        finally { document.PropertyChanged -= Changed; }
        await Idle();
    }

    private static T Bound<T>(DependencyObject owner, DependencyProperty property, string path) where T : FrameworkElement =>
        Descendants<T>(owner).Single(element => BindingOperations.GetBinding(element, property)?.Path.Path == path);

    private static async Task<CompletionWindow> PopupAsync()
    {
        await UntilAsync(() => Application.Current.Windows.OfType<CompletionWindow>().Any(), "the completion popup");
        await Idle();
        return Application.Current.Windows.OfType<CompletionWindow>().Single();
    }

    private static void Press(EditorSurface editor, Key key) =>
        editor.TextArea.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
}
