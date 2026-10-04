using System.Windows;
using System.Windows.Media;

namespace WpfStudio.App.Features.Profiling.Visuals;

/// <summary>Theme-aware colours for the profiler's custom-drawn visuals. Categorical hues come from the
/// Chart1..Chart8 brushes in fixed order; everything else uses the graphite tokens.</summary>
internal static class ChartPalette
{
    public static Color Resource(FrameworkElement owner, string key, Color fallback) =>
        owner.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    public static Brush Brush(FrameworkElement owner, string key) =>
        owner.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Categorical slot colour. A negative index is the neutral "Other" bucket.</summary>
    public static Color Series(FrameworkElement owner, int index) => index < 0
        ? Resource(owner, "SubtleBrush", Colors.Gray)
        : Resource(owner, $"Chart{index % 8 + 1}Brush", Colors.SteelBlue);

    public static Color Mix(Color a, Color b, double amountOfB)
    {
        var t = Math.Clamp(amountOfB, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }

    public static Pen Pen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze(); return pen;
    }

    /// <summary>White or near-black text, whichever contrasts more with the fill.</summary>
    public static Color ReadableText(Color fill)
    {
        static double Channel(byte c) { var v = c / 255d; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Channel(fill.R) + 0.7152 * Channel(fill.G) + 0.0722 * Channel(fill.B);
        var white = 1.05 / (luminance + 0.05); var black = (luminance + 0.05) / (0.012 + 0.05);
        return white >= black ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x16, 0x17, 0x1A);
    }

    public static FormattedText Text(Visual owner, string text, double size, Color color, double maxWidth, bool bold = false, bool code = false)
    {
        var face = code ? new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
            : new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size,
            Frozen(color), VisualTreeHelper.GetDpi(owner).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        return formatted;
    }
}
