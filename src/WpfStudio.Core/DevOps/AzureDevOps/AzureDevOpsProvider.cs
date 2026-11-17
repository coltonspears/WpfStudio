using System.Globalization;
using System.Text.Json;

namespace WpfStudio.Core.DevOps.AzureDevOps;

/// <summary>
/// Azure Repos pull requests, Azure Boards work items and Azure Pipelines for one repository, through the Azure DevOps REST API
/// with a personal access token or a Microsoft Entra token (the kind Git Credential Manager keeps for Azure Repos).
/// </summary>
public sealed partial class AzureDevOpsProvider : IDevOpsProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly DevOpsRestClient _rest;
    private readonly AzureDevOpsOrganization _organization;
    private readonly string _apiVersion;
    private (DateTimeOffset At, Task<bool> Valid)? _tokenCheck;
    private Task<DevOpsIdentity>? _me;
    private Task<JsonElement>? _repository;

    /// <param name="apiVersion">REST version; null picks 7.1 for Azure DevOps Services and 7.0 (Server 2022) for Azure DevOps Server.</param>
    public AzureDevOpsProvider(HttpClient http, DevOpsTarget target, string token, string? apiVersion = null)
    {
        if (target.Kind != DevOpsProviderKind.AzureDevOps || target.Organization is null) throw new ArgumentException("The target is not an Azure DevOps repository.", nameof(target));
        Target = target;
        _organization = target.Organization;
        _apiVersion = apiVersion ?? (_organization.Kind == OrganizationKind.Server ? "7.0" : "7.1");
        var authorization = DevOpsRestClient.IsJwt(token) ? DevOpsRestClient.Bearer(token) : DevOpsRestClient.Basic(token);
        // X-TFS-FedAuthRedirect stops a refused request from being redirected to an interactive sign-in page.
        _rest = new(http, authorization, uri => AzureDevOpsUrls.IsOrganizationUrl(_organization, uri),
            new Dictionary<string, string> { ["X-TFS-FedAuthRedirect"] = "Suppress" });
    }

    public DevOpsTarget Target { get; }

    /// <summary>Work item states the personal lists leave out.</summary>
    public IReadOnlyList<string> HiddenStates { get; init; } = ["Closed", "Done", "Removed", "Completed", "Cut"];

    public DevOpsCapabilities Capabilities { get; } = new("Azure DevOps", "Work items", "Work item", "Pipelines", "Complete", "Abandon",
        Votes: true, AutoComplete: true, ResolveThreads: true, Approvals: true, SavedQueries: true, MergeStrategies: true, CompleteWorkItems: true,
        RetryFailedJobs: true, WorkItemTypes: true);

    public string TokenPageUrl => AzureDevOpsUrls.TokenSettingsUrl(_organization);

    public Task<DevOpsIdentity> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        // Shared while it runs and once it succeeded; a failed read (a refused token) is asked again next time.
        if (_me is { IsFaulted: false, IsCanceled: false } known) return known;
        return _me = ReadCurrentUserAsync(cancellationToken);
    }

    private async Task<DevOpsIdentity> ReadCurrentUserAsync(CancellationToken cancellationToken)
    {
        var data = (await SendAsync(HttpMethod.Get, CoreUrl("_apis/connectionData", versioned: false), null, cancellationToken: cancellationToken)).Json();
        var user = data.Get("authenticatedUser") ?? throw new DevOpsException("Azure DevOps did not say who the token belongs to.");
        var account = user.Get("properties", "Account", "$value")?.GetString();
        return new(user.Text("id"), user.Str("providerDisplayName") ?? account ?? "Signed in", account);
    }

    public async Task<IReadOnlyList<DevOpsAccessEntry>> CheckAccessAsync(CancellationToken cancellationToken)
    {
        var probes = AzureDevOpsScopes.Probes(_organization, Target.Project, _apiVersion);
        var results = await Task.WhenAll(probes.Select(async probe =>
        {
            try
            {
                var response = await _rest.SendAsync(probe.Method, probe.Url, probe.Body, probe.ContentType, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (probe.Scope, Result: AzureDevOpsScopes.Classify(response.Status, response.ContentType));
            }
            catch (Exception ex) when (ex is HttpRequestException or DevOpsException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return (probe.Scope, Result: (AccessState.Unknown, (string?)("Could not check: " + ex.Message)));
            }
        })).ConfigureAwait(false);
        var states = AzureDevOpsScopes.Normalize(results.ToDictionary(result => result.Scope, result => result.Result.Item1));
        var details = results.ToDictionary(result => result.Scope, result => result.Result.Item2);
        return AzureDevOpsScopes.Features.Select(feature =>
        {
            var state = AzureDevOpsScopes.FeatureState(feature, states);
            var detail = feature.Requires.Select(scope => details.GetValueOrDefault(scope)).FirstOrDefault(text => text is not null);
            return new DevOpsAccessEntry(feature.Label, feature.Description, string.Join(", ", feature.Requires.Select(AzureDevOpsScopes.Requirement)), state, detail);
        }).ToList();
    }

    // ------------------------------------------------------------------ Transport

    private string CoreUrl(string path, params (string Name, object? Value)[] query) => BuildUrl(_organization.Url, path, versioned: true, preview: null, query);
    private string CoreUrl(string path, bool versioned, params (string Name, object? Value)[] query) => BuildUrl(_organization.Url, path, versioned, null, query);
    private string ProjectUrl(string path, params (string Name, object? Value)[] query) => BuildUrl($"{_organization.Url}/{Escape(Target.Project)}", path, true, null, query);
    private string PreviewUrl(string path, int preview, params (string Name, object? Value)[] query) => BuildUrl($"{_organization.Url}/{Escape(Target.Project)}", path, true, preview, query);
    private string RepositoryPath => $"_apis/git/repositories/{Escape(Target.Repository)}";

    private string BuildUrl(string root, string path, bool versioned, int? preview, (string Name, object? Value)[] query)
    {
        var parts = query.Where(pair => pair.Value is not null && pair.Value is not "")
            .Select(pair => $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(Convert.ToString(pair.Value, CultureInfo.InvariantCulture)!)}").ToList();
        if (versioned) parts.Add("api-version=" + (preview is { } number ? $"{_apiVersion}-preview.{number}" : _apiVersion));
        return $"{root}/{path.TrimStart('/')}" + (parts.Count == 0 ? "" : "?" + string.Join('&', parts));
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private async Task<DevOpsResponse> SendAsync(HttpMethod method, string url, string? scope, object? body = null, string? contentType = null,
        string accept = "application/json", CancellationToken cancellationToken = default)
    {
        var payload = body switch { null => null, string text => text, _ => JsonSerializer.Serialize(body, Json) };
        var response = await _rest.SendAsync(method, url, payload, contentType, accept, cancellationToken).ConfigureAwait(false);
        // Azure DevOps answers refused credentials with a sign-in page (203, or HTML where JSON was asked for).
        if (response.Status == 203 || (response.IsSuccess && response.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) && !accept.Contains("html", StringComparison.OrdinalIgnoreCase)))
            throw TokenRefused(response.Status);
        if (response.IsSuccess) return response;
        var message = ErrorMessage(response.Body);
        if (response.Status == 401)
        {
            if (!await IsTokenValidAsync(cancellationToken).ConfigureAwait(false)) throw TokenRefused(401);
            var requirement = scope is null ? null : AzureDevOpsScopes.Requirement(scope);
            throw new DevOpsAccessDeniedException(requirement is null ? message ?? "Your token does not grant access to this." :
                $"Your token does not include {requirement}. Add it on the token page, or sign in with a token that has it.", 401, requirement);
        }
        if (response.Status == 403) throw new DevOpsAccessDeniedException(message ?? "Access denied. Your account may not have permission for this.", 403);
        throw new DevOpsException(message ?? $"Azure DevOps returned HTTP {response.Status}.", response.Status);
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string url, string scope, object? body = null, string? contentType = null, CancellationToken cancellationToken = default) =>
        (await SendAsync(method, url, scope, body, contentType, cancellationToken: cancellationToken).ConfigureAwait(false)).Json();

    private Task<JsonElement> GetAsync(string url, string scope, CancellationToken cancellationToken) => JsonAsync(HttpMethod.Get, url, scope, cancellationToken: cancellationToken);

    /// <summary>After a 401, tells a dead token from one that lacks a scope. Remembered for a minute.</summary>
    private Task<bool> IsTokenValidAsync(CancellationToken cancellationToken)
    {
        if (_tokenCheck is { } check && DateTimeOffset.UtcNow - check.At < TimeSpan.FromMinutes(1)) return check.Valid;
        var valid = CheckTokenAsync(cancellationToken);
        _tokenCheck = (DateTimeOffset.UtcNow, valid);
        return valid;
    }

    private async Task<bool> CheckTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _rest.SendAsync(HttpMethod.Get, _organization.Url + "/_apis/connectionData", cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Status == 200 && !response.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase);
        }
        // Network trouble is not evidence that the token is dead.
        catch (HttpRequestException) { return true; }
    }

    private DevOpsAuthenticationException TokenRefused(int status) => new(
        $"Azure DevOps did not accept the token for {_organization.Name}. It may have expired, been revoked, or not be valid for this organization. Sign in again.", status);

    /// <summary>The readable message of an Azure DevOps error body, or the title of an HTML error page.</summary>
    internal static string? ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.Str("message") ?? root.Get("value")?.Str("Message");
        }
        catch (JsonException)
        {
            var start = body.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
            var end = start < 0 ? -1 : body.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
            return end > start ? System.Net.WebUtility.HtmlDecode(body[(start + 7)..end]).Trim() : null;
        }
    }

    private static DevOpsIdentity Identity(JsonElement? element) => element is not { } value ? DevOpsIdentity.Unknown :
        new(value.Text("id"), value.Str("displayName") ?? value.Str("uniqueName") ?? "Unknown", value.Str("uniqueName"), value.Bool("isContainer"));

    private static string ShortRef(string? reference) => reference is null ? "" :
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference[11..] : reference;

    private static string FullRef(string branch) => branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;
}
