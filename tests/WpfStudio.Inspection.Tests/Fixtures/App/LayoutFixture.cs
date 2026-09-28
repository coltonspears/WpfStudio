using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WpfStudio.InspectionFixture;

/// <summary>Observes actual adorner drawings using WPF's public visual APIs.</summary>
internal sealed class LayoutFixture(string directory, params Button[] targets)
{
    private readonly Visual?[] _roots = new Visual?[targets.Length];

    public void Apply(string command)
    {
        if (!command.StartsWith("layout-", StringComparison.Ordinal)) return;
        if (command == "layout-configure")
            for (int index = 0; index < targets.Length; index++)
            {
                int captured = index;
                var target = targets[index];
                target.Dispatcher.Invoke(() =>
                {
                    _roots[captured] = PresentationSource.FromVisual(target)?.RootVisual;
                    target.Width = 130;
                    target.Height = 42;
                    target.Margin = new Thickness(10, 12, 14, 16);
                    target.HorizontalAlignment = HorizontalAlignment.Left;
                    target.RenderTransformOrigin = new Point(.5, .5);
                    target.RenderTransform = Transform.Identity;
                    target.Clip = new RectangleGeometry(new Rect(4, 3, 100, 30));
                    if (VisualTreeHelper.GetParent(target) is UIElement parent) parent.ClipToBounds = true;
                });
            }
        if (command.StartsWith("layout-rotate-", StringComparison.Ordinal))
        {
            int index = command[14..] switch { "main" => 0, "child" => 1, "popup" => 2, _ => -1 };
            if (index >= 0) targets[index].Dispatcher.Invoke(() => targets[index].RenderTransform = new RotateTransform(15));
        }
        if (command == "layout-remove-main")
            targets[0].Dispatcher.Invoke(() => ((Panel)VisualTreeHelper.GetParent(targets[0])).Children.Remove(targets[0]));
        WriteState();
    }

    private void WriteState()
    {
        var states = targets.Select((target, index) => target.Dispatcher.Invoke(() =>
        {
            var root = _roots[index] ?? PresentationSource.FromVisual(target)?.RootVisual;
            var adorners = new List<AdornmentState>();
            if (root is not null)
            {
                var stack = new Stack<DependencyObject>();
                stack.Push(root);
                int visited = 0;
                while (stack.Count > 0 && visited++ < 10_000)
                {
                    var node = stack.Pop();
                    if (node is Adorner adorner && adorner.GetType().FullName?.Contains("RunningInspector+InspectionAdorner", StringComparison.Ordinal) == true)
                    {
                        var transform = adorner.TransformToVisual(root);
                        var drawings = new List<PolygonState>();
                        ReadDrawings(VisualTreeHelper.GetDrawing(adorner), transform, drawings);
                        adorners.Add(new((adorner.AdornedElement as FrameworkElement)?.Name, adorner.IsHitTestVisible, drawings));
                    }
                    if (node is not Visual) continue;
                    for (int child = 0; child < VisualTreeHelper.GetChildrenCount(node); child++) stack.Push(VisualTreeHelper.GetChild(node, child));
                }
            }
            return new TargetState(target.Name, adorners);
        })).ToArray();
        var path = Path.Combine(directory, "layout-state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(states));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void ReadDrawings(Drawing? drawing, GeneralTransform transform, List<PolygonState> result)
    {
        if (drawing is DrawingGroup group)
        {
            var composed = new GeneralTransformGroup();
            composed.Children.Add(group.Transform ?? Transform.Identity);
            composed.Children.Add(transform);
            foreach (var child in group.Children) ReadDrawings(child, composed, result);
        }
        else if (drawing is GeometryDrawing { Geometry: { } shape } geometry && geometry.Pen?.Brush is SolidColorBrush brush)
        {
            var path = shape.GetFlattenedPathGeometry();
            foreach (var figure in path.Figures)
            {
                var points = new List<Point> { figure.StartPoint };
                foreach (var segment in figure.Segments)
                    if (segment is PolyLineSegment polyline) points.AddRange(polyline.Points);
                    else if (segment is LineSegment line) points.Add(line.Point);
                result.Add(new(brush.Color.ToString(), points.Select(point =>
                {
                    var mapped = transform.Transform(path.Transform?.Transform(point) ?? point);
                    return new PointState(mapped.X, mapped.Y);
                }).ToArray()));
            }
        }
    }

    private sealed record PointState(double X, double Y);
    private sealed record PolygonState(string Color, IReadOnlyList<PointState> Points);
    private sealed record AdornmentState(string? AdornedName, bool IsHitTestVisible, IReadOnlyList<PolygonState> Polygons);
    private sealed record TargetState(string Name, IReadOnlyList<AdornmentState> Adorners);
}
