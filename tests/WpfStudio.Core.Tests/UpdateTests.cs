using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WpfStudio.Core.Updates;

namespace WpfStudio.Core.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V0.10.0", "0.10.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.2")]
    [InlineData("0.1.0+16df40ae027bb115c21938c5004216481701448c", "0.1.0")]
    [InlineData("1.0.0-rc.1+build.5", "1.0.0-rc.1")]
    public void ReadsVersionsAsTagsAndAssembliesWriteThem(string text, string expected) => Assert.Equal(expected, ReleaseVersion.Parse(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-be$ta")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("development build")]
    [InlineData("99999999999.0.0")]
    public void RefusesWhatIsNotAVersion(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void OrdersAsSemanticVersioningDoes()
    {
        // The order Semantic Versioning 2.0.0 gives as its example, then numeric rather than text order of each part.
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0"];
        var versions = ordered.Select(ReleaseVersion.Parse).ToArray();
        for (var i = 1; i < versions.Length; i++)
        {
            Assert.True(versions[i - 1] < versions[i], $"{versions[i - 1]} < {versions[i]}");
            Assert.True(versions[i] > versions[i - 1]);
            Assert.False(versions[i] <= versions[i - 1]);
        }
        Assert.Equal(ordered, Enumerable.Reverse(versions).Order().Select(v => v.ToString()));
        Assert.True(ReleaseVersion.Parse("v1.2") == ReleaseVersion.Parse("1.2.0"));
        Assert.True(ReleaseVersion.Parse("1.2.0+a") >= ReleaseVersion.Parse("1.2.0+b"));
        Assert.True(ReleaseVersion.Parse("1.2.0").IsPrerelease is false && ReleaseVersion.Parse("1.2.0-rc.1").IsPrerelease);
    }
}

public sealed class UpdateTests : IDisposable
{
    private const string Repository = "contoso/WpfStudio";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WpfStudio.Updates", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ------------------------------------------------------------------ Scripted GitHub

    /// <summary>Answers like GitHub: the latest-release endpoint, and release files by their download address.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Latest { get; set; }
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "api.github.com" && path == $"/repos/{Repository}/releases/latest")
                return Task.FromResult(Latest?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(Files.TryGetValue(request.RequestUri.AbsoluteUri, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string Download(string tag, string name) => $"https://github.com/{Repository}/releases/download/{tag}/{name}";

    private static string ReleaseJson(string tag, params (string Name, long Size, string? Digest, string State)[] assets) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["name"] = "",
            ["body"] = "## What's Changed\r\n* Add a check for updates by @someone in https://github.com/contoso/WpfStudio/pull/7\r\n",
            ["html_url"] = $"https://github.com/{Repository}/releases/tag/{tag}",
            ["published_at"] = "2026-11-20T10:00:00Z",
            ["draft"] = false,
            ["prerelease"] = false,
            ["assets"] = assets.Select(asset => new Dictionary<string, object?>
            {
                ["name"] = asset.Name, ["size"] = asset.Size, ["state"] = asset.State, ["digest"] = asset.Digest,
                ["browser_download_url"] = Download(tag, asset.Name),
            }).ToArray(),
        });

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A package laid out as Publish.ps1 zips it: one WpfStudio folder with the executable and its marker.</summary>
    private static byte[] PackageZip(string kind, string version, bool marker = true, bool executable = true)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
            if (executable) Add("WpfStudio/WpfStudio.exe", "exe " + version);
            Add("WpfStudio/WpfStudio.dll", "app " + version);
            Add("WpfStudio/WorkspaceHost/WpfStudio.WorkspaceHost.dll", "worker " + version);
            if (marker) Add("WpfStudio/WpfStudio.package.json", $$"""{ "schema": 1, "package": "{{kind}}", "repository": "{{Repository}}" }""");
        }
        return memory.ToArray();
    }

    private string Installed(string kind = "win-x64", string version = "0.1.0", bool marker = true)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "installed-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        File.WriteAllText(Path.Combine(folder, "WpfStudio.exe"), "exe " + version);
        File.WriteAllText(Path.Combine(folder, "WpfStudio.dll"), "app " + version);
        if (marker) File.WriteAllText(Path.Combine(folder, "WpfStudio.package.json"), $$"""{ "schema": 1, "package": "{{kind}}", "repository": "{{Repository}}" }""");
        return folder;
    }

    private UpdateService Service(GitHub github, string installed, string? current = "0.1.0") =>
        new(new HttpClient(github), installed, Path.Combine(_root, "data", "updates"), current, defaultRepository: Repository);

    private sealed class Recorder : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = [];
        public void Report(UpdateProgress value) => Reports.Add(value);
    }

    // ------------------------------------------------------------------ The release feed

    [Fact]
    public async Task ReadsTheNewestReleaseAndAsksGitHubAsDocumented()
    {
        var github = new GitHub
        {
            Latest = () => Json(ReleaseJson("v0.2.0",
                ("WpfStudio-0.2.0-win-x64.zip", 1234, "sha256:" + new string('A', 64), "uploaded"),
                ("WpfStudio-0.2.0-win-x64-self-contained.zip", 5678, null, "uploaded"),
                ("half-uploaded.zip", 1, null, "starter"))),
        };
        var release = await new ReleaseFeed(new HttpClient(github), Repository, "0.1.0").GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("0.2.0", release.Version.ToString());
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("WpfStudio 0.2.0", release.Title);
        Assert.Equal(new Uri($"https://github.com/{Repository}/releases/tag/v0.2.0"), release.Page);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(["WpfStudio-0.2.0-win-x64.zip", "WpfStudio-0.2.0-win-x64-self-contained.zip"], release.Assets.Select(asset => asset.Name));
        Assert.Equal(new string('a', 64), release.Assets[0].Sha256);
        Assert.Null(release.Assets[1].Sha256);
        Assert.Equal(1234, release.Assets[0].Size);

        var request = Assert.Single(github.Requests);
        Assert.Equal($"https://api.github.com/repos/{Repository}/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Equal("WpfStudio/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task ARepositoryWithoutReleasesHasNothingToOffer()
    {
        var feed = new ReleaseFeed(new HttpClient(new GitHub()), Repository);
        Assert.Null(await feed.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RefusalsAndUnreadableAnswersAreExplained()
    {
        var github = new GitHub();
        var feed = new ReleaseFeed(new HttpClient(github), Repository);

        github.Latest = () =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.Add("x-ratelimit-remaining", "0");
            limited.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return limited;
        };
        Assert.Contains("Try again after", (await Assert.ThrowsAsync<UpdateException>(() => feed.GetLatestAsync(CancellationToken.None))).Message);

        github.Latest = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Contains("502", (await Assert.ThrowsAsync<UpdateException>(() => feed.GetLatestAsync(CancellationToken.None))).Message);

        github.Latest = () => Json("{ not json");
        Assert.Contains("could not be read", (await Assert.ThrowsAsync<UpdateException>(() => feed.GetLatestAsync(CancellationToken.None))).Message);

        github.Latest = () => Json(ReleaseJson("nightly"));
        Assert.Contains("'nightly'", (await Assert.ThrowsAsync<UpdateException>(() => feed.GetLatestAsync(CancellationToken.None))).Message);

        Assert.Throws<ArgumentException>(() => new ReleaseFeed(new HttpClient(github), "contoso/WpfStudio/extra"));
        Assert.Throws<ArgumentException>(() => new ReleaseFeed(new HttpClient(github), "../WpfStudio"));
    }

    [Fact]
    public void GeneratedReleaseNotesReadAsPlainText()
    {
        const string markdown = "<!-- Release notes generated using configuration in .github/release.yml -->\r\n\r\n## What's Changed\r\n"
            + "* Add **Check for updates** by @someone in https://github.com/contoso/WpfStudio/pull/7\r\n"
            + "  - Uses `releases/latest`, see [the docs](https://docs.github.com/rest)\r\n\r\n\r\n\r\n"
            + "**Full Changelog**: https://github.com/contoso/WpfStudio/compare/v0.1.0...v0.2.0\r\n";
        Assert.Equal("What's Changed\n• Add Check for updates by @someone in https://github.com/contoso/WpfStudio/pull/7\n  • Uses releases/latest, see the docs\n\n"
            + "Full Changelog: https://github.com/contoso/WpfStudio/compare/v0.1.0...v0.2.0", ReleaseNotesText.ToPlainText(markdown));
        Assert.Equal("", ReleaseNotesText.ToPlainText(null));
    }

    // ------------------------------------------------------------------ The package marker and the check

    [Fact]
    public void ReadsThePackageMarkerAndExplainsABadOne()
    {
        var folder = Installed("win-x64-self-contained");
        var package = InstalledPackage.TryRead(folder, out var problem);
        Assert.Null(problem);
        Assert.Equal(new InstalledPackage(folder, "win-x64-self-contained", Repository), package);
        Assert.Equal("WpfStudio-1.2.3-win-x64-self-contained.zip", package!.AssetName(ReleaseVersion.Parse("1.2.3")));
        Assert.True(package.CanWrite());
        Assert.Empty(Directory.GetFiles(folder, ".write-test-*"));

        Assert.Null(InstalledPackage.TryRead(Installed(marker: false), out problem));
        Assert.Null(problem); // A build from source.

        foreach (var (json, expected) in new[] { ("""{ "package": "../x", "repository": "a/b" }""", "package kind"), ("""{ "package": "win-x64" }""", "repository"), ("{", "could not be read") })
        {
            File.WriteAllText(Path.Combine(folder, InstalledPackage.MarkerFileName), json);
            Assert.Null(InstalledPackage.TryRead(folder, out problem));
            Assert.Contains(expected, problem);
        }
    }

    [Fact]
    public async Task ChecksWhetherTheNewestReleaseIsNewerAndInstallableHere()
    {
        var github = new GitHub();
        var installed = Installed();
        var service = Service(github, installed);
        Assert.Equal(Repository, service.Feed.Repository);

        // No release yet.
        var check = await service.CheckAsync(CancellationToken.None);
        Assert.Null(check.Release);
        Assert.False(check.IsNewer);

        // The same version, and an older one.
        foreach (var tag in new[] { "v0.1.0", "v0.0.9" })
        {
            github.Latest = () => Json(ReleaseJson(tag));
            check = await service.CheckAsync(CancellationToken.None);
            Assert.NotNull(check.Release);
            Assert.False(check.IsNewer);
            Assert.False(check.CanInstall);
        }

        // Newer, with the package installed here.
        github.Latest = () => Json(ReleaseJson("v0.2.0", ("WpfStudio-0.2.0-win-x64-self-contained.zip", 10, null, "uploaded"), ("WpfStudio-0.2.0-win-x64.zip", 10, null, "uploaded")));
        check = await service.CheckAsync(CancellationToken.None);
        Assert.True(check.IsNewer);
        Assert.True(check.CanInstall);
        Assert.Equal("WpfStudio-0.2.0-win-x64.zip", check.Asset!.Name);

        // Newer, but without this kind of package.
        github.Latest = () => Json(ReleaseJson("v0.2.0", ("WpfStudio-0.2.0-win-x64-self-contained.zip", 10, null, "uploaded")));
        check = await service.CheckAsync(CancellationToken.None);
        Assert.True(check.IsNewer);
        Assert.False(check.CanInstall);
        Assert.Contains("WpfStudio-0.2.0-win-x64.zip", check.CannotInstall);

        // Newer, from a build without a marker: only the release page.
        check = await Service(github, Installed(marker: false)).CheckAsync(CancellationToken.None);
        Assert.True(check.IsNewer);
        Assert.False(check.CanInstall);
        Assert.Contains("built from source", check.CannotInstall);

        // A build whose version is unknown takes any release as newer.
        Assert.True((await Service(github, installed, current: "development build").CheckAsync(CancellationToken.None)).IsNewer);
    }

    // ------------------------------------------------------------------ Downloading

    [Fact]
    public async Task DownloadsVerifiesAndUnpacksTheMatchingPackage()
    {
        var zip = PackageZip("win-x64", "0.2.0");
        var github = new GitHub { Latest = () => Json(ReleaseJson("v0.2.0", ("WpfStudio-0.2.0-win-x64.zip", zip.Length, "sha256:" + Sha256(zip), "uploaded"))) };
        github.Files[Download("v0.2.0", "WpfStudio-0.2.0-win-x64.zip")] = zip;
        var service = Service(github, Installed());
        // An update staged earlier is replaced.
        var stale = Directory.CreateDirectory(Path.Combine(service.UpdatesDirectory, "0.1.5", "package")).FullName;
        var progress = new Recorder();

        var staged = await service.DownloadAsync(await service.CheckAsync(CancellationToken.None), progress, CancellationToken.None);

        Assert.Equal("0.2.0", staged.Version.ToString());
        Assert.Equal(Path.Combine(service.UpdatesDirectory, "0.2.0", "package", "WpfStudio"), staged.Directory);
        Assert.Equal("exe 0.2.0", File.ReadAllText(staged.Executable));
        Assert.Equal("worker 0.2.0", File.ReadAllText(Path.Combine(staged.Directory, "WorkspaceHost", "WpfStudio.WorkspaceHost.dll")));
        Assert.Empty(Directory.GetFiles(Path.Combine(service.UpdatesDirectory, "0.2.0"))); // The archive itself is not kept.
        Assert.False(Directory.Exists(stale));
        Assert.Contains(progress.Reports, report => report.Message.StartsWith("Downloading WpfStudio-0.2.0-win-x64.zip", StringComparison.Ordinal));
        Assert.Equal(1, progress.Reports[^1].Fraction);
    }

    [Fact]
    public async Task UsesSha256SumsWhenGitHubListsNoHash()
    {
        var zip = PackageZip("win-x64", "0.2.0");
        var sums = Encoding.UTF8.GetBytes($"{new string('0', 64)}  WpfStudio-0.2.0-win-x64-self-contained.zip\n{Sha256(zip)} *WpfStudio-0.2.0-win-x64.zip\n");
        var github = new GitHub { Latest = () => Json(ReleaseJson("v0.2.0", ("WpfStudio-0.2.0-win-x64.zip", zip.Length, null, "uploaded"), (UpdateDownloader.ChecksumFileName, sums.Length, null, "uploaded"))) };
        github.Files[Download("v0.2.0", "WpfStudio-0.2.0-win-x64.zip")] = zip;
        github.Files[Download("v0.2.0", UpdateDownloader.ChecksumFileName)] = sums;
        var service = Service(github, Installed());

        var staged = await service.DownloadAsync(await service.CheckAsync(CancellationToken.None), null, CancellationToken.None);
        Assert.True(File.Exists(staged.Executable));

        Assert.Null(UpdateDownloader.FindChecksum("not a hash  WpfStudio-0.2.0-win-x64.zip", "WpfStudio-0.2.0-win-x64.zip"));
        Assert.Null(UpdateDownloader.FindChecksum($"{Sha256(zip)}  other.zip", "WpfStudio-0.2.0-win-x64.zip"));
    }

    [Fact]
    public async Task RefusesAPackageThatCannotBeVerifiedOrIsNotTheRightOne()
    {
        var installed = Installed();
        var good = PackageZip("win-x64", "0.2.0");
        var github = new GitHub();
        var service = Service(github, installed);
        var folder = Path.Combine(service.UpdatesDirectory, "0.2.0");
        var address = Download("v0.2.0", "WpfStudio-0.2.0-win-x64.zip");

        async Task<string> Refusal(byte[] served, string? digest)
        {
            github.Files[address] = served;
            github.Latest = () => Json(ReleaseJson("v0.2.0", ("WpfStudio-0.2.0-win-x64.zip", served.Length, digest, "uploaded")));
            var exception = await Assert.ThrowsAsync<UpdateException>(async () => await service.DownloadAsync(await service.CheckAsync(CancellationToken.None), null, CancellationToken.None));
            Assert.False(Directory.Exists(folder)); // Nothing is left half-done.
            return exception.Message;
        }

        // A hash that does not match what was downloaded.
        Assert.Contains("does not match the SHA-256", await Refusal(good, "sha256:" + Sha256([1, 2, 3])));
        // No hash anywhere: nothing is even downloaded.
        github.Requests.Clear();
        Assert.Contains("lists no SHA-256", await Refusal(good, null));
        Assert.DoesNotContain(github.Requests, request => request.RequestUri!.AbsoluteUri == address);
        // The other kind of package, a package without its marker or executable, and not a zip at all.
        var selfContained = PackageZip("win-x64-self-contained", "0.2.0");
        Assert.Contains("not the win-x64 package", await Refusal(selfContained, "sha256:" + Sha256(selfContained)));
        var unmarked = PackageZip("win-x64", "0.2.0", marker: false);
        Assert.Contains("not a published WpfStudio package", await Refusal(unmarked, "sha256:" + Sha256(unmarked)));
        var headless = PackageZip("win-x64", "0.2.0", executable: false);
        Assert.Contains("holds no WpfStudio.exe", await Refusal(headless, "sha256:" + Sha256(headless)));
        byte[] text = "<html>not a zip</html>"u8.ToArray();
        Assert.Contains("not a readable zip", await Refusal(text, "sha256:" + Sha256(text)));
    }

    // ------------------------------------------------------------------ Applying

    private string Staged(params (string Relative, string Text)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "staged-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        foreach (var (relative, text) in files.Append(("WpfStudio.package.json", """{ "package": "win-x64", "repository": "contoso/WpfStudio" }""")))
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return folder;
    }

    private static Dictionary<string, string> Snapshot(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(folder, path).StartsWith(UpdateApplier.BackupFolderName, StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(folder, path).Replace('\\', '/'), File.ReadAllText);

    [Fact]
    public async Task ReplacesThePackageKeepsWhatIsNotItsAndBacksUpTheOldFiles()
    {
        var target = Installed();
        Directory.CreateDirectory(Path.Combine(target, "data"));
        File.WriteAllText(Path.Combine(target, "data", "settings.json"), "mine"); // A data folder kept beside the program.
        File.WriteAllText(Path.Combine(target, "Retired.dll"), "old only");
        var source = Staged(("WpfStudio.exe", "exe 0.2.0"), ("WpfStudio.dll", "app 0.2.0"), ("WorkspaceHost/Worker.dll", "worker 0.2.0"));
        var progress = new Recorder();

        await new UpdateApplier().ApplyAsync(source, target, progress, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string>
        {
            ["WpfStudio.exe"] = "exe 0.2.0", ["WpfStudio.dll"] = "app 0.2.0", ["WorkspaceHost/Worker.dll"] = "worker 0.2.0",
            ["WpfStudio.package.json"] = """{ "package": "win-x64", "repository": "contoso/WpfStudio" }""",
            ["data/settings.json"] = "mine", ["Retired.dll"] = "old only",
        }, Snapshot(target));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(target, UpdateApplier.BackupFolderName)));
        Assert.Equal("exe 0.1.0", File.ReadAllText(Path.Combine(backup, "WpfStudio.exe")));
        Assert.Equal(1, progress.Reports[^1].Fraction);
        Assert.True(File.Exists(Path.Combine(source, "WpfStudio.exe"))); // Copied, not moved: the staged executable is the one running.

        UpdateApplier.RemoveBackups(target);
        Assert.False(Directory.Exists(Path.Combine(target, UpdateApplier.BackupFolderName)));
    }

    [Fact]
    public async Task AFileThatCannotBeReplacedLeavesThePreviousVersionExactlyAsItWas()
    {
        var target = Installed();
        File.WriteAllText(Path.Combine(target, "a.dll"), "a old");
        // A folder where the update has a file. Files go in ordinal order, so the copy fails after the marker and a.dll were
        // replaced and b.dll added, and before new.dll, z.dll and the executable.
        Directory.CreateDirectory(Path.Combine(target, "m.dll"));
        var before = Snapshot(target);
        var source = Staged(("WpfStudio.exe", "exe new"), ("a.dll", "a new"), ("b.dll", "b new"), ("new.dll", "added"), ("m.dll", "m new"), ("z.dll", "z new"));

        var failure = await Assert.ThrowsAsync<UpdateException>(() => new UpdateApplier(TimeSpan.FromMilliseconds(200)).ApplyAsync(source, target, null, CancellationToken.None));

        Assert.Contains("previous version was kept", failure.Message);
        Assert.Equal(before, Snapshot(target));
        Assert.True(Directory.Exists(Path.Combine(target, "m.dll")));
        Assert.Empty(Directory.Exists(Path.Combine(target, UpdateApplier.BackupFolderName)) ? Directory.GetFiles(Path.Combine(target, UpdateApplier.BackupFolderName), "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task CancellingPutsEverythingBack()
    {
        var target = Installed();
        var before = Snapshot(target);
        var source = Staged(("WpfStudio.exe", "exe new"), ("WpfStudio.dll", "app new"));
        using var cancelled = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancelled);

        await Assert.ThrowsAsync<UpdateException>(() => new UpdateApplier().ApplyAsync(source, target, progress, cancelled.Token));
        Assert.Equal(before, Snapshot(target));
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource source) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => source.Cancel();
    }

    [Fact]
    public async Task OnlyEverReplacesAnInstallationWithACompletePackage()
    {
        var target = Installed();
        var source = Staged(("WpfStudio.exe", "exe new"));
        var applier = new UpdateApplier();

        Assert.Contains("holds no WpfStudio.exe", (await Assert.ThrowsAsync<UpdateException>(() => applier.ApplyAsync(source, Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName, null, CancellationToken.None))).Message);
        Assert.Contains("not a complete", (await Assert.ThrowsAsync<UpdateException>(() => applier.ApplyAsync(Installed(marker: false), target, null, CancellationToken.None))).Message);
        Assert.Contains("unpacked in", (await Assert.ThrowsAsync<UpdateException>(() => applier.ApplyAsync(source, source, null, CancellationToken.None))).Message);
        Assert.Contains("unpacked in", (await Assert.ThrowsAsync<UpdateException>(() => applier.ApplyAsync(source, Path.Combine(source, "inner"), null, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task WaitsForTheClosingProcess()
    {
        Assert.True(await UpdateApplier.WaitForExitAsync(int.MaxValue - 7, TimeSpan.FromSeconds(1), CancellationToken.None)); // Long gone.
        Assert.False(await UpdateApplier.WaitForExitAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        using var shortLived = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        Assert.True(await UpdateApplier.WaitForExitAsync(shortLived.Id, TimeSpan.FromSeconds(60), CancellationToken.None));
    }

    [Fact]
    public void TheCommandLinesBetweenTheProcessesRoundTrip()
    {
        var staged = new StagedUpdate(ReleaseVersion.Parse("0.2.0"), Path.Combine(_root, "updates", "0.2.0", "package", "WpfStudio"));
        var target = Path.Combine(_root, "Program Files", "WpfStudio");
        var apply = UpdateCommandLine.ApplyStart(staged, target, 4242, "0.1.0");

        Assert.Equal(staged.Executable, apply.FileName);
        Assert.Equal(staged.Directory, apply.WorkingDirectory);
        Assert.True(UpdateCommandLine.TryParseApply(apply.ArgumentList.ToArray(), out var request));
        Assert.Equal(new ApplyRequest(target, 4242, "0.1.0"), request);
        // AppContext.BaseDirectory ends with a separator; the folder is passed without one.
        Assert.Equal(target, UpdateCommandLine.ApplyStart(staged, target + Path.DirectorySeparatorChar, 4242, "0.1.0").ArgumentList[1]);
        Assert.Equal(0, UpdateApplier.CountOtherInstances(target));

        Assert.False(UpdateCommandLine.TryParseApply([], out _));
        Assert.False(UpdateCommandLine.TryParseApply([Path.Combine(_root, "Solution.sln")], out _));
        Assert.False(UpdateCommandLine.TryParseApply([UpdateCommandLine.ApplyOption, "relative", "4242", "0.1.0"], out _));
        Assert.False(UpdateCommandLine.TryParseApply([UpdateCommandLine.ApplyOption, target, "-1", "0.1.0"], out _));
        Assert.False(UpdateCommandLine.TryParseApply([UpdateCommandLine.ApplyOption, target, "4242"], out _));

        var restart = UpdateCommandLine.RestartStart(target, "0.1.0");
        Assert.Equal(Path.Combine(target, "WpfStudio.exe"), restart.FileName);
        Assert.Equal(target, restart.WorkingDirectory);
        Assert.Equal("0.1.0", UpdateCommandLine.UpdatedFrom(restart.ArgumentList.ToArray()));
        Assert.Empty(UpdateCommandLine.RestartStart(target, null).ArgumentList); // The update failed: the previous version starts as usual.
        Assert.Null(UpdateCommandLine.UpdatedFrom([Path.Combine(_root, "Solution.sln")]));
        Assert.Null(UpdateCommandLine.UpdatedFrom([UpdateCommandLine.UpdatedFromOption]));
    }
}
