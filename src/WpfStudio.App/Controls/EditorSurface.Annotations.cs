using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Controls;

/// <summary>Git change margin, current-line blame and other annotations shown after the end of a line.</summary>
public sealed partial class EditorSurface
{
    private GitChangeMargin? _changeMargin;
    private LineEndGenerator? _blameAnnotation;
    private int _blameLine = -1;
    private readonly Popup _changePopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Right, PopupAnimation = PopupAnimation.Fade };

    private void InitializeAnnotations()
    {
        _changeMargin = new GitChangeMargin(this);
        var numbers = TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault();
        TextArea.LeftMargins.Insert(numbers is null ? TextArea.LeftMargins.Count : TextArea.LeftMargins.IndexOf(numbers) + 1, _changeMargin);
        _blameAnnotation = new LineEndGenerator(line => line.LineNumber == _blameLine ? CreateBlameAnnotation(line.LineNumber) : null);
        TextArea.TextView.ElementGenerators.Add(_blameAnnotation);
        TextArea.Caret.PositionChanged += (_, _) => UpdateBlameLine();
    }

    private void OnGitChanged()
    {
        _changeMargin?.Update(); // The margin appears once the file is known to be in a repository.
        _blameLine = -1; // Redraw the annotation with the new blame.
        UpdateBlameLine();
    }

    private void UpdateBlameLine()
    {
        var line = ViewModel is { ShowInlineBlame: true } ? TextArea.Caret.Line : -1;
        if (line == _blameLine) return;
        var previous = _blameLine;
        _blameLine = line;
        RedrawLine(previous); RedrawLine(line);
    }

    private void RedrawLine(int number)
    {
        if (number >= 1 && number <= Document.LineCount) TextArea.TextView.Redraw(Document.GetLineByNumber(number));
    }

    private UIElement? CreateBlameAnnotation(int lineNumber)
    {
        if (ViewModel is not { } vm || vm.BlameVersion != vm.State.Version || lineNumber > vm.Blame.Count) return null;
        var blame = vm.Blame[lineNumber - 1];
        var text = Annotation(blame.Describe(DateTimeOffset.Now), italic: true);
        text.ToolTip = blame.IsUncommitted ? "This line has changes that are not committed yet."
            : $"{blame.Author} <{blame.AuthorEmail}>\n{blame.Time.LocalDateTime:MMM d, yyyy · HH:mm}\n{blame.ShortCommit}  {blame.Summary}\n\nClick to show this commit in Git history.";
        if (!blame.IsUncommitted)
        {
            text.Cursor = Cursors.Hand;
            text.MouseLeftButtonDown += (_, e) => { e.Handled = true; vm.RequestBlameCommit(blame); };
        }
        return text;
    }

    /// <summary>Subtle text placed after a line's last character, in the editor's font.</summary>
    private TextBlock Annotation(string text, bool italic = false)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = FontFamily, FontSize = Math.Max(8, FontSize - 1.5), Margin = new Thickness(28, 0, 0, 0),
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 640
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InlayHintBrush");
        TextBlock.SetBaselineOffset(block, block.FontSize * FontFamily.Baseline);
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
        revert.Click += (_, _) => { _changePopup.IsOpen = false; RevertChange(change); Focus(); };
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
                var top = visual.VisualTop - view.VerticalOffset;
                foreach (var change in vm.LineChanges)
                {
                    if (change.Kind == LineChangeKind.Deleted)
                    {
                        var at = Math.Min(change.StartLine, lineCount);
                        if (at != number) continue;
                        var y = change.StartLine > lineCount ? top + visual.Height : top;
                        var marker = new StreamGeometry();
                        using (var g = marker.Open()) { g.BeginFigure(new Point(0, y - 4), true, true); g.LineTo(new Point(6, y), true, false); g.LineTo(new Point(0, y + 4), true, false); }
                        marker.Freeze();
                        context.DrawGeometry(deleted, null, marker);
                    }
                    else if (number >= change.StartLine && number < change.StartLine + change.LineCount)
                        context.DrawRectangle(change.Kind == LineChangeKind.Added ? added : modified, null, new Rect(1, top, 3, visual.Height));
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

    /// <summary>Places an element after the last character of chosen lines. The element takes no document text.</summary>
    private sealed class LineEndGenerator(Func<DocumentLine, UIElement?> create) : VisualLineElementGenerator
    {
        private int _emittedAt = -1;
        public override void StartGeneration(ITextRunConstructionContext context) { base.StartGeneration(context); _emittedAt = -1; }
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            return line.EndOffset >= startOffset && line.EndOffset != _emittedAt ? line.EndOffset : -1;
        }
        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            if (offset != line.EndOffset || offset == _emittedAt || create(line) is not { } element) return null;
            _emittedAt = offset;
            return new AnnotationElement(element);
        }
    }
    private sealed class AnnotationElement(UIElement element) : InlineObjectElement(0, element)
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
