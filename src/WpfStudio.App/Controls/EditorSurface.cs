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
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using WpfStudio.App.ViewModels;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

[assembly: InternalsVisibleTo("WpfStudio.Shell.Tests")]

namespace WpfStudio.App.Controls;

/// <summary>Presentation adapter for AvalonEdit: caret, highlighting, adorners and completion popups.</summary>
public sealed class EditorSurface : TextEditor
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
    private readonly DebugMargin _margin;
    private readonly DiagnosticRenderer _diagnostics;
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
        PreviewMouseDown += (_, _) => CancelPendingCompletion();
        LostKeyboardFocus += (_, _) => CancelPendingCompletion();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
        MouseHover += OnMouseHover;
        MouseHoverStopped += (_, _) => CancelHover();
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
        _syncing = false; Navigate();
    }
    private void Detach()
    {
        CancelHover();
        CancelPendingCompletion();
        _completion?.Close(); _completionRequest?.Cancel();
        if (_attached != null)
        {
            _attached.State.PropertyChanged -= StateChanged; _attached.PropertyChanged -= ModelChanged;
            _attached.NavigationRequested -= Navigate; _attached.Diagnostics.CollectionChanged -= DiagnosticsChanged; _attached.BreakpointLines.CollectionChanged -= BreakpointsChanged;
            _attached.SelectionRequested -= RestoreSelection;
            _attached.BreakpointMarkers.CollectionChanged -= BreakpointsChanged;
        }
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
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = ShowCompletionAsync(); }
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
        if (ViewModel is not { } vm || GetPositionFromPoint(e.GetPosition(this)) is not { } position) return;
        int offset = Document.GetOffset(position.Location);
        long version = vm.State.Version;
        _hoverRequest = new CancellationTokenSource();
        var token = _hoverRequest.Token;
        var diagnostic = vm.Diagnostics.FirstOrDefault(d => offset >= d.Start && offset < d.Start + Math.Max(1, d.Length));
        string? message = diagnostic is null ? null : diagnostic.Id + ": " + diagnostic.Message;
        if (message is not null) ShowHover(message);
        try
        {
            var hover = await vm.HoverAsync(offset, token);
            if (token.IsCancellationRequested || vm != ViewModel || version != vm.State.Version) return;
            if (hover is not null) message = message is null ? hover.Text : message + Environment.NewLine + Environment.NewLine + hover.Text;
            if (!string.IsNullOrEmpty(message)) ShowHover(message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && vm == ViewModel) vm.LanguageStatus = ex.Message; }
    }
    private void ShowHover(string text)
    {
        _signature.Content = text; _signature.PlacementTarget = this; _signature.IsOpen = true;
    }
    private void CancelHover()
    {
        _hoverRequest?.Cancel(); _hoverRequest?.Dispose(); _hoverRequest = null;
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
                    context.DrawEllipse(marker.Enabled && marker.Bound ? brush : null, new Pen(brush, 1.8), new Point(10, y), 5.5, 5.5);
                    if (marker.Condition.Length > 0) context.DrawEllipse(brush, null, new Point(10, y), 1.7, 1.7);
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
            ToolTip = marker == null ? "Click to add a C# breakpoint (F9)" : $"{(marker.Enabled ? marker.Status : "Disabled")} breakpoint on line {line}" + (marker.Condition.Length > 0 ? $"\nCondition: {marker.Condition}" : "") + "\nClick to remove. Right-click the line for conditions.";
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
