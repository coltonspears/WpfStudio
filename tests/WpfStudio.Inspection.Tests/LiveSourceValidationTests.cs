using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveSourceValidationTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task SourceValidationUsesCanonicalObservedPropertiesWithoutChangingLiveValues(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        await app.WaitForReadyAsync(timeout.Token);
        await session.WaitForConnectionAsync(timeout.Token);
        Assert.Contains("source-property-validation", session.Hello!.Capabilities);
        await app.SendAsync("source-validation-create", timeout.Token);
        var tree = await RunningApplicationTests.WaitForTreeAsync(session,
            snapshot => snapshot.Nodes.Any(node => node.Name == "SourceOneWayToSource") && !snapshot.Truncated, timeout.Token);
        var probe = await InspectAsync(session, tree, "SourceValidationProbe", timeout.Token);
        var before = await StateAsync(app, timeout.Token);
        var value = Property(probe, "Value");
        Assert.True(value.CanWriteSource, value.SourceUnavailableReason);
        var request = Request(probe, value, "{literal & value}");
        var valid = await session.ValidateSourcePropertyAsync(request, timeout.Token);
        Assert.True(valid.Success, valid.Error);
        Assert.Equal("{literal & value}", valid.Literal);
        Assert.False(valid.Property!.IsAttached);
        Assert.Equal("Value", valid.Property.PropertyName);
        Assert.EndsWith("SourceValidationProbe", valid.Property.OwnerType);
        Assert.NotEqual(Guid.Empty, valid.Property.TargetModuleVersionId);
        Assert.Equal(valid.Property.TargetModuleVersionId, valid.Property.OwnerModuleVersionId);
        var empty = await session.ValidateSourcePropertyAsync(request with { Value = "" }, timeout.Token);
        Assert.True(empty.Success, empty.Error); Assert.Equal("", empty.Literal); Assert.False(empty.IsNull);
        var nullValue = await session.ValidateSourcePropertyAsync(request with { Value = null, IsNull = true }, timeout.Token);
        Assert.True(nullValue.Success, nullValue.Error); Assert.True(nullValue.IsNull); Assert.Null(nullValue.Literal);
        var removed = await session.ValidateSourcePropertyAsync(request with { Remove = true }, timeout.Token);
        Assert.True(removed.Success, removed.Error); Assert.Null(removed.Literal);

        var inherited = await InspectAsync(session, tree, "SourceInheritedProbe", timeout.Token);
        var inheritedResult = await session.ValidateSourcePropertyAsync(Request(inherited, Property(inherited, "SharedValue"), "Revised"), timeout.Token);
        Assert.True(inheritedResult.Success, inheritedResult.Error);
        Assert.EndsWith("SourceAddOwnerProbe", inheritedResult.Property!.OwnerType); // Original registering owner differs.
        var authoredBase = Assert.Single(inheritedResult.Property.AuthoredTargets!, candidate => candidate.Type.EndsWith("SourceAddOwnerProbe"));
        Assert.Equal("SharedValue", authoredBase.ContentProperty);
        var authoredDerived = Assert.Single(inheritedResult.Property.AuthoredTargets!, candidate => candidate.Type.EndsWith("SourceInheritedProbe"));
        Assert.Equal("Width", authoredDerived.ContentProperty);
        Assert.DoesNotContain(inheritedResult.Property.AuthoredTargets!, candidate => candidate.Type == "System.Windows.FrameworkElement");
        var runtimeOnly = await session.ValidateSourcePropertyAsync(Request(inherited, Property(inherited, "RuntimeOnly"), "Revised"), timeout.Token);
        Assert.True(runtimeOnly.Success, runtimeOnly.Error);
        Assert.Single(runtimeOnly.Property!.AuthoredTargets!);

        var first = Property(probe, "SourceOptions.Mode", ".SourceA.SourceOptions");
        var second = Property(probe, "SourceOptions.Mode", ".SourceB.SourceOptions");
        Assert.NotEqual(first.PropertyId, second.PropertyId);
        foreach (var pair in new[] { (Property: first, Value: "9", Owner: ".SourceA.SourceOptions"), (Property: second, Value: "Other", Owner: ".SourceB.SourceOptions") })
        {
            var result = await session.ValidateSourcePropertyAsync(Request(probe, pair.Property, pair.Value), timeout.Token);
            Assert.True(result.Success, result.Error); Assert.True(result.Property!.IsAttached); Assert.EndsWith(pair.Owner, result.Property.OwnerType);
        }
        var crossedToken = await session.ValidateSourcePropertyAsync(Request(probe, first, "9") with { SourceEditToken = second.SourceEditToken! }, timeout.Token);
        Assert.False(crossedToken.Success);

        var button = await InspectAsync(session, tree, "SourceStandardButton", timeout.Token);
        var width = Request(button, Property(button, "Width"), "-1");
        Assert.False((await session.ValidateSourcePropertyAsync(width, timeout.Token)).Success);
        Assert.False((await session.ValidateSourcePropertyAsync(width with { Value = null, IsNull = true }, timeout.Token)).Success);
        var goodWidth = await session.ValidateSourcePropertyAsync(width with { Value = "120.5" }, timeout.Token);
        Assert.True(goodWidth.Success, goodWidth.Error);
        Assert.Equal("System.Windows.FrameworkElement", goodWidth.Property!.OwnerType);
        Assert.Contains(goodWidth.Property.AuthoredTargets!, candidate => candidate.Type == "System.Windows.Controls.Button"
            && candidate.XmlNamespaces.Contains("http://schemas.microsoft.com/winfx/2006/xaml/presentation"));
        Assert.False(Property(button, "ActualWidth").CanWriteSource);

        foreach (var name in new[] { "SourceDynamicButton", "SourceOneWayToSource" })
        {
            var element = await InspectAsync(session, tree, name, timeout.Token);
            var property = Property(element, name == "SourceDynamicButton" ? "Content" : "Value");
            Assert.False(property.CanEdit);
            Assert.True(property.CanWriteSource, property.SourceUnavailableReason);
            Assert.True((await session.ValidateSourcePropertyAsync(Request(element, property, "Proposed source value"), timeout.Token)).Success);
        }
        Assert.False(Property(probe, "Converted").CanWriteSource);
        Assert.False(Property(probe, "NullableEnum").CanWriteSource);
        Assert.False(Property(probe, "Dangerous").CanWriteSource);
        Assert.False(Property(probe, "ReadOnlyValue").CanWriteSource);
        var after = await StateAsync(app, timeout.Token);
        Assert.Equal(before, after); // No getters, converters, custom attributes, setters, or live changes.

        await app.SendAsync("source-validation-french", timeout.Token);
        var number = await session.ValidateSourcePropertyAsync(Request(probe, Property(probe, "Number"), "12.5"), timeout.Token);
        Assert.True(number.Success, number.Error); Assert.Equal("12.5", number.Literal);
        var guarded = Request(probe, Property(probe, "Guarded"), "5");
        before = await StateAsync(app, timeout.Token);
        var checkedValue = await session.ValidateSourcePropertyAsync(guarded, timeout.Token);
        Assert.True(checkedValue.Success, checkedValue.Error);
        after = await StateAsync(app, timeout.Token);
        Assert.Equal(before.ValidationCalls + 1, after.ValidationCalls);
        Assert.Equal(before.Guarded, after.Guarded);
        Assert.True((await session.ValidateSourcePropertyAsync(guarded with { VerifyOnly = true }, timeout.Token)).Success);
        Assert.True((await session.ValidateSourcePropertyAsync(guarded with { Remove = true }, timeout.Token)).Success);
        Assert.Equal(after, await StateAsync(app, timeout.Token));
        var changedByCallback = await session.ValidateSourcePropertyAsync(guarded with { Value = "99" }, timeout.Token);
        Assert.False(changedByCallback.Success);
        Assert.Contains("changed", changedByCallback.Error!, StringComparison.OrdinalIgnoreCase);

        await app.SendAsync("source-validation-change", timeout.Token);
        Assert.False((await session.ValidateSourcePropertyAsync(request, timeout.Token)).Success);
        var refreshed = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.False((await session.ValidateSourcePropertyAsync(width with { Value = "121" }, timeout.Token)).Success);
        var freshProbe = await InspectAsync(session, refreshed, "SourceValidationProbe", timeout.Token);
        var detach = Request(freshProbe, Property(freshProbe, "Value"), "Proposal");
        await app.SendAsync("source-validation-remove", timeout.Token);
        Assert.False((await session.ValidateSourcePropertyAsync(detach, timeout.Token)).Success);
        await session.DisconnectAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }

    private static InspectionSourcePropertyRequest Request(InspectionElement element, InspectionProperty property, string? value)
    {
        Assert.True(property.CanWriteSource, property.SourceUnavailableReason);
        Assert.NotNull(property.SourceEditToken);
        return new(element.Revision, element.NodeId, property.PropertyId!, property.SourceEditToken!, value);
    }
    private static InspectionProperty Property(InspectionElement element, string name, string? owner = null) =>
        Assert.Single(element.Properties, property => property.Name == name && (owner is null || property.OwnerType.EndsWith(owner, StringComparison.Ordinal)));
    private static async Task<InspectionElement> InspectAsync(InspectionSession session, InspectionTree tree, string name, CancellationToken token)
    {
        var node = Assert.Single(tree.Nodes, node => node.Name == name);
        var element = await session.InspectAsync(new(tree.Revision, node.Id), token);
        Assert.True(element.Available, element.Status); return element;
    }
    private static async Task<SourceState> StateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("source-validation-state", token);
        return JsonSerializer.Deserialize<SourceState>(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "source-validation-state.json"), token))!;
    }
    private sealed record SourceState(string Value, int Guarded, int Gets, int Writes, int ValidationCalls, int ConverterCalls, int AttributeConstructions, int WrapperCalls);
}
