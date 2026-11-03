using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls.Editor;

/// <summary>Semantic colours from Roslyn layered over syntax highlighting: types, members, parameters.</summary>
internal sealed class SemanticColorizer : DocumentColorizingTransformer
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
internal sealed class InlayHintGenerator(EditorSurface owner) : VisualLineElementGenerator
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
internal sealed class HintElement(UIElement element) : InlineObjectElement(0, element)
{
    public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode) => -1;
}
internal sealed class DiagnosticRenderer(EditorSurface owner) : IBackgroundRenderer
{
    private static readonly Brush ExecutionLine = (Brush)new SolidColorBrush(Color.FromArgb(46, 227, 180, 88)).GetAsFrozen();
    private Pen? _errorPen, _warningPen;
    public KnownLayer Layer => KnownLayer.Selection;
    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || owner.ViewModel == null) return;
        var vm = owner.ViewModel;
        if (vm.ExecutionLine > 0 && vm.ExecutionLine <= owner.Document.LineCount)
        {
            var line = owner.Document.GetLineByNumber(vm.ExecutionLine);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line)) context.DrawRectangle(ExecutionLine, null, rect);
        }
        foreach (var diagnostic in vm.Diagnostics.Where(d => d.Start >= 0 && d.Start < owner.Document.TextLength))
        {
            var segment = new TextSegment { StartOffset = diagnostic.Start, Length = Math.Min(Math.Max(1, diagnostic.Length), owner.Document.TextLength - diagnostic.Start) };
            var pen = diagnostic.Severity == "Error"
                ? EditorSurface.ThemedPen(owner.TryFindResource("DangerBrush") as Brush ?? Brushes.IndianRed, 1.5, ref _errorPen)
                : EditorSurface.ThemedPen(owner.TryFindResource("WarningBrush") as Brush ?? Brushes.IndianRed, 1.5, ref _warningPen);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                context.DrawLine(pen, new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));
        }
    }
}
