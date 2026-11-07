namespace WpfStudio.App.Features.Workbench;

/// <summary>
/// The identity of every dock pane and document workbench. Saved layout files store these values as content ids and
/// MainWindow.xaml passes them as strings, so a value must never change: renaming one loses the pane from every saved layout.
/// </summary>
public static class Workbenches
{
    public const string Explorer = "Explorer";
    public const string FileGroups = "FileGroups";
    public const string WpfTools = "WpfTools";
    public const string Assistant = "ColtonGPT";
    public const string Output = "Output";
    public const string Problems = "Problems";
    public const string TaskList = "TaskList";
    public const string Tasks = "Tasks";
    public const string Search = "Search";
    public const string Terminal = "Terminal";
    public const string Debugger = "Debugger";
    public const string Database = "Database";
    public const string Designer = "Designer";
    public const string LiveInspection = "LiveInspection";
    public const string Packages = "Packages";
    public const string Git = "Git";
    public const string Profiler = "Profiler";
    public const string Welcome = "Welcome";

    public static IReadOnlyList<string> All { get; } =
        [Explorer, FileGroups, WpfTools, Assistant, Output, Problems, TaskList, Tasks, Search, Terminal, Debugger, Database, Designer, LiveInspection, Packages, Git, Profiler, Welcome];

    /// <summary>Workbenches that open as closable document tabs rather than tool panes.</summary>
    public static bool IsDocument(string? id) => id is Packages or Git or Profiler;

    /// <summary>Resolves the older names a tool can be requested by.</summary>
    public static string Canonical(string id) => id switch
    {
        "WPF" or "Resources" => WpfTools,
        "SQL Server" => Database,
        _ => id
    };
}
