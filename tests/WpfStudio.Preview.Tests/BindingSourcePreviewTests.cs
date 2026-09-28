using System.IO;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class BindingSourcePreviewTests
{
    private static readonly string AssemblyPath = Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll");
    private const string Header = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:c='clr-namespace:WpfStudio.PreviewFixture'";
    private static PreviewRequest Request(string text, long version = 901) => new("C:/project/BindingSources.xaml", text,
        version, 500, 500, AssemblyPath, AppContext.BaseDirectory, ApplicationResourcePath: null);

    [Fact]
    public async Task SourceMarkupObjectsCompositeChildrenAndQualifiedAttributesUseOriginalCoordinates()
    {
        string source = $$"""
            <Grid {{Header}} DataContext='{x:Static c:BindingSourceData.Instance}'>
              <Grid.Resources><Style x:Key='Shared' TargetType='TextBlock'><Setter Property='Text' Value='{Binding Primary}'/></Style></Grid.Resources>
              <StackPanel>
                <TextBlock x:Name='Inline' Text='{Binding Pri&#109;ary}' Tag='{Binding Primary}' c:BindingSourceOptions.Value='{Binding Secondary}'/>
                <TextBlock x:Name='Object'><TextBlock.Text><Binding Path='Primary'/></TextBlock.Text></TextBlock>
                <TextBlock x:Name='Styled' Style='{StaticResource Shared}'/><TextBlock x:Name='StyledAgain' Style='{StaticResource Shared}'/>
                <TextBlock x:Name='Multi'><TextBlock.Text><MultiBinding StringFormat='{}{0} {1}'><Binding Path='Primary'/><Binding Path='Secondary'/></MultiBinding></TextBlock.Text></TextBlock>
                <TextBlock x:Name='Priority'><TextBlock.Text><PriorityBinding><Binding Path='Primary'/><Binding Path='Secondary'/></PriorityBinding></TextBlock.Text></TextBlock>
              </StackPanel>
            </Grid>
            """;
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var render = await client.RenderAsync(Request(source));
        Assert.True(render.Success, Errors(render));
        var xml = XDocument.Parse(source, LoadOptions.SetLineInfo);
        var inline = await Property(client, render, "Inline", "Text");
        var tag = await Property(client, render, "Inline", "Tag");
        var attached = await Property(client, render, "Inline", "BindingSourceOptions.Value");
        var inlineElement = xml.Descendants().Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "Inline");
        AssertPosition(Assert.Single(inline.Property.BindingSources!.Declarations), inlineElement.Attribute("Text")!);
        AssertPosition(Assert.Single(tag.Property.BindingSources!.Declarations), inlineElement.Attribute("Tag")!);
        AssertPosition(Assert.Single(attached.Property.BindingSources!.Declarations), inlineElement.Attributes().Single(attribute => attribute.Name.LocalName == "BindingSourceOptions.Value"));
        Assert.Equal("Primary", Assert.Single(inline.Property.BindingSources.Declarations).Path);
        Assert.NotEqual(inline.Property.BindingSources.BindingId, tag.Property.BindingSources.BindingId);
        Assert.True((await client.GetBindingSourceAsync(ToRequest(render, inline))).Available);
        Assert.True((await client.GetBindingSourceAsync(ToRequest(render, attached))).Available);
        var objectBinding = await Property(client, render, "Object", "Text");
        AssertPosition(Assert.Single(objectBinding.Property.BindingSources!.Declarations), xml.Descendants().Single(element =>
            element.Name.LocalName == "Binding" && element.Parent?.Name.LocalName == "TextBlock.Text"));
        var style = await Property(client, render, "Styled", "Text");
        var shared = await Property(client, render, "StyledAgain", "Text");
        Assert.NotEqual(style.Property.BindingSources!.BindingId, shared.Property.BindingSources!.BindingId);
        Assert.Equal(Assert.Single(style.Property.BindingSources.Declarations).DeclarationId, Assert.Single(shared.Property.BindingSources.Declarations).DeclarationId);
        AssertPosition(Assert.Single(style.Property.BindingSources.Declarations), xml.Descendants().Single(element => element.Name.LocalName == "Setter").Attribute("Value")!);
        foreach (string name in new[] { "Multi", "Priority" })
        {
            var composite = await Property(client, render, name, "Text");
            var sources = composite.Property.BindingSources!;
            Assert.Equal(3, sources.Declarations.Count);
            var parent = Assert.Single(sources.Declarations, row => row.ParentExpressionId is null);
            Assert.Equal(name == "Multi" ? "MultiBinding" : "PriorityBinding", parent.Kind);
            foreach (var child in sources.Declarations.Where(row => row.ParentExpressionId is not null))
            {
                Assert.Equal(parent.ExpressionId, child.ParentExpressionId);
                Assert.Equal(child.ChildIndex == 0 ? "Primary" : "Secondary", child.Path);
                var response = await client.GetBindingSourceAsync(ToRequest(render, composite, child));
                Assert.True(response.Available, response.Status);
                Assert.Equal(child.ExpressionId, response.Declaration!.ExpressionId);
            }
        }
    }

    [Fact]
    public async Task RevalidationRejectsWrongIdentityReplacementRemovedTargetAndPreviousRender()
    {
        string source = $"<c:BindingSourceRemovalPanel {Header} x:Name='Panel' DataContext='{{x:Static c:BindingSourceData.Instance}}'><c:BindingSourceMutationControl x:Name='Target' Text='{{Binding Primary}}'/></c:BindingSourceRemovalPanel>";
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var render = await client.RenderAsync(Request(source));
        Assert.True(render.Success, Errors(render));
        var selected = await Property(client, render, "Target", "Text");
        var request = ToRequest(render, selected);
        Assert.True((await client.GetBindingSourceAsync(request)).Available);
        foreach (var invalid in new[] { request with { Revision = 900 }, request with { OwnerType = "Wrong.Type" },
            request with { DeclarationId = "missing" }, request with { ExpressionId = "missing" }, request with { BindingId = "missing" },
            request with { PropertyId = "foreign-live-token" } })
        {
            var response = await client.GetBindingSourceAsync(invalid);
            Assert.False(response.Available);
            Assert.Equal(invalid, response.Request);
        }
        var changed = await client.SetPropertyAsync(new(render.Version, selected.Node.Id, "Tag", "replace"));
        Assert.True(changed.Success, changed.Error);
        Assert.False((await client.GetBindingSourceAsync(request)).Available);
        var replacement = await Property(client, changed.Snapshot, "Target", "Text");
        var declaration = Assert.Single(replacement.Property.BindingSources!.Declarations);
        Assert.Equal("Primary", declaration.Path);
        Assert.Null(declaration.Source);
        Assert.NotEqual(request.BindingId, replacement.Property.BindingSources.BindingId);
        var panel = Assert.Single(render.Nodes, node => node.Name == "Panel");
        var removed = await client.SetPropertyAsync(new(render.Version, panel.Id, "Tag", "remove"));
        Assert.True(removed.Success, removed.Error);
        Assert.False((await client.GetBindingSourceAsync(ToRequest(render, replacement))).Available);
        var next = await client.RenderAsync(Request(source, 902));
        Assert.True(next.Success, Errors(next));
        Assert.False((await client.GetBindingSourceAsync(request)).Available);
    }

    [Fact]
    public async Task TemplateClonesDesignObjectsAndLoadedCodeBindingsNeverBorrowTargetOrigins()
    {
        string source = $$"""
            <StackPanel {{Header}} DataContext='{x:Static c:BindingSourceData.Instance}'>
              <TextBlock x:Name='Authored' Text='{Binding Primary}'/>
              <c:BindingSourceMutationControl x:Name='ReplacedOnLoad' Text='{Binding Primary}' Tag='replace-on-load'/>
              <Button><d:Button.Content><TextBlock x:Name='DesignOnly' Text='{Binding Primary}'/></d:Button.Content></Button>
              <Control><Control.Template><ControlTemplate><TextBlock x:Name='TemplateChild' Text='{Binding Primary}'/></ControlTemplate></Control.Template></Control>
              <ContentControl Content='{Binding}'><ContentControl.ContentTemplate><DataTemplate><TextBlock x:Name='DataTemplateChild' Text='{Binding Primary}'/></DataTemplate></ContentControl.ContentTemplate></ContentControl>
            </StackPanel>
            """;
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var render = await client.RenderAsync(Request(source));
        Assert.True(render.Success, Errors(render));
        Assert.True((await client.GetBindingSourceAsync(ToRequest(render, await Property(client, render, "Authored", "Text")))).Available);
        foreach (string name in new[] { "ReplacedOnLoad", "DesignOnly", "TemplateChild", "DataTemplateChild" })
        {
            var property = await Property(client, render, name, "Text");
            var declaration = Assert.Single(property.Property.BindingSources!.Declarations);
            Assert.Null(declaration.Source);
            Assert.False(string.IsNullOrEmpty(declaration.UnavailableReason));
            Assert.False((await client.GetBindingSourceAsync(ToRequest(render, property))).Available);
        }
    }

    [Fact]
    public async Task CompiledPreviewKeepsActualBuiltOriginsWithoutInventingCurrentBufferLocations()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var render = await client.RenderAsync(Request("<UnsavedUnrelated/>") with { Mode = PreviewMode.Compiled, ViewTypeName = "WpfStudio.PreviewFixture.BindingSourceView" });
        Assert.True(render.Success, Errors(render));
        var property = await Property(client, render, "CompiledBinding", "Text");
        var declaration = Assert.Single(property.Property.BindingSources!.Declarations);
        Assert.True(declaration.Source is not null, declaration.UnavailableReason ?? "The built binding declaration has no source hint.");
        var origin = Assert.IsType<InspectionSourceHint>(declaration.Source);
        Assert.Contains("bindingsourceview.xaml", origin.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Null(property.Node.Source);
        var response = await client.GetBindingSourceAsync(ToRequest(render, property));
        Assert.True(response.Available, response.Status);
        Assert.Equal(declaration.Source, response.Declaration!.Source);
    }

    private static async Task<(PreviewNode Node, PreviewProperty Property)> Property(PreviewClient client, PreviewSnapshot snapshot, string name, string property)
    {
        var node = Assert.Single(snapshot.Nodes, node => node.Name == name);
        var inspection = await client.InspectAsync(new(snapshot.Version, node.Id));
        var value = Assert.Single(inspection.Properties, value => value.Name == property);
        Assert.NotNull(value.BindingSources);
        return (node, value);
    }
    private static BindingSourceRequest ToRequest(PreviewSnapshot snapshot, (PreviewNode Node, PreviewProperty Property) selected, BindingSourceDeclaration? declaration = null)
    {
        var sources = selected.Property.BindingSources!;
        declaration ??= sources.Declarations.Single(row => row.ParentExpressionId is null);
        return new(snapshot.Version, selected.Node.Id, selected.Property.Name, sources.BindingId, declaration.ExpressionId,
            declaration.DeclarationId, selected.Property.OwnerType, selected.Property.OwnerAssembly);
    }
    private static void AssertPosition(BindingSourceDeclaration declaration, XObject source)
    {
        var info = (IXmlLineInfo)source;
        Assert.NotNull(declaration.Source);
        Assert.Equal(info.LineNumber, declaration.Source.Line);
        Assert.Equal(info.LinePosition, declaration.Source.Column);
    }
    private static string Errors(PreviewSnapshot snapshot) => string.Join("\n", snapshot.Diagnostics.Select(item => item.Message));
}
