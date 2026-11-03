using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.Designer;

/// <summary>An artboard size choice. <see cref="IsAuto"/> sizes the artboard from the root element.</summary>
public sealed record DesignerSizePreset(string Name, double Width = 0, double Height = 0, bool IsAuto = false, bool IsCustom = false)
{
    public string Description => IsAuto ? "Root size (Width/Height or d:DesignWidth/Height)" : IsCustom ? "Width and height from preview settings" : $"{Width:0} × {Height:0}";
    public override string ToString() => Name;
}

/// <summary>The artboard's size and its zoom in the canvas. Choosing a size is passed on through <c>sizeChanged</c>, because whether it renders at once is the designer's decision.</summary>
public sealed partial class PreviewCanvasViewModel(Func<string> documentName, Action sizeChanged) : ObservableObject
{
    private static readonly double[] ZoomSteps = [0.1, 0.25, 1d / 3, 0.5, 2d / 3, 0.75, 0.9, 1, 1.25, 1.5, 2, 3];
    public const double MinimumZoom = 0.1, MaximumZoom = 3;
    // Artboard margins, caption and frame around the rendered image in the canvas.
    private const double FitPaddingX = 72, FitPaddingY = 92;
    private bool _applyingFitZoom;
    private bool _applyingSizePreset;

    public static DesignerSizePreset AutoSizePreset { get; } = new("Auto", IsAuto: true);
    public static DesignerSizePreset CustomSizePreset { get; } = new("Custom", IsCustom: true);
    public IReadOnlyList<DesignerSizePreset> SizePresets { get; } =
    [
        AutoSizePreset,
        new("1920 × 1080", 1920, 1080), new("1600 × 900", 1600, 900), new("1366 × 768", 1366, 768),
        new("1280 × 720", 1280, 720), new("1024 × 768", 1024, 768), new("800 × 600", 800, 600),
        new("640 × 480", 640, 480), CustomSizePreset
    ];

    [ObservableProperty] public partial double PreviewWidth { get; set; } = 960;
    [ObservableProperty] public partial double PreviewHeight { get; set; } = 640;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ZoomText))] public partial double Zoom { get; set; } = 0.65;
    /// <summary>Uses the root element's own size for the artboard when it declares one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ArtboardTitle))] public partial bool AutoSize { get; set; } = true;
    [ObservableProperty] public partial DesignerSizePreset? SelectedSizePreset { get; set; } = AutoSizePreset;
    [ObservableProperty] public partial bool IsZoomToFit { get; set; } = true;
    [ObservableProperty] public partial double ViewportWidth { get; set; }
    [ObservableProperty] public partial double ViewportHeight { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ArtboardSizeText), nameof(ArtboardTitle))] public partial int ArtboardWidth { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ArtboardSizeText), nameof(ArtboardTitle))] public partial int ArtboardHeight { get; private set; }
    public string ArtboardSizeText => ArtboardWidth > 0 && ArtboardHeight > 0 ? $"{ArtboardWidth} × {ArtboardHeight}" : "";
    public string ArtboardTitle => documentName() + (ArtboardSizeText.Length == 0 ? "" : "  ·  " + ArtboardSizeText) + (AutoSize ? "" : "  ·  fixed size");
    public string ZoomText => Zoom.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>A frame of this size is on the canvas; zero clears it.</summary>
    internal void SetArtboard(int width, int height) { ArtboardWidth = width; ArtboardHeight = height; }
    internal void NotifyDocumentName() => OnPropertyChanged(nameof(ArtboardTitle));

    // ------------------------------------------------------------------ Zoom

    partial void OnZoomChanged(double value) { if (!_applyingFitZoom) IsZoomToFit = false; }
    partial void OnIsZoomToFitChanged(bool value) { if (value) ApplyFitZoom(); }
    partial void OnViewportWidthChanged(double value) { if (IsZoomToFit) ApplyFitZoom(); }
    partial void OnViewportHeightChanged(double value) { if (IsZoomToFit) ApplyFitZoom(); }

    /// <summary>Fits the artboard into the canvas viewport. Small artboards are not enlarged past 100%.</summary>
    public double FitZoom()
    {
        if (ArtboardWidth <= 0 || ArtboardHeight <= 0 || ViewportWidth <= FitPaddingX + 16 || ViewportHeight <= FitPaddingY + 16) return Zoom;
        double fit = Math.Min((ViewportWidth - FitPaddingX) / ArtboardWidth, (ViewportHeight - FitPaddingY) / ArtboardHeight);
        return Math.Clamp(Math.Min(fit, 1), MinimumZoom, MaximumZoom);
    }

    internal void ApplyFitZoom()
    {
        if (!IsZoomToFit) return;
        double fit = FitZoom();
        if (Math.Abs(fit - Zoom) < 0.0005) return;
        _applyingFitZoom = true;
        try { Zoom = fit; }
        finally { _applyingFitZoom = false; }
    }

    /// <summary>Returns the next zoom step in the given direction.</summary>
    public static double StepZoom(double current, int direction)
    {
        if (direction > 0) return ZoomSteps.FirstOrDefault(step => step > current + 0.001, MaximumZoom);
        return ZoomSteps.LastOrDefault(step => step < current - 0.001, MinimumZoom);
    }

    [RelayCommand] private void ZoomIn() => Zoom = StepZoom(Zoom, 1);
    [RelayCommand] private void ZoomOut() => Zoom = StepZoom(Zoom, -1);
    [RelayCommand] private void ZoomToActualSize() => Zoom = 1;
    [RelayCommand] private void ZoomToFit()
    {
        if (IsZoomToFit) ApplyFitZoom();
        else IsZoomToFit = true;
    }

    // ------------------------------------------------------------------ Artboard size

    partial void OnAutoSizeChanged(bool value)
    {
        if (_applyingSizePreset) return;
        _applyingSizePreset = true;
        try { SelectedSizePreset = value ? AutoSizePreset : MatchingPreset(); }
        finally { _applyingSizePreset = false; }
        sizeChanged();
    }
    partial void OnSelectedSizePresetChanged(DesignerSizePreset? value)
    {
        if (value is null || _applyingSizePreset) return;
        _applyingSizePreset = true;
        try
        {
            AutoSize = value.IsAuto;
            if (!value.IsAuto && !value.IsCustom) { PreviewWidth = value.Width; PreviewHeight = value.Height; }
        }
        finally { _applyingSizePreset = false; }
        sizeChanged();
    }
    partial void OnPreviewWidthChanged(double value) => ManualSizeChanged();
    partial void OnPreviewHeightChanged(double value) => ManualSizeChanged();
    private void ManualSizeChanged()
    {
        if (_applyingSizePreset) return;
        _applyingSizePreset = true;
        try { AutoSize = false; SelectedSizePreset = MatchingPreset(); }
        finally { _applyingSizePreset = false; }
        sizeChanged();
    }
    private DesignerSizePreset MatchingPreset() => SizePresets.FirstOrDefault(preset => !preset.IsAuto && !preset.IsCustom
        && preset.Width == PreviewWidth && preset.Height == PreviewHeight) ?? CustomSizePreset;
}
