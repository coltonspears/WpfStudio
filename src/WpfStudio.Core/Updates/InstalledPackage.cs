using System.Text.Json;

namespace WpfStudio.Core.Updates;

/// <summary>
/// What <c>tools/Publish.ps1</c> writes beside <c>WpfStudio.exe</c> in a published package: which kind of package it is,
/// so an update downloads the same kind, and which repository publishes its releases. A build from source has no marker
/// and is never replaced in place.
/// </summary>
public sealed record InstalledPackage(string Directory, string Kind, string Repository)
{
    public const string MarkerFileName = "WpfStudio.package.json";
    public const string ExecutableName = "WpfStudio.exe";

    /// <summary>The release asset with this kind of package for a version, such as <c>WpfStudio-0.4.2-win-x64.zip</c>.</summary>
    public string AssetName(ReleaseVersion version) => $"WpfStudio-{version}-{Kind}.zip";

    /// <summary>The marker in a directory. Null without one; <paramref name="problem"/> says why when one is there but unusable.</summary>
    public static InstalledPackage? TryRead(string directory, out string? problem)
    {
        problem = null;
        var path = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(path)) return null;
        try
        {
            // As text, which drops a byte order mark: Windows PowerShell 5.1 writes one, and JsonDocument refuses it in bytes.
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var kind = Text(root, "package");
            var repository = Text(root, "repository");
            // The kind becomes part of a file name to download, so it is held to the names Publish.ps1 writes.
            if (kind.Length == 0 || !kind.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) { problem = $"{MarkerFileName} names no package kind WpfStudio knows."; return null; }
            if (!ReleaseFeed.IsRepositoryName(repository)) { problem = $"{MarkerFileName} names no GitHub repository."; return null; }
            return new InstalledPackage(directory, kind, repository);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"{MarkerFileName} could not be read: {exception.Message}";
            return null;
        }
    }

    /// <summary>Whether this Windows account may create files in the package's folder, which replacing it needs.</summary>
    public bool CanWrite()
    {
        try
        {
            using var probe = new FileStream(Path.Combine(Directory, ".write-test-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString()!.Trim() : "";
}
