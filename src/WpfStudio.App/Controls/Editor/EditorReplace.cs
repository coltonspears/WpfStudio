using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
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
        bool replacedEmpty = false;
        var current = Current(panel, area, strategy);
        panel.ClearValue(ShownProperty);
        if (current is not null)
        {
            var replacement = Replacement(panel, current);
            document.Replace(current.Offset, current.Length, replacement);
            area.ClearSelection();
            area.Caret.Offset = current.Offset + replacement.Length;
            replaced = 1;
            replacedEmpty = current.Length == 0;
        }
        // From the caret on, then from the top: the panel's own Find next skips a match that starts at the caret.
        int from = Math.Min(area.Caret.Offset, document.TextLength);
        var next = After(strategy, document, from, replacedEmpty) ?? strategy.FindNext(document, 0, document.TextLength);
        // Coming round from the top to the place just replaced is the end, not another match.
        if (replacedEmpty && next is { Length: 0 } && next.Offset == from) next = null;
        if (next is null) return new(replaced, replaced == 1 ? "Replaced the last occurrence" : "Nothing to replace: no match for the search text");
        area.Caret.Offset = next.Offset + next.Length;
        if (next.Length > 0) area.Selection = Selection.Create(area, next.Offset, next.Offset + next.Length);
        else
        {
            area.ClearSelection();
            panel.SetValue(ShownProperty, new Shown(next.Offset, document.Version));
        }
        area.Caret.BringCaretToView();
        area.Caret.Show(); // The keyboard focus is in the panel.
        return new(replaced, replaced == 1 ? "Replaced 1 occurrence" : next.Length == 0 ? "The next match is at the caret and has no length; Replace inserts there" : "");
    }

    // A match of no length, as ^ or a lookahead finds, cannot be shown by selecting it: the caret is put there, and where
    // that was is kept so that the next press replaces it. The caret or the text moving on forgets it.
    private sealed record Shown(int Offset, ITextSourceVersion Version);
    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(Shown), typeof(EditorReplace));

    // The match a press replaces: the one that is selected, or one of no length last shown at the caret.
    private static ISearchResult? Current(SearchPanel panel, TextArea area, ISearchStrategy strategy)
    {
        var document = area.Document;
        if (area.Selection.SurroundingSegment is { Length: > 0 } selection)
            return strategy.FindAll(document, selection.Offset, selection.Length).FirstOrDefault(match => match.Offset == selection.Offset && match.Length == selection.Length);
        if (panel.GetValue(ShownProperty) is not Shown shown || shown.Offset != area.Caret.Offset
            || !shown.Version.BelongsToSameDocumentAs(document.Version) || shown.Version.CompareAge(document.Version) != 0) return null;
        return strategy.FindAll(document, shown.Offset, 0).FirstOrDefault(match => match.Length == 0);
    }

    // The first match from an offset on. After a match of no length was replaced, the place it was at is not matched empty
    // again, as a regular expression moves on after an empty match: otherwise a lookahead would be replaced there for ever.
    private static ISearchResult? After(ISearchStrategy strategy, TextDocument document, int from, bool afterEmpty)
    {
        var next = strategy.FindNext(document, from, document.TextLength - from);
        if (afterEmpty && next is { Length: 0 } && next.Offset == from)
            next = from < document.TextLength ? strategy.FindNext(document, from + 1, document.TextLength - from - 1) : null;
        return next;
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
