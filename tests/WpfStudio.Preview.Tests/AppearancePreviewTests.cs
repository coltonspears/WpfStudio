using System.Diagnostics;
using System.IO;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class AppearancePreviewTests
{
    private static readonly string AssemblyPath = Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll");
    private static PreviewRequest CompiledRequest => new("C:/project/AppearanceView.xaml", "<Unsaved/>", 500,
        400, 300, AssemblyPath, AppContext.BaseDirectory, PreviewMode.Compiled,
        "WpfStudio.PreviewFixture.AppearanceView", ApplicationResourcePath: null);

    [Fact]
    public async Task CompiledStylesScopesAndLocalDynamicKeysAreObservedWithoutInflatingUnusedValues()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var render = await client.RenderAsync(CompiledRequest);
        Assert.True(render.Success, string.Join("\n", render.Diagnostics.Select(d => d.Message)));
        using (var process = Process.GetProcessById(client.ProcessId!.Value))
            Assert.Equal(PreviewHostUnderTest.ExecutablePath, process.MainModule!.FileName, StringComparer.OrdinalIgnoreCase);
        await AssertCounters(client, render);
        var target = Assert.Single(render.Nodes, node => node.Name == "AppearanceTarget");
        var request = await PropertyRequest(client, render, target, "Background");
        var response = await client.GetAppearanceAsync(request);
        Assert.Equal(request, response.Request);
        var snapshot = response.Snapshot;
        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Contains(snapshot.Facts, fact => fact.Name == "Base value source" && fact.Value == "Style");
        Assert.Contains(snapshot.Declarations, declaration => declaration.Kind == "Style");
        Assert.Contains(snapshot.Declarations, declaration => declaration.Kind == "BasedOn");
        Assert.Contains(snapshot.Declarations, declaration => declaration.Kind == "Trigger");
        Assert.Contains(snapshot.Declarations, declaration => declaration.Kind == "DataTrigger");
        Assert.Contains(snapshot.Declarations, declaration => declaration.Kind == "Setter" && declaration.Property!.EndsWith(".Background"));
        Assert.Contains(snapshot.ResourceScopes, scope => scope.Keys.Contains("\"NeverConstructed\""));
        Assert.Contains(snapshot.ResourceScopes, scope => scope.Keys.Contains("(WpfStudio.PreviewFixture.AppearanceKey)"));
        Assert.Contains(snapshot.ResourceScopes, scope => scope.ParentId is not null && scope.Keys.Contains("\"AppearanceAccent\""));

        // Reading the hidden Tag setter's Value would instantiate NeverConstructed.
        var tag = await client.GetAppearanceAsync(await PropertyRequest(client, render, target, "Tag"));
        Assert.Contains(tag.Snapshot.Declarations, declaration => declaration.Kind == "Setter" && declaration.Property!.EndsWith(".Tag"));
        var dynamic = Assert.Single(render.Nodes, node => node.Name == "DynamicTarget");
        var dynamicSnapshot = (await client.GetAppearanceAsync(await PropertyRequest(client, render, dynamic, "Background"))).Snapshot;
        Assert.Contains(dynamicSnapshot.Facts, fact => fact.Name == "Local DynamicResource key" && fact.Value == "\"AppearanceAccent\"");
        await AssertCounters(client, render);
    }

    [Fact]
    public async Task StaticMergedLookupIsHistoricalAfterAPropertyOverrideAndClearsForTheNextRender()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var render = await client.RenderAsync(CompiledRequest);
        Assert.True(render.Success, string.Join("\n", render.Diagnostics.Select(d => d.Message)));
        var target = Assert.Single(render.Nodes, node => node.Name == "StaticTarget");
        var request = await PropertyRequest(client, render, target, "Background");
        var before = (await client.GetAppearanceAsync(request)).Snapshot;
        Assert.Contains(before.ResourceEvents, entry => entry.Key == "\"AppearanceAccent\"" &&
            entry.Property.EndsWith(".Background") && entry.DictionaryUri?.Contains("Appearance.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var edited = await client.SetPropertyAsync(new(render.Version, target.Id, request.Property, "Red",
            OwnerType: request.OwnerType, OwnerAssembly: request.OwnerAssembly));
        Assert.True(edited.Success, edited.Error);
        var after = (await client.GetAppearanceAsync(request)).Snapshot;
        Assert.Equal(before.ResourceEvents.Select(item => item.Id), after.ResourceEvents.Select(item => item.Id));
        Assert.Contains(after.Notices, notice => notice.Contains("historical", StringComparison.OrdinalIgnoreCase));
        Assert.False((await client.GetAppearanceAsync(request with { Revision = render.Version - 1 })).Snapshot.Available);
        Assert.False((await client.GetAppearanceAsync(request with { OwnerType = "Incorrect.Owner" })).Snapshot.Available);

        const string markup = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Button x:Name='Plain' Background='White'/></Grid>";
        var next = await client.RenderAsync(new("C:/project/Plain.xaml", markup, 501, 400, 300));
        Assert.True(next.Success);
        var plain = Assert.Single(next.Nodes, node => node.Name == "Plain");
        var nextAppearance = (await client.GetAppearanceAsync(await PropertyRequest(client, next, plain, "Background"))).Snapshot;
        Assert.Empty(nextAppearance.ResourceEvents);
        Assert.False((await client.GetAppearanceAsync(request)).Snapshot.Available);
    }

    [Fact]
    public async Task SourcePreviewCapturesStaticResourcesAndRejectsOldNodesOnSameProcessRerender()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        const string markup = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Grid.Resources><SolidColorBrush x:Key='Accent' Color='Teal'/></Grid.Resources><Button x:Name='Target' Background='{StaticResource Accent}'/></Grid>";
        var render = await client.RenderAsync(new("C:/project/Source.xaml", markup, 502, 400, 300));
        Assert.True(render.Success, string.Join("\n", render.Diagnostics.Select(d => d.Message)));
        var target = Assert.Single(render.Nodes, node => node.Name == "Target");
        var request = await PropertyRequest(client, render, target, "Background");
        var before = (await client.GetAppearanceAsync(request)).Snapshot;
        Assert.Contains(before.ResourceEvents, entry => entry.Key == "\"Accent\"");
        int? pid = client.ProcessId;
        var next = await client.RenderAsync(new("C:/project/Source.xaml", markup.Replace("{StaticResource Accent}", "White", StringComparison.Ordinal), 503, 400, 300));
        Assert.True(next.Success);
        Assert.Equal(pid, client.ProcessId);
        var nextTarget = Assert.Single(next.Nodes, node => node.Name == "Target");
        Assert.Empty((await client.GetAppearanceAsync(await PropertyRequest(client, next, nextTarget, "Background"))).Snapshot.ResourceEvents);
        Assert.False((await client.GetAppearanceAsync(request)).Snapshot.Available);
    }

    private static async Task<AppearanceRequest> PropertyRequest(PreviewClient client, PreviewSnapshot render, PreviewNode node, string property)
    {
        var inspection = await client.InspectAsync(new(render.Version, node.Id));
        var row = Assert.Single(inspection.Properties, value => value.Name == property);
        return new(render.Version, node.Id, row.Name, row.OwnerType, row.OwnerAssembly);
    }

    private static async Task AssertCounters(PreviewClient client, PreviewSnapshot render)
    {
        var counters = Assert.Single(render.Nodes, node => node.Name == "AppearanceCounters");
        var inspection = await client.InspectAsync(new(render.Version, counters.Id));
        Assert.Contains(inspection.Properties, property => property.Name == "Text" && property.Value == "constructed=0; formatted=0");
    }
}
