using WpfStudio.Api.CodeGeneration;
using WpfStudio.Api.Models;
using WpfStudio.Api.Storage;
using WpfStudio.App.Features.Api;
using WpfStudio.App.Services;
using WpfStudio.Core;
using WpfStudio.Core.Net;

namespace WpfStudio.Shell.Tests;

public sealed class ApiClientTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WpfStudio-ApiClientTests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeDesktop _desktop = new();
    private readonly StudioWebProxy _proxy = new(ProxyConfiguration.Direct);
    private readonly List<ApiClientViewModel> _clients = [];

    private async Task<ApiClientViewModel> ClientAsync()
    {
        var client = new ApiClientViewModel(new ApiStore(_root, PlainSecretProtector.Instance), _proxy, _dialogs, _dialogs, _desktop);
        _clients.Add(client);
        await client.InitializeAsync();
        return client;
    }

    [Fact]
    public async Task TheAddressBarAndTheParamsTableEditTheSameQuery()
    {
        var client = await ClientAsync();
        client.NewRequestCommand.Execute(null);
        var tab = client.SelectedTab!;
        tab.UrlText = "https://api.example.com/pets/{petId}?limit=5&tag=dog";

        Assert.Equal(["limit=5", "tag=dog"], tab.Query.ToModels().Select(row => row.Key + "=" + row.Value));
        Assert.Equal("petId", Assert.Single(tab.PathParameters.ToModels()).Key);
        Assert.True(tab.IsDirty);

        tab.Query.Rows[0].Enabled = false;
        Assert.Equal("https://api.example.com/pets/{petId}?tag=dog", tab.UrlText);
        tab.Query.Rows[^1].Key = "page";
        Assert.Equal("https://api.example.com/pets/{petId}?tag=dog&page", tab.UrlText);
        // The disabled row stays in the table, though the address bar leaves it out.
        tab.UrlText = "https://api.example.com/pets/{petId}?tag=cat";
        Assert.Equal(["tag=cat:True", "limit=5:False"], tab.Query.ToModels().Select(row => $"{row.Key}={row.Value}:{row.Enabled}"));
    }

    [Fact]
    public async Task SavingANewRequestAsksWhereAndCanCreateACollection()
    {
        var client = await ClientAsync();
        client.NewRequestCommand.Execute(null);
        var tab = client.SelectedTab!;
        tab.Name = "List pets";
        tab.UrlText = "https://api.example.com/pets";

        var saving = tab.SaveCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<SaveRequestViewModel>(client.SaveDialog);
        dialog.SelectedTarget = dialog.Targets[^1];
        Assert.True(dialog.IsNewCollection);
        dialog.NewCollectionName = "Pet store";
        dialog.SaveCommand.Execute(null);
        await saving;

        var collection = Assert.Single(client.Collections);
        Assert.Equal("Pet store", collection.Name);
        var saved = Assert.Single(collection.Requests);
        Assert.Equal(("List pets", "https://api.example.com/pets"), (saved.Name, saved.Url));
        Assert.False(tab.IsDirty);
        Assert.Equal(saved.Id, tab.SavedRequestId);
        Assert.Null(client.SaveDialog);
        Assert.True(File.Exists(Path.Combine(_root, "collections", collection.Id + ".json")));

        // Saving again writes into the same request without asking.
        tab.UrlText = "https://api.example.com/pets?limit=1";
        await tab.SaveCommand.ExecuteAsync(null);
        Assert.Null(client.SaveDialog);
        Assert.Equal("limit", Assert.Single(Assert.Single(client.Collections).Requests).Query.Single().Key);
    }

    [Fact]
    public async Task OpenTabsAndTheirUnsavedEditsComeBack()
    {
        var first = await ClientAsync();
        first.NewRequestCommand.Execute(null);
        first.SelectedTab!.UrlText = "https://api.example.com/draft?x=1";
        first.SelectedTab.BodyMode = BodyMode.Json;
        first.SelectedTab.Body = """{"a":1}""";
        await first.DisposeAsync();

        var second = await ClientAsync();
        var tab = Assert.Single(second.Tabs);
        Assert.Equal("https://api.example.com/draft?x=1", tab.UrlText);
        Assert.Equal((BodyMode.Json, """{"a":1}"""), (tab.BodyMode, tab.Body));
        Assert.True(tab.IsDirty);
        Assert.Same(tab, second.SelectedTab);
    }

    [Fact]
    public async Task AFailedSendExplainsItselfAndIsKeptInHistory()
    {
        var client = await ClientAsync();
        client.NewRequestCommand.Execute(null);
        var tab = client.SelectedTab!;
        tab.UrlText = "{{baseUrl}}/pets";
        await tab.SendCommand.ExecuteAsync(null);

        Assert.True(tab.Response!.HasError);
        Assert.Contains("{{baseUrl}}", tab.Response.Error, StringComparison.Ordinal);
        var entry = Assert.Single(client.History);
        Assert.Null(entry.StatusCode);
        client.OpenHistoryCommand.Execute(entry);
        Assert.Equal(2, client.Tabs.Count);
        Assert.Equal("{{baseUrl}}/pets", client.SelectedTab!.UrlText);
    }

    [Fact]
    public async Task ARequestSaysWhichProxyItUsesAndFollowsSettings()
    {
        var client = await ClientAsync();
        client.NewRequestCommand.Execute(null);
        var tab = client.SelectedTab!;
        Assert.Contains("No proxy", tab.Proxy.Effective, StringComparison.Ordinal);
        Assert.Contains("Settings", tab.Proxy.Effective, StringComparison.Ordinal);

        _proxy.Update(new ProxyConfiguration(ProxyMode.Manual, "proxy.example.com:8080"));
        Assert.Contains("proxy.example.com:8080", tab.Proxy.Effective, StringComparison.Ordinal);
        Assert.Contains("proxy.example.com:8080", client.ProxySummary, StringComparison.Ordinal);

        tab.Proxy.Mode = ProxyOverrideMode.None;
        Assert.Contains("this request", tab.Proxy.Effective, StringComparison.Ordinal);
        Assert.Equal(ProxyOverrideMode.None, tab.ToModel().Proxy.Mode);
    }

    [Fact]
    public async Task ModelsAreGeneratedFromAJsonBodyAndSavedWhereAsked()
    {
        var client = await ClientAsync();
        client.NewRequestCommand.Execute(null);
        var tab = client.SelectedTab!;
        tab.Name = "Create pet";
        tab.BodyMode = BodyMode.Json;
        tab.Body = """{ "id": 1, "name": "Rex", "tags": [ { "label": "good" } ] }""";
        tab.GenerateModelsCommand.Execute(null);

        var generator = client.CodeGenerator;
        await WaitForAsync(() => generator.IsOpen && generator.Preview.Contains("class CreatePetRequest", StringComparison.Ordinal));
        Assert.Equal(CodeSource.Sample, generator.Source);
        Assert.Contains("[JsonPropertyName(\"name\")]", generator.Preview, StringComparison.Ordinal);

        generator.Framework = JsonFramework.NewtonsoftJson;
        await WaitForAsync(() => generator.Preview.Contains("[JsonProperty(\"name\")]", StringComparison.Ordinal));
        generator.Layout = FileLayout.FilePerType;
        generator.OutputDirectory = Path.Combine(_root, "Models");
        generator.FileName = "Pets.cs";
        generator.OpenAfterSaving = false;
        await generator.SaveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(_root, "Models", "CreatePetRequest.cs")));
        Assert.True(File.Exists(Path.Combine(_root, "Models", "Tag.cs")));
        Assert.True(File.Exists(Path.Combine(_root, "Models", "Pets.cs")));
        Assert.Contains("Newtonsoft.Json", await File.ReadAllTextAsync(Path.Combine(_root, "Models", "Tag.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSettingsApplyAtOnceAndKeepThePasswordProtected()
    {
        var proxy = new StudioWebProxy();
        var network = new NetworkPreferences(proxy, PlainSecretProtector.Instance);
        var changes = 0;
        network.Changed += () => changes++;
        network.Mode = ProxyMode.Manual;
        network.Address = "proxy.corp.example.com:3128";
        // Made fresh each run: literals would read as a leaked credential to secret scanners.
        var (user, password) = ("user-" + Guid.NewGuid().ToString("N")[..8], "proxy-" + Guid.NewGuid().ToString("N")[..12]);
        network.UserName = user;
        network.Password = password;

        Assert.Equal(new Uri("http://proxy.corp.example.com:3128/"), proxy.GetProxy(new Uri("https://api.example.com/")));
        Assert.Equal(password, proxy.Credentials!.GetCredential(new Uri("https://api.example.com/"), "Basic")!.Password);
        Assert.True(changes >= 4);

        var saved = network.ToSettings();
        Assert.Equal(("proxy.corp.example.com:3128", user), (saved.ProxyAddress, saved.ProxyUserName));
        var restored = new NetworkPreferences(new StudioWebProxy(), PlainSecretProtector.Instance);
        restored.Load(saved);
        Assert.Equal((ProxyMode.Manual, password), (restored.Mode, restored.Password));
        Assert.Contains("proxy.corp.example.com", restored.Summary, StringComparison.Ordinal);

        network.Address = "not a proxy://";
        Assert.NotEmpty(network.AddressError);
        // An unusable address sends requests straight to the server rather than breaking them all.
        Assert.True(proxy.IsBypassed(new Uri("https://api.example.com/")));
    }

    [Fact]
    public async Task TheShellSavesAndRestoresTheNetworkSettings()
    {
        await using var test = new ShellTestContext();
        await test.Shell.InitializeAsync([]);
        test.Shell.Network.Mode = ProxyMode.None;
        await test.Shell.TryCloseAsync();
        var settings = await new SettingsStore(Path.Combine(test.Root, "state")).LoadAsync();
        Assert.Equal(ProxyMode.None, settings.Network.ProxyMode);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 25)
        {
            if (waited > 10_000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(25);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
