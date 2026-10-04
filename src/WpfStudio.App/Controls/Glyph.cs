using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace WpfStudio.App.Controls;

/// <summary>
/// Monochrome vector glyph drawn on a 16-unit grid. It inherits the surrounding text colour,
/// so a glyph inside a button, menu item or tab follows hover, disabled and theme changes.
/// </summary>
public sealed class Glyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Glyph), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Glyph), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeWeightProperty = DependencyProperty.Register(nameof(StrokeWeight), typeof(double), typeof(Glyph), new FrameworkPropertyMetadata(1.35d, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Stroke width in 16-unit grid space; it scales with the rendered size.</summary>
    public double StrokeWeight { get => (double)GetValue(StrokeWeightProperty); set => SetValue(StrokeWeightProperty, value); }

    public static bool Exists(string? kind) => kind != null && Shapes.ContainsKey(kind);

    /// <summary>
    /// Natural size is 16×16, but never more than the space allowed. An explicit smaller Width or
    /// Height arrives here as the available size; reporting 16 anyway would make layout arrange the
    /// glyph at 16 and clip its right and bottom edges to the smaller slot.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) => IconMetrics.Fit(availableSize, 16);

    protected override void OnRender(DrawingContext drawing)
    {
        if (string.IsNullOrEmpty(Kind) || !Shapes.TryGetValue(Kind, out var shape)) return;
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        var scale = side / 16d;
        var brush = Foreground ?? Brushes.Gray;
        drawing.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        if (shape.Fill != null) drawing.DrawGeometry(brush, null, shape.Fill);
        if (shape.Stroke != null)
        {
            var pen = new Pen(brush, StrokeWeight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            drawing.DrawGeometry(null, pen, shape.Stroke);
        }
        drawing.Pop(); drawing.Pop();
    }

    private sealed record Shape(Geometry? Stroke, Geometry? Fill);
    private static Geometry Parse(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Play"] = new(null, Parse("M5,3.6 C5,3 5.6,2.7 6.1,3 L12.3,7.2 C12.9,7.6 12.9,8.4 12.3,8.8 L6.1,13 C5.6,13.3 5,13 5,12.4 Z")),
        ["PlayOutline"] = new(Parse("M5.25,3.75 L12.25,8 L5.25,12.25 Z"), null),
        ["Stop"] = new(null, Parse("M5,4 H11 C11.6,4 12,4.4 12,5 V11 C12,11.6 11.6,12 11,12 H5 C4.4,12 4,11.6 4,11 V5 C4,4.4 4.4,4 5,4 Z")),
        ["Pause"] = new(null, Parse("M4.5,3.5 H6.5 V12.5 H4.5 Z M9.5,3.5 H11.5 V12.5 H9.5 Z")),
        ["Continue"] = new(null, Parse("M3.5,3.5 H5.5 V12.5 H3.5 Z M7.5,3.9 C7.5,3.4 8,3.2 8.4,3.4 L13,7.4 C13.4,7.7 13.4,8.3 13,8.6 L8.4,12.6 C8,12.8 7.5,12.6 7.5,12.1 Z")),
        ["Restart"] = new(Parse("M12.6,9.2 A4.75,4.75 0 1 1 11.2,4.6 M11.6,2.2 V5 H8.8"), null),
        ["StepOver"] = new(Parse("M2.75,8.5 C3.5,4.25 10.5,3.25 12.75,7.25 M13,3.9 V7.4 H9.5"), Parse("M6.6,12.6 A1.4,1.4 0 1 0 9.4,12.6 A1.4,1.4 0 1 0 6.6,12.6 Z")),
        ["StepInto"] = new(Parse("M8,2.25 V8.75 M5.25,6 L8,8.75 L10.75,6"), Parse("M6.6,12.6 A1.4,1.4 0 1 0 9.4,12.6 A1.4,1.4 0 1 0 6.6,12.6 Z")),
        ["StepOut"] = new(Parse("M8,8.75 V2.25 M5.25,5 L8,2.25 L10.75,5"), Parse("M6.6,12.6 A1.4,1.4 0 1 0 9.4,12.6 A1.4,1.4 0 1 0 6.6,12.6 Z")),
        ["Build"] = new(Parse("M8.9,7.1 L2.75,13.25 M8.6,2.5 L13.5,7.4 L11.4,9.5 L6.5,4.6 Z"), null),
        ["Search"] = new(Parse("M2.75,7 A4.25,4.25 0 1 0 11.25,7 A4.25,4.25 0 1 0 2.75,7 Z M10.2,10.2 L13.5,13.5"), null),
        ["Command"] = new(Parse("M3.25,4.25 L6.75,8 L3.25,11.75 M8.75,11.75 H12.75"), null),
        ["Settings"] = new(Parse("M2.5,4.75 H7.25 M11.75,4.75 H13.5 M2.5,11.25 H4.25 M8.75,11.25 H13.5 M7.5,4.75 A2,2 0 1 0 11.5,4.75 A2,2 0 1 0 7.5,4.75 Z M4.5,11.25 A2,2 0 1 0 8.5,11.25 A2,2 0 1 0 4.5,11.25 Z"), null),
        ["Git"] = new(Parse("M3.15,3.75 A1.6,1.6 0 1 0 6.35,3.75 A1.6,1.6 0 1 0 3.15,3.75 Z M3.15,12.25 A1.6,1.6 0 1 0 6.35,12.25 A1.6,1.6 0 1 0 3.15,12.25 Z M9.65,4.75 A1.6,1.6 0 1 0 12.85,4.75 A1.6,1.6 0 1 0 9.65,4.75 Z M4.75,5.35 V10.65 M11.25,6.35 C11.25,9.25 4.75,8.25 4.75,10.65"), null),
        ["Package"] = new(Parse("M8,1.9 L13.5,4.9 V11.1 L8,14.1 L2.5,11.1 V4.9 Z M2.5,4.9 L8,7.9 L13.5,4.9 M8,7.9 V14.1 M5.25,3.4 L10.75,6.4"), null),
        ["Chat"] = new(Parse("M3.5,3 H12.5 C13.1,3 13.5,3.4 13.5,4 V10 C13.5,10.6 13.1,11 12.5,11 H8.25 L5.25,13.5 V11 H3.5 C2.9,11 2.5,10.6 2.5,10 V4 C2.5,3.4 2.9,3 3.5,3 Z M5.5,6.25 H10.5 M5.5,8.25 H8.5"), null),
        ["Database"] = new(Parse("M2.75,4.25 C2.75,1.75 13.25,1.75 13.25,4.25 C13.25,6.75 2.75,6.75 2.75,4.25 Z M2.75,4.25 V11.75 C2.75,14.25 13.25,14.25 13.25,11.75 V4.25 M2.75,8 C2.75,10.5 13.25,10.5 13.25,8"), null),
        ["Terminal"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M4.75,6 L6.75,8 L4.75,10 M8.25,10 H11.25"), null),
        ["Bug"] = new(Parse("M8,5.25 C10.25,5.25 11,7 11,9 C11,11.4 9.7,13.25 8,13.25 C6.3,13.25 5,11.4 5,9 C5,7 5.75,5.25 8,5.25 Z M8,7.5 V13 M5,8.75 H2.5 M11,8.75 H13.5 M5.3,11.25 L3.25,12.75 M10.7,11.25 L12.75,12.75 M5.6,6.5 L3.75,4.75 M10.4,6.5 L12.25,4.75 M6.25,5.6 C6.25,3.9 9.75,3.9 9.75,5.6"), null),
        ["Output"] = new(Parse("M3,4 H13 M3,8 H13 M3,12 H9"), null),
        ["Warning"] = new(Parse("M8,2.4 C8.3,2.4 8.5,2.55 8.65,2.8 L14,12.3 C14.3,12.8 13.95,13.4 13.4,13.4 H2.6 C2.05,13.4 1.7,12.8 2,12.3 L7.35,2.8 C7.5,2.55 7.7,2.4 8,2.4 Z M8,6.25 V9.25"), Parse("M7.15,11.2 A0.85,0.85 0 1 0 8.85,11.2 A0.85,0.85 0 1 0 7.15,11.2 Z")),
        ["Error"] = new(Parse("M2.25,8 A5.75,5.75 0 1 0 13.75,8 A5.75,5.75 0 1 0 2.25,8 Z M5.9,5.9 L10.1,10.1 M10.1,5.9 L5.9,10.1"), null),
        ["Info"] = new(Parse("M2.25,8 A5.75,5.75 0 1 0 13.75,8 A5.75,5.75 0 1 0 2.25,8 Z M8,7.25 V11"), Parse("M7.15,5.1 A0.85,0.85 0 1 0 8.85,5.1 A0.85,0.85 0 1 0 7.15,5.1 Z")),
        ["Folder"] = new(Parse("M2,4 C2,3.4 2.4,3 3,3 H6.2 L7.7,4.5 H13 C13.6,4.5 14,4.9 14,5.5 V12 C14,12.6 13.6,13 13,13 H3 C2.4,13 2,12.6 2,12 Z"), null),
        ["FolderOpen"] = new(Parse("M2,12 V4 C2,3.4 2.4,3 3,3 H6.2 L7.7,4.5 H12 C12.6,4.5 13,4.9 13,5.5 V6.75 M2,12 L3.9,7.4 C4.05,7 4.4,6.75 4.85,6.75 H14 C14.4,6.75 14.65,7.15 14.5,7.5 L12.6,12.4 C12.45,12.75 12.1,13 11.7,13 H3 C2.4,13 2,12.6 2,12 Z"), null),
        ["File"] = new(Parse("M4.75,1.75 H9.25 L12.25,4.75 V13.25 C12.25,13.8 11.8,14.25 11.25,14.25 H4.75 C4.2,14.25 3.75,13.8 3.75,13.25 V2.75 C3.75,2.2 4.2,1.75 4.75,1.75 M9.25,1.75 V4.75 H12.25"), null),
        ["Layers"] = new(Parse("M8,2.25 L14,5.25 L8,8.25 L2,5.25 Z M2,8.25 L8,11.25 L14,8.25 M2,11 L8,14 L14,11"), null),
        ["Plus"] = new(Parse("M8,3 V13 M3,8 H13"), null),
        ["NewFile"] = new(Parse("M8.75,14.25 H4.75 C4.2,14.25 3.75,13.8 3.75,13.25 V2.75 C3.75,2.2 4.2,1.75 4.75,1.75 H9.25 L12.25,4.75 V8.25 M9.25,1.75 V4.75 H12.25 M12,10 V14.5 M9.75,12.25 H14.25"), null),
        ["Swap"] = new(Parse("M2.75,5.5 H12.75 M10.25,3 L12.75,5.5 L10.25,8 M13.25,10.5 H3.25 M5.75,8 L3.25,10.5 L5.75,13"), null),
        ["Refresh"] = new(Parse("M13,8 A5,5 0 1 1 11.3,4.2 M11.6,1.9 V4.6 H8.9"), null),
        ["ChevronDown"] = new(Parse("M4.5,6.25 L8,9.75 L11.5,6.25"), null),
        ["ChevronRight"] = new(Parse("M6.25,4.5 L9.75,8 L6.25,11.5"), null),
        ["ChevronUp"] = new(Parse("M4.5,9.75 L8,6.25 L11.5,9.75"), null),
        ["SplitView"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M8,2.75 V13.25"), null),
        ["InlineView"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M4.5,6 H11.5 M4.5,8 H11.5 M4.5,10 H9"), null),
        ["Expand"] = new(Parse("M5,5.5 L8,2.5 L11,5.5 M5,10.5 L8,13.5 L11,10.5 M8,2.5 V13.5"), null),
        ["Diff"] = new(Parse("M5,2.5 V8.5 M2,5.5 H8 M8,11.5 H14 M3.5,13.5 L12.5,2.5"), null),
        ["Commit"] = new(Parse("M5.25,8 A2.75,2.75 0 1 0 10.75,8 A2.75,2.75 0 1 0 5.25,8 Z M1.5,8 H5.25 M10.75,8 H14.5"), null),
        ["Copy"] = new(Parse("M5.5,5.5 H12 C12.55,5.5 13,5.95 13,6.5 V13 C13,13.55 12.55,14 12,14 H5.5 C4.95,14 4.5,13.55 4.5,13 V6.5 C4.5,5.95 4.95,5.5 5.5,5.5 Z M11.5,5.5 V3.5 C11.5,2.95 11.05,2.5 10.5,2.5 H4 C3.45,2.5 3,2.95 3,3.5 V10 C3,10.55 3.45,11 4,11 H4.5"), null),
        ["Close"] = new(Parse("M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5"), null),
        ["Check"] = new(Parse("M3.25,8.5 L6.5,11.5 L12.75,4.75"), null),
        ["Sun"] = new(Parse("M5.25,8 A2.75,2.75 0 1 0 10.75,8 A2.75,2.75 0 1 0 5.25,8 Z M8,1.5 V2.75 M8,13.25 V14.5 M1.5,8 H2.75 M13.25,8 H14.5 M3.4,3.4 L4.3,4.3 M11.7,11.7 L12.6,12.6 M3.4,12.6 L4.3,11.7 M11.7,4.3 L12.6,3.4"), null),
        ["Moon"] = new(Parse("M13.25,9.75 A5.75,5.75 0 1 1 6.25,2.75 A4.5,4.5 0 0 0 13.25,9.75 Z"), null),
        ["Flask"] = new(Parse("M6.25,2 H9.75 M6.75,2 V6.25 L3,12.4 C2.65,13 3.1,13.75 3.8,13.75 H12.2 C12.9,13.75 13.35,13 13,12.4 L9.25,6.25 V2 M4.5,10 H11.5"), null),
        ["Trash"] = new(Parse("M2.75,4.25 H13.25 M6.25,4.25 V2.75 H9.75 V4.25 M4.25,4.25 L4.9,13 C4.95,13.6 5.4,14 6,14 H10 C10.6,14 11.05,13.6 11.1,13 L11.75,4.25"), null),
        ["Clock"] = new(Parse("M2.25,8 A5.75,5.75 0 1 0 13.75,8 A5.75,5.75 0 1 0 2.25,8 Z M8,4.75 V8 L10.25,9.5"), null),
        ["Keyboard"] = new(Parse("M2.75,4 H13.25 C13.8,4 14.25,4.45 14.25,5 V11 C14.25,11.55 13.8,12 13.25,12 H2.75 C2.2,12 1.75,11.55 1.75,11 V5 C1.75,4.45 2.2,4 2.75,4 Z M5,9.5 H11"), Parse("M3.8,6.6 A0.7,0.7 0 1 0 5.2,6.6 A0.7,0.7 0 1 0 3.8,6.6 Z M6.1,6.6 A0.7,0.7 0 1 0 7.5,6.6 A0.7,0.7 0 1 0 6.1,6.6 Z M8.5,6.6 A0.7,0.7 0 1 0 9.9,6.6 A0.7,0.7 0 1 0 8.5,6.6 Z M10.8,6.6 A0.7,0.7 0 1 0 12.2,6.6 A0.7,0.7 0 1 0 10.8,6.6 Z")),
        ["Link"] = new(Parse("M6.75,9.25 L9.25,6.75 M7.25,4.75 L8.4,3.6 C9.6,2.4 11.5,2.4 12.6,3.6 C13.8,4.8 13.8,6.6 12.6,7.8 L11.25,9.1 M8.75,11.25 L7.6,12.4 C6.4,13.6 4.5,13.6 3.4,12.4 C2.2,11.2 2.2,9.4 3.4,8.2 L4.75,6.9"), null),
        ["Image"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M2,11.5 L5.75,8 L8.5,10.5 L10.5,8.75 L14,12"), Parse("M9.4,5.9 A1.1,1.1 0 1 0 11.6,5.9 A1.1,1.1 0 1 0 9.4,5.9 Z")),
        ["Tag"] = new(Parse("M2.25,3.25 V7.3 C2.25,7.6 2.35,7.85 2.55,8.05 L8,13.5 C8.4,13.9 9,13.9 9.4,13.5 L13.5,9.4 C13.9,9 13.9,8.4 13.5,8 L8.05,2.55 C7.85,2.35 7.6,2.25 7.3,2.25 H3.25 C2.7,2.25 2.25,2.7 2.25,3.25 Z"), Parse("M4.25,5.25 A1,1 0 1 0 6.25,5.25 A1,1 0 1 0 4.25,5.25 Z")),
        ["Braces"] = new(Parse("M5.5,2.5 H5 C4.2,2.5 3.75,3 3.75,3.75 V6.5 C3.75,7.3 3.3,8 2.5,8 C3.3,8 3.75,8.7 3.75,9.5 V12.25 C3.75,13 4.2,13.5 5,13.5 H5.5 M10.5,2.5 H11 C11.8,2.5 12.25,3 12.25,3.75 V6.5 C12.25,7.3 12.7,8 13.5,8 C12.7,8 12.25,8.7 12.25,9.5 V12.25 C12.25,13 11.8,13.5 11,13.5 H10.5"), null),
        ["Markup"] = new(Parse("M5.25,4.5 L1.75,8 L5.25,11.5 M10.75,4.5 L14.25,8 L10.75,11.5 M9.25,3 L6.75,13"), null),
        ["Target"] = new(Parse("M2.25,8 A5.75,5.75 0 1 0 13.75,8 A5.75,5.75 0 1 0 2.25,8 Z M5.5,8 A2.5,2.5 0 1 0 10.5,8 A2.5,2.5 0 1 0 5.5,8 Z"), null),
        ["Plug"] = new(Parse("M5.5,2 V5 M10.5,2 V5 M3.5,5 H12.5 V7.5 C12.5,10 10.5,12 8,12 C5.5,12 3.5,10 3.5,7.5 Z M8,12 V14.25"), null),
        ["Sidebar"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M6.25,2.75 V13.25"), null),
        ["Panel"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M1.75,9.75 H14.25"), null),
        ["Save"] = new(Parse("M3,2.25 H10.75 L13.75,5.25 V12.75 C13.75,13.3 13.3,13.75 12.75,13.75 H3.25 C2.7,13.75 2.25,13.3 2.25,12.75 V3 C2.25,2.6 2.6,2.25 3,2.25 Z M5,2.25 V5.5 H10 V2.25 M4.75,13.75 V9.25 H11.25 V13.75"), null),
        ["Sample"] = new(Parse("M8,1.9 L13.5,4.9 V11.1 L8,14.1 L2.5,11.1 V4.9 Z"), Parse("M6.6,5.9 L10.6,8 L6.6,10.1 Z")),
        ["Collapse"] = new(Parse("M5,3 L8,6 L11,3 M5,13 L8,10 L11,13"), null),
        ["More"] = new(null, Parse("M2.4,8 A1.1,1.1 0 1 0 4.6,8 A1.1,1.1 0 1 0 2.4,8 Z M6.9,8 A1.1,1.1 0 1 0 9.1,8 A1.1,1.1 0 1 0 6.9,8 Z M11.4,8 A1.1,1.1 0 1 0 13.6,8 A1.1,1.1 0 1 0 11.4,8 Z")),
        ["Attach"] = new(Parse("M13.25,7.5 L8.1,12.65 C6.7,14.05 4.45,14.05 3.1,12.65 C1.7,11.3 1.7,9.05 3.1,7.65 L8.3,2.45 C9.2,1.55 10.7,1.55 11.6,2.45 C12.5,3.35 12.5,4.85 11.6,5.75 L6.45,10.9 C6,11.35 5.25,11.35 4.8,10.9 C4.35,10.45 4.35,9.7 4.8,9.25 L9.5,4.55"), null),
        ["Pin"] = new(Parse("M9.25,2.25 L13.75,6.75 M11.9,4.1 L9.1,6.9 L6.1,6.4 L4.6,7.9 L8.1,11.4 L9.6,9.9 L9.1,6.9 M6.35,9.65 L2.5,13.5"), null),
        ["Window"] = new(Parse("M3.5,3 H12.5 C13.05,3 13.5,3.45 13.5,4 V12 C13.5,12.55 13.05,13 12.5,13 H3.5 C2.95,13 2.5,12.55 2.5,12 V4 C2.5,3.45 2.95,3 3.5,3 Z M2.5,6.25 H13.5 M7,6.25 V13"), null),
        // Designer canvas and outline.
        ["ZoomIn"] = new(Parse("M2.75,7 A4.25,4.25 0 1 0 11.25,7 A4.25,4.25 0 1 0 2.75,7 Z M10.2,10.2 L13.5,13.5 M7,5 V9 M5,7 H9"), null),
        ["ZoomOut"] = new(Parse("M2.75,7 A4.25,4.25 0 1 0 11.25,7 A4.25,4.25 0 1 0 2.75,7 Z M10.2,10.2 L13.5,13.5 M5,7 H9"), null),
        ["Fit"] = new(Parse("M2.5,5 V3.5 C2.5,2.95 2.95,2.5 3.5,2.5 H5 M11,2.5 H12.5 C13.05,2.5 13.5,2.95 13.5,3.5 V5 M13.5,11 V12.5 C13.5,13.05 13.05,13.5 12.5,13.5 H11 M5,13.5 H3.5 C2.95,13.5 2.5,13.05 2.5,12.5 V11 M5.5,6 H10.5 V10 H5.5 Z"), null),
        ["ActualSize"] = new(Parse("M3.75,5.25 L5.5,4 V12 M10.5,5.25 L12.25,4 V12 M8,6.5 V6.55 M8,9.5 V9.55"), null),
        ["Minus"] = new(Parse("M3,8 H13"), null),
        ["ArrowDown"] = new(Parse("M8,2.25 V10.75 M4.5,7.25 L8,10.75 L11.5,7.25 M3,13.75 H13"), null),
        ["ArrowUp"] = new(Parse("M8,13.75 V5.25 M4.5,8.75 L8,5.25 L11.5,8.75 M3,2.25 H13"), null),
        ["Pointer"] = new(Parse("M4,2.5 V12.5 L6.9,9.9 L8.9,13.75 L10.6,12.9 L8.6,9.1 L12.4,8.9 Z"), null),
        ["Interact"] = new(Parse("M6,5 V13.25 L8.1,11.3 L9.6,14.25 L11,13.55 L9.55,10.65 L12.4,10.45 Z M6,1.75 V3 M2.75,5 H4 M3.45,2.45 L4.35,3.35 M8.55,2.45 L7.65,3.35"), null),
        ["Move"] = new(Parse("M8,2 V14 M2,8 H14 M6.25,3.75 L8,2 L9.75,3.75 M6.25,12.25 L8,14 L9.75,12.25 M3.75,6.25 L2,8 L3.75,9.75 M12.25,6.25 L14,8 L12.25,9.75"), null),
        ["Magnet"] = new(Parse("M3.5,2.75 V8 A4.5,4.5 0 0 0 12.5,8 V2.75 H10 V8 A2,2 0 0 1 6,8 V2.75 Z M3.5,5.25 H6 M10,5.25 H12.5"), null),
        ["Outline"] = new(Parse("M2.5,3.5 H5.5 M4,3.5 V12.5 M4,8 H7 M4,12.5 H7 M8.5,3.5 H13.5 M9.5,8 H13.5 M9.5,12.5 H13.5"), null),
        ["Eye"] = new(Parse("M1.75,8 C3.4,5 5.6,3.5 8,3.5 C10.4,3.5 12.6,5 14.25,8 C12.6,11 10.4,12.5 8,12.5 C5.6,12.5 3.4,11 1.75,8 Z M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8 Z"), null),
        ["Filter"] = new(Parse("M2.5,3.5 H13.5 L9.25,8.6 V13 L6.75,11.75 V8.6 Z"), null),
        ["Camera"] = new(Parse("M2.5,5.5 C2.5,4.95 2.95,4.5 3.5,4.5 H5.25 L6.25,3 H9.75 L10.75,4.5 H12.5 C13.05,4.5 13.5,4.95 13.5,5.5 V12 C13.5,12.55 13.05,13 12.5,13 H3.5 C2.95,13 2.5,12.55 2.5,12 Z M5.75,8.6 A2.25,2.25 0 1 0 10.25,8.6 A2.25,2.25 0 1 0 5.75,8.6 Z"), null),
        ["Grid"] = new(Parse("M3,2.75 H13 C13.7,2.75 14.25,3.3 14.25,4 V12 C14.25,12.7 13.7,13.25 13,13.25 H3 C2.3,13.25 1.75,12.7 1.75,12 V4 C1.75,3.3 2.3,2.75 3,2.75 Z M1.75,8 H14.25 M8,2.75 V13.25"), null),
        ["Stack"] = new(Parse("M3.5,2.75 H12.5 C13.05,2.75 13.5,3.2 13.5,3.75 V5.75 C13.5,6.3 13.05,6.75 12.5,6.75 H3.5 C2.95,6.75 2.5,6.3 2.5,5.75 V3.75 C2.5,3.2 2.95,2.75 3.5,2.75 Z M3.5,9.25 H12.5 C13.05,9.25 13.5,9.7 13.5,10.25 V12.25 C13.5,12.8 13.05,13.25 12.5,13.25 H3.5 C2.95,13.25 2.5,12.8 2.5,12.25 V10.25 C2.5,9.7 2.95,9.25 3.5,9.25 Z"), null),
        ["Artboard"] = new(Parse("M4.5,2 V14 M11.5,2 V14 M2,4.5 H14 M2,11.5 H14"), null),
        ["Text"] = new(Parse("M3.5,3.5 H12.5 M8,3.5 V12.5 M6.25,12.5 H9.75"), null),
        ["Button"] = new(Parse("M3.5,4.5 H12.5 C13.6,4.5 14.5,5.4 14.5,6.5 V9.5 C14.5,10.6 13.6,11.5 12.5,11.5 H3.5 C2.4,11.5 1.5,10.6 1.5,9.5 V6.5 C1.5,5.4 2.4,4.5 3.5,4.5 Z M5.5,8 H10.5"), null),
        ["Frame"] = new(Parse("M3.5,3 H12.5 C13.05,3 13.5,3.45 13.5,4 V12 C13.5,12.55 13.05,13 12.5,13 H3.5 C2.95,13 2.5,12.55 2.5,12 V4 C2.5,3.45 2.95,3 3.5,3 Z"), null),
        ["Input"] = new(Parse("M3.25,4.5 H12.75 C13.2,4.5 13.5,4.8 13.5,5.25 V10.75 C13.5,11.2 13.2,11.5 12.75,11.5 H3.25 C2.8,11.5 2.5,11.2 2.5,10.75 V5.25 C2.5,4.8 2.8,4.5 3.25,4.5 Z M5.25,6.5 V9.5"), null),
        ["List"] = new(Parse("M5.5,4 H13.5 M5.5,8 H13.5 M5.5,12 H13.5 M2.5,4 V4.05 M2.5,8 V8.05 M2.5,12 V12.05"), null),
        ["Shape"] = new(Parse("M5.5,2.5 L9,8.5 H2 Z M8,11 A2.5,2.5 0 1 0 13,11 A2.5,2.5 0 1 0 8,11 Z"), null),
        ["Toggle"] = new(Parse("M3.5,3 H12.5 C13.05,3 13.5,3.45 13.5,4 V12 C13.5,12.55 13.05,13 12.5,13 H3.5 C2.95,13 2.5,12.55 2.5,12 V4 C2.5,3.45 2.95,3 3.5,3 Z M5.25,8.25 L7.25,10.25 L10.75,6.25"), null),
        ["Slider"] = new(Parse("M2,8 H5.5 M10.5,8 H14 M5.5,8 A2.5,2.5 0 1 0 10.5,8 A2.5,2.5 0 1 0 5.5,8 Z"), null),
        ["Slot"] = new(Parse("M2.5,5 V3.5 C2.5,2.95 2.95,2.5 3.5,2.5 H5 M11,2.5 H12.5 C13.05,2.5 13.5,2.95 13.5,3.5 V5 M13.5,11 V12.5 C13.5,13.05 13.05,13.5 12.5,13.5 H11 M5,13.5 H3.5 C2.95,13.5 2.5,13.05 2.5,12.5 V11"), null),
        // Memory profiler.
        ["ChevronLeft"] = new(Parse("M9.75,4.5 L6.25,8 L9.75,11.5"), null),
        ["ArrowLeft"] = new(Parse("M13,8 H3.25 M7,4.25 L3.25,8 L7,11.75"), null),
        ["ArrowRight"] = new(Parse("M3,8 H12.75 M9,4.25 L12.75,8 L9,11.75"), null),
        ["Dashboard"] = new(Parse("M3,2.5 H6.5 C6.8,2.5 7,2.7 7,3 V7.5 C7,7.8 6.8,8 6.5,8 H3 C2.7,8 2.5,7.8 2.5,7.5 V3 C2.5,2.7 2.7,2.5 3,2.5 Z M9.5,2.5 H13 C13.3,2.5 13.5,2.7 13.5,3 V5 C13.5,5.3 13.3,5.5 13,5.5 H9.5 C9.2,5.5 9,5.3 9,5 V3 C9,2.7 9.2,2.5 9.5,2.5 Z M9.5,7.5 H13 C13.3,7.5 13.5,7.7 13.5,8 V13 C13.5,13.3 13.3,13.5 13,13.5 H9.5 C9.2,13.5 9,13.3 9,13 V8 C9,7.7 9.2,7.5 9.5,7.5 Z M3,10 H6.5 C6.8,10 7,10.2 7,10.5 V13 C7,13.3 6.8,13.5 6.5,13.5 H3 C2.7,13.5 2.5,13.3 2.5,13 V10.5 C2.5,10.2 2.7,10 3,10 Z"), null),
        ["Treemap"] = new(Parse("M3,2.5 H13 C13.3,2.5 13.5,2.7 13.5,3 V13 C13.5,13.3 13.3,13.5 13,13.5 H3 C2.7,13.5 2.5,13.3 2.5,13 V3 C2.5,2.7 2.7,2.5 3,2.5 Z M8.5,2.5 V13.5 M8.5,7.5 H13.5 M11,7.5 V13.5 M2.5,10 H8.5"), null),
        ["Flow"] = new(Parse("M2.5,3 V6.5 M2.5,9.5 V13 M13.5,4.5 V11.5 M2.5,4.75 C7.5,4.75 8.5,6.5 13.5,6.5 M2.5,11.25 C7.5,11.25 8.5,9.5 13.5,9.5"), null),
        ["Nodes"] = new(Parse("M6.5,3.5 A1.5,1.5 0 1 0 9.5,3.5 A1.5,1.5 0 1 0 6.5,3.5 Z M2,12.5 A1.5,1.5 0 1 0 5,12.5 A1.5,1.5 0 1 0 2,12.5 Z M11,12.5 A1.5,1.5 0 1 0 14,12.5 A1.5,1.5 0 1 0 11,12.5 Z M7.25,4.85 L4.25,11.15 M8.75,4.85 L11.75,11.15 M5,12.5 H11"), null),
        ["Tree"] = new(Parse("M2.5,2.5 H7 V5.5 H2.5 Z M4.75,5.5 V12 M4.75,8.5 H9 M4.75,12 H9 M9,7 H13.5 V10 H9 Z M9,10.75 H13.5 V13.5 H9 Z"), null),
        ["Anchor"] = new(Parse("M6.75,3.25 A1.25,1.25 0 1 0 9.25,3.25 A1.25,1.25 0 1 0 6.75,3.25 Z M8,4.5 V13.5 M5.5,6.75 H10.5 M2.75,9.25 C2.75,11.75 5,13.5 8,13.5 C11,13.5 13.25,11.75 13.25,9.25"), null),
        ["Leak"] = new(Parse("M8,2.25 C8,2.25 3.75,7 3.75,9.75 A4.25,4.25 0 0 0 12.25,9.75 C12.25,7 8,2.25 8,2.25 Z M6,10 A2,2 0 0 0 8,12"), null),
        ["Bulb"] = new(Parse("M6.25,12 H9.75 M6.75,14 H9.25 M8,1.75 A4,4 0 0 0 5.6,8.95 C6,9.3 6.25,9.75 6.25,10.25 V10.5 H9.75 V10.25 C9.75,9.75 10,9.3 10.4,8.95 A4,4 0 0 0 8,1.75 Z"), null),
        ["Chip"] = new(Parse("M4.5,3.5 H11.5 C12.05,3.5 12.5,3.95 12.5,4.5 V11.5 C12.5,12.05 12.05,12.5 11.5,12.5 H4.5 C3.95,12.5 3.5,12.05 3.5,11.5 V4.5 C3.5,3.95 3.95,3.5 4.5,3.5 Z M6,1.5 V3.5 M10,1.5 V3.5 M6,12.5 V14.5 M10,12.5 V14.5 M1.5,6 H3.5 M1.5,10 H3.5 M12.5,6 H14.5 M12.5,10 H14.5 M6.5,6.5 H9.5 V9.5 H6.5 Z"), null),
        ["Export"] = new(Parse("M8,10 V2.25 M5,5.25 L8,2.25 L11,5.25 M3.25,8.5 V12.75 C3.25,13.3 3.7,13.75 4.25,13.75 H11.75 C12.3,13.75 12.75,13.3 12.75,12.75 V8.5"), null),
        ["Minimap"] = new(Parse("M3,3 H13 C13.55,3 14,3.45 14,4 V12 C14,12.55 13.55,13 13,13 H3 C2.45,13 2,12.55 2,12 V4 C2,3.45 2.45,3 3,3 Z M8,7.5 H12 V11 H8 Z"), null),
        ["Baseline"] = new(Parse("M2.5,13.5 H13.5 M4,10.5 V7.5 M7,10.5 V4.5 M10,10.5 V6 M13,10.5 V8.5"), null),
    };
}
