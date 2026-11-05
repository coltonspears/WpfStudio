using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.XamlAnalysis;

/// <summary>What project XAML analysis needs from the shell: the workspace, the open editors, and hooks for the state it invalidates.</summary>
public interface IProjectXamlAnalysisHost
{
    WorkspaceSnapshot? Workspace { get; }
    /// <summary>True while a workspace is loading; analysis waits for the load to finish.</summary>
    bool IsLoading { get; }
    IReadOnlyList<EditorViewModel> Documents { get; }
    /// <summary>The WPF index's file texts, used to decide whether an index diagnostic still matches the file.</summary>
    IReadOnlyDictionary<string, string>? IndexTexts { get; }
    /// <summary>Analysis results or project state changed: the merged diagnostics list must be rebuilt.</summary>
    void DiagnosticsChanged();
    /// <summary>Reload-required or types-pending changed: open XAML editors must be told which project contexts they can use.</summary>
    void XamlContextsChanged();
    /// <summary>A project file, or the set of source files, changed on disk: the loaded project model no longer describes the workspace.</summary>
    void ProjectFilesChanged() { }
    /// <summary>XAML resources may have changed on disk or in the project.</summary>
    void InvalidateXamlResources();
    /// <summary>The set or text of open XAML documents changed, so resource overlays must follow.</summary>
    void OpenXamlDocumentsChanged();
    /// <summary>Another workspace was opened: index-derived state (WPF index, build diagnostics) belongs to the old one.</summary>
    void ForgetIndexedState();
    /// <summary>The language worker's semantic state changed; symbol-based operations must re-verify their context.</summary>
    void SemanticStateChanged();
    void AppendOutput(string line);
}
