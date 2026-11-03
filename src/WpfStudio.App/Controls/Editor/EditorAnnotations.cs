using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Controls.Editor;

/// <summary>
/// Git change margin, and in C# a row above each type and member with its reference counts and Git history. The
/// row is part of the declaration's first line, so nothing clickable sits beside the code itself.
/// </summary>
internal sealed class EditorAnnotations
{
    private readonly EditorSurface _owner;
    private readonly GitChangeMargin _changeMargin;
    /// <summary>A declaration's lens; its offsets follow edits until the next counts arrive.</summary>
    private sealed class TrackedLens(int position, int start, int end, CodeLensEntry entry)
    {
        public int Position = position, Start = start, End = end;
        public CodeLensEntry Entry { get; } = entry;
    }
    private List<TrackedLens> _lenses = [];
    private readonly Popup _changePopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Right, PopupAnimation = PopupAnimation.Fade };
    private readonly Popup _historyPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };

    public EditorAnnotations(EditorSurface owner)
    {
        _owner = owner;
        _changeMargin = new GitChangeMargin(owner, this);
        var numbers = owner.TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault();
        owner.TextArea.LeftMargins.Insert(numbers is null ? owner.TextArea.LeftMargins.Count : owner.TextArea.LeftMargins.IndexOf(numbers) + 1, _changeMargin);
        owner.TextArea.TextView.ElementGenerators.Add(new LensRowGenerator(owner, this));
    }

    public void UpdateChangeMargin() => _changeMargin.Update();

    public void CloseChangePopup() => _changePopup.IsOpen = false;

    public void ClosePopups() { _changePopup.IsOpen = false; _historyPopup.IsOpen = false; }

    public void ClearCodeLens() => _lenses = [];

    private bool ShowsLensRows => _owner.ViewModel is { IsCSharp: true } vm && (vm.ShowCodeLens || vm.ShowInlineBlame);

    public void ApplyCodeLens()
    {
        _lenses = [];
        if (ShowsLensRows && _owner.ViewModel is { } vm && vm.CodeLensVersion == vm.State.Version && _owner.Text == vm.State.Content)
            foreach (var entry in vm.CodeLenses.Where(entry => entry.Position <= _owner.Document.TextLength).OrderBy(entry => entry.Position))
            {
                var first = _owner.Document.GetLineByNumber(Math.Clamp(entry.StartLine > 0 ? entry.StartLine : entry.Line, 1, _owner.Document.LineCount));
                var last = _owner.Document.GetLineByNumber(Math.Clamp(Math.Max(entry.EndLine, first.LineNumber), 1, _owner.Document.LineCount));
                _lenses.Add(new(entry.Position, TextUtilities.GetLeadingWhitespace(_owner.Document, first).EndOffset, last.EndOffset, entry));
            }
        _owner.TextArea.TextView.Redraw();
    }

    public void ShiftCodeLens(int offset, int removed, int inserted)
    {
        if (_lenses.Count == 0) return;
        var kept = new List<TrackedLens>(_lenses.Count);
        foreach (var lens in _lenses)
            if (WpfStudio.Core.Text.DecorationOffsets.MapPoint(lens.Position, offset, removed, inserted) is { } moved
                && WpfStudio.Core.Text.DecorationOffsets.MapPoint(lens.Start, offset, removed, inserted) is { } start)
            {
                lens.Position = moved; lens.Start = start;
                lens.End = WpfStudio.Core.Text.DecorationOffsets.MapPoint(lens.End, offset, removed, inserted) ?? Math.Max(start, offset + inserted);
                kept.Add(lens);
            }
        _lenses = kept;
    }

    public void OnGitChanged()
    {
        _changePopup.IsOpen = false; // Its change no longer describes the file.
        _historyPopup.IsOpen = false;
        _changeMargin.Update(); // The margin appears once the file is known to be in a repository.
        _owner.TextArea.TextView.Redraw(); // Lens rows show the new blame.
    }

    /// <summary>The lens row for the declarations starting on one line: counts, then Git history.</summary>
    private UIElement? CreateLensRow(IReadOnlyList<TrackedLens> lenses)
    {
        if (_owner.ViewModel is not { } vm) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Separator() { if (row.Children.Count > 0) row.Children.Add(Annotation("  |  ")); }
        if (vm.ShowCodeLens)
            foreach (var lens in lenses)
            {
                var entry = lens.Entry;
                var text = (entry.References == 1 ? "1 reference" : $"{entry.References} references")
                    + (entry.XamlReferences > 0 ? $" · {entry.XamlReferences} in XAML" + (entry.XamlName is { } name ? $" via {name}" : "") : "");
                Separator();
                var block = LensLink(text + (lenses.Count > 1 ? $" ({entry.Name})" : ""));
                block.ToolTip = $"Find references to {entry.Name}, including bindings and event handlers in XAML.";
                block.MouseLeftButtonDown += (_, e) => { e.Handled = true; vm.RequestCodeLens(entry, lens.Position); };
                row.Children.Add(block);
            }
        if (vm.ShowInlineBlame && vm.BlameVersion == vm.State.Version && _owner.Text == vm.State.Content)
        {
            var lens = lenses[0];
            var first = _owner.Document.GetLineByOffset(Math.Min(lens.Start, _owner.Document.TextLength)).LineNumber;
            var last = _owner.Document.GetLineByOffset(Math.Clamp(lens.End, 0, _owner.Document.TextLength)).LineNumber;
            if (GitBlame.Summarize(vm.Blame, first, last) is { } history && (history.Changes.Count > 0 || history.UncommittedLines > 0))
            {
                Separator();
                var block = LensLink(history.Describe(DateTimeOffset.Now));
                block.ToolTip = "Show who changed " + lens.Entry.Name + " and when";
                block.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowHistory(block, lens.Entry.Name, first, last, history); };
                row.Children.Add(block);
            }
        }
        return row.Children.Count == 0 ? null : row;
    }

    private TextBlock LensLink(string text)
    {
        var block = Annotation(text);
        block.Margin = new Thickness(0);
        block.Cursor = Cursors.Hand;
        block.MouseEnter += (_, _) => block.TextDecorations = TextDecorations.Underline;
        block.MouseLeave += (_, _) => block.TextDecorations = null;
        return block; // Click handlers mark the press handled, so the caret stays where it is.
    }

    private void ShowHistory(FrameworkElement anchor, string name, int first, int last, BlameHistory history)
    {
        if (CreateHistoryCard(name, first, last, history) is not { } card) return;
        _historyPopup.Child = new ContentPresenter { Content = card };
        _historyPopup.PlacementTarget = anchor;
        _historyPopup.IsOpen = true;
    }

    public BlameHistoryCard? CreateHistoryCard(string name, int first, int last, BlameHistory history)
    {
        if (_owner.ViewModel is not { } vm) return null;
        return BlameHistoryCard.Create(name, first, last, history, DateTimeOffset.Now,
            new RelayCommand<BlameLine>(commit =>
            {
                _historyPopup.IsOpen = false;
                if (commit is not null) vm.RequestBlameCommit(commit);
            }),
            new RelayCommand(() => { _historyPopup.IsOpen = false; _owner.Focus(); }));
    }

    /// <summary>Subtle text in the editor's font, used for lens rows.</summary>
    private TextBlock Annotation(string text)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = _owner.FontFamily, FontSize = Math.Max(8, _owner.FontSize - 2), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 720
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InlayHintBrush");
        return block;
    }

    /// <summary>Restores the committed text of a changed block in one undoable edit.</summary>
    private void RevertChange(LineChange change)
    {
        var document = _owner.Document;
        if (_owner.IsReadOnly) return;
        var newline = TextUtilities.GetNewLineFromDocument(document, Math.Clamp(change.StartLine, 1, document.LineCount));
        var original = change.OriginalText.Replace("\n", newline, StringComparison.Ordinal);
        using (document.RunUpdate())
        {
            if (change.Kind == LineChangeKind.Deleted)
            {
                if (change.StartLine > document.LineCount) document.Insert(document.TextLength, newline + original);
                else document.Insert(document.GetLineByNumber(change.StartLine).Offset, original + newline);
                return;
            }
            var first = document.GetLineByNumber(Math.Clamp(change.StartLine, 1, document.LineCount));
            var last = document.GetLineByNumber(Math.Clamp(change.StartLine + change.LineCount - 1, 1, document.LineCount));
            if (change.Kind == LineChangeKind.Modified) document.Replace(first.Offset, last.EndOffset - first.Offset, original);
            else if (last.DelimiterLength > 0) document.Remove(first.Offset, last.Offset + last.TotalLength - first.Offset);
            else if (first.PreviousLine is { } previous) document.Remove(previous.EndOffset, last.EndOffset - previous.EndOffset); // Added at the end of the file.
            else document.Remove(first.Offset, last.EndOffset - first.Offset);
        }
    }

    private void ShowChange(LineChange change, int line)
    {
        _changePopup.Child = new ContentPresenter { Content = CreateChangeCard(change) };
        _changePopup.PlacementTarget = _changeMargin;
        var visual = _owner.TextArea.TextView.GetVisualLine(line);
        _changePopup.VerticalOffset = visual is null ? 0 : visual.VisualTop - _owner.TextArea.TextView.VerticalOffset;
        _changePopup.HorizontalOffset = 4;
        _changePopup.IsOpen = true;
    }

    public LineChangeCard CreateChangeCard(LineChange change)
    {
        // The popup can outlive its editor's state; revert only the same buffer while the change is still current.
        var model = _owner.ViewModel; var document = _owner.Document;
        return LineChangeCard.Create(change, !_owner.IsReadOnly,
            new RelayCommand(() =>
            {
                _changePopup.IsOpen = false;
                if (!ReferenceEquals(model, _owner.ViewModel) || !ReferenceEquals(document, _owner.Document) || model?.LineChanges.Contains(change) != true) return;
                RevertChange(change); _owner.Focus();
            }),
            new RelayCommand(() => { _changePopup.IsOpen = false; _owner.Focus(); })) with { Preferences = model?.Preferences };
    }

    /// <summary>Thin bars beside the line numbers: added (green), changed (accent) and deleted (red marker) since the last commit.</summary>
    private sealed class GitChangeMargin(EditorSurface owner, EditorAnnotations annotations) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(owner.ViewModel?.GitBase is null ? 0 : 6, 0);
        protected override void OnRender(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } view || owner.ViewModel is not { } vm || vm.LineChanges.Count == 0) return;
            context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // Hit-testable for clicks.
            var added = owner.TryFindResource("SuccessBrush") as Brush ?? Brushes.SeaGreen;
            var modified = owner.TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
            var deleted = owner.TryFindResource("DangerBrush") as Brush ?? Brushes.IndianRed;
            var lineCount = owner.Document.LineCount;
            foreach (var visual in view.VisualLines)
            {
                var number = visual.FirstDocumentLine.LineNumber;
                // Start at the text, below any lens row above the line.
                var top = visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                var height = visual.VisualTop + visual.Height - view.VerticalOffset - top;
                foreach (var change in LineChanges.At(vm.LineChanges, number, lineCount))
                {
                    if (change.Kind == LineChangeKind.Deleted)
                    {
                        var y = change.StartLine > lineCount ? top + height : top;
                        var marker = new StreamGeometry();
                        using (var g = marker.Open()) { g.BeginFigure(new Point(0, y - 4), true, true); g.LineTo(new Point(6, y), true, false); g.LineTo(new Point(0, y + 4), true, false); }
                        marker.Freeze();
                        context.DrawGeometry(deleted, null, marker);
                    }
                    else context.DrawRectangle(change.Kind == LineChangeKind.Added ? added : modified, null, new Rect(1, top, 3, height));
                }
            }
        }
        private LineChange? ChangeAt(MouseEventArgs e, out int line)
        {
            line = TextView?.GetVisualLineFromVisualTop(e.GetPosition(TextView).Y + TextView.VerticalOffset)?.FirstDocumentLine.LineNumber ?? -1;
            return owner.ViewModel is { } vm ? LineChanges.At(vm.LineChanges, line, owner.Document.LineCount).FirstOrDefault() : null;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (ChangeAt(e, out var line) is { } change) { annotations.ShowChange(change, line); e.Handled = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hovered = ChangeAt(e, out _);
            Cursor = hovered is null ? null : Cursors.Hand;
            ToolTip = hovered is { } change ? change.Kind switch
            {
                LineChangeKind.Added => "Added since the last commit. Click to remove.",
                LineChangeKind.Deleted => "Lines deleted here since the last commit. Click to see or restore them.",
                _ => "Changed since the last commit. Click to compare or revert."
            } : null;
        }
        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Refresh;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Refresh;
        }
        private void Refresh(object? sender, EventArgs e) => InvalidateVisual();
        public void Update() { InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>
    /// Adds a lens row above lines where declarations start. The row is a zero-width element at the line's first
    /// character whose height raises the line's baseline, so the code moves down and the row fills the space above.
    /// The caret never stops on it, and the caret and line numbers stay aligned with the text.
    /// </summary>
    private sealed class LensRowGenerator(EditorSurface owner, EditorAnnotations annotations) : VisualLineElementGenerator
    {
        private int _emittedAt = -1;
        public override void StartGeneration(ITextRunConstructionContext context) { base.StartGeneration(context); _emittedAt = -1; }

        private List<TrackedLens> LensesOn(DocumentLine line) =>
            annotations._lenses.Where(lens => lens.Start >= line.Offset && lens.Start <= line.EndOffset).ToList();

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!annotations.ShowsLensRows || annotations._lenses.Count == 0) return -1;
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            if (LensesOn(line).Count == 0) return -1;
            var anchor = TextUtilities.GetLeadingWhitespace(CurrentContext.Document, line).EndOffset;
            return anchor >= startOffset && anchor != _emittedAt ? anchor : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            var lenses = LensesOn(line);
            if (offset == _emittedAt || lenses.Count == 0 || annotations.CreateLensRow(lenses) is not { } row) return null;
            _emittedAt = offset;
            var view = owner.TextArea.TextView;
            var rowHeight = Math.Ceiling(view.DefaultLineHeight * 0.95);
            var host = new Canvas { Width = 0, Height = rowHeight + view.DefaultBaseline, ClipToBounds = false, Tag = EditorAdornment.Tag };
            Canvas.SetTop(row, Math.Max(0, (rowHeight - view.DefaultLineHeight) / 2 + 1));
            host.Children.Add(row);
            TextBlock.SetBaselineOffset(host, host.Height);
            return new LensElement(host);
        }
    }
    private sealed class LensElement(UIElement element) : InlineObjectElement(0, element)
    {
        public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode) => -1;
    }
}
