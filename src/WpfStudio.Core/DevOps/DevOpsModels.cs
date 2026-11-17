using System.Globalization;
using WpfStudio.Core.Git;

namespace WpfStudio.Core.DevOps;

public enum DevOpsProviderKind { AzureDevOps, GitHub }

/// <summary>A person or group as a DevOps service names them.</summary>
public sealed record DevOpsIdentity(string Id, string DisplayName, string? UniqueName = null, bool IsGroup = false)
{
    public static DevOpsIdentity Unknown { get; } = new("", "Unknown");
    public string Initials => GitText.Initials(DisplayName);
    public int AvatarIndex => GitText.AvatarIndex(DisplayName);
    /// <summary>True when <paramref name="other"/> is the same account, compared by id and then by sign-in name.</summary>
    public bool IsSame(DevOpsIdentity? other) => other is not null && (Id.Length > 0 && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase)
        || UniqueName is { Length: > 0 } name && string.Equals(name, other.UniqueName, StringComparison.OrdinalIgnoreCase));
}

// ------------------------------------------------------------------ Pull requests

public enum PullRequestStatus { Active, Completed, Abandoned }

public enum PullRequestMergeState { Unknown, Mergeable, Conflicts, Blocked }

/// <summary>
/// A reviewer's position. Azure DevOps votes use the first five; GitHub reviews map to <see cref="Approved"/>,
/// <see cref="ChangesRequested"/> and <see cref="Commented"/>.
/// </summary>
public enum ReviewVote { None, Approved, ApprovedWithSuggestions, WaitingForAuthor, Rejected, ChangesRequested, Commented }

public sealed record DevOpsReviewer(DevOpsIdentity Identity, ReviewVote Vote, bool IsRequired = false)
{
    public string VoteText => DevOpsText.VoteLabel(Vote);
}

public sealed record DevOpsPullRequest(
    int Number, string Title, string Description, PullRequestStatus Status, bool IsDraft,
    DevOpsIdentity Author, DateTimeOffset Created, DateTimeOffset? Closed,
    string SourceBranch, string TargetBranch, IReadOnlyList<DevOpsReviewer> Reviewers, string WebUrl,
    PullRequestMergeState MergeState = PullRequestMergeState.Unknown, string? SourceCommit = null, string? TargetCommit = null,
    bool IsMerged = false, bool AutoComplete = false, bool IsFromFork = false,
    IReadOnlyList<string>? Labels = null, string? RepositoryId = null, string? ProjectId = null)
{
    public IReadOnlyList<string> LabelList => Labels ?? [];
    public bool IsActive => Status == PullRequestStatus.Active;
    /// <summary>Active, Draft, Completed (Merged on GitHub), or Abandoned (Closed on GitHub).</summary>
    public string StatusText => Status switch
    {
        PullRequestStatus.Active => IsDraft ? "Draft" : "Active",
        PullRequestStatus.Completed => IsMerged ? "Merged" : "Completed",
        _ => IsMerged ? "Merged" : RepositoryId is null ? "Closed" : "Abandoned"
    };
    public string BranchText => $"{SourceBranch} → {TargetBranch}";
    public string CreatedText => DevOpsText.Relative(Created);
    public string Summary => $"!{Number} · {Author.DisplayName} · {CreatedText}";
    public int Approvals => Reviewers.Count(r => r.Vote is ReviewVote.Approved or ReviewVote.ApprovedWithSuggestions);
    public bool HasRejection => Reviewers.Any(r => r.Vote is ReviewVote.Rejected or ReviewVote.ChangesRequested or ReviewVote.WaitingForAuthor);
    public string ReviewText => Reviewers.Count == 0 ? "No reviewers" :
        HasRejection ? "Changes requested" : Approvals > 0 ? $"{Approvals} approval{(Approvals == 1 ? "" : "s")}" : "Awaiting review";
}

public enum FileChangeKind { Modified, Added, Deleted, Renamed }

/// <summary>A file a pull request changes. The object ids name each side's blob where the service gives them.</summary>
public sealed record DevOpsPullRequestFile(string Path, string? OriginalPath, FileChangeKind Change, string? BaseObjectId = null,
    string? HeadObjectId = null, int? Additions = null, int? Deletions = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => GitText.Folder(Path) + (OriginalPath is null ? "" : $"  ← {OriginalPath}");
    public string Letter => Change switch { FileChangeKind.Added => "A", FileChangeKind.Deleted => "D", FileChangeKind.Renamed => "R", _ => "M" };
    public string AddedText => Additions is > 0 ? $"+{Additions}" : "";
    public string RemovedText => Deletions is > 0 ? $"−{Deletions}" : "";
}

/// <summary>Both sides of a changed file. Null text means that side does not exist; a message replaces both for binary or oversized files.</summary>
public sealed record DevOpsFileVersions(string? Before, string? After, string? Message = null);

public enum ThreadStatus { Active, Resolved, Closed, Unknown }

public sealed record DevOpsComment(string Id, DevOpsIdentity Author, string Text, DateTimeOffset Created)
{
    public string CreatedText => DevOpsText.Relative(Created);
}

/// <summary>A discussion: one comment and its replies, on the pull request as a whole or on a file line.</summary>
public sealed record DevOpsThread(string Id, ThreadStatus Status, IReadOnlyList<DevOpsComment> Comments, string? FilePath = null, int? Line = null,
    bool IsSystem = false, bool CanResolve = false)
{
    public bool IsResolved => Status is ThreadStatus.Resolved or ThreadStatus.Closed;
    public string Location => FilePath is null ? "" : Line is { } line ? $"{FilePath.TrimStart('/')}:{line}" : FilePath.TrimStart('/');
    public string StatusText => Status switch { ThreadStatus.Active => "Active", ThreadStatus.Resolved => "Resolved", ThreadStatus.Closed => "Closed", _ => "" };
}

public enum CheckState { Pending, Running, Succeeded, Failed, Neutral, Unknown }

/// <summary>A status check, build validation or branch policy reported against a pull request.</summary>
public sealed record DevOpsCheck(string Name, CheckState State, string? Description = null, string? Url = null, bool IsRequired = false)
{
    public string StateText => State switch
    {
        CheckState.Pending => "Pending", CheckState.Running => "Running", CheckState.Succeeded => "Succeeded",
        CheckState.Failed => "Failed", CheckState.Neutral => "Not applicable", _ => "Unknown"
    };
    /// <summary>The state in the vocabulary of <see cref="DevOpsText.StatusKind"/>, for one status style across runs, steps and checks.</summary>
    public string StatusKind => State switch
    {
        CheckState.Pending => "Queued", CheckState.Running => "Running", CheckState.Succeeded => "Succeeded", CheckState.Failed => "Failed",
        CheckState.Neutral => "Skipped", _ => "Unknown"
    };
}

public sealed record DevOpsCommit(string Id, string Message, string Author, DateTimeOffset? Date)
{
    public string ShortId => Id[..Math.Min(8, Id.Length)];
    public string Subject => Message.Split('\n', 2)[0].TrimEnd('\r');
}

/// <summary>What a pull request's page shows besides the request itself.</summary>
public sealed record DevOpsPullRequestDetails(DevOpsPullRequest PullRequest, IReadOnlyList<DevOpsCheck> Checks, IReadOnlyList<DevOpsThread> Threads,
    IReadOnlyList<DevOpsCommit> Commits, IReadOnlyList<int> WorkItemIds);

public enum PullRequestList { Mine, ReviewRequested, Active, Completed, Abandoned }

public sealed record NewPullRequest(string SourceBranch, string TargetBranch, string Title, string Description, bool IsDraft = false,
    IReadOnlyList<int>? WorkItemIds = null, bool AutoComplete = false);

public enum MergeStrategy { Squash, NoFastForward, Rebase, RebaseMerge }

public sealed record PullRequestCompletion(MergeStrategy Strategy = MergeStrategy.Squash, bool DeleteSourceBranch = true, bool CompleteWorkItems = true,
    string? MergeMessage = null);

// ------------------------------------------------------------------ Work items

public sealed record DevOpsWorkItem(int Id, string Title, string Type, string State, string? AssignedTo, string WebUrl,
    string? IterationPath = null, string? AreaPath = null, string? Tags = null, int? Priority = null, DateTimeOffset? Changed = null, string? Project = null)
{
    public string Summary => $"#{Id} · {State}" + (string.IsNullOrEmpty(AssignedTo) ? "" : " · " + AssignedTo);
    public string TypeLetter => Type.Length == 0 ? "?" : Type[..1].ToUpperInvariant();
    public IReadOnlyList<string> TagList => string.IsNullOrWhiteSpace(Tags) ? [] :
        Tags.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string ChangedText => Changed is { } changed ? DevOpsText.Relative(changed) : "";
}

public sealed record DevOpsWorkItemLink(string Kind, string Title, string? Url = null, int? WorkItemId = null);

public sealed record DevOpsWorkItemDetails(DevOpsWorkItem Item, string Description, IReadOnlyList<DevOpsComment> Comments, IReadOnlyList<DevOpsWorkItemLink> Links,
    IReadOnlyList<string> AllowedStates, int Revision = 0, string? CreatedBy = null, DateTimeOffset? Created = null,
    IReadOnlyList<KeyValuePair<string, string>>? Fields = null)
{
    /// <summary>Further named fields (acceptance criteria, repro steps, effort), already turned into plain text.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraFields => Fields ?? [];
}

/// <summary>A list of work items: a built-in one (assigned to me) or one saved on the service.</summary>
public sealed record DevOpsWorkItemQuery(string Id, string Name, string? Folder = null, bool IsBuiltIn = true)
{
    public string Label => Folder is null ? Name : $"{Folder} / {Name}";
}

// ------------------------------------------------------------------ Pipelines

public enum RunStatus { Queued, Running, Completed, Cancelling, Unknown }
public enum RunResult { None, Succeeded, PartiallySucceeded, Failed, Canceled, Skipped }

public sealed record DevOpsRun(string Id, string PipelineId, string PipelineName, string Number, RunStatus Status, RunResult Result, string WebUrl,
    string? Branch = null, string? Commit = null, string? RequestedFor = null, string? Reason = null,
    DateTimeOffset? Queued = null, DateTimeOffset? Started = null, DateTimeOffset? Finished = null)
{
    public bool IsActive => Status is RunStatus.Queued or RunStatus.Running or RunStatus.Cancelling;
    public string StatusText => DevOpsText.RunStatusText(Status, Result);
    public string StatusKind => DevOpsText.StatusKind(Status, Result);
    public string Title => $"{PipelineName} #{Number}";
    public string DurationText => DevOpsText.Duration(Started, Finished);
    public string WhenText => Finished is { } finished ? DevOpsText.Relative(finished) : Started is { } started ? "started " + DevOpsText.Relative(started) :
        Queued is { } queued ? "queued " + DevOpsText.Relative(queued) : "";
    public string Summary => string.Join(" · ", new[] { Branch, RequestedFor, WhenText }.Where(part => !string.IsNullOrEmpty(part)));
}

public sealed record DevOpsPipeline(string Id, string Name, string WebUrl, string? Folder = null, DevOpsRun? LatestRun = null)
{
    public string Summary => LatestRun is { } run ? $"{run.StatusText} · {run.WhenText}" : "No runs yet";
}

public sealed record DevOpsIssue(bool IsError, string Message);

/// <summary>One stage, job or task (step on GitHub) of a run, flat, as the service lists them.</summary>
public sealed record DevOpsTimelineRecord(string Id, string? ParentId, string Type, string Name, int Order, RunStatus State, RunResult Result,
    DateTimeOffset? Started = null, DateTimeOffset? Finished = null, string? LogId = null, int Errors = 0, int Warnings = 0,
    IReadOnlyList<DevOpsIssue>? Issues = null)
{
    public IReadOnlyList<DevOpsIssue> IssueList => Issues ?? [];
    public bool HasLog => LogId is not null;
    public string StatusText => DevOpsText.RunStatusText(State, Result);
    public string StatusKind => DevOpsText.StatusKind(State, Result);
    public string Detail => string.Join(" · ", new[]
    {
        State == RunStatus.Queued ? "pending" : DevOpsText.Duration(Started, Finished),
        Errors > 0 ? $"{Errors} error{(Errors == 1 ? "" : "s")}" : "", Warnings > 0 ? $"{Warnings} warning{(Warnings == 1 ? "" : "s")}" : ""
    }.Where(part => part.Length > 0));
}

/// <summary>A pipeline stage waiting for someone to approve it.</summary>
public sealed record DevOpsApproval(string Id, string PipelineName, string? Instructions, DateTimeOffset? Created, IReadOnlyList<string> Approvers, string? WebUrl = null)
{
    public string Summary => (Approvers.Count == 0 ? "" : "Approvers: " + string.Join(", ", Approvers)) + (Created is { } created ? " · " + DevOpsText.Relative(created) : "");
}

public enum RunList { Recent, Mine, CurrentBranch }

// ------------------------------------------------------------------ Access

public enum AccessState { Granted, Denied, Unknown }

/// <summary>One thing the signed-in token can or cannot do, with what to enable on the service's token page.</summary>
public sealed record DevOpsAccessEntry(string Feature, string Description, string Requirement, AccessState State, string? Detail = null)
{
    public string StateText => State switch { AccessState.Granted => "Available", AccessState.Denied => "Needs " + Requirement, _ => Detail ?? "Not checked" };
}

/// <summary>What one service supports beyond the operations every provider has, and the words it uses for them.</summary>
public sealed record DevOpsCapabilities(
    string ServiceName, string WorkItemsName, string WorkItemName, string PipelinesName, string CompleteVerb, string AbandonVerb,
    bool Votes, bool AutoComplete, bool ResolveThreads, bool Approvals, bool SavedQueries, bool MergeStrategies, bool CompleteWorkItems,
    bool RetryFailedJobs, bool WorkItemTypes);

/// <summary>Formats shared by the DevOps views.</summary>
public static partial class DevOpsText
{
    public static string Relative(DateTimeOffset date) => GitText.Relative(date);

    public static string Duration(DateTimeOffset? start, DateTimeOffset? finish)
    {
        if (start is null) return "";
        var span = (finish ?? DateTimeOffset.Now) - start.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    public static string VoteLabel(ReviewVote vote) => vote switch
    {
        ReviewVote.Approved => "Approved", ReviewVote.ApprovedWithSuggestions => "Approved with suggestions",
        ReviewVote.WaitingForAuthor => "Waiting for author", ReviewVote.Rejected => "Rejected",
        ReviewVote.ChangesRequested => "Changes requested", ReviewVote.Commented => "Commented", _ => "No vote"
    };

    public static string RunStatusText(RunStatus status, RunResult result) => status switch
    {
        RunStatus.Queued => "Queued", RunStatus.Running => "Running", RunStatus.Cancelling => "Cancelling",
        RunStatus.Completed => result switch
        {
            RunResult.Succeeded => "Succeeded", RunResult.PartiallySucceeded => "Partially succeeded", RunResult.Failed => "Failed",
            RunResult.Canceled => "Canceled", RunResult.Skipped => "Skipped", _ => "Completed"
        },
        _ => "Unknown"
    };

    /// <summary>
    /// One word for how a run, step or check stands, which the views style alike: Succeeded, Failed, Warning (partly succeeded),
    /// Running, Queued, Canceled, Skipped or Unknown.
    /// </summary>
    public static string StatusKind(RunStatus status, RunResult result) => status switch
    {
        RunStatus.Queued => "Queued", RunStatus.Running or RunStatus.Cancelling => "Running",
        RunStatus.Completed => result switch
        {
            RunResult.Succeeded => "Succeeded", RunResult.PartiallySucceeded => "Warning", RunResult.Failed => "Failed",
            RunResult.Canceled => "Canceled", RunResult.Skipped => "Skipped", _ => "Unknown"
        },
        _ => "Unknown"
    };

    internal static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) && value.Year > 1 ? value : null;
}
