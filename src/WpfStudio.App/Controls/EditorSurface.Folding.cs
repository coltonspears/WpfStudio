using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.App.Controls;

/// <summary>
/// The look of collapsible regions: chevrons in the gutter that brighten on hover, a tinted band and bracket over
/// the region a chevron would fold, and folded blocks drawn as a rounded pill that previews the hidden code.
/// AvalonEdit's FoldingManager still owns the regions and its element generator stays registered, because it is
/// what connects the text view to the manager for redraws; the pill generator runs ahead of it.
/// </summary>
public sealed partial class EditorSurface
{
    /// <summary>Marks editor adornments (folded pills, lens rows) so hovering them does not ask for Quick Info.</summary>
    internal static readonly object AdornmentTag = new();
    private FoldingGutter? _foldingGutter;
    private FoldedBlockGenerator? _foldedBlocks;
    private FoldRangeRenderer? _foldRange;
    private FoldingSection? _hoveredFold;

    private void InstallFoldingVisuals(FoldingManager manager)
    {
        foreach (var margin in TextArea.LeftMargins.OfType<FoldingMargin>().ToArray()) TextArea.LeftMargins.Remove(margin);
        _foldingGutter = new FoldingGutter(this, manager);
        TextArea.LeftMargins.Add(_foldingGutter);
        _foldedBlocks = new FoldedBlockGenerator(this, manager);
        TextArea.TextView.ElementGenerators.Insert(0, _foldedBlocks);
        _foldRange = new FoldRangeRenderer(this);
        TextArea.TextView.BackgroundRenderers.Add(_foldRange);
    }

    private void UninstallFoldingVisuals()
    {
        if (_foldingGutter is not null) TextArea.LeftMargins.Remove(_foldingGutter);
        if (_foldedBlocks is not null) TextArea.TextView.ElementGenerators.Remove(_foldedBlocks);
        if (_foldRange is not null) TextArea.TextView.BackgroundRenderers.Remove(_foldRange);
        _foldingGutter = null; _foldedBlocks = null; _foldRange = null; _hoveredFold = null;
    }

    private void SetHoveredFold(FoldingSection? section)
    {
        if (ReferenceEquals(section, _hoveredFold)) return;
        _hoveredFold = section;
        _foldingGutter?.InvalidateVisual();
        TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private static Brush ThemeBrush(FrameworkElement owner, string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;

    private static bool IsOverAdornment(object? source)
    {
        for (var node = source as DependencyObject; node is not null and not TextView; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, AdornmentTag)) return true;
        return false;
    }

    /// <summary>Chevrons beside the line numbers: down for an open region, right (in the accent colour) for a folded one.</summary>
    private sealed class FoldingGutter(EditorSurface owner, FoldingManager manager) : AbstractMargin
    {
        private const double Width = 16;
        private bool _pointerInside;

        protected override Size MeasureOverride(Size availableSize) => new(Width, 0);

        private IEnumerable<(VisualLine Line, FoldingSection Section, double Middle)> Markers()
        {
            if (TextView is not { VisualLinesValid: true } view) yield break;
            foreach (var line in view.VisualLines)
            {
                var section = manager.GetNextFolding(line.FirstDocumentLine.Offset);
                if (section is null || section.StartOffset > line.LastDocumentLine.EndOffset) continue;
                var middle = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextMiddle) - view.VerticalOffset;
                yield return (line, section, middle);
            }
        }

        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // Hit-testable between markers.
            if (TextView is not { VisualLinesValid: true } view) return;
            var quiet = ThemeBrush(owner, _pointerInside ? "FoldMarkerHotBrush" : "FoldMarkerBrush", Brushes.Gray);
            var hot = ThemeBrush(owner, "TextBrush", Brushes.White);
            var folded = ThemeBrush(owner, "AccentBrush", Brushes.SlateBlue);
            var hovered = owner._hoveredFold;
            if (hovered is not null) DrawBracket(context, view, hovered, ThemeBrush(owner, "AccentBrush", Brushes.SlateBlue));
            foreach (var (_, section, middle) in Markers())
            {
                var brush = section.IsFolded ? folded : ReferenceEquals(section, hovered) ? hot : quiet;
                var pen = new Pen(brush, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                pen.Freeze();
                var x = Width / 2;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    if (section.IsFolded) { g.BeginFigure(new Point(x - 1.5, middle - 3.5), false, false); g.LineTo(new Point(x + 2, middle), true, true); g.LineTo(new Point(x - 1.5, middle + 3.5), true, true); }
                    else { g.BeginFigure(new Point(x - 3.5, middle - 1.5), false, false); g.LineTo(new Point(x, middle + 2), true, true); g.LineTo(new Point(x + 3.5, middle - 1.5), true, true); }
                }
                geometry.Freeze();
                context.DrawGeometry(null, pen, geometry);
            }
        }

        // A thin bracket from below the chevron to the region's last line shows how far a fold reaches.
        private static void DrawBracket(DrawingContext context, TextView view, FoldingSection section, Brush brush)
        {
            var document = view.Document;
            if (document is null || section.IsFolded) return;
            var first = document.GetLineByOffset(section.StartOffset).LineNumber;
            var last = document.GetLineByOffset(Math.Min(section.EndOffset, document.TextLength)).LineNumber;
            double? top = null, bottom = null;
            foreach (var line in view.VisualLines)
            {
                if (line.FirstDocumentLine.LineNumber == first)
                    top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextBottom) - view.VerticalOffset;
                if (line.FirstDocumentLine.LineNumber <= last && line.LastDocumentLine.LineNumber >= last)
                    bottom = line.GetTextLineVisualYPosition(line.TextLines[^1], VisualYPosition.TextMiddle) - view.VerticalOffset;
            }
            top ??= 0;
            bottom ??= view.ActualHeight;
            if (bottom <= top) return;
            var faded = brush.CloneCurrentValue();
            faded.Opacity = 0.75;
            var pen = new Pen(faded, 1);
            pen.Freeze();
            context.DrawLine(pen, new Point(Width / 2, top.Value + 1), new Point(Width / 2, bottom.Value));
            context.DrawLine(pen, new Point(Width / 2, bottom.Value), new Point(Width - 2, bottom.Value));
        }

        private FoldingSection? SectionAt(MouseEventArgs e)
        {
            var y = e.GetPosition(this).Y;
            foreach (var (line, section, _) in Markers())
            {
                var top = line.VisualTop - TextView!.VerticalOffset;
                if (y >= top && y < top + line.Height) return section;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var section = SectionAt(e);
            Cursor = section is null ? null : Cursors.Hand;
            ToolTip = section is null ? null : section.IsFolded ? "Expand" : "Collapse";
            owner.SetHoveredFold(section);
        }

        protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _pointerInside = true; InvalidateVisual(); }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _pointerInside = false;
            owner.SetHoveredFold(null);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (SectionAt(e) is not { } section) return;
            section.IsFolded = !section.IsFolded;
            e.Handled = true;
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= Refresh;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += Refresh;
        }

        private void Refresh(object? sender, EventArgs e) => InvalidateVisual();
    }

    /// <summary>Tints the lines of the region under the gutter pointer, so a click's effect is visible before it happens.</summary>
    private sealed class FoldRangeRenderer(EditorSurface owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner._hoveredFold is not { IsFolded: false } section || textView.Document is not { } document || !textView.VisualLinesValid) return;
            var first = document.GetLineByOffset(section.StartOffset).LineNumber;
            var last = document.GetLineByOffset(Math.Min(section.EndOffset, document.TextLength)).LineNumber;
            var brush = ThemeBrush(owner, "FoldRegionBrush", Brushes.Transparent);
            foreach (var line in textView.VisualLines)
            {
                if (line.LastDocumentLine.LineNumber < first || line.FirstDocumentLine.LineNumber > last) continue;
                var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
                var bottom = line.VisualTop + line.Height - textView.VerticalOffset;
                drawingContext.DrawRectangle(brush, null, new Rect(0, top, textView.ActualWidth, bottom - top));
            }
        }
    }

    /// <summary>
    /// Draws a folded region as a pill with its title and line count. Clicking it expands the region, and hovering it
    /// previews the hidden lines. Nested folded regions that end later are merged, as AvalonEdit's generator does.
    /// </summary>
    private sealed class FoldedBlockGenerator(EditorSurface owner, FoldingManager manager) : VisualLineElementGenerator
    {
        private const int PreviewLines = 24;

        public override int GetFirstInterestedOffset(int startOffset) => manager.GetNextFoldedFoldingStart(startOffset);

        public override VisualLineElement? ConstructElement(int offset)
        {
            int until = -1;
            FoldingSection? section = null;
            foreach (var candidate in manager.GetFoldingsContaining(offset))
                if (candidate.IsFolded && candidate.EndOffset > until) { until = candidate.EndOffset; section = candidate; }
            if (section is null || until <= offset) return null;
            for (var extended = true; extended;)
            {
                extended = false;
                foreach (var candidate in manager.GetFoldingsContaining(until))
                    if (candidate.IsFolded && candidate.EndOffset > until) { until = candidate.EndOffset; extended = true; }
            }
            var document = CurrentContext.Document;
            var lines = document.GetLineByOffset(Math.Min(until, document.TextLength)).LineNumber - document.GetLineByOffset(offset).LineNumber + 1;
            return new FoldedBlockElement(until - offset, CreatePill(section, lines, document, offset, until));
        }

        private UIElement CreatePill(FoldingSection section, int lines, TextDocument document, int start, int end)
        {
            var size = Math.Max(8, owner.FontSize - 1);
            var title = new TextBlock { Text = string.IsNullOrEmpty(section.Title) ? "⋯" : section.Title.Replace("...", "…", StringComparison.Ordinal), FontFamily = owner.FontFamily, FontSize = size };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FoldPlaceholderBrush");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(title);
            if (lines > 1)
            {
                var count = new TextBlock { Text = $"  {lines} lines", FontSize = Math.Max(8, size - 2), VerticalAlignment = VerticalAlignment.Center };
                count.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
                content.Children.Add(count);
            }
            var padding = new Thickness(6, 0, 6, 0);
            var pill = new Border
            {
                Child = content, Padding = padding, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, Tag = AdornmentTag
            };
            pill.SetResourceReference(Border.BackgroundProperty, "FoldPlaceholderBackgroundBrush");
            pill.SetResourceReference(Border.BorderBrushProperty, "FoldPlaceholderBorderBrush");
            TextBlock.SetBaselineOffset(pill, padding.Top + 1 + size * owner.FontFamily.Baseline);
            pill.MouseEnter += (_, _) => pill.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            pill.MouseLeave += (_, _) => pill.SetResourceReference(Border.BorderBrushProperty, "FoldPlaceholderBorderBrush");
            pill.MouseLeftButtonDown += (_, e) => { section.IsFolded = false; e.Handled = true; };
            // The preview is built when the tooltip opens; a pill is created on every redraw.
            pill.ToolTip = "";
            pill.ToolTipOpening += (_, _) => pill.ToolTip = Preview(document, start, end);
            ToolTipService.SetInitialShowDelay(pill, 350);
            ToolTipService.SetShowDuration(pill, 60_000);
            return pill;
        }

        private FrameworkElement Preview(TextDocument document, int start, int end)
        {
            end = Math.Min(end, document.TextLength);
            var text = start < end ? document.GetText(start, end - start) : "";
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').SkipWhile(string.IsNullOrWhiteSpace).ToList();
            var hidden = Math.Max(0, lines.Count - PreviewLines);
            lines = lines.Take(PreviewLines).ToList();
            // Remove the common indentation so the preview starts at the left edge.
            var indent = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
            var body = string.Join("\n", lines.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
            var panel = new StackPanel { MaxWidth = 720 };
            panel.Children.Add(new TextBlock { Text = body, FontFamily = owner.FontFamily, FontSize = Math.Max(8, owner.FontSize - 1), TextTrimming = TextTrimming.CharacterEllipsis });
            var footer = new TextBlock { Text = (hidden > 0 ? $"… {hidden} more lines · " : "") + "Click to expand", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
            footer.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(footer);
            return panel;
        }
    }

    private sealed class FoldedBlockElement(int documentLength, UIElement element) : InlineObjectElement(documentLength, element);
}
