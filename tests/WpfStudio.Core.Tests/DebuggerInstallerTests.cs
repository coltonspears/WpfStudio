using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using WpfStudio.Core.Debugging;

namespace WpfStudio.Core.Tests;

public sealed class DebuggerInstallerTests
{
    private static string Directory() => System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WpfStudio.Installer", Guid.NewGuid().ToString("N"))).FullName;

    private static byte[] Archive()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("netcoredbg/netcoredbg.exe").Open())) writer.Write("adapter");
            using (var writer = new StreamWriter(zip.CreateEntry("netcoredbg/ManagedPart.dll").Open())) writer.Write("part");
        }
        return memory.ToArray();
    }

    private sealed class Server(byte[] archive, Action? license = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var isLicense = request.RequestUri!.AbsolutePath.EndsWith("LICENSE", StringComparison.Ordinal);
            if (isLicense) license?.Invoke();
            var body = isLicense ? "MIT"u8.ToArray() : archive;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public async Task VerifiedArchiveInstallsIntoTheProfileAndIsLocated()
    {
        var archive = Archive();
        var data = Directory();
        var server = new Server(archive);
        var installer = new DebuggerInstaller(server, data) { ExpectedSha256 = Convert.ToHexString(SHA256.HashData(archive)) };
        Assert.Null(installer.Locate());
        var messages = new List<string>();
        var path = await installer.InstallAsync(new SynchronousProgress(messages));
        Assert.Equal(Path.Combine(data, "debugger", DebuggerInstaller.Version, "netcoredbg.exe"), path);
        Assert.Equal("adapter", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(Path.Combine(installer.InstallDirectory, "ManagedPart.dll")));
        Assert.Equal("MIT", await File.ReadAllTextAsync(Path.Combine(installer.InstallDirectory, "LICENSE")));
        Assert.Equal(path, installer.Locate());
        Assert.Contains(messages, m => m.StartsWith("Installed", StringComparison.Ordinal));
        Assert.Equal([installer.InstallDirectory], System.IO.Directory.GetDirectories(Path.GetDirectoryName(installer.InstallDirectory)!)); // no staging left
        Assert.Equal(DebuggerInstaller.ArchiveUrl, server.Requests[0]);

        // Reinstalling replaces the previous copy.
        Assert.Equal(path, await installer.InstallAsync());
    }

    [Fact]
    public async Task ChecksumMismatchInstallsNothing()
    {
        var data = Directory();
        var installer = new DebuggerInstaller(new Server(Archive()), data);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync());
        Assert.Contains("checksum", error.Message);
        Assert.False(System.IO.Directory.Exists(installer.InstallDirectory));
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(installer.InstallDirectory)!));
    }

    [Fact]
    public async Task LicenseTimeoutLeavesTheVerifiedAdapterInstalled()
    {
        var archive = Archive();
        // HttpClient reports its own timeout as a cancellation nobody asked for.
        var installer = new DebuggerInstaller(new Server(archive, () => throw new TaskCanceledException()), Directory()) { ExpectedSha256 = Convert.ToHexString(SHA256.HashData(archive)) };
        var path = await installer.InstallAsync();
        Assert.Equal("adapter", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(installer.InstallDirectory, "LICENSE")));
    }

    [Fact]
    public async Task CancellingDuringTheLicenseDownloadStillCancelsTheInstall()
    {
        var archive = Archive();
        using var cancellation = new CancellationTokenSource();
        var installer = new DebuggerInstaller(new Server(archive, () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }), Directory()) { ExpectedSha256 = Convert.ToHexString(SHA256.HashData(archive)) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cancellationToken: cancellation.Token));
        Assert.False(System.IO.Directory.Exists(installer.InstallDirectory));
    }

    private sealed class SynchronousProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
