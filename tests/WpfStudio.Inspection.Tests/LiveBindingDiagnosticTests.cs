using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveBindingDiagnosticTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task ActualWpfEvidenceDistinguishesNestedMissingNullConversionAndValidationWithoutReevaluatingModels(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await session.WaitForConnectionAsync(timeout.Token);
        await app.SendAsync("diagnostic-create", timeout.Token);
        var tree = await RunningApplicationTests.WaitForTreeAsync(session,
            value => !value.Truncated && !value.BindingScanTruncated && value.Nodes.Any(node => node.Name == "DiagnosticOpaqueValidation"), timeout.Token);

        var missing = await ReadAsync(session, tree, "DiagnosticMissing", "Text", timeout.Token);
        Assert.Equal("PathError", missing.Status);
        Assert.NotEqual("MissingProperty", missing.Category); // Cached final owner cannot prove a nested cause.
        AssertUnresolved(missing);
        var missingEvidence = missing.Details!.Evidence.LastOrDefault(item => item.Kind == "MissingMember");
        Assert.True(missingEvidence is not null, JsonSerializer.Serialize(missing));
        Assert.NotNull(missingEvidence);
        Assert.Equal(40, missingEvidence.Code);
        Assert.Equal("Value", missingEvidence.Member);
        Assert.EndsWith("DiagnosticMissingChild", missingEvidence.OwnerType);
        Assert.Equal("Child.Value", missingEvidence.BindingPath);
        Assert.NotEqual(default, missingEvidence.ObservedAt);
        Assert.Contains("historical", missing.Details.SourceDescription);

        var nullItem = await ReadAsync(session, tree, "DiagnosticNull", "Text", timeout.Token);
        Assert.Equal("PathError", nullItem.Status);
        AssertUnresolved(nullItem);
        Assert.Contains(nullItem.Details!.Evidence, item => item.Kind == "NullPathItem" && item.Code == 41);
        Assert.DoesNotContain(nullItem.Details.Evidence, item => item.Kind == "MissingMember");
        var fallback = await ReadAsync(session, tree, "DiagnosticFallback", "Text", timeout.Token);
        AssertUnresolved(fallback);
        Assert.True(fallback.Details!.PathState!.UsesFallbackValue);
        var targetNull = await ReadAsync(session, tree, "DiagnosticTargetNull", "Text", timeout.Token);
        Assert.Equal("Active", targetNull.Status);
        Assert.Null(targetNull.Details!.PathState!.FirstUnresolvedLevel);
        var multi = await ReadAsync(session, tree, "DiagnosticMulti", "Text", timeout.Token);
        Assert.Equal("ChildBindingError", multi.Category);
        Assert.Contains(multi.Sources!.Declarations, declaration => declaration.ChildIndex == 1 &&
            declaration.Observation?.Details?.PathState?.FirstUnresolvedLevel == 1);
        var priority = await ReadAsync(session, tree, "DiagnosticPriority", "Text", timeout.Token);
        Assert.Equal("Active", priority.Category);
        Assert.Contains(priority.Sources!.Declarations, declaration => declaration.ChildIndex == 0 && declaration.Observation is not null);
        Assert.Contains(priority.Sources.Declarations, declaration => declaration.ChildIndex == 2 && declaration.Observation?.Status == "Inactive");
        var priorityNode = Assert.Single(tree.Nodes, node => node.Name == "DiagnosticPriority");
        Assert.DoesNotContain(tree.BindingObservations!, observation => observation.NodeId == priorityNode.Id &&
            observation.Binding.Category != "Active");
        var priorityAfterFailureNode = Assert.Single(tree.Nodes, node => node.Name == "DiagnosticPriorityAfterFailure");
        var priorityAfterFailureProperty = Assert.Single((await session.InspectAsync(new(tree.Revision, priorityAfterFailureNode.Id), timeout.Token)).Properties,
            property => property.Name == "Text");
        Assert.Equal("Value resolved", priorityAfterFailureProperty.Value);
        var priorityAfterFailure = Assert.IsType<InspectionBinding>(priorityAfterFailureProperty.Binding);
        Assert.Equal("UpdateTargetError", priorityAfterFailure.Status);
        Assert.Contains(priorityAfterFailure.Sources!.Declarations, declaration => declaration.ChildIndex == 1 &&
            declaration.Observation?.Status == "Active");
        var transfer = await ReadAsync(session, tree, "DiagnosticTransfer", "Number", timeout.Token);
        Assert.Equal("UpdateTargetError", transfer.Status);
        Assert.Contains(transfer.Details!.Evidence, item => item.Kind is "ConversionFailure" or "TargetRejectedValue");

        await app.SendAsync("diagnostic-validation", timeout.Token);
        var conversion = await ReadAsync(session, tree, "DiagnosticConversionValidation", "Text", timeout.Token);
        Assert.True(conversion.HasValidationError);
        Assert.True(conversion.Category == "Conversion", JsonSerializer.Serialize(conversion));
        Assert.Contains(conversion.Details!.ValidationErrors, error => error.Kind == "Conversion" && error.RuleType.EndsWith("ConversionValidationRule"));
        var exceptionValidation = await ReadAsync(session, tree, "DiagnosticExceptionValidation", "Text", timeout.Token);
        Assert.Equal("Validation", exceptionValidation.Category);
        Assert.Contains(exceptionValidation.Details!.ValidationErrors, error => error.Kind == "SourceUpdateException" && error.ExceptionType is not null);
        var validation = await ReadAsync(session, tree, "DiagnosticRuleValidation", "Text", timeout.Token);
        Assert.True(validation.HasValidationError);
        Assert.Equal("Validation", validation.Category);
        Assert.Contains(validation.Details!.ValidationErrors, error => error.Kind == "Validation" && error.Message == "The application rejected this proposal.");
        var opaque = await ReadAsync(session, tree, "DiagnosticOpaqueValidation", "Text", timeout.Token);
        Assert.True(opaque.HasValidationError);
        Assert.Contains(opaque.Details!.ValidationErrors, error => error.Message!.Contains("DiagnosticOpaqueContent") && error.Message.Contains("not evaluated"));

        // Let normal WPF transfers settle before measuring observation-only work.
        var before = await StateAsync(app, timeout.Token);
        Assert.True(before.Gets > 0);
        Assert.True(before.Conversions > 0);
        Assert.True(before.DescriptorNames > 0);
        Assert.True(before.DescriptorGets > 0);
        for (int i = 0; i < 3; i++)
        {
            tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
            var stable = await ReadAsync(session, tree, "DiagnosticStable", "Text", timeout.Token);
            Assert.Equal("Active", stable.Category);
            Assert.Equal("OneWay", stable.Details!.Mode);
            Assert.EndsWith("DiagnosticConverter", stable.Details.ConverterType);
            Assert.EndsWith("DiagnosticChild", stable.ResolvedSourceType);
            Assert.True(stable.Details.PathState!.Available, stable.Details.PathState.UnavailableReason);
            Assert.Null(stable.Details.PathState.FirstUnresolvedLevel);
            var descriptor = await ReadAsync(session, tree, "DiagnosticDescriptor", "Text", timeout.Token);
            Assert.Equal("Active", descriptor.Status);
            await ReadAsync(session, tree, "DiagnosticOpaqueValidation", "Text", timeout.Token);
        }
        var after = await StateAsync(app, timeout.Token);
        Assert.Equal(before, after);
        Assert.Equal(0, after.OpaqueToStrings);
        Assert.Equal(0, after.SourceWrites);

        await app.SendAsync("diagnostic-same-root-null", timeout.Token);
        var changedIntermediate = await ReadAsync(session, tree, "DiagnosticMissing", "Text", timeout.Token);
        Assert.Equal("PathError", changedIntermediate.Category);
        AssertUnresolved(changedIntermediate);
        Assert.Contains(changedIntermediate.Details!.Evidence, item => item.Kind == "NullPathItem" && item.SourceMatches);
        Assert.Contains(changedIntermediate.Details.Evidence, item => item.Kind == "MissingMember");
        // A matching root identity does not make the older missing-child evidence current.
        Assert.DoesNotContain("DiagnosticMissingChild", changedIntermediate.Explanation);

        await app.SendAsync("diagnostic-new-source", timeout.Token);
        var recovered = await ReadAsync(session, tree, "DiagnosticMissing", "Text", timeout.Token);
        Assert.Equal("Active", recovered.Status);
        Assert.Equal("Active", recovered.Category);
        Assert.NotEmpty(recovered.Details!.Evidence);
        Assert.All(recovered.Details.Evidence, item => Assert.False(item.SourceMatches));
        Assert.Null(recovered.Details.PathState!.FirstUnresolvedLevel);
        await session.DisconnectAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }

    [Fact]
    public async Task RepeatedFailureEvidenceIsBoundedAndDoesNotReplaceCurrentStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        await app.WaitForReadyAsync(timeout.Token);
        await session.WaitForConnectionAsync(timeout.Token);
        await app.SendAsync("diagnostic-create", timeout.Token);
        await app.SendAsync("diagnostic-many-failures", timeout.Token);
        var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var binding = await ReadAsync(session, tree, "DiagnosticMissing", "Text", timeout.Token);
        Assert.Equal("PathError", binding.Status);
        Assert.True(binding.Details!.EvidenceTruncated);
        Assert.InRange(binding.Details.Evidence.Count, 1, 2);
        Assert.All(binding.Details.Evidence, item => Assert.InRange(item.Message.Length, 0, 769));
        Assert.Equal(binding.Details.Evidence.Count, binding.Details.Evidence.Select(item => item.Id).Distinct().Count());
    }

    private static async Task<InspectionBinding> ReadAsync(InspectionSession session, InspectionTree tree, string name, string property, CancellationToken token)
    {
        var node = Assert.Single(tree.Nodes, node => node.Name == name);
        var element = await session.InspectAsync(new(tree.Revision, node.Id), token);
        Assert.True(element.Available, element.Status);
        var value = Assert.Single(element.Properties, item => item.Name == property);
        return Assert.IsType<InspectionBinding>(value.Binding);
    }

    private static async Task<DiagnosticState> StateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("diagnostic-state", token);
        return JsonSerializer.Deserialize<DiagnosticState>(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "diagnostic-state.json"), token))!;
    }

    private static void AssertUnresolved(InspectionBinding binding)
    {
        var path = Assert.IsType<InspectionBindingPathState>(binding.Details?.PathState);
        Assert.True(path.Available, path.UnavailableReason);
        Assert.Equal(1, path.FirstUnresolvedLevel);
        Assert.Equal("Child", path.Segments[0].Name);
        Assert.Equal("Value", path.Segments[1].Name);
    }

    private sealed record DiagnosticState(int Gets, int Conversions, int OpaqueToStrings, int SourceWrites,
        int DescriptorNames, int DescriptorGets);
}
