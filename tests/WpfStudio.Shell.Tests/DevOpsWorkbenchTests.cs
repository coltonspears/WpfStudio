using System.Diagnostics;
using System.Windows.Threading;
using WpfStudio.App.Features.DevOps;
using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;

namespace WpfStudio.Shell.Tests;

/// <summary>
/// The DevOps workbench against a scripted service and a real Git repository whose remote is on Azure DevOps or GitHub. Each test
/// runs on a dispatcher thread, as the workbench runs on the UI thread.
/// </summary>
public sealed class DevOpsWorkbenchTests
{
    private const string AzureRemote = "https://dev.azure.com/contoso/Web/_git/Web.App";
    private const string GitHubRemote = "git@github.com:octo-org/hello.git";
    private static readonly DevOpsIdentity Ada = new("me-id", "Ada Lovelace", "ada@contoso.com");

    [Fact]
    public Task ItSignsInWithGitsCredentialAndOpensTheFirstPullRequest() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.CreateAsync(AzureRemote);
        context.Credentials.Found = new("gcm-token", DevOpsSignInSource.GitCredentialManager);
        await context.ActivateAsync();

        var devOps = context.DevOps;
        Assert.Equal(DevOpsConnectionState.Connected, devOps.State);
        Assert.Equal("gcm-token", context.Provider.Token);
        Assert.Equal("contoso / Web / Web.App", devOps.Title);
        Assert.Contains("Git Credential Manager", devOps.SignInText, StringComparison.Ordinal);
        Assert.Equal(["Active", "Created by me", "Assigned to me", "Completed", "Abandoned"], devOps.PullRequests.Lists.Select(list => list.Label));
        Assert.Equal([42, 43], devOps.PullRequests.Items.Select(pr => pr.Number));
        Assert.Equal(42, devOps.PullRequests.Selected?.Number);
        // System messages (votes, pushes) are not discussion.
        Assert.Equal("Null check?", Assert.Single(devOps.PullRequests.Threads).First.Text);
        Assert.Equal("src/Login.cs", devOps.PullRequests.SelectedFile?.Path);
        Assert.Equal(("old\n", "new\n"), (devOps.PullRequests.FileDiff?.Before, devOps.PullRequests.FileDiff?.After));
        Assert.Equal("main", devOps.PullRequests.FileDiff?.OldLabel);
        Assert.Equal("Approved", devOps.PullRequests.Reviewers.Single(reviewer => reviewer.Identity.IsSame(Ada)).VoteText);
        Assert.Equal(ReviewVote.Approved, devOps.PullRequests.MyVote);
    });

    [Fact]
    public Task WithoutASignInItAsksForOneAndSavesATokenOnlyOnceItIsAccepted() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.CreateAsync(AzureRemote);
        await context.ActivateAsync();
        var devOps = context.DevOps;
        Assert.Equal(DevOpsConnectionState.SignedOut, devOps.State);
        Assert.True(devOps.NeedsSignIn);
        Assert.Equal("https://dev.azure.com/contoso/_usersSettings/tokens", devOps.Target!.TokenPageUrl);

        context.RefusedTokens.Add("typo");
        await devOps.SignInWithTokenCommand.ExecuteAsync("typo");
        await devOps.WhenIdleAsync();
        Assert.Equal(DevOpsConnectionState.SignedOut, devOps.State);
        Assert.Contains("did not accept that token", devOps.Error, StringComparison.Ordinal);
        Assert.Empty(context.Credentials.Saved);

        await devOps.SignInWithTokenCommand.ExecuteAsync("  pat-123  ");
        await devOps.WhenIdleAsync();
        Assert.Equal(DevOpsConnectionState.Connected, devOps.State);
        Assert.Equal(["pat-123"], context.Credentials.Saved);
        Assert.Contains("personal access token", devOps.SignInText, StringComparison.Ordinal);

        await devOps.SignOutCommand.ExecuteAsync(null);
        await devOps.WhenIdleAsync();
        Assert.Equal(DevOpsConnectionState.SignedOut, devOps.State);
        Assert.Equal(1, context.Credentials.SignOuts);
    });

    [Fact]
    public Task ARemoteOnAnotherHostIsExplainedAndNothingIsAsked() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.CreateAsync("https://gitlab.com/group/project.git");
        context.Credentials.Found = new("x", DevOpsSignInSource.PersonalAccessToken);
        await context.ActivateAsync();
        Assert.Equal(DevOpsConnectionState.Unsupported, context.DevOps.State);
        Assert.Contains("GitLab", context.DevOps.StateMessage, StringComparison.Ordinal);
        Assert.Equal(0, context.ProvidersCreated);
        // The Git pane falls back to the web page for a remote the workbench does not serve.
        context.Git.CreatePullRequestCommand.Execute(null);
        Assert.Contains("merge_requests/new", Assert.Single(context.Desktop.Opened), StringComparison.Ordinal);
    });

    [Fact]
    public Task VotesAndCompletionGoThroughTheActionGateAndReadTheRequestAgain() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        var requests = context.DevOps.PullRequests;
        await requests.VoteCommand.ExecuteAsync(ReviewVote.ApprovedWithSuggestions);
        await context.DevOps.WhenIdleAsync();
        Assert.Contains("vote !42 ApprovedWithSuggestions", context.Provider.Calls);
        Assert.Contains("get !42", context.Provider.Calls);

        requests.Strategy = requests.Strategies.Single(choice => choice.Strategy == MergeStrategy.RebaseMerge);
        context.Dialogs.Confirmations.Enqueue(false);
        await requests.CompleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(context.Provider.Calls, call => call.StartsWith("complete", StringComparison.Ordinal));
        context.Dialogs.Confirmations.Enqueue(true);
        await requests.CompleteCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Contains("complete !42 RebaseMerge delete=True workitems=True", context.Provider.Calls);
        Assert.Contains("merged into main", context.DevOps.Status, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task GitHubAsksWhatToChangeBeforeRequestingChanges() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(GitHubRemote);
        var requests = context.DevOps.PullRequests;
        Assert.Equal("Issues", context.DevOps.WorkItemsTitle);
        Assert.Equal("Actions", context.DevOps.PipelinesTitle);
        Assert.Equal(["Open", "Created by me", "Review requested", "Merged", "Closed"], requests.Lists.Select(list => list.Label));

        context.Dialogs.Prompts.Enqueue(null);
        await requests.VoteCommand.ExecuteAsync(ReviewVote.ChangesRequested);
        Assert.DoesNotContain(context.Provider.Calls, call => call.StartsWith("vote", StringComparison.Ordinal));
        context.Dialogs.Prompts.Enqueue("Please add a test");
        await requests.VoteCommand.ExecuteAsync(ReviewVote.ChangesRequested);
        await context.DevOps.WhenIdleAsync();
        Assert.Contains("vote !42 ChangesRequested Please add a test", context.Provider.Calls);
    });

    [Fact]
    public Task TheCurrentBranchsPullRequestShowsInTheGitPaneAndOpensHere() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        Assert.Equal("!42 · Active · 1 approval", context.Git.BranchPullRequestText);
        var shown = 0;
        context.DevOps.ShowRequested += () => shown++;
        context.DevOps.SelectedTab = DevOpsViewModel.WorkItemsTab;
        context.Git.OpenBranchPullRequestCommand.Execute(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Equal(1, shown);
        Assert.Equal(DevOpsViewModel.PullRequestsTab, context.DevOps.SelectedTab);
        Assert.Equal(42, context.DevOps.PullRequests.Selected?.Number);

        // Switching branches asks again.
        await context.Fixture.RunAsync("switch", "-c", "feature/none");
        await context.Git.RefreshCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Null(context.Git.BranchPullRequestText);
    });

    [Fact]
    public Task ANewPullRequestStartsFromTheBranchItsCommitsAndTheWorkItemsTheyMention() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        await context.Fixture.RunAsync("switch", "-c", "feature/7-login");
        await context.Fixture.CommitAsync("login.cs", "Fix login AB#9");
        await context.Fixture.CommitAsync("tests.cs", "Add tests");
        await context.Git.RefreshCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();

        // Signed in, the Git pane's Create pull request opens the form here instead of the web page.
        context.Git.CreatePullRequestCommand.Execute(null);
        await context.DevOps.WhenIdleAsync();
        var form = context.DevOps.NewPullRequest;
        Assert.True(form.IsOpen);
        Assert.Empty(context.Desktop.Opened);
        Assert.Equal("feature/7-login", form.SourceBranch);
        Assert.Equal("main", form.TargetBranch);
        Assert.Equal("Login", form.Title);
        Assert.Equal("- Fix login AB#9\n- Add tests", form.Description);
        Assert.Equal("7, 9", form.WorkItems);
        Assert.True(form.NeedsPublish);
        Assert.Contains("not on the remote yet", form.PublishText, StringComparison.Ordinal);

        form.WorkItems = "7, x";
        await form.CreateCommand.ExecuteAsync(null);
        Assert.Contains("“x” is not a work item number", context.DevOps.Error, StringComparison.Ordinal);
        form.WorkItems = "#7, AB#9";
        context.Dialogs.Confirmations.Enqueue(true);
        await form.CreateCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Contains("create feature/7-login → main \"Login\" [7,9]", context.Provider.Calls);
        Assert.False(form.IsOpen);
        Assert.Equal(44, context.DevOps.PullRequests.Selected?.Number);
    });

    [Fact]
    public Task WorkItemsChangeStateCopyAsMarkdownAndGetABranch() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        var devOps = context.DevOps;
        devOps.SelectedTab = DevOpsViewModel.WorkItemsTab;
        await devOps.WhenIdleAsync();
        var items = devOps.WorkItems;
        Assert.Equal(["Assigned to me", "Current iteration", "Created by me", "Recently changed by me", "Shared Queries / Bugs"], items.Queries.Select(query => query.Label));
        Assert.Equal(7, items.Details?.Item.Id);
        Assert.Equal("Active", items.NewState);

        items.NewState = "Resolved";
        await items.ApplyStateCommand.ExecuteAsync(null);
        await devOps.WhenIdleAsync();
        Assert.Contains("state 7 Resolved rev 3", context.Provider.Calls);

        items.CopyMarkdownCommand.Execute(null);
        Assert.StartsWith("## Bug 7: Login fails", Assert.Single(context.Desktop.Copied), StringComparison.Ordinal);

        Assert.Equal("feature/7-login-fails", items.BranchPreview);
        context.Dialogs.Prompts.Enqueue("feature/7-login-fails");
        await items.CreateBranchCommand.ExecuteAsync(null);
        await devOps.WhenIdleAsync();
        Assert.Equal("feature/7-login-fails", context.Git.Branch);

        items.SearchText = "#8";
        items.SearchCommand.Execute(null);
        await devOps.WhenIdleAsync();
        Assert.Equal("search #8", context.Provider.Calls.Last(call => call.StartsWith("search", StringComparison.Ordinal)));
    });

    [Fact]
    public Task AFailedRunOpensOnTheStepThatFailedWithItsLog() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        var devOps = context.DevOps;
        devOps.SelectedTab = DevOpsViewModel.PipelinesTab;
        await devOps.WhenIdleAsync();
        var pipelines = devOps.Pipelines;
        Assert.Equal(["Recent runs", "My runs", "This branch", "Pipelines", "Pending approvals"], pipelines.Views.Select(view => view.Label));
        Assert.Equal("CI #3", pipelines.SelectedRun?.Title);
        var job = Assert.Single(pipelines.Timeline);
        Assert.True(job.IsExpanded);
        Assert.Equal("Test", pipelines.SelectedNode?.Record.Name);
        Assert.Equal("ERROR: 1 test failed", pipelines.Log);

        pipelines.KeepTimestamps = true;
        Assert.StartsWith("2026-11-17T10:00:00.0000000Z ERROR", pipelines.Log, StringComparison.Ordinal);

        await pipelines.RetryRunCommand.ExecuteAsync(null);
        await devOps.WhenIdleAsync();
        Assert.Contains("retry 3", context.Provider.Calls);

        context.Dialogs.Prompts.Enqueue("release/1");
        await pipelines.RunPipelineCommand.ExecuteAsync(new DevOpsPipeline("9", "CI", "https://x"));
        await devOps.WhenIdleAsync();
        Assert.Contains("queue 9 release/1", context.Provider.Calls);
    });

    [Fact]
    public Task ATokenRevokedDuringTheSessionReturnsToSignIn() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        context.Provider.Revoked = true;
        await context.DevOps.PullRequests.VoteCommand.ExecuteAsync(ReviewVote.Approved);
        await context.DevOps.WhenIdleAsync();
        Assert.Equal(DevOpsConnectionState.SignedOut, context.DevOps.State);
        Assert.Contains("did not accept the token", context.DevOps.Error, StringComparison.Ordinal);
    });

    [Fact]
    public Task SettingsAreCheckedBeforeTheyAreSaved() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        var access = context.DevOps.Access;
        context.DevOps.SelectedTab = DevOpsViewModel.AccessTab;
        await context.DevOps.WhenIdleAsync();
        Assert.Equal("1 feature needs more access. ", access.Summary);

        access.BranchTemplate = "feature/{title}";
        await access.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("must contain {id}", context.DevOps.Error, StringComparison.Ordinal);
        access.BranchTemplate = "users/ada/{id}";
        access.RefreshSeconds = "30";
        await access.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal("users/ada/{id}", context.DevOps.Settings.BranchTemplate);
        Assert.Equal(30, context.Credentials.Settings.RefreshSeconds);
    });

    [Fact]
    public Task AConnectionStillSigningInDoesNotFinishIntoAnotherWorkspace() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.CreateAsync(AzureRemote);
        context.Credentials.Found = new("token", DevOpsSignInSource.PersonalAccessToken);
        var gate = context.Credentials.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activating = context.DevOps.ActivateAsync();
        Assert.Equal(DevOpsConnectionState.Connecting, context.DevOps.State);

        // The solution is closed while the sign-in is still being looked up.
        await context.Git.SetWorkspaceAsync(null);
        Assert.Equal(DevOpsConnectionState.NoRepository, context.DevOps.State);
        gate.SetResult();
        await activating;
        await context.DevOps.WhenIdleAsync();
        Assert.Equal(DevOpsConnectionState.NoRepository, context.DevOps.State);
        Assert.Null(context.DevOps.Provider);
        Assert.Equal(0, context.ProvidersCreated);
    });

    [Fact]
    public Task RefreshingWorkItemsReadsTheSelectedItemAgain() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        context.DevOps.SelectedTab = DevOpsViewModel.WorkItemsTab;
        await context.DevOps.WhenIdleAsync();
        Assert.Equal(("Active", 3), (context.DevOps.WorkItems.Details?.Item.State, context.DevOps.WorkItems.Details?.Revision));

        // Someone else resolves it; a refresh shows that, and the next change tests the new revision.
        context.Provider.WorkItemState = "Resolved";
        context.Provider.WorkItemRevision = 4;
        await context.DevOps.RefreshCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Equal(("Resolved", 4), (context.DevOps.WorkItems.Details?.Item.State, context.DevOps.WorkItems.Details?.Revision));
        context.DevOps.WorkItems.NewState = "Closed";
        await context.DevOps.WorkItems.ApplyStateCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Contains("state 7 Closed rev 4", context.Provider.Calls);
    });

    [Fact]
    public Task WorkItemTypesAreReadAgainForAnotherService() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(GitHubRemote);
        var items = context.DevOps.WorkItems;
        await items.StartCreateCommand.ExecuteAsync(null);
        Assert.Equal(["Issue"], items.Types);
        Assert.Equal("Issue", items.NewType);

        await context.Fixture.RunAsync("remote", "set-url", "origin", AzureRemote);
        await context.Git.RefreshCommand.ExecuteAsync(null);
        await context.DevOps.WhenIdleAsync();
        Assert.Equal(DevOpsConnectionState.Connected, context.DevOps.State);
        Assert.Empty(items.Types);
        Assert.Null(items.NewType);
        await items.StartCreateCommand.ExecuteAsync(null);
        Assert.Equal(["Bug", "Task"], items.Types);
    });

    [Fact]
    public Task OpeningAnotherPipelinesRunsLeavesThePreviousRunBehind() => OnDispatcherAsync(async () =>
    {
        await using var context = await Context.ConnectedAsync(AzureRemote);
        context.DevOps.SelectedTab = DevOpsViewModel.PipelinesTab;
        await context.DevOps.WhenIdleAsync();
        var pipelines = context.DevOps.Pipelines;
        Assert.Equal("3", pipelines.SelectedRun?.Id);

        // Another run is chosen, and before its timeline arrives, another pipeline's runs are opened.
        var gate = context.Provider.TimelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipelines.SelectedRun = pipelines.SelectedRun! with { Id = "2", Number = "2" };
        pipelines.ShowRunsCommand.Execute(new DevOpsPipeline("empty", "Nightly", "https://x"));
        gate.SetResult();
        await context.DevOps.WhenIdleAsync();
        Assert.Equal("Runs of Nightly", pipelines.ListHeading);
        Assert.Empty(pipelines.Runs);
        Assert.Null(pipelines.SelectedRun);
        Assert.Empty(pipelines.Timeline);
        Assert.Equal("", pipelines.Log);
    });

    // ------------------------------------------------------------------ Harness

    private static Task OnDispatcherAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(RepositoryFixture fixture) => Fixture = fixture;

        public RepositoryFixture Fixture { get; }
        public FakeDesktop Desktop { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeCredentials Credentials { get; } = new();
        public HashSet<string> RefusedTokens { get; } = [];
        public ScriptedProvider Provider { get; private set; } = null!;
        public int ProvidersCreated { get; private set; }
        public GitViewModel Git { get; private set; } = null!;
        public DevOpsViewModel DevOps { get; private set; } = null!;

        public static async Task<Context> CreateAsync(string remote)
        {
            var context = new Context(await RepositoryFixture.CreateAsync(remote));
            context.Git = new GitViewModel(context.Fixture.Service, context.Desktop);
            context.DevOps = new DevOpsViewModel(context.Git, context.Fixture.Service, context.Credentials, context.Create, context.Desktop, context.Dialogs, context.Desktop);
            await context.Git.SetWorkspaceAsync(context.Fixture.Root);
            return context;
        }

        public static async Task<Context> ConnectedAsync(string remote)
        {
            var context = await CreateAsync(remote);
            context.Credentials.Found = new("token", DevOpsSignInSource.PersonalAccessToken);
            await context.ActivateAsync();
            Assert.Equal(DevOpsConnectionState.Connected, context.DevOps.State);
            return context;
        }

        public async Task ActivateAsync()
        {
            await DevOps.ActivateAsync();
            await DevOps.WhenIdleAsync();
        }

        private IDevOpsProvider Create(DevOpsTarget target, string token, DevOpsSettings settings)
        {
            ProvidersCreated++;
            return Provider = new ScriptedProvider(target, token, RefusedTokens.Contains(token.Trim()));
        }

        public async ValueTask DisposeAsync()
        {
            DevOps.Dispose();
            Git.Dispose();
            await Task.Yield();
            Fixture.Dispose();
        }
    }

    internal sealed class FakeCredentials : IDevOpsCredentials
    {
        public DevOpsSignIn? Found { get; set; }
        /// <summary>When set, looking up a sign-in waits for it, as Git Credential Manager can.</summary>
        public TaskCompletionSource? Gate { get; set; }
        public List<string> Saved { get; } = [];
        public int SignOuts { get; private set; }
        public DevOpsSettings Settings { get; private set; } = new() { RefreshSeconds = 0 };

        public Task<DevOpsSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveSettingsAsync(Func<DevOpsSettings, DevOpsSettings> change, CancellationToken cancellationToken = default)
        {
            Settings = change(Settings);
            return Task.CompletedTask;
        }
        public async Task<DevOpsSignIn?> FindAsync(DevOpsTarget target, string repositoryRoot, CancellationToken cancellationToken = default)
        {
            if (Gate is { } gate) await gate.Task;
            return Found;
        }
        public Task<DevOpsSignIn?> SignInWithGitAsync(DevOpsTarget target, string repositoryRoot, CancellationToken cancellationToken = default) => Task.FromResult(Found);
        public Task SaveTokenAsync(DevOpsTarget target, string token, CancellationToken cancellationToken = default)
        {
            Saved.Add(token);
            Found = new(token, DevOpsSignInSource.PersonalAccessToken);
            return Task.CompletedTask;
        }
        public Task SignOutAsync(DevOpsTarget target, CancellationToken cancellationToken = default)
        {
            SignOuts++;
            Found = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>A service with two pull requests, one work item, one pipeline and a failed run, recording what it is asked.</summary>
    internal sealed class ScriptedProvider(DevOpsTarget target, string token, bool refused) : IDevOpsProvider
    {
        private static readonly DevOpsIdentity Grace = new("u1", "Grace Hopper", "grace@contoso.com");
        public List<string> Calls { get; } = [];
        public string Token { get; } = token;
        public bool Revoked { get; set; }
        public DevOpsTarget Target { get; } = target;
        public DevOpsCapabilities Capabilities { get; } = target.Kind == DevOpsProviderKind.GitHub
            ? new("GitHub", "Issues", "Issue", "Actions", "Merge", "Close", false, false, false, false, false, true, false, true, false)
            : new("Azure DevOps", "Work items", "Work item", "Pipelines", "Complete", "Abandon", true, true, true, true, true, true, true, true, true);
        public string TokenPageUrl => Target.TokenPageUrl;
        public IReadOnlyList<DevOpsWorkItemQuery> BuiltInQueries { get; } =
            [new("assigned", "Assigned to me"), new("iteration", "Current iteration"), new("created", "Created by me"), new("recent", "Recently changed by me")];

        private DevOpsPullRequest Request(int number, string source = "main") => new(number, number == 42 ? "Fix login" : "Other", "Fixes AB#7", PullRequestStatus.Active, false, Grace,
            DateTimeOffset.Now.AddDays(-1), null, source, "main", [new(Ada, number == 42 ? ReviewVote.Approved : ReviewVote.None)], $"https://x/pr/{number}",
            SourceCommit: "abc", RepositoryId: "repo", ProjectId: "project");

        private void Check()
        {
            if (refused || Revoked) throw new DevOpsAuthenticationException("Azure DevOps did not accept the token for contoso.", 401);
        }

        private Task<T> Answer<T>(string call, T value)
        {
            Check();
            Calls.Add(call);
            return Task.FromResult(value);
        }

        public Task<DevOpsIdentity> GetCurrentUserAsync(CancellationToken cancellationToken) => Answer("me", Ada);
        public Task<IReadOnlyList<DevOpsAccessEntry>> CheckAccessAsync(CancellationToken cancellationToken) => Answer<IReadOnlyList<DevOpsAccessEntry>>("access",
            [new("View pull requests", "", "Code (Read)", AccessState.Granted), new("Run pipelines", "", "Build (Read & execute)", AccessState.Denied)]);
        public Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(PullRequestList list, DevOpsIdentity me, int top, CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<DevOpsPullRequest>>("list " + list, [Request(42), Request(43, "feature/other")]);
        public Task<DevOpsPullRequest?> FindPullRequestAsync(string sourceBranch, CancellationToken cancellationToken) =>
            Answer("find " + sourceBranch, sourceBranch == "main" ? Request(42) : null);
        public Task<DevOpsPullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken) =>
            Answer("get !" + number, number == 44 ? Request(44, "feature/7-login") : Request(number));
        public Task<DevOpsPullRequestDetails> GetPullRequestDetailsAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken) =>
            Answer("details !" + pullRequest.Number, new DevOpsPullRequestDetails(pullRequest, [new("build", CheckState.Succeeded)],
                [new("1", ThreadStatus.Active, [new("1", Grace, "Null check?", DateTimeOffset.Now)], "/src/Login.cs", 12, CanResolve: true),
                 new("2", ThreadStatus.Unknown, [new("1", Grace, "Grace voted", DateTimeOffset.Now)], IsSystem: true)],
                [new("abc", "Fix login", "Grace", DateTimeOffset.Now)], [7]));
        public Task<IReadOnlyList<DevOpsPullRequestFile>> GetPullRequestFilesAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<DevOpsPullRequestFile>>("files !" + pullRequest.Number, [new("src/Login.cs", null, FileChangeKind.Modified, "old", "new")]);
        public Task<DevOpsFileVersions> GetPullRequestFileVersionsAsync(DevOpsPullRequest pullRequest, DevOpsPullRequestFile file, CancellationToken cancellationToken) =>
            Answer("versions " + file.Path, new DevOpsFileVersions("old\n", "new\n"));
        public Task<DevOpsPullRequest> CreatePullRequestAsync(NewPullRequest request, CancellationToken cancellationToken) =>
            Answer($"create {request.SourceBranch} → {request.TargetBranch} \"{request.Title}\" [{string.Join(',', request.WorkItemIds ?? [])}]", Request(44, request.SourceBranch));
        public Task VoteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, ReviewVote vote, string? comment, CancellationToken cancellationToken) =>
            Answer($"vote !{pullRequest.Number} {vote}{(comment is null ? "" : " " + comment)}", 0);
        public Task<DevOpsPullRequest> CompleteAsync(DevOpsPullRequest pullRequest, PullRequestCompletion completion, CancellationToken cancellationToken) =>
            Answer($"complete !{pullRequest.Number} {completion.Strategy} delete={completion.DeleteSourceBranch} workitems={completion.CompleteWorkItems}",
                pullRequest with { Status = PullRequestStatus.Completed, IsMerged = true });
        public Task<DevOpsPullRequest> SetAutoCompleteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, bool enabled, PullRequestCompletion completion, CancellationToken cancellationToken) =>
            Answer("autocomplete " + enabled, pullRequest with { AutoComplete = enabled });
        public Task<DevOpsPullRequest> SetStatusAsync(DevOpsPullRequest pullRequest, PullRequestStatus status, CancellationToken cancellationToken) =>
            Answer("status " + status, pullRequest with { Status = status });
        public Task<DevOpsPullRequest> SetDraftAsync(DevOpsPullRequest pullRequest, bool isDraft, CancellationToken cancellationToken) => Answer("draft " + isDraft, pullRequest with { IsDraft = isDraft });
        public Task<DevOpsPullRequest> UpdatePullRequestAsync(DevOpsPullRequest pullRequest, string title, string description, CancellationToken cancellationToken) =>
            Answer("rename " + title, pullRequest with { Title = title });
        public Task AddCommentAsync(DevOpsPullRequest pullRequest, string text, DevOpsThread? thread, CancellationToken cancellationToken) => Answer("comment " + text, 0);
        public Task SetThreadStatusAsync(DevOpsPullRequest pullRequest, DevOpsThread thread, ThreadStatus status, CancellationToken cancellationToken) => Answer("thread " + status, 0);
        public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken) => Answer<IReadOnlyList<string>>("branches", ["develop", "feature/7-login", "main"]);
        public Task<string?> GetDefaultBranchAsync(CancellationToken cancellationToken) => Answer<string?>("default branch", "main");

        public string WorkItemState { get; set; } = "Active";
        public int WorkItemRevision { get; set; } = 3;
        private DevOpsWorkItem Item => new(7, "Login fails", "Bug", WorkItemState, "Ada Lovelace", "https://x/7", "Web\\Sprint 1");
        public Task<IReadOnlyList<DevOpsWorkItemQuery>> GetSavedQueriesAsync(CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<DevOpsWorkItemQuery>>("queries", [new("q1", "Bugs", "Shared Queries", IsBuiltIn: false)]);
        public Task<IReadOnlyList<DevOpsWorkItem>> RunQueryAsync(DevOpsWorkItemQuery query, int top, CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<DevOpsWorkItem>>("query " + query.Id, [Item]);
        public Task<IReadOnlyList<DevOpsWorkItem>> SearchWorkItemsAsync(string text, int top, CancellationToken cancellationToken) => Answer<IReadOnlyList<DevOpsWorkItem>>("search " + text, []);
        public Task<DevOpsWorkItemDetails> GetWorkItemAsync(int id, CancellationToken cancellationToken) =>
            Answer("work item " + id, new DevOpsWorkItemDetails(Item, "Steps", [], [], ["New", "Active", "Resolved", "Closed"], Revision: WorkItemRevision));
        public Task<DevOpsWorkItem> SetWorkItemStateAsync(DevOpsWorkItemDetails item, string state, CancellationToken cancellationToken) =>
            Answer($"state {item.Item.Id} {state} rev {item.Revision}", item.Item with { State = state });
        public Task<DevOpsWorkItem> AssignWorkItemAsync(DevOpsWorkItemDetails item, DevOpsIdentity? assignee, CancellationToken cancellationToken) =>
            Answer("assign " + assignee?.DisplayName, item.Item with { AssignedTo = assignee?.DisplayName });
        public Task AddWorkItemCommentAsync(DevOpsWorkItem item, string text, CancellationToken cancellationToken) => Answer("work item comment " + text, 0);
        public Task<IReadOnlyList<string>> GetWorkItemTypesAsync(CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<string>>("types", Target.Kind == DevOpsProviderKind.GitHub ? ["Issue"] : ["Bug", "Task"]);
        public Task<DevOpsWorkItem> CreateWorkItemAsync(string type, string title, string description, DevOpsIdentity? assignee, CancellationToken cancellationToken) =>
            Answer($"create {type} {title}", Item with { Id = 8, Title = title, Type = type });

        private static DevOpsRun Run => new("3", "9", "CI", "3", RunStatus.Completed, RunResult.Failed, "https://x/run/3", Branch: "main");
        public Task<IReadOnlyList<DevOpsPipeline>> ListPipelinesAsync(CancellationToken cancellationToken) => Answer<IReadOnlyList<DevOpsPipeline>>("pipelines", [new("9", "CI", "https://x", LatestRun: Run)]);
        public Task<IReadOnlyList<DevOpsRun>> ListRunsAsync(RunList list, DevOpsIdentity me, string? branch, string? pipelineId, int top, CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<DevOpsRun>>($"runs {list}", pipelineId == "empty" ? [] : [Run]);
        public Task<DevOpsRun> GetRunAsync(DevOpsRun run, CancellationToken cancellationToken) => Answer("run " + run.Id, Run);
        /// <summary>When set, a timeline read waits for it, as a slow service would.</summary>
        public TaskCompletionSource? TimelineGate { get; set; }
        public async Task<IReadOnlyList<DevOpsTimelineRecord>> GetTimelineAsync(DevOpsRun run, CancellationToken cancellationToken)
        {
            if (TimelineGate is { } gate) await gate.Task;
            return await Timeline(run);
        }
        private Task<IReadOnlyList<DevOpsTimelineRecord>> Timeline(DevOpsRun run) => Answer<IReadOnlyList<DevOpsTimelineRecord>>("timeline " + run.Id,
            [new("j", null, "Job", "Build", 1, RunStatus.Completed, RunResult.Failed, LogId: "1"),
             new("t1", "j", "Task", "Restore", 1, RunStatus.Completed, RunResult.Succeeded, LogId: "2"),
             new("t2", "j", "Task", "Test", 2, RunStatus.Completed, RunResult.Failed, LogId: "3", Errors: 1)]);
        public Task<string> GetLogAsync(DevOpsRun run, DevOpsTimelineRecord record, CancellationToken cancellationToken) =>
            Answer("log " + record.LogId, "2026-11-17T10:00:00.0000000Z ##[error]1 test failed");
        public Task<DevOpsRun?> QueueRunAsync(string pipelineId, string branch, CancellationToken cancellationToken) =>
            Answer<DevOpsRun?>($"queue {pipelineId} {branch}", Run with { Id = "4", Number = "4", Status = RunStatus.Queued, Result = RunResult.None, Branch = branch });
        public Task CancelRunAsync(DevOpsRun run, CancellationToken cancellationToken) => Answer("cancel " + run.Id, 0);
        public Task RetryRunAsync(DevOpsRun run, CancellationToken cancellationToken) => Answer("retry " + run.Id, 0);
        public Task<IReadOnlyList<DevOpsApproval>> ListApprovalsAsync(CancellationToken cancellationToken) => Answer<IReadOnlyList<DevOpsApproval>>("approvals", []);
        public Task RespondToApprovalAsync(DevOpsApproval approval, bool approve, string comment, CancellationToken cancellationToken) => Answer("approve " + approve, 0);
    }

    /// <summary>A repository on branch main with one commit and an origin remote; the remote is never contacted.</summary>
    internal sealed class RepositoryFixture : IDisposable
    {
        private RepositoryFixture() { }
        public string Container { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-DevOpsTests-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Container, "repository");
        public GitService Service { get; } = new();

        public static async Task<RepositoryFixture> CreateAsync(string remote)
        {
            var fixture = new RepositoryFixture();
            Directory.CreateDirectory(fixture.Root);
            await fixture.RunAsync("init", "--initial-branch=main");
            await fixture.RunAsync("config", "user.name", "WpfStudio test");
            await fixture.RunAsync("config", "user.email", "wpfstudio-test@example.invalid");
            await fixture.RunAsync("config", "commit.gpgsign", "false");
            await fixture.RunAsync("remote", "add", "origin", remote);
            await fixture.CommitAsync("readme.md", "Initial commit");
            return fixture;
        }

        public async Task CommitAsync(string file, string message)
        {
            await File.WriteAllTextAsync(Path.Combine(Root, file), message + "\n");
            await RunAsync("add", "--", file);
            await RunAsync("commit", "-m", message);
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
            foreach (var file in Directory.EnumerateFiles(Container, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Container, recursive: true);
        }
    }
}
