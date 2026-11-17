namespace WpfStudio.Core.DevOps;

/// <summary>
/// One repository's pull requests, work items (issues on GitHub) and pipelines (Actions on GitHub) on its hosting service.
/// Every call goes to the service: nothing is cached here, so a refresh always shows the current state. Operations a service
/// does not offer are listed in <see cref="Capabilities"/> and throw <see cref="NotSupportedException"/>.
/// </summary>
public interface IDevOpsProvider
{
    DevOpsTarget Target { get; }
    DevOpsCapabilities Capabilities { get; }

    /// <summary>Who the token belongs to. Also the cheapest check that the token is accepted at all.</summary>
    Task<DevOpsIdentity> GetCurrentUserAsync(CancellationToken cancellationToken);
    /// <summary>What the token can do, feature by feature. Checking never changes anything on the service.</summary>
    Task<IReadOnlyList<DevOpsAccessEntry>> CheckAccessAsync(CancellationToken cancellationToken);
    /// <summary>The web page where a token with more access can be created.</summary>
    string TokenPageUrl { get; }

    // Pull requests
    Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(PullRequestList list, DevOpsIdentity me, int top, CancellationToken cancellationToken);
    /// <summary>The active pull request from <paramref name="sourceBranch"/>, if there is one.</summary>
    Task<DevOpsPullRequest?> FindPullRequestAsync(string sourceBranch, CancellationToken cancellationToken);
    Task<DevOpsPullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken);
    Task<DevOpsPullRequestDetails> GetPullRequestDetailsAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken);
    Task<IReadOnlyList<DevOpsPullRequestFile>> GetPullRequestFilesAsync(DevOpsPullRequest pullRequest, CancellationToken cancellationToken);
    Task<DevOpsFileVersions> GetPullRequestFileVersionsAsync(DevOpsPullRequest pullRequest, DevOpsPullRequestFile file, CancellationToken cancellationToken);
    Task<DevOpsPullRequest> CreatePullRequestAsync(NewPullRequest request, CancellationToken cancellationToken);
    /// <summary>Records the signed-in user's vote or review. GitHub needs a <paramref name="comment"/> to request changes.</summary>
    Task VoteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, ReviewVote vote, string? comment, CancellationToken cancellationToken);
    Task<DevOpsPullRequest> CompleteAsync(DevOpsPullRequest pullRequest, PullRequestCompletion completion, CancellationToken cancellationToken);
    /// <summary>Sets or clears auto-complete: the service completes the request once its policies pass.</summary>
    Task<DevOpsPullRequest> SetAutoCompleteAsync(DevOpsPullRequest pullRequest, DevOpsIdentity me, bool enabled, PullRequestCompletion completion, CancellationToken cancellationToken);
    /// <summary>Abandons (closes) or reactivates (reopens) a pull request.</summary>
    Task<DevOpsPullRequest> SetStatusAsync(DevOpsPullRequest pullRequest, PullRequestStatus status, CancellationToken cancellationToken);
    Task<DevOpsPullRequest> SetDraftAsync(DevOpsPullRequest pullRequest, bool isDraft, CancellationToken cancellationToken);
    Task<DevOpsPullRequest> UpdatePullRequestAsync(DevOpsPullRequest pullRequest, string title, string description, CancellationToken cancellationToken);
    /// <summary>Adds a comment to the pull request, or a reply to <paramref name="thread"/>.</summary>
    Task AddCommentAsync(DevOpsPullRequest pullRequest, string text, DevOpsThread? thread, CancellationToken cancellationToken);
    Task SetThreadStatusAsync(DevOpsPullRequest pullRequest, DevOpsThread thread, ThreadStatus status, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken);
    /// <summary>The branch new pull requests target by default.</summary>
    Task<string?> GetDefaultBranchAsync(CancellationToken cancellationToken);

    // Work items
    IReadOnlyList<DevOpsWorkItemQuery> BuiltInQueries { get; }
    Task<IReadOnlyList<DevOpsWorkItemQuery>> GetSavedQueriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DevOpsWorkItem>> RunQueryAsync(DevOpsWorkItemQuery query, int top, CancellationToken cancellationToken);
    /// <summary>Finds work items by number or by words of their title.</summary>
    Task<IReadOnlyList<DevOpsWorkItem>> SearchWorkItemsAsync(string text, int top, CancellationToken cancellationToken);
    Task<DevOpsWorkItemDetails> GetWorkItemAsync(int id, CancellationToken cancellationToken);
    /// <summary>Changes the state, failing rather than overwriting when someone else changed the item since <paramref name="item"/> was read.</summary>
    Task<DevOpsWorkItem> SetWorkItemStateAsync(DevOpsWorkItemDetails item, string state, CancellationToken cancellationToken);
    /// <summary>Assigns the item to <paramref name="assignee"/>, or leaves it unassigned when null.</summary>
    Task<DevOpsWorkItem> AssignWorkItemAsync(DevOpsWorkItemDetails item, DevOpsIdentity? assignee, CancellationToken cancellationToken);
    Task AddWorkItemCommentAsync(DevOpsWorkItem item, string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetWorkItemTypesAsync(CancellationToken cancellationToken);
    Task<DevOpsWorkItem> CreateWorkItemAsync(string type, string title, string description, DevOpsIdentity? assignee, CancellationToken cancellationToken);

    // Pipelines
    Task<IReadOnlyList<DevOpsPipeline>> ListPipelinesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DevOpsRun>> ListRunsAsync(RunList list, DevOpsIdentity me, string? branch, string? pipelineId, int top, CancellationToken cancellationToken);
    Task<DevOpsRun> GetRunAsync(DevOpsRun run, CancellationToken cancellationToken);
    Task<IReadOnlyList<DevOpsTimelineRecord>> GetTimelineAsync(DevOpsRun run, CancellationToken cancellationToken);
    Task<string> GetLogAsync(DevOpsRun run, DevOpsTimelineRecord record, CancellationToken cancellationToken);
    /// <summary>Starts a run of <paramref name="pipelineId"/> on <paramref name="branch"/>. Null when the service accepted it without naming the run.</summary>
    Task<DevOpsRun?> QueueRunAsync(string pipelineId, string branch, CancellationToken cancellationToken);
    Task CancelRunAsync(DevOpsRun run, CancellationToken cancellationToken);
    /// <summary>Runs the failed jobs of a finished run again.</summary>
    Task RetryRunAsync(DevOpsRun run, CancellationToken cancellationToken);
    Task<IReadOnlyList<DevOpsApproval>> ListApprovalsAsync(CancellationToken cancellationToken);
    Task RespondToApprovalAsync(DevOpsApproval approval, bool approve, string comment, CancellationToken cancellationToken);
}
