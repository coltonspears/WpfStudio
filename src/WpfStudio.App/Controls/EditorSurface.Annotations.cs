using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.Contracts;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Controls;

/// <summary>
/// Git change margin, and in C# a row above each type and member with its reference counts and Git history. The
/// row is part of the declaration's first line, so nothing clickable sits beside the code itself.
/// </summary>
public sealed partial class EditorSurface
{
    private GitChangeMargin? _changeMargin;
    private LensRowGenerator? _lensRows;
    /// <summary>A declaration's lens; its offsets follow edits until the next counts arrive.</summary>
    private sealed class TrackedLens(int position, int start, int end, CodeLensEntry entry)
    {
        public int Position = position, Start = start, End = end;
        public CodeLensEntry Entry { get; } = entry;
    }
    private List<TrackedLens> _lenses = [];
    private readonly Popup _changePopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Right, PopupAnimation = PopupAnimation.Fade };
    private readonly Popup _historyPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };

    private void InitializeAnnotations()
    {
        _changeMargin = new GitChangeMargin(this);
        var numbers = TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault();
        TextArea.LeftMargins.Insert(numbers is null ? TextArea.LeftMargins.Count : TextArea.LeftMargins.IndexOf(numbers) + 1, _changeMargin);
        _lensRows = new LensRowGenerator(this);
        TextArea.TextView.ElementGenerators.Add(_lensRows);
    }

    private bool ShowsLensRows => ViewModel is { IsCSharp: true } vm && (vm.ShowCodeLens || vm.ShowInlineBlame);

    private void ApplyCodeLens()
    {
        _lenses = [];
        if (ShowsLensRows && ViewModel is { } vm && vm.CodeLensVersion == vm.State.Version && Text == vm.State.Content)
            foreach (var entry in vm.CodeLenses.Where(entry => entry.Position <= Document.TextLength).OrderBy(entry => entry.Position))
            {
                var first = Document.GetLineByNumber(Math.Clamp(entry.StartLine > 0 ? entry.StartLine : entry.Line, 1, Document.LineCount));
                var last = Document.GetLineByNumber(Math.Clamp(Math.Max(entry.EndLine, first.LineNumber), 1, Document.LineCount));
                _lenses.Add(new(entry.Position, TextUtilities.GetLeadingWhitespace(Document, first).EndOffset, last.EndOffset, entry));
            }
        TextArea.TextView.Redraw();
    }

    private void ShiftCodeLens(int offset, int removed, int inserted)
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

    private void OnGitChanged()
    {
        _changePopup.IsOpen = false; // Its change no longer describes the file.
        _historyPopup.IsOpen = false;
        _changeMargin?.Update(); // The margin appears once the file is known to be in a repository.
        TextArea.TextView.Redraw(); // Lens rows show the new blame.
    }

    /// <summary>The lens row for the declarations starting on one line: counts, then Git history.</summary>
    private UIElement? CreateLensRow(IReadOnlyList<TrackedLens> lenses)
    {
        if (ViewModel is not { } vm) return null;
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
        if (vm.ShowInlineBlame && vm.BlameVersion == vm.State.Version && Text == vm.State.Content)
        {
            var lens = lenses[0];
            var first = Document.GetLineByOffset(Math.Min(lens.Start, Document.TextLength)).LineNumber;
            var last = Document.GetLineByOffset(Math.Clamp(lens.End, 0, Document.TextLength)).LineNumber;
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

    /// <summary>The commits behind a declaration, newest first, each opening its change to this file in Git history.</summary>
    private void ShowHistory(FrameworkElement anchor, string name, int first, int last, BlameHistory history)
    {
        if (ViewModel is not { } vm) return;
        var now = DateTimeOffset.Now;
        var panel = new StackPanel { Width = 460 };
        panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        var scope = $"Lines {first}–{last} · {history.Changes.Count} commit{(history.Changes.Count == 1 ? "" : "s")} by {history.Authors} author{(history.Authors == 1 ? "" : "s")}";
        panel.Children.Add(new TextBlock { Text = scope, FontSize = 11, Margin = new Thickness(0, 2, 0, 8) }.WithResource(TextBlock.ForegroundProperty, "MutedBrush"));
        var list = new StackPanel();
        if (history.UncommittedLines > 0)
            list.Children.Add(HistoryRow("Uncommitted changes", $"{history.UncommittedLines} line{(history.UncommittedLines == 1 ? "" : "s")} not committed yet · use the change bars in the margin to compare or revert", null));
        foreach (var change in history.Changes.Take(12))
        {
            var commit = change.Commit;
            var detail = $"{commit.Author} · {commit.Time.LocalDateTime:MMM d, yyyy} ({BlameLine.Relative(commit.Time, now)}) · {commit.ShortCommit} · {change.Lines} line{(change.Lines == 1 ? "" : "s")}";
            list.Children.Add(HistoryRow(commit.Summary.Length == 0 ? commit.ShortCommit : commit.Summary, detail, commit));
        }
        if (history.Changes.Count > 12)
            list.Children.Add(new TextBlock { Text = $"{history.Changes.Count - 12} older commits are in Git history.", FontSize = 11, Margin = new Thickness(6, 4, 0, 0) }.WithResource(TextBlock.ForegroundProperty, "MutedBrush"));
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => { _historyPopup.IsOpen = false; Focus(); };
        footer.Children.Add(close);
        panel.Children.Add(footer);
        var chrome = new Border { Child = panel, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        chrome.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        _historyPopup.Child = chrome;
        _historyPopup.PlacementTarget = anchor;
        _historyPopup.IsOpen = true;

        FrameworkElement HistoryRow(string title, string detail, BlameLine? commit)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) }.WithResource(TextBlock.ForegroundProperty, "MutedBrush"));
            var content = new DockPanel();
            if (commit is not null)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                var show = new Button { Content = "Show change", ToolTip = "Open this commit in Git history with this file's diff selected", Margin = new Thickness(0, 0, 4, 0) };
                show.Click += (_, _) => { _historyPopup.IsOpen = false; vm.RequestBlameCommit(commit); };
                var copy = new Button { Content = "Copy ID", ToolTip = commit.Commit };
                copy.Click += (_, _) => { try { Clipboard.SetText(commit.Commit); } catch (System.Runtime.InteropServices.ExternalException) { } };
                actions.Children.Add(show); actions.Children.Add(copy);
                DockPanel.SetDock(actions, Dock.Right);
                content.Children.Add(actions);
            }
            content.Children.Add(text);
            var row = new Border { Child = content, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent };
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            return row;
        }
    }

    /// <summary>Subtle text in the editor's font, used for lens rows.</summary>
    private TextBlock Annotation(string text)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = FontFamily, FontSize = Math.Max(8, FontSize - 2), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 720
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InlayHintBrush");
        return block;
    }

    /// <summary>Restores the committed text of a changed block in one undoable edit.</summary>
    internal void RevertChange(LineChange change)
    {
        if (IsReadOnly) return;
        var newline = TextUtilities.GetNewLineFromDocument(Document, Math.Clamp(change.StartLine, 1, Document.LineCount));
        var original = change.OriginalText.Replace("\n", newline, StringComparison.Ordinal);
        using (Document.RunUpdate())
        {
            if (change.Kind == LineChangeKind.Deleted)
            {
                if (change.StartLine > Document.LineCount) Document.Insert(Document.TextLength, newline + original);
                else Document.Insert(Document.GetLineByNumber(change.StartLine).Offset, original + newline);
                return;
            }
            var first = Document.GetLineByNumber(Math.Clamp(change.StartLine, 1, Document.LineCount));
            var last = Document.GetLineByNumber(Math.Clamp(change.StartLine + change.LineCount - 1, 1, Document.LineCount));
            if (change.Kind == LineChangeKind.Modified) Document.Replace(first.Offset, last.EndOffset - first.Offset, original);
            else if (last.DelimiterLength > 0) Document.Remove(first.Offset, last.Offset + last.TotalLength - first.Offset);
            else if (first.PreviousLine is { } previous) Document.Remove(previous.EndOffset, last.EndOffset - previous.EndOffset); // Added at the end of the file.
            else Document.Remove(first.Offset, last.EndOffset - first.Offset);
        }
    }

    private void ShowChange(LineChange change, int line)
    {
        var kind = change.Kind switch { LineChangeKind.Added => "Added since the last commit", LineChangeKind.Deleted => "Deleted since the last commit", _ => "Changed since the last commit" };
        var panel = new StackPanel { MaxWidth = 720 };
        panel.Children.Add(new TextBlock { Text = kind, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        if (change.OriginalText.Length > 0 || change.Kind != LineChangeKind.Added)
        {
            panel.Children.Add(new TextBlock { Text = "Committed version", FontSize = 11, Margin = new Thickness(0, 0, 0, 3) }.WithResource(TextBlock.ForegroundProperty, "MutedBrush"));
            var original = new TextBox
            {
                Text = change.OriginalText.Length == 0 ? "(empty line)" : change.OriginalText, IsReadOnly = true, FontFamily = FontFamily, FontSize = Math.Max(8, FontSize - 1),
                MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap
            };
            panel.Children.Add(original);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var revert = new Button { Content = change.Kind == LineChangeKind.Added ? "Remove added lines" : "Revert change", IsEnabled = !IsReadOnly, Margin = new Thickness(0, 0, 6, 0) };
        // The popup can outlive its editor's state; revert only the same buffer while the change is still current.
        var model = ViewModel; var document = Document;
        revert.Click += (_, _) =>
        {
            _changePopup.IsOpen = false;
            if (!ReferenceEquals(model, ViewModel) || !ReferenceEquals(document, Document) || model?.LineChanges.Contains(change) != true) return;
            RevertChange(change); Focus();
        };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => { _changePopup.IsOpen = false; Focus(); };
        buttons.Children.Add(revert); buttons.Children.Add(close);
        panel.Children.Add(buttons);
        var chrome = new Border { Child = panel, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        chrome.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        _changePopup.Child = chrome;
        _changePopup.PlacementTarget = _changeMargin;
        var visual = TextArea.TextView.GetVisualLine(line);
        _changePopup.VerticalOffset = visual is null ? 0 : visual.VisualTop - TextArea.TextView.VerticalOffset;
        _changePopup.HorizontalOffset = 4;
        _changePopup.IsOpen = true;
    }

    /// <summary>Thin bars beside the line numbers: added (green), changed (accent) and deleted (red marker) since the last commit.</summary>
    private sealed class GitChangeMargin(EditorSurface owner) : AbstractMargin
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
                foreach (var change in vm.LineChanges)
                {
                    if (change.Kind == LineChangeKind.Deleted)
                    {
                        var at = Math.Min(change.StartLine, lineCount);
                        if (at != number) continue;
                        var y = change.StartLine > lineCount ? top + height : top;
                        var marker = new StreamGeometry();
                        using (var g = marker.Open()) { g.BeginFigure(new Point(0, y - 4), true, true); g.LineTo(new Point(6, y), true, false); g.LineTo(new Point(0, y + 4), true, false); }
                        marker.Freeze();
                        context.DrawGeometry(deleted, null, marker);
                    }
                    else if (number >= change.StartLine && number < change.StartLine + change.LineCount)
                        context.DrawRectangle(change.Kind == LineChangeKind.Added ? added : modified, null, new Rect(1, top, 3, height));
                }
            }
        }
        private LineChange? ChangeAt(MouseEventArgs e, out int line)
        {
            line = TextView?.GetVisualLineFromVisualTop(e.GetPosition(TextView).Y + TextView.VerticalOffset)?.FirstDocumentLine.LineNumber ?? -1;
            var number = line;
            var lineCount = owner.Document.LineCount;
            return owner.ViewModel?.LineChanges.FirstOrDefault(change => change.Kind == LineChangeKind.Deleted
                ? Math.Min(change.StartLine, lineCount) == number
                : number >= change.StartLine && number < change.StartLine + change.LineCount);
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (ChangeAt(e, out var line) is { } change) { owner.ShowChange(change, line); e.Handled = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = ChangeAt(e, out _) is null ? null : Cursors.Hand;
            ToolTip = ChangeAt(e, out _) is { } change ? change.Kind switch
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
    private sealed class LensRowGenerator(EditorSurface owner) : VisualLineElementGenerator
    {
        private int _emittedAt = -1;
        public override void StartGeneration(ITextRunConstructionContext context) { base.StartGeneration(context); _emittedAt = -1; }

        private List<TrackedLens> LensesOn(DocumentLine line) =>
            owner._lenses.Where(lens => lens.Start >= line.Offset && lens.Start <= line.EndOffset).ToList();

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!owner.ShowsLensRows || owner._lenses.Count == 0) return -1;
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            if (LensesOn(line).Count == 0) return -1;
            var anchor = TextUtilities.GetLeadingWhitespace(CurrentContext.Document, line).EndOffset;
            return anchor >= startOffset && anchor != _emittedAt ? anchor : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            var lenses = LensesOn(line);
            if (offset == _emittedAt || lenses.Count == 0 || owner.CreateLensRow(lenses) is not { } row) return null;
            _emittedAt = offset;
            var view = owner.TextArea.TextView;
            var rowHeight = Math.Ceiling(view.DefaultLineHeight * 0.95);
            var host = new Canvas { Width = 0, Height = rowHeight + view.DefaultBaseline, ClipToBounds = false, Tag = AdornmentTag };
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

internal static class FrameworkElementExtensions
{
    public static T WithResource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
