using System.Collections.Concurrent;
using System.Text.Json;
using WpfStudio.Core.DevOps.AzureDevOps;

namespace WpfStudio.Core.DevOps.GitHub;

public sealed partial class GitHubProvider
{
    /// <summary>The merge base of each base and head commit pair compared, so a pull request's files share one compare request.</summary>
    private readonly ConcurrentDictionary<string, Task<string?>> _mergeBases = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(PullRequestList list, DevOpsIdentity me, int top, CancellationToken cancellationToken)
    {
        top = Math.Clamp(top, 1, 500);
        var login = me.UniqueName ?? me.Id;
        bool Mine(JsonElement pull) => string.Equals(pull.Get("user")?.Str("login"), login, StringComparison.OrdinalIgnoreCase);
        bool Requested(JsonElement pull) => pull.Items("requested_reviewers").Any(reviewer => string.Equals(reviewer.Str("login"), login, StringComparison.OrdinalIgnoreCase));
        bool Merged(JsonElement pull) => pull.Str("merged_at") is not null;
        // The list endpoint filters by state only; who wrote or must review a request, and whether a closed one was merged, are read here.
        var (state, keep) = list switch
        {
            PullRequestList.Mine => ("open", (Func<JsonElement, bool>)Mine),
            PullRequestList.ReviewRequested => ("open", Requested),
            PullRequestList.Completed => ("closed", Merged),
            PullRequestList.Abandoned => ("closed", pull => !Merged(pull)),
            _ => ("open", (Func<JsonElement, bool>?)null)
        };
        var url = state == "closed"
            ? RepoUrl("pulls", ("state", "closed"), ("sort", "updated"), ("direction", "desc"), ("per_page", 100))
            : RepoUrl("pulls", ("state", "open"), ("per_page", Math.Min(100, keep is null ? top : 100)));
        // Filtered lists stop after a few pages: a request that old is no longer what these lists are for.
        var pulls = await PagesAsync(url, top, cancellationToken, keep: keep).ConfigureAwait(false);
        return pulls.Select(pull => PullRequest(pull)).ToList();
    }

    public async Task<DevOpsPullRequest?> FindPullRequestAsync(string sourceBranch, CancellationToken cancellationToken)
    {
        var pulls = await GetAsync(RepoUrl("pulls", ("state", "open"), ("head", $"{Target.Owner}:{sourceBranch}"), ("per_page", 1)), cancellationToken).ConfigureAwait(false);
        return pulls.Items().Select(pull => PullRequest(pull)).FirstOrDefault();
    }

    public async Task<DevOpsPullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken)
    {
        var pullTask = GetAsync(RepoUrl($"pulls/{number}"), cancellationToken);
        var reviewsTask = PagesAsync(RepoUrl($"pulls/{number}/reviews", ("per_page", 100)), 500, cancellationToken);
        await Task.WhenAll(pullTask, reviewsTask).ConfigureAwait(false);
        return PullRequest(await pullTask, await reviewsTask);
    }

    public async Task<DevOpsPullRequestDetails> GetPullRequestDetailsAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var number = pullRequest.Number;
        var sha = pullRequest.SourceCommit;
        // Checks are a summary: a token that cannot read them still shows the request.
        var checkRunsTask = sha is null ? Task.FromResult<JsonElement?>(null) : OptionalAsync(GetAsync(RepoUrl($"commits/{Escape(sha)}/check-runs", ("per_page", 100)), cancellationToken));
        var statusTask = sha is null ? Task.FromResult<JsonElement?>(null) : OptionalAsync(GetAsync(RepoUrl($"commits/{Escape(sha)}/status", ("per_page", 100)), cancellationToken));
        var issueCommentsTask = PagesAsync(RepoUrl($"issues/{number}/comments", ("per_page", 100)), 500, cancellationToken);
        var reviewCommentsTask = PagesAsync(RepoUrl($"pulls/{number}/comments", ("per_page", 100)), 1000, cancellationToken);
        var reviewsTask = PagesAsync(RepoUrl($"pulls/{number}/reviews", ("per_page", 100)), 500, cancellationToken);
        var commitsTask = PagesAsync(RepoUrl($"pulls/{number}/commits", ("per_page", 100)), 250, cancellationToken);
        await Task.WhenAll(checkRunsTask, statusTask, issueCommentsTask, reviewCommentsTask, reviewsTask, commitsTask).ConfigureAwait(false);

        var checks = new List<DevOpsCheck>();
        if (await checkRunsTask is { } runs)
            checks.AddRange(runs.Items("check_runs").Select(run => new DevOpsCheck(run.Text("name"), CheckRunState(run.Str("status"), run.Str("conclusion")),
                run.Get("output")?.Str("title"), run.Str("html_url") ?? run.Str("details_url"))));
        if (await statusTask is { } combined)
            checks.AddRange(combined.Items("statuses").Select(status => new DevOpsCheck(status.Text("context"), status.Str("state") switch
            {
                "success" => CheckState.Succeeded, "pending" => CheckState.Pending, "failure" or "error" => CheckState.Failed, _ => CheckState.Unknown
            }, status.Str("description"), status.Str("target_url"))));

        var threads = new List<DevOpsThread>();
        threads.AddRange((await issueCommentsTask).Select(comment => new DevOpsThread("issue:" + comment.Text("id"), ThreadStatus.Unknown, [Comment(comment)])));
        // A review comment answers the first comment of its thread (in_reply_to_id), never another reply.
        threads.AddRange((await reviewCommentsTask).GroupBy(comment => comment.Long("in_reply_to_id") ?? comment.Long("id") ?? 0)
            .Select(group =>
            {
                var ordered = group.OrderBy(comment => comment.Date("created_at") ?? DateTimeOffset.MinValue).ToList();
                var root = ordered.FirstOrDefault(comment => comment.Long("id") == group.Key);
                if (root.ValueKind != JsonValueKind.Object) root = ordered[0];
                return new DevOpsThread("review:" + group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), ThreadStatus.Unknown,
                    ordered.Select(Comment).ToList(), root.Str("path"), root.Int("line") ?? root.Int("original_line"));
            }));
        threads.AddRange((await reviewsTask).Where(review => !string.IsNullOrWhiteSpace(review.Str("body")))
            .Select(review => new DevOpsThread("reviewbody:" + review.Text("id"), ThreadStatus.Unknown,
                [new DevOpsComment(review.Text("id"), Identity(review.Get("user")), review.Text("body"), review.Date("submitted_at") ?? DateTimeOffset.MinValue)])));
        threads.Sort((left, right) => (left.Comments.FirstOrDefault()?.Created ?? DateTimeOffset.MinValue).CompareTo(right.Comments.FirstOrDefault()?.Created ?? DateTimeOffset.MinValue));

        var commits = (await commitsTask).Select(commit => new DevOpsCommit(commit.Text("sha"), commit.Get("commit")?.Str("message") ?? "",
            commit.Get("commit", "author")?.Str("name") ?? commit.Get("author")?.Str("login") ?? "", commit.Get("commit", "author")?.Date("date"))).ToList();
        // A request from a list carries no reviews; the details add each reviewer's vote.
        var reviewed = pullRequest with { Reviewers = MergeReviewers(pullRequest.Reviewers, Votes(await reviewsTask, pullRequest.Author)) };
        return new(reviewed, checks, threads, commits, DevOpsText.WorkItemReferences(pullRequest.Title, pullRequest.Description));
    }

    public async Task<IReadOnlyList<DevOpsPullRequestFile>> GetPullRequestFilesAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var files = await PagesAsync(RepoUrl($"pulls/{pullRequest.Number}/files", ("per_page", 100)), 3000, cancellationToken).ConfigureAwait(false);
        return files.Select(file =>
        {
            var kind = file.Str("status") switch
            {
                "added" or "copied" => FileChangeKind.Added, "removed" => FileChangeKind.Deleted, "renamed" => FileChangeKind.Renamed, _ => FileChangeKind.Modified
            };
            return new DevOpsPullRequestFile(file.Text("filename"), kind == FileChangeKind.Renamed ? file.Str("previous_filename") : null, kind,
                HeadObjectId: kind == FileChangeKind.Deleted ? null : file.Str("sha"), Additions: file.Int("additions"), Deletions: file.Int("deletions"));
        }).ToList();
    }

    /// <summary>
    /// Both sides as GitHub's diff shows them: the base side at the merge base of the two branches, not at the base branch's tip,
    /// so changes made to the base branch since do not show up as reverted. A fork's commits are read from this repository, which
    /// keeps every pull request's head under <c>refs/pull</c>.
    /// </summary>
    public async Task<DevOpsFileVersions> GetPullRequestFileVersionsAsync(DevOpsPullRequest pullRequest, DevOpsPullRequestFile file, CancellationToken cancellationToken)
    {
        try
        {
            string? before = null, after = null;
            if (file.Change != FileChangeKind.Added)
            {
                var mergeBase = await MergeBaseAsync(pullRequest, cancellationToken).ConfigureAwait(false) ?? pullRequest.TargetCommit ?? pullRequest.TargetBranch;
                before = await ContentAsync(file.OriginalPath ?? file.Path, mergeBase, cancellationToken).ConfigureAwait(false);
            }
            if (file.Change != FileChangeKind.Deleted)
                after = await ContentAsync(file.Path, pullRequest.SourceCommit ?? pullRequest.SourceBranch, cancellationToken).ConfigureAwait(false);
            return AzureDevOpsProvider.Versions(before, after);
        }
        catch (DevOpsException ex) when (ex.Message.Contains("MiB", StringComparison.Ordinal)) { return new(null, null, "This file is larger than the workbench compares. Open the pull request on the web to see it."); }
    }

    private Task<string?> MergeBaseAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest.TargetCommit is not { } baseCommit || pullRequest.SourceCommit is not { } headCommit) return Task.FromResult<string?>(null);
        var key = baseCommit + "..." + headCommit;
        var task = _mergeBases.GetOrAdd(key, _ => ReadMergeBaseAsync(baseCommit, headCommit, cancellationToken));
        // A failed or cancelled compare is asked again next time.
        if (task.IsFaulted || task.IsCanceled) { _mergeBases.TryRemove(key, out _); }
        return task;
    }

    private async Task<string?> ReadMergeBaseAsync(string baseCommit, string headCommit, CancellationToken cancellationToken)
    {
        var compare = await GetAsync(RepoUrl($"compare/{Escape(baseCommit)}...{Escape(headCommit)}", ("per_page", 1)), cancellationToken).ConfigureAwait(false);
        return compare.Get("merge_base_commit")?.Str("sha");
    }

    /// <summary>A file's text at a commit; null when the path does not exist there.</summary>
    private async Task<string?> ContentAsync(string path, string version, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Get, RepoUrl("contents/" + EscapePath(path), ("ref", version)), accept: "application/vnd.github.raw", cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Body;
        }
        catch (DevOpsException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<DevOpsPullRequest> CreatePullRequestAsync(NewPullRequest request, CancellationToken cancellationToken)
    {
        var body = request.Description;
        // GitHub links an issue to a pull request, and closes it on merge, when the description says so.
        var mentioned = DevOpsText.WorkItemReferences(body);
        var missing = (request.WorkItemIds ?? []).Where(id => !mentioned.Contains(id)).ToList();
        if (missing.Count > 0)
            body = (string.IsNullOrWhiteSpace(body) ? "" : body.TrimEnd() + "\n\n") + string.Join('\n', missing.Select(id => $"Resolves #{id}"));
        var created = await JsonAsync(HttpMethod.Post, RepoUrl("pulls"), new
        {
            title = request.Title, head = request.SourceBranch, @base = request.TargetBranch, body, draft = request.IsDraft
        }, cancellationToken).ConfigureAwait(false);
        return PullRequest(created);
    }

    public async Task VoteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, ReviewVote vote, string? comment, CancellationToken cancellationToken)
    {
        var reviewEvent = vote switch
        {
            ReviewVote.Approved or ReviewVote.ApprovedWithSuggestions => "APPROVE",
            ReviewVote.Rejected or ReviewVote.ChangesRequested or ReviewVote.WaitingForAuthor => "REQUEST_CHANGES",
            ReviewVote.Commented => "COMMENT",
            _ => throw new NotSupportedException("GitHub cannot withdraw a review from here. Dismiss it on the web.")
        };
        if (reviewEvent != "APPROVE" && string.IsNullOrWhiteSpace(comment))
            throw new DevOpsException(reviewEvent == "COMMENT" ? "Write the review comment first." : "GitHub needs a comment that says what to change.");
        await JsonAsync(HttpMethod.Post, RepoUrl($"pulls/{pullRequest.Number}/reviews"), new
        {
            @event = reviewEvent, body = string.IsNullOrWhiteSpace(comment) ? null : comment, commitId = pullRequest.SourceCommit
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevOpsPullRequest> CompleteAsync(DevOpsPullRequest pullRequest, PullRequestCompletion completion, CancellationToken cancellationToken)
    {
        var message = completion.MergeMessage?.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', 2);
        try
        {
            // The head commit it was read at goes with the merge, so a push since is refused (409) rather than merged unseen.
            await JsonAsync(HttpMethod.Put, RepoUrl($"pulls/{pullRequest.Number}/merge"), new
            {
                mergeMethod = completion.Strategy switch { MergeStrategy.NoFastForward => "merge", MergeStrategy.Rebase or MergeStrategy.RebaseMerge => "rebase", _ => "squash" },
                sha = pullRequest.SourceCommit,
                commitTitle = message is null || string.IsNullOrWhiteSpace(message[0]) ? null : message[0].Trim(),
                commitMessage = message is { Length: 2 } && !string.IsNullOrWhiteSpace(message[1]) ? message[1].Trim() : null
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DevOpsException ex) when (ex.Status == 409)
        {
            throw new DevOpsException("New commits were pushed since this pull request was loaded. Refresh and review them before merging.", 409, ex);
        }
        if (completion.DeleteSourceBranch && !pullRequest.IsFromFork)
        {
            try { await SendAsync(HttpMethod.Delete, RepoUrl("git/refs/heads/" + EscapePath(pullRequest.SourceBranch)), cancellationToken: cancellationToken).ConfigureAwait(false); }
            // Already deleted, by GitHub's own setting or by someone else.
            catch (DevOpsException ex) when (ex.Status is 404 or 422) { }
            catch (DevOpsException ex) when (ex is not DevOpsAuthenticationException)
            {
                throw new DevOpsException($"Merged, but the branch {pullRequest.SourceBranch} could not be deleted: {ex.Message}", ex.Status, ex);
            }
        }
        return await GetPullRequestAsync(pullRequest.Number, cancellationToken).ConfigureAwait(false);
    }

    public Task<DevOpsPullRequest> SetAutoCompleteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, bool enabled, PullRequestCompletion completion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Turn on auto-merge for GitHub pull requests on the web.");

    public async Task<DevOpsPullRequest> SetStatusAsync(DevOpsPullRequest pullRequest, PullRequestStatus status, CancellationToken cancellationToken)
    {
        var updated = await JsonAsync(HttpMethod.Patch, RepoUrl($"pulls/{pullRequest.Number}"),
            new { state = status == PullRequestStatus.Abandoned ? "closed" : "open" }, cancellationToken).ConfigureAwait(false);
        // Closing or reopening changes no review, and this answer does not carry them.
        return PullRequest(updated) with { Reviewers = pullRequest.Reviewers };
    }

    /// <summary>REST cannot change whether a pull request is a draft; GraphQL can, by the request's node id.</summary>
    public async Task<DevOpsPullRequest> SetDraftAsync(DevOpsPullRequest pullRequest, bool isDraft, CancellationToken cancellationToken)
    {
        var current = await GetAsync(RepoUrl($"pulls/{pullRequest.Number}"), cancellationToken).ConfigureAwait(false);
        if (current.Bool("draft") != isDraft)
        {
            var mutation = isDraft
                ? "mutation($id: ID!) { convertPullRequestToDraft(input: {pullRequestId: $id}) { pullRequest { isDraft } } }"
                : "mutation($id: ID!) { markPullRequestReadyForReview(input: {pullRequestId: $id}) { pullRequest { isDraft } } }";
            var result = await JsonAsync(HttpMethod.Post, GraphQlUrl, new { query = mutation, variables = new { id = current.Text("node_id") } }, cancellationToken).ConfigureAwait(false);
            var errors = result.Items("errors").Select(error => error.Str("message")).OfType<string>().ToList();
            if (errors.Count > 0) throw new DevOpsException(string.Join("; ", errors));
        }
        return await GetPullRequestAsync(pullRequest.Number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevOpsPullRequest> UpdatePullRequestAsync(DevOpsPullRequest pullRequest, string title, string description, CancellationToken cancellationToken)
    {
        var updated = await JsonAsync(HttpMethod.Patch, RepoUrl($"pulls/{pullRequest.Number}"), new { title, body = description }, cancellationToken).ConfigureAwait(false);
        return PullRequest(updated) with { Reviewers = pullRequest.Reviewers };
    }

    public async Task AddCommentAsync(DevOpsPullRequest pullRequest, string text, DevOpsThread? thread, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new DevOpsException("Write a comment first.");
        // A review thread takes replies on its first comment; GitHub's other comments have no threads, so a reply is a new comment.
        var url = thread?.Id is { } id && id.StartsWith("review:", StringComparison.Ordinal)
            ? RepoUrl($"pulls/{pullRequest.Number}/comments/{Escape(id[7..])}/replies")
            : RepoUrl($"issues/{pullRequest.Number}/comments");
        await JsonAsync(HttpMethod.Post, url, new { body = text }, cancellationToken).ConfigureAwait(false);
    }

    public Task SetThreadStatusAsync(DevOpsPullRequest pullRequest, DevOpsThread thread, ThreadStatus status, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Resolve GitHub review conversations on the web.");

    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken)
    {
        var branches = await PagesAsync(RepoUrl("branches", ("per_page", 100)), 1000, cancellationToken).ConfigureAwait(false);
        return branches.Select(branch => branch.Text("name")).Where(name => name.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string?> GetDefaultBranchAsync(CancellationToken cancellationToken) =>
        (await RepositoryAsync(cancellationToken).ConfigureAwait(false)).Str("default_branch") is { Length: > 0 } branch ? branch : null;

    private static CheckState CheckRunState(string? status, string? conclusion) => status switch
    {
        "completed" => conclusion switch
        {
            "success" => CheckState.Succeeded, "failure" or "timed_out" or "cancelled" or "startup_failure" => CheckState.Failed,
            "neutral" or "skipped" => CheckState.Neutral, "action_required" => CheckState.Pending, _ => CheckState.Unknown
        },
        "in_progress" => CheckState.Running,
        "queued" or "waiting" or "requested" or "pending" => CheckState.Pending,
        _ => CheckState.Unknown
    };

    private static DevOpsComment Comment(JsonElement comment) =>
        new(comment.Text("id"), Identity(comment.Get("user")), comment.Text("body"), comment.Date("created_at") ?? DateTimeOffset.MinValue);

    /// <summary>The requested reviewers first, keeping votes cast meanwhile, then everyone else who reviewed.</summary>
    private static List<DevOpsReviewer> MergeReviewers(IReadOnlyList<DevOpsReviewer> listed, IReadOnlyList<DevOpsReviewer> reviewed)
    {
        var result = listed.Select(reviewer => reviewed.FirstOrDefault(vote => vote.Identity.IsSame(reviewer.Identity)) ?? reviewer).ToList();
        result.AddRange(reviewed.Where(vote => !result.Any(reviewer => reviewer.Identity.IsSame(vote.Identity))));
        return result;
    }

    private DevOpsPullRequest PullRequest(JsonElement pull, IReadOnlyList<JsonElement>? reviews = null)
    {
        var merged = pull.Str("merged_at") is not null || pull.Bool("merged");
        var status = pull.Str("state") == "closed" ? merged ? PullRequestStatus.Completed : PullRequestStatus.Abandoned : PullRequestStatus.Active;
        var head = pull.Get("head");
        var @base = pull.Get("base");
        var author = Identity(pull.Get("user"));
        // A head repository GitHub no longer has (a deleted fork) is still a fork's.
        var headRepository = head?.Get("repo")?.Str("full_name");
        var baseRepository = @base?.Get("repo")?.Str("full_name") ?? $"{Target.Owner}/{Target.Repository}";
        var isFork = head is not null && !string.Equals(headRepository, baseRepository, StringComparison.OrdinalIgnoreCase);
        return new(pull.Int("number") ?? 0, pull.Text("title"), pull.Text("body"), status, pull.Bool("draft"), author,
            pull.Date("created_at") ?? DateTimeOffset.MinValue, merged ? pull.Date("merged_at") ?? pull.Date("closed_at") : pull.Date("closed_at"),
            head?.Str("ref") ?? "", @base?.Str("ref") ?? "", Reviewers(pull, reviews, author), pull.Text("html_url"),
            pull.Str("mergeable_state") switch
            {
                "clean" or "has_hooks" or "unstable" => PullRequestMergeState.Mergeable, "dirty" => PullRequestMergeState.Conflicts,
                "blocked" or "behind" => PullRequestMergeState.Blocked, _ => PullRequestMergeState.Unknown
            },
            head?.Str("sha"), @base?.Str("sha"), IsMerged: merged, AutoComplete: pull.Get("auto_merge") is not null, IsFromFork: isFork,
            Labels: pull.Items("labels").Select(label => label.Text("name")).Where(name => name.Length > 0).ToList());
    }

    /// <summary>
    /// Requested reviewers and teams, with each person's latest review: an approval or a change request stands until that person
    /// reviews again or it is dismissed, and a comment counts only when nothing stronger was said.
    /// </summary>
    private static List<DevOpsReviewer> Reviewers(JsonElement pull, IReadOnlyList<JsonElement>? reviews, DevOpsIdentity author)
    {
        var votes = Votes(reviews ?? [], author);
        var result = pull.Items("requested_reviewers").Select(reviewer => Identity(reviewer))
            .Select(identity => votes.FirstOrDefault(vote => vote.Identity.IsSame(identity)) ?? new DevOpsReviewer(identity, ReviewVote.None)).ToList();
        result.AddRange(votes.Where(vote => !result.Any(reviewer => reviewer.Identity.IsSame(vote.Identity))));
        result.AddRange(pull.Items("requested_teams").Select(team => new DevOpsReviewer(
            new DevOpsIdentity(team.Text("slug"), team.Str("name") ?? team.Text("slug"), team.Str("slug"), IsGroup: true), ReviewVote.None)));
        return result;
    }

    /// <summary>Each reviewer's standing vote, in the order they first reviewed. The author's own comments are not a review.</summary>
    private static List<DevOpsReviewer> Votes(IReadOnlyList<JsonElement> reviews, DevOpsIdentity author)
    {
        var votes = new List<DevOpsReviewer>();
        foreach (var review in reviews)
        {
            var identity = Identity(review.Get("user"));
            if (identity.IsSame(author)) continue;
            var vote = review.Str("state") switch
            {
                "APPROVED" => ReviewVote.Approved, "CHANGES_REQUESTED" => ReviewVote.ChangesRequested, "COMMENTED" => ReviewVote.Commented,
                "DISMISSED" => ReviewVote.None, _ => (ReviewVote?)null
            };
            if (vote is not { } cast) continue;
            var index = votes.FindIndex(existing => existing.Identity.IsSame(identity));
            if (index < 0) votes.Add(new(identity, cast));
            else if (cast != ReviewVote.Commented || votes[index].Vote is ReviewVote.None or ReviewVote.Commented) votes[index] = new(identity, cast);
        }
        return votes;
    }
}
