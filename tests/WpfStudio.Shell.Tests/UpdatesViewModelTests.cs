using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.App.Features.Updates;
using WpfStudio.Contracts;
using WpfStudio.Core.Updates;

namespace WpfStudio.Shell.Tests;

/// <summary>
/// Help > Check for updates against a scripted GitHub: what the page says and offers at each step, and when it asks the
/// shell to restart. The view model has no WPF in it, so these need no dispatcher thread.
/// </summary>
public sealed class UpdatesViewModelTests : IDisposable
{
    private const string Repository = "contoso/WpfStudio";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WpfStudio.UpdatesPage", Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<UpdateService> _services = [];

    public void Dispose()
    {
        // A service holds the update it downloaded until it is disposed.
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { action(); return Task.CompletedTask; }
    }

    private sealed class Host : IUpdateHost
    {
        public List<(StagedUpdate Staged, bool Automatic)> Restarts { get; } = [];
        public List<string> Opened { get; } = [];
        public bool Restart { get; set; }
        public Task<bool> RestartToUpdateAsync(StagedUpdate staged, bool automatic) { Restarts.Add((staged, automatic)); return Task.FromResult(Restart); }
        public void OpenUrl(string url) => Opened.Add(url);
    }

    private sealed class GitHub : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Latest { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        public Dictionary<string, byte[]> Files { get; } = [];
        /// <summary>Held open until released, to cancel a download under way.</summary>
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Downloading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com") return Latest();
            Downloading.TrySetResult();
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return Files.TryGetValue(request.RequestUri.AbsoluteUri, out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static string Address(string name) => $"https://github.com/{Repository}/releases/download/v0.2.0/{name}";

    private static HttpResponseMessage Release(params (string Name, byte[] Bytes, string Digest)[] assets) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = "v0.2.0", ["name"] = "WpfStudio 0.2.0", ["html_url"] = $"https://github.com/{Repository}/releases/tag/v0.2.0",
            ["published_at"] = "2026-11-20T10:00:00Z", ["body"] = "## What's Changed\n* **Check for updates** by @someone",
            ["assets"] = assets.Select(asset => new Dictionary<string, object?>
            {
                ["name"] = asset.Name, ["size"] = asset.Bytes.Length, ["state"] = "uploaded", ["digest"] = asset.Digest, ["browser_download_url"] = Address(asset.Name),
            }).ToArray(),
        }), Encoding.UTF8, "application/json"),
    };

    private static byte[] Package()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("WpfStudio/WpfStudio.exe", "exe 0.2.0"), ("WpfStudio/WpfStudio.package.json", $$"""{ "package": "win-x64", "repository": "{{Repository}}" }""") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return memory.ToArray();
    }

    private (UpdatesViewModel Page, Host Host, GitHub GitHub, UpdateService Service) Create(bool packaged = true, string? version = "0.1.0")
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "WpfStudio")).FullName;
        File.WriteAllText(Path.Combine(installed, "WpfStudio.exe"), "exe 0.1.0");
        if (packaged) File.WriteAllText(Path.Combine(installed, InstalledPackage.MarkerFileName), $$"""{ "package": "win-x64", "repository": "{{Repository}}" }""");
        var github = new GitHub();
        var service = new UpdateService(new HttpClient(github), installed, Path.Combine(_root, "data", "updates"), version, Repository);
        _services.Add(service);
        var host = new Host();
        return (new UpdatesViewModel(service, host, new Inline()), host, github, service);
    }

    private static void Serve(GitHub github, byte[] package, string? digest = null)
    {
        github.Files[Address("WpfStudio-0.2.0-win-x64.zip")] = package;
        github.Latest = () => Release(("WpfStudio-0.2.0-win-x64.zip", package, digest ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(package))));
    }

    [Fact]
    public async Task FindsANewerReleaseDownloadsItAndAsksTheShellToRestart()
    {
        var (page, host, github, _) = Create();
        Serve(github, Package());
        Assert.Equal(UpdateState.NotChecked, page.State);
        Assert.Equal("0.1.0", page.CurrentVersion);

        await page.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Available, page.State);
        Assert.Equal("WpfStudio 0.2.0 is available.", page.Message);
        Assert.Contains("closes WpfStudio, replaces it and starts it again", page.Detail);
        Assert.True(page.CanInstall);
        Assert.True(page.ShowInstall);
        Assert.Equal("Update and restart", page.InstallLabel);
        Assert.True(page.HasRelease);
        Assert.StartsWith("WpfStudio 0.2.0 · ", page.ReleaseHeading);
        Assert.Equal("What's Changed\n• Check for updates by @someone", page.ReleaseNotes);
        Assert.False(page.IsBusy);

        // The person stopped watching, so the shell leaves the restart to them.
        host.Restart = false;
        await page.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Ready, page.State);
        Assert.True(page.IsReady);
        Assert.Equal("WpfStudio 0.2.0 is ready to install.", page.Message);
        Assert.Equal("Restart to update", page.InstallLabel);
        Assert.True(page.ShowInstall);
        Assert.False(page.CanInstall);
        Assert.Equal(1, page.Progress);
        Assert.False(page.ShowProgress || page.ShowActivity);
        Assert.False(page.CheckCommand.CanExecute(null)); // Nothing to check while an update waits to be installed.
        var (staged, automatic) = Assert.Single(host.Restarts);
        Assert.True(automatic);
        Assert.Equal("exe 0.2.0", File.ReadAllText(staged.Executable));
        Assert.Same(staged, page.Staged);

        // Restart to update: no second download.
        await page.InstallCommand.ExecuteAsync(null);
        Assert.Equal(2, host.Restarts.Count);
        Assert.False(host.Restarts[1].Automatic);
        Assert.Same(staged, host.Restarts[1].Staged);
    }

    [Fact]
    public async Task AReadyUpdateThatWasRemovedIsDownloadedAgainRatherThanRestartedInto()
    {
        var (page, host, github, service) = Create();
        Serve(github, Package());
        await page.CheckCommand.ExecuteAsync(null);
        await page.InstallCommand.ExecuteAsync(null); // The host declines the automatic restart.
        Assert.True(page.IsReady);
        service.Dispose(); // As if this window let go of it and another one cleaned up.
        Directory.Delete(Path.Combine(service.UpdatesDirectory, "0.2.0"), recursive: true);

        await page.InstallCommand.ExecuteAsync(null);

        Assert.Single(host.Restarts); // Only the automatic one.
        Assert.Equal(UpdateState.Available, page.State);
        Assert.Null(page.Staged);
        Assert.Contains("download it again", page.Detail);
        Assert.Equal("Update and restart", page.InstallLabel);
        Assert.True(page.CanInstall);
    }

    [Fact]
    public async Task SaysWhenThereIsNothingNewer()
    {
        var (page, _, github, _) = Create(version: "0.2.0");
        await page.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.NoReleases, page.State);
        Assert.Equal("No release has been published yet.", page.Message);
        Assert.False(page.ShowInstall);

        Serve(github, Package());
        await page.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.UpToDate, page.State);
        Assert.Equal("WpfStudio 0.2.0 is up to date.", page.Message);
        Assert.Equal("It is the newest release.", page.Detail);
        Assert.False(page.ShowInstall);
        Assert.True(page.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task ABuildFromSourceIsOnlyPointedAtTheRelease()
    {
        var (page, host, github, _) = Create(packaged: false);
        Serve(github, Package());

        await page.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Available, page.State);
        Assert.Contains("built from source", page.Detail);
        Assert.False(page.CanInstall);
        Assert.False(page.ShowInstall);
        page.OpenReleasePageCommand.Execute(null);
        Assert.Equal([$"https://github.com/{Repository}/releases/tag/v0.2.0"], host.Opened);
    }

    [Fact]
    public async Task FailuresAreExplainedAndCanBeTriedAgain()
    {
        var (page, host, github, _) = Create();
        github.Latest = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        await page.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, page.State);
        Assert.Equal("Could not check for updates.", page.Message);
        Assert.Contains("502", page.Detail);
        Assert.Null(page.LastCheck);
        Assert.True(page.CheckCommand.CanExecute(null));
        page.OpenReleasePageCommand.Execute(null);
        Assert.Equal([$"https://github.com/{Repository}/releases"], host.Opened);

        // A download that does not match its hash is refused, and can be tried again.
        Serve(github, Package(), digest: "sha256:" + new string('0', 64));
        await page.CheckCommand.ExecuteAsync(null);
        await page.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, page.State);
        Assert.Equal("WpfStudio 0.2.0 could not be downloaded.", page.Message);
        Assert.Contains("SHA-256", page.Detail);
        Assert.Empty(host.Restarts);
        Assert.True(page.CanInstall);

        Serve(github, Package());
        await page.CheckCommand.ExecuteAsync(null);
        host.Restart = true;
        await page.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Ready, page.State);
        Assert.Single(host.Restarts);
    }

    [Fact]
    public async Task ADownloadCanBeCancelled()
    {
        var (page, host, github, service) = Create();
        Serve(github, Package());
        await page.CheckCommand.ExecuteAsync(null);
        github.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var install = page.InstallCommand.ExecuteAsync(null);
        await github.Downloading.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(UpdateState.Downloading, page.State);
        Assert.True(page.IsBusy);
        Assert.True(page.CancelCommand.CanExecute(null));
        Assert.False(page.CheckCommand.CanExecute(null));
        page.CancelCommand.Execute(null);
        await install.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(UpdateState.Available, page.State);
        Assert.Equal("The download was cancelled.", page.Detail);
        Assert.True(page.CanInstall);
        Assert.Empty(host.Restarts);
        Assert.False(Directory.Exists(Path.Combine(service.UpdatesDirectory, "0.2.0")));
    }

    [Fact]
    public async Task ClosingWpfStudioStopsADownloadUnderWay()
    {
        var (page, host, github, service) = Create();
        Serve(github, Package());
        await page.CheckCommand.ExecuteAsync(null);
        github.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var install = page.InstallCommand.ExecuteAsync(null);
        await github.Downloading.Task.WaitAsync(TimeSpan.FromSeconds(30));
        page.Dispose();
        await install.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(page.IsBusy);
        Assert.Empty(host.Restarts);
        Assert.False(Directory.Exists(Path.Combine(service.UpdatesDirectory, "0.2.0")));
        page.Dispose(); // Once is enough; twice does no harm.
    }

    [Fact]
    public async Task StartingAfterAnUpdateSaysSoAndClearsWhatItLeft()
    {
        var (page, _, _, service) = Create(version: "0.2.0");
        var backup = Directory.CreateDirectory(Path.Combine(service.ApplicationDirectory, UpdateApplier.BackupFolderName, "earlier")).FullName;
        File.WriteAllText(Path.Combine(backup, "WpfStudio.exe"), "exe 0.1.0");
        File.WriteAllText(Path.Combine(backup, UpdateApplier.CompletedMarkerName), ""); // As a completed update leaves it.
        var staged = Directory.CreateDirectory(Path.Combine(service.UpdatesDirectory, "0.2.0", "package")).FullName;

        Assert.Equal("Updated WpfStudio from 0.1.0 to 0.2.0", page.Start([UpdateCommandLine.UpdatedFromOption, "0.1.0"]));
        Assert.Null(page.Start([]));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((Directory.Exists(backup) || Directory.Exists(staged)) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.False(Directory.Exists(Path.Combine(service.ApplicationDirectory, UpdateApplier.BackupFolderName)));
        Assert.False(Directory.Exists(staged));
        Assert.True(File.Exists(Path.Combine(service.ApplicationDirectory, "WpfStudio.exe")));
    }
}
