using WpfStudio.App.Features.DevOps;
using WpfStudio.App.Features.Git;
using WpfStudio.Contracts;
using WpfStudio.Core.DevOps;

namespace WpfStudio.Shell.Tests;

public sealed class PullRequestsPathContainmentTests
{
    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\file.cs")]
    public void OpenFileRejectsRootedAndEscapingPaths(string maliciousPath)
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "WpfStudio-PrPathTest");
        var host = new FakeDevOpsHost(repoRoot);
        using var viewModel = new PullRequestsViewModel(host);

        viewModel.OpenFileCommand.Execute(new DevOpsPullRequestFile(maliciousPath, null, FileChangeKind.Modified));

        Assert.Empty(host.OpenedFiles);
        Assert.Contains("outside the repository", host.LastStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenFileAllowsValidRelativePathInsideRepository()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "WpfStudio-PrPathTest");
        var host = new FakeDevOpsHost(repoRoot);
        using var viewModel = new PullRequestsViewModel(host);

        viewModel.OpenFileCommand.Execute(new DevOpsPullRequestFile("src/Login.cs", null, FileChangeKind.Modified));

        var opened = Assert.Single(host.OpenedFiles);
        Assert.Equal(Path.GetFullPath(Path.Combine(repoRoot, "src", "Login.cs")), opened);
    }

    private sealed class FakeDevOpsHost : IDevOpsHost
    {
        public FakeDevOpsHost(string repositoryRoot)
        {
            GitService = new GitService();
            Git = new GitViewModel(GitService, new FakeDesktopService()) { RepositoryRoot = repositoryRoot };
        }

        public List<string> OpenedFiles { get; } = [];
        public string LastStatus { get; private set; } = "";
        public IDevOpsProvider? Provider => null;
        public DevOpsIdentity? User => null;
        public DevOpsSettings Settings { get; } = new();
        public GitViewModel Git { get; }
        public GitService GitService { get; }
        public int Version => 0;
        public bool CanAct => false;
        public Task RunAsync(string label, Func<IDevOpsProvider, DevOpsIdentity, CancellationToken, Task> action, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ReportError(Exception exception) => LastStatus = exception.Message;
        public void ReportStatus(string status) => LastStatus = status;
        public void OpenUrl(string url) { }
        public void OpenFile(string path) => OpenedFiles.Add(path);
        public void Copy(string text, string what) { }
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public void ShowWorkItem(int id) { }
        public void ShowPullRequest(int number) { }
        public Task SaveSettingsAsync(DevOpsSettings settings) => Task.CompletedTask;
        public void StartCreatePullRequest() { }
        public void NotifyCommands() { }
    }

    private sealed class FakeDesktopService : IDesktopService
    {
        public void Reveal(string path) { }
        public void OpenUrl(string url) { }
    }
}
