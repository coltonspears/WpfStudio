using System.Text.Json;
using WpfStudio.Core.DevOps;
using WpfStudio.Core.DevOps.GitHub;

namespace WpfStudio.Core.Tests.DevOps;

public sealed class GitHubProviderTests
{
    private const string Api = "https://api.github.com/repos/octo/hello/";
    private static readonly DevOpsTarget Target = DevOpsTarget.FromRemoteUrl("https://github.com/octo/hello.git")!;
    private static readonly DevOpsIdentity Me = new("mona", "Mona Lisa", "mona");

    private static (GitHubProvider Provider, FakeHttp Http) Create(Action<FakeHttp> routes, DevOpsTarget? target = null)
    {
        var http = new FakeHttp();
        routes(http);
        return (new GitHubProvider(http.Client(), target ?? Target, "ghp_secret"), http);
    }

    private static string Pull(int number, string author = "octo", string state = "open", string? mergedAt = null, bool draft = false,
        string headRepository = "octo/hello", string reviewers = "[]", string? mergeable = null) => $$$"""
        {"number":{{{number}}},"title":"Change {{{number}}}","body":"Fixes #12 and AB#34","state":"{{{state}}}","draft":{{{(draft ? "true" : "false")}}},
         "user":{"login":"{{{author}}}"},"created_at":"2026-11-01T10:00:00Z","closed_at":{{{(state == "closed" ? "\"2026-11-02T10:00:00Z\"" : "null")}}},
         "merged_at":{{{(mergedAt is null ? "null" : $"\"{mergedAt}\"")}}},
         "head":{"ref":"feature/{{{number}}}","sha":"head{{{number}}}","repo":{"full_name":"{{{headRepository}}}"}},
         "base":{"ref":"main","sha":"base{{{number}}}","repo":{"full_name":"octo/hello"}},
         "requested_reviewers":{{{reviewers}}},"labels":[{"name":"ready"}],"html_url":"https://github.com/octo/hello/pull/{{{number}}}"
         {{{(mergeable is null ? "" : $",\"mergeable_state\":\"{mergeable}\"")}}} }
        """;

    [Fact]
    public void GitHubRemotesBecomeTargetsWithTheirApiAddress()
    {
        foreach (var remote in new[] { "https://github.com/octo/hello.git", "git@github.com:octo/hello.git", "ssh://git@github.com/octo/hello.git", "https://www.github.com/octo/hello" })
        {
            var target = DevOpsTarget.FromRemoteUrl(remote);
            Assert.NotNull(target);
            Assert.Equal(DevOpsProviderKind.GitHub, target.Kind);
            Assert.Equal(("octo", "hello"), (target.Owner, target.Repository));
            Assert.Equal("https://api.github.com", target.ApiUrl);
            Assert.Equal("https://github.com/octo/hello", target.RepositoryWebUrl);
            Assert.Equal("github|github.com", target.CredentialKey);
        }
        var enterprise = DevOpsTarget.FromRemoteUrl("git@github.contoso.com:team/app.git");
        Assert.NotNull(enterprise);
        Assert.Equal("https://github.contoso.com/api/v3", enterprise.ApiUrl);
        Assert.Equal("https://github.contoso.com/team/app", enterprise.RepositoryWebUrl);
        Assert.Null(DevOpsTarget.FromRemoteUrl("https://gitlab.com/octo/hello.git"));
    }

    [Fact]
    public void OnlyGitHubTargetsAreAccepted()
    {
        var azure = DevOpsTarget.FromRemoteUrl("https://dev.azure.com/contoso/Web/_git/App")!;
        Assert.Throws<ArgumentException>(() => new GitHubProvider(new FakeHttp().Client(), azure, "token"));
    }

    [Fact]
    public async Task RequestsCarryTheTokenAndGitHubsHeaders()
    {
        var http = new FakeHttp().Json("/user", """{"login":"mona","name":"Mona Lisa"}""");
        var capture = new HeaderCapture(http);
        var provider = new GitHubProvider(new HttpClient(capture), Target, "ghp_secret");
        var me = await provider.GetCurrentUserAsync(CancellationToken.None);
        Assert.Equal(("mona", "Mona Lisa", "mona"), (me.Id, me.DisplayName, me.UniqueName));
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.github.com/user", request.Url);
        Assert.Equal("Bearer ghp_secret", request.Authorization);
        Assert.Equal("application/vnd.github+json", request.Accept);
        var headers = Assert.Single(capture.Seen);
        Assert.Equal("2022-11-28", headers["X-GitHub-Api-Version"]);
        Assert.Equal("WpfStudio", headers["User-Agent"]);
        // The current user is read once.
        await provider.GetCurrentUserAsync(CancellationToken.None);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task OpenPullRequestListsAreFilteredByAuthorAndRequestedReviewer()
    {
        var pulls = $"[{Pull(1, "mona")},{Pull(2, reviewers: """[{"login":"mona"}]""")},{Pull(3, headRepository: "someone/hello", draft: true)}]";
        var (provider, http) = Create(h => h.Json("/pulls?state=open", pulls));
        var mine = await provider.ListPullRequestsAsync(PullRequestList.Mine, Me, 50, CancellationToken.None);
        Assert.Equal([1], mine.Select(pull => pull.Number));
        var review = await provider.ListPullRequestsAsync(PullRequestList.ReviewRequested, Me, 50, CancellationToken.None);
        Assert.Equal([2], review.Select(pull => pull.Number));
        Assert.Equal("mona", Assert.Single(review[0].Reviewers).Identity.DisplayName);
        var active = await provider.ListPullRequestsAsync(PullRequestList.Active, Me, 50, CancellationToken.None);
        Assert.Equal([1, 2, 3], active.Select(pull => pull.Number));
        var first = active[0];
        Assert.Equal(("feature/1", "main", "head1", "base1"), (first.SourceBranch, first.TargetBranch, first.SourceCommit, first.TargetCommit));
        Assert.Equal(("Active", "Change 1", "https://github.com/octo/hello/pull/1"), (first.StatusText, first.Title, first.WebUrl));
        Assert.Equal(["ready"], first.LabelList);
        Assert.False(first.IsFromFork);
        Assert.Null(first.RepositoryId);
        Assert.True(active[2].IsFromFork);
        Assert.Equal("Draft", active[2].StatusText);
        Assert.All(http.Requests, request => Assert.Equal("open", request.Query("state")));
    }

    [Fact]
    public async Task ClosedPullRequestsAreSplitIntoMergedAndClosed()
    {
        var closed = $"[{Pull(4, state: "closed", mergedAt: "2026-11-02T09:00:00Z")},{Pull(5, state: "closed")}]";
        var (provider, http) = Create(h => h.Json("/pulls?state=closed", closed));
        var merged = Assert.Single(await provider.ListPullRequestsAsync(PullRequestList.Completed, Me, 50, CancellationToken.None));
        Assert.Equal((4, PullRequestStatus.Completed, true, "Merged"), (merged.Number, merged.Status, merged.IsMerged, merged.StatusText));
        var abandoned = Assert.Single(await provider.ListPullRequestsAsync(PullRequestList.Abandoned, Me, 50, CancellationToken.None));
        Assert.Equal((5, PullRequestStatus.Abandoned, "Closed"), (abandoned.Number, abandoned.Status, abandoned.StatusText));
        Assert.Equal(("updated", "desc"), (http.Requests[0].Query("sort"), http.Requests[0].Query("direction")));
    }

    [Fact]
    public async Task EachReviewerKeepsTheirStandingVote()
    {
        const string reviews = """
            [{"user":{"login":"alice"},"state":"APPROVED"},{"user":{"login":"alice"},"state":"COMMENTED"},
             {"user":{"login":"bob"},"state":"DISMISSED"},{"user":{"login":"dave"},"state":"COMMENTED"},
             {"user":{"login":"erin"},"state":"COMMENTED"},{"user":{"login":"erin"},"state":"CHANGES_REQUESTED"},
             {"user":{"login":"octo"},"state":"COMMENTED"},{"user":{"login":"frank"},"state":"PENDING"}]
            """;
        var (provider, _) = Create(h => h
            .Json("/pulls/9/reviews", reviews)
            .Json("/pulls/9", Pull(9, reviewers: """[{"login":"carol"},{"login":"bob"}]""", mergeable: "dirty")));
        var pull = await provider.GetPullRequestAsync(9, CancellationToken.None);
        var votes = pull.Reviewers.ToDictionary(reviewer => reviewer.Identity.DisplayName, reviewer => reviewer.Vote);
        Assert.Equal(new Dictionary<string, ReviewVote>
        {
            ["carol"] = ReviewVote.None, ["bob"] = ReviewVote.None, ["alice"] = ReviewVote.Approved, ["dave"] = ReviewVote.Commented, ["erin"] = ReviewVote.ChangesRequested
        }, votes);
        Assert.Equal(PullRequestMergeState.Conflicts, pull.MergeState);
        Assert.Equal("Changes requested", pull.ReviewText);
    }

    [Fact]
    public async Task DetailsGatherChecksThreadsCommitsAndReferencedIssues()
    {
        var (provider, _) = Create(h => h
            .Json("/commits/head9/check-runs", """{"check_runs":[{"name":"build","status":"completed","conclusion":"failure","html_url":"https://github.com/run/1","output":{"title":"2 errors"}},{"name":"lint","status":"in_progress"}]}""")
            .Json("/commits/head9/status", """{"statuses":[{"context":"ci/legacy","state":"success","description":"ok","target_url":"https://ci"}]}""")
            .Json("/issues/9/comments", """[{"id":1,"user":{"login":"bob"},"body":"Looks fine","created_at":"2026-11-03T10:00:00Z"}]""")
            .Json("/pulls/9/comments", """
                [{"id":10,"user":{"login":"alice"},"body":"Rename this","path":"src/a.cs","line":5,"created_at":"2026-11-01T11:00:00Z"},
                 {"id":11,"in_reply_to_id":10,"user":{"login":"octo"},"body":"Done","path":"src/a.cs","line":5,"created_at":"2026-11-01T12:00:00Z"},
                 {"id":12,"user":{"login":"alice"},"body":"Outdated","path":"src/b.cs","line":null,"original_line":7,"created_at":"2026-11-02T11:00:00Z"}]
                """)
            .Json("/pulls/9/reviews", """[{"id":20,"user":{"login":"alice"},"state":"APPROVED","body":"LGTM","submitted_at":"2026-11-04T10:00:00Z"},{"id":21,"user":{"login":"bob"},"state":"COMMENTED","body":""}]""")
            .Json("/pulls/9/commits", """[{"sha":"abcdef1234","commit":{"message":"Fix login\n\nDetails","author":{"name":"Octo Cat","date":"2026-11-01T09:00:00Z"}}}]"""));
        var listed =(await Create(h => h.Json("/pulls?state=open", $"[{Pull(9)}]")).Provider.ListPullRequestsAsync(PullRequestList.Active, Me, 10, CancellationToken.None))[0];
        var details = await provider.GetPullRequestDetailsAsync(listed, CancellationToken.None);
        Assert.Equal([("build", CheckState.Failed), ("lint", CheckState.Running), ("ci/legacy", CheckState.Succeeded)], details.Checks.Select(check => (check.Name, check.State)));
        Assert.Equal("2 errors", details.Checks[0].Description);
        Assert.Equal(["review:10", "review:12", "issue:1", "reviewbody:20"], details.Threads.Select(thread => thread.Id));
        var review = details.Threads[0];
        Assert.Equal(["Rename this", "Done"], review.Comments.Select(comment => comment.Text));
        Assert.Equal("src/a.cs:5", review.Location);
        Assert.False(review.CanResolve);
        Assert.Equal("src/b.cs:7", details.Threads[1].Location);
        var commit = Assert.Single(details.Commits);
        Assert.Equal(("Fix login", "Octo Cat", "abcdef12"), (commit.Subject, commit.Author, commit.ShortId));
        Assert.Equal([12, 34], details.WorkItemIds);
        Assert.Equal(ReviewVote.Approved, Assert.Single(details.PullRequest.Reviewers, reviewer => reviewer.Identity.Id == "alice").Vote);
    }

    [Fact]
    public async Task MergingSendsTheReadHeadCommitAndDeletesTheBranch()
    {
        var (provider, http) = Create(h => h
            .On(HttpMethod.Put, "/pulls/9/merge", 200, """{"merged":true}""")
            .On(HttpMethod.Delete, "/git/refs/heads/feature/9", 204)
            .Json("/pulls/9/reviews", "[]")
            .Json("/pulls/9", Pull(9, state: "closed", mergedAt: "2026-11-02T09:00:00Z")));
        var open = await Create(h => h.Json("/pulls/9", Pull(9)).Json("/pulls/9/reviews", "[]")).Provider.GetPullRequestAsync(9, CancellationToken.None);
        var result = await provider.CompleteAsync(open, new(MergeStrategy.NoFastForward, DeleteSourceBranch: true, MergeMessage: "Merge change 9\n\nWith details"), CancellationToken.None);
        Assert.True(result.IsMerged);
        var merge = JsonSerializer.Deserialize<JsonElement>(http.Single(HttpMethod.Put, "/pulls/9/merge").Body!);
        Assert.Equal(("merge", "head9", "Merge change 9", "With details"),
            (merge.GetProperty("merge_method").GetString(), merge.GetProperty("sha").GetString(), merge.GetProperty("commit_title").GetString(), merge.GetProperty("commit_message").GetString()));
        Assert.Equal(Api + "git/refs/heads/feature/9", http.Single(HttpMethod.Delete, "/git/refs/").Url);
    }

    [Fact]
    public async Task AForkBranchIsNotDeletedAndAnAlreadyDeletedBranchIsNoFailure()
    {
        var (provider, http) = Create(h => h
            .On(HttpMethod.Put, "/pulls/", 200, """{"merged":true}""")
            .On(HttpMethod.Delete, "/git/refs/heads/", 422, """{"message":"Reference does not exist"}""")
            .Json("/reviews", "[]")
            .Json("/pulls/3", Pull(3, state: "closed", mergedAt: "2026-11-02T09:00:00Z", headRepository: "someone/hello"))
            .Json("/pulls/4", Pull(4, state: "closed", mergedAt: "2026-11-02T09:00:00Z")));
        var listed = await Create(h => h.Json("/pulls?state=open", $"[{Pull(3, headRepository: "someone/hello")},{Pull(4)}]"))
            .Provider.ListPullRequestsAsync(PullRequestList.Active, Me, 10, CancellationToken.None);
        await provider.CompleteAsync(listed[0], new(), CancellationToken.None);
        Assert.DoesNotContain(http.Requests, request => request.Method == "DELETE");
        Assert.Equal("squash", JsonSerializer.Deserialize<JsonElement>(http.Requests[0].Body!).GetProperty("merge_method").GetString());
        var merged = await provider.CompleteAsync(listed[1], new(), CancellationToken.None);
        Assert.True(merged.IsMerged);
        Assert.Single(http.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task MergingAfterANewPushIsRefused()
    {
        var (provider, _) = Create(h => h.On(HttpMethod.Put, "/merge", 409, """{"message":"Head branch was modified. Review and try the merge again."}"""));
        var pull = (await Create(h => h.Json("/pulls?state=open", $"[{Pull(9)}]")).Provider.ListPullRequestsAsync(PullRequestList.Active, Me, 1, CancellationToken.None))[0];
        var error = await Assert.ThrowsAsync<DevOpsException>(() => provider.CompleteAsync(pull, new(), CancellationToken.None));
        Assert.Contains("New commits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewsMapToGitHubEventsAndRequestingChangesNeedsAComment()
    {
        var (provider, http) = Create(h => h.On(HttpMethod.Post, "/pulls/9/reviews", 200, "{}"));
        var pull = (await Create(h => h.Json("/pulls?state=open", $"[{Pull(9)}]")).Provider.ListPullRequestsAsync(PullRequestList.Active, Me, 1, CancellationToken.None))[0];
        await Assert.ThrowsAsync<DevOpsException>(() => provider.VoteAsync(pull, Me, ReviewVote.ChangesRequested, " ", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.VoteAsync(pull, Me, ReviewVote.None, null, CancellationToken.None));
        Assert.Empty(http.Requests);
        await provider.VoteAsync(pull, Me, ReviewVote.Approved, null, CancellationToken.None);
        await provider.VoteAsync(pull, Me, ReviewVote.Rejected, "Please add tests", CancellationToken.None);
        var approve = JsonSerializer.Deserialize<JsonElement>(http.Requests[0].Body!);
        Assert.Equal(("APPROVE", "head9"), (approve.GetProperty("event").GetString(), approve.GetProperty("commit_id").GetString()));
        Assert.False(approve.TryGetProperty("body", out _));
        var reject = JsonSerializer.Deserialize<JsonElement>(http.Requests[1].Body!);
        Assert.Equal(("REQUEST_CHANGES", "Please add tests"), (reject.GetProperty("event").GetString(), reject.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task FilesAreComparedAtTheMergeBase()
    {
        const string files = """
            [{"filename":"src/a.cs","status":"modified","sha":"blob-a","additions":3,"deletions":1},
             {"filename":"src/new name.cs","previous_filename":"src/old.cs","status":"renamed","sha":"blob-n"},
             {"filename":"src/b.cs","status":"added","sha":"blob-b","additions":10},
             {"filename":"src/c.cs","status":"removed","sha":"blob-c","deletions":4},
             {"filename":"logo.png","status":"modified","sha":"blob-p"}]
            """;
        var (provider, http) = Create(h => h
            .Json("/pulls/9/files", files)
            .Json("/compare/base9...head9", """{"merge_base_commit":{"sha":"mb1"}}""")
            .Json("/contents/src/a.cs?ref=mb1", "old a")
            .Json("/contents/src/a.cs?ref=head9", "new a")
            .Json("/contents/src/old.cs?ref=mb1", "renamed before")
            .Json("/contents/src/new%20name.cs?ref=head9", "renamed after")
            .Json("/contents/src/b.cs?ref=head9", "added")
            .Json("/contents/src/c.cs?ref=mb1", "removed")
            .Json("/contents/logo.png", "PNG\0binary"));
        var pull = (await Create(h => h.Json("/pulls?state=open", $"[{Pull(9)}]")).Provider.ListPullRequestsAsync(PullRequestList.Active, Me, 1, CancellationToken.None))[0];
        var list = await provider.GetPullRequestFilesAsync(pull, CancellationToken.None);
        Assert.Equal([FileChangeKind.Modified, FileChangeKind.Renamed, FileChangeKind.Added, FileChangeKind.Deleted, FileChangeKind.Modified], list.Select(file => file.Change));
        Assert.Equal(("src/old.cs", "blob-n"), (list[1].OriginalPath, list[1].HeadObjectId));
        Assert.Null(list[3].HeadObjectId);
        Assert.Equal(("+3", "−1"), (list[0].AddedText, list[0].RemovedText));

        Assert.Equal(new DevOpsFileVersions("old a", "new a"), await provider.GetPullRequestFileVersionsAsync(pull, list[0], CancellationToken.None));
        Assert.Equal(new DevOpsFileVersions("renamed before", "renamed after"), await provider.GetPullRequestFileVersionsAsync(pull, list[1], CancellationToken.None));
        Assert.Equal(new DevOpsFileVersions(null, "added"), await provider.GetPullRequestFileVersionsAsync(pull, list[2], CancellationToken.None));
        Assert.Equal(new DevOpsFileVersions("removed", null), await provider.GetPullRequestFileVersionsAsync(pull, list[3], CancellationToken.None));
        var binary = await provider.GetPullRequestFileVersionsAsync(pull, list[4], CancellationToken.None);
        Assert.Null(binary.Before);
        Assert.Contains("Binary", binary.Message, StringComparison.Ordinal);
        // One compare for the whole pull request, and the raw media type for every read.
        Assert.Single(http.Requests, request => request.Url.Contains("/compare/", StringComparison.Ordinal));
        Assert.All(http.Requests.Where(request => request.Url.Contains("/contents/", StringComparison.Ordinal)),
            request => Assert.Equal("application/vnd.github.raw", request.Accept));
    }

    [Fact]
    public async Task CreatingAPullRequestLinksTheIssuesItResolves()
    {
        var (provider, http) = Create(h => h.On(HttpMethod.Post, "/pulls", 201, Pull(21, "mona")));
        var created = await provider.CreatePullRequestAsync(new("feature/x", "main", "Add login", "Implements #5", IsDraft: true, WorkItemIds: [5, 12]), CancellationToken.None);
        Assert.Equal(21, created.Number);
        var body = JsonSerializer.Deserialize<JsonElement>(http.Requests[0].Body!);
        Assert.Equal(("feature/x", "main", true), (body.GetProperty("head").GetString(), body.GetProperty("base").GetString(), body.GetProperty("draft").GetBoolean()));
        Assert.Equal("Implements #5\n\nResolves #12", body.GetProperty("body").GetString());
    }

    [Fact]
    public async Task ValidationErrorsSayWhatWasWrong()
    {
        var (provider, _) = Create(h => h.On(HttpMethod.Post, "/pulls", 422,
            """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for octo:feature/x."}]}"""));
        var error = await Assert.ThrowsAsync<DevOpsException>(() => provider.CreatePullRequestAsync(new("feature/x", "main", "T", ""), CancellationToken.None));
        Assert.Equal("Validation Failed: A pull request already exists for octo:feature/x.", error.Message);
        Assert.Equal(422, error.Status);
    }

    [Fact]
    public async Task DraftStateChangesThroughGraphQl()
    {
        foreach (var (target, graphQl) in new[] { (Target, "https://api.github.com/graphql"), (DevOpsTarget.FromRemoteUrl("https://github.contoso.com/octo/hello")!, "https://github.contoso.com/api/graphql") })
        {
            var (provider, http) = Create(h => h
                .On(HttpMethod.Post, "graphql", 200, """{"data":{"markPullRequestReadyForReview":{"pullRequest":{"isDraft":false}}}}""")
                .Json("/pulls/9/reviews", "[]")
                .Json("/pulls/9", Pull(9, draft: true).Replace("\"number\":9", "\"number\":9,\"node_id\":\"PR_kw9\"", StringComparison.Ordinal)), target);
            var pull = await provider.GetPullRequestAsync(9, CancellationToken.None);
            await provider.SetDraftAsync(pull, false, CancellationToken.None);
            var mutation = http.Single(HttpMethod.Post, "graphql");
            Assert.Equal(graphQl, mutation.Url);
            Assert.Contains("markPullRequestReadyForReview", mutation.Body, StringComparison.Ordinal);
            Assert.Equal("PR_kw9", JsonSerializer.Deserialize<JsonElement>(mutation.Body!).GetProperty("variables").GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task IssueListsLeaveOutPullRequests()
    {
        const string issues = """
            [{"number":3,"title":"Crash on start","state":"open","assignees":[{"login":"mona"},{"login":"octo"}],"labels":[{"name":"bug"},{"name":"ui"}],
              "milestone":{"title":"v2"},"updated_at":"2026-11-05T10:00:00Z","html_url":"https://github.com/octo/hello/issues/3"},
             {"number":4,"title":"A pull request","state":"open","pull_request":{"url":"x"}}]
            """;
        var (provider, http) = Create(h => h.Json("/user", """{"login":"mona"}""").Json("/issues?state=open", issues));
        var assigned = await provider.RunQueryAsync(provider.BuiltInQueries[0], 50, CancellationToken.None);
        var issue = Assert.Single(assigned);
        Assert.Equal((3, "Issue", "Open", "mona, octo", "bug; ui", "v2"), (issue.Id, issue.Type, issue.State, issue.AssignedTo, issue.Tags, issue.IterationPath));
        Assert.Equal(["bug", "ui"], issue.TagList);
        Assert.Equal("mona", http.Requests[^1].Query("assignee"));
        Assert.Empty(await provider.GetSavedQueriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IssuesAreClosedReopenedAndAssigned()
    {
        const string issue = """{"number":3,"title":"Crash","state":"closed","body":"Steps","user":{"login":"octo"},"html_url":"https://github.com/octo/hello/issues/3"}""";
        var (provider, http) = Create(h => h
            .On(HttpMethod.Patch, "/issues/3", 200, issue)
            .On(HttpMethod.Post, "/issues/3/assignees", 201, issue)
            .Json("/issues/3/comments", """[{"id":1,"user":{"login":"a"},"body":"first","created_at":"2026-11-01T10:00:00Z"},{"id":2,"user":{"login":"b"},"body":"second","created_at":"2026-11-02T10:00:00Z"}]""")
            .Json("/issues/3", issue));
        var details = await provider.GetWorkItemAsync(3, CancellationToken.None);
        Assert.Equal(["Open", "Closed"], details.AllowedStates);
        Assert.Equal(["second", "first"], details.Comments.Select(comment => comment.Text));
        Assert.Equal(("Steps", "octo"), (details.Description, details.CreatedBy));
        var closed = await provider.SetWorkItemStateAsync(details, "Closed", CancellationToken.None);
        Assert.Equal("Closed", closed.State);
        Assert.Equal("""{"state":"closed"}""", http.Single(HttpMethod.Patch, "/issues/3").Body);
        await Assert.ThrowsAsync<DevOpsException>(() => provider.SetWorkItemStateAsync(details, "Resolved", CancellationToken.None));
        await provider.AssignWorkItemAsync(details, Me, CancellationToken.None);
        Assert.Equal("""{"assignees":["mona"]}""", http.Single(HttpMethod.Post, "/assignees").Body);
    }

    [Fact]
    public async Task WorkflowRunsAndJobsBecomeRunsAndATimeline()
    {
        const string runs = """
            {"workflow_runs":[{"id":555,"workflow_id":77,"name":"CI","run_number":42,"status":"completed","conclusion":"failure","head_branch":"main","head_sha":"abc",
              "actor":{"login":"mona"},"event":"push","created_at":"2026-11-01T10:00:00Z","run_started_at":"2026-11-01T10:00:05Z","updated_at":"2026-11-01T10:03:05Z",
              "html_url":"https://github.com/octo/hello/actions/runs/555"},
             {"id":556,"workflow_id":77,"name":"CI","run_number":43,"status":"in_progress","conclusion":null,"head_branch":"main","event":"workflow_dispatch",
              "updated_at":"2026-11-01T11:00:00Z"}]}
            """;
        const string jobs = """
            {"jobs":[{"id":901,"name":"build","status":"completed","conclusion":"failure","started_at":"2026-11-01T10:00:10Z","completed_at":"2026-11-01T10:02:00Z",
              "steps":[{"name":"Set up job","status":"completed","conclusion":"success","number":1},{"name":"Run tests","status":"completed","conclusion":"failure","number":2}]}]}
            """;
        var (provider, http) = Create(h => h.Json("/actions/runs/555/jobs", jobs).Json("/actions/runs", runs));
        var list = await provider.ListRunsAsync(RunList.Mine, Me, "main", null, 20, CancellationToken.None);
        Assert.Equal("mona", http.Requests[0].Query("actor"));
        Assert.Null(http.Requests[0].Query("branch"));
        var run = list[0];
        Assert.Equal(("555", "77", "CI", "42", RunStatus.Completed, RunResult.Failed, "Push"), (run.Id, run.PipelineId, run.PipelineName, run.Number, run.Status, run.Result, run.Reason));
        Assert.Equal(("CI #42", "3m 0s"), (run.Title, run.DurationText));
        Assert.Equal((RunStatus.Running, "Manual", (DateTimeOffset?)null), (list[1].Status, list[1].Reason, list[1].Finished));
        var records = await provider.GetTimelineAsync(run, CancellationToken.None);
        Assert.Equal("latest", http.Requests[^1].Query("filter"));
        Assert.Equal(["job:901", "step:901:1", "step:901:2"], records.Select(record => record.Id));
        Assert.All(records, record => Assert.Equal("901", record.LogId));
        var tree = Assert.Single(PipelineTimeline.Build(records));
        Assert.Equal(["Set up job", "Run tests"], tree.Children.Select(child => child.Record.Name));
        Assert.Equal("step:901:2", PipelineTimeline.FirstFailure([tree])!.Record.Id);
    }

    [Fact]
    public async Task WorkflowsShowTheirLatestRun()
    {
        var (provider, _) = Create(h => h
            .Json("/actions/workflows", """{"workflows":[{"id":77,"name":"CI","path":".github/workflows/ci.yml","state":"active"},{"id":78,"name":"","path":".github/workflows/nightly.yml","state":"disabled_manually"}]}""")
            .Json("/actions/runs", """{"workflow_runs":[{"id":2,"workflow_id":77,"name":"CI","run_number":9,"status":"completed","conclusion":"success"},{"id":1,"workflow_id":77,"run_number":8,"status":"completed","conclusion":"failure"}]}"""));
        var workflows = await provider.ListPipelinesAsync(CancellationToken.None);
        Assert.Equal(["CI", "nightly.yml (disabled)"], workflows.Select(workflow => workflow.Name));
        Assert.Equal(("9", RunResult.Succeeded), (workflows[0].LatestRun!.Number, workflows[0].LatestRun!.Result));
        Assert.Equal("https://github.com/octo/hello/actions/workflows/ci.yml", workflows[0].WebUrl);
        Assert.Null(workflows[1].LatestRun);
    }

    [Fact]
    public async Task RunsAreStartedCancelledAndRetried()
    {
        var (provider, http) = Create(h => h
            .On(HttpMethod.Post, "/dispatches", 204)
            .On(HttpMethod.Post, "/cancel", 202, "{}")
            .On(HttpMethod.Post, "/rerun-failed-jobs", 201, "{}"));
        Assert.Null(await provider.QueueRunAsync("77", "feature/x", CancellationToken.None));
        Assert.Equal("""{"ref":"feature/x"}""", http.Single(HttpMethod.Post, "/actions/workflows/77/dispatches").Body);
        var run = new DevOpsRun("555", "77", "CI", "42", RunStatus.Running, RunResult.None, "https://github.com/octo/hello/actions/runs/555");
        await provider.CancelRunAsync(run, CancellationToken.None);
        await provider.RetryRunAsync(run, CancellationToken.None);
        Assert.Equal([Api + "actions/runs/555/cancel", Api + "actions/runs/555/rerun-failed-jobs"], http.Requests.Skip(1).Select(request => request.Url));
        Assert.Empty(await provider.ListApprovalsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task JobLogsComeFromStorageWithoutTheToken()
    {
        var (provider, http) = Create(h => h
            .On(HttpMethod.Get, "/actions/jobs/901/logs", 302, "", headers: new Dictionary<string, string> { ["Location"] = "https://pipelines.actions.githubusercontent.com/logs/901?sig=abc" })
            .On(HttpMethod.Get, "sig=abc", 200, "2026-11-01T10:00:00.000Z ##[group]Run tests\nok", "text/plain"));
        var run = new DevOpsRun("555", "77", "CI", "42", RunStatus.Completed, RunResult.Failed, "https://github.com/octo/hello/actions/runs/555");
        var record = new DevOpsTimelineRecord("job:901", null, "Job", "build", 0, RunStatus.Completed, RunResult.Failed, LogId: "901");
        var log = await provider.GetLogAsync(run, record, CancellationToken.None);
        Assert.StartsWith("2026-11-01", log, StringComparison.Ordinal);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("Bearer ghp_secret", http.Requests[0].Authorization);
        Assert.Equal("pipelines.actions.githubusercontent.com", http.Requests[1].Uri.Host);
        Assert.Null(http.Requests[1].Authorization);
    }

    [Fact]
    public async Task ARefusedTokenIsAnAuthenticationFailure()
    {
        var (provider, _) = Create(h => h.On(null, "/user", 401, """{"message":"Bad credentials"}"""));
        var error = await Assert.ThrowsAsync<DevOpsAuthenticationException>(() => provider.GetCurrentUserAsync(CancellationToken.None));
        Assert.Contains("Sign in again", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExhaustedRateLimitSaysWhenItResetsAndOtherRefusalsNameTheScope()
    {
        var (limited, _) = Create(h => h.On(null, "/pulls", 403, """{"message":"API rate limit exceeded for user."}""",
            headers: new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = "1700000000" }));
        var error = await Assert.ThrowsAsync<DevOpsException>(() => limited.ListPullRequestsAsync(PullRequestList.Active, Me, 10, CancellationToken.None));
        Assert.IsNotType<DevOpsAccessDeniedException>(error);
        Assert.Contains("rate limit is used up until", error.Message, StringComparison.Ordinal);

        var (refused, _) = Create(h => h.On(null, "/pulls", 403, """{"message":"Resource not accessible by personal access token"}""",
            headers: new Dictionary<string, string> { ["x-accepted-oauth-scopes"] = "repo" }));
        var denied = await Assert.ThrowsAsync<DevOpsAccessDeniedException>(() => refused.ListPullRequestsAsync(PullRequestList.Active, Me, 10, CancellationToken.None));
        Assert.Equal("repo", denied.Requirement);
        Assert.Contains("needs repo", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRepositoryHintsThatItMayBePrivate()
    {
        var (provider, _) = Create(h => h.On(null, "/pulls", 404, """{"message":"Not Found"}"""));
        var error = await Assert.ThrowsAsync<DevOpsException>(() => provider.ListPullRequestsAsync(PullRequestList.Active, Me, 10, CancellationToken.None));
        Assert.Equal(404, error.Status);
        Assert.Contains("private", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsFollowTheLinkHeader()
    {
        var (provider, http) = Create(h => h
            .Json("page=2", """[{"name":"alpha"}]""")
            .On(HttpMethod.Get, "/branches", 200, """[{"name":"main"},{"name":"Beta"}]""",
                headers: new Dictionary<string, string> { ["Link"] = $"<{Api}branches?per_page=100&page=2>; rel=\"next\", <{Api}branches?per_page=100&page=2>; rel=\"last\"" }));
        var branches = await provider.ListBranchesAsync(CancellationToken.None);
        Assert.Equal(["alpha", "Beta", "main"], branches);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("2", http.Requests[1].Query("page"));
    }

    [Fact]
    public async Task ClassicTokensAreJudgedByTheirScopes()
    {
        var (provider, _) = Create(h => h
            .On(HttpMethod.Get, "/user", 200, """{"login":"mona"}""", headers: new Dictionary<string, string> { ["X-OAuth-Scopes"] = "repo, read:org" })
            .Json("/repos/octo/hello", """{"private":true,"default_branch":"main"}"""));
        var access = await provider.CheckAccessAsync(CancellationToken.None);
        Assert.All(access.Take(5), entry => Assert.Equal(AccessState.Granted, entry.State));
        Assert.Equal(AccessState.Denied, Assert.Single(access, entry => entry.Requirement == "workflow").State);
        Assert.Equal(AccessState.Granted, Assert.Single(access, entry => entry.Requirement == "read:org").State);
        Assert.Equal("main", await provider.GetDefaultBranchAsync(CancellationToken.None));

        var (publicOnly, _) = Create(h => h
            .On(HttpMethod.Get, "/user", 200, """{"login":"mona"}""", headers: new Dictionary<string, string> { ["X-OAuth-Scopes"] = "public_repo" })
            .Json("/repos/octo/hello", """{"private":false}"""));
        Assert.All((await publicOnly.CheckAccessAsync(CancellationToken.None)).Take(5), entry => Assert.Equal(AccessState.Granted, entry.State));
    }

    [Fact]
    public async Task FineGrainedTokensAreJudgedWhenTheyAreUsed()
    {
        var (provider, _) = Create(h => h.Json("/user", """{"login":"mona"}""").Json("/repos/octo/hello", """{"private":true}"""));
        var access = await provider.CheckAccessAsync(CancellationToken.None);
        Assert.Equal(5, access.Count);
        Assert.Equal([AccessState.Granted, AccessState.Unknown, AccessState.Unknown, AccessState.Granted, AccessState.Unknown], access.Select(entry => entry.State));
        Assert.Contains("Fine-grained", access[1].StateText, StringComparison.Ordinal);
        Assert.StartsWith("https://github.com/settings/tokens/new?scopes=repo,workflow", provider.TokenPageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdsFromTheServiceStayOnePathSegment()
    {
        var (provider, http) = Create(_ => { });
        var run = new DevOpsRun("../../../../user", "77", "CI", "1", RunStatus.Completed, RunResult.None, "x");
        await Assert.ThrowsAsync<DevOpsException>(() => provider.GetRunAsync(run, CancellationToken.None));
        var request = Assert.Single(http.Requests);
        Assert.Equal("api.github.com", request.Uri.Host);
        Assert.Equal(Api + "actions/runs/..%2F..%2F..%2F..%2Fuser", request.Url);
    }

    /// <summary>Records each request's headers before the fake service answers it.</summary>
    private sealed class HeaderCapture(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<Dictionary<string, string>> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
