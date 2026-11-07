using System.Net;
using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Runtimes;

namespace WpfStudio.Core.Tests;

/// <summary>Which runtimes a workspace needs, which are missing, and finding and running their official installers.</summary>
public sealed class RuntimeRequirementTests
{
    private static WorkspaceProject Project(string name, string? framework, bool executable = true, IReadOnlyList<ProjectDependency>? dependencies = null, bool sdkStyle = true, string? moniker = null, params string[] files) =>
        new(name, name, $@"C:\repo\{name}\{name}.csproj", framework, null, executable, files.Select(file => new WorkspaceFile($@"C:\repo\{name}\{file}", file, "Compile")).ToArray(),
            IsSdkStyle: sdkStyle, TargetFrameworkMoniker: moniker, Dependencies: dependencies);
    private static ProjectDependency Framework(string name) => new("Framework", name);
    private static ProjectDependency Package(string name) => new("Package", name, "1.0");

    [Theory]
    [InlineData("net8.0", "8.0")]
    [InlineData("net8.0-windows", "8.0")]
    [InlineData("net10.0-windows10.0.19041.0", "10.0")]
    [InlineData("netcoreapp3.1", "3.1")]
    [InlineData("net48", null)]
    [InlineData("net472", null)]
    [InlineData("netstandard2.0", null)]
    [InlineData("net4.0", null)]
    public void TheChannelComesFromTheTargetFramework(string moniker, string? channel) => Assert.Equal(channel, RuntimeRequirements.ChannelOf(moniker)?.ToString(2));

    [Fact]
    public void ProgramsAndTestsNeedTheirRuntimesAndLibrariesNeedNone()
    {
        var required = RuntimeRequirements.For(
        [
            Project("Shop.App", "net8.0-windows", dependencies: [Framework("net8.0-windows"), Framework("Microsoft.WindowsDesktop.App.WPF")]),
            Project("Shop.Core", "net8.0", executable: false),
            Project("Shop.Tests", "net9.0", executable: false, dependencies: [Framework("net9.0"), Package("Microsoft.NET.Test.Sdk")]),
            Project("Shop.Api", "net10.0", dependencies: [Framework("net10.0"), Framework("Microsoft.AspNetCore.App")]),
            Project("Legacy", "net48", sdkStyle: false, moniker: ".NETFramework,Version=v4.8"),
            // Not evaluated yet, so only its target frameworks and its markup are known.
            Project("Tool", null, dependencies: [Framework("net8.0-windows"), Framework("net9.0-windows")], files: "MainWindow.xaml"),
        ]);

        Assert.Equal(
        [
            "Microsoft.AspNetCore.App 10.0: Shop.Api", "Microsoft.NETCore.App 10.0: Shop.Api",
            "Microsoft.NETCore.App 9.0: Shop.Tests, Tool", "Microsoft.WindowsDesktop.App 9.0: Tool",
            "Microsoft.NETCore.App 8.0: Shop.App, Tool", "Microsoft.WindowsDesktop.App 8.0: Shop.App, Tool",
        ], required.Select(requirement => $"{requirement.Framework} {requirement.Channel.ToString(2)}: {string.Join(", ", requirement.Projects)}"));
        Assert.Equal(".NET Desktop Runtime 8.0", required.Last().Title);
    }

    private static readonly DotNetInventory Installed = RuntimeRequirements.ParseInventory("""
        Microsoft.AspNetCore.App 9.0.8 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 3.1.32 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.NETCore.App 9.0.8 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.NETCore.App 11.0.0-rc.3.26522.148 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.WindowsDesktop.App 8.0.19 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
        """, """
        9.0.300 [C:\Program Files\dotnet\sdk]
        11.0.100-rc.3.26522.148 [C:\Program Files\dotnet\sdk]
        """);

    [Fact]
    public void TheListingsReadAsRuntimesAndSdks()
    {
        Assert.Equal(5, Installed.Runtimes.Count);
        Assert.Equal(new InstalledRuntime("Microsoft.NETCore.App", "11.0.0-rc.3.26522.148"), Installed.Runtimes[3]);
        Assert.Equal(["9.0.300", "11.0.100-rc.3.26522.148"], Installed.Sdks);
    }

    [Fact]
    public void ARuntimeIsMetBySameMajorVersionAndTheDesktopInstallerCoversTheBaseOne()
    {
        RuntimeRequirement Need(string framework, int major, int minor, params string[] projects) => new(framework, new Version(major, minor), projects);
        var missing = RuntimeRequirements.Missing(
        [
            Need(SharedFrameworks.Core, 3, 0, "Old"),            // 3.1 rolls forward from 3.0
            Need(SharedFrameworks.Core, 8, 0, "App"),            // 9.0 does not stand in for 8.0
            Need(SharedFrameworks.Desktop, 8, 0, "App"),         // installed
            Need(SharedFrameworks.Core, 10, 0, "Next", "Tests"),
            Need(SharedFrameworks.Desktop, 10, 0, "Next"),       // brings the 10.0 base runtime with it
            Need(SharedFrameworks.AspNet, 9, 0, "Api"),          // installed
            Need(SharedFrameworks.Core, 11, 0, "Preview"),       // a preview counts
        ], Installed);

        Assert.Equal(["Microsoft.NETCore.App 8.0: App", "Microsoft.WindowsDesktop.App 10.0: Next, Tests"],
            missing.Select(requirement => $"{requirement.Framework} {requirement.Channel.ToString(2)}: {string.Join(", ", requirement.Projects)}"));
    }

    [Fact]
    public void LegacyFrameworkProjectsNeedTheirReferenceAssemblies()
    {
        var missing = RuntimeRequirements.MissingTargetingPacks(
        [
            Project("Old", "net472", sdkStyle: false, moniker: ".NETFramework,Version=v4.7.2"),
            Project("Older", "net472", sdkStyle: false, moniker: ".NETFramework,Version=v4.7.2"),
            Project("Fine", "net48", sdkStyle: false, moniker: ".NETFramework,Version=v4.8"),
            Project("Sdk", "net472", sdkStyle: true, moniker: ".NETFramework,Version=v4.7.2"),
        ], path => path.EndsWith("v4.8", StringComparison.Ordinal));

        var pack = Assert.Single(missing);
        Assert.Equal(("4.7.2", ".NET Framework 4.7.2 Developer Pack", "https://dotnet.microsoft.com/download/dotnet-framework/net472"), (pack.Version, pack.Title, pack.DownloadPage.AbsoluteUri));
        Assert.Equal(["Old", "Older"], pack.Projects);
    }

    [Fact]
    public void GlobalJsonGivesTheSdkVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), "WpfStudio-global-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ /* pinned */ "sdk": { "version": "8.0.100", "rollForward": "latestFeature", }, }""");
            Assert.Equal("8.0.100", RuntimeRequirements.ReadGlobalJsonVersion(path));
            File.WriteAllText(path, """{ "msbuild-sdks": {} }""");
            Assert.Null(RuntimeRequirements.ReadGlobalJsonVersion(path));
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ Release metadata and installers

    private const string Index = """
        { "releases-index": [
          { "channel-version": "11.0", "release-type": "sts", "support-phase": "go-live", "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json" },
          { "channel-version": "10.0", "release-type": "lts", "support-phase": "active", "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json" },
          { "channel-version": "8.0", "release-type": "lts", "support-phase": "eol", "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json" },
          { "channel-version": "7.0", "release-type": "sts", "support-phase": "eol", "releases.json": "https://example.com/7.0/releases.json" }
        ] }
        """;

    private static string Hash(char digit) => new(digit, 128);
    private static string Asset(string name, string rid, string host = "builds.dotnet.microsoft.com", char hash = 'a') =>
        $$"""{ "name": "{{name}}", "rid": "{{rid}}", "url": "https://{{host}}/dotnet/{{name}}", "hash": "{{Hash(hash)}}" }""";

    private static readonly string Channel8 = $$"""
        { "releases": [
          { "release-version": "8.0.33",
            "runtime": { "version": "8.0.33", "files": [ {{Asset("dotnet-runtime-win-x64.exe", "win-x64")}}, {{Asset("dotnet-runtime-win-x64.zip", "win-x64")}} ] },
            "windowsdesktop": { "version": "8.0.33", "files": [ {{Asset("windowsdesktop-runtime-win-arm64.exe", "win-arm64")}}, {{Asset("windowsdesktop-runtime-win-x64.exe", "win-x64", host: "evil.example.com")}} ] },
            "sdk": { "version": "8.0.427", "files": [ {{Asset("dotnet-sdk-win-x64.exe", "win-x64", hash: 'c')}} ] },
            "sdks": [ { "version": "8.0.427", "files": [ {{Asset("dotnet-sdk-win-x64.exe", "win-x64", hash: 'c')}} ] }, { "version": "8.0.133", "files": [ {{Asset("dotnet-sdk-8.0.133-win-x64.exe", "win-x64", hash: 'd')}} ] } ] },
          { "release-version": "8.0.32",
            "windowsdesktop": { "version": "8.0.32", "files": [ {{Asset("windowsdesktop-runtime-win-x64.exe", "win-x64", hash: 'b')}} ] },
            "sdks": [ { "version": "8.0.132", "files": [ {{Asset("dotnet-sdk-8.0.132-win-x64.exe", "win-x64", hash: 'e')}} ] } ] }
        ] }
        """;

    private static readonly string Channel10 = $$"""
        { "releases": [ { "release-version": "10.0.14", "sdk": { "version": "10.0.403", "files": [ {{Asset("dotnet-sdk-win-x64.exe", "win-x64", hash: 'f')}} ] } } ] }
        """;

    private sealed class Server(Func<Uri, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static DotNetReleases Releases() => new(new HttpClient(new Server(url => url.AbsolutePath switch
    {
        "/dotnet/release-metadata/releases-index.json" => Json(Index),
        "/dotnet/release-metadata/8.0/releases.json" => Json(Channel8),
        "/dotnet/release-metadata/10.0/releases.json" => Json(Channel10),
        _ => null
    })));

    [Fact]
    public async Task ARuntimeInstallerIsTheNewestTrustedOneForTheMachine()
    {
        var core = await Releases().FindRuntimeAsync(SharedFrameworks.Core, new Version(8, 0), "win-x64", CancellationToken.None);
        Assert.Equal((".NET Runtime 8.0.33 (x64)", "8.0.33", "dotnet-runtime-win-x64.exe", true), (core.Title, core.Version, core.FileName, core.OutOfSupport));
        Assert.Equal(Hash('a'), core.Sha512);

        // The newest release's x64 desktop installer points at another host, so the one before it is offered.
        var desktop = await Releases().FindRuntimeAsync(SharedFrameworks.Desktop, new Version(8, 0), "win-x64", CancellationToken.None);
        Assert.Equal(("8.0.32", Hash('b')), (desktop.Version, desktop.Sha512));
        Assert.Equal("builds.dotnet.microsoft.com", desktop.Url.Host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Releases().FindRuntimeAsync(SharedFrameworks.AspNet, new Version(8, 0), "win-x64", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Releases().FindRuntimeAsync(SharedFrameworks.Core, new Version(6, 0), "win-x64", CancellationToken.None));
        Assert.Contains("over HTTPS", (await Assert.ThrowsAsync<InvalidOperationException>(() => Releases().FindRuntimeAsync(SharedFrameworks.Core, new Version(7, 0), "win-x64", CancellationToken.None))).Message);
    }

    [Theory]
    [InlineData("8.0.133", "8.0.133", 'd')]   // listed
    [InlineData("8.0.130", "8.0.133", 'd')]   // the newest of its feature band
    [InlineData("8.0.400", "8.0.427", 'c')]
    [InlineData(null, "10.0.403", 'f')]       // the newest supported LTS
    public async Task AnSdkInstallerMatchesGlobalJsonsFeatureBand(string? requested, string version, char hash)
    {
        var sdk = await Releases().FindSdkAsync(requested, "win-x64", CancellationToken.None);
        Assert.Equal((version, Hash(hash)), (sdk.Version, sdk.Sha512));
    }

    [Fact]
    public async Task AnSdkOutsideAnyListedBandIsNotGuessed() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Releases().FindSdkAsync("8.0.500", "win-x64", CancellationToken.None));

    private sealed class Launcher(int exitCode) : IInstallerLauncher
    {
        public List<(string Path, bool Existed, long Length)> Runs { get; } = [];
        public Task<int> RunAsync(string path, CancellationToken cancellationToken)
        {
            Runs.Add((path, System.IO.File.Exists(path), System.IO.File.Exists(path) ? new FileInfo(path).Length : 0));
            return Task.FromResult(exitCode);
        }
    }

    private static (RuntimeInstaller Installer, Launcher Launcher, string Folder, InstallerFile File) Installer(int exitCode, bool rightHash = true)
    {
        var payload = Encoding.UTF8.GetBytes("pretend installer " + Guid.NewGuid());
        var hash = Convert.ToHexStringLower(SHA512.HashData(payload));
        var file = new InstallerFile("Test runtime", "1.0.0", "runtime.exe", new Uri("https://builds.dotnet.microsoft.com/dotnet/runtime.exe"), rightHash ? hash : Hash('0'), false);
        var launcher = new Launcher(exitCode);
        var folder = Path.Combine(Path.GetTempPath(), "WpfStudio-installers-" + Guid.NewGuid().ToString("N"));
        var http = new HttpClient(new Server(url => url == file.Url ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) } : null));
        return (new RuntimeInstaller(http, launcher, folder), launcher, folder, file);
    }

    [Theory]
    [InlineData(0, InstallOutcome.Installed)]
    [InlineData(3010, InstallOutcome.RestartNeeded)]
    [InlineData(1602, InstallOutcome.Cancelled)]
    [InlineData(1603, InstallOutcome.Failed)]
    public async Task AVerifiedInstallerRunsAndIsDeletedAfterwards(int exitCode, InstallOutcome outcome)
    {
        var (installer, launcher, folder, file) = Installer(exitCode);
        try
        {
            var messages = new List<string>();
            var result = await installer.InstallAsync(file, new SynchronousProgress(messages.Add), CancellationToken.None);
            Assert.Equal((outcome, exitCode), (result.Outcome, result.ExitCode));
            var run = Assert.Single(launcher.Runs);
            Assert.True(run.Existed && run.Length > 0);
            Assert.False(System.IO.File.Exists(run.Path));
            Assert.Contains(messages, message => message.StartsWith("Installing Test runtime", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task AnInstallerWithTheWrongHashIsNeverRun()
    {
        var (installer, launcher, folder, file) = Installer(0, rightHash: false);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(file, null, CancellationToken.None));
            Assert.Contains("SHA-512", failure.Message);
            Assert.Empty(launcher.Runs);
            Assert.Empty(Directory.GetFiles(folder));
            await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(file with { Url = new Uri("http://builds.dotnet.microsoft.com/x.exe") }, null, CancellationToken.None));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
