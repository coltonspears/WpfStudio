using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// Canvas presentation: the last successful frame stays visible while a newer render is
/// pending or failed, so typing with live preview never blanks the artboard. The frame is
/// display-only; <see cref="Image"/>, picking and inspection still require a current render.
/// </summary>
public sealed partial class DesignerViewModel
{
    private int _suppressSourceSync;

    /// <summary>The last successfully rendered frame, retained while a newer render is pending or failed.</summary>
    [ObservableProperty] public partial byte[]? DisplayImage { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRenderError))] public partial string? RenderError { get; set; }
    [ObservableProperty] public partial int? RenderErrorLine { get; set; }
    [ObservableProperty] public partial int? RenderErrorColumn { get; set; }

    public PreviewCanvasViewModel Canvas { get; }
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
            Canvas.SetArtboard(snapshot.PixelWidth, snapshot.PixelHeight);
            DisplayImage = bytes;
            SetRenderError(null, null);
            Canvas.ApplyFitZoom();
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
        DisplayImage = null; Canvas.SetArtboard(0, 0);
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
        OnPropertyChanged(nameof(CanvasNodes));
    }
    private void NotifyDiagnosticCounts()
    {
        OnPropertyChanged(nameof(ErrorCount)); OnPropertyChanged(nameof(WarningCount)); OnPropertyChanged(nameof(DiagnosticsSummary));
    }

    partial void OnDisplayImageChanged(byte[]? value) => NotifyCanvasState();
    partial void OnStatusChanged(string value) { _statusShowsSnapshotUpdate = false; if (DisplayImage is null) OnPropertyChanged(nameof(EmptyCanvasText)); }

    /// <summary>A size choice applies immediately when live preview may render without further confirmation.</summary>
    private void ArtboardSizeChanged()
    {
        if (_document is null || _disposed) return;
        if (AutoRefresh && !IsCompiledPreview && !Scenarios.SelectionPending) { RenderAsync(debounce: true).Forget(ReportFault); return; }
        Invalidate();
        Status = "Artboard size changed. Refresh to apply it.";
    }

    /// <summary>Asks the shell to highlight the selected element's markup when the user chose it in the designer.</summary>
    private void RevealSelectionSource(DesignerNode? node)
    {
        if (_suppressSourceSync > 0 || node?.Node.Source is not { } source || !IsCurrent || _document is not { } document
            || document.Version != _sourceVersion || !string.Equals(source.Path, document.Path, StringComparison.OrdinalIgnoreCase)) return;
        _sourceHost?.RevealSelection(source);
    }
}
