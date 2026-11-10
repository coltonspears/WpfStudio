namespace WpfStudio.App.ViewModels;

/// <summary>One entry on Help > What's new.</summary>
public sealed record ReleaseNote(string Date, string Title, IReadOnlyList<string> Highlights);

/// <summary>What changed recently, newest first. Add an entry when a release-worthy change merges.</summary>
public static class ReleaseNoteCatalog
{
    public static IReadOnlyList<ReleaseNote> All { get; } =
    [
        new("Unreleased", "Design-time data in the XAML designer",
        [
            "d:DataContext works in the designer. {d:DesignInstance Type=vm:OrdersViewModel} fills the view model with sample values chosen from its property names and types, without running its constructor, so a view model that needs services still previews with lists, names, dates and totals. IsDesignTimeCreatable=True shows the instance its own constructor builds.",
            "CreateList, d:DesignData files, d:ItemsSource=\"{d:SampleData ItemCount=5}\" and any markup extension in a d: attribute (Binding, StaticResource, x:Static) are supported.",
            "A view that declares no DataContext is drawn with sample values from the view model its name pairs it with: OrdersView with OrdersViewModel, MainWindow with MainViewModel. Turn this off under Preview settings.",
            "The designer toolbar names the data a preview is drawn with, and its tooltip lists each design-time data source with its line. Declare writes a view model matched by name into the view as d:DataContext, after you review the change.",
            "A preview that fails because of a design-time value is shown without design-time values, with the reason as a warning.",
            "Completion, hover, F12 and binding diagnostics follow a d:DataContext declared with x:Static, StaticResource or Binding, as they already did for d:DesignInstance.",
        ]),
        new("Unreleased", "Tasks, debugger options and runtime checks",
        [
            "Tasks in Visual Studio Code's tasks.json format, from .vscode/tasks.json and .wpfstudio/tasks.json in the repository and tasks.json in your profile. Run them from View > Tasks, the command palette (Task: ...) or Build > Run build task. Each run gets its own terminal tab; dependencies run together or in order, and the $msCompile problem matcher puts compiler errors in Problems.",
            "Tasks > New task fills in a task with a form, tries it with Test run, and saves it to the repository or your profile after you review the change.",
            "Settings > Debugging turns Just My Code, stepping over properties and operators, and stopping at entry on or off for programs F5 starts. The Debugger pane lists loaded modules and whether their symbols loaded.",
            "Opening a solution checks that the runtimes its programs need and the SDK its global.json asks for are installed. A banner lists what is missing and installs it from Microsoft's official installer after checking its SHA-512 hash.",
        ]),
        new("Unreleased", "Quieter typing",
        [
            "Reference counts and Git history above declarations no longer flicker on every character.",
            "Editing a method body or a comment no longer re-analyzes other open files or the project's XAML, and only searches again for declarations whose names the edit used. On large solutions this is most of the work a keystroke caused.",
        ]),
        new("Unreleased", "A more useful completion list",
        [
            "The C# completion list shows a glyph for each entry's kind and its namespace or containing type, never cuts a name off, and describes the selected entry beside the list with its signature and documentation.",
        ]),
        new("Unreleased", "Faster solution opening",
        [
            "A solution whose project files changed opens from its saved project model in seconds. MSBuild corrects it in the background, and Output says when it is out of date.",
        ]),
        new("Unreleased", "Workbench essentials",
        [
            "File > Close solution closes every tab and unloads the solution, returning to the start page.",
            "Window menu: close all tabs, other tabs, saved tabs, or tabs to the right of the active one.",
            "Problems can be searched (prefix a term with - to exclude it), narrowed to errors, warnings or messages, and scoped to open files or the current file.",
            "Solution Explorer has a filter box that shows matching files and dependencies with their folders expanded.",
            "Solution Explorer nests code-behind under its XAML, Designer files under .resx, and partial files such as ShellViewModel.Tools.cs under their main file. Turn this off from the Solution Explorer toolbar.",
            "Help > About shows the version, runtime and data folder, with a button that copies them for bug reports.",
        ]),
        new("2026-10-28", "Editor polish and settings",
        [
            "Restyled code folding.",
            "Quick Info on hover for C# symbols.",
            "Rebuilt settings window.",
        ]),
        new("2026-10-28", "Faster startup and preview dependencies",
        [
            "Preview loads referenced controls and resources through the owning project's dependencies.",
            "Cached project model at startup, refreshed by MSBuild in the background.",
            "Git history above members, and a reload prompt for files changed outside WpfStudio.",
            "Support for legacy .NET Framework projects and their references.",
        ]),
        new("2026-10-26", "Code intelligence and Git",
        [
            "CodeLens reference counts for C# and XAML.",
            "Git change margin, blame and merge resolver, with Azure DevOps remotes.",
            "Debugger installer for netcoredbg.",
        ]),
        new("2026-10-07", "Memory profiler performance",
        [
            "Live snapshot loading went from 63 s to 6 s, with faster queries.",
        ]),
    ];
}
