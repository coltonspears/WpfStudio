namespace WpfStudio.App.ViewModels;

/// <summary>One entry on Help > What's new.</summary>
public sealed record ReleaseNote(string Date, string Title, IReadOnlyList<string> Highlights);

/// <summary>What changed recently, newest first. Add an entry when a release-worthy change merges.</summary>
public static class ReleaseNoteCatalog
{
    public static IReadOnlyList<ReleaseNote> All { get; } =
    [
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
