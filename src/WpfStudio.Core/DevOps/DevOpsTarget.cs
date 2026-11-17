using WpfStudio.Core.DevOps.AzureDevOps;
using WpfStudio.Core.Git;

namespace WpfStudio.Core.DevOps;

/// <summary>
/// The repository a workspace's Git remote points at, on the service that hosts it. For Azure DevOps, <see cref="Owner"/> is the
/// organization and <see cref="Project"/> the team project; for GitHub both are the owning user or organization.
/// </summary>
public sealed record DevOpsTarget(DevOpsProviderKind Kind, string Owner, string Project, string Repository, string RepositoryWebUrl,
    AzureDevOpsOrganization? Organization = null, string? ApiUrl = null)
{
    /// <summary>Who stored tokens belong to: one Azure DevOps organization, or one GitHub host.</summary>
    public string CredentialKey => Kind == DevOpsProviderKind.AzureDevOps
        ? "azure-devops|" + Organization!.Key
        : "github|" + new Uri(RepositoryWebUrl).Host.ToLowerInvariant();

    /// <summary>The HTTPS address Git Credential Manager keys this repository's sign-in on, whatever protocol the remote uses.</summary>
    public string CredentialUrl => RepositoryWebUrl;

    public string ServiceName => Kind == DevOpsProviderKind.AzureDevOps ? "Azure DevOps" : "GitHub";

    public string DisplayName => Kind == DevOpsProviderKind.AzureDevOps
        ? $"{Owner} / {Project} / {Repository}"
        : $"{Owner} / {Repository}";

    /// <summary>The service's own host name, shown where the token will be sent.</summary>
    public string Host => new Uri(Kind == DevOpsProviderKind.AzureDevOps ? Organization!.Url : RepositoryWebUrl).Host;

    /// <summary>Reads a remote's fetch URL (HTTPS or SSH) or its web address; null when the remote is on another service.</summary>
    public static DevOpsTarget? FromRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (AzureDevOpsUrls.ParseGitRemote(url) is { Project: { } project, Repository: { } name } azure)
            return new(DevOpsProviderKind.AzureDevOps, azure.Organization.Name, project, name,
                AzureDevOpsUrls.RepositoryWebUrl(azure.Organization, project, name), azure.Organization);
        if (GitRemote.Parse(url) is { Host: GitHost.GitHub } remote && Uri.TryCreate(remote.WebUrl, UriKind.Absolute, out var web))
        {
            var parts = web.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            var (owner, repository) = (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
            var host = web.Host.ToLowerInvariant();
            // github.com has its own API host; GitHub Enterprise Server serves the API under /api/v3 of its own host.
            var api = host is "github.com" or "www.github.com" ? "https://api.github.com" : $"https://{web.Authority}/api/v3";
            var site = host == "www.github.com" ? "https://github.com" : $"https://{web.Authority}";
            return new(DevOpsProviderKind.GitHub, owner, owner, repository, $"{site}/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}", ApiUrl: api);
        }
        return null;
    }
}
