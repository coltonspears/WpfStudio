namespace WpfStudio.Core.IO;

/// <summary>Validates and resolves relative file paths so they cannot escape a repository root.</summary>
public static class RepositoryPath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="repositoryRoot"/> when it is a safe relative path
    /// contained within the repository; returns false for null/empty, rooted paths, or <c>..</c> escapes.
    /// </summary>
    public static bool TryResolveContained(string repositoryRoot, string? relativePath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\')
            || (relativePath.Length >= 2 && relativePath[1] == ':' && char.IsAsciiLetter(relativePath[0])))
            return false;
        try
        {
            var fullRoot = Path.GetFullPath(repositoryRoot);
            var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            var normalizedRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalizedRelative));
            if (!candidate.StartsWith(rootPrefix, PathComparison))
                return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Resolves <paramref name="relativePath"/> under <paramref name="repositoryRoot"/> or throws <see cref="InvalidOperationException"/>.</summary>
    public static string ResolveContained(string repositoryRoot, string relativePath) =>
        TryResolveContained(repositoryRoot, relativePath, out var fullPath)
            ? fullPath
            : throw new InvalidOperationException("The selected file is outside this Git repository.");
}
