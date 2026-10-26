using System.IO.Compression;
using System.Security.Cryptography;

namespace WpfStudio.Core.Debugging;

/// <summary>
/// Finds the netcoredbg debug adapter and installs the pinned release into the user's WpfStudio profile, so a
/// portable copy without <c>debugger/</c> can debug without rebuilding. The archive is verified against the same
/// checksum as <c>tools/Get-Debugger.ps1</c> before anything is extracted.
/// </summary>
public sealed class DebuggerInstaller(HttpMessageHandler? handler = null, string? dataDirectory = null)
{
    public const string Version = "3.2.0-1092";
    public const string ArchiveUrl = "https://github.com/Samsung/netcoredbg/releases/download/" + Version + "/netcoredbg-win64.zip";
    public const string ArchiveSha256 = "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a";
    public const string LicenseUrl = "https://raw.githubusercontent.com/Samsung/netcoredbg/" + Version + "/LICENSE";
    public const string ExecutableName = "netcoredbg.exe";
    /// <summary>The release archive is about 5 MB; anything far larger is not the pinned file.</summary>
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;

    public string Url { get; init; } = ArchiveUrl;
    public string ExpectedSha256 { get; init; } = ArchiveSha256;
    public string InstallDirectory => Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "debugger", Version);
    public string InstalledExecutable => Path.Combine(InstallDirectory, ExecutableName);

    /// <summary>The bundled adapter next to the application, else a profile installation, else null.</summary>
    public string? Locate()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "debugger", ExecutableName);
        if (File.Exists(bundled)) return bundled;
        return File.Exists(InstalledExecutable) ? InstalledExecutable : null;
    }

    /// <summary>Downloads, verifies and extracts the adapter, then returns its executable path. Progress reports short status lines.</summary>
    public async Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetDirectoryName(InstallDirectory)!;
        Directory.CreateDirectory(root);
        var unique = Guid.NewGuid().ToString("N");
        var archive = Path.Combine(root, $"download-{unique}.zip");
        var staging = Path.Combine(root, $"staging-{unique}");
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WpfStudio");
        try
        {
            progress?.Report($"Downloading netcoredbg {Version}…");
            using (var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (total > MaximumArchiveBytes) throw new InvalidDataException("The debugger download is larger than expected.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(archive);
                var buffer = new byte[81920];
                long received = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaximumArchiveBytes) throw new InvalidDataException("The debugger download is larger than expected.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total is > 0 && received - reported > total / 10) { reported = received; progress?.Report($"Downloading netcoredbg {Version}… {received * 100 / total}%"); }
                }
            }
            progress?.Report("Verifying the download…");
            string actual;
            await using (var stream = File.OpenRead(archive))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The debugger download did not match its expected checksum ({actual}). Nothing was installed.");

            progress?.Report("Extracting…");
            ZipFile.ExtractToDirectory(archive, staging); // Rejects entries that would escape the directory.
            var executable = Directory.EnumerateFiles(staging, ExecutableName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("The debugger archive contains no " + ExecutableName + ".");
            var contents = Path.GetDirectoryName(executable)!;
            try
            {
                using var license = await client.GetAsync(LicenseUrl, cancellationToken).ConfigureAwait(false);
                if (license.IsSuccessStatusCode) await File.WriteAllBytesAsync(Path.Combine(contents, "LICENSE"), await license.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) { } // The license copy is informational; the adapter works without it.
            if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
            Directory.Move(contents, InstallDirectory);
            progress?.Report($"Installed netcoredbg {Version}");
            return InstalledExecutable;
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch (IOException) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
