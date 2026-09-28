using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.PreviewHost;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class PreviewCaptureEngineTests(PreviewFixture fixture)
{
    private static PreviewRequest Request(long version) => new("C:/preview/Capture.xaml",
        "<probe:CaptureStateControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' Name='Root'/>",
        version, 400, 300);

    [Fact]
    public async Task CaptureObservesRuntimeChangesWithoutReplacingViewDataBindingsOrSurvivingNodeIds()
    {
        fixture.OnDispatcher(() => CaptureStateControl.Constructions = 0);
        var original = await fixture.Engine.RenderAsync(Request(811), default);
        Assert.True(original.Success, string.Join("\n", original.Diagnostics.Select(item => item.Message)));
        var source = Assert.Single(original.Nodes, node => node.Name == "Source");
        var removed = Assert.Single(original.Nodes, node => node.Name == "Removable");
        var before = fixture.OnDispatcher(() =>
        {
            var view = CaptureStateControl.LastInstance!;
            var data = (CaptureStateData)view.DataContext;
            var binding = BindingOperations.GetBindingExpression(view.Source, TextBox.TextProperty);
            data.Title = "Runtime data changed";
            view.Children.Remove(view.Removable);
            var added = new TextBlock { Name = "AddedAtRuntime" };
            added.SetBinding(TextBlock.TextProperty, new Binding("MissingMember"));
            view.Children.Add(added);
            return (view, data, binding);
        });

        var captured = await fixture.Engine.CaptureAsync(new(original.Version), default);
        Assert.True(captured.Success, captured.Status);
        Assert.Equal(original.Version, captured.Version);
        Assert.NotEmpty(captured.PngBytes!);
        Assert.False(original.PngBytes!.SequenceEqual(captured.PngBytes!));
        Assert.Equal(source.Id, Assert.Single(captured.Nodes, node => node.Name == "Source").Id);
        Assert.DoesNotContain(captured.Nodes, node => node.Id == removed.Id);
        Assert.Single(captured.Nodes, node => node.Name == "AddedAtRuntime");
        Assert.Contains(captured.Diagnostics, diagnostic => diagnostic.Message.Contains("MissingMember", StringComparison.Ordinal));
        var inspection = await fixture.Engine.InspectAsync(new(captured.Version, source.Id), default);
        Assert.Contains(inspection.Properties, property => property.Name == "Text" && property.Value == "Runtime data changed");
        fixture.OnDispatcher(() =>
        {
            Assert.Same(before.view, CaptureStateControl.LastInstance);
            Assert.Same(before.data, before.view.DataContext);
            Assert.Same(before.binding, BindingOperations.GetBindingExpression(before.view.Source, TextBox.TextProperty));
            Assert.Equal(1, CaptureStateControl.Constructions);
            return true;
        });
    }

    [Fact]
    public async Task CaptureRetainsTemporaryEditsAndTheirOriginalResetBaseline()
    {
        var rendered = await fixture.Engine.RenderAsync(Request(812), default);
        Assert.True(rendered.Success);
        var source = Assert.Single(rendered.Nodes, node => node.Name == "Source");
        var edited = await fixture.Engine.SetPropertyAsync(new(rendered.Version, source.Id, "Text", "Preview override"), default);
        Assert.True(edited.Success, edited.Error);
        var captured = await fixture.Engine.CaptureAsync(new(rendered.Version), default);
        Assert.True(captured.Success);
        var retained = await fixture.Engine.InspectAsync(new(rendered.Version, source.Id), default);
        Assert.Contains(retained.Properties, property => property.Name == "Text" && property.Value == "Preview override" && property.IsOverridden);
        var reset = await fixture.Engine.SetPropertyAsync(new(rendered.Version, source.Id, "Text", null, Reset: true), default);
        Assert.True(reset.Success, reset.Error);
        Assert.Contains(reset.Inspection.Properties, property => property.Name == "Text" && property.Value == "Original data"
            && property.BindingPath == nameof(CaptureStateData.Title) && !property.IsOverridden);
    }

    [Fact]
    public async Task CaptureSeparatesCurrentBindingFailuresFromRetainedTraceHistory()
    {
        var rendered = await fixture.Engine.RenderAsync(Request(817), default);
        Assert.True(rendered.Success);
        var source = Assert.Single(rendered.Nodes, node => node.Name == "Source");
        fixture.OnDispatcher(() => CaptureStateControl.LastInstance!.Source.SetBinding(TextBox.TextProperty,
            new Binding("MissingMember") { Mode = BindingMode.OneWay }));
        var failed = await fixture.Engine.CaptureAsync(new(rendered.Version), default);
        Assert.True(failed.Success);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.NodeId == source.Id && diagnostic.Property == "Text"
            && diagnostic.Severity == "Error");
        Assert.True(failed.Diagnostics.Any(diagnostic => diagnostic.Message.StartsWith("Historical WPF binding trace:", StringComparison.Ordinal)
            && diagnostic.Message.Contains("MissingMember", StringComparison.Ordinal) && diagnostic.Severity == "Warning"),
            fixture.OnDispatcher(() => "Source level=" + PresentationTraceSources.DataBindingSource.Switch.Level + "; listeners=" +
                string.Join(",", PresentationTraceSources.DataBindingSource.Listeners.Cast<System.Diagnostics.TraceListener>().Select(listener => listener.GetType().FullName))));

        fixture.OnDispatcher(() => CaptureStateControl.LastInstance!.Source.DataContext = new CaptureRepairedData());
        var repaired = await fixture.Engine.CaptureAsync(new(rendered.Version), default);
        Assert.True(repaired.Success);
        Assert.DoesNotContain(repaired.Diagnostics, diagnostic => diagnostic.NodeId == source.Id && diagnostic.Property == "Text"
            && diagnostic.Severity == "Error");
        Assert.Contains(repaired.Diagnostics, diagnostic => diagnostic.Message.StartsWith("Historical WPF binding trace:", StringComparison.Ordinal)
            && diagnostic.Message.Contains("MissingMember", StringComparison.Ordinal) && diagnostic.Severity == "Warning");
        Assert.Contains((await fixture.Engine.InspectAsync(new(rendered.Version, source.Id), default)).Properties,
            property => property.Name == "Text" && property.Value == "Recovered" && property.BindingStatus == "Active");
    }

    [Fact]
    public async Task StaleOrFailedPreviewCaptureReturnsNoObservations()
    {
        var current = await fixture.Engine.RenderAsync(Request(813), default);
        Assert.True(current.Success);
        AssertUnavailable(await fixture.Engine.CaptureAsync(new(812), default), 812);
        Assert.True((await fixture.Engine.CaptureAsync(new(813), default)).Success);
        var failed = await fixture.Engine.RenderAsync(Request(814) with { Text = "<Unfinished" }, default);
        Assert.False(failed.Success);
        AssertUnavailable(await fixture.Engine.CaptureAsync(new(814), default), 814);
    }

    [Fact]
    public async Task CaptureRechecksAvailabilityAfterTheDispatcherSettles()
    {
        var engine = fixture.OnDispatcher(() => new PreviewEngine(Dispatcher.CurrentDispatcher));
        try
        {
            AssertUnavailable(await engine.CaptureAsync(new(0), default), 0);
            Assert.True((await engine.RenderAsync(Request(815), default)).Success);
            var capture = fixture.OnDispatcher(() =>
            {
                var pending = engine.CaptureAsync(new(815), default);
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(engine.Dispose));
                return pending;
            });
            AssertUnavailable(await capture, 815);
        }
        finally { fixture.OnDispatcher(() => { engine.Dispose(); return true; }); }
    }

    private static void AssertUnavailable(PreviewSnapshot result, long version)
    {
        Assert.False(result.Success);
        Assert.Equal(version, result.Version);
        Assert.Null(result.PngBytes);
        Assert.Equal(0, result.PixelWidth);
        Assert.Equal(0, result.PixelHeight);
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Diagnostics);
        Assert.Null(result.Build);
        Assert.Null(result.Scenario);
        Assert.Contains("preview", result.Status!, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PreviewCaptureProcessTests
{
    [Fact]
    public async Task IsolatedHostKeepsRealBindingTraceAfterFirstBindingInitialization()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var rendered = await client.RenderAsync(new("C:/preview/Trace.xaml",
            "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' DataContext='sample' Text='{Binding Lenght}'/>", 818));
        Assert.True(rendered.Success);
        var captured = await client.CaptureAsync(new(rendered.Version));
        Assert.True(captured.Success);
        Assert.Contains(captured.Diagnostics, diagnostic => diagnostic.NodeId is not null && diagnostic.Property == "Text"
            && diagnostic.Severity == "Error");
        Assert.Contains(captured.Diagnostics, diagnostic => diagnostic.Message.StartsWith("Historical WPF binding trace:", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Lenght", StringComparison.Ordinal) && diagnostic.Severity == "Warning");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompiledCapturePreservesProcessConstructorsScenarioDataAndPropertyEdits(bool scenario)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var request = new PreviewRequest("C:/preview/CompiledCapture.xaml", "<Unfinished", 816, 400, 300,
            typeof(CaptureStateControl).Assembly.Location, AppContext.BaseDirectory, PreviewMode.Compiled,
            typeof(CaptureStateControl).FullName, ApplicationResourcePath: null,
            Scenario: scenario ? new("Capture state", new(typeof(CaptureStateFactories).FullName!, nameof(CaptureStateFactories.View)),
                new(typeof(CaptureStateFactories).FullName!, nameof(CaptureStateFactories.Data))) : null);
        var rendered = await client.RenderAsync(request);
        Assert.True(rendered.Success, string.Join("\n", rendered.Diagnostics.Select(item => item.Message)));
        var processId = client.ProcessId;
        var source = Assert.Single(rendered.Nodes, node => node.Name == "Source");
        var context = Assert.Single(rendered.Nodes, node => node.Name == "ContextIdentity");
        var count = Assert.Single(rendered.Nodes, node => node.Name == "ConstructorCount");
        var persistentControls = new[] { source, context, count, Assert.Single(rendered.Nodes, node => node.Name == "Removable") };
        var contextBefore = await ReadTextProperty(context.Id);
        var contextBinding = Assert.IsType<WpfStudio.Inspection.Protocol.BindingSourcesSnapshot>(contextBefore.BindingSources);
        var contextDeclaration = Assert.Single(contextBinding.Declarations);
        Assert.Equal("1", await ReadText(count.Id));
        Assert.Equal(scenario ? "Scenario data factory 1" : "Original data", await ReadText(source.Id));
        Assert.True((await client.SetPropertyAsync(new(rendered.Version, source.Id, "Text", "Retained override"))).Success);

        for (int i = 0; i < 2; i++)
        {
            var captured = await client.CaptureAsync(new(rendered.Version));
            Assert.True(captured.Success, captured.Status);
            Assert.Equal(processId, client.ProcessId);
            Assert.Equal(rendered.Version, captured.Version);
            Assert.Equal(rendered.Build, captured.Build);
            Assert.Equal(rendered.Scenario, captured.Scenario);
            // Text changes may regenerate framework text visuals. The actual
            // view controls and untouched binding expression must survive.
            foreach (var control in persistentControls)
                Assert.Equal(control.Id, Assert.Single(captured.Nodes, node => node.Name == control.Name).Id);
            var currentContext = await ReadTextProperty(context.Id);
            Assert.Equal(contextBefore.Value, currentContext.Value);
            var currentBinding = Assert.IsType<WpfStudio.Inspection.Protocol.BindingSourcesSnapshot>(currentContext.BindingSources);
            Assert.Equal(contextBinding.BindingId, currentBinding.BindingId);
            var currentDeclaration = Assert.Single(currentBinding.Declarations);
            Assert.Equal(contextDeclaration.ExpressionId, currentDeclaration.ExpressionId);
            Assert.Equal(contextDeclaration.DeclarationId, currentDeclaration.DeclarationId);
            Assert.Equal("1", await ReadText(count.Id));
            Assert.Equal("Retained override", await ReadText(source.Id));
        }
        var reset = await client.SetPropertyAsync(new(rendered.Version, source.Id, "Text", null, Reset: true));
        Assert.True(reset.Success, reset.Error);
        Assert.Equal(scenario ? "Scenario data factory 1" : "Original data", await ReadText(source.Id));

        async Task<PreviewProperty> ReadTextProperty(string nodeId) => Assert.Single((await client.InspectAsync(new(rendered.Version, nodeId))).Properties,
            property => property.Name == "Text");
        async Task<string> ReadText(string nodeId) => (await ReadTextProperty(nodeId)).Value;
    }
}

public sealed class CaptureStateControl : StackPanel
{
    public static int Constructions;
    public static CaptureStateControl? LastInstance;
    public TextBox Source { get; }
    public TextBlock Removable { get; } = new() { Name = "Removable", Text = "Before interaction" };

    public CaptureStateControl()
    {
        LastInstance = this;
        DataContext = new CaptureStateData("Original data");
        Children.Add(new TextBlock { Name = "ConstructorCount", Text = (++Constructions).ToString(CultureInfo.InvariantCulture) });
        Source = new TextBox { Name = "Source" };
        Source.SetBinding(TextBox.TextProperty, new Binding(nameof(CaptureStateData.Title)) { Mode = BindingMode.OneWay });
        Children.Add(Source);
        var identity = new TextBlock { Name = "ContextIdentity" };
        identity.SetBinding(TextBlock.TextProperty, new Binding(nameof(CaptureStateData.Identity)));
        Children.Add(identity);
        Children.Add(Removable);
    }
}

public sealed class CaptureStateData(string title) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Identity { get; } = Guid.NewGuid().ToString("D");
    public string Title
    {
        get => title;
        set { title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }
}

public static class CaptureStateFactories
{
    private static int _dataCalls;
    public static CaptureStateControl View() => new();
    public static object Data() => new CaptureStateData("Scenario data factory " + ++_dataCalls);
}

public sealed class CaptureRepairedData
{
    public string MissingMember => "Recovered";
}
