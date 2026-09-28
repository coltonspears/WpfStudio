using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Diagnostics;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.PreviewHost;
using Xunit.Abstractions;

namespace WpfStudio.Preview.Tests;

// A source-loader probe. It records WPF's actual coordinates instead of treating
// binding-path equality or a target element's origin as a binding's origin.
[Collection("WPF preview")]
public sealed class PreviewBindingSourceProbeTests(PreviewFixture fixture, ITestOutputHelper output)
{
    private const string Source = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:WpfStudio.PreviewHost"
                x:Class="Probe.AuthoredWindow" Tag="{Binding Primary}">
          <Window.Resources>
            <ResourceDictionary>
              <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Colors.xaml"/></ResourceDictionary.MergedDictionaries>
              <Style x:Key="SharedStyle" TargetType="TextBlock">
                <Setter Property="Text" Value="{Binding Primary}"/>
              </Style>
            </ResourceDictionary>
          </Window.Resources>
          <StackPanel>
            <TextBlock x:Name="Inline" Text="{Binding Primary}" Tag="{Binding Primary}" Foreground="{StaticResource Accent}"/>
            <TextBlock x:Name="Entity" Text="{Binding Path=Pri&#109;ary}"/>
            <TextBlock x:Name="Multiline" Text="{Binding
                Path=Primary}"/>
            <TextBlock x:Name="Object"><TextBlock.Text><Binding Path="Primary"/></TextBlock.Text></TextBlock>
            <TextBlock x:Name="Styled" Style="{StaticResource SharedStyle}"/>
            <TextBlock x:Name="StyledAgain" Style="{StaticResource SharedStyle}"/>
            <Control x:Name="TemplateOwner">
              <Control.Template><ControlTemplate><TextBlock x:Name="TemplateChild" Text="{Binding Primary}"/></ControlTemplate></Control.Template>
            </Control>
            <ContentControl x:Name="DataTemplateOwner" Content="{Binding}">
              <ContentControl.ContentTemplate><DataTemplate><TextBlock x:Name="DataTemplateChild" Text="{Binding Primary}"/></DataTemplate></ContentControl.ContentTemplate>
            </ContentControl>
            <TextBlock x:Name="Multi"><TextBlock.Text><MultiBinding StringFormat="{}{0} {1}"><Binding Path="Primary"/><Binding Path="Secondary"/></MultiBinding></TextBlock.Text></TextBlock>
            <TextBlock x:Name="Priority"><TextBlock.Text><PriorityBinding><Binding Path="Primary"/><Binding Path="Secondary"/></PriorityBinding></TextBlock.Text></TextBlock>
          </StackPanel>
        </Window>
        """;

    [Fact]
    public void SyntheticBindingAfterAuthoredBindingCannotInheritItsSourcePosition() => fixture.OnDispatcher(() =>
    {
        const string source = "<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Name='Authored' Text='{Binding Primary}'/></StackPanel>";
        Type documentType = typeof(PreviewEngine).Assembly.GetType("WpfStudio.PreviewHost.PreviewDocument", throwOnError: true)!;
        object document = documentType.GetMethod("Parse")!.Invoke(null, [new PreviewRequest("C:/project/Synthetic.xaml", source, 1), null])!;
        var xml = (XDocument)documentType.GetField("_sourceDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document)!;
        xml.Root!.Add(new XElement(xml.Root.Name.Namespace + "TextBlock", new XAttribute("Name", "Generated"), new XAttribute("Text", "{Binding Primary}")));
        using var reader = (XmlReader)documentType.GetMethod("CreateSourceReader", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, null)!;
        var root = Assert.IsType<StackPanel>(XamlReader.Load(reader));
        var authored = BindingOperations.GetBindingExpressionBase(root.Children[0], TextBlock.TextProperty)!;
        var generated = BindingOperations.GetBindingExpressionBase(root.Children[1], TextBlock.TextProperty)!;
        Assert.NotNull(Hint(authored.ParentBindingBase));
        var hint = Hint(generated.ParentBindingBase);
        Assert.NotNull(hint);
        Assert.Equal(int.MaxValue, hint.Line);
        Assert.NotEqual(Hint(authored.ParentBindingBase), hint);
        var row = new BindingSourceDeclaration("expression", "declaration", null, null, "Binding", "Primary", null,
            generated.Status.ToString(), new InspectionSourceHint(hint.Uri!, hint.Line, hint.Column));
        var mapped = (BindingSourceDeclaration)documentType.GetMethod("MapBindingSource", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, [row])!;
        Assert.Null(mapped.Source);
        Assert.Contains("Generated", mapped.UnavailableReason, StringComparison.Ordinal);
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualPreviewTransformsExposeBindingObjectOriginsWithoutSourceGetterCalls(bool retainedReader)
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-binding-source-probe-");
        try
        {
            string path = Path.Combine(directory.FullName, "View.xaml");
            File.WriteAllText(Path.Combine(directory.FullName, "Colors.xaml"), """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <SolidColorBrush x:Key="Accent" Color="Coral"/>
                </ResourceDictionary>
                """);
            fixture.OnDispatcher(() =>
            {
                // This invokes the real internal transformation; no duplicated test
                // rewriter could accidentally make the candidate reader look safer.
                Type documentType = typeof(PreviewEngine).Assembly.GetType("WpfStudio.PreviewHost.PreviewDocument", throwOnError: true)!;
                object document = documentType.GetMethod("Parse")!.Invoke(null,
                    [new PreviewRequest(path, Source, 1, 600, 600), typeof(PreviewEngine).Assembly])!;
                string markup = (string)documentType.GetProperty("Markup")!.GetValue(document)!;
                FrameworkElement root;
                if (retainedReader)
                {
                    using var reader = (XmlReader)documentType.GetMethod("CreateSourceReader", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, null)!;
                    root = Assert.IsAssignableFrom<FrameworkElement>(XamlReader.Load(reader));
                }
                else
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markup));
                    root = Assert.IsAssignableFrom<FrameworkElement>(XamlReader.Load(stream, new ParserContext { BaseUri = new Uri(path) }));
                }
                var model = new ProbeModel();
                root.DataContext = model;
                root.Measure(new Size(600, 600)); root.Arrange(new Rect(0, 0, 600, 600)); root.UpdateLayout();
                var rows = new List<ProbeRow>();
                var identities = new ConditionalWeakTable<object, Identity>();
                int nextIdentity = 0;
                int before = model.GetterCalls;
                Read("Root", root, FrameworkElement.TagProperty);
                Read("Inline", Named("Inline"), TextBlock.TextProperty);
                Read("InlineSamePath", Named("Inline"), FrameworkElement.TagProperty);
                Read("Entity", Named("Entity"), TextBlock.TextProperty);
                Read("Multiline", Named("Multiline"), TextBlock.TextProperty);
                Read("Object", Named("Object"), TextBlock.TextProperty);
                Read("Style", Named("Styled"), TextBlock.TextProperty);
                Read("SharedStyle", Named("StyledAgain"), TextBlock.TextProperty);
                var templateOwner = Assert.IsType<Control>(Named("TemplateOwner"));
                Read("Template", Assert.IsType<TextBlock>(templateOwner.Template.FindName("TemplateChild", templateOwner)), TextBlock.TextProperty);
                Read("DataTemplate", Assert.IsType<TextBlock>(FindVisual(Named("DataTemplateOwner"), "DataTemplateChild")), TextBlock.TextProperty);
                Read("Multi", Named("Multi"), TextBlock.TextProperty);
                Read("Priority", Named("Priority"), TextBlock.TextProperty);
                Assert.Equal(before, model.GetterCalls);
                output.WriteLine(retainedReader ? "Retained XML reader" : "Production serialized stream");
                output.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                Dump("Original XAML", Source);
                Dump("Serialized transformed XAML", markup);
                Assert.Equal(16, rows.Count);
                Assert.All(rows.Where(row => row.Form is "Template" or "DataTemplate"), row => Assert.Null(row.BindingSource));
                var sourced = rows.Where(row => row.Form is not ("Template" or "DataTemplate")).ToArray();
                Assert.Equal(14, sourced.Length);
                Assert.All(sourced, row => Assert.NotNull(row.BindingSource));
                Assert.All(sourced, row => Assert.Equal(new Uri(path).AbsoluteUri, row.BindingSource!.Uri));
                if (retainedReader)
                {
                    var authored = XDocument.Parse(Source, LoadOptions.SetLineInfo);
                    var declaration = authored.Descendants().Single(element => element.Name.LocalName == "Binding"
                        && element.Parent?.Name.LocalName == "TextBlock.Text");
                    var lineInfo = (IXmlLineInfo)declaration;
                    var origin = rows.Single(row => row.Form == "Object").BindingSource!;
                    Assert.Equal(lineInfo.LineNumber, origin.Line);
                    Assert.Equal(lineInfo.LinePosition, origin.Column);
                }
                var style = rows.Single(row => row.Form == "Style");
                var sharedStyle = rows.Single(row => row.Form == "SharedStyle");
                Assert.Equal(style.BindingObjectId, sharedStyle.BindingObjectId);
                Assert.NotEqual(style.ExpressionId, sharedStyle.ExpressionId);
                Assert.Equal(Colors.Coral, Assert.IsType<SolidColorBrush>(Named("Inline").GetValue(TextBlock.ForegroundProperty)).Color);
                var original = rows.Single(row => row.Form == "Inline");
                BindingOperations.SetBinding(Named("Inline"), TextBlock.TextProperty, new Binding("Primary"));
                before = model.GetterCalls;
                rows.Clear(); Read("Replaced", Named("Inline"), TextBlock.TextProperty);
                Assert.Equal(before, model.GetterCalls);
                var replacement = Assert.Single(rows);
                Assert.Equal(original.Path, replacement.Path);
                Assert.NotEqual(original.ExpressionId, replacement.ExpressionId);
                Assert.Null(replacement.BindingSource);
                Assert.Null(replacement.ExpressionSource);
                root.DataContext = null;
                return true;

                FrameworkElement Named(string name) => Assert.IsAssignableFrom<FrameworkElement>(root.FindName(name));
                int Id(object value) => identities.GetValue(value, _ => new(++nextIdentity)).Id;
                void Read(string form, DependencyObject target, DependencyProperty property)
                {
                    var expression = BindingOperations.GetBindingExpressionBase(target, property);
                    Assert.NotNull(expression);
                    Walk(expression, null, null);
                    void Walk(BindingExpressionBase current, int? parent, int? index)
                    {
                        var binding = current.ParentBindingBase;
                        int id = Id(current);
                        rows.Add(new(form, id, Id(binding), parent, index, binding.GetType().Name, (binding as Binding)?.Path?.Path,
                            Hint(binding), Hint(current), Hint(target)));
                        var children = current is MultiBindingExpression multi ? multi.BindingExpressions
                            : current is PriorityBindingExpression priority ? priority.BindingExpressions : null;
                        if (children is not null)
                            for (int child = 0; child < children.Count; child++) Walk(children[child], id, child);
                    }
                }
            });
        }
        finally { directory.Delete(recursive: true); }
    }

    private void Dump(string title, string text)
    {
        output.WriteLine(title + ":");
        int line = 0;
        foreach (string row in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) output.WriteLine($"{++line}: {row}");
    }

    private static DependencyObject? FindVisual(DependencyObject target, string name)
    {
        if (target is FrameworkElement element && element.Name == name) return target;
        for (int child = 0; child < VisualTreeHelper.GetChildrenCount(target); child++)
            if (FindVisual(VisualTreeHelper.GetChild(target, child), name) is { } found) return found;
        return null;
    }

    private static SourceHint? Hint(object value)
    {
        var hint = VisualDiagnostics.GetXamlSourceInfo(value);
        return hint is null ? null : new(hint.SourceUri?.AbsoluteUri, hint.LineNumber, hint.LinePosition);
    }

    public sealed class ProbeModel
    {
        public int GetterCalls;
        public string Primary { get { GetterCalls++; return "Primary"; } }
        public string Secondary { get { GetterCalls++; return "Secondary"; } }
    }
    private sealed record Identity(int Id);
    private sealed record SourceHint(string? Uri, int Line, int Column);
    private sealed record ProbeRow(string Form, int ExpressionId, int BindingObjectId, int? ParentExpressionId,
        int? ChildIndex, string Kind, string? Path, SourceHint? BindingSource, SourceHint? ExpressionSource, SourceHint? TargetSource);
}
