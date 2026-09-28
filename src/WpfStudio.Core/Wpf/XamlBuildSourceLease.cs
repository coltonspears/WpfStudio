using System.Security.Cryptography;

namespace WpfStudio.Core.Wpf;

public sealed record XamlBuildSource(string Path, string Sha1, string Sha256);

/// <summary>Holds read-only source handles while WPF separately parses and checksums its build inputs.</summary>
public sealed class XamlBuildSourceLease : IDisposable
{
    private const int MaximumFiles = 1024;
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private const long MaximumTotalBytes = 64 * 1024 * 1024;
    private const int MaximumIssues = 64;
    private readonly List<FileStream> _streams = [];
    private int _disposed;
    private XamlBuildSourceLease() { }

    public IReadOnlyList<XamlBuildSource> Sources { get; private set; } = [];
    public IReadOnlyList<string> Issues { get; private set; } = [];

    public static async Task<XamlBuildSourceLease> AcquireAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var lease = new XamlBuildSourceLease();
        var sources = new List<XamlBuildSource>();
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int examined = 0;
        var buffer = new byte[64 * 1024];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > 16_384) { Issue("The source input limit was reached; further files cannot be verified."); break; }
                if (sources.Count >= MaximumFiles) { Issue("The limit of 1,024 leased XAML sources was reached."); break; }
                FileStream? stream = null;
                try
                {
                    if (!IsLocalAbsolutePath(candidate)) { Issue("A source path is not an absolute local drive path and cannot be leased."); continue; }
                    string path = System.IO.Path.GetFullPath(candidate);
                    if (!seen.Add(path)) continue;
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long length = stream.Length;
                    if (length > MaximumFileBytes) { Issue($"Source exceeds the 8 MiB file limit: {Display(path)}"); continue; }
                    if (length > MaximumTotalBytes - total) { Issue($"Source exceeds the remaining 64 MiB source budget: {Display(path)}"); continue; }
                    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long readLength = 0;
                    while (true)
                    {
                        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        readLength += read;
                        if (readLength > length) throw new IOException("The source length changed while acquiring its lease.");
                        sha1.AppendData(buffer, 0, read);
                        sha256.AppendData(buffer, 0, read);
                    }
                    if (readLength != length) throw new IOException("The source length changed while acquiring its lease.");
                    cancellationToken.ThrowIfCancellationRequested();
                    sources.Add(new(path, Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha256.GetHashAndReset())));
                    total += length;
                    lease._streams.Add(stream);
                    stream = null; // The lease owns this handle until disposal.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Issue($"Source could not be held stable for the build: {Display(candidate)} ({Display(exception.Message)})");
                }
                finally { stream?.Dispose(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lease.Sources = sources.ToArray();
            lease.Issues = issues.ToArray();
            return lease;
        }
        catch { lease.Dispose(); throw; }

        void Issue(string message)
        {
            if (issues.Count < MaximumIssues) issues.Add(message);
            else if (issues.Count == MaximumIssues) issues.Add("Additional source lease issues were omitted.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var stream in _streams) stream.Dispose();
        _streams.Clear();
    }

    private static bool IsLocalAbsolutePath(string? path) => !string.IsNullOrWhiteSpace(path)
        && System.IO.Path.IsPathFullyQualified(path) && path.Length >= 3 && char.IsAsciiLetter(path[0])
        && path[1] == ':' && path[2] is '\\' or '/';
    private static string Display(string? value) => value is null ? "(missing path)" : value.Length <= 300 ? value : value[..300] + "…";
}
