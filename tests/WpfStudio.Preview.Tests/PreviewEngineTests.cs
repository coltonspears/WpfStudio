using System.Windows;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.PreviewHost;

namespace WpfStudio.Preview.Tests;

[CollectionDefinition("WPF preview", DisableParallelization = true)]
public sealed class PreviewCollection : ICollectionFixture<PreviewFixture>;

[Collection("WPF preview")]
public sealed class PreviewEngineTests(PreviewFixture fixture)
{
    private const string Namespace = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private PreviewEngine Engine => fixture.Engine;
    private static PreviewRequest Request(string text, long version = 1) => new("C:/preview/View.xaml", text, version, 400, 300);

    [Fact]
    public async Task RendersWindowContentToPngWithNamedSourceAndVisualTree()
    {
        string text = $"<Window {Namespace} x:Class='Test.View' Title='Window'>\n<Grid><Button x:Name='SaveButton' Width='120' Height='40' Click='Save_Click'>Save</Button></Grid></Window>";
        var snapshot = await Engine.RenderAsync(Request(text), default);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, snapshot.PngBytes![..8]);
        Assert.Equal(400, snapshot.PixelWidth);
        Assert.Equal(300, snapshot.PixelHeight);
        var button = Assert.Single(snapshot.Nodes, n => n.Name == "SaveButton");
        Assert.Equal(120, button.Bounds!.Width);
        Assert.Equal(40, button.Bounds.Height);
        Assert.NotNull(button.ParentId);
        Assert.NotNull(button.LogicalParentId);
        Assert.Equal(2, button.Source!.Line);
        Assert.Equal(text.IndexOf("<Button", StringComparison.Ordinal), button.Source.Start);
        Assert.Contains(snapshot.Diagnostics, d => d.Message.Contains("code-behind", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics, d => d.Message.Contains("Save_Click", StringComparison.Ordinal));
        Assert.Contains(snapshot.Nodes, n => n.Source is not null && n.Type.EndsWith("Grid", StringComparison.Ordinal));
        Assert.Contains(snapshot.Nodes, n => n.Source is null && n.Type.EndsWith("ContentPresenter", StringComparison.Ordinal));
        var picked = await Engine.PickAsync(new(snapshot.Version, button.Bounds.X + 2, button.Bounds.Y + 2), default);
        Assert.NotNull(picked.Node);
        Assert.True(picked.Node!.Bounds!.Width > 0);
    }

    [Fact]
    public async Task InspectsEffectiveStyleValuesAndResetsOverrides()
    {
        var snapshot = await Engine.RenderAsync(Request($"<Grid {Namespace}><Grid.Resources><Style TargetType='Button'><Setter Property='Width' Value='120'/></Style></Grid.Resources><Button x:Name='Subject'>Save</Button></Grid>"), default);
        Assert.True(snapshot.Success);
        var button = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        var inspection = await Engine.InspectAsync(new(snapshot.Version, button.Id), default);
        var width = Assert.Single(inspection.Properties, p => p.Name == "Width");
        Assert.Equal("Style", width.ValueSource);
        Assert.True(width.CanEdit);
        var edit = await Engine.SetPropertyAsync(new(snapshot.Version, button.Id, "Width", "180"), default);
        Assert.True(edit.Success, edit.Error);
        var edited = Assert.Single(edit.Inspection.Properties, p => p.Name == "Width");
        Assert.Equal("180", edited.Value);
        Assert.True(edited.IsOverridden);
        Assert.Equal("Local", edited.ValueSource);
        var reset = await Engine.SetPropertyAsync(new(snapshot.Version, button.Id, "Width", null, Reset: true), default);
        Assert.True(reset.Success, reset.Error);
        Assert.Equal("Style", Assert.Single(reset.Inspection.Properties, p => p.Name == "Width").ValueSource);
        Assert.Equal("120", Assert.Single(reset.Inspection.Properties, p => p.Name == "Width").Value);
    }

    [Fact]
    public async Task ResetRestoresStyleBindingWithoutCreatingALocalBinding()
    {
        var snapshot = await Engine.RenderAsync(Request($"<StackPanel {Namespace}><StackPanel.Resources><Style x:Key='BoundText' TargetType='TextBlock'><Setter Property='Text' Value='{{Binding Text, ElementName=Source}}'/></Style></StackPanel.Resources><TextBox x:Name='Source' Text='Original'/><TextBlock x:Name='Subject' Style='{{StaticResource BoundText}}'/></StackPanel>"), default);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var subject = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        var initial = await Engine.InspectAsync(new(snapshot.Version, subject.Id), default);
        Assert.Equal("Style", Assert.Single(initial.Properties, p => p.Name == "Text").ValueSource);
        Assert.Equal("Original", Assert.Single(initial.Properties, p => p.Name == "Text").Value);

        var changed = await Engine.SetPropertyAsync(new(snapshot.Version, subject.Id, "Text", "Temporary"), default);
        Assert.True(changed.Success, changed.Error);
        Assert.Equal("Local", Assert.Single(changed.Inspection.Properties, p => p.Name == "Text").ValueSource);

        var reset = await Engine.SetPropertyAsync(new(snapshot.Version, subject.Id, "Text", null, Reset: true), default);
        Assert.True(reset.Success, reset.Error);
        var restored = Assert.Single(reset.Inspection.Properties, p => p.Name == "Text");
        Assert.Equal("Style", restored.ValueSource);
        Assert.Equal("Text", restored.BindingPath);
        Assert.Equal("Original", restored.Value);
        Assert.False(restored.IsOverridden);

        var source = Assert.Single(snapshot.Nodes, n => n.Name == "Source");
        Assert.True((await Engine.SetPropertyAsync(new(snapshot.Version, source.Id, "Text", "Updated"), default)).Success);
        var updated = await Engine.InspectAsync(new(snapshot.Version, subject.Id), default);
        Assert.Equal("Updated", Assert.Single(updated.Properties, p => p.Name == "Text").Value);
        Assert.Equal("Style", Assert.Single(updated.Properties, p => p.Name == "Text").ValueSource);
    }

    [Fact]
    public async Task ResetRestoresBindingAndDiagnosticsIdentifyFailedMember()
    {
        var snapshot = await Engine.RenderAsync(Request($"<StackPanel {Namespace}><TextBox x:Name='Source' Text='Original'/><TextBlock x:Name='Bound' Text='{{Binding Text, ElementName=Source}}'/><TextBlock x:Name='Broken' Text='{{Binding Mistyped, ElementName=Source}}'/></StackPanel>"), default);
        Assert.True(snapshot.Success);
        var bound = Assert.Single(snapshot.Nodes, n => n.Name == "Bound");
        var edit = await Engine.SetPropertyAsync(new(snapshot.Version, bound.Id, "Text", "Override"), default);
        Assert.True(edit.Success, edit.Error);
        Assert.Equal("Override", Assert.Single(edit.Inspection.Properties, p => p.Name == "Text").Value);
        var reset = await Engine.SetPropertyAsync(new(snapshot.Version, bound.Id, "Text", null, Reset: true), default);
        Assert.True(reset.Success, reset.Error);
        var restored = Assert.Single(reset.Inspection.Properties, p => p.Name == "Text");
        Assert.Equal("Text", restored.BindingPath);
        Assert.Equal("Original", restored.Value);
        var broken = Assert.Single(snapshot.Nodes, n => n.Name == "Broken");
        var failure = await Engine.InspectAsync(new(snapshot.Version, broken.Id), default);
        Assert.Contains(failure.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
        var repaired = await Engine.SetPropertyAsync(new(snapshot.Version, broken.Id, "Text", "Preview override"), default);
        Assert.True(repaired.Success);
        Assert.DoesNotContain(repaired.Snapshot.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
        var failedAgain = await Engine.SetPropertyAsync(new(snapshot.Version, broken.Id, "Text", null, Reset: true), default);
        Assert.Contains(failedAgain.Snapshot.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnnamedTemplateInstancesNavigateToTheAuthoredTemplateElement()
    {
        string text = $"<StackPanel {Namespace}>\r<StackPanel.Resources><DataTemplate x:Key='Item'><TextBlock Text='value &gt; zero'/></DataTemplate></StackPanel.Resources>\r<ContentControl Content='One' ContentTemplate='{{StaticResource Item}}'/>\r<ContentControl Content='Two' ContentTemplate='{{StaticResource Item}}'/></StackPanel>";
        var snapshot = await Engine.RenderAsync(Request(text), default);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var instances = snapshot.Nodes.Where(n => n.Type == typeof(System.Windows.Controls.TextBlock).FullName).ToArray();
        Assert.Equal(2, instances.Length);
        foreach (var instance in instances)
        {
            Assert.NotNull(instance.Source);
            Assert.Equal(2, instance.Source.Line);
            Assert.Equal(text.IndexOf("<TextBlock", StringComparison.Ordinal), instance.Source.Start);
            Assert.Equal("<TextBlock Text='value &gt; zero'/>", text.Substring(instance.Source.Start, instance.Source.Length));
            var inspection = await Engine.InspectAsync(new(snapshot.Version, instance.Id), default);
            Assert.DoesNotContain(inspection.Properties, p => p.Name.Contains("PreviewSource", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task InvalidMarkupDoesNotPoisonFollowingPreviewAndStaleIdsAreRejected()
    {
        var first = await Engine.RenderAsync(Request($"<Grid {Namespace}/>", 10), default);
        var oldId = first.Nodes[0].Id;
        var invalid = await Engine.RenderAsync(Request("<Grid>", 11), default);
        Assert.False(invalid.Success);
        Assert.Null(invalid.PngBytes);
        Assert.Contains(invalid.Diagnostics, d => d.Line is > 0);
        var recovered = await Engine.RenderAsync(Request($"<Border {Namespace} Background='Red'/>", 12), default);
        Assert.True(recovered.Success);
        Assert.Null((await Engine.InspectAsync(new(10, oldId), default)).Node);
        var edit = await Engine.SetPropertyAsync(new(10, oldId, "Width", "100"), default);
        Assert.False(edit.Success);
        Assert.Equal(12, edit.Snapshot.Version);
    }

    [Fact]
    public async Task ResourceDictionariesLoadAndInvalidDimensionsAreRejected()
    {
        var dictionary = await Engine.RenderAsync(Request($"<ResourceDictionary {Namespace}><SolidColorBrush x:Key='Accent' Color='Coral'/></ResourceDictionary>"), default);
        Assert.True(dictionary.Success);
        Assert.Contains("Resources loaded", dictionary.Status);
        var invalid = await Engine.RenderAsync(Request($"<Grid {Namespace}/>") with { Width = double.NaN }, default);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("dimensions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsDtdAndOutOfProjectAssemblies()
    {
        var dtd = await Engine.RenderAsync(Request("<!DOCTYPE Grid [<!ENTITY x 'bad'>]><Grid/>"), default);
        Assert.False(dtd.Success);
        var assembly = await Engine.RenderAsync(Request($"<Grid {Namespace}/>") with { AssemblyPath = typeof(PreviewEngine).Assembly.Location, ProjectDirectory = "C:/unrelated-project" }, default);
        Assert.False(assembly.Success);
        Assert.Contains(assembly.Diagnostics, d => d.Message.Contains("inside the selected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PropertyMetadataPreservesAttachedIdentityContentPropertyAndUntruncatedLiterals()
    {
        string longText = new('a', 3000);
        var snapshot = await Engine.RenderAsync(Request($"<Grid {Namespace}><TextBlock x:Name='Subject' Grid.Row='2' Text='{longText}'/></Grid>"), default);
        Assert.True(snapshot.Success);
        var subject = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        var inspection = await Engine.InspectAsync(new(snapshot.Version, subject.Id), default);
        var row = Assert.Single(inspection.Properties, p => p.Name == "Grid.Row");
        Assert.True(row.IsAttached);
        Assert.Equal("System.Windows.Controls.Grid", row.OwnerType);
        Assert.Equal("PresentationFramework", row.OwnerAssembly);
        Assert.Equal("2", row.EditableValue);
        Assert.True(row.CanWriteSource);
        var text = Assert.Single(inspection.Properties, p => p.Name == "Text");
        Assert.False(text.IsAttached);
        Assert.Equal("Inlines", text.ContentProperty);
        Assert.Equal(longText, text.EditableValue);
        Assert.True(text.Value.Length < text.EditableValue!.Length);
        Assert.True(text.CanWriteSource);
        Assert.Contains(inspection.Properties, p => p.Name == "DataContext" && p.EditableValue is null && p.CanWriteSource);
    }

    [Fact]
    public async Task SourceValidationDoesNotMutateOrRemoveDynamicResources()
    {
        var snapshot = await Engine.RenderAsync(Request($"<Grid {Namespace}><Grid.Resources><SolidColorBrush x:Key='Accent' Color='Coral'/></Grid.Resources><Button x:Name='Subject' Background='{{DynamicResource Accent}}' Width='120'/></Grid>"), default);
        Assert.True(snapshot.Success);
        var subject = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        var before = await Engine.InspectAsync(new(snapshot.Version, subject.Id), default);
        var background = Assert.Single(before.Properties, p => p.Name == "Background");
        Assert.False(background.CanEdit);
        Assert.True(background.CanWriteSource);
        Assert.Equal("#FFFF7F50", background.EditableValue);
        Assert.True((await Engine.ValidatePropertyAsync(new(snapshot.Version, subject.Id, "Background", "Blue"), default)).Success);
        Assert.False((await Engine.ValidatePropertyAsync(new(snapshot.Version, subject.Id, "Width", "-12"), default)).Success);
        Assert.False((await Engine.ValidatePropertyAsync(new(snapshot.Version, subject.Id, "Background", "NotAColor"), default)).Success);
        var after = await Engine.InspectAsync(new(snapshot.Version, subject.Id), default);
        Assert.Equal(background, Assert.Single(after.Properties, p => p.Name == "Background"));
        Assert.Equal("120", Assert.Single(after.Properties, p => p.Name == "Width").Value);
    }

    [Fact]
    public async Task AttachedPropertyDisplayNameCollisionsRequireAndHonorExactOwnerIdentity()
    {
        var snapshot = await Engine.RenderAsync(Request($"<Grid {Namespace} xmlns:first='clr-namespace:WpfStudio.Preview.Tests.First;assembly=WpfStudio.Preview.Tests' xmlns:second='clr-namespace:WpfStudio.Preview.Tests.Second;assembly=WpfStudio.Preview.Tests' first:Options.Mode='12' second:Options.Mode='initial'/>") , default);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var node = snapshot.Nodes[0];
        var before = await Engine.InspectAsync(new(snapshot.Version, node.Id), default);
        var duplicates = before.Properties.Where(p => p.Name == "Options.Mode").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.False((await Engine.ValidatePropertyAsync(new(snapshot.Version, node.Id, "Options.Mode", "13"), default)).Success);
        var ambiguous = await Engine.SetPropertyAsync(new(snapshot.Version, node.Id, "Options.Mode", "13"), default);
        Assert.False(ambiguous.Success);
        Assert.Contains("ambiguous", ambiguous.Error);

        var textProperty = Assert.Single(duplicates, p => p.OwnerType == "WpfStudio.Preview.Tests.Second.Options");
        var numberProperty = Assert.Single(duplicates, p => p.OwnerType == "WpfStudio.Preview.Tests.First.Options");
        var textEdit = new PreviewPropertyEdit(snapshot.Version, node.Id, "Options.Mode", "changed", OwnerType: textProperty.OwnerType, OwnerAssembly: textProperty.OwnerAssembly);
        Assert.True((await Engine.ValidatePropertyAsync(textEdit, default)).Success);
        Assert.False((await Engine.ValidatePropertyAsync(textEdit with { OwnerType = numberProperty.OwnerType }, default)).Success);
        var edited = await Engine.SetPropertyAsync(textEdit, default);
        Assert.True(edited.Success, edited.Error);
        Assert.Contains(edited.Inspection.Properties, p => p.OwnerType == textProperty.OwnerType && p.Name == "Options.Mode" && p.Value == "changed");
        Assert.Contains(edited.Inspection.Properties, p => p.OwnerType == numberProperty.OwnerType && p.Name == "Options.Mode" && p.Value == "12");
        var reset = await Engine.SetPropertyAsync(textEdit with { Reset = true, Value = null }, default);
        Assert.True(reset.Success, reset.Error);
        Assert.Contains(reset.Inspection.Properties, p => p.OwnerType == textProperty.OwnerType && p.Name == "Options.Mode" && p.Value == "initial");
    }

    [Fact]
    public async Task NullAndComplexBrushValuesCanBeReplacedAndObjectContentCanValidateOverrideAndReset()
    {
        var snapshot = await Engine.RenderAsync(Request($"<StackPanel {Namespace}><Button x:Name='Subject' Background='{{x:Null}}'>Original content</Button><Border x:Name='Gradient'><Border.Background><LinearGradientBrush><GradientStop Color='Red' Offset='0'/><GradientStop Color='Blue' Offset='1'/></LinearGradientBrush></Border.Background></Border></StackPanel>"), default);
        Assert.True(snapshot.Success);
        var button = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        var inspection = await Engine.InspectAsync(new(snapshot.Version, button.Id), default);
        var background = Assert.Single(inspection.Properties, p => p.Name == "Background");
        Assert.Null(background.EditableValue);
        Assert.True(background.CanWriteSource);
        Assert.True((await Engine.ValidatePropertyAsync(new(snapshot.Version, button.Id, "Background", "Coral"), default)).Success);
        var content = Assert.Single(inspection.Properties, p => p.Name == "Content");
        Assert.Equal("Original content", content.EditableValue);
        Assert.Equal("Content", content.ContentProperty);
        Assert.True(content.CanWriteSource);
        Assert.True((await Engine.ValidatePropertyAsync(new(snapshot.Version, button.Id, "Content", "New content"), default)).Success);
        Assert.Contains((await Engine.InspectAsync(new(snapshot.Version, button.Id), default)).Properties, p => p.Name == "Content" && p.Value == "Original content");
        var edited = await Engine.SetPropertyAsync(new(snapshot.Version, button.Id, "Content", "New content"), default);
        Assert.True(edited.Success, edited.Error);
        Assert.Contains(edited.Inspection.Properties, p => p.Name == "Content" && p.Value == "New content");
        var reset = await Engine.SetPropertyAsync(new(snapshot.Version, button.Id, "Content", null, Reset: true), default);
        Assert.True(reset.Success, reset.Error);
        Assert.Contains(reset.Inspection.Properties, p => p.Name == "Content" && p.Value == "Original content");

        var gradient = Assert.Single(snapshot.Nodes, n => n.Name == "Gradient");
        var gradientInspection = await Engine.InspectAsync(new(snapshot.Version, gradient.Id), default);
        Assert.Contains(gradientInspection.Properties, p => p.Name == "Background" && p.EditableValue is null && p.CanWriteSource);
    }

    [Fact]
    public async Task TemporaryTextEditsCannotDestroyRichInlineContent()
    {
        var snapshot = await Engine.RenderAsync(Request($"<TextBlock {Namespace}><Run x:Name='StyledRun' FontWeight='Bold'>Keep this run</Run></TextBlock>"), default);
        Assert.True(snapshot.Success);
        var root = snapshot.Nodes[0];
        var before = await Engine.InspectAsync(new(snapshot.Version, root.Id), default);
        Assert.Contains(before.Properties, p => p.Name == "Text" && !p.CanEdit);
        var result = await Engine.SetPropertyAsync(new(snapshot.Version, root.Id, "Text", "Replacement"), default);
        Assert.False(result.Success);
        var run = Assert.Single(result.Snapshot.Nodes, n => n.Name == "StyledRun");
        var runInspection = await Engine.InspectAsync(new(snapshot.Version, run.Id), default);
        Assert.Contains(runInspection.Properties, p => p.Name == "Text" && p.Value == "Keep this run");
        Assert.Contains(runInspection.Properties, p => p.Name == "FontWeight" && p.Value == "Bold");
    }

    [Fact]
    public async Task TransformedPageOnlyAllowsPropertiesThatExistOnTheAuthoredPage()
    {
        var snapshot = await Engine.RenderAsync(Request($"<Page {Namespace}><Grid/></Page>"), default);
        Assert.True(snapshot.Success);
        var properties = (await Engine.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id), default)).Properties;
        Assert.Contains(properties, p => p.Name == "Width" && p.CanWriteSource);
        Assert.Contains(properties, p => p.Name == "Background" && p.CanWriteSource);
        Assert.Contains(properties, p => p.Name == "IsTabStop" && !p.CanWriteSource);
        Assert.Contains(properties, p => p.Name == "Padding" && !p.CanWriteSource);
        Assert.False((await Engine.ValidatePropertyAsync(new(snapshot.Version, snapshot.Nodes[0].Id, "IsTabStop", "False"), default)).Success);
    }
}

public sealed class PreviewFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    public PreviewEngine Engine { get; }
    public T OnDispatcher<T>(Func<T> action) => _dispatcher.Invoke(action);

    public PreviewFixture()
    {
        // Source diagnostics must be opted in before WPF initializes its weak
        // object-to-source table, just as the isolated preview client does.
        Environment.SetEnvironmentVariable("ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO", "1");
        var started = new TaskCompletionSource<(PreviewEngine, Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var engine = new PreviewEngine(app.Dispatcher);
                started.SetResult((engine, app.Dispatcher));
                app.Run();
            }
            catch (Exception exception) { started.TrySetException(exception); }
        }) { IsBackground = true, Name = "WPF preview test dispatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        (Engine, _dispatcher) = started.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dispatcher.Invoke(() => { Engine.Dispose(); Application.Current.Shutdown(); });
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}
