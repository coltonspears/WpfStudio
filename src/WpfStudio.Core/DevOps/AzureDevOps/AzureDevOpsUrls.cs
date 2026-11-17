using System.Text.RegularExpressions;

namespace WpfStudio.Core.DevOps.AzureDevOps;

public enum OrganizationKind { Cloud, Legacy, Server }

/// <summary>
/// An Azure DevOps organization (Services) or collection (Server). <see cref="Url"/> is the base of core REST calls, without a
/// trailing slash: <c>https://dev.azure.com/contoso</c>, <c>https://contoso.visualstudio.com</c> or <c>https://tfs.contoso.com/tfs/DefaultCollection</c>.
/// </summary>
public sealed record AzureDevOpsOrganization(string Url, string Name, OrganizationKind Kind)
{
    /// <summary>
    /// The same for every address of one organization, so <c>contoso.visualstudio.com</c> and <c>dev.azure.com/contoso</c> share a
    /// stored token.
    /// </summary>
    public string Key => Kind == OrganizationKind.Server ? "server:" + Url.ToLowerInvariant() : "cloud:" + Name.ToLowerInvariant();
}

public sealed record AzureDevOpsLocation(AzureDevOpsOrganization Organization, string? Project = null, string? Repository = null);

/// <summary>The REST services Azure DevOps Services splits across hosts.</summary>
public enum AzureDevOpsService { Core, Release, Identity, Feeds }

/// <summary>
/// Reads the addresses Azure DevOps uses: organization URLs a person types, Git remote URLs (HTTPS and SSH, Services and Server),
/// and the hosts each REST service lives on.
/// </summary>
public static partial class AzureDevOpsUrls
{
    private static AzureDevOpsOrganization Cloud(string name) => new($"https://dev.azure.com/{Uri.EscapeDataString(name)}", name, OrganizationKind.Cloud);
    private static AzureDevOpsOrganization Legacy(string name) => new($"https://{name.ToLowerInvariant()}.visualstudio.com", name, OrganizationKind.Legacy);

    /// <summary>
    /// What a person typed for an organization: a bare name, a dev.azure.com or *.visualstudio.com address (optionally deep into a
    /// project or repository), or an Azure DevOps Server collection address.
    /// </summary>
    public static AzureDevOpsLocation? ParseOrganization(string? input)
    {
        var trimmed = input?.Trim().TrimEnd('/') ?? "";
        if (trimmed.Length == 0) return null;
        if (OrganizationName().IsMatch(trimmed) && !trimmed.Contains('.', StringComparison.Ordinal)) return new(Cloud(trimmed));
        var withScheme = Scheme().IsMatch(trimmed) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return null;
        var host = url.Host.ToLowerInvariant();
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Decode).ToArray();
        if (host == "dev.azure.com")
        {
            if (segments.Length == 0 || segments[0].StartsWith('_')) return null;
            return WithProject(Cloud(segments[0]), segments[1..]);
        }
        if (LegacyHost().Match(host) is { Success: true } legacy)
        {
            var rest = segments.Length > 0 && segments[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) ? segments[1..] : segments;
            return WithProject(Legacy(legacy.Groups[1].Value), rest);
        }
        // Azure DevOps Server: https://host/tfs/Collection/Project/... or https://host/Collection/Project/...
        var depth = segments.Length > 0 && segments[0].Equals("tfs", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (segments.Length < depth || segments[..depth].Any(segment => segment.StartsWith('_'))) return null;
        var collection = segments[..depth];
        var authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        var organization = new AzureDevOpsOrganization($"{url.Scheme}://{authority}/{string.Join('/', collection.Select(Uri.EscapeDataString))}", collection[^1], OrganizationKind.Server);
        return WithProject(organization, segments[depth..]);
    }

    /// <summary>A Git remote of an Azure Repos repository, over HTTPS or SSH; null for any other remote.</summary>
    public static AzureDevOpsLocation? ParseGitRemote(string? remote)
    {
        var text = remote?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (!Scheme().IsMatch(text))
        {
            // scp-like SSH: git@ssh.dev.azure.com:v3/org/project/repo or org@vs-ssh.visualstudio.com:v3/org/project/repo
            return ScpLike().Match(text) is { Success: true } scp ? ParseSsh(scp.Groups["host"].Value.ToLowerInvariant(), scp.Groups["path"].Value) : null;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        if (url.Scheme == "ssh")
        {
            if (ParseSsh(url.Host.ToLowerInvariant(), url.AbsolutePath.TrimStart('/')) is { } ssh) return ssh;
            // Azure DevOps Server over SSH has the HTTPS path layout.
            text = $"https://{url.Host}{url.AbsolutePath}";
        }
        else if (url.Scheme is not ("https" or "http")) return null;
        var parsed = ParseOrganization(text);
        return parsed is { Project: not null, Repository: not null } && HasGitSegment(text) ? parsed : null;
    }

    public static string ServiceUrl(AzureDevOpsOrganization organization, AzureDevOpsService service)
    {
        if (organization.Kind == OrganizationKind.Server || service == AzureDevOpsService.Core) return organization.Url;
        var prefix = service switch { AzureDevOpsService.Release => "vsrm", AzureDevOpsService.Identity => "vssps", _ => "feeds" };
        return organization.Kind == OrganizationKind.Cloud
            ? $"https://{prefix}.dev.azure.com/{Uri.EscapeDataString(organization.Name)}"
            : $"https://{organization.Name.ToLowerInvariant()}.{prefix}.visualstudio.com";
    }

    /// <summary>Where a person creates or edits personal access tokens for the organization.</summary>
    public static string TokenSettingsUrl(AzureDevOpsOrganization organization) => organization.Url + "/_usersSettings/tokens";

    /// <summary>
    /// True when <paramref name="candidate"/> is on one of the organization's own hosts, where its token may be sent. dev.azure.com
    /// serves every organization, so there the path must match too.
    /// </summary>
    public static bool IsOrganizationUrl(AzureDevOpsOrganization organization, Uri candidate)
    {
        if (candidate.Scheme != "https" && !(organization.Kind == OrganizationKind.Server && candidate.Scheme == "http")) return false;
        var variants = organization.Kind == OrganizationKind.Server ? [organization] : new[] { Cloud(organization.Name), Legacy(organization.Name) };
        var path = candidate.AbsolutePath.ToLowerInvariant();
        foreach (var variant in variants)
            foreach (var service in Enum.GetValues<AzureDevOpsService>())
            {
                var root = new Uri(ServiceUrl(variant, service));
                if (!string.Equals(root.Authority, candidate.Authority, StringComparison.OrdinalIgnoreCase) || root.Scheme != candidate.Scheme) continue;
                var rootPath = root.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (rootPath.Length == 0 || path == rootPath || path.StartsWith(rootPath + "/", StringComparison.Ordinal)) return true;
            }
        return false;
    }

    /// <summary>The repository's page, from which file, branch and pull request pages are built.</summary>
    public static string RepositoryWebUrl(AzureDevOpsOrganization organization, string project, string repository) =>
        $"{organization.Url}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}";

    private static AzureDevOpsLocation WithProject(AzureDevOpsOrganization organization, string[] rest)
    {
        var git = Array.FindIndex(rest, segment => segment.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (git >= 0)
        {
            var repository = rest[(git + 1)..].FirstOrDefault(segment => !segment.Equals("_optimized", StringComparison.OrdinalIgnoreCase)
                && !segment.Equals("_full", StringComparison.OrdinalIgnoreCase));
            repository = StripGit(repository);
            // dev.azure.com/org/_git/name is used when the repository is named after its project.
            return new(organization, git > 0 ? rest[git - 1] : repository, repository);
        }
        return rest.Length > 0 && !rest[0].StartsWith('_') ? new(organization, rest[0]) : new(organization);
    }

    private static AzureDevOpsLocation? ParseSsh(string host, string path)
    {
        if (host is not ("ssh.dev.azure.com" or "vs-ssh.visualstudio.com")) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Decode).ToArray();
        if (segments.Length < 4 || !segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase)) return null;
        var repository = StripGit(segments[3]);
        if (string.IsNullOrEmpty(repository)) return null;
        return new(host == "ssh.dev.azure.com" ? Cloud(segments[1]) : Legacy(segments[1]), segments[2], repository);
    }

    private static bool HasGitSegment(string url) => url.Contains("/_git/", StringComparison.OrdinalIgnoreCase);

    private static string? StripGit(string? name) => name is not null && name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string Decode(string segment)
    {
        try { return Uri.UnescapeDataString(segment); }
        catch (UriFormatException) { return segment; }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex OrganizationName();
    [GeneratedRegex("^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();
    [GeneratedRegex("^([a-z0-9][a-z0-9-]*)\\.visualstudio\\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyHost();
    [GeneratedRegex("^(?:[^@/]+@)?(?<host>[^:/]+):(?<path>.+)$")]
    private static partial Regex ScpLike();
}
