using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.Services;

namespace WpfStudio.App.Controls;

/// <summary>Applies <see cref="EditorPreferences"/> to this editor, live, and zooms the editor font with Ctrl+wheel.</summary>
public sealed partial class EditorSurface
{
    private bool _preferencesObserved;

    private void ObservePreferences(bool observe)
    {
        if (observe == _preferencesObserved) return;
        _preferencesObserved = observe;
        if (observe) { EditorPreferences.Current.PropertyChanged += PreferencesChanged; ApplyPreferences(); }
        else EditorPreferences.Current.PropertyChanged -= PreferencesChanged;
    }

    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e) => ApplyPreferences();

    private void ApplyPreferences()
    {
        var preferences = EditorPreferences.Current;
        FontFamily = preferences.EditorFont;
        FontSize = preferences.FontSize;
        Options.IndentationSize = preferences.TabSize;
        Options.ConvertTabsToSpaces = preferences.InsertSpaces;
        // Hidden rather than removed, so the breakpoint and change margins keep their places beside it.
        foreach (var numbers in TextArea.LeftMargins.OfType<LineNumberMargin>()) numbers.Visibility = preferences.ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
        Options.HighlightCurrentLine = preferences.HighlightCurrentLine;
        WordWrap = preferences.WordWrap;
        Options.ShowSpaces = Options.ShowTabs = preferences.ShowWhitespace;
        Options.ShowEndOfLine = preferences.ShowLineEndings;
        Options.ShowColumnRuler = preferences.ShowColumnRuler;
        Options.ColumnRulerPosition = preferences.ColumnRulerPosition;
        Options.AllowScrollBelowDocument = preferences.ScrollBelowDocument;
        Options.CutCopyWholeLine = preferences.CutCopyWholeLine;
        Options.EnableTextDragDrop = preferences.EnableTextDragDrop;
        Options.HideCursorWhileTyping = preferences.HideCursorWhileTyping;
        if (ViewModel is { } vm && ReferenceEquals(_attached, vm))
        {
            if (preferences.ShowFolding && _folding is null) InstallFolding();
            else if (!preferences.ShowFolding && _folding is not null) UninstallFolding(vm.State);
        }
    }

    /// <summary>Applies tidying edits as one undoable change, so the caret and selection stay in place.</summary>
    private bool ApplyReplacements(IReadOnlyList<WpfStudio.Core.Text.TextReplacement> edits)
    {
        if (ViewModel is null || IsReadOnly || Text != ViewModel.State.Content) return false;
        using (Document.RunUpdate())
            foreach (var edit in edits.OrderByDescending(edit => edit.Start)) Document.Replace(edit.Start, edit.Length, edit.Text);
        return true;
    }

    // Whitespace marks and the column guide use quiet theme colours; the brushes change colour with the theme.
    private void InitializePreferenceChrome()
    {
        TextArea.TextView.SetResourceReference(ICSharpCode.AvalonEdit.Rendering.TextView.NonPrintableCharacterBrushProperty, "FoldMarkerBrush");
        if (TryFindResource("StrongBorderBrush") is Brush ruler) TextArea.TextView.ColumnRulerPen = new Pen(ruler, 1);
        PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Delta > 0) EditorPreferences.Current.ZoomInCommand.Execute(null); else EditorPreferences.Current.ZoomOutCommand.Execute(null);
            e.Handled = true;
        };
    }
}
