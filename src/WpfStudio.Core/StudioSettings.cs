namespace WpfStudio.Core;

public sealed record StudioSettings
{
    public List<string> RecentWorkspaces { get; init; } = [];
    public List<string> OpenDocuments { get; init; } = [];
    public string? WorkspacePath { get; init; }
    public string Theme { get; init; } = "Dark";
    public string Configuration { get; init; } = "Debug";
    public string? StartupProject { get; init; }
    public bool ShowInlayHints { get; init; } = true;
    public bool ShowInlineBlame { get; init; } = true;
    public bool ShowCodeLens { get; init; } = true;
    /// <summary>Load files changed outside WpfStudio without asking when the editor has no unsaved edits to them.</summary>
    public bool AutoReloadUnchangedFiles { get; init; }
    public bool ShowQuickInfo { get; init; } = true;
    /// <summary>Solution Explorer nests code-behind under its XAML and partial files under their main file.</summary>
    public bool NestRelatedFiles { get; init; } = true;
    public EditorSettings Editor { get; init; } = new();
}
