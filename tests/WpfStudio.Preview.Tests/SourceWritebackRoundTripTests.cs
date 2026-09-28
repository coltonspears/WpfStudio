using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

// These tests cross the actual process boundary and reload the edited XAML. Lexically
// valid XML is insufficient: WPF must produce the requested effective property value.
[Collection("WPF preview")]
public sealed class SourceWritebackRoundTripTests(PreviewFixture fixture)
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string KeepElement = "<Border Name='Keep' Width='37' Height='12' Background='Coral'/>";
    private const string TextResources = "<TextBlock.Resources><SolidColorBrush x:Key='KeepBrush' Color='CornflowerBlue'/></TextBlock.Resources>";

    [Theory]
    [InlineData("Original", false)]
    [InlineData("<Run FontWeight='Bold' Text='Original'/>", true)]
    [InlineData("<TextBlock.Inlines><Run FontStyle='Italic' Text='Original'/></TextBlock.Inlines>", true)]
    public async Task TextBlockTextReplacesImplicitOrExplicitInlinesWithoutAppendingOldContent(string content, bool replacesObject)
    {
        string source = Surround($"<TextBlock Name='Subject' Tag='Keep attribute' FontSize='19'>{TextResources}{content}</TextBlock>");
        var result = await RoundTripAsync(source, "Text", "Replacement");

        // WPF keeps the Text DP at its default when authored Run content is
        // loaded before formatting. Inspect the actual rich content separately.
        Assert.Equal(replacesObject ? "" : "Original", result.Before.Value);
        if (replacesObject)
        {
            var run = Assert.Single(result.BeforeInlines);
            Assert.Equal("Original", Assert.Single(run.Properties, property => property.Name == "Text").Value);
            Assert.False(result.Before.CanEdit);
            if (content.Contains("FontWeight", StringComparison.Ordinal))
                Assert.Equal("Bold", Assert.Single(run.Properties, property => property.Name == "FontWeight").Value);
            else
                Assert.Equal("Italic", Assert.Single(run.Properties, property => property.Name == "FontStyle").Value);
        }
        Assert.Equal("Replacement", result.After.Value);
        Assert.Equal(replacesObject, result.Proposal.ReplacesObjectValue);
        Assert.Contains(TextResources, result.Text);
        Assert.Contains("FontSize='19'", result.Text);
        Assert.DoesNotContain("Original", result.Text);
        Assert.DoesNotContain("TextBlock.Inlines", result.Text);
        Assert.DoesNotContain(result.Snapshot.Nodes, node => node.Type == "System.Windows.Documents.Run" && node.Source != null);
    }

    [Theory]
    [InlineData("Original", false)]
    [InlineData("<TextBlock Name='OldContent' Text='Original'/>", true)]
    [InlineData("<Button.Content><TextBlock Name='OldContent' Text='Original'/></Button.Content>", true)]
    public async Task ButtonContentReplacesTextAndObjectsWithoutLosingOtherDeclarations(string content, bool replacesObject)
    {
        const string resources = "<Button.Resources><SolidColorBrush x:Key='KeepBrush' Color='CornflowerBlue'/></Button.Resources>";
        string source = Surround($"<Button Name='Subject' Tag='Keep attribute' Padding='8'>{resources}{content}</Button>");
        var result = await RoundTripAsync(source, "Content", "New caption");

        Assert.Equal("New caption", result.After.Value);
        Assert.Equal("New caption", result.After.EditableValue);
        Assert.Equal(replacesObject, result.Proposal.ReplacesObjectValue);
        Assert.Contains(resources, result.Text);
        Assert.Contains("Padding='8'", result.Text);
        Assert.DoesNotContain("Original", result.Text);
        Assert.DoesNotContain(result.Snapshot.Nodes, node => node.Name == "OldContent");
    }

    [Theory]
    [InlineData("{Binding Name}")]
    [InlineData("{}literal")]
    [InlineData("First\r\nSecond\nThird\t& <tag> \"double\" 'single'")]
    public async Task LiteralEscapesReloadAsTextWithNativeWpfNewlineSemantics(string literal)
    {
        string source = Surround($"<TextBlock Name='Subject' Tag='Keep attribute' Text='Original'>{TextResources}</TextBlock>");
        var result = await RoundTripAsync(source, "Text", literal);

        // WPF's own stream loader folds CRLF to LF, including character refs.
        // The source edit still preserves every supplied character explicitly.
        string runtimeLiteral = literal.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(runtimeLiteral, result.After.Value);
        Assert.Equal(runtimeLiteral, result.After.EditableValue);
        Assert.Null(result.After.BindingStatus);
        Assert.Contains(TextResources, result.Text);
        if (literal.StartsWith('{')) Assert.Contains("Text='{}" + literal + "'", result.Text);
        if (literal.Contains('\n'))
        {
            Assert.Contains("&#xD;&#xA;", result.Text);
            Assert.Contains("&#x9;", result.Text);
            Assert.Contains("&amp;", result.Text);
            Assert.Contains("&lt;tag&gt;", result.Text);
            Assert.Contains("&apos;single&apos;", result.Text);
        }
    }

    [Theory]
    [InlineData("Text='First&#xD;&#xA;Second'", "", "First\nSecond", null)]
    [InlineData("", "<Run FontWeight='Bold' Text='Original'/>", "", "Original")]
    [InlineData("", "<TextBlock.Inlines><Run FontStyle='Italic' Text='Original'/></TextBlock.Inlines>", "", "Original")]
    public void NativeWpfTextValuesStayStableAcrossLogicalTraversalLayoutAndPropertyInspection(
        string attributes, string content, string expectedText, string? expectedRunText)
    {
        fixture.OnDispatcher(() =>
        {
            // Use the same stream overload as the host, with no source transform
            // or inspector involved. This establishes the native WPF baseline.
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"<TextBlock xmlns='{Presentation}' {attributes}>{content}</TextBlock>"));
            var text = (TextBlock)XamlReader.Load(stream, new ParserContext());
            Assert.Equal(expectedText, text.Text);

            var runs = LogicalTreeHelper.GetChildren(text).OfType<Run>().ToArray();
            if (expectedRunText is null) Assert.Empty(runs);
            else Assert.Equal(expectedRunText, Assert.Single(runs).Text);
            Assert.Equal(expectedText, text.Text);

            text.Measure(new Size(400, 300));
            text.Arrange(new Rect(0, 0, 400, 300));
            text.UpdateLayout();
            Assert.Equal(expectedText, text.Text);

            // Mirror dependency-property discovery/reads used by the inspector.
            var properties = new HashSet<DependencyProperty>();
            for (Type? type = text.GetType(); type is not null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (field.FieldType == typeof(DependencyProperty) && field.GetValue(null) is DependencyProperty property)
                        properties.Add(property);
            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(text))
                if (DependencyPropertyDescriptor.FromProperty(descriptor)?.DependencyProperty is { } property)
                    properties.Add(property);
            foreach (var property in properties)
            {
                _ = DependencyPropertyHelper.GetValueSource(text, property);
                _ = BindingOperations.GetBindingExpressionBase(text, property);
                _ = text.GetValue(property);
            }
            Assert.Equal(expectedText, text.Text);
            if (expectedRunText is not null) Assert.Equal(expectedRunText, Assert.Single(runs).Text);
            return true;
        });
    }

    [Fact]
    public async Task AttachedPropertyKeepsItsAuthoredPresentationNamespaceAlias()
    {
        string source = Surround("<TextBlock Name='Subject' Tag='Keep attribute' layout:Grid.Row='1' Text='Original'/>",
            namespaces: $"xmlns:layout='{Presentation}'");
        var result = await RoundTripAsync(source, "Grid.Row", "2");

        Assert.True(result.Before.IsAttached);
        Assert.Equal("System.Windows.Controls.Grid", result.Before.OwnerType);
        Assert.Equal("2", result.After.Value);
        Assert.Contains("layout:Grid.Row='2'", result.Text);
        Assert.DoesNotContain(" Grid.Row=", result.Text);
    }

    [Theory]
    [InlineData("first", "WpfStudio.Preview.Tests.First.Options", "3", "7")]
    [InlineData("second", "WpfStudio.Preview.Tests.Second.Options", "Before", "After & more")]
    public async Task CustomAttachedAliasesUseTheCanonicalOwnerDespiteIdenticalDisplayNames(string alias, string owner, string original, string literal)
    {
        const string namespaces = "xmlns:first='clr-namespace:WpfStudio.Preview.Tests.First;assembly=WpfStudio.Preview.Tests' xmlns:second='clr-namespace:WpfStudio.Preview.Tests.Second;assembly=WpfStudio.Preview.Tests'";
        string source = Surround($"<TextBlock Name='Subject' Tag='Keep attribute' {alias}:Options.Mode='{original}' Text='Original'/>", namespaces: namespaces);
        var result = await RoundTripAsync(source, "Options.Mode", literal, owner: owner, projectAssembly: true);

        Assert.Equal(owner, result.Before.OwnerType);
        Assert.Equal(owner, result.After.OwnerType);
        Assert.Equal(literal, result.After.Value);
        Assert.Contains($" {alias}:Options.Mode=", result.Text);
        Assert.DoesNotContain($" {(alias == "first" ? "second" : "first")}:Options.Mode=", result.Text);
        Assert.Contains(namespaces, result.Text);
    }

    [Fact]
    public async Task RemovingLocalAttributeRestoresTheStyleAfterReload()
    {
        const string resources = "<StackPanel.Resources><Style TargetType='Button'><Setter Property='Width' Value='42'/></Style></StackPanel.Resources>";
        string source = Surround("<Button Name='Subject' Tag='Keep attribute' Width='123'>Original caption</Button>", resources);
        var result = await RoundTripAsync(source, "Width", null, clear: true);

        Assert.Equal("123", result.Before.Value);
        Assert.Equal("Local", result.Before.ValueSource);
        Assert.Equal("42", result.After.Value);
        Assert.Equal("Style", result.After.ValueSource);
        Assert.Contains(resources, result.Text);
        Assert.DoesNotContain("Width='123'", result.Text);
        Assert.Equal("Original caption", Assert.Single(result.Inspection.Properties, property => property.Name == "Content").Value);
    }

    private static string Surround(string subject, string resources = "", string namespaces = "") =>
        $"<StackPanel xmlns='{Presentation}' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' {namespaces}>\n{resources}\n{subject}\n{KeepElement}\n</StackPanel>";

    private static async Task<RoundTrip> RoundTripAsync(string source, string propertyName, string? literal,
        bool clear = false, string? owner = null, bool projectAssembly = false)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var request = new PreviewRequest("C:/preview/Writeback.xaml", source, 1, 400, 300,
            AssemblyPath: projectAssembly ? typeof(SourceWritebackRoundTripTests).Assembly.Location : null,
            ProjectDirectory: projectAssembly ? AppContext.BaseDirectory : null);
        var snapshot = await client.RenderAsync(request);
        Assert.True(snapshot.Success, Diagnostics(snapshot));
        var node = Assert.Single(snapshot.Nodes, item => item.Name == "Subject");
        Assert.NotNull(node.Source);
        var inspection = await client.InspectAsync(new(snapshot.Version, node.Id));
        var beforeInlines = new List<PreviewInspection>();
        foreach (var inline in snapshot.Nodes.Where(item => item.Type == "System.Windows.Documents.Run" && item.Source != null))
            beforeInlines.Add(await client.InspectAsync(new(snapshot.Version, inline.Id)));
        var property = Assert.Single(inspection.Properties, item => item.Name == propertyName && (owner == null || item.OwnerType == owner));
        Assert.True(property.CanWriteSource, $"{property.Name} on {node.Type} should support source editing.");
        if (!clear)
        {
            var validation = await client.ValidatePropertyAsync(new(snapshot.Version, node.Id, property.Name, literal,
                OwnerType: property.OwnerType, OwnerAssembly: property.OwnerAssembly));
            Assert.True(validation.Success, validation.Error);
        }

        var proposal = XamlPropertyEditService.CreateEdit(new(request.Path, source, request.Version, node.Source!,
            request.Version, DocumentStore.Hash(Encoding.UTF8.GetBytes(source)), property.Name, literal,
            ClearLocalValue: clear, ReplaceExistingValue: true, OwnerType: property.OwnerType,
            OwnerAssembly: property.OwnerAssembly, IsAttached: property.IsAttached,
            SourceAssembly: projectAssembly ? typeof(SourceWritebackRoundTripTests).Assembly.GetName().Name : null,
            ContentProperty: property.ContentProperty));
        Assert.True(proposal.Success, proposal.Error ?? proposal.Explanation);
        Assert.NotEmpty(proposal.Edit!.Edits);
        string after = WorkspaceEditTransaction.ApplyTextEdits(source, proposal.Edit.Edits);
        Assert.Contains(KeepElement, after);
        Assert.Contains("Tag='Keep attribute'", after);

        // Validation/source preparation must not mutate the existing runtime tree.
        var untouched = await client.InspectAsync(new(snapshot.Version, node.Id));
        Assert.Equal(property.Value, Assert.Single(untouched.Properties, item =>
            item.Name == property.Name && item.OwnerType == property.OwnerType && item.OwnerAssembly == property.OwnerAssembly).Value);

        var reloaded = await client.RenderAsync(request with { Text = after, Version = 2 });
        Assert.True(reloaded.Success, Diagnostics(reloaded));
        var updatedNode = Assert.Single(reloaded.Nodes, item => item.Name == "Subject");
        var updatedInspection = await client.InspectAsync(new(reloaded.Version, updatedNode.Id));
        var updatedProperty = Assert.Single(updatedInspection.Properties, item =>
            item.Name == property.Name && item.OwnerType == property.OwnerType && item.OwnerAssembly == property.OwnerAssembly);
        Assert.Equal("Keep attribute", Assert.Single(updatedInspection.Properties, item => item.Name == "Tag").Value);
        Assert.Equal(37d, Assert.Single(reloaded.Nodes, item => item.Name == "Keep").Bounds!.Width);
        return new RoundTrip(property, updatedProperty, after, proposal, reloaded, updatedInspection, beforeInlines);
    }

    private static string Diagnostics(PreviewSnapshot snapshot) => string.Join("\n", snapshot.Diagnostics.Select(item => item.Message));
    private sealed record RoundTrip(PreviewProperty Before, PreviewProperty After, string Text,
        XamlPropertyEditResult Proposal, PreviewSnapshot Snapshot, PreviewInspection Inspection,
        IReadOnlyList<PreviewInspection> BeforeInlines);
}
