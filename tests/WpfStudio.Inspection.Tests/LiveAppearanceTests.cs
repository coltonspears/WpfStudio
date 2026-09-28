using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveAppearanceTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task CurrentAppearanceAndHistoricalResourcesArePassiveAcrossDispatchers(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        await StartAsync(session, app, timeout.Token);
        Assert.Contains("appearance", session.Hello!.Capabilities);
        var before = await StateAsync(app, timeout.Token);
        Assert.Equal(0, before.Constructors);
        var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var main = Assert.Single(tree.Nodes, node => node.Name == "AppearanceStyle");
        var child = Assert.Single(tree.Nodes, node => node.Name == "ChildAppearanceStyle");
        Assert.NotEqual(main.DispatcherId, child.DispatcherId);

        foreach (string prefix in new[] { "", "Child" })
        {
            var styled = await ObserveAsync(session, tree, prefix + "AppearanceStyle", "Foreground", timeout.Token);
            var style = await session.GetAppearanceAsync(styled.Request, timeout.Token);
            Assert.Equal(styled.Request, style.Request);
            Assert.True(style.Snapshot.Available, style.Snapshot.Status);
            Assert.Contains(style.Snapshot.Facts, fact => fact.Value == "Style");
            Assert.Contains(style.Snapshot.Declarations, declaration => declaration.Property == "Foreground" || declaration.Property?.EndsWith(".Foreground", StringComparison.Ordinal) == true);
            Assert.Contains(style.Snapshot.ResourceScopes.SelectMany(scope => scope.Keys), key => key.Contains("UnusedDeferredValue", StringComparison.Ordinal));
            Assert.Contains(style.Snapshot.ResourceScopes.SelectMany(scope => scope.Keys), key => key.Contains("SharedAccent", StringComparison.Ordinal));

            var staticProperty = await ObserveAsync(session, tree, prefix + "AppearanceStatic", "Foreground", timeout.Token);
            var staticResult = await session.GetAppearanceAsync(staticProperty.Request, timeout.Token);
            Assert.True(staticResult.Snapshot.Available, staticResult.Snapshot.Status);
            Assert.Contains(staticResult.Snapshot.ResourceEvents, item => item.Key.Contains("LocalAccent", StringComparison.Ordinal));

            var dynamicProperty = await ObserveAsync(session, tree, prefix + "AppearanceDynamic", "Foreground", timeout.Token);
            Assert.True(dynamicProperty.Property.IsExpression);
            var dynamicResult = await session.GetAppearanceAsync(dynamicProperty.Request, timeout.Token);
            Assert.True(dynamicResult.Snapshot.Available, dynamicResult.Snapshot.Status);
            Assert.Contains(dynamicResult.Snapshot.Facts, fact => fact.Name == "Local DynamicResource key" && fact.Value.Contains("SharedAccent", StringComparison.Ordinal));
            Assert.DoesNotContain(dynamicResult.Snapshot.ResourceEvents, item => item.Key.Contains("SharedAccent", StringComparison.Ordinal));

            var readOnly = await ObserveAsync(session, tree, prefix + "AppearanceReadOnly", "ReadOnlyText", timeout.Token);
            Assert.False(readOnly.Property.CanEdit);
            Assert.Null(readOnly.Property.EditToken);
            Assert.True((await session.GetAppearanceAsync(readOnly.Request, timeout.Token)).Snapshot.Available);

            var template = await ObserveAsync(session, tree, prefix + "AppearanceTemplate", "Template", timeout.Token);
            Assert.False(template.Property.CanEdit);
            Assert.True((await session.GetAppearanceAsync(template.Request, timeout.Token)).Snapshot.Available);
        }
        Assert.Equal(before, await StateAsync(app, timeout.Token));
        await session.DisconnectAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        Assert.Equal(before, await StateAsync(app, timeout.Token));
    }

    [Fact]
    public async Task AppearanceRejectsForgedUnobservedStaleAndDetachedTargets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        await StartAsync(session, app, timeout.Token);
        var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var readOnly = await ObserveAsync(session, tree, "AppearanceReadOnly", "ReadOnlyText", timeout.Token);
        var styled = await ObserveAsync(session, tree, "AppearanceStyle", "Foreground", timeout.Token);
        foreach (var invalid in new[]
        {
            styled.Request with { PropertyId = Guid.NewGuid().ToString("N") },
            styled.Request with { PropertyId = readOnly.Request.PropertyId, Property = "", OwnerType = null, OwnerAssembly = null },
            styled.Request with { Property = "AnotherProperty" },
            styled.Request with { OwnerType = "Another.Owner" },
            styled.Request with { OwnerAssembly = "AnotherAssembly" },
            styled.Request with { NodeId = "unknown" },
            styled.Request with { PropertyId = null }
        })
        {
            var rejected = await session.GetAppearanceAsync(invalid, timeout.Token);
            Assert.Equal(invalid, rejected.Request);
            Assert.False(rejected.Snapshot.Available);
            Assert.Empty(rejected.Snapshot.Facts);
        }
        var refreshed = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.False((await session.GetAppearanceAsync(styled.Request, timeout.Token)).Snapshot.Available);
        // Knowing a stable property ID does not substitute for observing this revision.
        Assert.False((await session.GetAppearanceAsync(styled.Request with { Revision = refreshed.Revision }, timeout.Token)).Snapshot.Available);
        styled = await ObserveAsync(session, refreshed, "AppearanceStyle", "Foreground", timeout.Token);
        Assert.True((await session.GetAppearanceAsync(styled.Request, timeout.Token)).Snapshot.Available);
        await app.SendAsync("appearance-unload", timeout.Token);
        Assert.False((await session.GetAppearanceAsync(styled.Request, timeout.Token)).Snapshot.Available);
        await session.DisconnectAsync(timeout.Token);
        AppearanceState state;
        var deadline = DateTime.UtcNow.AddSeconds(6);
        do
        {
            await app.SendAsync("appearance-collect", timeout.Token);
            state = await StateAsync(app, timeout.Token);
            if (!state.RemovedAlive) break;
            await Task.Delay(50, timeout.Token);
        } while (DateTime.UtcNow < deadline);
        Assert.False(state.RemovedAlive);
        Assert.Equal(0, state.Constructors);
        Assert.Equal(0, state.Formattings);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }

    private static async Task StartAsync(InspectionSession session, FixtureApplication app, CancellationToken token)
    {
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(token));
        await session.WaitForConnectionAsync(token);
    }

    private static async Task<(AppearanceRequest Request, InspectionProperty Property)> ObserveAsync(InspectionSession session,
        InspectionTree tree, string name, string property, CancellationToken token)
    {
        var node = Assert.Single(tree.Nodes, node => node.Name == name);
        var element = await session.InspectAsync(new(tree.Revision, node.Id), token);
        Assert.True(element.Available, element.Status);
        var selected = Assert.Single(element.Properties, item => item.Name == property);
        Assert.NotNull(selected.PropertyId);
        return (new(tree.Revision, node.Id, selected.Name, selected.OwnerType, selected.OwnerAssembly, selected.PropertyId), selected);
    }

    private static async Task<AppearanceState> StateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("appearance-state", token);
        return JsonSerializer.Deserialize<AppearanceState>(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "appearance-state.json"), token))!;
    }
    private sealed record AppearanceState(int Constructors, int Formattings, bool RemovedAlive);
}
