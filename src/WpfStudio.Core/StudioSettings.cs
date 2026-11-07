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
    public CodeAssistSettings CodeAssist { get; init; } = new();
    public DebuggingSettings Debugging { get; init; } = new();
}

/// <summary>How the debugger treats programs WpfStudio launches. The defaults are the adapter's own.</summary>
public sealed record DebuggingSettings
{
    /// <summary>Step through and stop in the solution's own code only, not in framework or package code.</summary>
    public bool JustMyCode { get; init; } = true;
    /// <summary>Step over properties and operators instead of into them.</summary>
    public bool StepFiltering { get; init; } = true;
    /// <summary>Pause on the program's first line.</summary>
    public bool StopAtEntry { get; init; }
}
