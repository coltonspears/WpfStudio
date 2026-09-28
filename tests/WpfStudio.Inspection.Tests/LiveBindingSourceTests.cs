using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveBindingSourceTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task ActualDeclarationsAndCompositeChildrenKeepIdentityAndLoaderHintsWithoutReevaluatingSources(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        await StartAsync(session, app, timeout.Token);
        Assert.Contains("binding-source", session.Hello!.Capabilities);
        var tree = await RunningApplicationTests.WaitForTreeAsync(session, value =>
        {
            var node = value.Nodes.SingleOrDefault(node => node.Name == "InlinePrimary");
            return node is not null && value.BindingObservations?.Any(binding => binding.NodeId == node.Id && binding.TargetProperty == "Text") == true;
        }, timeout.Token);
        await app.SendAsync("binding-source-probe-dump", timeout.Token);
        using var probe = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "binding-source-probe.json"), timeout.Token));
        var rows = probe.RootElement.GetProperty("Rows").EnumerateArray().ToArray();
        var observations = new Dictionary<string, Observed>();
        int before = await GetterCountAsync(app, timeout.Token);
        foreach (var (form, name, property) in new[]
        {
            ("Inline", "InlinePrimary", "Text"), ("InlineSameLineOtherProperty", "InlinePrimary", "Tag"),
            ("InlineSameLineSamePath", "InlinePrimary", "ToolTip"), ("InlineEntity", "EntityPrimary", "Text"),
            ("InlineMultiline", "MultilinePrimary", "Text"), ("Object", "ObjectPrimary", "Text"),
            ("StyleSetter", "StylePrimary", "Text"), ("SharedStyleSetter", "StyleShared", "Text"),
            ("Template", "TemplatePrimary", "Text"), ("DataTemplate", "DataTemplatePrimary", "Text"),
            ("Multi", "MultiPrimary", "Text"), ("Priority", "PriorityPrimary", "Text"),
            ("Resource", "ResourcePrimary", "Text"), ("Code", "CodePrimary", "Text")
        })
        {
            var observed = await ObserveAsync(session, tree, name, property, timeout.Token);
            observations[form] = observed;
            Assert.False(observed.Sources.Truncated);
            var expected = rows.Where(row => row.GetProperty("Form").GetString() == form).ToArray();
            Assert.Equal(expected.Length, observed.Sources.Declarations.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                var declaration = observed.Sources.Declarations[i];
                var request = observed.Request(declaration);
                Assert.Equal(expected[i].GetProperty("Kind").GetString(), declaration.Kind);
                Assert.Equal(expected[i].GetProperty("Path").GetString(), declaration.Path);
                if (i == 0)
                {
                    Assert.Equal(observed.Sources.BindingId, declaration.ExpressionId);
                    Assert.Null(declaration.ParentExpressionId);
                }
                else
                {
                    Assert.Equal(observed.Sources.BindingId, declaration.ParentExpressionId);
                    Assert.Equal(i - 1, declaration.ChildIndex);
                }
                var result = await session.GetBindingSourceAsync(request, timeout.Token);
                Assert.Equal(request, result.Request);
                if (expected[i].GetProperty("BindingSource").ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(declaration.Source);
                    Assert.NotNull(declaration.UnavailableReason);
                    Assert.False(result.Available);
                }
                else
                {
                    var hint = expected[i].GetProperty("BindingSource");
                    Assert.Equal(new InspectionSourceHint(hint.GetProperty("Uri").GetString()!,
                        hint.GetProperty("Line").GetInt32(), hint.GetProperty("Column").GetInt32()), declaration.Source);
                    Assert.True(result.Available, result.Status);
                    // Navigation revalidates declaration identity and source. It
                    // must not replay the earlier inspection's diagnostic payload.
                    Assert.Equal(declaration with { Observation = null }, result.Declaration);
                    Assert.Null(result.Declaration!.Observation);
                }
            }
        }
        var firstStyle = observations["StyleSetter"].Sources.Declarations[0];
        var secondStyle = observations["SharedStyleSetter"].Sources.Declarations[0];
        Assert.NotEqual(firstStyle.ExpressionId, secondStyle.ExpressionId);
        Assert.Equal(firstStyle.DeclarationId, secondStyle.DeclarationId);
        Assert.Equal(firstStyle.Source, secondStyle.Source);
        var inline = observations["Inline"];
        Assert.Equal(Assert.Single(tree.BindingObservations!, observation => observation.NodeId == inline.NodeId && observation.TargetProperty == "Text").BindingId,
            inline.Sources.BindingId);
        Assert.NotEqual(inline.Sources.Declarations[0].Source, observations["InlineSameLineSamePath"].Sources.Declarations[0].Source);
        Assert.Equal(before, await GetterCountAsync(app, timeout.Token));
        await session.DisconnectAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }

    [Fact]
    public async Task BindingSourceValidationRejectsForgedStaleReplacedAndUnloadedObservations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        await StartAsync(session, app, timeout.Token);
        var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var inline = await ObserveAsync(session, tree, "InlinePrimary", "Text", timeout.Token);
        var multi = await ObserveAsync(session, tree, "MultiPrimary", "Text", timeout.Token);
        var valid = inline.Request(inline.Sources.Declarations[0]);
        foreach (var forged in new[]
        {
            valid with { PropertyId = Guid.NewGuid().ToString("N") }, valid with { Property = "Tag" },
            valid with { OwnerType = "Unrelated.Owner" }, valid with { OwnerAssembly = "Unrelated" },
            valid with { BindingId = Guid.NewGuid().ToString("N") }, valid with { ExpressionId = multi.Sources.Declarations[1].ExpressionId },
            valid with { DeclarationId = multi.Sources.Declarations[1].DeclarationId }, valid with { NodeId = "unknown" }
        })
        {
            var rejected = await session.GetBindingSourceAsync(forged, timeout.Token);
            Assert.Equal(forged, rejected.Request);
            Assert.False(rejected.Available);
        }
        Assert.True((await session.GetBindingSourceAsync(valid, timeout.Token)).Available);
        var next = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.False((await session.GetBindingSourceAsync(valid, timeout.Token)).Available);
        Assert.False((await session.GetBindingSourceAsync(valid with { Revision = next.Revision }, timeout.Token)).Available);
        var refreshed = await ObserveAsync(session, next, "InlinePrimary", "Text", timeout.Token);
        Assert.Equal(inline.Sources.BindingId, refreshed.Sources.BindingId);
        valid = refreshed.Request(refreshed.Sources.Declarations[0]);
        Assert.True((await session.GetBindingSourceAsync(valid, timeout.Token)).Available);
        await app.SendAsync("binding-source-probe-replace", timeout.Token);
        Assert.False((await session.GetBindingSourceAsync(valid, timeout.Token)).Available);
        var replacement = await ObserveAsync(session, next, "InlinePrimary", "Text", timeout.Token);
        Assert.NotEqual(refreshed.Sources.BindingId, replacement.Sources.BindingId);
        Assert.Equal(refreshed.Sources.Declarations[0].Path, replacement.Sources.Declarations[0].Path);
        Assert.Null(replacement.Sources.Declarations[0].Source);
        Assert.False((await session.GetBindingSourceAsync(replacement.Request(replacement.Sources.Declarations[0]), timeout.Token)).Available);
        var objectBinding = await ObserveAsync(session, next, "ObjectPrimary", "Text", timeout.Token);
        await app.SendAsync("binding-source-probe-unload", timeout.Token);
        Assert.False((await session.GetBindingSourceAsync(objectBinding.Request(objectBinding.Sources.Declarations[0]), timeout.Token)).Available);
    }

    [Fact]
    public async Task LargeCompositeDeclarationTreesAreBoundedAndExplicitlyPartial()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        await StartAsync(session, app, timeout.Token);
        await app.SendAsync("binding-source-probe-large", timeout.Token);
        var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var observed = await ObserveAsync(session, tree, "CodePrimary", "Text", timeout.Token);
        Assert.True(observed.Sources.Truncated);
        Assert.InRange(observed.Sources.Declarations.Count, 1, 65);
        Assert.Equal(observed.Sources.Declarations.Count, observed.Sources.Declarations.Select(item => item.ExpressionId).Distinct().Count());
        Assert.All(observed.Sources.Declarations, declaration => Assert.Null(declaration.Source));
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }

    private sealed record Observed(long Revision, string NodeId, InspectionProperty Property, BindingSourcesSnapshot Sources)
    {
        internal BindingSourceRequest Request(BindingSourceDeclaration declaration) => new(Revision, NodeId, Property.Name,
            Sources.BindingId, declaration.ExpressionId, declaration.DeclarationId, Property.OwnerType, Property.OwnerAssembly, Property.PropertyId);
    }

    private static async Task<Observed> ObserveAsync(InspectionSession session, InspectionTree tree, string name, string property, CancellationToken token)
    {
        var node = Assert.Single(tree.Nodes, item => item.Name == name);
        var element = await session.InspectAsync(new(tree.Revision, node.Id), token);
        Assert.True(element.Available, element.Status);
        var selected = Assert.Single(element.Properties, item => item.Name == property);
        return new(tree.Revision, node.Id, selected, Assert.IsType<BindingSourcesSnapshot>(selected.Binding?.Sources));
    }

    private static async Task StartAsync(InspectionSession session, FixtureApplication app, CancellationToken token)
    {
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        await app.WaitForReadyAsync(token);
        await session.WaitForConnectionAsync(token);
        await app.SendAsync("binding-source-probe-create", token);
        await app.SendAsync("ping", token);
    }

    private static async Task<int> GetterCountAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("binding-source-probe-state", token);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "binding-source-state.json"), token));
        return state.RootElement.GetProperty("GetterCalls").GetInt32();
    }
}
