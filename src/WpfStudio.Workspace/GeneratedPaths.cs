using System.Collections.Concurrent;

namespace WpfStudio.Workspace;

/// <summary>
/// Tells a build's intermediate output from source that merely lives under a folder named obj. "Has /obj/ somewhere in its path"
/// alone took a whole workspace cloned under <c>D:\obj\</c>, or a source folder such as <c>Models\Obj</c>, for compiler output, and such
/// files get no diagnostics, completion, references or rename.
/// </summary>
internal static class GeneratedPaths
{
    // A folder that is not an intermediate directory now can become one at the next restore, so that answer is asked for again.
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (bool Intermediate, long Checked)> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProjectFiles = ["*.csproj", "*.vbproj", "*.fsproj", "*.proj"];

    /// <summary>True when the path lies in the intermediate directory of a build.</summary>
    public static bool IsIntermediate(string path)
    {
        var normalized = path.Replace('\\', '/');
        for (int at = normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase); at >= 0;
             at = normalized.IndexOf("/obj/", at + 4, StringComparison.OrdinalIgnoreCase))
            if (IsIntermediateDirectory(normalized[..(at + 4)])) return true;
        return false;
    }

    private static bool IsIntermediateDirectory(string directory)
    {
        long now = Environment.TickCount64;
        if (Known.TryGetValue(directory, out var known) && (known.Intermediate || now - known.Checked < Recheck.TotalMilliseconds)) return known.Intermediate;
        bool intermediate = Look(directory);
        Known[directory] = (intermediate, now);
        return intermediate;
    }

    private static bool Look(string directory)
    {
        try
        {
            // Nothing on disk to judge by, as for a file that has been removed: the name is all there is.
            if (!Directory.Exists(directory)) return true;
            // The folder a restore writes to: beside the project, or one per project under a shared artifacts folder.
            if (Directory.EnumerateFiles(directory, "project.assets.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true }).Any()) return true;
            // A project that has not been restored yet, or that does not use restore.
            return Path.GetDirectoryName(directory) is { } parent && ProjectFiles.Any(pattern => Directory.EnumerateFiles(parent, pattern).Any());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }
}
