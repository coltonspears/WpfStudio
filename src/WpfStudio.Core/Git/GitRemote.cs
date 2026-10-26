using System.Text.RegularExpressions;

namespace WpfStudio.Core.Git;

public enum GitHost { Generic, GitHub, AzureDevOps, GitLab, Bitbucket }

/// <summary>
/// A repository's web location derived from a remote URL, with links to commits, files, branches and pull
/// requests. Azure DevOps accepts both <c>dev.azure.com</c> and legacy <c>*.visualstudio.com</c> addresses over
/// HTTPS or SSH (v3). Credentials embedded in a URL are never kept.
/// </summary>
public sealed partial record GitRemote(GitHost Host, string WebUrl, string Name)
{
    public string HostName => Host switch
    {
        GitHost.GitHub => "GitHub", GitHost.AzureDevOps => "Azure DevOps", GitHost.GitLab => "GitLab", GitHost.Bitbucket => "Bitbucket", _ => "the remote"
    };

    public static GitRemote? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        // Azure DevOps SSH: git@ssh.dev.azure.com:v3/org/project/repo and org@vs-ssh.visualstudio.com:v3/org/project/repo
        if (AzureSsh().Match(url) is { Success: true } ssh)
        {
            var (org, project, repo) = (ssh.Groups["org"].Value, ssh.Groups["project"].Value, ssh.Groups["repo"].Value);
            var web = ssh.Groups["legacy"].Success ? $"https://{org}.visualstudio.com/{project}/_git/{repo}" : $"https://dev.azure.com/{org}/{project}/_git/{repo}";
            return new(GitHost.AzureDevOps, web, Uri.UnescapeDataString(repo));
        }
        // scp-like SSH, for example git@github.com:owner/repo.git
        if (ScpLike().Match(url) is { Success: true } scp && !url.Contains("://", StringComparison.Ordinal))
            url = $"https://{scp.Groups["host"].Value}/{scp.Groups["path"].Value}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git")) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (path.Length == 0) return null;
        if (host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            // dev.azure.com/org/project/_git/repo or org.visualstudio.com[/DefaultCollection]/project/_git/repo
            var parts = path.Split('/');
            var git = Array.IndexOf(parts, "_git");
            if (git < 1 || git + 1 >= parts.Length) return null;
            var prefix = string.Join('/', parts[..(git + 2)]);
            return new(GitHost.AzureDevOps, $"https://{host}/{prefix}", Uri.UnescapeDataString(parts[git + 1]));
        }
        var kind = host switch
        {
            "github.com" or "www.github.com" => GitHost.GitHub,
            "gitlab.com" => GitHost.GitLab,
            "bitbucket.org" => GitHost.Bitbucket,
            _ => host.StartsWith("github.", StringComparison.Ordinal) ? GitHost.GitHub : host.StartsWith("gitlab.", StringComparison.Ordinal) ? GitHost.GitLab : GitHost.Generic
        };
        var port = uri.IsDefaultPort || uri.Scheme is "ssh" or "git" ? "" : ":" + uri.Port;
        return new(kind, $"https://{host}{port}/{path}", path.Split('/')[^1]);
    }

    public string CommitUrl(string commit) => Host switch
    {
        GitHost.AzureDevOps => $"{WebUrl}/commit/{commit}",
        GitHost.GitLab => $"{WebUrl}/-/commit/{commit}",
        GitHost.Bitbucket => $"{WebUrl}/commits/{commit}",
        _ => $"{WebUrl}/commit/{commit}"
    };

    /// <summary>A file at a commit or branch, scrolled to a 1-based line when given.</summary>
    public string FileUrl(string version, string relativePath, int? line = null, bool versionIsBranch = false)
    {
        var path = string.Join('/', relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return Host switch
        {
            GitHost.AzureDevOps => $"{WebUrl}?path=/{path}&version={(versionIsBranch ? "GB" : "GC")}{Uri.EscapeDataString(version)}"
                + (line is { } l ? $"&line={l}&lineEnd={l + 1}&lineStartColumn=1&lineEndColumn=1&lineStyle=plain&_a=contents" : "&_a=contents"),
            GitHost.GitLab => $"{WebUrl}/-/blob/{version}/{path}" + (line is { } g ? $"#L{g}" : ""),
            GitHost.Bitbucket => $"{WebUrl}/src/{version}/{path}" + (line is { } b ? $"#lines-{b}" : ""),
            _ => $"{WebUrl}/blob/{version}/{path}" + (line is { } h ? $"#L{h}" : "")
        };
    }

    public string BranchUrl(string branch) => Host switch
    {
        GitHost.AzureDevOps => $"{WebUrl}?version=GB{Uri.EscapeDataString(branch)}",
        GitHost.GitLab => $"{WebUrl}/-/tree/{branch}",
        GitHost.Bitbucket => $"{WebUrl}/branch/{branch}",
        _ => $"{WebUrl}/tree/{branch}"
    };

    /// <summary>The page that starts a pull request (merge request on GitLab) from <paramref name="branch"/>.</summary>
    public string NewPullRequestUrl(string branch) => Host switch
    {
        GitHost.AzureDevOps => $"{WebUrl}/pullrequestcreate?sourceRef={Uri.EscapeDataString(branch)}",
        GitHost.GitLab => $"{WebUrl}/-/merge_requests/new?merge_request%5Bsource_branch%5D={Uri.EscapeDataString(branch)}",
        GitHost.Bitbucket => $"{WebUrl}/pull-requests/new?source={Uri.EscapeDataString(branch)}",
        _ => $"{WebUrl}/compare/{Uri.EscapeDataString(branch)}?expand=1"
    };

    public string PullRequestsUrl => Host switch
    {
        GitHost.AzureDevOps => $"{WebUrl}/pullrequests",
        GitHost.GitLab => $"{WebUrl}/-/merge_requests",
        GitHost.Bitbucket => $"{WebUrl}/pull-requests",
        _ => $"{WebUrl}/pulls"
    };

    [GeneratedRegex(@"^(?:ssh://)?(?:[^@/]+@)?(?:ssh\.dev\.azure\.com|(?<legacy>vs-ssh\.visualstudio\.com))(?::22)?[:/]v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AzureSsh();
    [GeneratedRegex(@"^(?:[^@/]+@)?(?<host>[A-Za-z0-9.\-]+):(?<path>[^/\\][^:]*)$")]
    private static partial Regex ScpLike();
}
