using System.IO;

namespace WpfStudio.Runtime.Design;

/// <summary>Owns one directory created for one preview process, never a project output.</summary>
internal sealed class PreviewShadowDirectory
{
    private readonly SemaphoreSlim _cleanup = new(1, 1);
    public string Path { get; }
    public string Token { get; }

    private PreviewShadowDirectory(string path, string token) { Path = path; Token = token; }

    public static PreviewShadowDirectory Create()
    {
        string path = Directory.CreateTempSubdirectory("WpfStudio.Preview.").FullName;
        string token = Guid.NewGuid().ToString("N");
        File.WriteAllText(System.IO.Path.Combine(path, "owner.token"), token);
        return new PreviewShadowDirectory(path, token);
    }

    public async Task CleanupAsync()
    {
        await _cleanup.WaitAsync().ConfigureAwait(false);
        try
        {
            // Only the exact direct child of Temp created above is eligible. A
            // changed ownership marker or a replaced directory is left alone.
            string target = System.IO.Path.GetFullPath(Path);
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(System.IO.Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(target).StartsWith("WpfStudio.Preview.", StringComparison.Ordinal)) return;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
                    string marker = System.IO.Path.Combine(target, "owner.token");
                    if (!File.Exists(marker) || File.ReadAllText(marker) != Token) return;
                    DeleteContents(target, target, marker);
                    File.Delete(marker);
                    Directory.Delete(target);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 2) await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }
        finally { _cleanup.Release(); }
    }

    private static void DeleteContents(string directory, string root, string marker)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string path = System.IO.Path.GetFullPath(entry);
            if (!path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A shadow cleanup path escaped its owned directory.");
            if (string.Equals(path, marker, StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                // Delete links themselves without ever traversing their targets.
                if ((attributes & FileAttributes.ReparsePoint) == 0) DeleteContents(path, root, marker);
                Directory.Delete(path);
            }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }
    }
}
