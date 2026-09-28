using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class PreviewScenarioTests
{
    private const string FactoryType = "WpfStudio.PreviewFixture.ScenarioFactories";
    private const string DependencyView = "WpfStudio.PreviewFixture.DependencyView";
    private const string Source = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Name='Root' DataContext='Authored data'><TextBlock x:Name='ScenarioMessage' Text='{Binding Title}'/></Grid>";
    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll");
    private static PreviewFactory Factory(string method) => new(FactoryType, method);
    private static PreviewRequest Request(PreviewMode mode, string? view = null, string? data = null, string name = "Test scenario") =>
        new("C:/preview/Scenario.xaml", mode == PreviewMode.Compiled ? "<UnfinishedCurrentBuffer" : Source, 301, 400, 300,
            AssemblyPath, AppContext.BaseDirectory, mode, mode == PreviewMode.Compiled ? DependencyView : null,
            Scenario: new(name, view is null ? null : Factory(view), data is null ? null : Factory(data)));

    [Theory]
    [InlineData("Loading", "Loading orders")]
    [InlineData("Empty", "No orders")]
    [InlineData("Populated", "3 orders")]
    [InlineData("Error", "Unable to load orders")]
    public async Task NamedDataFactoriesRenderCurrentSourceAndKeepAuthoringProvenance(string method, string title)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var request = Request(PreviewMode.Source, data: method, name: method);
        var snapshot = await client.RenderAsync(request);
        Assert.True(snapshot.Success, Errors(snapshot));
        Assert.Null(snapshot.Build);
        var provenance = Assert.IsType<PreviewScenarioProvenance>(snapshot.Scenario);
        Assert.Equal(request.Scenario, provenance.Configuration);
        Assert.Equal(AssemblyPath, provenance.AssemblyPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AssemblyPath))), provenance.AssemblySha256);
        Assert.True(Guid.TryParse(provenance.ModuleVersionId, out _));
        Assert.Equal("System.Windows.Controls.Grid", provenance.ViewTypeName);
        Assert.Equal(title, await TextAsync(client, snapshot, "ScenarioMessage"));
        Assert.NotNull(Assert.Single(snapshot.Nodes, node => node.Name == "ScenarioMessage").Source);
        var root = Assert.Single(snapshot.Nodes, node => node.Name == "Root");
        var inspection = await client.InspectAsync(new(snapshot.Version, root.Id));
        var property = Assert.Single(inspection.Properties, property => property.Name == "DataContext");
        Assert.Equal("Scenario", property.ValueSource); Assert.Null(property.BindingPath); Assert.Null(property.BindingStatus);
        Assert.False(property.CanWriteSource);
        Assert.False((await client.ValidatePropertyAsync(new(snapshot.Version, root.Id, "DataContext", "Other"))).Success);
        Assert.Contains("Scenario", snapshot.Status);
    }

    [Fact]
    public async Task ViewFactoryComposesConstructorDependencyAndCompiledResourcesWithoutAppStartup()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var withoutFactory = await client.RenderAsync(Request(PreviewMode.Compiled, data: "Empty"));
        Assert.False(withoutFactory.Success); Assert.Contains("parameterless constructor", Errors(withoutFactory));
        var snapshot = await client.RenderAsync(Request(PreviewMode.Compiled, "CreateView"));
        Assert.True(snapshot.Success, Errors(snapshot));
        Assert.Equal("Constructor dependency", await TextAsync(client, snapshot, "ScenarioMessage"));
        Assert.Equal("2", await TextAsync(client, snapshot, "ScenarioCount"));
        Assert.Equal(DependencyView, snapshot.Build!.ViewTypeName);
        Assert.Equal(DependencyView, snapshot.Scenario!.ViewTypeName);
        Assert.All(snapshot.Nodes, node => Assert.Null(node.Source));
        var message = Assert.Single(snapshot.Nodes, node => node.Name == "ScenarioMessage");
        var inspection = await client.InspectAsync(new(snapshot.Version, message.Id));
        Assert.Contains(inspection.Properties, property => property.Name == "Foreground" && property.Value == "#FF008080" && property.ValueSource == "Style");

        var overridden = await client.RenderAsync(Request(PreviewMode.Compiled, "CreateView", "Populated", "Populated"));
        Assert.True(overridden.Success, Errors(overridden));
        Assert.Equal("3 orders", await TextAsync(client, overridden, "ScenarioMessage"));
        Assert.Equal("3", await TextAsync(client, overridden, "ScenarioCount"));
    }

    [Fact]
    public async Task NullDataIsAnExplicitNoDataScenarioAndDoesNotFallBackToAuthoredContext()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(Request(PreviewMode.Source, data: "NullData"));
        Assert.True(snapshot.Success, Errors(snapshot));
        var root = Assert.Single(snapshot.Nodes, node => node.Name == "Root");
        var inspection = await client.InspectAsync(new(snapshot.Version, root.Id));
        var context = Assert.Single(inspection.Properties, property => property.Name == "DataContext");
        Assert.Equal("(null)", context.Value); Assert.Equal("Scenario", context.ValueSource); Assert.Null(context.DataContextType);
        Assert.Equal("", await TextAsync(client, snapshot, "ScenarioMessage"));
    }

    [Fact]
    public async Task DataFactoryOverridesTwoWayRootBindingWithoutWritingBackToItsSource()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(Request(PreviewMode.Compiled, "CreateTwoWayView", "TwoWayData") with
        { ViewTypeName = "WpfStudio.PreviewFixture.ScenarioTwoWayView" });
        Assert.True(snapshot.Success, Errors(snapshot));
        Assert.Equal("Scenario overrides binding", await TextAsync(client, snapshot, "ScenarioMessage"));
        Assert.Equal("0", await TextAsync(client, snapshot, "SourceWrites"));
    }

    [Fact]
    public async Task ScenarioContextTakesPrecedenceOverDesignContextWhileDesignMemberOverridesRemainExplicit()
    {
        const string xaml = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='d' x:Name='Root' d:DataContext='Design context'><TextBlock x:Name='ScenarioMessage' Text='{Binding Title}' d:Text='Design title'/><TextBlock x:Name='LocalContext' DataContext='Local child' Text='{Binding}'/></Grid>";
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var request = Request(PreviewMode.Source, data: "Populated") with { Text = xaml };
        var snapshot = await client.RenderAsync(request);
        Assert.True(snapshot.Success, Errors(snapshot));
        Assert.Equal("Design title", await TextAsync(client, snapshot, "ScenarioMessage"));
        var root = Assert.Single(snapshot.Nodes, node => node.Name == "Root");
        var rootInspection = await client.InspectAsync(new(snapshot.Version, root.Id));
        Assert.Contains(rootInspection.Properties, property => property.Name == "DataContext" && property.ValueSource == "Scenario" && !property.CanWriteSource);
        var message = Assert.Single(snapshot.Nodes, node => node.Name == "ScenarioMessage");
        var inspection = await client.InspectAsync(new(snapshot.Version, message.Id));
        Assert.Contains(inspection.Properties, property => property.Name == "Text" && property.ValueSource == "Local (design baseline)" && !property.CanWriteSource);
        Assert.Contains(inspection.Properties, property => property.Name == "DataContext" && property.ValueSource == "Scenario" && !property.CanWriteSource);
        var localNode = Assert.Single(snapshot.Nodes, node => node.Name == "LocalContext");
        var localInspection = await client.InspectAsync(new(snapshot.Version, localNode.Id));
        Assert.Contains(localInspection.Properties, property => property.Name == "DataContext" && property.ValueSource == "Local" && property.CanWriteSource);
        var edited = await client.SetPropertyAsync(new(snapshot.Version, message.Id, "Text", "Temporary title"));
        Assert.True(edited.Success, edited.Error);
        Assert.Contains(edited.Inspection.Properties, property => property.Name == "Text" && property.IsOverridden && property.ValueSource == "Preview override (design baseline)" && !property.CanWriteSource);
        var reset = await client.SetPropertyAsync(new(snapshot.Version, message.Id, "Text", null, Reset: true));
        Assert.True(reset.Success, reset.Error);
        Assert.Contains(reset.Inspection.Properties, property => property.Name == "Text" && property.Value == "Design title" && property.ValueSource == "Local (design baseline)");
        var runtime = await client.RenderAsync(request with { Version = 302, UseDesignTimeValues = false });
        Assert.True(runtime.Success, Errors(runtime)); Assert.Equal("3 orders", await TextAsync(client, runtime, "ScenarioMessage"));
    }

    [Theory]
    [InlineData(false, "Loaded replacement", "Local (design baseline)")]
    [InlineData(true, "Style replacement", "Style (design baseline)")]
    public async Task LoadedChangesReportTheCurrentValueSourceWithoutClaimingTheDesignValueStillApplies(bool clearToStyle, string value, string source)
    {
        string markup = "<fixture:DesignOverwriteControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:fixture='clr-namespace:WpfStudio.PreviewFixture' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='d' x:Name='Subject' d:Text='Design declaration' Tag='" + (clearToStyle ? "clear" : "replace") +
            "'><fixture:DesignOverwriteControl.Style><Style TargetType='{x:Type fixture:DesignOverwriteControl}'><Setter Property='Text' Value='Style replacement'/></Style></fixture:DesignOverwriteControl.Style></fixture:DesignOverwriteControl>";
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(Request(PreviewMode.Source) with { Scenario = null, Text = markup });
        Assert.True(snapshot.Success, Errors(snapshot));
        var node = Assert.Single(snapshot.Nodes, item => item.Name == "Subject");
        var inspection = await client.InspectAsync(new(snapshot.Version, node.Id));
        Assert.Contains(inspection.Properties, property => property.Name == "Text" && property.Value == value && property.ValueSource == source && !property.CanWriteSource);
        Assert.False((await client.ValidatePropertyAsync(new(snapshot.Version, node.Id, "Text", "New literal"))).Success);
    }

    [Fact]
    public async Task EachScenarioAndReturnToOrdinarySourceUseFreshProcessesAndStaticState()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var first = await client.RenderAsync(Request(PreviewMode.Source, data: "InvocationCounter"));
        Assert.True(first.Success, Errors(first)); Assert.Equal("Invocation 1", await TextAsync(client, first, "ScenarioMessage"));
        using var firstProcess = Process.GetProcessById(client.ProcessId!.Value);
        var second = await client.RenderAsync(Request(PreviewMode.Source, data: "InvocationCounter") with { Version = 302 });
        Assert.True(second.Success, Errors(second)); Assert.Equal("Invocation 1", await TextAsync(client, second, "ScenarioMessage"));
        Assert.NotEqual(firstProcess.Id, client.ProcessId);
        await firstProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var secondProcess = Process.GetProcessById(client.ProcessId!.Value);
        var plain = await client.RenderAsync(Request(PreviewMode.Source) with { Scenario = null, Version = 303,
            Text = "<fixture:ScenarioCounterControl xmlns:fixture='clr-namespace:WpfStudio.PreviewFixture' Name='ScenarioMessage'/>" });
        Assert.True(plain.Success, Errors(plain)); Assert.Null(plain.Scenario); Assert.Null(plain.Build);
        Assert.Equal("Invocation 1", await TextAsync(client, plain, "ScenarioMessage"));
        Assert.NotEqual(secondProcess.Id, client.ProcessId);
        await secondProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null((await client.InspectAsync(new(first.Version, first.Nodes[0].Id))).Node);
    }

    [Theory]
    [InlineData("AsyncData", "synchronous")]
    [InlineData("DisguisedAsyncData", "asynchronous result")]
    [InlineData("GenericData", "non-generic")]
    [InlineData("ParameterData", "parameterless")]
    [InlineData("OverloadedData", "exactly one")]
    [InlineData("PrivateData", "public static")]
    [InlineData("VoidData", "returning a value")]
    [InlineData("MissingData", "exactly one")]
    [InlineData("WrongDispatcherData", "another dispatcher")]
    [InlineData("ThrowData", "Deliberate scenario failure")]
    public async Task InvalidDataFactoriesReturnSpecificDiagnostics(string method, string expected)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var result = await client.RenderAsync(Request(PreviewMode.Source, data: method));
        Assert.False(result.Success); Assert.Contains(expected, Errors(result));
    }

    [Theory]
    [InlineData("NullView", "returned null")]
    [InlineData("WrongView", "not assignable")]
    [InlineData("ParentedView", "already parented")]
    [InlineData("WrongDispatcherView", "another dispatcher")]
    public async Task InvalidViewResultsAreRejectedBeforeHosting(string method, string expected)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var result = await client.RenderAsync(Request(PreviewMode.Compiled, view: method));
        Assert.False(result.Success); Assert.Contains(expected, Errors(result));
    }

    [Fact]
    public async Task AllFactoryMetadataIsValidatedBeforeViewFactoryRunsAndRequestsAreBounded()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var invalid = await client.RenderAsync(Request(PreviewMode.Compiled, "ThrowView", "ParameterData"));
        Assert.False(invalid.Success); Assert.Contains("parameterless", Errors(invalid)); Assert.DoesNotContain("must not run", Errors(invalid));
        var instance = await client.RenderAsync(Request(PreviewMode.Source) with
        { Scenario = new("Instance", DataContextFactory: new("WpfStudio.PreviewFixture.InstanceScenarioFactories", "CreateData")) });
        Assert.False(instance.Success); Assert.Contains("public static", Errors(instance));
        var sourceView = await client.RenderAsync(Request(PreviewMode.Source, view: "CreateView"));
        Assert.False(sourceView.Success); Assert.Contains("requires Compiled", Errors(sourceView));
        var missingAssembly = await client.RenderAsync(Request(PreviewMode.Source, data: "Empty") with { AssemblyPath = null });
        Assert.False(missingAssembly.Success); Assert.Contains("built project assembly", Errors(missingAssembly));
        var oversized = await client.RenderAsync(Request(PreviewMode.Source, data: "Empty", name: new string('x', 129)));
        Assert.False(oversized.Success); Assert.Contains("at most 128", Errors(oversized));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HangingFactoryIsBoundedWithoutStoppingAnotherPreviewAndRefreshRecovers(bool cancel)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath, TimeSpan.FromSeconds(8));
        await using var other = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        Assert.True((await other.RenderAsync(new("C:/preview/Other.xaml", Source, 1, 400, 300))).Success);
        using var otherProcess = Process.GetProcessById(other.ProcessId!.Value);
        using var cancellation = new CancellationTokenSource();
        Task<PreviewSnapshot> pending = client.RenderAsync(Request(PreviewMode.Source, data: "HangingData"), cancellation.Token);
        string? marker = null;
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(6))
            {
                if (client.ProcessId is { } id)
                {
                    marker = Path.Combine(Path.GetTempPath(), $"WpfStudio.Scenario.Hang.{id}.marker");
                    if (File.Exists(marker)) break;
                }
                await Task.Delay(30);
            }
            Assert.True(marker is not null && File.Exists(marker), "The factory must be entered before testing cancellation or timeout.");
            using var process = Process.GetProcessById(client.ProcessId!.Value);
            if (cancel) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
            else await Assert.ThrowsAsync<TimeoutException>(() => pending);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(otherProcess.HasExited); Assert.Null(client.ProcessId);
            var recovered = await client.RenderAsync(Request(PreviewMode.Source) with { Scenario = null, Version = 302 });
            Assert.True(recovered.Success, Errors(recovered)); Assert.Null(recovered.Scenario);
        }
        finally { cancellation.Cancel(); if (marker is not null) File.Delete(marker); }
    }

    private static string Errors(PreviewSnapshot snapshot) => string.Join("\n", snapshot.Diagnostics.Select(diagnostic => diagnostic.Message));
    private static async Task<string> TextAsync(PreviewClient client, PreviewSnapshot snapshot, string name)
    {
        var node = Assert.Single(snapshot.Nodes, node => node.Name == name);
        var inspection = await client.InspectAsync(new(snapshot.Version, node.Id));
        return Assert.Single(inspection.Properties, property => property.Name == "Text").Value;
    }
}
