using System.Net;
using System.Net.Http;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using WpfStudio.App.Features.Packages;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class PackageManagementTests
{
    [Fact]
    public void SourcesRespectDisabledEntriesAndDoNotInventNugetOrg()
    {
        var sources = NuGetPackageService.ParseSources("Registered Sources:\nE https://packages.example.test/v3/index.json\nD https://api.nuget.org/v3/index.json\nE C:\\Shared packages\\\n");
        Assert.Equal(2, sources.Count);
        Assert.DoesNotContain(sources, x => x.Address.Contains("nuget.org"));
        Assert.Equal(@"C:\Shared packages\", sources[1].Address);
    }

    [Fact]
    public void VersionsUseNumericSemverOrderingAndFilterPrerelease()
    {
        string[] versions = ["2.0.0-beta.2", "1.9.0", "2.0.0-beta.10", "1.10.0", "2.0.0", "1.10.0", "garbage"];
        Assert.Equal(["2.0.0", "2.0.0-beta.10", "2.0.0-beta.2", "1.10.0", "1.9.0"], NuGetPackageService.SortVersions(versions, true));
        Assert.Equal(["2.0.0", "1.10.0", "1.9.0"], NuGetPackageService.SortVersions(versions, false));
    }

    [Fact]
    public void CommandArgumentsNeverUseAShellOrConcatenateUserValues()
    {
        var path = Path.GetFullPath(@"project with spaces & punctuation\Example.csproj");
        var arguments = NuGetPackageService.CreateChangeArguments(path, "CommunityToolkit.Mvvm", "8.4.2", false);
        var start = PackageProcessRunner.CreateStartInfo(Path.GetDirectoryName(path)!, arguments);
        Assert.False(start.UseShellExecute);
        Assert.Equal("dotnet", start.FileName);
        Assert.Equal(path, start.ArgumentList[1]);
        Assert.Equal(["add", path, "package", "CommunityToolkit.Mvvm", "--version", "8.4.2"], start.ArgumentList);
        Assert.DoesNotContain("--source", start.ArgumentList); // Preserve configured source mapping and private feed credentials.
    }

    [Theory]
    [InlineData("--source", "1.0.0")]
    [InlineData("Example; calc", "1.0.0")]
    [InlineData("Example", "--interactive")]
    [InlineData("Example", "1.*")]
    public void InvalidPackageOrVersionIsRejectedBeforeProcessStart(string id, string version) =>
        Assert.Throws<ArgumentException>(() => NuGetPackageService.CreateChangeArguments("Example.csproj", id, version, false));

    [Fact]
    public void InstalledReferencesMergeFrameworksAndExcludeImplicitAndTransitivePackages()
    {
        const string json = """
        {"version":1,"projects":[{"frameworks":[
          {"framework":"net8.0","topLevelPackages":[{"id":"Example","requestedVersion":"[1.0,2.0)","resolvedVersion":"1.2.0"},{"id":"Implicit","autoReferenced":true,"resolvedVersion":"8.0.0"}],"transitivePackages":[{"id":"Transitive"}]},
          {"framework":"net10.0","topLevelPackages":[{"id":"Example","requestedVersion":"[1.0,2.0)","resolvedVersion":"1.2.0"}]}
        ]}]}
        """;
        var package = Assert.Single(NuGetPackageService.ParseInstalledPackages(json));
        Assert.Equal("Example", package.Id);
        Assert.Equal("net8.0, net10.0", package.Frameworks);
        Assert.Equal("[1.0,2.0)", package.RequestedVersion);
    }

    [Fact]
    public void DeclaredReferenceFallbackHandlesCentralVersionsAndOverrides()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Project, """
        <Project><ItemGroup><PackageReference Include="Example"/><PackageReference Include="Override" VersionOverride="2.3.4"/><PackageReference Include="Nested"><Version>4.5.6</Version></PackageReference></ItemGroup></Project>
        """);
        var central = Path.Combine(fixture.Root, "Directory.Packages.props");
        File.WriteAllText(central, """
        <Project><PropertyGroup><ExampleVersion>1.2.3</ExampleVersion></PropertyGroup><ItemGroup><PackageVersion Include="Example" Version="$(ExampleVersion)"/><PackageVersion Include="Override" Version="1.0.0"/></ItemGroup></Project>
        """);
        var packages = NuGetPackageService.ReadProjectReferences(fixture.Project, central);
        Assert.Equal("1.2.3", packages.Single(x => x.Id == "Example").RequestedVersion);
        Assert.Equal("2.3.4", packages.Single(x => x.Id == "Override").RequestedVersion);
        Assert.All(packages, x => Assert.Equal("Not evaluated", x.ResolvedVersion));
    }

    [Theory]
    [InlineData("8.0.425", false)]
    [InlineData("10.0.400", true)]
    public async Task ReadDoesNotRestoreImplicitlyAndUsesTheProjectSdk(string sdk, bool noRestore)
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner((args, _) => Task.FromResult(new PackageProcessResult(0,
            args[0] == "nuget" ? "E https://api.nuget.org/v3/index.json" : args[0] == "--version" ? sdk : "{\"projects\":[]}")));
        using var service = new NuGetPackageService(new HttpClient(new HttpStub(_ => "{}")), runner);
        await service.ReadProjectAsync(fixture.Project, default);
        var command = runner.Calls.Single(x => x.Args[0] == "list");
        Assert.Equal(fixture.Root, command.Directory);
        Assert.Equal(noRestore, command.Args.Contains("--no-restore"));
        Assert.DoesNotContain(runner.Calls, x => x.Args[0] == "restore");
    }

    [Fact]
    public async Task BrowsingDiscoversFeedEndpointsAndEncodesSearchQuery()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new HttpStub(uri =>
        {
            requests.Add(uri);
            return uri.AbsolutePath == "/v3/index.json"
                ? """{"resources":[{"@id":"https://feed.test/query","@type":"SearchQueryService/3.5.0"},{"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}"""
                : """{"data":[{"id":"Example","version":"1.2.3","description":"A package","authors":["Author"],"versions":[{"version":"1.2.3"}],"totalDownloads":42}]}""";
        }));
        using var service = new NuGetPackageService(http, new FakeRunner());
        var package = Assert.Single(await service.SearchAsync(new("Custom", "https://feed.test/v3/index.json"), "WPF & MVVM", false, default));
        Assert.Equal("Example", package.Id);
        Assert.Equal("Author", package.Authors);
        Assert.Contains("q=WPF%20%26%20MVVM", requests[1].AbsoluteUri);
        Assert.Contains("semVerLevel=2.0.0", requests[1].Query);
    }

    [Fact]
    public async Task RemoveRestoresAssetsAfterChangingTheReference()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        using var service = new NuGetPackageService(new HttpClient(new HttpStub(_ => "{}")), runner);
        var result = await service.ChangeAsync(fixture.Project, "Example", null, true, null, default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "remove", "restore" }, runner.Calls.Select(x => x.Args[0]));
    }

    [Fact]
    public async Task RealCliInstallsUpdatesAndRemovesACentrallyManagedPackageFromALocalFeed()
    {
        using var fixture = new Fixture();
        var feed = Path.Combine(fixture.Root, "feed"); Directory.CreateDirectory(feed);
        foreach (var version in new[] { "1.0.0", "1.1.0" })
        {
            using var archive = ZipFile.Open(Path.Combine(feed, $"WpfStudio.LocalTest.{version}.nupkg"), ZipArchiveMode.Create);
            using (var writer = new StreamWriter(archive.CreateEntry("WpfStudio.LocalTest.nuspec").Open()))
                writer.Write($"<package><metadata><id>WpfStudio.LocalTest</id><version>{version}</version><authors>WpfStudio</authors><description>Disposable test package</description></metadata></package>");
            archive.CreateEntry("lib/net10.0/_._");
        }
        new XDocument(new XElement("configuration",
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", Path.Combine(fixture.Root, "cache")))),
            new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "LocalTest"), new XAttribute("value", feed))),
            new XElement("packageSourceMapping", new XElement("clear"), new XElement("packageSource", new XAttribute("key", "LocalTest"), new XElement("package", new XAttribute("pattern", "*")))),
            new XElement("auditSources", new XElement("clear")))).Save(Path.Combine(fixture.Root, "NuGet.Config"));
        var central = Path.Combine(fixture.Root, "Directory.Packages.props");
        File.WriteAllText(central, "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var service = new NuGetPackageService();
        var installed = await service.ChangeAsync(fixture.Project, "WpfStudio.LocalTest", "1.0.0", false, null, timeout.Token);
        Assert.True(installed.ExitCode == 0, installed.Output);
        var inventory = await service.ReadProjectAsync(fixture.Project, timeout.Token);
        Assert.Null(inventory.Warning);
        Assert.Equal("1.0.0", Assert.Single(inventory.Packages).ResolvedVersion);
        Assert.Equal(central, inventory.CentralVersionsFile);
        var updated = await service.ChangeAsync(fixture.Project, "WpfStudio.LocalTest", "1.1.0", false, null, timeout.Token);
        Assert.True(updated.ExitCode == 0, updated.Output);
        Assert.Equal("1.1.0", XDocument.Load(central).Descendants("PackageVersion").Single().Attribute("Version")!.Value);
        Assert.Null(XDocument.Load(fixture.Project).Descendants("PackageReference").Single().Attribute("Version"));
        var removed = await service.ChangeAsync(fixture.Project, "WpfStudio.LocalTest", null, true, null, timeout.Token);
        Assert.True(removed.ExitCode == 0, removed.Output);
        Assert.Empty((await service.ReadProjectAsync(fixture.Project, timeout.Token)).Packages);
        Assert.Single(XDocument.Load(central).Descendants("PackageVersion"));
    }

    [Fact]
    public async Task OpeningWorkspaceDoesNotStartPackageWorkUntilPaneIsActivated()
    {
        using var fixture = new Fixture();
        var service = new FakeService();
        using var vm = CreateVm(service, fixture, () => Task.FromResult(true));
        Assert.Equal(0, service.ReadCount);
        await vm.ActivateAsync();
        Assert.Equal(1, service.ReadCount);
        Assert.Single(vm.Installed);
    }

    [Fact]
    public async Task FailedSavePreventsPackageMutationAndWorkspaceReload()
    {
        using var fixture = new Fixture();
        var service = new FakeService(); var reloads = 0;
        using var vm = CreateVm(service, fixture, () => Task.FromResult(false), () => { reloads++; return Task.CompletedTask; });
        vm.PackageId = "Example"; vm.SelectedVersion = "1.0.0";
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, service.ChangeCount); Assert.Equal(0, reloads);
        Assert.Contains("could not be saved", vm.Status);
    }

    [Fact]
    public async Task DecliningRemovalDoesNotSaveOrMutate()
    {
        using var fixture = new Fixture();
        var service = new FakeService(); var saves = 0;
        using var vm = CreateVm(service, fixture, () => { saves++; return Task.FromResult(true); }, dialogs: new FakeDialogs(false));
        await vm.ActivateAsync(); vm.SelectedInstalled = vm.Installed.Single();
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(0, service.ChangeCount); Assert.Equal(0, saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCliStillInvalidatesWorkspace(bool cancelled)
    {
        using var fixture = new Fixture();
        var service = new FakeService { Change = _ => cancelled ? Task.FromException<PackageProcessResult>(new OperationCanceledException()) : Task.FromResult(new PackageProcessResult(1, "NU1101 restore failed")) };
        var reloads = 0;
        using var vm = CreateVm(service, fixture, () => Task.FromResult(true), () => { reloads++; return Task.CompletedTask; });
        vm.PackageId = "Example"; vm.SelectedVersion = "1.0.0";
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, reloads); Assert.False(vm.IsBusy);
        Assert.Contains(cancelled ? "cancelled" : "code 1", vm.Status);
    }

    [Fact]
    public async Task PendingMutationDisablesOtherCommandsAndCanBeCancelled()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Change = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(0, ""); } };
        using var vm = CreateVm(service, fixture, () => Task.FromResult(true));
        vm.PackageId = "Example"; vm.SelectedVersion = "1.0.0";
        var pending = vm.InstallCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.False(vm.RefreshCommand.CanExecute(null)); Assert.False(vm.InstallCommand.CanExecute(null)); Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await pending;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DisposingDuringMutationCancelsTheCliWithoutReloadingTheClosingWorkspace()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Change = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(0, ""); } };
        var reloads = 0;
        var vm = CreateVm(service, fixture, () => Task.FromResult(true), () => { reloads++; return Task.CompletedTask; });
        vm.PackageId = "Example"; vm.SelectedVersion = "1.0.0";
        var pending = vm.InstallCommand.ExecuteAsync(null);
        await entered.Task;
        vm.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, service.ChangeCount);
        Assert.Equal(0, reloads);
        Assert.False(vm.IsBusy);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingFeedDiscardsAnUncancellableOldSearchResponse()
    {
        using var fixture = new Fixture();
        var response = new TaskCompletionSource<IReadOnlyList<PackageSearchItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Search = _ => response.Task };
        using var vm = CreateVm(service, fixture, () => Task.FromResult(true));
        await vm.ActivateAsync();
        var searching = vm.SearchCommand.ExecuteAsync(null);
        vm.SelectedSource = new("Different", "https://different.test/v3/index.json");
        response.SetResult([new("Stale", "1.0.0", "", "", 0, null, ["1.0.0"])]);
        await searching;
        Assert.Empty(vm.SearchResults);
        Assert.Contains("cancelled", vm.Status);
    }

    [Fact]
    public async Task SwitchingWorkspaceCancelsTheOldInventoryAndRefreshesTheNewProject()
    {
        using var first = new Fixture(); using var second = new Fixture();
        var response = new TaskCompletionSource<PackageInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Read = (path, _) => path == first.Project ? response.Task : Task.FromResult(new PackageInventory([new("Second", "1.0.0", "1.0.0", "net10.0")], [], [], null, null)) };
        using var vm = CreateVm(service, first, () => Task.FromResult(true));
        var reading = vm.ActivateAsync();
        vm.SetWorkspace(new(second.Project, "10.0.400", [new("other", "Second", second.Project, "net10.0", null, false, [])], []));
        response.SetResult(new([new("Stale", "1.0.0", "1.0.0", "net10.0")], [], [], null, null));
        await reading;
        Assert.Equal("Second", Assert.Single(vm.Installed).Id);
        Assert.Equal(second.Project, vm.SelectedProject!.ProjectPath);
    }

    private static PackagesViewModel CreateVm(FakeService service, Fixture fixture, Func<Task<bool>> save,
        Func<Task>? changed = null, IUserDialogService? dialogs = null)
    {
        var vm = new PackagesViewModel(service, dialogs ?? new FakeDialogs(true), save, changed ?? (() => Task.CompletedTask));
        vm.SetWorkspace(new(fixture.Project, "10.0.400", [new("id", "Example", fixture.Project, "net10.0", null, false, [])], []));
        return vm;
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, CancellationToken, Task<PackageProcessResult>>? run = null) : IPackageProcessRunner
    {
        public List<(string Directory, IReadOnlyList<string> Args)> Calls { get; } = [];
        public Task<PackageProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments, IProgress<string>? output, CancellationToken cancellationToken)
        { Calls.Add((directory, arguments)); return run?.Invoke(arguments, cancellationToken) ?? Task.FromResult(new PackageProcessResult(0, "OK")); }
    }
    private sealed class HttpStub(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!), Encoding.UTF8, "application/json") });
    }
    private sealed class FakeService : INuGetPackageService
    {
        public int ReadCount, ChangeCount;
        public Func<CancellationToken, Task<PackageProcessResult>>? Change { get; init; }
        public Func<CancellationToken, Task<IReadOnlyList<PackageSearchItem>>>? Search { get; init; }
        public Func<string, CancellationToken, Task<PackageInventory>>? Read { get; init; }
        public Task<PackageInventory> ReadProjectAsync(string projectPath, CancellationToken cancellationToken)
        { ReadCount++; return Read?.Invoke(projectPath, cancellationToken) ?? Task.FromResult(new PackageInventory([new("Example", "1.0.0", "1.0.0", "net10.0")], [new("nuget.org", "https://api.nuget.org/v3/index.json")], [], null, null)); }
        public Task<IReadOnlyList<PackageSearchItem>> SearchAsync(PackageSource source, string query, bool prerelease, CancellationToken cancellationToken) => Search?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<PackageSearchItem>>([]);
        public Task<IReadOnlyList<string>> GetVersionsAsync(PackageSource source, string packageId, bool prerelease, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["1.0.0"]);
        public Task<PackageProcessResult> ChangeAsync(string projectPath, string packageId, string? version, bool remove, IProgress<string>? output, CancellationToken cancellationToken)
        { ChangeCount++; return Change?.Invoke(cancellationToken) ?? Task.FromResult(new PackageProcessResult(0, "OK")); }
    }
    private sealed class FakeDialogs(bool confirm) : IUserDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(confirm);
        public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(SaveDecision.Cancel);
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-package-tests", Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "Example.csproj");
        public Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
