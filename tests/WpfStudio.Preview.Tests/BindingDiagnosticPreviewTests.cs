using System.IO;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class BindingDiagnosticPreviewTests
{
    [Theory]
    [InlineData(PreviewMode.Source)]
    [InlineData(PreviewMode.Compiled)]
    public async Task PreviewReportsCachedPathValidationAndCompositeFailuresWithoutReevaluatingApplicationCode(PreviewMode mode)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(Request(mode));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(item => item.Message)));
        var missing = await Property(client, snapshot, "Missing");
        var absent = Assert.IsType<InspectionBinding>(missing.Binding);
        Assert.Equal("PathError", absent.Status);
        AssertUnresolved(absent);
        AssertUnresolved(Assert.IsType<InspectionBinding>((await Property(client, snapshot, "Null")).Binding));
        Assert.Contains(absent.Details!.Evidence, evidence => evidence.Kind == "MissingMember" && evidence.Member == "Value");
        var declared = Assert.IsType<InspectionBinding>(Assert.Single(missing.BindingSources!.Declarations).Observation);
        Assert.Equal(absent.Category, declared.Category);
        Assert.Equal(absent.Path, declared.Path);
        AssertUnresolved(declared);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.NodeId == Node(snapshot, "Missing").Id &&
            diagnostic.BindingId == missing.BindingSources.BindingId && diagnostic.Severity == "Error");
        var fallback = await Property(client, snapshot, "Fallback");
        Assert.Equal("Fallback displayed", fallback.Value);
        AssertUnresolved(Assert.IsType<InspectionBinding>(fallback.Binding));
        Assert.True(fallback.Binding!.Details!.PathState!.UsesFallbackValue);
        var targetNull = await Property(client, snapshot, "TargetNull");
        Assert.Equal("Null displayed", targetNull.Value);
        Assert.Null(targetNull.Binding!.Details!.PathState!.FirstUnresolvedLevel);
        Assert.Equal("Active", targetNull.Binding.Status);
        Assert.Equal("UpdateTargetError", (await Property(client, snapshot, "Conversion", "Number")).Binding!.Status);
        Assert.Equal("UpdateTargetError", (await Property(client, snapshot, "ConverterUnset")).Binding!.Status);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.NodeId == Node(snapshot, "ConverterDoNothing").Id && diagnostic.Severity == "Error");
        var multi = await Property(client, snapshot, "Multi");
        Assert.Equal("", multi.Value);
        Assert.Equal("ChildBindingError", multi.Binding!.Category);
        Assert.Contains(multi.BindingSources!.Declarations, declaration => declaration.ChildIndex == 1 &&
            declaration.Observation?.Details?.PathState?.FirstUnresolvedLevel == 1);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.BindingId == multi.BindingSources.BindingId);
        var priority = await Property(client, snapshot, "Priority");
        Assert.Equal("Active", priority.Binding!.Category);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.BindingId == priority.BindingSources!.BindingId);
        Assert.Contains(priority.BindingSources!.Declarations, declaration => declaration.ChildIndex == 0 && declaration.Observation is not null);
        Assert.Contains(priority.BindingSources.Declarations, declaration => declaration.ChildIndex == 2 &&
            declaration.Observation?.Status == "Inactive");
        // WPF does not reset this parent's initial transfer-error status when a
        // later explicitly sourced child succeeds. Preserve both current facts.
        var priorityAfterFailure = await Property(client, snapshot, "PriorityAfterFailure");
        Assert.Equal("Healthy value", priorityAfterFailure.Value);
        Assert.Equal("UpdateTargetError", priorityAfterFailure.Binding!.Status);
        Assert.Contains(priorityAfterFailure.BindingSources!.Declarations, declaration => declaration.ChildIndex == 1 &&
            declaration.Observation?.Status == "Active");

        // A converter suppresses a subsequent transfer after WPF has accepted an
        // initial value. Initial DoNothing without a fallback can legitimately
        // leave WPF in UpdateTargetError when its default value is requested.
        await Command(client, snapshot, "do-nothing");
        snapshot = await client.CaptureAsync(new(snapshot.Version));
        var unchanged = await Property(client, snapshot, "ConverterDoNothing");
        Assert.Equal("Initial accepted value", unchanged.Value);
        Assert.Equal("Active", unchanged.Binding!.Status);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.BindingId == unchanged.BindingSources!.BindingId);

        await Command(client, snapshot, "validate");
        var validation = await Property(client, snapshot, "Validation");
        Assert.True(validation.Binding!.HasValidationError);
        Assert.Contains(validation.Binding.Details!.ValidationErrors, error => error.Message!.Contains("not evaluated", StringComparison.Ordinal));
        var before = await Counters(client, snapshot, "before");
        Assert.True(before.Gets > 0);
        Assert.True(before.Conversions > 0);
        Assert.True(before.DescriptorNames > 0);
        Assert.True(before.DescriptorGets > 0);
        for (int i = 0; i < 3; i++)
        {
            snapshot = await client.CaptureAsync(new(snapshot.Version));
            foreach (string name in new[] { "Missing", "Null", "Stable", "Descriptor", "Validation", "Multi", "Priority" })
                Assert.NotNull((await Property(client, snapshot, name)).Binding);
        }
        Assert.Equal(before, await Counters(client, snapshot, "after"));

        await Command(client, snapshot, "null-intermediate");
        var changed = (await Property(client, snapshot, "Missing")).Binding!;
        AssertUnresolved(changed);
        Assert.DoesNotContain("DiagnosticMissing", changed.Explanation);
        Assert.Contains(changed.Details!.Evidence, evidence => evidence.Kind == "MissingMember");
        Assert.Contains(changed.Details.Evidence, evidence => evidence.Kind == "NullPathItem");
        await Command(client, snapshot, "recover");
        snapshot = await client.CaptureAsync(new(snapshot.Version));
        foreach (string name in new[] { "Missing", "Null", "Fallback" })
        {
            var recovered = await Property(client, snapshot, name);
            Assert.True(recovered.Binding!.Category == "Active", name + ": " + JsonSerializer.Serialize(recovered));
            Assert.Equal("Healthy value", recovered.Value);
            Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.NodeId == Node(snapshot, name).Id && diagnostic.Severity == "Error");
        }
        // MultiBinding retains its parent transfer-error status after the child
        // recovers, even though a later StringFormat transfer succeeds. Report
        // that raw status separately from current child failure classification.
        var recoveredMulti = await Property(client, snapshot, "Multi");
        Assert.True(recoveredMulti.Value == "Healthy value Healthy value", JsonSerializer.Serialize(recoveredMulti));
        Assert.Equal("UpdateTargetError", recoveredMulti.Binding!.Status);
        Assert.Equal("UpdateTargetError", recoveredMulti.Binding.Category);
        var recoveredChildren = recoveredMulti.BindingSources!.Declarations.Where(declaration => declaration.ParentExpressionId is not null).ToArray();
        Assert.Equal(2, recoveredChildren.Length);
        Assert.All(recoveredChildren, declaration =>
        {
            Assert.True(declaration.Observation?.Category == "Active", JsonSerializer.Serialize(declaration));
            Assert.Null(declaration.Observation!.Details!.PathState!.FirstUnresolvedLevel);
        });
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.BindingId == recoveredMulti.BindingSources.BindingId &&
            diagnostic.Message.Contains("failing child", StringComparison.Ordinal));
        var restored = await Property(client, snapshot, "Missing");
        Assert.Equal(missing.BindingSources.BindingId, restored.BindingSources!.BindingId);
        Assert.NotEmpty(restored.Binding!.Details!.Evidence);
        Assert.Null(restored.Binding.Details.PathState!.FirstUnresolvedLevel);

        var next = await client.RenderAsync(Request(mode) with { Version = snapshot.Version + 1 });
        Assert.True(next.Success);
        var fresh = await Property(client, next, "Missing");
        Assert.NotEqual(restored.BindingSources.BindingId, fresh.BindingSources!.BindingId);
        Assert.DoesNotContain(fresh.Binding!.Details!.Evidence, evidence => evidence.Kind == "NullPathItem");
    }

    private static void AssertUnresolved(InspectionBinding binding)
    {
        var path = Assert.IsType<InspectionBindingPathState>(binding.Details?.PathState);
        Assert.True(path.Available, path.UnavailableReason);
        Assert.Equal(1, path.FirstUnresolvedLevel);
        Assert.Equal("Child", path.Segments[0].Name);
        Assert.Equal("Value", path.Segments[1].Name);
    }
    private static PreviewRequest Request(PreviewMode mode) => new("C:/project/BindingDiagnostics.xaml",
        "<c:BindingDiagnosticView xmlns:c='clr-namespace:WpfStudio.PreviewFixture'/>", 971, 500, 400,
        Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll"), AppContext.BaseDirectory,
        mode, "WpfStudio.PreviewFixture.BindingDiagnosticView", ApplicationResourcePath: null);
    private static PreviewNode Node(PreviewSnapshot snapshot, string name) => Assert.Single(snapshot.Nodes, node => node.Name == name);
    private static async Task<PreviewProperty> Property(PreviewClient client, PreviewSnapshot snapshot, string name, string property = "Text") =>
        Assert.Single((await client.InspectAsync(new(snapshot.Version, Node(snapshot, name).Id))).Properties, value => value.Name == property);
    private static async Task Command(PreviewClient client, PreviewSnapshot snapshot, string command)
    {
        var result = await client.SetPropertyAsync(new(snapshot.Version, Node(snapshot, "DiagnosticRoot").Id, "Command", command));
        Assert.True(result.Success, result.Error);
    }
    private static async Task<DiagnosticCounters> Counters(PreviewClient client, PreviewSnapshot snapshot, string phase)
    {
        await Command(client, snapshot, "state:" + phase);
        return JsonSerializer.Deserialize<DiagnosticCounters>((await Property(client, snapshot, "Counters")).EditableValue!)!;
    }
    private sealed record DiagnosticCounters(int Gets, int Conversions, int DescriptorNames, int DescriptorGets, int Writes, int ToStrings);
}
