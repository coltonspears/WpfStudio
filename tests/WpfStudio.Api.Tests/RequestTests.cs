using System.Net;
using System.Text;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;
using WpfStudio.Core.Net;

namespace WpfStudio.Api.Tests;

public sealed class RequestTests
{
    private static readonly string Json = TestServer.Response(200, "OK", """{"ok":true,"items":[1,2]}""", "Content-Type: application/json; charset=utf-8");

    [Fact]
    public async Task SendingFillsInVariablesPathAndQueryParametersHeadersAndTheCollectionsSignIn()
    {
        await using var server = new TestServer((_, _) => Json);
        var token = TestSecrets.Make("token");
        var collection = new ApiCollection
        {
            Variables = [new("baseUrl", server.Address), new("token", token) { Secret = true }, new("id", "collection-value")],
            Auth = new ApiAuth { Kind = AuthKind.Bearer, Token = "{{token}}" },
        };
        var environment = new ApiEnvironment { Variables = [new("id", "a b/c")] };
        var request = new ApiRequest
        {
            Url = "{{baseUrl}}/pets/{petId}",
            PathParameters = [new("petId", "{{id}}")],
            Query = [new("limit", "5"), new("skip", "1", enabled: false), new("q", "x&y")],
            Headers = [new("X-Trace", "trace-{{id}}")],
        };
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var response = await sender.SendAsync(request, collection, environment, CancellationToken.None);

        var received = Assert.Single(server.Requests);
        Assert.Equal("/pets/a%20b%2Fc?limit=5&q=x%26y", received.Target);
        Assert.Equal("Bearer " + token, received.Header("Authorization"));
        Assert.Equal("trace-a b/c", received.Header("X-Trace"));
        Assert.Equal(ApiRequestBuilder.UserAgent, received.Header("User-Agent"));
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsJson);
        Assert.Contains("\n", response.Pretty, StringComparison.Ordinal);
        Assert.Equal("application/json", response.MediaType);
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task BodiesAreSentWithTheirContentTypeAndATypedHeaderWins()
    {
        await using var server = new TestServer((_, _) => Json);
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var json = new ApiRequest { Method = "post", Url = server.Address + "/a", BodyMode = BodyMode.Json, Body = """{"name":"{{name}}"}""" };
        var form = new ApiRequest { Method = "PUT", Url = server.Address + "/b", BodyMode = BodyMode.FormUrlEncoded, Form = [new("a", "1 2"), new("b", "&", enabled: false)] };
        var vendor = new ApiRequest { Method = "PATCH", Url = server.Address + "/c", BodyMode = BodyMode.Json, Body = "{}", Headers = [new("Content-Type", "application/vnd.api+json")] };
        var collection = new ApiCollection { Variables = [new("name", "Rex")] };
        foreach (var request in new[] { json, form, vendor }) await sender.SendAsync(request, collection, null, CancellationToken.None);

        var requests = server.Requests.ToArray();
        Assert.Equal("POST", requests[0].Method);
        Assert.StartsWith("application/json", requests[0].Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal("""{"name":"Rex"}""", requests[0].Body);
        Assert.Equal("application/x-www-form-urlencoded", requests[1].Header("Content-Type"));
        Assert.Equal("a=1+2", requests[1].Body);
        Assert.Equal("application/vnd.api+json", requests[2].Header("Content-Type"));
    }

    [Fact]
    public async Task ARequestCanSendThroughItsOwnProxy()
    {
        await using var proxy = new TestServer((_, _) => TestServer.Response(200, "OK", "from proxy"));
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var request = new ApiRequest { Url = "http://api.example.invalid/hello", Proxy = new ProxyOverride { Mode = ProxyOverrideMode.Custom, Address = proxy.Address } };
        var response = await sender.SendAsync(request, null, null, CancellationToken.None);

        // A proxied request names the whole address, not just the path.
        Assert.Equal("http://api.example.invalid/hello", Assert.Single(proxy.Requests).Target);
        Assert.Equal("from proxy", response.Text);
        Assert.Contains("this request", response.Proxy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSettingsProxyIsUsedAndAnswersItsChallengeWithTheSavedCredentials()
    {
        await using var proxy = new TestServer((request, count) => request.Header("Proxy-Authorization") is null
            ? TestServer.Response(407, "Proxy Authentication Required", "", "Proxy-Authenticate: Basic realm=\"test\"")
            : TestServer.Response(200, "OK", "signed in"));
        var (user, password) = (TestSecrets.Make("user"), TestSecrets.Make("proxy"));
        var settings = new ProxyConfiguration(ProxyMode.Manual, proxy.Address, UserName: user, Password: password);
        using var sender = new ApiSender(() => settings);
        var response = await sender.SendAsync(new ApiRequest { Url = "http://api.example.invalid/x" }, null, null, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)), proxy.Requests.Last().Header("Proxy-Authorization"));
        Assert.Contains("Settings", response.Proxy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BypassedHostsAndNoProxyOverridesGoDirect()
    {
        await using var server = new TestServer((_, _) => Json);
        // Nothing listens on the proxy's port, so any request sent to it fails.
        var deadProxy = new ProxyConfiguration(ProxyMode.Manual, "127.0.0.1:1", Bypass: "127.0.0.*", BypassLocalAddresses: false);
        using var sender = new ApiSender(() => deadProxy);
        await sender.SendAsync(new ApiRequest { Url = server.Address + "/bypassed" }, null, null, CancellationToken.None);

        var direct = new ProxyConfiguration(ProxyMode.Manual, "127.0.0.1:1", BypassLocalAddresses: false);
        using var overridden = new ApiSender(() => direct);
        await overridden.SendAsync(new ApiRequest { Url = server.Address + "/none" }, new ApiCollection { Proxy = new ProxyOverride { Mode = ProxyOverrideMode.None } }, null, CancellationToken.None);

        Assert.Equal(["/bypassed", "/none"], server.Requests.Select(request => request.Target));
        var failure = await Assert.ThrowsAsync<ApiRequestException>(() => overridden.SendAsync(new ApiRequest { Url = server.Address + "/proxied" }, null, null, CancellationToken.None));
        Assert.Contains("127.0.0.1:1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookiesTheServerSetsAreSentBackUntilCleared()
    {
        await using var server = new TestServer((_, count) => count == 1 ? TestServer.Response(200, "OK", "", "Set-Cookie: session=abc; Path=/") : Json);
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var request = new ApiRequest { Url = server.Address + "/" };
        var first = await sender.SendAsync(request, null, null, CancellationToken.None);
        await sender.SendAsync(request, null, null, CancellationToken.None);
        sender.ClearCookies();
        await sender.SendAsync(request, null, null, CancellationToken.None);

        Assert.Equal("session", Assert.Single(first.Cookies).Key);
        var requests = server.Requests.ToArray();
        Assert.Null(requests[0].Header("Cookie"));
        Assert.Equal("session=abc", requests[1].Header("Cookie"));
        Assert.Null(requests[2].Header("Cookie"));
    }

    [Fact]
    public async Task AnAddressWithAnUnknownVariableSaysWhichOne()
    {
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var failure = await Assert.ThrowsAsync<ApiRequestException>(() => sender.SendAsync(new ApiRequest { Url = "{{baseUrl}}/pets" }, null, null, CancellationToken.None));
        Assert.Contains("{{baseUrl}}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAddressBarAndTheQueryTableDescribeTheSameRequest()
    {
        var (address, query) = RequestUrl.Split("{{baseUrl}}/pets/:id/{owner}?a=1&b&c={{x}}#top");
        Assert.Equal("{{baseUrl}}/pets/:id/{owner}", address);
        Assert.Equal(["a=1", "b=", "c={{x}}"], query.Select(row => row.Key + "=" + row.Value));
        query[0].Enabled = false;
        Assert.Equal("{{baseUrl}}/pets/:id/{owner}?b&c={{x}}", RequestUrl.Compose(address, query));
        Assert.Equal(["id", "owner"], RequestUrl.PathParameterNames(address));
        // A port is not a path parameter, and {{variables}} are not either.
        Assert.Empty(RequestUrl.PathParameterNames("http://localhost:5000/{{segment}}/x"));
        var synchronized = RequestUrl.SynchronizePathParameters("/a/{id}/{other}", [new("id", "7"), new("gone", "1")]);
        Assert.Equal(["id=7", "other="], synchronized.Select(row => row.Key + "=" + row.Value));
    }

    [Fact]
    public void VariablesResolveThroughEachOtherAndReportWhatIsMissing()
    {
        var resolver = new VariableResolver([new("host", "example.com"), new("baseUrl", "https://{{host}}/v1"), new("off", "x", enabled: false)], [new("host", "staging.example.com")]);
        Assert.Equal("https://staging.example.com/v1/pets", resolver.Resolve("{{baseUrl}}/pets"));
        Assert.Equal(["off", "missing"], resolver.Unresolved("{{off}} {{missing}} {{ host }} {{$guid}}"));
        Assert.True(Guid.TryParse(resolver.Resolve("{{$guid}}"), out _));
        Assert.Equal("%7B%7Bx%7D%7D", resolver.Resolve(VariableResolver.EncodeUrlComponent("{{x}}")));
        Assert.Equal("a%20b%2Fc%3F%25zz%41", VariableResolver.EncodeUrlComponent("a b/c?%zz%41", pathSegment: true));
        Assert.Equal("caf%C3%A9%F0%9F%98%80", VariableResolver.EncodeUrlComponent("café😀"));
    }

    [Fact]
    public void CurlCommandsQuoteForAPosixShell()
    {
        var (user, password) = (TestSecrets.Make("user"), TestSecrets.Make("basic"));
        var request = new ApiRequest
        {
            Method = "POST", Url = "https://example.com/it's", BodyMode = BodyMode.Json, Body = """{"a":"b'c"}""",
            Auth = new ApiAuth { Kind = AuthKind.Basic, UserName = user, Password = password }, Settings = new RequestSettings { VerifyCertificates = false },
        };
        var command = CurlExporter.Export(request, null, null);
        Assert.StartsWith("curl -X POST 'https://example.com/it'\\''s'", command, StringComparison.Ordinal);
        Assert.Contains("-u '" + user + ":" + password + "'", command, StringComparison.Ordinal);
        Assert.Contains("--data-raw '{\"a\":\"b'\\''c\"}'", command, StringComparison.Ordinal);
        Assert.Contains("--insecure", command, StringComparison.Ordinal);
        Assert.Contains("-H 'Content-Type: application/json'", command, StringComparison.Ordinal);
    }
}

public sealed class ProxyTests
{
    [Theory]
    [InlineData("*.corp.example.com", "https://build.corp.example.com/x", true)]
    [InlineData("*.corp.example.com", "https://corp.example.com.evil.net/x", false)]
    [InlineData("example.com", "https://example.com.evil.net/", false)]
    [InlineData("10.*", "http://10.1.2.3/", true)]
    [InlineData("build:8080", "http://build:8080/", true)]
    [InlineData("build:8080", "http://build:9090/", false)]
    [InlineData("https://secure.example.com", "http://secure.example.com/", false)]
    [InlineData("<local>", "http://intranet/", true)]
    [InlineData("<local>", "http://intranet.example.com/", false)]
    [InlineData("[::1]", "http://[::1]:5000/", true)]
    public void BypassEntriesMatchWholeHosts(string bypass, string address, bool bypassed) =>
        Assert.Equal(bypassed, ProxyBypassList.Parse(bypass).Matches(new Uri(address)));

    [Theory]
    [InlineData("proxy:8080", "http://proxy:8080/")]
    [InlineData("https://proxy.example.com", "https://proxy.example.com/")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    public void ProxyAddressesAreReadTheWayTheyAreUsuallyWritten(string text, string expected)
    {
        Assert.True(ProxyConfiguration.TryParseAddress(text, out var address, out _));
        Assert.Equal(expected, address!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://proxy")]
    [InlineData("http://proxy:8080/path")]
    public void UnusableProxyAddressesAreExplained(string text)
    {
        Assert.False(ProxyConfiguration.TryParseAddress(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void LocalAddressesSkipAManualProxyUnlessToldOtherwise()
    {
        var proxy = new ProxyConfiguration(ProxyMode.Manual, "proxy:8080").CreateProxy();
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:5000/")));
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1/")));
        Assert.False(proxy.IsBypassed(new Uri("https://api.example.com/")));
        Assert.Equal(new Uri("http://proxy:8080/"), proxy.GetProxy(new Uri("https://api.example.com/")));
        Assert.False(new ProxyConfiguration(ProxyMode.Manual, "proxy:8080", BypassLocalAddresses: false).CreateProxy().IsBypassed(new Uri("http://localhost/")));
    }

    [Fact]
    public void TheRequestWinsThenTheCollectionThenSettings()
    {
        var settings = new ProxyConfiguration(ProxyMode.Manual, "settings:1");
        var collection = new ProxyOverride { Mode = ProxyOverrideMode.Custom, Address = "collection:2" };
        var request = new ProxyOverride { Mode = ProxyOverrideMode.None };
        Assert.Equal(ProxyMode.None, ProxyResolution.Resolve(settings, collection, request).Configuration.Mode);
        Assert.Equal("collection:2", ProxyResolution.Resolve(settings, collection, new ProxyOverride()).Configuration.Address);
        Assert.Same(settings, ProxyResolution.Resolve(settings, new ProxyOverride(), new ProxyOverride()).Configuration);
    }

    [Fact]
    public void TheSharedProxyFollowsChangesIncludingCredentialsHandlersReadOnce()
    {
        var (one, two, user) = (TestSecrets.Make("first"), TestSecrets.Make("second"), TestSecrets.Make("user"));
        var proxy = new StudioWebProxy(new ProxyConfiguration(ProxyMode.Manual, "first:1", UserName: @"CORP\" + user, Password: one));
        var credentials = proxy.Credentials!;
        var target = new Uri("https://api.example.com/");
        Assert.Equal(new Uri("http://first:1/"), proxy.GetProxy(target));
        var first = credentials.GetCredential(target, "Basic")!;
        Assert.Equal(("CORP", user, one), (first.Domain, first.UserName, first.Password));

        proxy.Update(new ProxyConfiguration(ProxyMode.Manual, "second:2", UserName: user, Password: two));
        Assert.Equal(new Uri("http://second:2/"), proxy.GetProxy(target));
        Assert.Equal(two, credentials.GetCredential(target, "Basic")!.Password);

        proxy.Update(ProxyConfiguration.Direct);
        Assert.True(proxy.IsBypassed(target));
        Assert.Null(credentials.GetCredential(target, "Basic"));
        // A manual proxy without an address cannot break every client: requests go direct until it is fixed.
        proxy.Update(new ProxyConfiguration(ProxyMode.Manual, ""));
        Assert.True(proxy.IsBypassed(target));
    }

    [Fact]
    public void AConfigurationNeverPrintsItsPassword()
    {
        var (user, password) = (TestSecrets.Make("user"), TestSecrets.Make("proxy"));
        var configuration = new ProxyConfiguration(ProxyMode.Manual, "proxy:8080", UserName: user, Password: password);
        Assert.DoesNotContain(password, configuration.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, configuration.Describe(), StringComparison.Ordinal);
        Assert.IsType<NetworkCredential>(configuration.CreateCredentials());
    }
}
