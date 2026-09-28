using WpfStudio.Contracts;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class DesignTimePreviewTests(PreviewFixture fixture)
{
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:design='http://schemas.microsoft.com/expression/blend/2008' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='design'";
    private const string Path = "C:/preview/DesignValues.xaml";
    private Task<PreviewSnapshot> Render(string markup, bool useDesignValues = true) => fixture.Engine.RenderAsync(
        new(Path, markup, 1, 400, 300, UseDesignTimeValues: useDesignValues), default);
    private async Task<PreviewInspection> Inspect(PreviewSnapshot snapshot, string name)
    {
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var node = Assert.Single(snapshot.Nodes, node => node.Name == name);
        return await fixture.Engine.InspectAsync(new(snapshot.Version, node.Id), default);
    }
    private static PreviewProperty Property(PreviewInspection inspection, string name) => Assert.Single(inspection.Properties, property => property.Name == name);

    [Fact]
    public async Task LiteralOverridesAreNamespaceAwareAndNeverWriteBackAsRuntimeValues()
    {
        string markup = $"<StackPanel {Namespaces}><TextBox Name='Source' Text='Runtime'/><TextBlock Name='Subject' Text='{{Binding Text, ElementName=Source}}' design:Text='Design &amp; sample' design:FontSize='23' design:Margin='1,2,3,4'/></StackPanel>";
        var snapshot = await Render(markup);
        var inspection = await Inspect(snapshot, "Subject");
        var text = Property(inspection, "Text");
        Assert.Equal("Design & sample", text.Value);
        Assert.Equal("Local (design baseline)", text.ValueSource);
        Assert.False(text.CanWriteSource);
        Assert.Null(text.BindingPath);
        Assert.Equal("23", Property(inspection, "FontSize").Value);
        Assert.Equal("Local (design baseline)", Property(inspection, "Margin").ValueSource);
        Assert.False((await fixture.Engine.ValidatePropertyAsync(new(snapshot.Version, inspection.Node!.Id, "Text", "Runtime edit"), default)).Success);
        Assert.Equal(markup.IndexOf("<TextBlock", StringComparison.Ordinal), inspection.Node!.Source!.Start);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("replaced binding is not evaluated", StringComparison.Ordinal));

        var runtime = await Inspect(await Render(markup, false), "Subject");
        Assert.Equal("Runtime", Property(runtime, "Text").Value);
        Assert.Equal("Text", Property(runtime, "Text").BindingPath);
        Assert.True(Property(runtime, "Text").CanWriteSource);
        Assert.NotEqual("Local (design baseline)", Property(runtime, "Text").ValueSource);
    }

    [Fact]
    public async Task MerelySpellingAPrefixDDoesNotMakeItsValuesDesignTime()
    {
        string markup = $"<TextBlock {Namespaces.Replace("mc:Ignorable='design'", "xmlns:d='urn:unrelated' mc:Ignorable='design d'", StringComparison.Ordinal)} Name='Subject' Text='Runtime' d:Text='Not design' design:Tag='Design tag'/>";
        var inspection = await Inspect(await Render(markup), "Subject");
        Assert.Equal("Runtime", Property(inspection, "Text").Value);
        Assert.Equal("Design tag", Property(inspection, "Tag").Value);
        Assert.NotEqual("Local (design baseline)", Property(inspection, "Text").ValueSource);
    }

    [Theory]
    [InlineData("Old text")]
    [InlineData("<Run Name='OldRun' FontWeight='Bold'>Old text</Run>")]
    [InlineData("<TextBlock.Inlines><Run Name='OldRun'>Old text</Run></TextBlock.Inlines>")]
    public async Task DesignTextReplacesInlineContentWithoutAppendingOrDiscardingResources(string content)
    {
        string markup = $"<TextBlock {Namespaces} Name='Subject' design:Text='Replacement'><TextBlock.Resources><SolidColorBrush x:Key='Keep' Color='Red'/></TextBlock.Resources>{content}</TextBlock>";
        var snapshot = await Render(markup);
        var inspection = await Inspect(snapshot, "Subject");
        Assert.Equal("Replacement", Property(inspection, "Text").Value);
        Assert.Equal("Local (design baseline)", Property(inspection, "Text").ValueSource);
        Assert.DoesNotContain(snapshot.Nodes, node => node.Name == "OldRun");
        var runtime = await Render(markup, false);
        Assert.True(runtime.Success, string.Join("\n", runtime.Diagnostics.Select(diagnostic => diagnostic.Message)));
        if (content.Contains("OldRun", StringComparison.Ordinal)) Assert.Contains(runtime.Nodes, node => node.Name == "OldRun");
    }

    [Theory]
    [InlineData("Old caption")]
    [InlineData("<TextBlock Name='OldContent' Text='Old caption'/>")]
    [InlineData("<Button.Content><TextBlock Name='OldContent' Text='Old caption'/></Button.Content>")]
    public async Task DesignContentPropertyElementReplacesTheActualContentDeclaration(string content)
    {
        string markup = $"<Button {Namespaces} Name='Subject'>{content}<design:Button.Content><TextBlock Name='SampleContent' Text='Sample caption'/></design:Button.Content></Button>";
        var snapshot = await Render(markup);
        Assert.DoesNotContain(snapshot.Nodes, node => node.Name == "OldContent");
        var contentInspection = await Inspect(snapshot, "SampleContent");
        Assert.Equal("Sample caption", Property(contentInspection, "Text").Value);
        Assert.Equal(markup.IndexOf("<TextBlock Name='SampleContent'", StringComparison.Ordinal), contentInspection.Node!.Source!.Start);
        var subject = await Inspect(snapshot, "Subject");
        Assert.Equal("Local (design baseline)", Property(subject, "Content").ValueSource);
        Assert.False(Property(subject, "Content").CanWriteSource);
        Assert.DoesNotContain((await Render(markup, false)).Nodes, node => node.Name == "SampleContent");
    }

    [Fact]
    public async Task ExplicitDesignDataContextObjectFlowsToBindingsWithInheritedProvenance()
    {
        string markup = $"<Grid {Namespaces} Name='Root'><Grid.DataContext><TextBox Text='Runtime context'/></Grid.DataContext><design:Grid.DataContext><TextBox Text='Sample context'/></design:Grid.DataContext><TextBlock Name='Subject' Text='{{Binding Text}}'/></Grid>";
        var snapshot = await Render(markup);
        var subject = await Inspect(snapshot, "Subject");
        Assert.Equal("Sample context", Property(subject, "Text").Value);
        Assert.Equal("Inherited (design baseline)", Property(subject, "DataContext").ValueSource);
        Assert.False(Property(subject, "DataContext").CanWriteSource);
        Assert.Equal("Local (design baseline)", Property(await Inspect(snapshot, "Root"), "DataContext").ValueSource);
        Assert.Equal("Runtime context", Property(await Inspect(await Render(markup, false), "Subject"), "Text").Value);
    }

    [Fact]
    public async Task DesignItemsSourceArrayRendersActualItems()
    {
        string markup = $"<ListBox {Namespaces} xmlns:sys='clr-namespace:System;assembly=mscorlib' Name='Subject'><ListBox.Items><ListBoxItem Name='RuntimeItem'>Runtime item</ListBoxItem></ListBox.Items><design:ListBox.ItemsSource><x:Array Type='{{x:Type sys:String}}'><sys:String>Alpha</sys:String><sys:String>Beta</sys:String></x:Array></design:ListBox.ItemsSource></ListBox>";
        var snapshot = await Render(markup);
        var inspection = await Inspect(snapshot, "Subject");
        Assert.Equal("Local (design baseline)", Property(inspection, "ItemsSource").ValueSource);
        Assert.Equal(2, snapshot.Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));
        Assert.DoesNotContain(snapshot.Nodes, node => node.Name == "RuntimeItem");
        var without = await Render(markup, false);
        Assert.True(without.Success);
        Assert.Contains(without.Nodes, node => node.Name == "RuntimeItem");
    }

    [Theory]
    [InlineData("", "180")]
    [InlineData("Width='55'", "55")]
    [InlineData("Width='Auto'", "180")]
    [InlineData("Width='55' design:Width='90'", "90")]
    public async Task DesignDimensionsAreHintsUnlessExplicitlyOverridden(string width, string expected)
    {
        string markup = $"<Grid {Namespaces} Name='Subject' {width} design:DesignWidth='180' design:DesignHeight='75'/>";
        var inspection = await Inspect(await Render(markup), "Subject");
        Assert.Equal(expected, Property(inspection, "Width").Value);
        Assert.Equal("75", Property(inspection, "Height").Value);
        Assert.Equal("Local (design baseline)", Property(inspection, "Height").ValueSource);
    }

    [Theory]
    [InlineData("{design:DesignInstance Type={x:Type sample:MustNotConstruct}, IsDesignTimeCreatable=True}")]
    [InlineData("{design:DesignInstance sample:MustNotConstruct, CreateList=True}")]
    [InlineData("{design:DesignData Source=MissingSample.xaml}")]
    public async Task UnsupportedDesignExtensionsAreReportedWithoutConstructingOrLoadingSamples(string designData)
    {
        MustNotConstruct.ConstructorCalls = 0;
        string markup = $"<Grid {Namespaces} xmlns:sample='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' design:DataContext='{designData}'><TextBlock Text='Runtime'/></Grid>";
        var snapshot = await Render(markup);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(0, MustNotConstruct.ConstructorCalls);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Warning" && diagnostic.Message.Contains("DesignInstance", StringComparison.Ordinal) && diagnostic.Line == 1);
    }

    [Fact]
    public async Task InvalidAndConflictingValuesRetainTheAuthoredValue()
    {
        string markup = $"<TextBlock {Namespaces} Name='Subject' Text='Runtime' Width='45' design:Width='-1' design:Text='Attribute'><design:TextBlock.Text>Element</design:TextBlock.Text></TextBlock>";
        var snapshot = await Render(markup);
        var inspection = await Inspect(snapshot, "Subject");
        Assert.Equal("Runtime", Property(inspection, "Text").Value);
        Assert.Equal("45", Property(inspection, "Width").Value);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Multiple design declarations", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("rejects this value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EscapedMarkupLiteralAndTemplateInstancesKeepTheirSourceAndDesignOrigin()
    {
        string markup = $"<Button {Namespaces}><Button.Template><ControlTemplate TargetType='Button'><TextBlock Name='Subject' Text='Runtime' design:Text='{{}}{{literal}}'/></ControlTemplate></Button.Template></Button>";
        var snapshot = await Render(markup);
        var inspection = await Inspect(snapshot, "Subject");
        Assert.Equal("{literal}", Property(inspection, "Text").Value);
        Assert.Equal("ParentTemplate (design baseline)", Property(inspection, "Text").ValueSource);
        Assert.Equal(markup.IndexOf("<TextBlock", StringComparison.Ordinal), inspection.Node!.Source!.Start);
    }

    [Fact]
    public async Task QualifiedCustomDesignPropertiesKeepTheirResolvedNamespace()
    {
        string markup = $"<sample:DesignSampleControl {Namespaces} xmlns:sample='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' Name='Subject' Sample='Runtime' design:DesignSampleControl.Sample='Design'/>";
        var inspection = await Inspect(await Render(markup), "Subject");
        Assert.Equal("Design", Property(inspection, "Sample").Value);
        Assert.Equal("Local (design baseline)", Property(inspection, "Sample").ValueSource);
        Assert.False(Property(inspection, "Sample").CanWriteSource);
        Assert.Equal("Runtime", Property(await Inspect(await Render(markup, false), "Subject"), "Sample").Value);
    }
}

public sealed class DesignSampleControl : System.Windows.Controls.ContentControl
{
    public static readonly System.Windows.DependencyProperty SampleProperty = System.Windows.DependencyProperty.Register(
        nameof(Sample), typeof(string), typeof(DesignSampleControl), new System.Windows.PropertyMetadata(""));
    public string Sample { get => (string)GetValue(SampleProperty); set => SetValue(SampleProperty, value); }
}

public sealed class MustNotConstruct
{
    public static int ConstructorCalls;
    public MustNotConstruct() { ConstructorCalls++; throw new InvalidOperationException("The default design shape must not invoke a constructor."); }
}
