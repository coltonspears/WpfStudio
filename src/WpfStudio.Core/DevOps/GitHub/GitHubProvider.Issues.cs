using System.Globalization;
using System.Text.Json;

namespace WpfStudio.Core.DevOps.GitHub;

public sealed partial class GitHubProvider
{
    private const string AssignedQuery = "assigned", CreatedQuery = "created", MentionedQuery = "mentioned", OpenQuery = "open";
    private static readonly IReadOnlyList<string> IssueStates = ["Open", "Closed"];

    public IReadOnlyList<DevOpsWorkItemQuery> BuiltInQueries { get; } =
    [
        new(AssignedQuery, "Assigned to me"), new(CreatedQuery, "Created by me"), new(MentionedQuery, "Mentioning me"), new(OpenQuery, "All open issues"),
    ];

    /// <summary>GitHub keeps no saved issue queries a token can read.</summary>
    public Task<IReadOnlyList<DevOpsWorkItemQuery>> GetSavedQueriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DevOpsWorkItemQuery>>([]);

    public async Task<IReadOnlyList<DevOpsWorkItem>> RunQueryAsync(DevOpsWorkItemQuery query, int top, CancellationToken cancellationToken)
    {
        top = Math.Clamp(top, 1, 200);
        var login = query.Id == OpenQuery ? null : (await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false)) is var me ? me.UniqueName ?? me.Id : null;
        var filter = query.Id switch
        {
            AssignedQuery => ("assignee", login), CreatedQuery => ("creator", login), MentionedQuery => ("mentioned", login), OpenQuery => ("state", "open"),
            _ => throw new DevOpsException("Unknown issue list.")
        };
        var issues = await PagesAsync(RepoUrl("issues", ("state", "open"), filter, ("sort", "updated"), ("per_page", 100)), top, cancellationToken,
            keep: IsIssue).ConfigureAwait(false);
        return issues.Select(Issue).ToList();
    }

    public async Task<IReadOnlyList<DevOpsWorkItem>> SearchWorkItemsAsync(string text, int top, CancellationToken cancellationToken)
    {
        text = text.Trim().TrimStart('#');
        if (text.Length == 0) return [];
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            try
            {
                var issue = await GetAsync(RepoUrl($"issues/{number}"), cancellationToken).ConfigureAwait(false);
                return IsIssue(issue) ? [Issue(issue)] : [];
            }
            catch (DevOpsException ex) when (ex.Status == 404) { return []; }
        }
        var found = await GetAsync(Url("search/issues", ("q", $"{text} repo:{Target.Owner}/{Target.Repository} is:issue"), ("per_page", Math.Clamp(top, 1, 100))),
            cancellationToken).ConfigureAwait(false);
        return found.Items("items").Where(IsIssue).Select(Issue).ToList();
    }

    public async Task<DevOpsWorkItemDetails> GetWorkItemAsync(int id, CancellationToken cancellationToken)
    {
        var issueTask = GetAsync(RepoUrl($"issues/{id}"), cancellationToken);
        var commentsTask = PagesAsync(RepoUrl($"issues/{id}/comments", ("per_page", 100)), 300, cancellationToken);
        await Task.WhenAll(issueTask, commentsTask).ConfigureAwait(false);
        var issue = await issueTask;
        // Newest first, as Azure DevOps lists a work item's discussion.
        var comments = (await commentsTask).Select(comment => new DevOpsComment(comment.Text("id"), Identity(comment.Get("user")), comment.Text("body"),
            comment.Date("created_at") ?? DateTimeOffset.MinValue)).Reverse().ToList();
        var fields = new List<KeyValuePair<string, string>>();
        if (issue.Str("state_reason") is { Length: > 0 } reason && issue.Str("state") == "closed")
            fields.Add(new("Closed as", reason == "not_planned" ? "Not planned" : reason == "completed" ? "Completed" : reason));
        var links = new List<DevOpsWorkItemLink>();
        if (issue.Get("pull_request")?.Str("html_url") is { } pullUrl) links.Add(new("Pull request", "Pull request #" + id, pullUrl));
        return new(Issue(issue), issue.Text("body"), comments, links, IssueStates, 0, issue.Get("user")?.Str("login"), issue.Date("created_at"), fields);
    }

    public async Task<DevOpsWorkItem> SetWorkItemStateAsync(DevOpsWorkItemDetails item, string state, CancellationToken cancellationToken)
    {
        var value = state.Trim().ToLowerInvariant() switch
        {
            "open" or "reopened" => "open", "closed" => "closed",
            _ => throw new DevOpsException("A GitHub issue is either Open or Closed.")
        };
        return Issue(await JsonAsync(HttpMethod.Patch, RepoUrl($"issues/{item.Item.Id}"), new { state = value }, cancellationToken).ConfigureAwait(false));
    }

    public async Task<DevOpsWorkItem> AssignWorkItemAsync(DevOpsWorkItemDetails item, DevOpsIdentity? assignee, CancellationToken cancellationToken)
    {
        var updated = assignee is null
            ? await JsonAsync(HttpMethod.Patch, RepoUrl($"issues/{item.Item.Id}"), new { assignees = Array.Empty<string>() }, cancellationToken).ConfigureAwait(false)
            : await JsonAsync(HttpMethod.Post, RepoUrl($"issues/{item.Item.Id}/assignees"), new { assignees = new[] { assignee.UniqueName ?? assignee.Id } }, cancellationToken).ConfigureAwait(false);
        return Issue(updated);
    }

    public async Task AddWorkItemCommentAsync(DevOpsWorkItem item, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new DevOpsException("Write a comment first.");
        await JsonAsync(HttpMethod.Post, RepoUrl($"issues/{item.Id}/comments"), new { body = text }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> GetWorkItemTypesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["Issue"]);

    public async Task<DevOpsWorkItem> CreateWorkItemAsync(string type, string title, string description, DevOpsIdentity? assignee, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DevOpsException("Give the issue a title.");
        var created = await JsonAsync(HttpMethod.Post, RepoUrl("issues"), new
        {
            title = title.Trim(), body = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            assignees = assignee is null ? null : new[] { assignee.UniqueName ?? assignee.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Issue(created);
    }

    /// <summary>GitHub's issue lists include pull requests, which carry a <c>pull_request</c> member; the Issues tab leaves them out.</summary>
    private static bool IsIssue(JsonElement item) => item.ValueKind == JsonValueKind.Object && item.Get("pull_request") is null;

    private DevOpsWorkItem Issue(JsonElement issue)
    {
        var assignees = issue.Items("assignees").Select(user => user.Str("login")).OfType<string>().ToList();
        if (assignees.Count == 0 && issue.Get("assignee")?.Str("login") is { } single) assignees.Add(single);
        var labels = issue.Items("labels").Select(label => label.ValueKind == JsonValueKind.String ? label.GetString() : label.Str("name")).OfType<string>().ToList();
        return new(issue.Int("number") ?? 0, issue.Text("title"), "Issue", issue.Str("state") == "closed" ? "Closed" : "Open",
            assignees.Count == 0 ? null : string.Join(", ", assignees), issue.Str("html_url") ?? $"{Target.RepositoryWebUrl}/issues/{issue.Int("number")}",
            IterationPath: issue.Get("milestone")?.Str("title"), Tags: labels.Count == 0 ? null : string.Join("; ", labels), Changed: issue.Date("updated_at"));
    }
}
