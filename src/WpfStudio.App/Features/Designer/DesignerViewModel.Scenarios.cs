using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel : IScenarioCatalogHost
{
    public ScenarioCatalogViewModel Scenarios { get; }
    [ObservableProperty] public partial bool UseDesignTimeValues { get; set; } = true;
    public string ScenarioDescription => IsCurrent
        ? _snapshot?.Scenario is { } applied
            ? $"Active scenario: {applied.Configuration.Name} · {Path.GetFileName(applied.AssemblyPath)} · build {applied.AssemblySha256[..Math.Min(12, applied.AssemblySha256.Length)]}"
            : IsCompiledPreview || !UseDesignTimeValues ? "Active scenario: Default · view data"
            : "Active scenario: Default · view data and supported design-time declarations"
        : $"Selected scenario: {Scenarios.Selected?.Name ?? "Default"} · refresh to apply";

    partial void OnUseDesignTimeValuesChanged(bool value)
    {
        if (!IsCompiledPreview) { Scenarios.SelectionPending = true; Invalidate(); Status = "Design-time values changed. Refresh the source preview."; }
    }

    string? IScenarioCatalogHost.DocumentPath => _document?.Path;
    long IScenarioCatalogHost.Revision => _revision;
    bool IScenarioCatalogHost.IsDisposed => _disposed;
    CancellationToken IScenarioCatalogHost.Lifetime => _lifetime.Token;
    void IScenarioCatalogHost.InvalidatePreview() => Invalidate();
    void IScenarioCatalogHost.ScenarioChosen(DesignerScenario? scenario)
    {
        Invalidate();
        Status = scenario?.Configuration?.ViewFactory is not null && !IsCompiledPreview
            ? "This scenario creates a view. Select Compiled mode, then Refresh."
            : "Scenario selected. Refresh to apply it in the isolated preview.";
    }
    void IScenarioCatalogHost.ScenariosChanged()
    {
        OnPropertyChanged(nameof(UsesApplicationResources));
        OnPropertyChanged(nameof(ScenarioDescription));
    }
    void IScenarioCatalogHost.ReportStatus(string status) => Status = status;
    void IScenarioCatalogHost.ReportFault(Exception fault) => ReportFault(fault);
    void IScenarioCatalogHost.OpenSource(SourceLocation location) => SourceRequested?.Invoke(location);
}
