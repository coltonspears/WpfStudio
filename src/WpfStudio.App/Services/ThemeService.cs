using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.App.Services;

/// <summary>The current theme: recolours the application's brushes, its window frames and the syntax colours.</summary>
public interface IThemeService
{
    Palette Palette { get; }
    /// <summary>Raised after brushes and frames have been updated, so views can refresh non-brush theme state.</summary>
    event Action<Palette>? Applied;
    void Apply(string name);
    /// <summary>Colours a window's frame with the current palette once it has a handle.</summary>
    void Track(Window window);
}

/// <summary>Application palette and dynamic syntax brushes shared by source and SQL editors.</summary>
public sealed class ThemeService : IThemeService
{
    private bool _syntaxConfigured;

    public Palette Palette { get; private set; } = Palette.Dark;
    public event Action<Palette>? Applied;

    public void Track(Window window)
    {
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero) WindowChromeService.ApplyFrame(window, Palette);
        else window.SourceInitialized += (_, _) => WindowChromeService.ApplyFrame(window, Palette);
    }

    public void Apply(string name)
    {
        Palette = Palette.For(name);
        if (Application.Current is { } app) ApplyResources(app);
        Applied?.Invoke(Palette);
    }

    private void ApplyResources(Application app)
    {
        foreach (var (key, color) in Palette.Colors)
        {
            if (app.TryFindResource(key) is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
            else app.Resources[key] = new SolidColorBrush(color);
        }
        app.Resources[SystemColors.ControlBrushKey] = app.FindResource("SurfaceBrush");
        app.Resources[SystemColors.ControlTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.WindowBrushKey] = app.FindResource("EditorBrush");
        app.Resources[SystemColors.WindowTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.MenuBrushKey] = app.FindResource("SurfaceBrush");
        app.Resources[SystemColors.MenuTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.HighlightBrushKey] = app.FindResource("SelectionBrush");
        app.Resources[SystemColors.HighlightTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = app.FindResource("InactiveSelectionBrush");
        app.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.GrayTextBrushKey] = app.FindResource("SubtleBrush");
        if (!_syntaxConfigured)
        {
            foreach (var language in new[] { "C#", "XML", "JavaScript" })
            {
                var definition = HighlightingManager.Instance.GetDefinition(language);
                if (definition is null) continue;
                foreach (var color in definition.NamedHighlightingColors.Where(c => !c.IsFrozen))
                {
                    var label = (color.Name ?? "").ToLowerInvariant();
                    var key = label.Contains("comment") ? "SyntaxComment" : label.Contains("string") || label.Contains("char") || label.Contains("value") ? "SyntaxString" : label.Contains("number") || label.Contains("digit") ? "SyntaxNumber" : label.Contains("type") || label.Contains("tag") ? "SyntaxType" : label.Contains("attribute") ? "SyntaxAttribute" : "SyntaxKeyword";
                    color.Foreground = new ResourceHighlightingBrush(key);
                }
            }
            _syntaxConfigured = true;
        }
        app.Resources[Palette.ResourceKey] = Palette;
        foreach (Window window in app.Windows) { window.InvalidateVisual(); WindowChromeService.ApplyFrame(window, Palette); }
    }
    private sealed class ResourceHighlightingBrush(string key) : HighlightingBrush
    {
        public override Brush GetBrush(ITextRunConstructionContext context) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.SteelBlue;
    }
}
