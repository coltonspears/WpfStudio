using System.Diagnostics;
using WpfStudio.App.Features.Git;

namespace WpfStudio.Shell.Tests;

public sealed class GitIntegrationTests
{
    [Fact]
    public void PorcelainParserPreservesSpacesUnicodeAndRenameOrder()
    {
        var changes = GitService.ParseStatus(" M folder/space λ.cs\0R  new name.cs\0old name.cs\0?? note[1].txt\0UU conflict.cs\0");
        Assert.Equal(4, changes.Count);
        Assert.Equal("folder/space λ.cs", changes[0].Path);
        Assert.True(changes[0].IsUnstaged);
        Assert.False(changes[0].IsStaged);
        Assert.Equal("new name.cs", changes[1].Path);
        Assert.Equal("old name.cs", changes[1].OriginalPath);
        Assert.True(changes[1].IsStaged);
        Assert.True(changes[2].IsUnstaged);
        Assert.True(changes[3].IsConflict);
    }

    [Fact]
    public async Task UnbornStageAndUnstageOnlyChangeIndexAndUseLiteralPaths()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File("note[1] λ.txt"), "new file");
        await File.WriteAllTextAsync(fixture.File("note1 λ.txt"), "unrelated");
        var snapshot = await fixture.Service.LoadAsync(fixture.Root);
        Assert.Equal("main", snapshot.Branch);
        Assert.Empty(snapshot.History);
        await fixture.Service.StageAsync(fixture.Root, snapshot.Changes.Single(x => x.Path == "note[1] λ.txt"));
        snapshot = await fixture.Service.LoadAsync(fixture.Root);
        Assert.Single(snapshot.Changes, x => x.IsStaged);
        await fixture.Service.UnstageAsync(fixture.Root, snapshot.Changes.Single(x => x.IsStaged));
        snapshot = await fixture.Service.LoadAsync(fixture.Root);
        Assert.All(snapshot.Changes, x => Assert.False(x.IsStaged));
        Assert.Equal("new file", await File.ReadAllTextAsync(fixture.File("note[1] λ.txt")));
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.UnstageAsync(fixture.Root, null);
        Assert.True(File.Exists(fixture.File("note1 λ.txt")));
    }

    [Fact]
    public async Task CommitOnlyIncludesStagedContentAndUnstagePreservesLaterEdits()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        await File.WriteAllTextAsync(fixture.File("file.cs"), "staged content\n");
        var snapshot = await fixture.Service.LoadAsync(fixture.Root);
        await fixture.Service.StageAsync(fixture.Root, snapshot.Changes.Single());
        await File.WriteAllTextAsync(fixture.File("file.cs"), "later buffer content\n");
        await fixture.Service.CommitAsync(fixture.Root, "Only staged content\n\nSecond paragraph");
        Assert.Equal("staged content", (await fixture.RunAsync("show", "HEAD:file.cs")).Trim());
        snapshot = await fixture.Service.LoadAsync(fixture.Root);
        Assert.Single(snapshot.Changes);
        Assert.True(snapshot.Changes[0].IsUnstaged);
        Assert.False(snapshot.Changes[0].IsStaged);
        Assert.Equal("Only staged content", snapshot.History[0].Subject);
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.UnstageAsync(fixture.Root, null);
        Assert.Equal("later buffer content\n", await File.ReadAllTextAsync(fixture.File("file.cs")));
    }

    [Fact]
    public async Task RenamedUnicodeFileCanBeInspectedAndUnstaged()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File("old λ name.cs"), "class Original { }\n");
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.CommitAsync(fixture.Root, "Initial");
        await fixture.RunAsync("mv", "old λ name.cs", "new λ name.cs");
        var snapshot = await fixture.Service.LoadAsync(fixture.Root);
        var rename = Assert.Single(snapshot.Changes);
        Assert.Equal("old λ name.cs", rename.OriginalPath);
        Assert.Equal("new λ name.cs", rename.Path);
        var diff = await fixture.Service.GetDiffAsync(fixture.Root, rename, true);
        Assert.Contains("rename from old λ name.cs", diff.Text);
        await fixture.Service.UnstageAsync(fixture.Root, rename);
        Assert.True(File.Exists(fixture.File("new λ name.cs")));
        Assert.False(File.Exists(fixture.File("old λ name.cs")));
        Assert.All((await fixture.Service.LoadAsync(fixture.Root)).Changes, x => Assert.False(x.IsStaged));
    }

    [Fact]
    public async Task BranchSwitchRejectsDirtyFilesWithoutDiscardingThem()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        await fixture.Service.SwitchBranchAsync(fixture.Root, "feature/test", create: true);
        await File.WriteAllTextAsync(fixture.File("file.cs"), "keep this change");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchBranchAsync(fixture.Root, "main"));
        Assert.Contains("Commit or stash", error.Message);
        Assert.Equal("keep this change", await File.ReadAllTextAsync(fixture.File("file.cs")));
        Assert.Equal("feature/test", (await fixture.Service.LoadAsync(fixture.Root)).Branch);
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.CommitAsync(fixture.Root, "Retain change");
        await fixture.Service.SwitchBranchAsync(fixture.Root, "main");
        Assert.Equal("original\n", await File.ReadAllTextAsync(fixture.File("file.cs")));
    }

    [Fact]
    public async Task ViewModelAbortsStageWhenSavingFailsAndShowsDiffWithoutMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        await File.WriteAllTextAsync(fixture.File("file.cs"), "pending changes\n");
        using var model = new GitViewModel(fixture.Service, _ => Task.FromResult(false));
        await model.SetWorkspaceAsync(fixture.Root);
        await model.StageAllCommand.ExecuteAsync(null);
        Assert.Contains("could not be saved", model.Error);
        Assert.All((await fixture.Service.LoadAsync(fixture.Root)).Changes, x => Assert.False(x.IsStaged));
        GitDiff? opened = null;
        model.DiffRequested += diff => opened = diff;
        await model.ViewWorkingDiffCommand.ExecuteAsync(model.Unstaged.Single());
        Assert.NotNull(opened);
        Assert.Contains("+pending changes", opened.Text);
        Assert.Equal(2, model.SelectedTab);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task LocalRemoteFetchPullAndPushRespectFastForwardAndDirtyProtection()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        var remote = fixture.File("../remote.git");
        await fixture.RunAsync("init", "--bare", remote);
        await fixture.RunAsync("remote", "add", "origin", remote);
        await fixture.RunAsync("push", "--set-upstream", "origin", "main");
        await fixture.Service.FetchAsync(fixture.Root);
        await fixture.Service.PullAsync(fixture.Root);
        Assert.Contains("origin/main", (await fixture.Service.LoadAsync(fixture.Root)).Tracking);
        await File.WriteAllTextAsync(fixture.File("file.cs"), "new version\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PullAsync(fixture.Root));
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.CommitAsync(fixture.Root, "Second version");
        await fixture.Service.PushAsync(fixture.Root);
        Assert.Equal((await fixture.RunAsync("rev-parse", "HEAD")).Trim(), (await fixture.RunAsync("rev-parse", "origin/main")).Trim());
    }

    [Fact]
    public async Task MissingRepoAndCancellationHaveHelpfulBoundedOutcomes()
    {
        var service = new GitService();
        Assert.Null((await service.LoadAsync(null)).Root);
        using var fixture = await Fixture.CreateAsync();
        Assert.Contains("not in a Git", (await service.LoadAsync(fixture.Container)).Message);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(fixture.Root, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(fixture.Root, new("../outside.cs", null, '?', '?')));
    }

    [Fact]
    public async Task SuccessfulWorkingFileOperationsNotifyTheWorkspaceButFailuresDoNot()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        using var model = new GitViewModel(fixture.Service);
        await model.SetWorkspaceAsync(fixture.Root);
        var changed = 0;
        model.WorkspaceFilesChanged += () => changed++;
        model.NewBranchName = "feature/notifications";
        await model.CreateBranchCommand.ExecuteAsync(null);
        Assert.Equal(1, changed);
        model.SelectedBranch = "main";
        await model.SwitchBranchCommand.ExecuteAsync(null);
        Assert.Equal(2, changed);
        var remote = fixture.File("../notification-remote.git");
        await fixture.RunAsync("init", "--bare", remote);
        await fixture.RunAsync("remote", "add", "origin", remote);
        await fixture.RunAsync("push", "--set-upstream", "origin", "main");
        await model.PullCommand.ExecuteAsync(null);
        Assert.Equal(3, changed);
        await model.FetchCommand.ExecuteAsync(null);
        Assert.Equal(3, changed);
        await File.WriteAllTextAsync(fixture.File("file.cs"), "uncommitted\n");
        model.SelectedBranch = "feature/notifications";
        await model.SwitchBranchCommand.ExecuteAsync(null);
        Assert.Equal(3, changed);
        Assert.NotEmpty(model.Error);
        await model.StageAllCommand.ExecuteAsync(null);
        Assert.Equal(3, changed);
    }

    [Fact]
    public async Task WorkspaceSwitchCancelsPendingActionsAndDisablesOldRepositoryCommands()
    {
        using var first = await Fixture.CreateAsync();
        using var second = await Fixture.CreateAsync();
        await first.InitialCommitAsync();
        await second.InitialCommitAsync();
        await File.WriteAllTextAsync(first.File("file.cs"), "keep first workspace intact\n");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new GitViewModel(first.Service, async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        await model.SetWorkspaceAsync(first.Root);
        var stage = model.StageAllCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = model.SetWorkspaceAsync(second.Root);
        Assert.False(model.StageAllCommand.CanExecute(null));
        Assert.False(model.CommitCommand.CanExecute(null));
        await switching;
        await stage;
        Assert.Equal(second.Root, model.RepositoryRoot, ignoreCase: true);
        Assert.Empty(model.Unstaged);
        Assert.Empty(model.Staged);
        Assert.All((await first.Service.LoadAsync(first.Root)).Changes, change => Assert.False(change.IsStaged));
    }

    [Fact]
    public async Task CommitOutputCannotExposeCredentialsEmbeddedInItsSubject()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File("file.cs"), "example\n");
        await fixture.Service.StageAsync(fixture.Root, null);
        var output = await fixture.Service.CommitAsync(fixture.Root, "Test https://user:private-password@example.invalid/repo.git");
        Assert.DoesNotContain("private-password", output);
        Assert.DoesNotContain("user:", output);
        Assert.Contains("example.invalid/repo.git", output);
    }

    [Theory]
    [InlineData("fatal: https://alice:password%40secret@github.com/org/repo.git", "fatal: https://[redacted]@github.com/org/repo.git")]
    [InlineData("https://github_pat_privateToken@github.com/org/repo.git", "https://[redacted]@github.com/org/repo.git")]
    [InlineData("https://host/repo.git?access_token=secret&branch=main", "https://host/repo.git?access_token=[redacted]&branch=main")]
    [InlineData("Authorization: Bearer secret-token", "Authorization: Bearer [redacted]")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    public void ProcessDiagnosticsRedactCredentialsAndKeepRepositoryLocations(string input, string expected)
        => Assert.Equal(expected, GitDiagnosticSanitizer.Redact(input));

    private sealed class Fixture : IDisposable
    {
        public string Container { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-GitTests-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Container, "repository with spaces");
        public GitService Service { get; } = new();
        public string File(string name) => Path.GetFullPath(Path.Combine(Root, name));

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            await fixture.RunAsync("init", "--initial-branch=main");
            await fixture.RunAsync("config", "user.name", "WpfStudio test");
            await fixture.RunAsync("config", "user.email", "wpfstudio-test@example.invalid");
            await fixture.RunAsync("config", "commit.gpgsign", "false");
            await fixture.RunAsync("config", "core.autocrlf", "false");
            return fixture;
        }

        public async Task InitialCommitAsync()
        {
            await System.IO.File.WriteAllTextAsync(File("file.cs"), "original\n");
            await Service.StageAsync(Root, null);
            await Service.CommitAsync(Root, "Initial commit");
        }

        public async Task<string> RunAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Container, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Container, recursive: true);
        }
    }
}
