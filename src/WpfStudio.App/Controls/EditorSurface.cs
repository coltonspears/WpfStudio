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
        Padding = new Thickness(4, 8, 8, 8);
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
        MouseHover += (_, e) =>
        {
            var position = GetPositionFromPoint(e.GetPosition(this)); if (position == null || ViewModel == null) return;
            var offset = Document.GetOffset(position.Value.Location);
            var diagnostic = ViewModel.Diagnostics.FirstOrDefault(d => offset >= d.Start && offset <= d.Start + Math.Max(1, d.Length));
            if (diagnostic != null) { _signature.Content = diagnostic.Id + ": " + diagnostic.Message; _signature.PlacementTarget = this; _signature.IsOpen = true; }
        };
        MouseHoverStopped += (_, _) => _signature.IsOpen = false;
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
        var extension = ViewModel.State.Extension;
        SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(extension == ".cs" ? "C#" : extension is ".xaml" or ".xml" or ".csproj" or ".props" or ".targets" ? "XML" : extension == ".json" ? "JavaScript" : extension == ".sql" ? "SQL" : null);
        IsReadOnly = ViewModel.State.Path.Contains(System.IO.Path.Combine("WpfStudio", "GeneratedSources"), StringComparison.OrdinalIgnoreCase);
        ViewModel.State.PropertyChanged += StateChanged; ViewModel.PropertyChanged += ModelChanged;
        ViewModel.NavigationRequested += Navigate; ViewModel.Diagnostics.CollectionChanged += DiagnosticsChanged; ViewModel.BreakpointLines.CollectionChanged += BreakpointsChanged;
        ViewModel.BreakpointMarkers.CollectionChanged += BreakpointsChanged;
        _syncing = false; Navigate();
    }
    private void Detach()
    {
        CancelPendingCompletion();
        _completion?.Close(); _completionRequest?.Cancel();
        if (_attached != null)
        {
            _attached.State.PropertyChanged -= StateChanged; _attached.PropertyChanged -= ModelChanged;
            _attached.NavigationRequested -= Navigate; _attached.Diagnostics.CollectionChanged -= DiagnosticsChanged; _attached.BreakpointLines.CollectionChanged -= BreakpointsChanged;
            _attached.BreakpointMarkers.CollectionChanged -= BreakpointsChanged;
        }
        _attached = null;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(DocumentState.Content) && !_syncing) SyncText(); }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(EditorViewModel.ExecutionLine)) { _margin.InvalidateVisual(); TextArea.TextView.InvalidateLayer(KnownLayer.Background); } }
    private void DiagnosticsChanged(object? sender, NotifyCollectionChangedEventArgs args) => TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
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
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pendingCompletion != null && e.Key == Key.Escape) { CancelPendingCompletion(); e.Handled = true; return; }
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = ShowCompletionAsync(); }
        else if (e.Key == Key.OemPeriod && Keyboard.Modifiers == ModifierKeys.Control && ContextMenu != null) { ContextMenu.PlacementTarget = this; ContextMenu.IsOpen = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { _completion?.Close(); _signature.IsOpen = false; }
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
        try
        {
            var result = await vm.CompleteAsync(caret, token);
            if (token.IsCancellationRequested || vm != ViewModel || version != vm.State.Version || caret != CaretOffset || result.Items.Count == 0 || !IsKeyboardFocusWithin) return;
            _completion?.Close();
            var window = new CompletionWindow(TextArea) { StartOffset = result.Start, EndOffset = caret, CloseWhenCaretAtBeginning = true };
            foreach (var item in result.Items.Take(250)) window.CompletionList.CompletionData.Add(new StudioCompletion(this, item, result.Version));
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
        if (_replayingInput) return;
        if (_pendingCompletion is { } pending)
        {
            pending.Input.Add(new BufferedInput(args.Text, null, null)); args.Handled = true; return;
        }
        if (_completion != null && args.Text.Length > 0 && !char.IsLetterOrDigit(args.Text[0]) && args.Text[0] != '_')
            _completion.CompletionList.RequestInsertion(args);
    }
    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs args)
    {
        if (_pendingCompletion is not { } pending || _replayingInput || args.Command is not RoutedCommand command || command == ApplicationCommands.Copy) return;
        pending.Input.Add(new BufferedInput(null, command, args.Parameter)); args.Handled = true;
    }

    private Task AcceptCompletionAsync(CompletionEntry entry, long version, ISegment segment, EventArgs insertionRequest)
    {
        if (ViewModel is not { } vm) return Task.CompletedTask;
        string? punctuation = insertionRequest is TextCompositionEventArgs textInput ? textInput.Text : null;
        if (insertionRequest is TextCompositionEventArgs textArgs) textArgs.Handled = true;
        int start = segment.Offset, length = segment.Length;
        return CommitCompletionAsync(async token =>
        {
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
    private sealed class StudioCompletion(EditorSurface owner, CompletionEntry entry, long version) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => entry.DisplayText;
        public object Content => entry.DisplayText;
        public object Description => entry.Description ?? string.Join(", ", entry.Tags);
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => _ = owner.AcceptCompletionAsync(entry, version, completionSegment, insertionRequestEventArgs);
    }
    private sealed class DebugMargin(EditorSurface owner) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(22, 0);
        protected override void OnRender(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } view || owner.ViewModel == null) return;
            context.DrawRectangle(owner.TryFindResource("SurfaceBrush") as Brush ?? Brushes.Transparent, null, new Rect(RenderSize));
            foreach (var visual in view.VisualLines)
            {
                var line = visual.FirstDocumentLine.LineNumber; var y = visual.VisualTop - view.VerticalOffset + visual.Height / 2;
                var marker = owner.ViewModel.BreakpointMarkers.FirstOrDefault(b => b.Line == line);
                if (marker != null)
                {
                    var brush = marker.Enabled ? Brushes.IndianRed : Brushes.SlateGray;
                    context.DrawEllipse(marker.Enabled && marker.Bound ? brush : null, new Pen(brush, 1.8), new Point(10, y), 5.5, 5.5);
                    if (marker.Condition.Length > 0) context.DrawEllipse(brush, null, new Point(10, y), 1.7, 1.7);
                }
                else if (owner.ViewModel.BreakpointLines.Contains(line)) context.DrawEllipse(Brushes.IndianRed, null, new Point(10, y), 5.5, 5.5);
                if (owner.ViewModel.ExecutionLine == line) context.DrawText(new FormattedText("➜", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.Goldenrod, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(1, y - 10));
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
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line)) context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 224, 173, 44)), null, rect);
            }
            foreach (var diagnostic in vm.Diagnostics.Where(d => d.Start >= 0 && d.Start < owner.Document.TextLength))
            {
                var segment = new TextSegment { StartOffset = diagnostic.Start, Length = Math.Min(Math.Max(1, diagnostic.Length), owner.Document.TextLength - diagnostic.Start) };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    context.DrawLine(new Pen(diagnostic.Severity == "Error" ? Brushes.IndianRed : Brushes.DarkGoldenrod, 1.5), new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));
            }
        }
    }
}
