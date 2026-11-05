using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using WpfStudio.App.Controls.Editor;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Git;

[assembly: InternalsVisibleTo("WpfStudio.Shell.Tests")]
[assembly: InternalsVisibleTo("WpfStudio.App.Tests")]

namespace WpfStudio.App.Controls;

/// <summary>Presentation adapter for AvalonEdit: keeps the buffer, caret and decorations in step with the view model and hosts the editor's collaborators.</summary>
public sealed partial class EditorSurface : TextEditor
{
    private static readonly ConditionalWeakTable<DocumentState, TextDocument> Buffers = new();
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel), typeof(EditorViewModel), typeof(EditorSurface), new PropertyMetadata(null, Changed));
    private EditorViewModel? _attached;
    private bool _syncing;
    private readonly DebugMargin _margin;
    private readonly DiagnosticRenderer _diagnostics;
    private readonly SemanticColorizer _semantic = new();
    private readonly InlayHintGenerator _hints;
    private readonly EditorAnnotations _annotations;
    private readonly EditorFolding _folding;
    private readonly EditorHover _hover;
    private readonly EditorCompletion _completion;
    private readonly EditorLineSuggestion _lineSuggestion;
    private TextDocument? _trackedDocument;
    public EditorSurface()
    {
        FontFamily = new FontFamily("Cascadia Code, Consolas"); FontSize = 13; ShowLineNumbers = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.ConvertTabsToSpaces = true; Options.IndentationSize = 4; Options.HighlightCurrentLine = true;
        Options.EnableHyperlinks = false; Options.EnableEmailHyperlinks = false;
        Padding = new Thickness(0, 6, 8, 8);
        // Theme-aware editor chrome: current line, selection and a quiet gutter without the dotted rule.
        TextArea.TextView.SetResourceReference(ICSharpCode.AvalonEdit.Rendering.TextView.CurrentLineBackgroundProperty, "EditorLineBrush");
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "EditorSelectionBrush");
        TextArea.SelectionBorder = null;
        TextArea.SelectionForeground = null;
        TextArea.SelectionCornerRadius = 2;
        foreach (var rule in TextArea.LeftMargins.OfType<System.Windows.Shapes.Line>().ToArray()) rule.Visibility = Visibility.Collapsed;
        foreach (var numbers in TextArea.LeftMargins.OfType<LineNumberMargin>()) numbers.Margin = new Thickness(6, 0, 14, 0);
        SearchPanel.Install(TextArea);
        _margin = new DebugMargin(this); TextArea.LeftMargins.Insert(0, _margin);
        _diagnostics = new DiagnosticRenderer(this); TextArea.TextView.BackgroundRenderers.Add(_diagnostics);
        TextArea.TextView.LineTransformers.Add(_semantic);
        _hints = new InlayHintGenerator(this); TextArea.TextView.ElementGenerators.Add(_hints);
        _annotations = new EditorAnnotations(this);
        InitializePreferenceChrome();
        _folding = new EditorFolding(this);
        _hover = new EditorHover(this);
        _typing = new EditorTyping(this);
        _completion = new EditorCompletion(this, _hover, _typing);
        _lineSuggestion = new EditorLineSuggestion(this, () => _completion.IsWindowOpen || _completion.IsPending); TextArea.TextView.ElementGenerators.Add(_lineSuggestion);
        TextChanged += OnEditorTextChanged;
        TextArea.Caret.PositionChanged += (_, _) => { _lineSuggestion.OnCaretMoved(); if (ViewModel is { } vm && !_syncing) vm.ReportCaret(TextArea.Caret.Line, TextArea.Caret.Column, CaretOffset); };
        TextArea.TextEntered += _completion.OnTextEntered;
        TextArea.SelectionChanged += (_, _) => ViewModel?.UpdateSelection(SelectionStart, SelectionLength);
        PreviewMouseRightButtonDown += (_, e) =>
        {
            if (SelectionLength == 0 && GetPositionFromPoint(e.GetPosition(this)) is { } position) CaretOffset = Document.GetOffset(position.Location);
        };
        TextArea.TextEntering += _completion.OnTextEntering;
        CommandManager.AddPreviewExecutedHandler(TextArea, _completion.OnPreviewExecuted);
        InitializeEditing();
        PreviewMouseDown += (_, _) => { _completion.CancelPending(); _hover.Cancel(); };
        LostKeyboardFocus += (_, _) => { _completion.CancelPending(); _lineSuggestion.Cancel(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            ObservePreferences(true);
            Attach();
            // An owned popup stays above other applications unless it is closed with its window.
            if (Window.GetWindow(this) is { } window) { window.Deactivated -= OnWindowDeactivated; window.Deactivated += OnWindowDeactivated; }
        };
        Unloaded += (_, _) => { Detach(); ObservePreferences(false); if (Window.GetWindow(this) is { } window) window.Deactivated -= OnWindowDeactivated; };
        MouseHover += _hover.OnMouseHover;
        MouseHoverStopped += _hover.OnMouseHoverStopped;
        PreviewMouseWheel += (_, _) => _hover.Cancel();
        ContextMenuOpening += (_, _) => { if (ViewModel is { } vm) vm.RefreshQuickFixesAsync(CaretOffset).Forget(vm.ReportFault); };
    }
    public EditorViewModel? ViewModel { get => (EditorViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    /// <summary>The model this control is attached to. It differs from <see cref="ViewModel"/> while a change of tab is under way.</summary>
    internal EditorViewModel? AttachedModel => _attached;
    internal EditorLineSuggestion LineSuggestion => _lineSuggestion;
    internal Task CommitCompletionAsync(Func<CancellationToken, Task<(long Version, TextEdit Edit)>> resolve, string? punctuation = null) => _completion.CommitAsync(resolve, punctuation);
    internal BlameHistoryCard? CreateHistoryCard(string name, int first, int last, BlameHistory history) => _annotations.CreateHistoryCard(name, first, last, history);
    internal LineChangeCard CreateChangeCard(LineChange change) => _annotations.CreateChangeCard(change);
    internal bool IsHistoryPopupOpen => _annotations.IsHistoryOpen;
    internal void CloseAnnotationPopups() => _annotations.ClosePopups();
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var surface = (EditorSurface)owner;
        surface.Attach();
        if (surface.IsLoaded) surface.ObservePreferences(true);
    }
    private void Attach()
    {
        if (ReferenceEquals(_attached, ViewModel)) return;
        Detach(); if (ViewModel == null) return; _attached = ViewModel;
        _syncing = true;
        Document = Buffers.GetValue(ViewModel.State, s => new TextDocument(s.Content));
        SyncText();
        SyntaxHighlighting = SyntaxHighlightingSelector.For(ViewModel.State.Path);
        IsReadOnly = ViewModel.IsReadOnly;
        ViewModel.State.PropertyChanged += StateChanged; ViewModel.PropertyChanged += ModelChanged;
        ViewModel.NavigationRequested += Navigate; ViewModel.Diagnostics.CollectionChanged += DiagnosticsChanged; ViewModel.BreakpointLines.CollectionChanged += BreakpointsChanged;
        ViewModel.SelectionRequested += RestoreSelection;
        ViewModel.BreakpointMarkers.CollectionChanged += BreakpointsChanged;
        ViewModel.SnippetRequested += InsertRequestedSnippet;
        ViewModel.DecorationsChanged += ApplyDecorations;
        ViewModel.FoldingRequested += _folding.SetAllFolded;
        ViewModel.GitChanged += _annotations.OnGitChanged;
        ViewModel.CodeLensChanged += _annotations.ApplyCodeLens;
        ViewModel.ApplyReplacements = ApplyReplacements;
        ViewModel.MemberSnippetInserter = InsertRequestedMember;
        _trackedDocument = Document; Document.Changed += OnDocumentChanged;
        _hints.Enabled = ViewModel.ShowInlayHints;
        ApplyDecorations();
        _folding.Install();
        _annotations.UpdateChangeMargin(); _annotations.ApplyCodeLens();
        _guides.Refresh();
        _syncing = false; Navigate();
    }
    private void Detach()
    {
        _hover.Cancel();
        _annotations.ClosePopups();
        _lineSuggestion.Cancel();
        _completion.CancelPending();
        _completion.CloseWindow(); _completion.CancelRequest();
        if (_attached != null)
        {
            _attached.State.PropertyChanged -= StateChanged; _attached.PropertyChanged -= ModelChanged;
            _attached.NavigationRequested -= Navigate; _attached.Diagnostics.CollectionChanged -= DiagnosticsChanged; _attached.BreakpointLines.CollectionChanged -= BreakpointsChanged;
            _attached.SelectionRequested -= RestoreSelection;
            _attached.BreakpointMarkers.CollectionChanged -= BreakpointsChanged;
            _attached.SnippetRequested -= InsertRequestedSnippet;
            _attached.DecorationsChanged -= ApplyDecorations;
            _attached.FoldingRequested -= _folding.SetAllFolded;
            _attached.GitChanged -= _annotations.OnGitChanged;
            _attached.CodeLensChanged -= _annotations.ApplyCodeLens;
            if (_attached.ApplyReplacements == ApplyReplacements) _attached.ApplyReplacements = null;
            if (_attached.MemberSnippetInserter == InsertRequestedMember) _attached.MemberSnippetInserter = null;
            _folding.Uninstall(_attached.State);
        }
        if (_trackedDocument != null) { _trackedDocument.Changed -= OnDocumentChanged; _trackedDocument = null; }
        _semantic.Clear(); _hints.Clear(); _annotations.ClearCodeLens();
        _attached = null;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DocumentState.Content)) return;
        _hover.Cancel();
        if (!_syncing) { _lineSuggestion.Cancel(); SyncText(); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorViewModel.ExecutionLine)) { _margin.InvalidateVisual(); TextArea.TextView.InvalidateLayer(KnownLayer.Background); }
        if (args.PropertyName == nameof(EditorViewModel.ShowInlayHints) && ViewModel is { } model) { _hints.Enabled = model.ShowInlayHints; TextArea.TextView.Redraw(); }
        if (args.PropertyName is nameof(EditorViewModel.ShowInlineBlame) or nameof(EditorViewModel.ShowCodeLens)) _annotations.ApplyCodeLens();
        if (args.PropertyName == nameof(EditorViewModel.XamlContextRevision))
        {
            _completion.CancelRequest(); _completion.CloseWindow(); _hover.Cancel();
        }
    }
    private void DiagnosticsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        _hover.Cancel();
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }
    private void BreakpointsChanged(object? sender, NotifyCollectionChangedEventArgs args) => _margin.InvalidateVisual();
    private void SyncText()
    {
        if (ViewModel == null || Text == ViewModel.State.Content) return;
        _syncing = true;
        try
        {
            // Preserve the buffer's undo history and anchors for small external/workspace edits.
            var oldText = Text; var newText = ViewModel.State.Content; var start = 0;
            while (start < oldText.Length && start < newText.Length && oldText[start] == newText[start]) start++;
            var end = 0; while (end < oldText.Length - start && end < newText.Length - start && oldText[^(end + 1)] == newText[^(end + 1)]) end++;
            using (Document.RunUpdate()) Document.Replace(start, oldText.Length - start - end, newText.Substring(start, newText.Length - start - end));
        }
        finally { _syncing = false; }
    }
    private void OnEditorTextChanged(object? sender, EventArgs args)
    {
        if (_syncing || ViewModel == null) return;
        _syncing = true; try { ViewModel.State.Content = Text; } finally { _syncing = false; }
        _lineSuggestion.OnTextChanged();
    }
    /// <summary>Runs a change the caller reports to the model itself, so the text and caret events do not report it too.</summary>
    internal void RunSynced(Action change)
    {
        bool syncing = _syncing;
        _syncing = true;
        try { change(); }
        finally { _syncing = syncing; }
    }
    private void Navigate()
    {
        if (ViewModel == null) return;
        CaretOffset = Math.Clamp(ViewModel.State.CaretOffset, 0, Document.TextLength);
        TextArea.Caret.BringCaretToView(); Focus();
    }
    private void RestoreSelection(EditorSelection selection) => RunSynced(() =>
    {
        CaretOffset = selection.CaretOffset;
        int anchor = selection.CaretOffset == selection.Start ? selection.Start + selection.Length : selection.Start;
        TextArea.Selection = Selection.Create(TextArea, anchor, selection.CaretOffset);
        TextArea.Caret.BringCaretToView();
        ViewModel?.ReportCaret(TextArea.Caret.Line, TextArea.Caret.Column, CaretOffset);
    });
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_completion.IsPending && e.Key == Key.Escape) { _completion.CancelPending(); e.Handled = true; return; }
        if (HandleEditingKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && (TryExpandSnippet() || (!_completion.IsWindowOpen && _lineSuggestion.TryAccept()))) e.Handled = true;
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _completion.Show(); }
        else if (e.Key == Key.OemPeriod && Keyboard.Modifiers == ModifierKeys.Control && ViewModel is { IsCSharp: true } csharp)
        {
            e.Handled = true; ShowQuickActionsAsync(csharp).Forget(csharp.ReportFault);
        }
        else if (e.Key == Key.OemPeriod && Keyboard.Modifiers == ModifierKeys.Control && ContextMenu != null)
        {
            if (ViewModel is { } vm) vm.RefreshQuickFixesAsync(CaretOffset).Forget(vm.ReportFault);
            ContextMenu.PlacementTarget = this; ContextMenu.IsOpen = true; e.Handled = true;
        }
        else if (e.Key == Key.Escape) { _completion.CloseWindow(); _hover.Cancel(); _lineSuggestion.Cancel(); }
    }
    private void OnWindowDeactivated(object? sender, EventArgs e) => _hover.Cancel();
    /// <summary>Takes the view model's decorations when they describe the current text; later edits shift them.</summary>
    private void ApplyDecorations()
    {
        if (ViewModel is { } vm && vm.DecorationsVersion == vm.State.Version && Text == vm.State.Content)
        { _semantic.Set(vm.SemanticSpans); _hints.Set(vm.InlayHints); }
        else { _semantic.Clear(); _hints.Clear(); }
        TextArea.TextView.Redraw();
    }
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _annotations.CloseChangePopup(); // Its line numbers describe the text before this edit.
        _semantic.Shift(e.Offset, e.RemovalLength, e.InsertionLength);
        _hints.Shift(e.Offset, e.RemovalLength, e.InsertionLength);
        _annotations.ShiftCodeLens(e.Offset, e.RemovalLength, e.InsertionLength);
        _folding.ScheduleUpdate();
    }
    /// <summary>
    /// A frozen pen of the brush's current colour, kept in <paramref name="pen"/> until that colour changes. The theme recolours
    /// its brushes in place, so the pen holds a frozen copy (freezing the pen would freeze the theme's brush) and compares colours.
    /// </summary>
    internal static Pen ThemedPen(Brush brush, double thickness, ref Pen? pen)
    {
        if (pen?.Brush is SolidColorBrush held && brush is SolidColorBrush current && held.Color == current.Color) return pen;
        pen = new Pen((Brush)brush.GetCurrentValueAsFrozen(), thickness);
        pen.Freeze();
        return pen;
    }
}
