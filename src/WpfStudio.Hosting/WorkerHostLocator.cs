using System.IO;

namespace WpfStudio.Hosting;

/// <summary>Finds a worker's executable: packaged beside the application, or under a source checkout for development and test runs.</summary>
public static class WorkerHostLocator
{
    /// <param name="name">Project and file name without extension, such as WpfStudio.PreviewHost.</param>
    /// <param name="packagedFolder">The folder a package keeps it in, under the application.</param>
    /// <param name="configurations">Build configurations to accept from a source checkout, in order of preference.</param>
    /// <param name="extensions">File extensions to accept, in order of preference. The first names the file reported when none is found.</param>
    /// <param name="companions">Files that must sit beside a candidate. A partial copy without them is skipped.</param>
    /// <returns>The first complete candidate, or the packaged path so the caller can report what is missing.</returns>
    public static string Find(string name, string packagedFolder, string targetFramework, IEnumerable<string> configurations,
        IReadOnlyList<string> extensions, IReadOnlyList<string> companions, string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var accepted = configurations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var folder in Folders(root, name, packagedFolder, targetFramework, accepted))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate) && companions.All(companion => File.Exists(Path.Combine(folder, companion)))) return candidate;
            }
        return Path.Combine(root, packagedFolder, name + extensions[0]);
    }

    private static IEnumerable<string> Folders(string root, string name, string packagedFolder, string targetFramework, string[] configurations)
    {
        yield return Path.Combine(root, packagedFolder);
        yield return root;
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            foreach (var configuration in configurations)
                yield return Path.Combine(directory.FullName, "src", name, "bin", configuration, targetFramework);
    }
}
