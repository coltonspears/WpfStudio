using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>What the scenario catalog needs from the designer whose preview it configures.</summary>
public interface IScenarioCatalogHost
{
    /// <summary>The open document; scenarios apply to it. Null when none is open.</summary>
    string? DocumentPath { get; }
    /// <summary>The newest render's revision; a catalog read under an older one is stale.</summary>
    long Revision { get; }
    bool IsDisposed { get; }
    CancellationToken Lifetime { get; }
    /// <summary>The saved configuration or the selection no longer matches the preview.</summary>
    void InvalidatePreview();
    /// <summary>The user chose another scenario.</summary>
    void ScenarioChosen(DesignerScenario? scenario);
    /// <summary>The list or the selection changed, however it came to.</summary>
    void ScenariosChanged();
    void ReportStatus(string status);
    /// <summary>For a check nobody awaits. Safe on any thread.</summary>
    void ReportFault(Exception fault);
    void OpenSource(SourceLocation location);
}
