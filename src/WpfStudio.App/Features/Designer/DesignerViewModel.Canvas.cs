using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>An artboard size choice. <see cref="IsAuto"/> sizes the artboard from the root element.</summary>
public sealed record DesignerSizePreset(string Name, double Width = 0, double Height = 0, bool IsAuto = false, bool IsCustom = false)
{
    public string Description => IsAuto ? "Root size (Width/Height or d:DesignWidth/Height)" : IsCustom ? "Width and height from preview settings" : $"{Width:0} × {Height:0}";
    public override string ToString() => Name;
}

/// <summary>
/// Canvas presentation: the last successful frame stays visible while a newer render is
/// pending or failed, so typing with live preview never blanks the artboard. The frame is
/// display-only; <see cref="Image"/>, picking and inspection still require a current render.
/// </summary>
public sealed partial class DesignerViewModel
{
    private static readonly double[] ZoomSteps = [0.1, 0.25, 1d / 3, 0.5, 2d / 3, 0.75, 0.9, 1, 1.25, 1.5, 2, 3];
    public const double MinimumZoom = 0.1, MaximumZoom = 3;
    // Artboard margins, caption and frame around the rendered image in the canvas.
    private const double FitPaddingX = 72, FitPaddingY = 92;
    private bool _applyingFitZoom;
    private bool _applyingSizePreset;
    private int _suppressSourceSync;

    public static DesignerSizePreset AutoSizePreset { get; } = new("Auto", IsAuto: true);
    public static DesignerSizePreset CustomSizePreset { get; } = new("Custom", IsCustom: true);
    public IReadOnlyList<DesignerSizePreset> SizePresets { get; } =
    [
        AutoSizePreset,
        new("1920 × 1080", 1920, 1080), new("1600 × 900", 1600, 900), new("1366 × 768", 1366, 768),
        new("1280 × 720", 1280, 720), new("1024 × 768", 1024, 768), new("800 × 600", 800, 600),
        new("640 × 480", 640, 480), CustomSizePreset
    ];

    /// <summary>The last successfully rendered frame, retained while a newer render is pending or failed.</summary>
    [ObservableProperty] public partial byte[]? DisplayImage { get; set; }
    [ObservableProperty] public partial string? RenderError { get; set; }
    [ObservableProperty] public partial int? RenderErrorLine { get; set; }
    [ObservableProperty] public partial int? RenderErrorColumn { get; set; }
    /// <summary>Uses the root element's own size for the artboard when it declares one.</summary>
    [ObservableProperty] public partial bool AutoSize { get; set; } = true;
    [ObservableProperty] public partial DesignerSizePreset? SelectedSizePreset { get; set; } = AutoSizePreset;
    [ObservableProperty] public partial bool IsZoomToFit { get; set; } = true;
    [ObservableProperty] public partial double CanvasViewportWidth { get; set; }
    [ObservableProperty] public partial double CanvasViewportHeight { get; set; }
    public event Action<SourceLocation>? SelectionSourceChanged;

    public int ArtboardWidth { get; private set; }
    public int ArtboardHeight { get; private set; }
    public bool HasCanvasContent => DisplayImage is not null;
    public bool HasRenderError => !string.IsNullOrEmpty(RenderError) && !IsCurrent;
    /// <summary>A previous frame is shown for a source revision that is not rendered yet.</summary>
    public bool IsStale => DisplayImage is not null && !IsCurrent;
    public bool ShowCanvasNotice => IsStale && !HasRenderError;
    public string CanvasNotice => IsBusy ? "Updating preview…"
        : IsCompiledPreview ? "Out of date · rebuild, then Refresh"
        : "Out of date · Refresh to update";
    public string EmptyCanvasTitle => _document is null ? "No XAML preview" : IsBusy ? "Rendering preview…" : HasRenderError ? "The preview could not render" : "Preview not rendered";
    public string EmptyCanvasText => _document is null ? "Open a .xaml file and choose Preview in the editor's context bar, or use View > XAML Designer."
        : IsBusy ? "The isolated WPF host is creating the view."
        : Status;
    public string ArtboardSizeText => ArtboardWidth > 0 && ArtboardHeight > 0 ? $"{ArtboardWidth} × {ArtboardHeight}" : "";
    public string ArtboardTitle => DocumentName + (ArtboardSizeText.Length == 0 ? "" : "  ·  " + ArtboardSizeText) + (AutoSize ? "" : "  ·  fixed size");
    public string ZoomText => Zoom.ToString("P0", CultureInfo.CurrentCulture);
    /// <summary>Snapshot nodes for canvas hover hints. Only a current frame can be hit tested.</summary>
    public IReadOnlyList<PreviewNode>? CanvasNodes => IsCurrent ? _snapshot?.Nodes : null;
    public string? SelectionLabel => SelectedNode is { } node ? node.TypeName + (node.ElementName is { } name ? " #" + name : "") : null;
    public int ErrorCount => Diagnostics.Count(d => d.Severity == "Error");
    public int WarningCount => Diagnostics.Count(d => d.Severity == "Warning");
    public string DiagnosticsSummary => Diagnostics.Count == 0 ? "" : ErrorCount > 0 ? ErrorCount.ToString(CultureInfo.CurrentCulture) : Diagnostics.Count.ToString(CultureInfo.CurrentCulture);

    private void UpdateCanvas(PreviewSnapshot snapshot)
    {
        if (snapshot.Success && snapshot.PngBytes is { Length: > 0 } bytes)
        {
            bool resized = ArtboardWidth != snapshot.PixelWidth || ArtboardHeight != snapshot.PixelHeight;
            ArtboardWidth = snapshot.PixelWidth; ArtboardHeight = snapshot.PixelHeight;
            DisplayImage = bytes;
            SetRenderError(null, null);
            if (resized || IsZoomToFit) ApplyFitZoom();
        }
        else if (!snapshot.Success)
        {
            var error = snapshot.Diagnostics.FirstOrDefault(d => d.Severity == "Error") ?? snapshot.Diagnostics.FirstOrDefault();
            SetRenderError(error?.Message ?? snapshot.Status ?? "The preview could not render.", error?.Line, error?.Column);
        }
        NotifyCanvasState();
    }

    private void SetRenderError(string? message, int? line, int? column = null)
    {
        RenderError = message; RenderErrorLine = line; RenderErrorColumn = column;
        GoToRenderErrorCommand.NotifyCanExecuteChanged();
        NotifyCanvasState();
    }

    private bool CanGoToRenderError() => RenderErrorLine is > 0 && _document is not null;
    [RelayCommand(CanExecute = nameof(CanGoToRenderError))]
    private void GoToRenderError()
    {
        if (RenderErrorLine is not { } line || line <= 0 || _document is null) return;
        SourceRequested?.Invoke(new(_document.Path, 0, 0, line, Math.Max(1, RenderErrorColumn ?? 1)));
    }

    private void ClearCanvas()
    {
        DisplayImage = null; ArtboardWidth = ArtboardHeight = 0;
        SetRenderError(null, null);
    }

    private void ResetCanvasForDocument()
    {
        ClearCanvas();
        ForgetRememberedSelection();
    }

    private void NotifyCanvasState()
    {
        OnPropertyChanged(nameof(HasCanvasContent)); OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(HasRenderError)); OnPropertyChanged(nameof(ShowCanvasNotice));
        OnPropertyChanged(nameof(CanvasNotice)); OnPropertyChanged(nameof(EmptyCanvasTitle)); OnPropertyChanged(nameof(EmptyCanvasText));
        OnPropertyChanged(nameof(ArtboardSizeText)); OnPropertyChanged(nameof(ArtboardTitle)); OnPropertyChanged(nameof(ArtboardWidth));
        OnPropertyChanged(nameof(ArtboardHeight)); OnPropertyChanged(nameof(CanvasNodes));
    }
    private void NotifyDiagnosticCounts()
    {
        OnPropertyChanged(nameof(ErrorCount)); OnPropertyChanged(nameof(WarningCount)); OnPropertyChanged(nameof(DiagnosticsSummary));
    }

    partial void OnDisplayImageChanged(byte[]? value) => NotifyCanvasState();
    partial void OnRenderErrorChanged(string? value) => OnPropertyChanged(nameof(HasRenderError));
    partial void OnStatusChanged(string value) { if (DisplayImage is null) OnPropertyChanged(nameof(EmptyCanvasText)); }
    partial void OnDocumentNameChanged(string value) => OnPropertyChanged(nameof(ArtboardTitle));

    // ------------------------------------------------------------------ Zoom

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        if (!_applyingFitZoom) IsZoomToFit = false;
    }
    partial void OnIsZoomToFitChanged(bool value) { if (value) ApplyFitZoom(); }
    partial void OnCanvasViewportWidthChanged(double value) { if (IsZoomToFit) ApplyFitZoom(); }
    partial void OnCanvasViewportHeightChanged(double value) { if (IsZoomToFit) ApplyFitZoom(); }

    /// <summary>Fits the artboard into the canvas viewport. Small artboards are not enlarged past 100%.</summary>
    public double FitZoom()
    {
        if (ArtboardWidth <= 0 || ArtboardHeight <= 0 || CanvasViewportWidth <= FitPaddingX + 16 || CanvasViewportHeight <= FitPaddingY + 16) return Zoom;
        double fit = Math.Min((CanvasViewportWidth - FitPaddingX) / ArtboardWidth, (CanvasViewportHeight - FitPaddingY) / ArtboardHeight);
        return Math.Clamp(Math.Min(fit, 1), MinimumZoom, MaximumZoom);
    }

    private void ApplyFitZoom()
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
        OnPropertyChanged(nameof(ArtboardTitle));
        if (_applyingSizePreset) return;
        _applyingSizePreset = true;
        try { SelectedSizePreset = value ? AutoSizePreset : MatchingPreset(); }
        finally { _applyingSizePreset = false; }
        ArtboardSizeChanged();
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
        ArtboardSizeChanged();
    }
    partial void OnPreviewWidthChanged(double value) => ManualSizeChanged();
    partial void OnPreviewHeightChanged(double value) => ManualSizeChanged();
    private void ManualSizeChanged()
    {
        if (_applyingSizePreset) return;
        _applyingSizePreset = true;
        try { AutoSize = false; SelectedSizePreset = MatchingPreset(); }
        finally { _applyingSizePreset = false; }
        ArtboardSizeChanged();
    }
    private DesignerSizePreset MatchingPreset() => SizePresets.FirstOrDefault(preset => !preset.IsAuto && !preset.IsCustom
        && preset.Width == PreviewWidth && preset.Height == PreviewHeight) ?? CustomSizePreset;

    /// <summary>A size choice applies immediately when live preview may render without further confirmation.</summary>
    private void ArtboardSizeChanged()
    {
        if (_document is null || _disposed) return;
        if (AutoRefresh && !IsCompiledPreview && !_scenarioSelectionPending) { _ = RenderAsync(debounce: true); return; }
        Invalidate();
        Status = "Artboard size changed. Refresh to apply it.";
    }

    // ------------------------------------------------------------------ Editor selection sync

    /// <summary>Asks the shell to highlight the selected element's markup when the user chose it in the designer.</summary>
    private void RevealSelectionSource(DesignerNode? node)
    {
        if (_suppressSourceSync > 0 || node?.Node.Source is not { } source || !IsCurrent || _document is not { } document
            || document.Version != _sourceVersion || !string.Equals(source.Path, document.Path, StringComparison.OrdinalIgnoreCase)) return;
        SelectionSourceChanged?.Invoke(source);
    }
}
