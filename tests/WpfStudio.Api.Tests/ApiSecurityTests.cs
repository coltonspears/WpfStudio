using WpfStudio.Api.Import;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;

namespace WpfStudio.Api.Tests;

public sealed class ApiSecurityTests
{
    [Fact]
    public void CurlExporterQuotesUnsafeMethodStrings()
    {
        var request = new ApiRequest
        {
            Method = "POST; whoami",
            Url = "https://example.com/api",
        };
        var command = CurlExporter.Export(request, null, null);
        Assert.StartsWith("curl -X 'POST; WHOAMI' 'https://example.com/api'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void CurlExporterUsesFormStringForNonFileMultipartRowsToPreventLocalFileReads()
    {
        var request = new ApiRequest
        {
            Method = "POST",
            Url = "https://example.com/upload",
            BodyMode = BodyMode.Multipart,
            Form =
            [
                new KeyValue("bio", "@/etc/passwd") { IsFile = false },
                new KeyValue("note", "<secret.txt") { IsFile = false },
                new KeyValue("avatar", "/tmp/pic.png") { IsFile = true },
            ],
        };

        var command = CurlExporter.Export(request, null, null);
        Assert.Contains("--form-string 'bio=@/etc/passwd'", command, StringComparison.Ordinal);
        Assert.Contains("--form-string 'note=<secret.txt'", command, StringComparison.Ordinal);
        Assert.Contains("-F 'avatar=@/tmp/pic.png'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void CurlExporterResolvesApiKeyHeaderNameBeforeCheckingExplicitHeaders()
    {
        var collection = new ApiCollection
        {
            Variables =
            [
                new("headerName", "X-Api-Key"),
                new("emptyHeader", "   "),
            ],
        };
        var overridden = new ApiRequest
        {
            Url = "https://example.com/items",
            Headers = [new("x-api-key", "explicit-value")],
            Auth = new ApiAuth
            {
                Kind = AuthKind.ApiKey,
                KeyLocation = ApiKeyLocation.Header,
                KeyName = "{{headerName}}",
                KeyValue = "auth-value",
            },
        };
        var overriddenCommand = CurlExporter.Export(overridden, collection, null);
        Assert.Contains("-H 'x-api-key: explicit-value'", overriddenCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-value", overriddenCommand, StringComparison.Ordinal);

        var blankHeader = new ApiRequest
        {
            Url = "https://example.com/items",
            Auth = new ApiAuth
            {
                Kind = AuthKind.ApiKey,
                KeyLocation = ApiKeyLocation.Header,
                KeyName = "{{emptyHeader}}",
                KeyValue = "auth-value",
            },
        };
        var blankCommand = CurlExporter.Export(blankHeader, collection, null);
        Assert.DoesNotContain("auth-value", blankCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiImporterExportScrubsMixedTemplateSecretsWhilePreservingPureReferences()
    {
        var collection = new ApiCollection
        {
            Name = "Secrets",
            Auth = new ApiAuth
            {
                Kind = AuthKind.Bearer,
                Token = "{{prefix}}SUPER_SECRET{{suffix}}",
                Password = "{{purePassword}}",
                KeyValue = "{{a}}LEAK{{b}}",
            },
            Proxy = new ProxyOverride
            {
                Mode = ProxyOverrideMode.Custom,
                Address = "http://proxy:8080",
                Password = "{{p1}}PROXY_SECRET{{p2}}",
            },
        };

        var exported = ApiImporter.Export(collection, null);
        Assert.DoesNotContain("SUPER_SECRET", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAK", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXY_SECRET", exported, StringComparison.Ordinal);
        Assert.Contains("{{purePassword}}", exported, StringComparison.Ordinal);
    }
}
