using System.Text.Json;

namespace WpfStudio.Core.DevOps.AzureDevOps;

public sealed partial class AzureDevOpsProvider
{
    /// <summary>A file larger than this is not compared in the workbench, as in the Git pane.</summary>
    internal const int FileLimit = 2 * 1024 * 1024;

    public async Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(PullRequestList list, DevOpsIdentity me, int top, CancellationToken cancellationToken)
    {
        var status = list switch { PullRequestList.Completed => "completed", PullRequestList.Abandoned => "abandoned", _ => "active" };
        var data = await GetAsync(ProjectUrl(RepositoryPath + "/pullrequests",
            ("searchCriteria.status", status),
            ("searchCriteria.creatorId", list == PullRequestList.Mine ? me.Id : null),
            ("searchCriteria.reviewerId", list == PullRequestList.ReviewRequested ? me.Id : null),
            ("$top", Math.Clamp(top, 1, 500))), AzureDevOpsScopes.Code, cancellationToken).ConfigureAwait(false);
        return data.Items("value").Select(PullRequest).ToList();
    }

    public async Task<DevOpsPullRequest?> FindPullRequestAsync(string sourceBranch, CancellationToken cancellationToken)
    {
        var data = await GetAsync(ProjectUrl(RepositoryPath + "/pullrequests", ("searchCriteria.status", "active"),
            ("searchCriteria.sourceRefName", FullRef(sourceBranch)), ("$top", 1)), AzureDevOpsScopes.Code, cancellationToken).ConfigureAwait(false);
        return data.Items("value").Select(PullRequest).FirstOrDefault();
    }

    public async Task<DevOpsPullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken) =>
        PullRequest(await GetAsync(ProjectUrl($"_apis/git/pullrequests/{number}"), AzureDevOpsScopes.Code, cancellationToken).ConfigureAwait(false));

    public async Task<DevOpsPullRequestDetails> GetPullRequestDetailsAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var path = PullRequestPath(pullRequest);
        var threadsTask = GetAsync(Url(path + "/threads"), AzureDevOpsScopes.Code, cancellationToken);
        var commitsTask = GetAsync(Url(path + "/commits", ("$top", 100)), AzureDevOpsScopes.Code, cancellationToken);
        var workItemsTask = GetAsync(Url(path + "/workitems"), AzureDevOpsScopes.Code, cancellationToken);
        // Checks are a summary: a project whose policies this account cannot read still shows its pull requests.
        var statusesTask = OptionalAsync(GetAsync(Url(path + "/statuses", preview: 1), AzureDevOpsScopes.Code, cancellationToken));
        var policiesTask = pullRequest.ProjectId is { } projectId
            ? OptionalAsync(GetAsync(BuildUrl($"{_organization.Url}/{Escape(projectId)}", "_apis/policy/evaluations", true, 1,
                [("artifactId", $"vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequest.Number}")]), AzureDevOpsScopes.Code, cancellationToken))
            : Task.FromResult<JsonElement?>(null);
        await Task.WhenAll(threadsTask, commitsTask, workItemsTask, statusesTask, policiesTask).ConfigureAwait(false);

        var threads = (await threadsTask).Items("value").Where(thread => !thread.Bool("isDeleted")).Select(Thread).Where(thread => thread.Comments.Count > 0).ToList();
        var commits = (await commitsTask).Items("value").Select(commit => new DevOpsCommit(commit.Text("commitId"), commit.Text("comment"),
            commit.Get("author")?.Str("name") ?? "", commit.Get("author")?.Date("date"))).ToList();
        var workItems = (await workItemsTask).Items("value").Select(item => item.Int("id")).OfType<int>().ToList();
        var checks = new List<DevOpsCheck>();
        if (await policiesTask is { } policies)
            checks.AddRange(policies.Items("value").Where(policy => policy.Get("configuration")?.Bool("isEnabled") != false).Select(Policy));
        if (await statusesTask is { } statuses)
        {
            // Each iteration posts its statuses again; the newest for each name is the one that counts.
            checks.AddRange(statuses.Items("value")
                .GroupBy(status => (status.Get("context")?.Str("genre"), status.Get("context")?.Str("name")))
                .Select(group => group.OrderByDescending(status => status.Int("id") ?? 0).First())
                .Select(status => new DevOpsCheck(
                    string.Join('/', new[] { status.Get("context")?.Str("genre"), status.Get("context")?.Str("name") }.Where(part => !string.IsNullOrEmpty(part))),
                    status.Str("state") switch { "succeeded" => CheckState.Succeeded, "pending" => CheckState.Pending, "failed" or "error" => CheckState.Failed, "notApplicable" => CheckState.Neutral, _ => CheckState.Unknown },
                    status.Str("description"), status.Str("targetUrl"))));
        }
        return new(pullRequest, checks, threads, commits, workItems);
    }

    public async Task<IReadOnlyList<DevOpsPullRequestFile>> GetPullRequestFilesAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var path = PullRequestPath(pullRequest);
        var iterations = (await GetAsync(Url(path + "/iterations"), AzureDevOpsScopes.Code, cancellationToken).ConfigureAwait(false)).Items("value").ToList();
        if (iterations.Count == 0) return [];
        var iteration = iterations.Max(item => item.Int("id") ?? 0);
        var files = new List<DevOpsPullRequestFile>();
        var skip = 0;
        // The changes of the newest iteration against the target branch ($compareTo=0), a page at a time.
        for (var page = 0; page < 20; page++)
        {
            var changes = await GetAsync(Url($"{path}/iterations/{iteration}/changes", ("$top", 1000), ("$skip", skip), ("$compareTo", 0)),
                AzureDevOpsScopes.Code, cancellationToken).ConfigureAwait(false);
            foreach (var change in changes.Items("changeEntries"))
            {
                var item = change.Get("item");
                if (item is not { } file || file.Bool("isFolder") || file.Str("gitObjectType") == "tree" || file.Str("path") is not { } filePath) continue;
                var type = change.Text("changeType");
                var kind = type.Contains("delete", StringComparison.OrdinalIgnoreCase) ? FileChangeKind.Deleted
                    : type.Contains("add", StringComparison.OrdinalIgnoreCase) ? FileChangeKind.Added
                    : type.Contains("rename", StringComparison.OrdinalIgnoreCase) ? FileChangeKind.Renamed : FileChangeKind.Modified;
                var original = change.Str("originalPath") ?? change.Str("sourceServerItem");
                files.Add(new(filePath.TrimStart('/'), kind == FileChangeKind.Renamed ? original?.TrimStart('/') : null, kind,
                    file.Str("originalObjectId"), kind == FileChangeKind.Deleted ? null : file.Str("objectId")));
            }
            if (changes.Int("nextSkip") is not { } next || next <= skip) break;
            skip = next;
        }
        return files;
    }

    public async Task<DevOpsFileVersions> GetPullRequestFileVersionsAsync(DevOpsPullRequest pullRequest, DevOpsPullRequestFile file, CancellationToken cancellationToken)
    {
        var repository = pullRequest.RepositoryId ?? Target.Repository;
        var project = pullRequest.ProjectId ?? Target.Project;
        async Task<string?> BlobAsync(string? objectId, string? path, string? commit)
        {
            string url;
            if (objectId is not null)
                url = BuildUrl($"{_organization.Url}/{Escape(project)}", $"_apis/git/repositories/{Escape(repository)}/blobs/{Escape(objectId)}", true, null, [("$format", "octetstream")]);
            else if (path is not null && commit is not null)
                url = BuildUrl($"{_organization.Url}/{Escape(project)}", $"_apis/git/repositories/{Escape(repository)}/items", true, null,
                    [("path", "/" + path), ("versionDescriptor.version", commit), ("versionDescriptor.versionType", "commit"), ("$format", "octetstream")]);
            else return null;
            var response = await SendAsync(HttpMethod.Get, url, AzureDevOpsScopes.Code, accept: "application/octet-stream", cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Body;
        }
        try
        {
            var before = file.Change == FileChangeKind.Added ? null : await BlobAsync(file.BaseObjectId, file.OriginalPath ?? file.Path, pullRequest.TargetCommit).ConfigureAwait(false);
            var after = file.Change == FileChangeKind.Deleted ? null : await BlobAsync(file.HeadObjectId, file.Path, pullRequest.SourceCommit).ConfigureAwait(false);
            return Versions(before, after);
        }
        catch (DevOpsException ex) when (ex.Message.Contains("MiB", StringComparison.Ordinal)) { return new(null, null, "This file is larger than the workbench compares. Open the pull request on the web to see it."); }
    }

    internal static DevOpsFileVersions Versions(string? before, string? after)
    {
        if (before?.Contains('\0') == true || after?.Contains('\0') == true) return new(null, null, "Binary file — there is no text to compare.");
        if (before?.Length > FileLimit || after?.Length > FileLimit) return new(null, null, "This file is larger than 2 MiB. Open the pull request on the web to compare it.");
        return new(before, after);
    }

    public async Task<DevOpsPullRequest> CreatePullRequestAsync(NewPullRequest request, CancellationToken cancellationToken)
    {
        var created = PullRequest(await JsonAsync(HttpMethod.Post, ProjectUrl(RepositoryPath + "/pullrequests", ("supportsIterations", "true")), AzureDevOpsScopes.CodeWrite, new
        {
            sourceRefName = FullRef(request.SourceBranch), targetRefName = FullRef(request.TargetBranch), title = request.Title, description = request.Description,
            isDraft = request.IsDraft, workItemRefs = request.WorkItemIds?.Select(id => new { id = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray()
        }, cancellationToken: cancellationToken).ConfigureAwait(false));
        if (request.AutoComplete && !request.IsDraft)
            created = await SetAutoCompleteAsync(created, await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false), true, new(), cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task VoteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, ReviewVote vote, string? comment, CancellationToken cancellationToken)
    {
        var value = vote switch
        {
            ReviewVote.Approved => 10, ReviewVote.ApprovedWithSuggestions => 5, ReviewVote.WaitingForAuthor => -5,
            ReviewVote.Rejected or ReviewVote.ChangesRequested => -10, _ => 0
        };
        await JsonAsync(HttpMethod.Put, Url($"{PullRequestPath(pullRequest)}/reviewers/{Escape(me.Id)}"), AzureDevOpsScopes.CodeWrite, new { vote = value }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(comment)) await AddCommentAsync(pullRequest, comment, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevOpsPullRequest> CompleteAsync(DevOpsPullRequest pullRequest, PullRequestCompletion completion, CancellationToken cancellationToken)
    {
        // Completing names the source commit it merges, so a push since the request was read is refused rather than merged unseen.
        var current = await GetPullRequestAsync(pullRequest.Number, cancellationToken).ConfigureAwait(false);
        if (current.SourceCommit != pullRequest.SourceCommit && pullRequest.SourceCommit is not null)
            throw new DevOpsException("New commits were pushed since this pull request was loaded. Refresh and review them before completing.");
        return await PatchAsync(current, new
        {
            status = "completed", lastMergeSourceCommit = new { commitId = current.SourceCommit }, completionOptions = CompletionOptions(completion)
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DevOpsPullRequest> SetAutoCompleteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, bool enabled, PullRequestCompletion completion, CancellationToken cancellationToken) =>
        PatchAsync(pullRequest, enabled
            ? new { autoCompleteSetBy = new { id = me.Id }, completionOptions = CompletionOptions(completion) }
            : (object)new { autoCompleteSetBy = new { id = AzureDevOpsScopes.ZeroGuid } }, cancellationToken);

    public Task<DevOpsPullRequest> SetStatusAsync(DevOpsPullRequest pullRequest, PullRequestStatus status, CancellationToken cancellationToken) =>
        PatchAsync(pullRequest, new { status = status == PullRequestStatus.Abandoned ? "abandoned" : "active" }, cancellationToken);

    public Task<DevOpsPullRequest> SetDraftAsync(DevOpsPullRequest pullRequest, bool isDraft, CancellationToken cancellationToken) =>
        PatchAsync(pullRequest, new { isDraft }, cancellationToken);

    public Task<DevOpsPullRequest> UpdatePullRequestAsync(DevOpsPullRequest pullRequest, string title, string description, CancellationToken cancellationToken) =>
        PatchAsync(pullRequest, new { title, description }, cancellationToken);

    public async Task AddCommentAsync(DevOpsPullRequest pullRequest, string text, DevOpsThread? thread, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new DevOpsException("Write a comment first.");
        var path = PullRequestPath(pullRequest);
        if (thread is null)
            await JsonAsync(HttpMethod.Post, Url(path + "/threads"), AzureDevOpsScopes.CodeWrite,
                new { comments = new[] { new { parentCommentId = 0, content = text, commentType = "text" } }, status = "active" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        else
            await JsonAsync(HttpMethod.Post, Url($"{path}/threads/{Escape(thread.Id)}/comments"), AzureDevOpsScopes.CodeWrite,
                new { content = text, parentCommentId = int.TryParse(thread.Comments.FirstOrDefault()?.Id, out var parent) ? parent : 1, commentType = "text" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SetThreadStatusAsync(DevOpsPullRequest pullRequest, DevOpsThread thread, ThreadStatus status, CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Patch, Url($"{PullRequestPath(pullRequest)}/threads/{Escape(thread.Id)}"), AzureDevOpsScopes.CodeWrite,
            new { status = status switch { ThreadStatus.Resolved => "fixed", ThreadStatus.Closed => "closed", _ => "active" } }, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken)
    {
        var data = await GetAsync(ProjectUrl(RepositoryPath + "/refs", ("filter", "heads/"), ("$top", 1000)), AzureDevOpsScopes.Code, cancellationToken).ConfigureAwait(false);
        return data.Items("value").Select(reference => ShortRef(reference.Str("name"))).Where(name => name.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string?> GetDefaultBranchAsync(CancellationToken cancellationToken) =>
        ShortRef((await RepositoryAsync(cancellationToken).ConfigureAwait(false)).Str("defaultBranch")) is { Length: > 0 } branch ? branch : null;

    /// <summary>The repository's record (its id, project and default branch), read once.</summary>
    private Task<JsonElement> RepositoryAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<JsonElement>(cancellationToken);
        Task<JsonElement> task;
        lock (_cacheGate)
        {
            if (_repository is { IsFaulted: false, IsCanceled: false } known) task = known;
            else task = _repository = GetAsync(ProjectUrl(RepositoryPath), AzureDevOpsScopes.Code, CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task<DevOpsPullRequest> PatchAsync(DevOpsPullRequest pullRequest, object body, CancellationToken cancellationToken) =>
        PullRequest(await JsonAsync(HttpMethod.Patch, Url(PullRequestPath(pullRequest)), AzureDevOpsScopes.CodeWrite, body, cancellationToken: cancellationToken).ConfigureAwait(false));

    private static object CompletionOptions(PullRequestCompletion completion) => new
    {
        mergeStrategy = completion.Strategy switch
        {
            MergeStrategy.NoFastForward => "noFastForward", MergeStrategy.Rebase => "rebase", MergeStrategy.RebaseMerge => "rebaseMerge", _ => "squash"
        },
        deleteSourceBranch = completion.DeleteSourceBranch, transitionWorkItems = completion.CompleteWorkItems, mergeCommitMessage = completion.MergeMessage
    };

    /// <summary>The pull request's REST path, by the ids it was read with so a request from another repository still resolves.</summary>
    private string PullRequestPath(DevOpsPullRequest pullRequest) =>
        $"{Escape(pullRequest.ProjectId ?? Target.Project)}/_apis/git/repositories/{Escape(pullRequest.RepositoryId ?? Target.Repository)}/pullrequests/{pullRequest.Number}";

    private string Url(string organizationPath, params (string Name, object? Value)[] query) => BuildUrl(_organization.Url, organizationPath, true, null, query);
    private string Url(string organizationPath, int preview, params (string Name, object? Value)[] query) => BuildUrl(_organization.Url, organizationPath, true, preview, query);

    private static async Task<JsonElement?> OptionalAsync(Task<JsonElement> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch (DevOpsException) { return null; }
    }

    private DevOpsPullRequest PullRequest(JsonElement item)
    {
        var repository = item.Get("repository");
        var project = repository?.Get("project");
        var number = item.Int("pullRequestId") ?? 0;
        var projectName = project?.Str("name") ?? Target.Project;
        var repositoryName = repository?.Str("name") ?? Target.Repository;
        var status = item.Str("status") switch { "completed" => PullRequestStatus.Completed, "abandoned" => PullRequestStatus.Abandoned, _ => PullRequestStatus.Active };
        return new(number, item.Text("title"), item.Text("description"), status, item.Bool("isDraft"), Identity(item.Get("createdBy")),
            item.Date("creationDate") ?? DateTimeOffset.MinValue, item.Date("closedDate"), ShortRef(item.Str("sourceRefName")), ShortRef(item.Str("targetRefName")),
            item.Items("reviewers").Select(reviewer => new DevOpsReviewer(Identity(reviewer), reviewer.Int("vote") switch
            {
                10 => ReviewVote.Approved, 5 => ReviewVote.ApprovedWithSuggestions, -5 => ReviewVote.WaitingForAuthor, -10 => ReviewVote.Rejected, _ => ReviewVote.None
            }, reviewer.Bool("isRequired"))).ToList(),
            $"{AzureDevOpsUrls.RepositoryWebUrl(_organization, projectName, repositoryName)}/pullrequest/{number}",
            item.Str("mergeStatus") switch
            {
                "succeeded" => PullRequestMergeState.Mergeable, "conflicts" => PullRequestMergeState.Conflicts,
                "rejectedByPolicy" or "failure" => PullRequestMergeState.Blocked, _ => PullRequestMergeState.Unknown
            },
            item.Get("lastMergeSourceCommit")?.Str("commitId"), item.Get("lastMergeTargetCommit")?.Str("commitId"),
            IsMerged: status == PullRequestStatus.Completed,
            AutoComplete: item.Get("autoCompleteSetBy") is { } setBy && setBy.Str("id") is { } id && id != AzureDevOpsScopes.ZeroGuid,
            IsFromFork: item.Get("forkSource") is not null,
            Labels: item.Items("labels").Where(label => label.Get("active") is not { ValueKind: JsonValueKind.False }).Select(label => label.Text("name")).ToList(),
            RepositoryId: repository?.Str("id") ?? Target.Repository, ProjectId: project?.Str("id") ?? Target.Project);
    }

    private static DevOpsThread Thread(JsonElement thread)
    {
        var comments = thread.Items("comments").Where(comment => !comment.Bool("isDeleted") && !string.IsNullOrWhiteSpace(comment.Str("content")))
            .Select(comment => new DevOpsComment(comment.Text("id"), Identity(comment.Get("author")), comment.Text("content"),
                comment.Date("publishedDate") ?? DateTimeOffset.MinValue)).ToList();
        var isSystem = thread.Items("comments").FirstOrDefault().Str("commentType") == "system";
        var status = thread.Str("status") switch
        {
            "active" or "pending" => ThreadStatus.Active, "fixed" or "wontFix" or "byDesign" => ThreadStatus.Resolved, "closed" => ThreadStatus.Closed, _ => ThreadStatus.Unknown
        };
        var context = thread.Get("threadContext");
        var line = context?.Get("rightFileStart")?.Int("line") ?? context?.Get("leftFileStart")?.Int("line");
        return new(thread.Text("id"), status, comments, context?.Str("filePath"), line, isSystem, CanResolve: !isSystem && status != ThreadStatus.Unknown);
    }

    private static DevOpsCheck Policy(JsonElement policy)
    {
        var configuration = policy.Get("configuration");
        var name = configuration?.Get("settings")?.Str("displayName") ?? configuration?.Get("type")?.Str("displayName") ?? "Policy";
        var state = policy.Str("status") switch
        {
            "approved" => CheckState.Succeeded, "rejected" or "broken" => CheckState.Failed, "running" => CheckState.Running,
            "queued" => CheckState.Pending, "notApplicable" => CheckState.Neutral, _ => CheckState.Unknown
        };
        var expired = policy.Get("context")?.Bool("isExpired") == true;
        return new(name, state, expired ? "Expired: queue it again" : null, IsRequired: configuration?.Bool("isBlocking") == true);
    }
}
