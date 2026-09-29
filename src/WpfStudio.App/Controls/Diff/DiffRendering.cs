using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.App.Services;

namespace WpfStudio.App.Controls.Diff;

/// <summary>Theme-derived diff colours. Line tints are faint; changed words use a stronger tint of the same hue.</summary>
internal static class DiffPalette
{
    public static Color Color(FrameworkElement owner, string key, Color fallback) => (owner.TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;
    public static Brush Brush(FrameworkElement owner, string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;
    public static Brush Tint(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
    public static (Brush Line, Brush Word, Color Solid) Removed(FrameworkElement owner)
    {
        var color = Color(owner, "DangerBrush", Colors.IndianRed);
        return ThemeService.IsLight ? (Tint(color, 36), Tint(color, 84), color) : (Tint(color, 30), Tint(color, 78), color);
    }
    public static (Brush Line, Brush Word, Color Solid) Added(FrameworkElement owner)
    {
        var color = Color(owner, "SuccessBrush", Colors.SeaGreen);
        return ThemeService.IsLight ? (Tint(color, 36), Tint(color, 84), color) : (Tint(color, 28), Tint(color, 74), color);
    }
    public static Brush Filler(FrameworkElement owner)
    {
        var color = Color(owner, "BorderBrush", Colors.Gray);
        var pen = new Pen(new SolidColorBrush(color), 1); pen.Freeze();
        var drawing = new GeometryDrawing(null, pen, Geometry.Parse("M0,8 L8,0 M-2,2 L2,-2 M6,10 L10,6"));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}

/// <summary>Colours whole changed lines, changed words, alignment fillers and collapsed-region separators.</summary>
internal sealed class DiffBackgroundRenderer(FrameworkElement owner, Func<IReadOnlyList<DiffDisplayLine>> lines) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawing)
    {
        if (!textView.VisualLinesValid || textView.Document is not { } document) return;
        var display = lines();
        var removed = DiffPalette.Removed(owner);
        var added = DiffPalette.Added(owner);
        Brush? filler = null;
        double width = Math.Max(textView.ActualWidth, 1);
        foreach (var visual in textView.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (index < 0 || index >= display.Count) continue;
            var line = display[index];
            var rect = new Rect(0, visual.VisualTop - textView.VerticalOffset, width, visual.Height);
            switch (line.Kind)
            {
                case DiffLineKind.Removed:
                case DiffLineKind.Added:
                    var (lineBrush, wordBrush, _) = line.Kind == DiffLineKind.Removed ? removed : added;
                    drawing.DrawRectangle(lineBrush, null, rect);
                    var documentLine = visual.FirstDocumentLine;
                    foreach (var span in line.Spans)
                    {
                        int start = Math.Clamp(span.Start, 0, documentLine.Length);
                        int length = Math.Clamp(span.Length, 0, documentLine.Length - start);
                        if (length == 0) continue;
                        var segment = new TextSegment { StartOffset = documentLine.Offset + start, Length = length };
                        foreach (var word in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                            drawing.DrawRoundedRectangle(wordBrush, null, word, 2, 2);
                    }
                    break;
                case DiffLineKind.Filler:
                    drawing.DrawRectangle(filler ??= DiffPalette.Filler(owner), null, rect);
                    break;
                case DiffLineKind.Separator:
                    DrawSeparator(drawing, rect, line);
                    break;
            }
        }
    }

    private void DrawSeparator(DrawingContext drawing, Rect rect, DiffDisplayLine line)
    {
        drawing.DrawRectangle(DiffPalette.Brush(owner, "RaisedBrush", Brushes.DimGray), null, rect);
        var border = new Pen(DiffPalette.Brush(owner, "BorderBrush", Brushes.Gray), 1); border.Freeze();
        drawing.DrawLine(border, new Point(rect.Left, rect.Top + 0.5), new Point(rect.Right, rect.Top + 0.5));
        drawing.DrawLine(border, new Point(rect.Left, rect.Bottom - 0.5), new Point(rect.Right, rect.Bottom - 0.5));
        string label = $"⋯   {line.HiddenRows} unchanged line{(line.HiddenRows == 1 ? "" : "s")} · click to show";
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11.5,
            DiffPalette.Brush(owner, "MutedBrush", Brushes.Gray), VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        drawing.DrawText(text, new Point(rect.Left + 12, rect.Top + (rect.Height - text.Height) / 2));
    }
}

public enum DiffNumberColumns { Old, New, Both }

/// <summary>Original line numbers per displayed line, with a +/− marker. Fillers and separators have no number.</summary>
internal sealed class DiffLineNumberMargin(FrameworkElement owner, Func<IReadOnlyList<DiffDisplayLine>> lines, DiffNumberColumns columns) : AbstractMargin
{
    private const double MarkerWidth = 14, Gap = 10;
    private int _digits = 3;
    public event Action<DiffDisplayLine>? SeparatorClicked;

    private Typeface Typeface => new(owner.GetValue(TextElement.FontFamilyProperty) as FontFamily ?? new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double FontSize => (double)owner.GetValue(TextElement.FontSizeProperty) - 1;

    private FormattedText Text(string text, Brush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private double ColumnWidth => Text(new string('9', _digits), Brushes.Black).WidthIncludingTrailingWhitespace;

    protected override Size MeasureOverride(Size availableSize) =>
        new(Gap + ColumnWidth * (columns == DiffNumberColumns.Both ? 2 : 1) + (columns == DiffNumberColumns.Both ? Gap : 0) + MarkerWidth + 6, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null) { oldTextView.VisualLinesChanged -= Changed; oldTextView.ScrollOffsetChanged -= Changed; }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) { newTextView.VisualLinesChanged += Changed; newTextView.ScrollOffsetChanged += Changed; }
        InvalidateVisual();
    }
    private void Changed(object? sender, EventArgs args) => InvalidateVisual();
    /// <summary>Call after the displayed lines change: the gutter width follows the largest line number.</summary>
    public void Refresh()
    {
        int max = 0;
        foreach (var line in lines()) max = Math.Max(max, Math.Max(line.OldLine ?? 0, line.NewLine ?? 0));
        _digits = Math.Max(3, max.ToString(CultureInfo.InvariantCulture).Length);
        InvalidateMeasure(); InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(DiffPalette.Brush(owner, "EditorBrush", Brushes.Transparent), null, new Rect(RenderSize));
        if (TextView is not { VisualLinesValid: true } view) return;
        var display = lines();
        var numbers = DiffPalette.Brush(owner, "GutterTextBrush", Brushes.Gray);
        var removed = DiffPalette.Removed(owner);
        var added = DiffPalette.Added(owner);
        double column = ColumnWidth;
        foreach (var visual in view.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (index < 0 || index >= display.Count) continue;
            var line = display[index];
            var rect = new Rect(0, visual.VisualTop - view.VerticalOffset, RenderSize.Width, visual.Height);
            if (line.Kind == DiffLineKind.Separator)
            {
                drawing.DrawRectangle(DiffPalette.Brush(owner, "RaisedBrush", Brushes.DimGray), null, rect);
                continue;
            }
            if (line.Kind == DiffLineKind.Filler) { drawing.DrawRectangle(DiffPalette.Filler(owner), null, rect); continue; }
            if (line.Kind is DiffLineKind.Removed or DiffLineKind.Added)
                drawing.DrawRectangle(line.Kind == DiffLineKind.Removed ? removed.Line : added.Line, null, rect);
            double x = Gap;
            void Number(int? value)
            {
                if (value is { } number)
                {
                    var text = Text(number.ToString(CultureInfo.InvariantCulture), numbers);
                    drawing.DrawText(text, new Point(x + column - text.Width, rect.Top + (rect.Height - text.Height) / 2));
                }
                x += column;
            }
            if (columns != DiffNumberColumns.New) Number(line.OldLine);
            if (columns == DiffNumberColumns.Both) x += Gap;
            if (columns != DiffNumberColumns.Old) Number(line.NewLine);
            if (line.Kind is DiffLineKind.Removed or DiffLineKind.Added)
            {
                var marker = Text(line.Kind == DiffLineKind.Removed ? "−" : "+", DiffPalette.Tint(line.Kind == DiffLineKind.Removed ? removed.Solid : added.Solid, 255));
                drawing.DrawText(marker, new Point(x + 4 + (MarkerWidth - marker.Width) / 2, rect.Top + (rect.Height - marker.Height) / 2));
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (LineAt(e.GetPosition(TextView).Y) is { Kind: DiffLineKind.Separator } separator) { SeparatorClicked?.Invoke(separator); e.Handled = true; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = LineAt(e.GetPosition(TextView).Y) is { Kind: DiffLineKind.Separator } ? Cursors.Hand : null;
    }
    private DiffDisplayLine? LineAt(double y)
    {
        if (TextView is not { } view) return null;
        int? number = view.GetVisualLineFromVisualTop(y + view.VerticalOffset)?.FirstDocumentLine.LineNumber;
        var display = lines();
        return number is { } n && n - 1 < display.Count ? display[n - 1] : null;
    }
}

public enum DiffMark : byte { None, Removed, Added, Modified }

/// <summary>A strip beside the vertical scroll bar marking where changes are; click or drag to navigate.</summary>
internal sealed class DiffOverviewRuler : FrameworkElement
{
    private IReadOnlyList<DiffMark> _kinds = [];
    private double _viewportStart, _viewportLength, _fill = 1;
    public event Action<double>? Navigate;

    public DiffOverviewRuler() { Width = 12; ToolTip = "Changes in this file · click to jump"; Cursor = Cursors.Hand; }

    public void SetLines(IReadOnlyList<DiffMark> kinds) { _kinds = kinds; InvalidateVisual(); }
    /// <param name="fill">Fraction of the ruler the document occupies; below 1 when the whole document fits in the view.</param>
    public void SetViewport(double start, double length, double fill = 1)
    {
        if (Math.Abs(start - _viewportStart) < 0.0005 && Math.Abs(length - _viewportLength) < 0.0005 && Math.Abs(fill - _fill) < 0.0005) return;
        _viewportStart = start; _viewportLength = length; _fill = fill; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(RenderSize);
        drawing.DrawRectangle(DiffPalette.Brush(this, "EditorBrush", Brushes.Transparent), null, bounds);
        var rule = new Pen(DiffPalette.Brush(this, "BorderBrush", Brushes.Gray), 1); rule.Freeze();
        drawing.DrawLine(rule, new Point(0.5, 0), new Point(0.5, bounds.Height));
        if (_kinds.Count == 0 || bounds.Height <= 0) return;
        if (_viewportLength is > 0 and < 1)
            drawing.DrawRectangle(DiffPalette.Tint(DiffPalette.Color(this, "TextBrush", Colors.Gray), 22), null,
                new Rect(1, _viewportStart * bounds.Height, bounds.Width - 1, Math.Max(4, _viewportLength * bounds.Height)));
        var removed = DiffPalette.Tint(DiffPalette.Color(this, "DangerBrush", Colors.IndianRed), 230);
        var added = DiffPalette.Tint(DiffPalette.Color(this, "SuccessBrush", Colors.SeaGreen), 230);
        var modified = DiffPalette.Tint(DiffPalette.Color(this, "WarningBrush", Colors.Goldenrod), 230);
        double scale = bounds.Height * Math.Clamp(_fill, 0.01, 1) / _kinds.Count;
        for (int i = 0; i < _kinds.Count;)
        {
            var kind = _kinds[i];
            int end = i + 1;
            while (end < _kinds.Count && _kinds[end] == kind) end++;
            Brush? brush = kind switch { DiffMark.Removed => removed, DiffMark.Added => added, DiffMark.Modified => modified, _ => null };
            if (brush is not null) drawing.DrawRectangle(brush, null, new Rect(3, i * scale, bounds.Width - 5, Math.Max(2, (end - i) * scale)));
            i = end;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); CaptureMouse(); Jump(e); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured) Jump(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); ReleaseMouseCapture(); }
    private void Jump(MouseEventArgs e) { if (ActualHeight > 0) Navigate?.Invoke(Math.Clamp(e.GetPosition(this).Y / ActualHeight, 0, 1)); }
}
