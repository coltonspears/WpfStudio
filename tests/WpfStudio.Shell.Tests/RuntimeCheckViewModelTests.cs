using System.Net;
using System.Security.Cryptography;
using System.Text;
using WpfStudio.App.Features.Runtimes;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Runtimes;

namespace WpfStudio.Shell.Tests;

/// <summary>The missing-runtime banner against a pretend machine, release list and installer.</summary>
public sealed class RuntimeCheckViewModelTests
{
    private const string Workspace = @"C:\repo\Shop.sln";
    private static readonly WorkspaceProject App = new("app", "Shop.App", @"C:\repo\Shop.App\Shop.App.csproj", "net8.0-windows", null, true, [],
        Dependencies: [new("Framework", "net8.0-windows"), new("Framework", "Microsoft.WindowsDesktop.App.WPF")]);
    private static readonly WorkspaceProject Legacy = new("old", "Old.Tool", @"C:\repo\Old\Old.Tool.csproj", "net472", null, true, [], IsSdkStyle: false, TargetFrameworkMoniker: ".NETFramework,Version=v4.7.2");

    private sealed class Machine : IDotNetInventorySource
    {
        public List<InstalledRuntime> Runtimes { get; } = [new(SharedFrameworks.Core, "9.0.8")];
        public SdkRequirement? MissingSdk { get; set; }
        public Task<DotNetInventory> ReadAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new DotNetInventory([.. Runtimes], ["9.0.300"]));
        public Task<SdkRequirement?> FindMissingSdkAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(MissingSdk);
        public bool DirectoryExists(string path) => false;
    }

    private sealed class Launcher(Machine machine) : IInstallerLauncher
    {
        public int Runs { get; private set; }
        public Task<int> RunAsync(string path, CancellationToken cancellationToken)
        {
            Runs++;
            // The desktop installer brings the base runtime of its version with it.
            machine.Runtimes.Add(new(SharedFrameworks.Core, "8.0.33"));
            machine.Runtimes.Add(new(SharedFrameworks.Desktop, "8.0.33"));
            return Task.FromResult(0);
        }
    }

    private sealed class Host : IRuntimeCheckHost
    {
        public FakeDialogs Dialogs { get; } = new();
        public string Status { get; private set; } = "";
        public List<string> Opened { get; } = [];
        public void OpenUrl(string url) => Opened.Add(url);
        public string? WorkspacePath => Workspace;
        public IReadOnlyList<string> WorkspaceFiles => [];
        public IReadOnlyList<string> OpenDocumentPaths => [];
        public event Action? DocumentsChanged { add { } remove { } }
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => new Dictionary<string, string>();
        public (string Path, int Line)? ActiveLocation => null;
        public string? SelectedExplorerFile => null;
        IUserDialogService App.Features.Workbench.IWorkbenchHost.Dialogs => Dialogs;
        public CancellationToken Lifetime => CancellationToken.None;
        public async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { Status = ex.Message; } }
        public void ReportStatus(string status) => Status = status;
        public Task NavigateAsync(string path, int line, int column) => Task.CompletedTask;
        public Task OpenDocumentAsync(string path) => Task.CompletedTask;
        public Task<bool> CloseDocumentAsync(string path) => Task.FromResult(true);
    }

    private sealed class Server(Func<Uri, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request.RequestUri!) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static (RuntimeCheckViewModel Check, Host Host, Machine Machine, Launcher Launcher) Create()
    {
        var payload = Encoding.UTF8.GetBytes("desktop runtime installer");
        var hash = Convert.ToHexStringLower(SHA512.HashData(payload));
        var rid = DotNetReleases.MachineRid;
        var index = """{ "releases-index": [ { "channel-version": "8.0", "support-phase": "active", "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json" } ] }""";
        var channel = $$"""{ "releases": [ { "windowsdesktop": { "version": "8.0.33", "files": [ { "name": "windowsdesktop-runtime-{{rid}}.exe", "rid": "{{rid}}", "url": "https://builds.dotnet.microsoft.com/dotnet/wd.exe", "hash": "{{hash}}" } ] } } ] }""";
        var http = new HttpClient(new Server(url => url.AbsolutePath switch
        {
            "/dotnet/release-metadata/releases-index.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index) },
            "/dotnet/release-metadata/8.0/releases.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(channel) },
            "/dotnet/wd.exe" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) },
            _ => null
        }));
        var machine = new Machine();
        var launcher = new Launcher(machine);
        var host = new Host();
        var downloads = Path.Combine(Path.GetTempPath(), "WpfStudio-runtime-check-" + Guid.NewGuid().ToString("N"));
        var check = new RuntimeCheckViewModel(host, new RuntimeCheckServices(machine, new DotNetReleases(http), new RuntimeInstaller(http, launcher, downloads)));
        return (check, host, machine, launcher);
    }

    [Fact]
    public async Task AMissingRuntimeIsListedAndADismissalLastsForTheSameFindings()
    {
        var (check, _, machine, _) = Create();
        await check.CheckAsync(Workspace, [App, Legacy]);

        Assert.True(check.IsVisible);
        Assert.Equal("2 things this workspace needs aren't installed", check.Summary);
        var runtime = check.Missing[0];
        Assert.Equal(("Shop.App needs the .NET Desktop Runtime 8.0", true), (runtime.Title, runtime.CanInstall));
        Assert.Equal("https://dotnet.microsoft.com/download/dotnet/8.0", runtime.DownloadPage.AbsoluteUri);
        var pack = check.Missing[1];
        Assert.Equal(("Old.Tool needs the .NET Framework 4.7.2 Developer Pack", false), (pack.Title, pack.CanInstall));

        check.DismissCommand.Execute(null);
        Assert.False(check.IsVisible);
        await check.CheckAsync(Workspace, [App, Legacy]);
        Assert.False(check.IsVisible);
        // What dotnet said is kept, so a reload alone does not ask again; Check again does, and something new brings the banner back.
        machine.MissingSdk = new SdkRequirement("8.0.100", @"C:\repo\global.json", "A compatible .NET SDK was not found.");
        await check.CheckAsync(Workspace, [App, Legacy]);
        Assert.Equal(2, check.Missing.Count);
        await check.CheckAgainCommand.ExecuteAsync(null);
        Assert.True(check.IsVisible);
        Assert.Equal("global.json asks for the .NET SDK 8.0.100", check.Missing[0].Title);
        Assert.Contains(@"C:\repo\global.json", check.Missing[0].Detail);

        await check.CheckAsync(null, []);
        Assert.False(check.IsVisible);
        Assert.Empty(check.Missing);
    }

    [Fact]
    public async Task InstallingAsksFirstThenRunsTheVerifiedInstallerAndChecksAgain()
    {
        var (check, host, _, launcher) = Create();
        await check.CheckAsync(Workspace, [App]);
        var item = Assert.Single(check.Missing);
        var asked = new List<string>();
        host.Dialogs.WhileConfirming = title => { asked.Add(title); return Task.CompletedTask; };

        host.Dialogs.Confirmations.Enqueue(false);
        await check.InstallCommand.ExecuteAsync(item);
        Assert.Equal(0, launcher.Runs);
        Assert.Equal("", item.Status);
        Assert.StartsWith("Install .NET Desktop Runtime 8.0.33", Assert.Single(asked));

        host.Dialogs.Confirmations.Enqueue(true);
        await check.InstallCommand.ExecuteAsync(item);
        Assert.Equal(1, launcher.Runs);
        Assert.StartsWith("Installed .NET Desktop Runtime 8.0.33", host.Status);
        Assert.Empty(check.Missing);
        Assert.False(check.IsVisible);
        Assert.False(item.IsInstalling);
    }

    [Fact]
    public async Task ADeveloperPackOpensItsDownloadPage()
    {
        var (check, host, _, launcher) = Create();
        await check.CheckAsync(Workspace, [Legacy]);
        var pack = Assert.Single(check.Missing);
        await check.InstallCommand.ExecuteAsync(pack);
        Assert.Equal(0, launcher.Runs);
        check.OpenDownloadPageCommand.Execute(pack);
        Assert.Equal("https://dotnet.microsoft.com/download/dotnet-framework/net472", Assert.Single(host.Opened));
    }
}
