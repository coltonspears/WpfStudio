using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.Services;

namespace WpfStudio.App.Controls;

/// <summary>Applies the view model's <see cref="EditorPreferences"/> to this editor, live, and zooms the editor font with Ctrl+wheel.</summary>
public sealed partial class EditorSurface
{
    private EditorPreferences? _observedPreferences;

    // XAML creates this control, so it cannot take the instance in a constructor. The view model is how it reaches everything
    // else, and unlike an inherited property or an ancestor binding that also works in a floating document window.
    private void ObservePreferences(bool observe)
    {
        var preferences = observe ? ViewModel?.Preferences : null;
        if (ReferenceEquals(preferences, _observedPreferences)) return;
        if (_observedPreferences is { } previous) previous.PropertyChanged -= PreferencesChanged;
        _observedPreferences = preferences;
        if (preferences is null) return;
        preferences.PropertyChanged += PreferencesChanged;
        ApplyPreferences(preferences);
    }

    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e) { if (_observedPreferences is { } preferences) ApplyPreferences(preferences); }

    private void ApplyPreferences(EditorPreferences preferences)
    {
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
            if (ViewModel?.Preferences is { } preferences) (e.Delta > 0 ? preferences.ZoomInCommand : preferences.ZoomOutCommand).Execute(null);
            e.Handled = true;
        };
    }
}
