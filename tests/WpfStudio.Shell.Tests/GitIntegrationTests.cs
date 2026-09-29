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
        Assert.Equal("original\n", opened.Before);
        Assert.Equal("pending changes\n", opened.After);
        Assert.Equal("Working tree", opened.NewLabel);
        Assert.Same(opened, model.CurrentDiff);
        Assert.Equal(model.Unstaged.Single(), model.SelectedUnstaged);
        Assert.Equal(GitViewModel.ChangesTab, model.SelectedTab);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task StagedWorkingNewAndDeletedDiffsCarryBothFileVersions()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        await File.WriteAllTextAsync(fixture.File("gone.cs"), "remove me\n");
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.CommitAsync(fixture.Root, "Add a file to delete");
        await File.WriteAllTextAsync(fixture.File("file.cs"), "staged\n");
        await fixture.RunAsync("add", "file.cs");
        await File.WriteAllTextAsync(fixture.File("file.cs"), "working\n");
        await File.WriteAllTextAsync(fixture.File("new λ.txt"), "brand new\n");
        File.Delete(fixture.File("gone.cs"));
        await File.WriteAllBytesAsync(fixture.File("blob.bin"), [1, 0, 2, 0]);
        var changes = (await fixture.Service.LoadAsync(fixture.Root)).Changes;

        var file = changes.Single(x => x.Path == "file.cs");
        var staged = await fixture.Service.GetDiffAsync(fixture.Root, file, staged: true);
        Assert.Equal(("original\n", "staged\n"), (staged.Before, staged.After));
        Assert.Equal(("Last commit (HEAD)", "Staged"), (staged.OldLabel, staged.NewLabel));
        var working = await fixture.Service.GetDiffAsync(fixture.Root, file, staged: false);
        Assert.Equal(("staged\n", "working\n"), (working.Before, working.After));
        Assert.Equal("Staged", working.OldLabel);

        var added = await fixture.Service.GetDiffAsync(fixture.Root, changes.Single(x => x.Path == "new λ.txt"), staged: false);
        Assert.Null(added.Before);
        Assert.Equal("brand new\n", added.After);
        Assert.Equal("N", changes.Single(x => x.Path == "new λ.txt").Letter);

        var deleted = await fixture.Service.GetDiffAsync(fixture.Root, changes.Single(x => x.Path == "gone.cs"), staged: false);
        Assert.Equal("remove me\n", deleted.Before);
        Assert.Null(deleted.After);

        var binary = await fixture.Service.GetDiffAsync(fixture.Root, changes.Single(x => x.Path == "blob.bin"), staged: false);
        Assert.Contains("Binary", binary.Message);
        Assert.False(binary.HasVersions);
    }

    [Fact]
    public async Task HistoryCarriesDecorationsAndCommitDetailsListFilesWithBothVersions()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        await File.WriteAllTextAsync(fixture.File("file.cs"), "original\nsecond line\n");
        await File.WriteAllTextAsync(fixture.File("added λ.txt"), "hello\n");
        await File.WriteAllBytesAsync(fixture.File("image.bin"), [0, 1, 2, 3]);
        await fixture.Service.StageAsync(fixture.Root, null);
        await fixture.Service.CommitAsync(fixture.Root, "Second commit\n\nExplains the change.");
        await fixture.RunAsync("tag", "v1.0");
        await fixture.RunAsync("mv", "added λ.txt", "renamed λ.txt");
        await fixture.Service.CommitAsync(fixture.Root, "Rename");

        var history = (await fixture.Service.LoadAsync(fixture.Root)).History;
        Assert.Equal(new[] { "Rename", "Second commit", "Initial commit" }, history.Select(x => x.Subject));
        Assert.Contains(history[0].RefList, x => x is { Name: "main", IsHead: true });
        Assert.Contains(history[1].RefList, x => x is { Name: "v1.0", IsTag: true });
        Assert.All(history, x => Assert.False(x.IsMerge));
        Assert.Equal(2, (await fixture.Service.LoadHistoryAsync(fixture.Root, 1, 5)).Count);

        var second = await fixture.Service.GetCommitDetailsAsync(fixture.Root, history[1].Id);
        Assert.Equal(("Second commit", "Explains the change."), (second.Subject, second.Body));
        Assert.Equal(history[2].Id, Assert.Single(second.Parents));
        var modified = second.Files.Single(x => x.Path == "file.cs");
        Assert.Equal<(char, int?, int?)>(('M', 1, 0), (modified.Status, modified.Added, modified.Deleted));
        Assert.Equal('A', second.Files.Single(x => x.Path == "added λ.txt").Status);
        Assert.True(second.Files.Single(x => x.Path == "image.bin").IsBinary);
        var diff = await fixture.Service.GetCommitFileDiffAsync(fixture.Root, second, modified);
        Assert.Equal(("original\n", "original\nsecond line\n"), (diff.Before, diff.After));
        var binary = await fixture.Service.GetCommitFileDiffAsync(fixture.Root, second, second.Files.Single(x => x.Path == "image.bin"));
        Assert.Contains("Binary", binary.Message);

        var rename = Assert.Single((await fixture.Service.GetCommitDetailsAsync(fixture.Root, history[0].Id)).Files);
        Assert.Equal(('R', "added λ.txt", "renamed λ.txt"), (rename.Status, rename.OriginalPath, rename.Path));
        Assert.Equal<(int?, int?)>((0, 0), (rename.Added, rename.Deleted));

        var root = await fixture.Service.GetCommitDetailsAsync(fixture.Root, history[2].Id);
        Assert.Empty(root.Parents);
        var first = Assert.Single(root.Files);
        var rootDiff = await fixture.Service.GetCommitFileDiffAsync(fixture.Root, root, first);
        Assert.Null(rootDiff.Before);
        Assert.Equal("original\n", rootDiff.After);
    }

    [Fact]
    public void CommitFileRecordsJoinStatusesWithLineCounts()
    {
        var files = GitService.ParseCommitFiles("M\0src/a b.cs\0R087\0old.cs\0new.cs\0A\0logo.png\0",
            "3\t1\tsrc/a b.cs\0" + "2\t2\t\0old.cs\0new.cs\0" + "-\t-\tlogo.png\0");
        Assert.Equal(3, files.Count);
        Assert.Equal<(string, char, int?, int?)>(("src/a b.cs", 'M', 3, 1), (files[0].Path, files[0].Status, files[0].Added, files[0].Deleted));
        Assert.Equal<(string, string?, char, int?)>(("new.cs", "old.cs", 'R', 2), (files[1].Path, files[1].OriginalPath, files[1].Status, files[1].Added));
        Assert.True(files[2].IsBinary);
        Assert.Equal("binary", files[2].AddedText);
    }

    [Fact]
    public async Task ViewModelPagesFiltersAndLoadsSelectedCommitsAndChanges()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.InitialCommitAsync();
        for (var index = 1; index <= GitViewModel.HistoryPageSize + 2; index++)
            await fixture.RunAsync("commit", "--allow-empty", "--quiet", "-m", $"Empty commit {index}");
        await File.WriteAllTextAsync(fixture.File("file.cs"), "changed\n");
        await File.WriteAllTextAsync(fixture.File("staged.cs"), "staged\n");
        await fixture.RunAsync("add", "staged.cs");
        using var model = new GitViewModel(fixture.Service);
        await model.SetWorkspaceAsync(fixture.Root);
        Assert.Equal(GitViewModel.HistoryPageSize, model.History.Count);
        Assert.True(model.HasMoreHistory);
        await model.LoadMoreHistoryCommand.ExecuteAsync(null);
        Assert.Equal(GitViewModel.HistoryPageSize + 3, model.History.Count);
        Assert.False(model.HasMoreHistory);
        Assert.Equal(model.History.Count, model.VisibleHistory.Count);

        model.HistoryFilter = "commit 5";
        Assert.Equal(new[] { "Empty commit 52", "Empty commit 51", "Empty commit 50", "Empty commit 5" }, model.VisibleHistory.Select(x => x.Subject));
        model.HistoryFilter = "no such commit";
        Assert.True(model.HistoryFilterHasNoMatches);
        model.ClearHistoryFilterCommand.Execute(null);
        Assert.Equal(model.History.Count, model.VisibleHistory.Count);

        model.SelectedCommit = model.VisibleHistory[^1];
        await model.WhenLoadedAsync();
        Assert.Equal("Initial commit", model.CommitDetails?.Subject);
        Assert.Equal("file.cs", Assert.Single(model.CommitFiles).Path);
        Assert.Equal("original\n", model.CommitDiff?.After);

        model.SelectedStaged = model.Staged.Single();
        await model.WhenLoadedAsync();
        Assert.Equal("staged\n", model.CurrentDiff?.After);
        model.SelectedUnstaged = model.Unstaged.Single(x => x.Path == "file.cs");
        Assert.Null(model.SelectedStaged);
        await model.WhenLoadedAsync();
        Assert.Equal(("original\n", "changed\n"), (model.CurrentDiff?.Before, model.CurrentDiff?.After));

        // Staging keeps the reader on the same file, now in the staged list, with its diff reloaded.
        await model.StageFileCommand.ExecuteAsync(model.SelectedUnstaged);
        await model.WhenLoadedAsync();
        Assert.Equal("file.cs", model.SelectedStaged?.Path);
        Assert.Equal("Staged", model.CurrentDiff?.NewLabel);
        Assert.Equal("Initial commit", model.CommitDetails?.Subject);
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
