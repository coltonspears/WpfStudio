using System.Security.Cryptography;
using System.Text;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlBuildSourceLeaseTests
{
    [Fact]
    public async Task LeaseHashesExactBytesAndPreventsNormalWritesOrReplacementUntilDisposed()
    {
        using var directory = new LeaseDirectory();
        string path = directory.PathFor("View.xaml");
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("<Button Content=\"Original\" />\r\n")];
        await File.WriteAllBytesAsync(path, bytes);
        string replacement = directory.PathFor("replacement.xaml");
        await File.WriteAllTextAsync(replacement, "<TextBlock />");
        using var lease = await XamlBuildSourceLease.AcquireAsync([path, path]);
        var source = Assert.Single(lease.Sources);
        Assert.Empty(lease.Issues);
        Assert.Equal(path, source.Path);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(bytes)), source.Sha1);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), source.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); // Build readers remain allowed.
        AssertSharingDenied(() => File.WriteAllText(path, "Changed"));
        AssertSharingDenied(() => File.Move(replacement, path, overwrite: true));
        AssertSharingDenied(() => File.Replace(replacement, path, destinationBackupFileName: null));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        lease.Dispose();
        lease.Dispose();
        File.Move(replacement, path, overwrite: true);
        await File.WriteAllTextAsync(path, "<Button Content=\"Changed\" />");
        Assert.NotEqual(source.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
    }

    [Fact]
    public async Task MissingNonLocalAndLockedSourcesAreOmittedWithoutDiscardingUsableInputs()
    {
        using var directory = new LeaseDirectory();
        string good = directory.PathFor("Good.xaml");
        string locked = directory.PathFor("Locked.xaml");
        await File.WriteAllTextAsync(good, "<Button />");
        await File.WriteAllTextAsync(locked, "<Grid />");
        using var writer = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var lease = await XamlBuildSourceLease.AcquireAsync([directory.PathFor("Missing.xaml"), @"\\server\share\View.xaml", "relative.xaml", locked, good]);
        Assert.Equal(good, Assert.Single(lease.Sources).Path);
        Assert.Equal(4, lease.Issues.Count);
        Assert.All(lease.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue)));
    }

    [Fact]
    public async Task CancellationAfterOneAcquisitionReleasesItsHandle()
    {
        using var directory = new LeaseDirectory();
        string path = directory.PathFor("View.xaml");
        await File.WriteAllTextAsync(path, "<Grid />");
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> Inputs()
        {
            yield return path;
            cancellation.Cancel();
            yield return directory.PathFor("Other.xaml");
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XamlBuildSourceLease.AcquireAsync(Inputs(), cancellation.Token));
        await File.WriteAllTextAsync(path, "<Button />");
        Assert.Equal("<Button />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FileAndAggregateByteLimitsReleaseSkippedFilesAndRetainBoundedIssues()
    {
        using var directory = new LeaseDirectory();
        var paths = new List<string>();
        for (int index = 0; index < 9; index++)
        {
            string path = directory.PathFor($"Large{index}.xaml");
            using (var stream = File.Create(path)) stream.SetLength(8 * 1024 * 1024);
            paths.Add(path);
        }
        string oversized = directory.PathFor("Oversized.xaml");
        using (var stream = File.Create(oversized)) stream.SetLength(8 * 1024 * 1024 + 1);
        using var lease = await XamlBuildSourceLease.AcquireAsync(paths.Prepend(oversized));
        Assert.Equal(8, lease.Sources.Count);
        Assert.Equal(2, lease.Issues.Count);
        Assert.Contains(lease.Issues, issue => issue.Contains("8 MiB"));
        Assert.Contains(lease.Issues, issue => issue.Contains("64 MiB"));
        await File.WriteAllTextAsync(oversized, "<Grid />");
        await File.WriteAllTextAsync(paths[^1], "<Grid />");
        using var missing = await XamlBuildSourceLease.AcquireAsync(Enumerable.Range(0, 200).Select(index => directory.PathFor($"Missing{index}.xaml")));
        Assert.Empty(missing.Sources);
        Assert.InRange(missing.Issues.Count, 1, 65);
        Assert.Contains(missing.Issues, issue => issue.Contains("omitted"));
    }

    [Fact]
    public async Task SourceCountLimitDoesNotOpenAdditionalFiles()
    {
        using var directory = new LeaseDirectory();
        var paths = Enumerable.Range(0, 1025).Select(index => directory.PathFor($"View{index}.xaml")).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "<Grid />");
        using var lease = await XamlBuildSourceLease.AcquireAsync(paths);
        Assert.Equal(1024, lease.Sources.Count);
        Assert.Contains(lease.Issues, issue => issue.Contains("1,024"));
        await File.WriteAllTextAsync(paths[^1], "<Button />");
    }

    private static void AssertSharingDenied(Action action)
    {
        var exception = Record.Exception(action);
        Assert.True(exception is IOException or UnauthorizedAccessException,
            $"Expected a sharing denial, received {exception?.GetType().Name ?? "no exception"}.");
    }

    private sealed class LeaseDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "WpfStudio-LeaseTests", Guid.NewGuid().ToString("N"));
        public LeaseDirectory() => Directory.CreateDirectory(_path);
        public string PathFor(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
