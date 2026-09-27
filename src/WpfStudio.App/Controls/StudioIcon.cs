using System.Windows;
using System.Windows.Media;

namespace WpfStudio.App.Controls;

/// <summary>Small vector navigation icons, drawn in device-independent units.</summary>
public sealed class StudioIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(StudioIcon), new FrameworkPropertyMetadata("File", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    protected override Size MeasureOverride(Size availableSize) => new(18, 18);
    private static readonly Geometry File = Freeze("M3,1.5 L10,1.5 15,6.5 15,16.5 3,16.5 Z M10,1.5 L10,6.5 15,6.5");
    private static readonly Geometry Folder = Freeze("M1.5,4 L7,4 9,6 16.5,6 16.5,15 1.5,15 Z M1.5,7 L16.5,7");
    private static readonly Geometry Project = Freeze("M9,1.5 L16,5.5 9,9.5 2,5.5 Z M2,5.5 L2,13 9,17 16,13 16,5.5 M9,9.5 L9,17");
    private static readonly Geometry Braces = Freeze("M7,7 L5,7 5,9 4,10 5,11 5,13 7,13 M11,7 L13,7 13,9 14,10 13,11 13,13 11,13");
    private static readonly Geometry Markup = Freeze("M7,8 L5,10 7,12 M11,8 L13,10 11,12 M10,7 L8,13");
    private static readonly Geometry Lines = Freeze("M5,9 L12,9 M5,12 L12,12");
    private static readonly Geometry Image = Freeze("M4,13 L7,10 9,12 11,9 14,13 M5,7 L6,7");
    private static readonly Geometry Database = Freeze("M2.5,5 C2.5,1.5 15.5,1.5 15.5,5 C15.5,8.5 2.5,8.5 2.5,5 M2.5,5 L2.5,13 C2.5,17 15.5,17 15.5,13 L15.5,5 M2.5,9 C2.5,12.5 15.5,12.5 15.5,9");
    private static Geometry Freeze(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }
    protected override void OnRender(DrawingContext drawing)
    {
        var kind = Kind?.ToLowerInvariant() ?? "file";
        var color = kind switch
        {
            "folder" => "#C39143", "project" => "#598CCB", "csharp" or "viewmodel" or "converter" => "#429775",
            "xaml" or "window" or "page" or "usercontrol" or "resourcedictionary" or "style" or "datatemplate" or "controltemplate" => "#A083C5",
            "sql" => "#5A99BD", "asset" => "#C18462", _ => "#8093AA"
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        var pen = new Pen(brush, 1.35) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var scale = Math.Min(ActualWidth, ActualHeight) / 18;
        drawing.PushTransform(new ScaleTransform(scale, scale));
        if (kind == "folder") drawing.DrawGeometry(null, pen, Folder);
        else if (kind == "project") drawing.DrawGeometry(null, pen, Project);
        else if (kind == "sql") drawing.DrawGeometry(null, pen, Database);
        else
        {
            drawing.DrawGeometry(null, pen, File);
            drawing.DrawGeometry(null, pen, kind is "csharp" or "viewmodel" or "converter" ? Braces : kind is "xaml" or "window" or "page" or "usercontrol" or "resourcedictionary" or "style" or "datatemplate" or "controltemplate" ? Markup : kind == "asset" ? Image : Lines);
        }
        drawing.Pop();
    }
}
