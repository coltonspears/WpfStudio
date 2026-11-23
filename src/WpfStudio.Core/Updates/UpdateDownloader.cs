using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WpfStudio.Core.Updates;

/// <summary>A downloaded, verified and extracted package, ready to replace the running one.</summary>
public sealed record StagedUpdate(ReleaseVersion Version, string Directory)
{
    public string Executable => Path.Combine(Directory, InstalledPackage.ExecutableName);
}

/// <summary>A line for the person waiting, and how far along the work is from 0 to 1 when that is known.</summary>
public sealed record UpdateProgress(string Message, double? Fraction = null);

/// <summary>
/// Downloads a release's package into <c>updates/&lt;version&gt;</c> under the data folder, refuses it unless its SHA-256
/// hash matches the one GitHub lists for it (or the release's <c>SHA256SUMS.txt</c>), and extracts it there. Nothing in
/// the running package changes: <see cref="UpdateApplier"/> does that once WpfStudio has closed.
/// </summary>
/// <remarks>
/// Every WpfStudio window shares the folder. While a window downloads an update or holds one ready, it keeps
/// <c>.in-use</c> in that update's folder open for itself alone, so no other window removes or replaces it. Windows
/// releases the file when the process ends, however it ends.
/// </remarks>
public sealed class UpdateDownloader(HttpClient http, string updatesDirectory) : IDisposable
{
    public const string InUseFileName = ".in-use";
    private FileStream? _hold;

    /// <summary>A package is a few hundred megabytes; anything far larger is not one.</summary>
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    public const string ChecksumFileName = "SHA256SUMS.txt";
    private const long MaximumChecksumFileBytes = 64 * 1024;

    public string UpdatesDirectory { get; } = updatesDirectory;

    public async Task<StagedUpdate> DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string packageKind, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (asset.Name.Length == 0 || Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UpdateException($"The release file '{asset.Name}' does not have a plain file name.");
        if (asset.Size > MaximumPackageBytes) throw new UpdateException($"{asset.Name} is larger than any WpfStudio package should be, so it was not downloaded.");
        var expected = asset.Sha256 ?? await ReadPublishedChecksumAsync(release, asset.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new UpdateException($"The release lists no SHA-256 hash for {asset.Name}, so it cannot be checked and was not downloaded.");

        var folder = Path.Combine(UpdatesDirectory, release.Version.ToString());
        Release(); // This window's earlier update, if any, is replaced by this one.
        if (IsInUse(folder)) throw new UpdateException($"Another WpfStudio window is downloading WpfStudio {release.Version} or holds it ready to install. Install it from there, or close that window and try again.");
        DeleteDirectory(folder); // What an earlier, interrupted attempt left.
        Directory.CreateDirectory(folder);
        _hold = Hold(folder) ?? throw new UpdateException($"Another WpfStudio window is downloading WpfStudio {release.Version}. Install it from there, or close that window and try again.");
        var archive = Path.Combine(folder, asset.Name + ".download");
        try
        {
            await DownloadVerifiedAsync(asset, expected, archive, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateProgress("Unpacking…"));
            var extracted = Path.Combine(folder, "package");
            try { await ZipFile.ExtractToDirectoryAsync(archive, extracted, cancellationToken).ConfigureAwait(false); } // Refuses entries that would land outside the folder.
            catch (InvalidDataException exception) { throw new UpdateException($"{asset.Name} is not a readable zip file.", exception); }
            File.Delete(archive);
            var root = FindPackageRoot(extracted) ?? throw new UpdateException($"{asset.Name} holds no {InstalledPackage.ExecutableName}.");
            var marker = InstalledPackage.TryRead(root, out var problem)
                ?? throw new UpdateException(problem ?? $"{asset.Name} is not a published WpfStudio package: it has no {InstalledPackage.MarkerFileName}.");
            if (marker.Kind != packageKind) throw new UpdateException($"{asset.Name} holds a {marker.Kind} package, not the {packageKind} package installed here.");
            progress?.Report(new UpdateProgress($"WpfStudio {release.Version} is ready to install.", 1));
            return new StagedUpdate(release.Version, root);
        }
        catch
        {
            Release();
            DeleteDirectory(folder);
            throw;
        }
    }

    /// <summary>Lets other windows remove this window's staged update again.</summary>
    public void Release()
    {
        _hold?.Dispose();
        _hold = null;
    }

    public void Dispose() => Release();

    /// <summary>Takes <c>.in-use</c> in an update's folder for this process alone; null when another process has it.</summary>
    public static FileStream? Hold(string folder)
    {
        try { return new FileStream(Path.Combine(folder, InUseFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The update's folder in <paramref name="updatesDirectory"/> that holds a staged package, or null when it is not in one.</summary>
    public static string? UpdateFolderOf(string stagedDirectory, string updatesDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updatesDirectory));
        for (var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDirectory)); Path.GetDirectoryName(folder) is { } parent; folder = parent)
            if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) return folder;
        return null;
    }

    /// <summary>Whether another window holds the update in a folder: its <c>.in-use</c> is there and cannot be opened.</summary>
    private static bool IsInUse(string folder)
    {
        var marker = Path.Combine(folder, InUseFileName);
        if (!File.Exists(marker)) return false;
        try
        {
            using var probe = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; } // Released just now.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    private async Task DownloadVerifiedAsync(ReleaseAsset asset, string expectedSha256, string path, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
        if (total > MaximumPackageBytes) throw new UpdateException($"{asset.Name} is larger than any WpfStudio package should be, so it was not downloaded.");
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0, reported = 0;
            int read;
            progress?.Report(new UpdateProgress($"Downloading {asset.Name}…", total is > 0 ? 0 : null));
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > MaximumPackageBytes) throw new UpdateException($"{asset.Name} is larger than any WpfStudio package should be, so it was not kept.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (total is > 0 && received - reported >= total / 100)
                {
                    reported = received;
                    progress?.Report(new UpdateProgress($"Downloading {asset.Name}… {FormatSize(received)} of {FormatSize(total.Value)}", Math.Min(1, (double)received / total.Value)));
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException($"The downloaded {asset.Name} does not match the SHA-256 hash the release lists for it, so it was not installed.");
        }
        progress?.Report(new UpdateProgress("Download verified.", 1));
    }

    /// <summary>The hash for a file from the release's SHA256SUMS.txt, for releases whose files GitHub lists without one.</summary>
    private async Task<string?> ReadPublishedChecksumAsync(ReleaseInfo release, string fileName, CancellationToken cancellationToken)
    {
        if (release.Assets.FirstOrDefault(asset => asset.Name.Equals(ChecksumFileName, StringComparison.OrdinalIgnoreCase)) is not { } sums) return null;
        if (sums.Size > MaximumChecksumFileBytes) return null;
        using var response = await SendAsync(sums.DownloadUrl, cancellationToken).ConfigureAwait(false);
        // Read no more than the limit allows, whatever size the release listed or the server sends.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumChecksumFileBytes + 1];
        int total = 0, read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0) total += read;
        return total > MaximumChecksumFileBytes ? null : FindChecksum(Encoding.UTF8.GetString(buffer, 0, total), fileName);
    }

    /// <summary>The hash for a file in <c>sha256sum</c> output: a hash, spaces (and <c>*</c> for binary mode), then the name.</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64 || !ReleaseFeed.IsSha256(line[..64])) continue;
            if (line[space..].TrimStart().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase)) return line[..64].ToLowerInvariant();
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme != Uri.UriSchemeHttps) throw new UpdateException($"Updates are only downloaded over HTTPS, not from {address}.");
        HttpResponseMessage response;
        try { response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException exception) { throw new UpdateException($"The download could not start: {exception.Message}", exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw new UpdateException("The download stopped answering.", exception); }
        if (!response.IsSuccessStatusCode)
        {
            using (response) throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {Path.GetFileName(address.AbsolutePath)}.");
        }
        // A redirect may only lead to another HTTPS address; HttpClient never follows one from HTTPS to HTTP.
        return response;
    }

    /// <summary>The folder holding WpfStudio.exe: the archive's root, or its only folder as Publish.ps1 writes it.</summary>
    public static string? FindPackageRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, InstalledPackage.ExecutableName))) return extracted;
        var candidates = Directory.GetDirectories(extracted).Where(folder => File.Exists(Path.Combine(folder, InstalledPackage.ExecutableName))).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    /// <summary>Removes every staged update except <paramref name="keep"/> and those another window holds. Ones still in use stay for the next time.</summary>
    public void RemoveStaleUpdates(string? keep = null)
    {
        if (!Directory.Exists(UpdatesDirectory)) return;
        var kept = keep is null ? null : Path.GetFullPath(keep);
        foreach (var folder in Directory.GetDirectories(UpdatesDirectory))
        {
            var full = Path.GetFullPath(folder);
            var holdsKept = kept is not null && (kept.Equals(full, StringComparison.OrdinalIgnoreCase) || kept.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!holdsKept && !IsInUse(folder)) DeleteDirectory(folder);
        }
    }

    internal static void DeleteDirectory(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* In use, perhaps by the update that is installing; the next start removes it. */ }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} bytes",
    };
}
