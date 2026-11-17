using System.Globalization;
using System.Text.Json;

namespace WpfStudio.Core.DevOps.AzureDevOps;

public sealed partial class AzureDevOpsProvider
{
    private const string AssignedQuery = "assigned", IterationQuery = "iteration", CreatedQuery = "created", RecentQuery = "recent";
    private const string JsonPatch = "application/json-patch+json";

    /// <summary>The fields list views show.</summary>
    private static readonly string[] ListFields =
    [
        "System.Id", "System.Title", "System.State", "System.WorkItemType", "System.AssignedTo", "System.TeamProject",
        "System.IterationPath", "System.AreaPath", "System.Tags", "System.ChangedDate", "Microsoft.VSTS.Common.Priority"
    ];

    /// <summary>Rich-text and numeric fields shown below the description, by the label the work item form gives them.</summary>
    private static readonly (string Field, string Label)[] ExtraFieldNames =
    [
        ("Microsoft.VSTS.TCM.ReproSteps", "Repro steps"), ("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance criteria"),
        ("Microsoft.VSTS.TCM.SystemInfo", "System info"), ("Microsoft.VSTS.Scheduling.StoryPoints", "Story points"),
        ("Microsoft.VSTS.Scheduling.Effort", "Effort"), ("Microsoft.VSTS.Scheduling.RemainingWork", "Remaining work"),
        ("Microsoft.VSTS.Common.Severity", "Severity"), ("Microsoft.VSTS.Common.ValueArea", "Value area"),
    ];

    public IReadOnlyList<DevOpsWorkItemQuery> BuiltInQueries { get; } =
    [
        new(AssignedQuery, "Assigned to me"), new(IterationQuery, "Current iteration"), new(CreatedQuery, "Created by me"), new(RecentQuery, "Recently changed by me"),
    ];

    public async Task<IReadOnlyList<DevOpsWorkItemQuery>> GetSavedQueriesAsync(CancellationToken cancellationToken)
    {
        var data = await GetAsync(ProjectUrl("_apis/wit/queries", ("$depth", 2), ("$expand", "minimal")), AzureDevOpsScopes.Work, cancellationToken).ConfigureAwait(false);
        var queries = new List<DevOpsWorkItemQuery>();
        void Collect(JsonElement item, string? folder)
        {
            if (item.Bool("isFolder"))
            {
                foreach (var child in item.Items("children")) Collect(child, folder is null ? item.Text("name") : $"{folder} / {item.Text("name")}");
            }
            else if (item.Str("id") is { } id) queries.Add(new(id, item.Text("name"), folder, IsBuiltIn: false));
        }
        foreach (var root in data.Items("value")) Collect(root, null);
        return queries;
    }

    public async Task<IReadOnlyList<DevOpsWorkItem>> RunQueryAsync(DevOpsWorkItemQuery query, int top, CancellationToken cancellationToken)
    {
        top = Math.Clamp(top, 1, 200);
        if (!query.IsBuiltIn)
        {
            var saved = await GetAsync(ProjectUrl($"_apis/wit/wiql/{Escape(query.Id)}", ("$top", top)), AzureDevOpsScopes.Work, cancellationToken).ConfigureAwait(false);
            return await WorkItemsAsync(QueryIds(saved), cancellationToken).ConfigureAwait(false);
        }
        var select = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project";
        var hidden = HiddenStates.Count == 0 ? "" : $" AND [System.State] NOT IN ({string.Join(", ", HiddenStates.Select(DevOpsText.WiqlString))})";
        const string order = " ORDER BY [System.ChangedDate] DESC";
        string wiql;
        switch (query.Id)
        {
            case AssignedQuery: wiql = $"{select} AND [System.AssignedTo] = @me{hidden}{order}"; break;
            case CreatedQuery: wiql = $"{select} AND [System.CreatedBy] = @me{hidden}{order}"; break;
            case RecentQuery: wiql = $"{select} AND [System.ChangedBy] = @me AND [System.ChangedDate] >= @today - 14{order}"; break;
            case IterationQuery:
                var iterations = await GetAsync(ProjectUrl("_apis/work/teamsettings/iterations", ("$timeframe", "current")), AzureDevOpsScopes.Work, cancellationToken).ConfigureAwait(false);
                if (iterations.Items("value").FirstOrDefault().Str("path") is not { } path) return [];
                wiql = $"{select} AND [System.IterationPath] = {DevOpsText.WiqlString(path)}{hidden} ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.Id] ASC";
                break;
            default: throw new DevOpsException("Unknown work item list.");
        }
        return await WiqlAsync(wiql, top, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevOpsWorkItem>> SearchWorkItemsAsync(string text, int top, CancellationToken cancellationToken)
    {
        text = text.Trim().TrimStart('#');
        if (text.Length == 0) return [];
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return await WorkItemsAsync([id], cancellationToken).ConfigureAwait(false);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => $"[System.Title] CONTAINS {DevOpsText.WiqlString(word)}");
        return await WiqlAsync($"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND {string.Join(" AND ", words)} ORDER BY [System.ChangedDate] DESC",
            Math.Clamp(top, 1, 200), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevOpsWorkItemDetails> GetWorkItemAsync(int id, CancellationToken cancellationToken)
    {
        var item = await GetAsync(CoreUrl($"_apis/wit/workitems/{id}", ("$expand", "all")), AzureDevOpsScopes.Work, cancellationToken).ConfigureAwait(false);
        var summary = WorkItem(item);
        var fields = item.Get("fields");
        var project = summary.Project ?? Target.Project;
        var commentsTask = OptionalAsync(GetAsync(BuildUrl($"{_organization.Url}/{Escape(project)}", $"_apis/wit/workItems/{id}/comments", true, 3,
            [("$top", 50), ("order", "desc")]), AzureDevOpsScopes.Work, cancellationToken));
        var statesTask = OptionalAsync(GetAsync(BuildUrl($"{_organization.Url}/{Escape(project)}", $"_apis/wit/workitemtypes/{Escape(summary.Type)}/states", true, null, []),
            AzureDevOpsScopes.Work, cancellationToken));
        var links = new List<DevOpsWorkItemLink>();
        var linkedIds = new List<(int Id, string Kind)>();
        foreach (var relation in item.Items("relations"))
        {
            var rel = relation.Text("rel");
            var url = relation.Text("url");
            var name = relation.Get("attributes")?.Str("name");
            if (rel.StartsWith("System.LinkTypes", StringComparison.Ordinal) || rel.StartsWith("Microsoft.VSTS", StringComparison.Ordinal))
            {
                var kind = rel switch
                {
                    "System.LinkTypes.Hierarchy-Reverse" => "Parent", "System.LinkTypes.Hierarchy-Forward" => "Child", "System.LinkTypes.Related" => "Related",
                    _ => name ?? rel.Split('.')[^1]
                };
                if (int.TryParse(url.Split('/')[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var linked)) linkedIds.Add((linked, kind));
            }
            else if (rel == "ArtifactLink")
            {
                // vstfs:///Git/{Kind}/{project}%2F{repository}%2F{id}: the id is the last part, and a branch's own slashes are escaped too.
                var parts = url[(url.LastIndexOf('/') + 1)..].Split("%2F", 3, StringSplitOptions.None);
                var artifact = Uri.UnescapeDataString(parts[^1]);
                var title = name switch
                {
                    "Pull Request" => "Pull request !" + artifact,
                    "Branch" => "Branch " + (artifact.StartsWith("GB", StringComparison.Ordinal) ? artifact[2..] : artifact),
                    "Fixed in Commit" or "Commit" => "Commit " + artifact[..Math.Min(8, artifact.Length)],
                    _ => name ?? "Artifact"
                };
                links.Add(new(name ?? "Artifact", title));
            }
            else if (rel == "Hyperlink") links.Add(new("Hyperlink", relation.Get("attributes")?.Str("comment") is { Length: > 0 } comment ? comment : url, url));
        }
        if (linkedIds.Count > 0)
        {
            var linkedItems = (await OptionalWorkItemsAsync(linkedIds.Select(link => link.Id).Distinct().ToList(), cancellationToken).ConfigureAwait(false)).ToDictionary(work => work.Id);
            links.InsertRange(0, linkedIds.Select(link => linkedItems.TryGetValue(link.Id, out var work)
                ? new DevOpsWorkItemLink(link.Kind, $"{work.Type} {work.Id}: {work.Title} ({work.State})", work.WebUrl, work.Id)
                : new DevOpsWorkItemLink(link.Kind, $"Work item {link.Id}", WorkItemUrl(project, link.Id), link.Id)));
        }
        await Task.WhenAll(commentsTask, statesTask).ConfigureAwait(false);
        var comments = (await commentsTask)?.Items("comments").Select(comment => new DevOpsComment(comment.Text("id"), Identity(comment.Get("createdBy")),
            DevOpsText.HtmlToText(comment.Str("text")), comment.Date("createdDate") ?? DateTimeOffset.MinValue)).ToList() ?? [];
        var states = (await statesTask)?.Items("value").Select(state => state.Text("name")).Where(name => name.Length > 0).ToList() ?? [];
        if (!states.Contains(summary.State)) states.Insert(0, summary.State);
        var extra = ExtraFieldNames.Select(field => (field.Label, Value: fields?.Get(field.Field) is { } value
                ? value.ValueKind == JsonValueKind.String ? DevOpsText.HtmlToText(value.GetString()) : value.GetRawText() : ""))
            .Where(field => field.Value.Length > 0).Select(field => new KeyValuePair<string, string>(field.Label, field.Value)).ToList();
        return new(summary, DevOpsText.HtmlToText(fields?.Str("System.Description")), comments, links, states, item.Int("rev") ?? 0,
            IdentityName(fields?.Get("System.CreatedBy")), fields?.Date("System.CreatedDate"), extra);
    }

    public Task<DevOpsWorkItem> SetWorkItemStateAsync(DevOpsWorkItemDetails item, string state, CancellationToken cancellationToken) =>
        PatchWorkItemAsync(item, "System.State", state, cancellationToken);

    public Task<DevOpsWorkItem> AssignWorkItemAsync(DevOpsWorkItemDetails item, DevOpsIdentity? assignee, CancellationToken cancellationToken) =>
        PatchWorkItemAsync(item, "System.AssignedTo", assignee?.UniqueName ?? assignee?.DisplayName ?? "", cancellationToken);

    public async Task AddWorkItemCommentAsync(DevOpsWorkItem item, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new DevOpsException("Write a comment first.");
        await JsonAsync(HttpMethod.Post, BuildUrl($"{_organization.Url}/{Escape(item.Project ?? Target.Project)}", $"_apis/wit/workItems/{item.Id}/comments", true, 3, []),
            AzureDevOpsScopes.WorkWrite, new { text = DevOpsText.TextToHtml(text) }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetWorkItemTypesAsync(CancellationToken cancellationToken)
    {
        var typesTask = GetAsync(ProjectUrl("_apis/wit/workitemtypes"), AzureDevOpsScopes.Work, cancellationToken);
        var categoriesTask = OptionalAsync(GetAsync(ProjectUrl("_apis/wit/workitemtypecategories"), AzureDevOpsScopes.Work, cancellationToken));
        await Task.WhenAll(typesTask, categoriesTask).ConfigureAwait(false);
        // Types such as Code Review Request and Shared Steps live in the hidden category; nobody creates them by hand.
        var hidden = (await categoriesTask)?.Items("value").Where(category => category.Str("referenceName") == "Microsoft.HiddenCategory")
            .SelectMany(category => category.Items("workItemTypes")).Select(type => type.Text("name")).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return (await typesTask).Items("value").Where(type => !type.Bool("isDisabled")).Select(type => type.Text("name"))
            .Where(name => name.Length > 0 && !hidden.Contains(name)).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<DevOpsWorkItem> CreateWorkItemAsync(string type, string title, string description, DevOpsIdentity? assignee, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DevOpsException("Give the work item a title.");
        var operations = new List<object> { new { op = "add", path = "/fields/System.Title", value = title.Trim() } };
        if (!string.IsNullOrWhiteSpace(description)) operations.Add(new { op = "add", path = "/fields/System.Description", value = DevOpsText.TextToHtml(description.Trim()) });
        if (assignee?.UniqueName is { } name) operations.Add(new { op = "add", path = "/fields/System.AssignedTo", value = name });
        var created = await JsonAsync(HttpMethod.Post, ProjectUrl($"_apis/wit/workitems/{Escape("$" + type)}"), AzureDevOpsScopes.WorkWrite, operations, JsonPatch, cancellationToken).ConfigureAwait(false);
        return WorkItem(created);
    }

    /// <summary>
    /// Changes one field. The patch first tests the revision the item was read at, so a change someone else made meanwhile makes it
    /// fail instead of being overwritten.
    /// </summary>
    private async Task<DevOpsWorkItem> PatchWorkItemAsync(DevOpsWorkItemDetails item, string field, string value, CancellationToken cancellationToken)
    {
        object[] operations = [new { op = "test", path = "/rev", value = item.Revision }, new { op = "add", path = "/fields/" + field, value }];
        try
        {
            return WorkItem(await JsonAsync(HttpMethod.Patch, CoreUrl($"_apis/wit/workitems/{item.Item.Id}"), AzureDevOpsScopes.WorkWrite, operations, JsonPatch, cancellationToken).ConfigureAwait(false));
        }
        catch (DevOpsException ex) when (ex is not DevOpsAccessDeniedException and not DevOpsAuthenticationException && ex.Message.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new DevOpsException($"Someone changed work item {item.Item.Id} after it was loaded. Refresh it and try again.", ex.Status, ex);
        }
    }

    private async Task<IReadOnlyList<DevOpsWorkItem>> WiqlAsync(string wiql, int top, CancellationToken cancellationToken)
    {
        var result = await JsonAsync(HttpMethod.Post, ProjectUrl("_apis/wit/wiql", ("$top", top)), AzureDevOpsScopes.Work, new { query = wiql }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await WorkItemsAsync(QueryIds(result), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The work item ids of a flat query, or every item of a tree or one-hop query once, in query order.</summary>
    internal static List<int> QueryIds(JsonElement result)
    {
        if (result.Get("workItems") is { ValueKind: JsonValueKind.Array } items) return items.EnumerateArray().Select(item => item.Int("id")).OfType<int>().ToList();
        var ids = new List<int>();
        foreach (var relation in result.Items("workItemRelations"))
            foreach (var end in new[] { relation.Get("source"), relation.Get("target") })
                if (end?.Int("id") is { } id && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>Reads work items 200 at a time, keeping the order of <paramref name="ids"/>; ids that no longer exist are left out.</summary>
    private async Task<IReadOnlyList<DevOpsWorkItem>> WorkItemsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<int, DevOpsWorkItem>();
        for (var start = 0; start < ids.Count; start += 200)
        {
            var batch = await JsonAsync(HttpMethod.Post, CoreUrl("_apis/wit/workitemsbatch"), AzureDevOpsScopes.Work,
                new { ids = ids.Skip(start).Take(200).ToArray(), fields = ListFields, errorPolicy = "omit" }, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in batch.Items("value").Where(item => item.ValueKind == JsonValueKind.Object))
                if (item.Int("id") is { } id) byId[id] = WorkItem(item);
        }
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private async Task<IReadOnlyList<DevOpsWorkItem>> OptionalWorkItemsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        try { return await WorkItemsAsync(ids, cancellationToken).ConfigureAwait(false); }
        catch (DevOpsException) { return []; }
    }

    private DevOpsWorkItem WorkItem(JsonElement item)
    {
        var fields = item.Get("fields");
        var id = item.Int("id") ?? fields?.Int("System.Id") ?? 0;
        var project = fields?.Str("System.TeamProject") ?? Target.Project;
        return new(id, fields?.Str("System.Title") ?? "", fields?.Str("System.WorkItemType") ?? "", fields?.Str("System.State") ?? "",
            IdentityName(fields?.Get("System.AssignedTo")), WorkItemUrl(project, id), fields?.Str("System.IterationPath"), fields?.Str("System.AreaPath"),
            fields?.Str("System.Tags"), fields?.Int("Microsoft.VSTS.Common.Priority"), fields?.Date("System.ChangedDate"), project);
    }

    private string WorkItemUrl(string project, int id) => $"{_organization.Url}/{Escape(project)}/_workitems/edit/{id}";

    /// <summary>An identity field: an object with a display name since REST 5.0, a "Name &lt;account&gt;" string before.</summary>
    private static string? IdentityName(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Object } identity => identity.Str("displayName") ?? identity.Str("uniqueName"),
        { ValueKind: JsonValueKind.String } text => text.GetString()?.Split(" <", 2)[0],
        _ => null
    };
}
