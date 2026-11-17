using WpfStudio.Core.DevOps;
using WpfStudio.Core.DevOps.AzureDevOps;

namespace WpfStudio.Core.Tests.DevOps;

public sealed class AzureDevOpsUrlTests
{
    [Theory]
    [InlineData("https://dev.azure.com/contoso/Web/_git/Web.App", "https://dev.azure.com/contoso", OrganizationKind.Cloud, "Web", "Web.App")]
    [InlineData("https://contoso@dev.azure.com/contoso/Web%20Site/_git/Web%20App", "https://dev.azure.com/contoso", OrganizationKind.Cloud, "Web Site", "Web App")]
    [InlineData("https://dev.azure.com/contoso/Web/_git/Web.App.git", "https://dev.azure.com/contoso", OrganizationKind.Cloud, "Web", "Web.App")]
    [InlineData("https://dev.azure.com/contoso/_git/Web", "https://dev.azure.com/contoso", OrganizationKind.Cloud, "Web", "Web")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Web/Web.App", "https://dev.azure.com/contoso", OrganizationKind.Cloud, "Web", "Web.App")]
    [InlineData("ssh://git@ssh.dev.azure.com:22/v3/contoso/Web/Web.App", "https://dev.azure.com/contoso", OrganizationKind.Cloud, "Web", "Web.App")]
    [InlineData("contoso@vs-ssh.visualstudio.com:v3/contoso/Web/Repo", "https://contoso.visualstudio.com", OrganizationKind.Legacy, "Web", "Repo")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Web/_git/Repo", "https://contoso.visualstudio.com", OrganizationKind.Legacy, "Web", "Repo")]
    [InlineData("https://Contoso.visualstudio.com/Web/_git/_optimized/Repo", "https://contoso.visualstudio.com", OrganizationKind.Legacy, "Web", "Repo")]
    [InlineData("https://tfs.contoso.com/tfs/DefaultCollection/Web/_git/Repo", "https://tfs.contoso.com/tfs/DefaultCollection", OrganizationKind.Server, "Web", "Repo")]
    [InlineData("https://ado.contoso.com:8443/Fabrikam/Web/_git/Repo", "https://ado.contoso.com:8443/Fabrikam", OrganizationKind.Server, "Web", "Repo")]
    [InlineData("ssh://tfs.contoso.com:22/tfs/DefaultCollection/Web/_git/Repo", "https://tfs.contoso.com/tfs/DefaultCollection", OrganizationKind.Server, "Web", "Repo")]
    public void GitRemotesOfEveryKindNameTheirOrganizationProjectAndRepository(string remote, string organizationUrl, OrganizationKind kind, string project, string repository)
    {
        var location = AzureDevOpsUrls.ParseGitRemote(remote);
        Assert.NotNull(location);
        Assert.Equal(organizationUrl, location.Organization.Url);
        Assert.Equal(kind, location.Organization.Kind);
        Assert.Equal(project, location.Project);
        Assert.Equal(repository, location.Repository);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("https://gitlab.com/group/sub/project")]
    [InlineData("https://dev.azure.com/contoso/Web")]
    [InlineData("https://dev.azure.com/_apis/projects")]
    [InlineData("")]
    [InlineData("not a url")]
    public void RemotesThatAreNotAzureReposAreNotRead(string remote) => Assert.Null(AzureDevOpsUrls.ParseGitRemote(remote));

    [Fact]
    public void TypedOrganizationsAcceptANameOrAnAddress()
    {
        Assert.Equal("https://dev.azure.com/contoso", AzureDevOpsUrls.ParseOrganization("contoso")!.Organization.Url);
        Assert.Equal("Web", AzureDevOpsUrls.ParseOrganization("dev.azure.com/contoso/Web/_boards")!.Project);
        Assert.Equal(OrganizationKind.Legacy, AzureDevOpsUrls.ParseOrganization("https://contoso.visualstudio.com/")!.Organization.Kind);
        Assert.Null(AzureDevOpsUrls.ParseOrganization("ftp://dev.azure.com/contoso"));
        Assert.Null(AzureDevOpsUrls.ParseOrganization("   "));
    }

    [Fact]
    public void BothAddressesOfACloudOrganizationShareOneKey()
    {
        var cloud = AzureDevOpsUrls.ParseGitRemote("https://dev.azure.com/Contoso/Web/_git/Repo")!.Organization;
        var legacy = AzureDevOpsUrls.ParseGitRemote("https://contoso.visualstudio.com/Web/_git/Repo")!.Organization;
        Assert.Equal(cloud.Key, legacy.Key);
        Assert.NotEqual(cloud.Key, AzureDevOpsUrls.ParseGitRemote("https://tfs.contoso.com/tfs/Contoso/Web/_git/Repo")!.Organization.Key);
    }

    [Fact]
    public void ServicesLiveOnTheirOwnHostsInTheCloudAndOnOneHostOnServer()
    {
        var cloud = AzureDevOpsUrls.ParseOrganization("contoso")!.Organization;
        var legacy = AzureDevOpsUrls.ParseOrganization("contoso.visualstudio.com")!.Organization;
        var server = AzureDevOpsUrls.ParseOrganization("https://tfs.contoso.com/tfs/DefaultCollection")!.Organization;
        Assert.Equal("https://vsrm.dev.azure.com/contoso", AzureDevOpsUrls.ServiceUrl(cloud, AzureDevOpsService.Release));
        Assert.Equal("https://vssps.dev.azure.com/contoso", AzureDevOpsUrls.ServiceUrl(cloud, AzureDevOpsService.Identity));
        Assert.Equal("https://contoso.vsrm.visualstudio.com", AzureDevOpsUrls.ServiceUrl(legacy, AzureDevOpsService.Release));
        Assert.Equal(server.Url, AzureDevOpsUrls.ServiceUrl(server, AzureDevOpsService.Release));
        Assert.Equal("https://dev.azure.com/contoso/_usersSettings/tokens", AzureDevOpsUrls.TokenSettingsUrl(cloud));
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Web/_apis/git/repositories", true)]
    [InlineData("https://dev.azure.com/Contoso/_apis/connectionData", true)]
    [InlineData("https://contoso.visualstudio.com/_apis/wit/workitems/1", true)]
    [InlineData("https://vssps.dev.azure.com/contoso/_apis/identities", true)]
    [InlineData("https://contoso.vsrm.visualstudio.com/Web/_apis/release/definitions", true)]
    [InlineData("https://dev.azure.com/fabrikam/_apis/projects", false)]
    [InlineData("https://dev.azure.com/contosoevil/_apis/projects", false)]
    [InlineData("http://dev.azure.com/contoso/_apis/projects", false)]
    [InlineData("https://dev.azure.com.evil.example/contoso/_apis/projects", false)]
    [InlineData("https://productionresultssa0.blob.core.windows.net/logs/1", false)]
    public void TheTokenGoesOnlyToTheOrganizationsOwnHosts(string url, bool allowed)
    {
        var organization = AzureDevOpsUrls.ParseOrganization("contoso")!.Organization;
        Assert.Equal(allowed, AzureDevOpsUrls.IsOrganizationUrl(organization, new Uri(url)));
    }

    [Fact]
    public void AServerOverHttpMayReceiveItsToken()
    {
        var server = AzureDevOpsUrls.ParseOrganization("http://tfs.local:8080/tfs/DefaultCollection")!.Organization;
        Assert.True(AzureDevOpsUrls.IsOrganizationUrl(server, new Uri("http://tfs.local:8080/tfs/DefaultCollection/Web/_apis/git/repositories")));
        Assert.False(AzureDevOpsUrls.IsOrganizationUrl(server, new Uri("http://tfs.local:8080/tfs/OtherCollection/_apis/projects")));
    }

    [Fact]
    public void TargetsComeFromAzureReposAndGitHubRemotes()
    {
        var azure = DevOpsTarget.FromRemoteUrl("git@ssh.dev.azure.com:v3/contoso/Web/Web.App")!;
        Assert.Equal(DevOpsProviderKind.AzureDevOps, azure.Kind);
        Assert.Equal("https://dev.azure.com/contoso/Web/_git/Web.App", azure.RepositoryWebUrl);
        Assert.Equal("contoso / Web / Web.App", azure.DisplayName);
        Assert.Equal("dev.azure.com", azure.Host);
        Assert.Equal(DevOpsTarget.FromRemoteUrl("https://contoso.visualstudio.com/Web/_git/Other")!.CredentialKey, azure.CredentialKey);

        var github = DevOpsTarget.FromRemoteUrl("git@github.com:octo-org/hello.git")!;
        Assert.Equal(DevOpsProviderKind.GitHub, github.Kind);
        Assert.Equal("https://github.com/octo-org/hello", github.RepositoryWebUrl);
        Assert.Equal("https://api.github.com", github.ApiUrl);
        Assert.Null(DevOpsTarget.FromRemoteUrl("https://gitlab.com/group/project"));
        Assert.Null(DevOpsTarget.FromRemoteUrl(null));
    }
}
