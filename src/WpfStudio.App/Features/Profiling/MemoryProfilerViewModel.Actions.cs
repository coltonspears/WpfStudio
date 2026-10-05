using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Cross-view actions behind the right-click menus and command palette: open an object in a specific tab or
/// view, open a type at its retention paths, and copy text.</summary>
public sealed partial class MemoryProfilerViewModel
{
    /// <summary>Opens an object in the browser and shows why it is alive.</summary>
    [RelayCommand] private void ShowWhyAlive(int objectId) { ShowObject(objectId); InspectorTab = RootsTab; }

    /// <summary>Opens an object in the browser and shows what it keeps alive.</summary>
    [RelayCommand] private void ShowKeepsAlive(int objectId) { ShowObject(objectId); InspectorTab = KeepsAliveTab; }

    [RelayCommand] private void ShowInGraph(int objectId) { ShowObject(objectId); SelectedView = GraphView; }

    [RelayCommand] private void ShowInBrowser(int objectId) => ShowObject(objectId);

    /// <summary>Opens a type at its retention-path Sankey.</summary>
    [RelayCommand]
    private void ShowRetentionPaths(string? typeKey)
    {
        if (typeKey is null) return;
        OpenType(typeKey); TypeDetailTab = 0;
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); Status = text.Length <= 80 ? $"Copied {text}." : "Copied to the clipboard."; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    /// <summary>True when the type exists in the current snapshot (types that only exist in the baseline cannot be opened).</summary>
    public bool HasType(string? typeKey) => typeKey is not null && Summary?.Types.Any(t => t.Key == typeKey) == true;
    public string? TypeNameOf(string typeKey) => Summary?.Types.FirstOrDefault(t => t.Key == typeKey)?.Name ?? _baseline?.Types.FirstOrDefault(t => t.Key == typeKey)?.Name;

    /// <summary>Focus the go-to box: raised for the command palette's "Go to address" entry.</summary>
    public event Action? FocusGoToRequested;
    [RelayCommand] private void FocusGoTo() { if (HasCapture) FocusGoToRequested?.Invoke(); }
}
