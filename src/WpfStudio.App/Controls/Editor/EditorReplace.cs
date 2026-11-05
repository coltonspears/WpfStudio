using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Search;

namespace WpfStudio.App.Controls.Editor;

/// <summary>
/// Replace for the editor's find panel. The panel finds; this adds a row under it with the replacement text, Replace and
/// Replace all, using the panel's own pattern and its case, whole-word and regular-expression switches. The row and its
/// commands are named here so the panel's template in StudioTheme.xaml can bind to them.
/// </summary>
public static class EditorReplace
{
    public static readonly RoutedUICommand ReplaceNext = new("Replace", nameof(ReplaceNext), typeof(EditorReplace));
    public static readonly RoutedUICommand ReplaceAll = new("Replace all", nameof(ReplaceAll), typeof(EditorReplace));

    /// <summary>Set on a find panel whose editor can replace, which is what shows the switch for the replace row.</summary>
    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.RegisterAttached("IsAvailable", typeof(bool), typeof(EditorReplace), new PropertyMetadata(false));
    public static readonly DependencyProperty IsVisibleProperty = DependencyProperty.RegisterAttached("IsVisible", typeof(bool), typeof(EditorReplace),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(EditorReplace),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    /// <summary>Marks the replacement text box: Enter in it replaces, where in the rest of the panel it finds.</summary>
    public static readonly DependencyProperty IsReplaceBoxProperty = DependencyProperty.RegisterAttached("IsReplaceBox", typeof(bool), typeof(EditorReplace), new PropertyMetadata(false, ReplaceBoxChanged));

    public static bool GetIsAvailable(DependencyObject target) => (bool)target.GetValue(IsAvailableProperty);
    public static void SetIsAvailable(DependencyObject target, bool value) => target.SetValue(IsAvailableProperty, value);
    public static bool GetIsVisible(DependencyObject target) => (bool)target.GetValue(IsVisibleProperty);
    public static void SetIsVisible(DependencyObject target, bool value) => target.SetValue(IsVisibleProperty, value);
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static bool GetIsReplaceBox(DependencyObject target) => (bool)target.GetValue(IsReplaceBoxProperty);
    public static void SetIsReplaceBox(DependencyObject target, bool value) => target.SetValue(IsReplaceBoxProperty, value);

    private static void ReplaceBoxChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBox box) return;
        box.PreviewKeyDown -= ReplaceBoxKeyDown;
        if (args.NewValue is true) box.PreviewKeyDown += ReplaceBoxKeyDown;
    }

    private static void ReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        var command = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) ? ReplaceAll : ReplaceNext;
        if (command.CanExecute(null, box)) command.Execute(null, box);
        e.Handled = true;
    }

    /// <summary>What replacing did, for the status line.</summary>
    internal readonly record struct Outcome(int Replaced, string Status);

    /// <summary>Gives a find panel its replace row: the commands act on the editor the panel belongs to.</summary>
    internal static void Attach(SearchPanel panel, TextArea area, Func<bool> canEdit, Action<string> report)
    {
        SetIsAvailable(panel, true);
        void CanReplace(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = canEdit() && !string.IsNullOrEmpty(panel.SearchPattern); e.Handled = true; }
        panel.CommandBindings.Add(new CommandBinding(ReplaceNext, (_, e) => { e.Handled = true; report(Next(panel, area).Status); }, CanReplace));
        panel.CommandBindings.Add(new CommandBinding(ReplaceAll, (_, e) => { e.Handled = true; report(All(panel, area).Status); }, CanReplace));
    }

    /// <summary>
    /// Replaces the match that is selected and selects the next one. With no match selected, which is how it is before the
    /// first press, it only selects the next one, so what is about to be replaced is always seen first.
    /// </summary>
    internal static Outcome Next(SearchPanel panel, TextArea area)
    {
        if (Strategy(panel, out var problem) is not { } strategy) return new(0, problem);
        var document = area.Document;
        int replaced = 0;
        var selection = area.Selection.SurroundingSegment;
        if (selection is { Length: > 0 } && strategy.FindAll(document, selection.Offset, selection.Length).FirstOrDefault() is { } current
            && current.Offset == selection.Offset && current.Length == selection.Length)
        {
            var replacement = Replacement(panel, current);
            document.Replace(current.Offset, current.Length, replacement);
            area.ClearSelection();
            area.Caret.Offset = current.Offset + replacement.Length;
            replaced = 1;
        }
        // From the caret on, then from the top: the panel's own Find next skips a match that starts at the caret.
        var next = strategy.FindNext(document, Math.Min(area.Caret.Offset, document.TextLength), document.TextLength)
            ?? strategy.FindNext(document, 0, document.TextLength);
        if (next is null) return new(replaced, replaced == 1 ? "Replaced the last occurrence" : "Nothing to replace: no match for the search text");
        area.Caret.Offset = next.Offset + next.Length;
        area.Selection = Selection.Create(area, next.Offset, next.Offset + next.Length);
        area.Caret.BringCaretToView();
        area.Caret.Show(); // The keyboard focus is in the panel.
        return new(replaced, replaced == 1 ? "Replaced 1 occurrence" : "");
    }

    /// <summary>Replaces every match in the document as one undoable change.</summary>
    internal static Outcome All(SearchPanel panel, TextArea area)
    {
        if (Strategy(panel, out var problem) is not { } strategy) return new(0, problem);
        var document = area.Document;
        var matches = strategy.FindAll(document, 0, document.TextLength).ToArray();
        if (matches.Length == 0) return new(0, "Nothing to replace: no match for the search text");
        using (document.RunUpdate())
            foreach (var match in matches.Reverse()) document.Replace(match.Offset, match.Length, Replacement(panel, match));
        area.ClearSelection();
        return new(matches.Length, matches.Length == 1 ? "Replaced 1 occurrence" : $"Replaced {matches.Length} occurrences");
    }

    // The panel's switches, read when a command runs so they are always the ones on screen.
    private static ISearchStrategy? Strategy(SearchPanel panel, out string problem)
    {
        problem = "";
        if (string.IsNullOrEmpty(panel.SearchPattern)) { problem = "Type what to find first"; return null; }
        try { return SearchStrategyFactory.Create(panel.SearchPattern, !panel.MatchCase, panel.WholeWords, panel.UseRegex ? SearchMode.RegEx : SearchMode.Normal); }
        catch (SearchPatternException exception) { problem = "The search pattern is not valid: " + exception.Message; return null; }
    }

    // A regular expression's replacement may use $1 and the like. Plain text is inserted as typed, dollar signs included.
    private static string Replacement(SearchPanel panel, ISearchResult match) => panel.UseRegex ? match.ReplaceWith(GetText(panel)) : GetText(panel);
}
