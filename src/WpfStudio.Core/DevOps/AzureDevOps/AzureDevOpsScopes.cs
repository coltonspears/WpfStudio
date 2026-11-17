namespace WpfStudio.Core.DevOps.AzureDevOps;

/// <summary>A personal access token scope, named as the Azure DevOps token page names its checkbox.</summary>
public sealed record AzureDevOpsScope(string Id, string Area, string Level, string Description, params string[] Implies)
{
    public string Label => $"{Area} ({Level})";
}

/// <summary>A feature of the DevOps workbench and the scopes it needs.</summary>
public sealed record AzureDevOpsFeature(string Id, string Label, string Description, params string[] Requires);

/// <summary>A request that reveals whether a token has a scope without changing anything.</summary>
public sealed record ScopeProbe(string Scope, HttpMethod Method, string Url, string? Body = null, string? ContentType = null);

/// <summary>
/// Which scopes a token grants. Azure DevOps has no API that tells a token its own scopes, but it checks scopes before it looks at
/// the target or the payload, so one small request per scope answers: 401 means the scope is missing, anything that got past the
/// check (2xx, 400, 404, …) means it is granted. Write scopes are probed against targets that cannot exist (work item 0, build 0,
/// the all-zero repository) or with <c>validateOnly</c>, so checking never changes anything in the organization.
/// </summary>
public static class AzureDevOpsScopes
{
    public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
    public const string Project = "vso.project", Work = "vso.work", WorkWrite = "vso.work_write", Code = "vso.code", CodeWrite = "vso.code_write",
        Build = "vso.build", BuildExecute = "vso.build_execute", Release = "vso.release", Identity = "vso.identity";

    public static IReadOnlyList<AzureDevOpsScope> All { get; } =
    [
        new(Work, "Work Items", "Read", "Read work items, queries, iterations and comments."),
        new(WorkWrite, "Work Items", "Read & write", "Create and update work items, links and comments.", Work),
        new(Code, "Code", "Read", "Read repositories, branches, pull requests and diffs."),
        new(CodeWrite, "Code", "Read & write", "Create, vote on, comment on and complete pull requests.", Code),
        new(Build, "Build", "Read", "Read pipelines, runs, timelines, logs and approvals."),
        new(BuildExecute, "Build", "Read & execute", "Queue, cancel and retry runs; approve or reject stages.", Build),
        new(Release, "Release", "Read", "Read classic release pipelines and releases."),
        new(Project, "Project and Team", "Read", "List projects, teams and their iterations."),
        new(Identity, "Identity", "Read", "Find people to assign or to add as reviewers."),
    ];

    public static IReadOnlyList<AzureDevOpsFeature> Features { get; } =
    [
        new("workItems.view", "View and query work items", "Assigned to me, the current iteration, saved queries and work item details.", Work),
        new("workItems.edit", "Create and edit work items", "Create work items, change state, assign and comment.", WorkWrite),
        new("pullRequests.view", "View pull requests", "Pull request lists, changed files, diffs, comments and checks.", Code),
        new("pullRequests.contribute", "Create and review pull requests", "Create, vote, comment, resolve, complete and abandon.", CodeWrite),
        new("pipelines.view", "View pipelines and logs", "Runs, their stages, jobs and tasks, logs and pending approvals.", Build),
        new("pipelines.run", "Run and manage pipelines", "Queue, cancel and retry runs; approve or reject stages.", BuildExecute),
        new("projects.browse", "Read teams and iterations", "The current iteration of the project's default team.", Project),
    ];

    /// <summary>The scopes to tick when creating a token, so every feature works.</summary>
    public static IReadOnlyList<string> Recommended { get; } = [WorkWrite, CodeWrite, BuildExecute, Project];

    public static AzureDevOpsScope Find(string id) => All.First(scope => scope.Id == id);

    /// <summary>The probes for every scope; those that need a project are skipped when there is none.</summary>
    public static IReadOnlyList<ScopeProbe> Probes(AzureDevOpsOrganization organization, string? project, string apiVersion)
    {
        var core = AzureDevOpsUrls.ServiceUrl(organization, AzureDevOpsService.Core);
        var release = AzureDevOpsUrls.ServiceUrl(organization, AzureDevOpsService.Release);
        var identity = AzureDevOpsUrls.ServiceUrl(organization, AzureDevOpsService.Identity);
        string Url(string root, string path, string query = "") => $"{root}/{path}?api-version={apiVersion}{query}";
        var probes = new List<ScopeProbe>
        {
            new(Project, HttpMethod.Get, Url(core, "_apis/projects", "&$top=1")),
            new(Work, HttpMethod.Get, Url(core, "_apis/wit/fields/System.Title")),
            // validateOnly and an empty patch of work item 0: nothing is ever saved.
            new(WorkWrite, HttpMethod.Patch, Url(core, "_apis/wit/workitems/0", "&validateOnly=true"), "[]", "application/json-patch+json"),
            new(Code, HttpMethod.Get, Url(core, $"_apis/git/repositories/{ZeroGuid}")),
            new(CodeWrite, HttpMethod.Post, Url(core, $"_apis/git/repositories/{ZeroGuid}/pullrequests"), "{}"),
            new(Identity, HttpMethod.Get, Url(identity, "_apis/identities", $"&identityIds={ZeroGuid}&queryMembership=None")),
        };
        if (project is not null)
        {
            var scoped = $"{core}/{Uri.EscapeDataString(project)}";
            probes.Add(new(Build, HttpMethod.Get, Url(scoped, "_apis/build/definitions", "&$top=1")));
            // Build numbers start at 1, so updating build 0 always fails after the scope check.
            probes.Add(new(BuildExecute, HttpMethod.Patch, Url(scoped, "_apis/build/builds/0"), "{}"));
            probes.Add(new(Release, HttpMethod.Get, Url($"{release}/{Uri.EscapeDataString(project)}", "_apis/release/definitions", "&$top=1")));
        }
        return probes;
    }

    /// <summary>What a probe's response says about its scope.</summary>
    public static (AccessState State, string? Detail) Classify(int status, string? contentType)
    {
        // A sign-in page instead of an answer means the credentials themselves were not accepted.
        if (status == 203 || (status == 200 && contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)) return (AccessState.Denied, "The token was not accepted");
        if (status == 401) return (AccessState.Denied, "Scope not granted to this token");
        if (status == 403) return (AccessState.Denied, "Access denied (scope or permissions)");
        if (status is >= 200 and < 300 or 400 or 404 or 405 or 409 or 412 or 415 or 422) return (AccessState.Granted, null);
        return (AccessState.Unknown, $"Unexpected response (HTTP {status})");
    }

    /// <summary>
    /// Applies the rules between scopes: a granted write scope grants its read scope, and a denied read scope denies the write
    /// scope built on it. Scopes that were not checked stay unknown.
    /// </summary>
    public static Dictionary<string, AccessState> Normalize(IReadOnlyDictionary<string, AccessState> states)
    {
        var result = All.ToDictionary(scope => scope.Id, scope => states.TryGetValue(scope.Id, out var state) ? state : AccessState.Unknown);
        foreach (var scope in All.Where(scope => result[scope.Id] == AccessState.Granted))
            foreach (var implied in scope.Implies) result[implied] = AccessState.Granted;
        foreach (var scope in All.Where(scope => result[scope.Id] != AccessState.Granted && scope.Implies.Any(implied => result[implied] == AccessState.Denied)))
            result[scope.Id] = AccessState.Denied;
        return result;
    }

    /// <summary>A feature is denied when one scope it needs is denied, and granted when all are.</summary>
    public static AccessState FeatureState(AzureDevOpsFeature feature, IReadOnlyDictionary<string, AccessState> states)
    {
        var values = feature.Requires.Select(id => states.TryGetValue(id, out var state) ? state : AccessState.Unknown).ToList();
        return values.Contains(AccessState.Denied) ? AccessState.Denied : values.All(value => value == AccessState.Granted) ? AccessState.Granted : AccessState.Unknown;
    }

    /// <summary>The scope a REST area needs, for telling a person what to enable when a request is refused.</summary>
    public static string Requirement(string scope) => Find(scope).Label;
}
