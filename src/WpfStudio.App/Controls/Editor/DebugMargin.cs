using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.App.Controls.Editor;

internal sealed class DebugMargin(EditorSurface owner) : AbstractMargin
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
