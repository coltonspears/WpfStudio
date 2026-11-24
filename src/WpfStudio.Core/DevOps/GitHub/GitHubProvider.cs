using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpfStudio.Core.DevOps.GitHub;

/// <summary>
/// GitHub pull requests, issues (as work items) and Actions (as pipelines) for one repository, through the GitHub REST API with a
/// personal access token (classic or fine-grained) or the OAuth token Git Credential Manager keeps for GitHub. GitHub Enterprise
/// Server is reached at its own host under <c>/api/v3</c>.
/// </summary>
public sealed partial class GitHubProvider : IDevOpsProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private const string Accept = "application/vnd.github+json";
    /// <summary>The most pages one list reads, at 100 entries a page.</summary>
    private const int MaxPages = 30;
    private readonly DevOpsRestClient _rest;
    private readonly Uri _api;
    private readonly string _web;
    private readonly Lock _cacheGate = new();
    private Task<DevOpsIdentity>? _me;
    private Task<JsonElement>? _repository;

    public GitHubProvider(HttpClient http, DevOpsTarget target, string token)
    {
        if (target.Kind != DevOpsProviderKind.GitHub || target.ApiUrl is null) throw new ArgumentException("The target is not a GitHub repository.", nameof(target));
        Target = target;
        _api = new Uri(target.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var web = new Uri(target.RepositoryWebUrl, UriKind.Absolute);
        _web = $"{web.Scheme}://{web.Authority}";
        _rest = new(http, DevOpsRestClient.Bearer(token),
            uri => uri.Scheme == "https" && string.Equals(uri.Authority, _api.Authority, StringComparison.OrdinalIgnoreCase),
            new Dictionary<string, string> { ["X-GitHub-Api-Version"] = "2022-11-28", ["User-Agent"] = "WpfStudio" });
    }

    public DevOpsTarget Target { get; }

    public DevOpsCapabilities Capabilities { get; } = new("GitHub", "Issues", "Issue", "Actions", "Merge", "Close",
        Votes: false, AutoComplete: false, ResolveThreads: false, Approvals: false, SavedQueries: false, MergeStrategies: true, CompleteWorkItems: false,
        RetryFailedJobs: true, WorkItemTypes: false);

    public string TokenPageUrl => $"{_web}/settings/tokens/new?scopes=repo,workflow,read:org&description=WpfStudio";

    /// <summary>GitHub's GraphQL endpoint, for the few changes REST does not offer: <c>api.github.com/graphql</c>, or <c>/api/graphql</c> on Enterprise Server.</summary>
    private string GraphQlUrl => _api.AbsolutePath.EndsWith("/api/v3/", StringComparison.OrdinalIgnoreCase)
        ? $"{_api.Scheme}://{_api.Authority}/api/graphql" : new Uri(_api, "graphql").AbsoluteUri;

    public Task<DevOpsIdentity> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<DevOpsIdentity>(cancellationToken);
        // Shared while it runs and once it succeeded; a failed or cancelled read is asked again next time.
        Task<DevOpsIdentity> task;
        lock (_cacheGate)
        {
            if (_me is { IsFaulted: false, IsCanceled: false } known) task = known;
            else task = _me = ReadCurrentUserAsync(CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task<DevOpsIdentity> ReadCurrentUserAsync(CancellationToken cancellationToken)
    {
        var user = (await SendAsync(HttpMethod.Get, Url("user"), cancellationToken: cancellationToken).ConfigureAwait(false)).Json();
        var login = user.Text("login");
        return new(login, user.Str("name") is { Length: > 0 } name ? name : login, login);
    }

    public async Task<IReadOnlyList<DevOpsAccessEntry>> CheckAccessAsync(CancellationToken cancellationToken)
    {
        var user = await SendAsync(HttpMethod.Get, Url("user"), cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement? repository = null;
        string? repositoryError = null;
        try { repository = await RepositoryAsync(cancellationToken).ConfigureAwait(false); }
        catch (DevOpsException ex) when (ex is not DevOpsAuthenticationException) { repositoryError = ex.Message; }
        var canSee = repository is not null;
        var isPrivate = repository?.Bool("private") != false;
        const string RepositoryScope = "repo (public_repo for a public repository)";
        (string Feature, string Description, string Requirement, bool Write)[] features =
        [
            ("View pull requests and issues", "Pull request and issue lists, changed files, diffs, comments and checks.", RepositoryScope, false),
            ("Create, review and merge pull requests", "Create pull requests, review, comment, merge and close.", RepositoryScope, true),
            ("Create and edit issues", "Create issues, close and reopen them, assign and comment.", RepositoryScope, true),
            ("View workflow runs and logs", "Actions runs, their jobs and steps, and job logs.", RepositoryScope, false),
            ("Run and manage workflows", "Run a workflow, cancel a run and re-run failed jobs.", RepositoryScope, true),
        ];
        // A classic token or an OAuth token lists its scopes; a fine-grained token does not, and is judged when it is used.
        if (user.Header("X-OAuth-Scopes") is not { } header)
            return features.Select(feature => new DevOpsAccessEntry(feature.Feature, feature.Description, feature.Requirement,
                !canSee ? AccessState.Denied : feature.Write ? AccessState.Unknown : AccessState.Granted,
                !canSee ? repositoryError : feature.Write ? "Fine-grained token: its permissions are checked when an action uses them" : null)).ToList();
        var scopes = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repositoryAccess = scopes.Contains("repo") || (scopes.Contains("public_repo") && !isPrivate);
        var entries = features.Select(feature =>
        {
            // Reading a public repository needs no scope at all.
            var granted = canSee && (repositoryAccess || (!feature.Write && !isPrivate));
            return new DevOpsAccessEntry(feature.Feature, feature.Description, feature.Requirement, granted ? AccessState.Granted : AccessState.Denied,
                canSee ? null : repositoryError);
        }).ToList();
        entries.Add(new("Push changes to workflow files", "Git pushes that add or change files in .github/workflows.", "workflow",
            scopes.Contains("workflow") ? AccessState.Granted : AccessState.Denied));
        entries.Add(new("Team review requests", "Read the teams of the repository's organization.", "read:org",
            scopes.Contains("read:org") || scopes.Contains("admin:org") || scopes.Contains("write:org") ? AccessState.Granted : AccessState.Denied));
        return entries;
    }

    // ------------------------------------------------------------------ Transport

    private string Url(string path, params (string Name, object? Value)[] query)
    {
        var parts = query.Where(pair => pair.Value is not null && pair.Value is not "")
            .Select(pair => $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(Convert.ToString(pair.Value, CultureInfo.InvariantCulture)!)}").ToList();
        return new Uri(_api, path.TrimStart('/')).AbsoluteUri + (parts.Count == 0 ? "" : "?" + string.Join('&', parts));
    }

    private string RepoUrl(string path, params (string Name, object? Value)[] query) =>
        Url($"repos/{Escape(Target.Owner)}/{Escape(Target.Repository)}" + (path.Length == 0 ? "" : "/" + path.TrimStart('/')), query);

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    /// <summary>A repository path with each segment escaped and the slashes kept.</summary>
    private static string EscapePath(string path) => string.Join('/', path.Trim('/').Split('/').Select(Uri.EscapeDataString));

    private async Task<DevOpsResponse> SendAsync(HttpMethod method, string url, object? body = null, string accept = Accept, CancellationToken cancellationToken = default)
    {
        var payload = body switch { null => null, string text => text, _ => JsonSerializer.Serialize(body, Json) };
        var response = await _rest.SendAsync(method, url, payload, payload is null ? null : "application/json", accept, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? response : throw Error(response);
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string url, object? body = null, CancellationToken cancellationToken = default) =>
        (await SendAsync(method, url, body, cancellationToken: cancellationToken).ConfigureAwait(false)).Json();

    private Task<JsonElement> GetAsync(string url, CancellationToken cancellationToken) => JsonAsync(HttpMethod.Get, url, cancellationToken: cancellationToken);

    /// <summary>
    /// Reads a list page by page, following <c>Link: rel="next"</c>, until <paramref name="limit"/> entries were kept or
    /// <see cref="MaxPages"/> pages were read. <paramref name="member"/> names the array in responses that wrap it in an object.
    /// </summary>
    private async Task<List<JsonElement>> PagesAsync(string url, int limit, CancellationToken cancellationToken, string? member = null, Func<JsonElement, bool>? keep = null)
    {
        var items = new List<JsonElement>();
        string? next = url;
        for (var page = 0; page < MaxPages && next is not null && items.Count < limit; page++)
        {
            var response = await SendAsync(HttpMethod.Get, next, cancellationToken: cancellationToken).ConfigureAwait(false);
            var json = response.Json();
            var array = member is null ? json : json.Get(member) ?? default;
            items.AddRange(array.Items().Where(item => keep?.Invoke(item) ?? true));
            next = NextLink(response.Header("Link"));
        }
        return items.Count > limit ? items.GetRange(0, limit) : items;
    }

    /// <summary>The <c>rel="next"</c> address of a <c>Link</c> header, or null on the last page.</summary>
    internal static string? NextLink(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (Match match in LinkPart().Matches(header))
            if (match.Groups["rel"].Value.Split(' ').Contains("next", StringComparer.Ordinal)) return match.Groups["url"].Value;
        return null;
    }

    private static async Task<JsonElement?> OptionalAsync(Task<JsonElement> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch (DevOpsException ex) when (ex is not DevOpsAuthenticationException) { return null; }
    }

    /// <summary>The repository's record (its default branch and visibility), read once.</summary>
    private Task<JsonElement> RepositoryAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<JsonElement>(cancellationToken);
        Task<JsonElement> task;
        lock (_cacheGate)
        {
            if (_repository is { IsFaulted: false, IsCanceled: false } known) task = known;
            else task = _repository = GetAsync(RepoUrl(""), CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>What a refused request means, in the words of the action a person can take.</summary>
    internal static DevOpsException Error(DevOpsResponse response)
    {
        var message = ErrorMessage(response.Body);
        var status = response.Status;
        if (status == 401)
            return new DevOpsAuthenticationException("GitHub did not accept the token. It may have expired or been revoked. Sign in again.", 401);
        if (status is 403 or 429 && (response.Header("x-ratelimit-remaining") == "0" || message?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true))
        {
            var reset = long.TryParse(response.Header("x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? $" until {DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime():HH:mm}" : response.Header("retry-after") is { } retry ? $" for {retry} seconds" : "";
            return new DevOpsException($"GitHub's API rate limit is used up{reset}. Try again then.", status);
        }
        if (status == 403)
        {
            var accepted = response.Header("x-accepted-oauth-scopes") is { Length: > 0 } scopes ? scopes : null;
            return new DevOpsAccessDeniedException(message is null ? "GitHub refused this request. The token may lack a scope, or your account may not have permission."
                : accepted is null ? message : $"{message} (needs {accepted})", 403, accepted);
        }
        if (status == 404)
            return new DevOpsException($"{message ?? "Not Found"}. If the repository is private, the token may not be allowed to see it.", 404);
        return new DevOpsException(message ?? $"GitHub returned HTTP {status}.", status);
    }

    /// <summary>GitHub's error message, with the validation errors a 422 lists after it.</summary>
    internal static string? ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var message = root.Str("message");
            var errors = root.Items("errors").Select(error => error.ValueKind == JsonValueKind.String ? error.GetString() : error.Str("message") ?? error.Str("code"))
                .Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
            return errors.Count == 0 ? message : $"{message ?? "Validation failed"}: {string.Join("; ", errors)}";
        }
        catch (JsonException) { return null; }
    }

    private static DevOpsIdentity Identity(JsonElement? user) => user is not { } value ? DevOpsIdentity.Unknown :
        new(value.Text("login"), value.Str("login") ?? value.Str("name") ?? "Unknown", value.Str("login"));

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"(?<rel>[^\"]+)\"")]
    private static partial Regex LinkPart();
}
