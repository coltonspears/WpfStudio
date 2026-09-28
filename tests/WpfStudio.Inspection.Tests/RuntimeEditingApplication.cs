using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

internal sealed class RuntimeEditingApplication : IAsyncDisposable
{
    private RuntimeEditingApplication(InspectionSession session, FixtureApplication app) { Session = session; App = app; }
    public InspectionSession Session { get; }
    public FixtureApplication App { get; }
    public InspectionTree Tree { get; private set; } = null!;

    public static async Task<RuntimeEditingApplication> StartAsync(string framework, CancellationToken token)
    {
        var session = new InspectionSession();
        var app = new FixtureApplication(framework);
        var result = new RuntimeEditingApplication(session, app);
        try
        {
            app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
            session.ExpectProcess(app.ProcessId);
            InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(token));
            await session.WaitForConnectionAsync(token);
            await app.SendAsync("edit-create", token);
            await result.RefreshAsync(token);
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public async Task RefreshAsync(CancellationToken token) => Tree = await Session.SnapshotAsync(cancellationToken: token);

    public async Task<InspectionElement> InspectAsync(string name, CancellationToken token)
    {
        var node = Assert.Single(Tree.Nodes, node => node.Name == name);
        var result = await Session.InspectAsync(new(Tree.Revision, node.Id), token);
        Assert.True(result.Available, result.Status);
        return result;
    }

    public async Task<InspectionPropertyEdit> RequestAsync(string name, string property, string? value, CancellationToken token,
        bool isNull = false, bool reset = false, string? ownerSuffix = null)
    {
        var element = await InspectAsync(name, token);
        return Request(element, property, value, isNull, reset, ownerSuffix);
    }

    public static InspectionPropertyEdit Request(InspectionElement element, string property, string? value,
        bool isNull = false, bool reset = false, string? ownerSuffix = null)
    {
        var selected = Property(element, property, ownerSuffix);
        Assert.False(string.IsNullOrEmpty(selected.PropertyId));
        Assert.False(string.IsNullOrEmpty(selected.EditToken));
        return new(Guid.NewGuid().ToString("N"), element.Revision, element.NodeId, selected.PropertyId!, selected.EditToken!, value, isNull, reset);
    }

    public static InspectionProperty Property(InspectionElement element, string property, string? ownerSuffix = null) =>
        Assert.Single(element.Properties, value => value.Name == property && (ownerSuffix is null || value.OwnerType.EndsWith(ownerSuffix, StringComparison.Ordinal)));

    public async Task<InspectionPropertyEditResult> EditAsync(string name, string property, string? value, CancellationToken token,
        bool isNull = false, bool reset = false, string? ownerSuffix = null)
    {
        var request = await RequestAsync(name, property, value, token, isNull, reset, ownerSuffix);
        return await Session.SetPropertyAsync(request, token);
    }

    public Task<EditingState> StateAsync(CancellationToken token) => ReadStateAsync(App, token);

    public static async Task<EditingState> ReadStateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("edit-state", token);
        return JsonSerializer.Deserialize<EditingState>(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "edit-state.json"), token))!;
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        await App.DisposeAsync();
    }
}

internal sealed record EditingState(EditingProbeState[] Probes, double ChildWidth)
{
    public EditingProbeState this[string name] => Assert.Single(Probes, probe => probe.Name == name);
}

internal sealed record EditingProbeState(string Name, string Value, string? NullableValue, string LongValue, double Number,
    string PaddingValue, string InheritedValue, string ThrowingValue, string SlowValue, int ValueChanges, string? BindingKind, int BindingIdentity,
    string? SourceValue, int SourceWrites, string ValueSource, bool HasLocalValue, int FirstMode, string SecondMode);
