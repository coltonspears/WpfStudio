using System.IO;
using WpfStudio.Contracts;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

/// <summary>Validates a request's project assembly and copies its output into the client-owned temporary directory, so loaded code never locks the project's files.</summary>
internal static class PreviewOutputStage
{
    /// <summary>The full path of the request's project assembly, once it is known to be a local file inside the selected project.</summary>
    public static string ValidateAssemblyPath(PreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectDirectory)) throw new InvalidOperationException("A project directory is required to load a project assembly.");
        string root = Path.GetFullPath(request.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(request.AssemblyPath!);
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !IsAssemblyFile(path))
            throw new InvalidOperationException(AssemblyRequirement);
        if (!File.Exists(path)) throw new InvalidOperationException($"{Path.GetFileName(path)} has not been built at {Path.GetDirectoryName(path)}. Build the project, then refresh the preview.");
        return path;
    }

    /// <summary>Where the output of <paramref name="assemblyPath"/> belongs, after checking that this client owns the temporary directory.</summary>
    public static string OutputDirectory(string assemblyPath, string? shadowDirectory, string? shadowToken)
    {
        string shadowRoot = ValidateShadowDirectory(shadowDirectory, shadowToken);
        string sourceDirectory = Path.GetDirectoryName(assemblyPath)!;
        if (shadowRoot.StartsWith(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The project output directory cannot contain the preview's temporary directory.");
        return Path.Combine(shadowRoot, "output");
    }

    public static string ValidateShadowDirectory(string? shadowDirectory, string? shadowToken)
    {
        if (string.IsNullOrWhiteSpace(shadowDirectory) || string.IsNullOrWhiteSpace(shadowToken))
            throw new InvalidOperationException("Project assembly previews require a client-owned shadow directory.");
        string path = Path.GetFullPath(shadowDirectory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("WpfStudio.Preview.", StringComparison.Ordinal) ||
            !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(Path.Combine(path, "owner.token")) != shadowToken)
            throw new InvalidOperationException("The preview shadow directory is not owned by this client.");
        return path;
    }

    public static void CopyOutputTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A project output dependency is a filesystem link. Copy it into the build output before previewing.");
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A project output dependency is a filesystem link. Copy it into the build output before previewing.");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyOutputTree(entry, target);
            else File.Copy(entry, target, overwrite: true);
        }
    }

    /// <summary>Copies the catalogued references the build left out of its output. Returns those that exist nowhere, by assembly name.</summary>
    public static Dictionary<string, string> StageReferences(PreviewRequest request, string directory)
    {
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in PreviewDependencyCatalog.Read(request).Assets)
        {
            string destination = PreviewDependencyCatalog.Within(directory, asset.RelativePath);
            if (File.Exists(destination)) continue;
            if (!File.Exists(asset.Source))
            {
                missing[Path.GetFileNameWithoutExtension(asset.RelativePath)] = asset.Source;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset.Source, destination);
        }
        return missing;
    }

#if NETFRAMEWORK
    // A .NET Framework application's build output is its .exe; there is no separate managed .dll beside an apphost.
    private const string AssemblyRequirement = "The preview assembly must be a DLL or EXE inside the selected local project directory.";
    private static bool IsAssemblyFile(string path) =>
        string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
#else
    // A missing .exe is an unbuilt .NET Framework application, chosen before anything could tell it runs on that host.
    private const string AssemblyRequirement = "The preview assembly must be a DLL inside the selected local project directory.";
    private static bool IsAssemblyFile(string path) =>
        string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
#endif
}
