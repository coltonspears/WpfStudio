using System.Runtime.InteropServices;

namespace WpfStudio.Workspace.Tests;

/// <summary>
/// The folder of WPF's reference assemblies for the .NET version the tests run on. The tests compile against the running
/// runtime's own assemblies with these added, so the Windows Desktop pack must be the same version: a newer one, which any
/// machine with a newer SDK has (GitHub's windows-latest has .NET 11's), references newer framework assemblies, and every
/// compilation then fails with CS1705.
/// </summary>
internal static class WindowsDesktopReferences
{
    public static string Directory() => Directory(
        Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")), "packs", "Microsoft.WindowsDesktop.App.Ref"),
        Environment.Version);

    /// <summary>The newest pack of <paramref name="runtime"/>'s major and minor version in <paramref name="packRoot"/>.</summary>
    public static string Directory(string packRoot, System.Version runtime)
    {
        // Folders are named by version, such as 10.0.5 or 11.0.0-rc.2.25502.107.
        var pack = System.IO.Directory.GetDirectories(packRoot)
            .Select(path => (Path: path, Version: System.Version.TryParse(Path.GetFileName(path).Split('-')[0], out var version) ? version : null))
            .Where(candidate => candidate.Version is { } version && version.Major == runtime.Major && version.Minor == runtime.Minor)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No Microsoft.WindowsDesktop.App.Ref {runtime.Major}.{runtime.Minor} pack is installed in {packRoot}; the tests run on .NET {runtime}.");
        return System.IO.Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(path => path, StringComparer.Ordinal).First();
    }
}
