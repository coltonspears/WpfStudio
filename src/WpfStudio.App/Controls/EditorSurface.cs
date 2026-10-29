using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.AvalonEdit.Snippets;
using WpfStudio.App.ViewModels;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Text;

[assembly: InternalsVisibleTo("WpfStudio.Shell.Tests")]

namespace WpfStudio.App.Controls;

/// <summary>Presentation adapter for AvalonEdit: caret, highlighting, adorners and completion popups.</summary>
public sealed partial class EditorSurface : TextEditor
{
    private static readonly ConditionalWeakTable<DocumentState, TextDocument> Buffers = new();
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel), typeof(EditorViewModel), typeof(EditorSurface), new PropertyMetadata(null, Changed));
    private EditorViewModel? _attached;
    private bool _syncing;
    private CompletionWindow? _completion;
    private CancellationTokenSource? _completionRequest;
    private CancellationTokenSource? _hoverRequest;
    private PendingCompletion? _pendingCompletion;
    private bool _replayingInput;
    private readonly ToolTip _signature = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, StaysOpen = false };
    // Hover information is a popup the editor owns, below the pointer. A tooltip at the pointer covered the
    // text, ended the hover and reopened on the next one, so it flashed; ToolTipService could also close it.
    private readonly System.Windows.Controls.Primitives.Popup _hover = new()
    {
        Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, StaysOpen = true, AllowsTransparency = true,
        PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None
    };
    private readonly Border _hoverContent = new();
    private readonly DebugMargin _margin;
    private readonly DiagnosticRenderer _diagnostics;
    private readonly SemanticColorizer _semantic = new();
    private readonly InlayHintGenerator _hints;
    private readonly System.Windows.Threading.DispatcherTimer _foldingTimer;
    private readonly XmlFoldingStrategy _xmlFolding = new() { ShowAttributesWhenFolded = true };
    private FoldingManager? _folding;
    private TextDocument? _trackedDocument;
    /// <summary>Folded regions survive switching tabs, which detaches the editor from its buffer.</summary>
    private static readonly ConditionalWeakTable<DocumentState, List<(int Start, int End)>> FoldedRegions = new();
    /// <summary>Files larger than this keep syntax colouring but no folding.</summary>
    private const int MaximumFoldingCharacters = 2_000_000;
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
        InitializeAnnotations();
        InitializePreferenceChrome();
        _foldingTimer = new(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Background, (_, _) => UpdateFoldings(), Dispatcher) { IsEnabled = false };
        TextChanged += OnEditorTextChanged;
        TextArea.Caret.PositionChanged += (_, _) => { if (ViewModel is { } vm && !_syncing) { vm.State.CaretOffset = CaretOffset; vm.State.CaretLine = TextArea.Caret.Line; vm.State.CaretColumn = TextArea.Caret.Column; } };
        TextArea.TextEntered += OnTextEntered;
        TextArea.SelectionChanged += (_, _) => ViewModel?.UpdateSelection(SelectionStart, SelectionLength);
        PreviewMouseRightButtonDown += (_, e) =>
        {
            if (SelectionLength == 0 && GetPositionFromPoint(e.GetPosition(this)) is { } position) CaretOffset = Document.GetOffset(position.Location);
        };
        TextArea.TextEntering += OnTextEntering;
        CommandManager.AddPreviewExecutedHandler(TextArea, OnPreviewExecuted);
        PreviewMouseDown += (_, _) => { CancelPendingCompletion(); CancelHover(); };
        LostKeyboardFocus += (_, _) => CancelPendingCompletion();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            ObservePreferences(true);
            Attach();
            // An owned popup stays above other applications unless it is closed with its window.
            if (Window.GetWindow(this) is { } window) { window.Deactivated -= OnWindowDeactivated; window.Deactivated += OnWindowDeactivated; }
        };
        Unloaded += (_, _) => { Detach(); ObservePreferences(false); if (Window.GetWindow(this) is { } window) window.Deactivated -= OnWindowDeactivated; };
        var hoverChrome = new Border
        {
            Child = _hoverContent, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 2, 8, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.28, Direction = 270 }
        };
        hoverChrome.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        hoverChrome.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        _hover.Child = hoverChrome;
        _hover.MouseLeave += (_, _) => { if (!IsMouseOver) CancelHover(); };
        MouseHover += OnMouseHover;
        // Moving onto the popup (to read or select from it) keeps it open.
        MouseHoverStopped += (_, _) => { if (!_hover.IsMouseOver) CancelHover(); };
        PreviewMouseWheel += (_, _) => CancelHover();
        ContextMenuOpening += (_, _) => { if (ViewModel is { } vm) _ = vm.RefreshQuickFixesAsync(CaretOffset); };
    }
    public EditorViewModel? ViewModel { get => (EditorViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((EditorSurface)owner).Attach();
    private void Attach()
    {
        if (ReferenceEquals(_attached, ViewModel)) return;
        Detach(); if (ViewModel == null) return; _attached = ViewModel;
        _syncing = true;
        Document = Buffers.GetValue(ViewModel.State, s => new TextDocument(s.Content));
        SyncText();
        SyntaxHighlighting = SyntaxHighlightingSelector.For(ViewModel.State.Path);
        IsReadOnly = ViewModel.State.Path.Contains(System.IO.Path.Combine("WpfStudio", "GeneratedSources"), StringComparison.OrdinalIgnoreCase);
        ViewModel.State.PropertyChanged += StateChanged; ViewModel.PropertyChanged += ModelChanged;
        ViewModel.NavigationRequested += Navigate; ViewModel.Diagnostics.CollectionChanged += DiagnosticsChanged; ViewModel.BreakpointLines.CollectionChanged += BreakpointsChanged;
        ViewModel.SelectionRequested += RestoreSelection;
        ViewModel.BreakpointMarkers.CollectionChanged += BreakpointsChanged;
        ViewModel.SnippetRequested += InsertRequestedSnippet;
        ViewModel.DecorationsChanged += ApplyDecorations;
        ViewModel.FoldingRequested += SetAllFolded;
        ViewModel.GitChanged += OnGitChanged;
        ViewModel.CodeLensChanged += ApplyCodeLens;
        ViewModel.ApplyReplacements = ApplyReplacements;
        _trackedDocument = Document; Document.Changed += OnDocumentChanged;
        _hints.Enabled = ViewModel.ShowInlayHints;
        ApplyDecorations();
        InstallFolding();
        _changeMargin?.Update(); ApplyCodeLens();
        _syncing = false; Navigate();
    }
    private void Detach()
    {
        CancelHover();
        _changePopup.IsOpen = false; _historyPopup.IsOpen = false;
        CancelPendingCompletion();
        _completion?.Close(); _completionRequest?.Cancel();
        if (_attached != null)
        {
            _attached.State.PropertyChanged -= StateChanged; _attached.PropertyChanged -= ModelChanged;
            _attached.NavigationRequested -= Navigate; _attached.Diagnostics.CollectionChanged -= DiagnosticsChanged; _attached.BreakpointLines.CollectionChanged -= BreakpointsChanged;
            _attached.SelectionRequested -= RestoreSelection;
            _attached.BreakpointMarkers.CollectionChanged -= BreakpointsChanged;
            _attached.SnippetRequested -= InsertRequestedSnippet;
            _attached.DecorationsChanged -= ApplyDecorations;
            _attached.FoldingRequested -= SetAllFolded;
            _attached.GitChanged -= OnGitChanged;
            _attached.CodeLensChanged -= ApplyCodeLens;
            if (_attached.ApplyReplacements == ApplyReplacements) _attached.ApplyReplacements = null;
            UninstallFolding(_attached.State);
        }
        if (_trackedDocument != null) { _trackedDocument.Changed -= OnDocumentChanged; _trackedDocument = null; }
        _semantic.Clear(); _hints.Clear(); _lenses = [];
        _attached = null;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DocumentState.Content)) return;
        CancelHover();
        if (!_syncing) SyncText();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorViewModel.ExecutionLine)) { _margin.InvalidateVisual(); TextArea.TextView.InvalidateLayer(KnownLayer.Background); }
        if (args.PropertyName == nameof(EditorViewModel.ShowInlayHints) && ViewModel is { } model) { _hints.Enabled = model.ShowInlayHints; TextArea.TextView.Redraw(); }
        if (args.PropertyName is nameof(EditorViewModel.ShowInlineBlame) or nameof(EditorViewModel.ShowCodeLens)) ApplyCodeLens();
        if (args.PropertyName == nameof(EditorViewModel.XamlContextRevision))
        {
            _completionRequest?.Cancel(); _completion?.Close(); CancelHover();
        }
    }
    private void DiagnosticsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        CancelHover();
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
    }
    private void Navigate()
    {
        if (ViewModel == null) return;
        CaretOffset = Math.Clamp(ViewModel.State.CaretOffset, 0, Document.TextLength);
        TextArea.Caret.BringCaretToView(); Focus();
    }
    private void RestoreSelection(EditorSelection selection)
    {
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            CaretOffset = selection.CaretOffset;
            int anchor = selection.CaretOffset == selection.Start ? selection.Start + selection.Length : selection.Start;
            TextArea.Selection = Selection.Create(TextArea, anchor, selection.CaretOffset);
            TextArea.Caret.BringCaretToView();
            if (ViewModel is { } model)
            {
                model.State.CaretLine = TextArea.Caret.Line;
                model.State.CaretColumn = TextArea.Caret.Column;
            }
        }
        finally { _syncing = syncing; }
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pendingCompletion != null && e.Key == Key.Escape) { CancelPendingCompletion(); e.Handled = true; return; }
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && TryExpandSnippet()) e.Handled = true;
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = ShowCompletionAsync(); }
        else if (e.Key == Key.OemPeriod && Keyboard.Modifiers == ModifierKeys.Control && ViewModel is { IsCSharp: true } csharp)
        {
            e.Handled = true; _ = ShowQuickActionsAsync(csharp);
        }
        else if (e.Key == Key.OemPeriod && Keyboard.Modifiers == ModifierKeys.Control && ContextMenu != null)
        {
            if (ViewModel is { } vm) _ = vm.RefreshQuickFixesAsync(CaretOffset);
            ContextMenu.PlacementTarget = this; ContextMenu.IsOpen = true; e.Handled = true;
        }
        else if (e.Key == Key.Escape) { _completion?.Close(); CancelHover(); }
    }
    private async void OnMouseHover(object? sender, MouseEventArgs e)
    {
        CancelHover();
        if (IsOverAdornment(e.OriginalSource) || IsOverAdornment(Mouse.DirectlyOver)) return; // Folded blocks and lens rows have their own tips.
        if (ViewModel is not { } vm || GetPositionFromPoint(e.GetPosition(this)) is not { } position) return;
        int offset = Document.GetOffset(position.Location);
        long version = vm.State.Version;
        _hoverRequest = new CancellationTokenSource();
        var token = _hoverRequest.Token;
        var parts = new HoverParts(null, null, null,
            vm.Diagnostics.Where(d => offset >= d.Start && offset < d.Start + Math.Max(1, d.Length)).OrderBy(d => d.Severity switch { "Error" => 0, "Warning" => 1, _ => 2 }).Take(4).ToArray());
        if (!vm.ShowQuickInfo && parts.Diagnostics.Count == 0 && vm.DataTipEvaluator is null) return;
        ShowHover(parts);
        try
        {
            if (await vm.DataTipAsync(offset, token) is { } dataTip)
            {
                if (token.IsCancellationRequested || vm != ViewModel || version != vm.State.Version) return;
                ShowHover(parts = parts with { DataTip = dataTip });
            }
            if (!vm.ShowQuickInfo) return;
            if (vm.IsCSharp)
            {
                var info = await vm.QuickInfoAsync(offset, token);
                if (token.IsCancellationRequested || vm != ViewModel || version != vm.State.Version) return;
                if (info is not null) ShowHover(parts = parts with { QuickInfo = info });
            }
            else if (await vm.HoverAsync(offset, token) is { } hover)
            {
                if (token.IsCancellationRequested || vm != ViewModel || version != vm.State.Version) return;
                ShowHover(parts = parts with { Xaml = hover.Text });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && vm == ViewModel) vm.LanguageStatus = ex.Message; }
    }
    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelHover();
    private void CancelHover()
    {
        _hoverRequest?.Cancel(); _hoverRequest?.Dispose(); _hoverRequest = null;
        _hover.IsOpen = false;
        _signature.IsOpen = false;
    }
    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (_replayingInput || _pendingCompletion != null) return;
        if (e.Text is "(" or ",") _ = ShowSignatureAsync();
        else if (e.Text == ")") _signature.IsOpen = false;
        if (_completion == null && e.Text.Length == 1 && (e.Text[0] is '.' or '<' or '{' || char.IsLetter(e.Text[0]))) _ = ShowCompletionAsync();
    }
    private async Task ShowCompletionAsync()
    {
        if (ViewModel == null || IsReadOnly || _pendingCompletion != null) return;
        _completionRequest?.Cancel(); _completionRequest?.Dispose(); _completionRequest = new();
        var token = _completionRequest.Token; var vm = ViewModel; var version = vm.State.Version; var caret = CaretOffset;
        long contextRevision = vm.XamlContextRevision;
        try
        {
            var result = await vm.CompleteAsync(caret, token);
            if (token.IsCancellationRequested || vm != ViewModel || version != vm.State.Version || caret != CaretOffset || result.Items.Count == 0 || !IsKeyboardFocusWithin
                || vm.IsXaml && contextRevision != vm.XamlContextRevision) return;
            _completion?.Close();
            var end = vm.State.Extension == ".xaml" ? Math.Clamp(result.Start + result.Length, caret, Document.TextLength) : caret;
            var window = new CompletionWindow(TextArea) { StartOffset = result.Start, EndOffset = end, CloseWhenCaretAtBeginning = true };
            foreach (var item in result.Items.Take(250)) window.CompletionList.CompletionData.Add(new StudioCompletion(this, item, result.Version, contextRevision));
            // C# snippets join the list outside member access, as in Visual Studio; Tab or Enter expands them.
            if (vm.IsCSharp && (result.Start == 0 || Document.GetCharAt(result.Start - 1) != '.'))
                foreach (var snippet in SnippetCatalog.For(".cs")) window.CompletionList.CompletionData.Add(new SnippetCompletion(this, snippet));
            window.Closed += (_, _) => { if (_completion == window) _completion = null; };
            _completion = window; window.Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (vm == ViewModel) vm.LanguageStatus = ex.Message; }
    }
    private async Task ShowSignatureAsync()
    {
        if (ViewModel is not { } vm) return;
        long version = vm.State.Version;
        int caret = CaretOffset;
        try
        {
            var text = await vm.SignatureAsync(caret);
            if (vm != ViewModel || vm.State.Version != version || caret != CaretOffset || !IsKeyboardFocusWithin) return;
            if (!string.IsNullOrEmpty(text)) { _signature.Content = text; _signature.PlacementTarget = this; _signature.IsOpen = true; }
        }
        catch (Exception ex) { if (vm == ViewModel) vm.LanguageStatus = ex.Message; }
    }
    private void OnTextEntering(object? sender, TextCompositionEventArgs args)
    {
        if (!_replayingInput && _pendingCompletion is { } pending)
        {
            pending.Input.Add(new BufferedInput(args.Text, null, null)); args.Handled = true; return;
        }
        if (!_replayingInput && _completion != null && args.Text.Length > 0 && !char.IsLetterOrDigit(args.Text[0]) && args.Text[0] != '_')
            _completion.CompletionList.RequestInsertion(args);
        if (!args.Handled && TryXamlTyping(args.Text)) args.Handled = true;
    }
    // AvalonEdit must consume this in TextEntering: a single update contains the
    // typed character and generated suffix, including input replayed after completion.
    private bool TryXamlTyping(string input)
    {
        if ((_replayingInput ? _attached : ViewModel) is not { IsXaml: true } || IsReadOnly || TextArea.OverstrikeMode || !TextArea.Selection.IsEmpty ||
            Document.TextLength > XamlTypingService.MaximumCharacters || !TextArea.ReadOnlySectionProvider.CanInsert(CaretOffset)) return false;
        var plan = XamlTypingService.GetEdit(Text, CaretOffset, SelectionLength, input, Options.IndentationString,
            TextUtilities.GetNewLineFromDocument(Document, TextArea.Caret.Line));
        if (plan is null) return false;
        if (plan.Edit.Length > 0)
        {
            var editable = TextArea.ReadOnlySectionProvider.GetDeletableSegments(new TextSegment { StartOffset = plan.Edit.Start, Length = plan.Edit.Length }).ToArray();
            if (editable.Length != 1 || editable[0].Offset != plan.Edit.Start || editable[0].Length != plan.Edit.Length) return false;
        }
        using (Document.RunUpdate())
        {
            if (plan.Edit.Length != 0 || plan.Edit.NewText.Length != 0)
            {
                Document.Replace(plan.Edit.Start, plan.Edit.Length, plan.Edit.NewText);
                // AvalonEdit records the pre-edit caret for undo. Redo replays
                // that position before the insertion, so it also needs the
                // intended position inside the generated pair after insertion.
                Document.UndoStack.PushOptional(new RestoreTypingCaretOnRedo(TextArea, Document, plan.CaretOffset));
            }
            CaretOffset = plan.CaretOffset;
            TextArea.ClearSelection();
        }
        TextArea.Caret.BringCaretToView();
        return true;
    }
    private sealed class RestoreTypingCaretOnRedo(TextArea textArea, TextDocument document, int caretOffset) : IUndoableOperation
    {
        private readonly WeakReference<TextArea> _textArea = new(textArea);
        private readonly WeakReference<TextDocument> _document = new(document);
        public void Undo() { }
        public void Redo()
        {
            if (!_textArea.TryGetTarget(out var area) || !_document.TryGetTarget(out var buffer) || area.Document != buffer) return;
            area.Caret.Offset = Math.Clamp(caretOffset, 0, buffer.TextLength);
            area.ClearSelection();
        }
    }
    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs args)
    {
        if (_pendingCompletion is not { } pending || _replayingInput || args.Command is not RoutedCommand command || command == ApplicationCommands.Copy) return;
        pending.Input.Add(new BufferedInput(null, command, args.Parameter)); args.Handled = true;
    }

    private Task AcceptCompletionAsync(CompletionEntry entry, long version, long contextRevision, ISegment segment, EventArgs insertionRequest)
    {
        if (ViewModel is not { } vm) return Task.CompletedTask;
        string? punctuation = insertionRequest is TextCompositionEventArgs textInput ? textInput.Text : null;
        if (insertionRequest is TextCompositionEventArgs textArgs) textArgs.Handled = true;
        int start = segment.Offset, length = segment.Length;
        return CommitCompletionAsync(async token =>
        {
            if (vm.IsXaml) return (vm.State.Version, vm.XamlCompletionEdit(entry, start, length, contextRevision));
            if (vm.State.Extension != ".cs") return (vm.State.Version, new TextEdit(start, length, entry.InsertText));
            if (version != vm.State.Version)
            {
                var current = await vm.CompleteAsync(CaretOffset, token);
                entry = current.Items.FirstOrDefault(item => item.DisplayText == entry.DisplayText)
                    ?? throw new InvalidOperationException("Completion changed. Typed input was kept; request completion again.");
                version = current.Version;
            }
            var edit = await vm.CompletionEditAsync(entry, version, token)
                ?? throw new InvalidOperationException("Completion is no longer available. Typed input was kept.");
            return (version, edit);
        }, punctuation);
    }

    // Keep text and editing commands in order while Roslyn computes the actual change.
    // In particular, AvalonEdit's synchronous completion callback must not let the
    // triggering '(' reach the document before the asynchronous replacement returns.
    internal async Task CommitCompletionAsync(Func<CancellationToken, Task<(long Version, TextEdit Edit)>> resolve, string? punctuation = null)
    {
        if (ViewModel is not { } vm || IsReadOnly) return;
        CancelPendingCompletion();
        var pending = new PendingCompletion(vm, Document);
        if (!string.IsNullOrEmpty(punctuation)) pending.Input.Add(new BufferedInput(punctuation, null, null));
        _pendingCompletion = pending;
        try
        {
            var result = await resolve(pending.Cancellation.Token);
            if (!ReferenceEquals(_pendingCompletion, pending) || pending.Cancellation.IsCancellationRequested) return;
            if (ViewModel != vm || Document != pending.Document || vm.State.Version != result.Version)
            { vm.LanguageStatus = "The document changed before completion finished. Typed input was kept."; return; }
            if (result.Edit.Start < 0 || result.Edit.Length < 0 || result.Edit.Start + result.Edit.Length > Document.TextLength)
                throw new InvalidOperationException("The completion edit is outside the current document.");
            Document.Replace(result.Edit.Start, result.Edit.Length, result.Edit.NewText);
            CaretOffset = result.Edit.Start + result.Edit.NewText.Length;
            vm.LanguageStatus = "";
        }
        catch (OperationCanceledException) { vm.LanguageStatus = "Completion cancelled. Typed input was kept."; }
        catch (Exception exception) { vm.LanguageStatus = exception.Message; }
        finally
        {
            if (ReferenceEquals(_pendingCompletion, pending)) ReplayInput(pending);
            pending.Cancellation.Dispose();
        }
    }
    private void CancelPendingCompletion()
    {
        if (_pendingCompletion is not { } pending) return;
        pending.Cancellation.Cancel(); ReplayInput(pending);
    }
    private void ReplayInput(PendingCompletion pending)
    {
        _pendingCompletion = null;
        bool previousSync = _syncing;
        _syncing = true; _replayingInput = true;
        try
        {
            // Detach invokes this before exchanging buffers. Always update the
            // captured model, even when the dependency property already points to a new tab.
            if (Document != pending.Document) return;
            foreach (BufferedInput input in pending.Input)
            {
                if (input.Text is not null) TextArea.PerformTextInput(input.Text);
                else if (input.Command?.CanExecute(input.Parameter, TextArea) == true) input.Command.Execute(input.Parameter, TextArea);
            }
            pending.ViewModel.State.Content = pending.Document.Text;
            pending.ViewModel.State.CaretOffset = CaretOffset;
            pending.ViewModel.State.CaretLine = TextArea.Caret.Line;
            pending.ViewModel.State.CaretColumn = TextArea.Caret.Column;
        }
        finally { _replayingInput = false; _syncing = previousSync; }
    }
    private sealed record BufferedInput(string? Text, RoutedCommand? Command, object? Parameter);
    private sealed class PendingCompletion(EditorViewModel viewModel, TextDocument document)
    {
        public EditorViewModel ViewModel { get; } = viewModel;
        public TextDocument Document { get; } = document;
        public CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromSeconds(5));
        public List<BufferedInput> Input { get; } = [];
    }
    private sealed class StudioCompletion(EditorSurface owner, CompletionEntry entry, long version, long contextRevision) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => entry.DisplayText;
        public object Content => entry.DisplayText;
        public object Description => entry.Description ?? string.Join(", ", entry.Tags);
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => _ = owner.AcceptCompletionAsync(entry, version, contextRevision, completionSegment, insertionRequestEventArgs);
    }
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
        _changePopup.IsOpen = false; // Its line numbers describe the text before this edit.
        _semantic.Shift(e.Offset, e.RemovalLength, e.InsertionLength);
        _hints.Shift(e.Offset, e.RemovalLength, e.InsertionLength);
        ShiftCodeLens(e.Offset, e.RemovalLength, e.InsertionLength);
        if (_folding != null) { _foldingTimer.Stop(); _foldingTimer.Start(); }
    }
    private void InstallFolding()
    {
        if (!Services.EditorPreferences.Current.ShowFolding || _folding is not null) return;
        if (ViewModel is not { } vm || vm.State.Extension is not (".cs" or ".xaml" or ".xml" or ".csproj" or ".props" or ".targets" or ".resx")) return;
        _folding = FoldingManager.Install(TextArea);
        InstallFoldingVisuals(_folding);
        UpdateFoldings();
        if (FoldedRegions.TryGetValue(vm.State, out var folded))
            foreach (var section in _folding.AllFoldings.Where(section => folded.Contains((section.StartOffset, section.EndOffset))))
                section.IsFolded = true;
    }
    private void UninstallFolding(DocumentState state)
    {
        _foldingTimer.Stop();
        if (_folding is null) return;
        FoldedRegions.AddOrUpdate(state, _folding.AllFoldings.Where(section => section.IsFolded).Select(section => (section.StartOffset, section.EndOffset)).ToList());
        UninstallFoldingVisuals();
        FoldingManager.Uninstall(_folding);
        _folding = null;
    }
    private void UpdateFoldings()
    {
        _foldingTimer.Stop();
        if (_folding is null || ViewModel is not { } vm) return;
        if (Document.TextLength > MaximumFoldingCharacters) { _folding.Clear(); return; }
        if (vm.State.Extension == ".cs")
            _folding.UpdateFoldings(CSharpFolding.Find(Text).Select(region => new NewFolding(region.Start, region.End) { Name = region.Title }), -1);
        else _xmlFolding.UpdateFoldings(_folding, Document); // Keeps the previous foldings while XML is malformed.
    }
    private void SetAllFolded(bool folded)
    {
        if (_folding is null) return;
        UpdateFoldings();
        foreach (var section in _folding.AllFoldings) section.IsFolded = folded;
        if (folded) TextArea.Caret.BringCaretToView();
    }
    private ContextMenu? _quickActions;
    /// <summary>Ctrl+. in C#: a menu at the caret listing fixes first, then refactorings. Each opens the change review.</summary>
    private async Task ShowQuickActionsAsync(EditorViewModel vm)
    {
        if (_quickActions is { } previous) previous.IsOpen = false;
        var view = TextArea.TextView;
        var caret = TextArea.Caret.CalculateCaretRectangle();
        var menu = new ContextMenu
        {
            PlacementTarget = view, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = caret.Left - view.HorizontalOffset, VerticalOffset = caret.Bottom - view.VerticalOffset
        };
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Finding quick actions…" }, IsEnabled = false });
        _quickActions = menu;
        menu.IsOpen = true;
        await vm.RefreshQuickFixesAsync(CaretOffset);
        if (!ReferenceEquals(_quickActions, menu) || !menu.IsOpen || vm != ViewModel) return;
        menu.Items.Clear();
        if (vm.CodeActions.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = vm.LanguageStatus is { Length: > 0 } status ? status : "No quick actions here" }, IsEnabled = false });
            return;
        }
        bool? fixes = null;
        foreach (var item in vm.CodeActions)
        {
            if (fixes is not null && fixes != item.IsFix) menu.Items.Add(new Separator());
            fixes = item.IsFix;
            // A TextBlock header keeps underscores in titles from becoming access keys.
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = item.Title }, Command = item.ApplyCommand, CommandParameter = item.Action, ToolTip = item.IsFix ? "Code fix" : "Refactoring" });
        }
        if (menu.Items[0] is MenuItem first) first.Focus();
    }
    /// <summary>Expands the snippet whose shortcut is the word before the caret. Returns false to let Tab indent.</summary>
    internal bool TryExpandSnippet()
    {
        if (ViewModel is not { } vm || IsReadOnly || _completion != null || _pendingCompletion != null || !TextArea.Selection.IsEmpty
            || !TextArea.StackedInputHandlers.IsEmpty) return false; // An active snippet's fields own Tab.
        if (SnippetCatalog.WordBefore(Text, CaretOffset, out var start) is not { } word || SnippetCatalog.Find(word, vm.State.Extension) is not { } snippet) return false;
        InsertSnippet(snippet, start, CaretOffset - start);
        return true;
    }
    private void InsertRequestedSnippet(CodeSnippet snippet)
    {
        if (IsReadOnly) return;
        InsertSnippet(snippet, CaretOffset, 0);
        Focus();
    }
    /// <summary>Replaces <paramref name="length"/> characters at <paramref name="start"/> with an interactive snippet: Tab moves between fields, Enter or Esc finishes.</summary>
    internal void InsertSnippet(CodeSnippet snippet, int start, int length)
    {
        var parts = SnippetCatalog.Resolve(SnippetCatalog.Parse(snippet.Body), Text, start);
        var expansion = new Snippet();
        var fields = new Dictionary<string, SnippetReplaceableTextElement>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            switch (part)
            {
                case SnippetLiteral literal: expansion.Elements.Add(new SnippetTextElement { Text = literal.Text }); break;
                case SnippetField field: expansion.Elements.Add(fields[field.Name] = new SnippetReplaceableTextElement { Text = field.Default }); break;
                case SnippetMirror mirror: expansion.Elements.Add(new SnippetBoundElement { TargetElement = fields[mirror.Name] }); break;
                case SnippetCaret: expansion.Elements.Add(new SnippetCaretElement()); break;
                case SnippetSelection: expansion.Elements.Add(new SnippetSelectionElement()); break;
            }
        }
        // One undo step removes both the shortcut and the expansion.
        using (Document.RunUpdate())
        {
            if (length > 0) { Document.Remove(start, length); CaretOffset = start; }
            expansion.Insert(TextArea);
        }
        TextArea.Caret.BringCaretToView();
    }
    private sealed class SnippetCompletion(EditorSurface owner, CodeSnippet snippet) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => snippet.Shortcut;
        public object Content => snippet.Shortcut;
        public object Description => "Snippet: " + snippet.Title + " (Tab)";
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (insertionRequestEventArgs is TextCompositionEventArgs typed) typed.Handled = true;
            owner.InsertSnippet(snippet, completionSegment.Offset, completionSegment.Length);
        }
    }
    /// <summary>Semantic colours from Roslyn layered over syntax highlighting: types, members, parameters.</summary>
    private sealed class SemanticColorizer : DocumentColorizingTransformer
    {
        private List<SemanticSpan> _spans = [];
        public void Set(IReadOnlyList<SemanticSpan> spans) => _spans = [.. spans.OrderBy(span => span.Start)];
        public void Clear() => _spans = [];
        public void Shift(int offset, int removed, int inserted)
        {
            if (_spans.Count == 0) return;
            var shifted = new List<SemanticSpan>(_spans.Count);
            foreach (var span in _spans)
                if (DecorationOffsets.MapRange(span.Start, span.Length, offset, removed, inserted) is { } mapped) shifted.Add(span with { Start = mapped.Start });
            _spans = shifted;
        }
        protected override void ColorizeLine(DocumentLine line)
        {
            if (_spans.Count == 0) return;
            int lineStart = line.Offset, lineEnd = line.EndOffset;
            // Spans do not overlap, so the first candidate is at most one before the first span starting on the line.
            int low = 0, high = _spans.Count;
            while (low < high) { var middle = (low + high) / 2; if (_spans[middle].Start < lineStart) low = middle + 1; else high = middle; }
            for (var index = Math.Max(0, low - 1); index < _spans.Count && _spans[index].Start < lineEnd; index++)
            {
                var span = _spans[index];
                int start = Math.Max(span.Start, lineStart), end = Math.Min(span.Start + span.Length, lineEnd);
                if (end <= start || BrushFor(span.Kind) is not { } brush) continue;
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
        private static Brush? BrushFor(string kind) => kind switch
        {
            "Type" => Resource("SyntaxType"), "Interface" => Resource("SyntaxInterface"), "Enum" => Resource("SyntaxEnum"),
            "TypeParameter" => Resource("SyntaxTypeParameter"), "Method" => Resource("SyntaxMethod"),
            "Property" or "Field" or "Event" or "Constant" or "EnumMember" => Resource("SyntaxMember"),
            "Parameter" => Resource("SyntaxParameter"),
            _ => null // Locals and namespaces keep the text colour.
        };
        private static Brush? Resource(string key) => Application.Current?.TryFindResource(key) as Brush;
    }
    /// <summary>
    /// Renders inline hints as zero-length elements. The caret never stops on a hint, and each offset emits at most
    /// one hint per visual line, so generation always advances.
    /// </summary>
    private sealed class InlayHintGenerator(EditorSurface owner) : VisualLineElementGenerator
    {
        private List<InlayHint> _hints = [];
        private int _emittedAt = -1;
        public bool Enabled { get; set; } = true;
        public void Set(IReadOnlyList<InlayHint> hints) => _hints = [.. hints.OrderBy(hint => hint.Position).DistinctBy(hint => hint.Position)];
        public void Clear() => _hints = [];
        public void Shift(int offset, int removed, int inserted)
        {
            if (_hints.Count == 0) return;
            var shifted = new List<InlayHint>(_hints.Count);
            foreach (var hint in _hints)
                if (DecorationOffsets.MapPoint(hint.Position, offset, removed, inserted) is { } position) shifted.Add(hint with { Position = position });
            _hints = shifted;
        }
        public override void StartGeneration(ITextRunConstructionContext context) { base.StartGeneration(context); _emittedAt = -1; }
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!Enabled || _hints.Count == 0) return -1;
            var index = IndexAtOrAfter(startOffset);
            if (index < _hints.Count && _hints[index].Position == _emittedAt) index++;
            if (index >= _hints.Count) return -1;
            var position = _hints[index].Position;
            return position <= CurrentContext.VisualLine.LastDocumentLine.EndOffset ? position : -1;
        }
        public override VisualLineElement? ConstructElement(int offset)
        {
            var index = IndexAtOrAfter(offset);
            if (index >= _hints.Count || _hints[index].Position != offset || offset == _emittedAt) return null;
            _emittedAt = offset;
            var hint = _hints[index];
            var size = Math.Max(8, owner.FontSize - 1.5);
            var padding = new Thickness(3, 0, 3, 0);
            var text = new TextBlock { Text = hint.Text, FontFamily = owner.FontFamily, FontSize = size };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InlayHintBrush");
            var chip = new Border
            {
                Child = text, Padding = padding, CornerRadius = new CornerRadius(3), Cursor = Cursors.Arrow,
                Margin = hint.Kind == "Parameter" ? new Thickness(0, 0, 3, 0) : new Thickness(1, 0, 0, 0)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "InlayHintBackgroundBrush");
            // Align the hint's text baseline with the line's baseline instead of its bottom edge.
            TextBlock.SetBaselineOffset(chip, padding.Top + size * owner.FontFamily.Baseline);
            return new HintElement(chip);
        }
        private int IndexAtOrAfter(int offset)
        {
            int low = 0, high = _hints.Count;
            while (low < high) { var middle = (low + high) / 2; if (_hints[middle].Position < offset) low = middle + 1; else high = middle; }
            return low;
        }
    }
    private sealed class HintElement(UIElement element) : InlineObjectElement(0, element)
    {
        public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode) => -1;
    }
    private sealed class DebugMargin(EditorSurface owner) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(22, 0);
        protected override void OnRender(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } view || owner.ViewModel == null) return;
            context.DrawRectangle(owner.TryFindResource("EditorBrush") as Brush ?? Brushes.Transparent, null, new Rect(RenderSize));
            var danger = owner.TryFindResource("DangerBrush") as Brush ?? Brushes.IndianRed;
            var muted = owner.TryFindResource("SubtleBrush") as Brush ?? Brushes.SlateGray;
            var execution = owner.TryFindResource("WarningBrush") as Brush ?? Brushes.Goldenrod;
            foreach (var visual in view.VisualLines)
            {
                var line = visual.FirstDocumentLine.LineNumber; var y = visual.VisualTop - view.VerticalOffset + visual.Height / 2;
                var marker = owner.ViewModel.BreakpointMarkers.FirstOrDefault(b => b.Line == line);
                if (marker != null)
                {
                    var brush = marker.Enabled ? danger : muted;
                    var fill = marker.Enabled && marker.Bound ? brush : null;
                    if (marker.IsTracepoint)
                    {
                        // Tracepoints use a diamond, as in Visual Studio.
                        var diamond = new StreamGeometry();
                        using (var g = diamond.Open()) { g.BeginFigure(new Point(10, y - 6), true, true); g.LineTo(new Point(16, y), true, false); g.LineTo(new Point(10, y + 6), true, false); g.LineTo(new Point(4, y), true, false); }
                        diamond.Freeze();
                        context.DrawGeometry(fill, new Pen(brush, 1.8), diamond);
                    }
                    else context.DrawEllipse(fill, new Pen(brush, 1.8), new Point(10, y), 5.5, 5.5);
                    if (marker.Condition.Length > 0 || marker.HitCondition.Length > 0) context.DrawEllipse(fill is null ? brush : owner.TryFindResource("EditorBrush") as Brush ?? Brushes.White, null, new Point(10, y), 1.7, 1.7);
                }
                else if (owner.ViewModel.BreakpointLines.Contains(line)) context.DrawEllipse(danger, null, new Point(10, y), 5.5, 5.5);
                if (owner.ViewModel.ExecutionLine == line)
                {
                    // Current statement: a solid arrow drawn over the breakpoint column.
                    var arrow = new StreamGeometry();
                    using (var g = arrow.Open()) { g.BeginFigure(new Point(3, y - 4.5), true, true); g.LineTo(new Point(10, y - 4.5), true, false); g.LineTo(new Point(15, y), true, false); g.LineTo(new Point(10, y + 4.5), true, false); g.LineTo(new Point(3, y + 4.5), true, false); }
                    arrow.Freeze();
                    context.DrawGeometry(execution, null, arrow);
                }
            }
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (TextView == null || owner.ViewModel == null) return;
            // The gutter's X coordinate is outside the text view. Resolve by Y so
            // clicking beside a line also works when the editor is scrolled horizontally.
            var line = TextView.GetVisualLineFromVisualTop(e.GetPosition(TextView).Y + TextView.VerticalOffset)?.FirstDocumentLine.LineNumber;
            if (line != null && owner.ViewModel.State.Extension == ".cs") owner.ViewModel.ToggleBreakpoint(line.Value);
            e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var line = TextView?.GetVisualLineFromVisualTop(e.GetPosition(TextView).Y + TextView.VerticalOffset)?.FirstDocumentLine.LineNumber;
            var marker = owner.ViewModel?.BreakpointMarkers.FirstOrDefault(b => b.Line == line);
            ToolTip = marker == null ? "Click to add a C# breakpoint (F9)" : $"{(marker.Enabled ? marker.Status : "Disabled")} {(marker.IsTracepoint ? "tracepoint" : "breakpoint")} on line {line}"
                + (marker.Condition.Length > 0 ? $"\nCondition: {marker.Condition}" : "") + (marker.HitCondition.Length > 0 ? $"\nHit count: {marker.HitCondition}" : "")
                + (marker.IsTracepoint ? $"\nLogs: {marker.LogMessage}" : "") + "\nClick to remove. Right-click the line for conditions, hit counts and log messages.";
        }
        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Refresh;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Refresh;
        }
        private void Refresh(object? sender, EventArgs e) => InvalidateVisual();
    }
    private sealed class DiagnosticRenderer(EditorSurface owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext context)
        {
            if (!textView.VisualLinesValid || owner.ViewModel == null) return;
            var vm = owner.ViewModel;
            if (vm.ExecutionLine > 0 && vm.ExecutionLine <= owner.Document.LineCount)
            {
                var line = owner.Document.GetLineByNumber(vm.ExecutionLine);
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line)) context.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, 227, 180, 88)), null, rect);
            }
            foreach (var diagnostic in vm.Diagnostics.Where(d => d.Start >= 0 && d.Start < owner.Document.TextLength))
            {
                var segment = new TextSegment { StartOffset = diagnostic.Start, Length = Math.Min(Math.Max(1, diagnostic.Length), owner.Document.TextLength - diagnostic.Start) };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    context.DrawLine(new Pen((diagnostic.Severity == "Error" ? owner.TryFindResource("DangerBrush") : owner.TryFindResource("WarningBrush")) as Brush ?? Brushes.IndianRed, 1.5), new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));
            }
        }
    }
}
