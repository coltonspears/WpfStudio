using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlNamespaceIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("WpfStudio-Xmlns-").FullName;
    private readonly string _markup;

    public XamlNamespaceIndexTests() =>
        // Stands in for System.Xaml: only the attribute names and constructor shapes matter to the metadata reader.
        _markup = Emit("System.Xaml", """
            namespace System.Windows.Markup
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute { public string? AssemblyName { get; set; } }
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class XmlnsCompatibleWithAttribute(string oldNamespace, string newNamespace) : System.Attribute { }
            }
            """);

    [Fact]
    public void ReadsDeclarationsAndSkipsDesignTimeAndReferenceAssemblies()
    {
        var controls = XamlNamespaceIndex.ReadLibrary(Emit("Vendor.Controls", """
            [assembly: System.Windows.Markup.XmlnsDefinition("http://vendor/wpf", "Vendor")]
            [assembly: System.Windows.Markup.XmlnsCompatibleWith("http://vendor/2019", "http://vendor/wpf")]
            namespace Vendor { public class Gauge { } }
            """, _markup));
        Assert.NotNull(controls);
        Assert.Equal("Vendor.Controls", controls.Name);
        Assert.Equal(["http://vendor/wpf"], controls.Namespaces);
        Assert.Equal(new KeyValuePair<string, string>("http://vendor/2019", "http://vendor/wpf"), Assert.Single(controls.Compatible));

        // Visual Studio design-time parts often repeat the runtime namespace but cannot load outside the designer.
        var designer = Emit("Microsoft.VisualStudio.DesignTools.Extensibility", "namespace Designer { public class Metadata { } }");
        Assert.Null(XamlNamespaceIndex.ReadLibrary(Emit("Vendor.Controls.Design", """
            [assembly: System.Windows.Markup.XmlnsDefinition("http://vendor/wpf", "Vendor.Design")]
            namespace Vendor.Design { public class Registration : Designer.Metadata { } }
            """, _markup, designer)));
        Assert.Null(XamlNamespaceIndex.ReadLibrary(Emit("Vendor.Reference", """
            [assembly: System.Windows.Markup.XmlnsDefinition("http://vendor/wpf", "Vendor")]
            [assembly: System.Runtime.CompilerServices.ReferenceAssembly]
            """, _markup)));
        Assert.Null(XamlNamespaceIndex.ReadLibrary(designer));
    }

    [Fact]
    public void SelectsEveryLibraryForAUrlNamespaceAndReportsOnlyUsedUnknownNamespaces()
    {
        var libraries = new[]
        {
            new XamlNamespaceLibrary("/out/Vendor.Controls.dll", "Vendor.Controls", ["http://vendor/wpf"], [new("http://vendor/2019", "http://vendor/wpf")]),
            new XamlNamespaceLibrary("/out/Vendor.Charts.dll", "Vendor.Charts", ["http://vendor/wpf"], []),
            new XamlNamespaceLibrary("/hint/Vendor.Charts.dll", "Vendor.Charts", ["http://vendor/wpf"], []),
            new XamlNamespaceLibrary("/out/Other.dll", "Other", ["http://other"], [])
        };
        var use = XamlNamespaceIndex.Read("""
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                         xmlns:design="http://design/only" mc:Ignorable="d design"
                         xmlns:old="http://vendor/2019" xmlns:missing="http://missing/used" xmlns:unused="http://missing/unused"
                         xmlns:b="http://behaviors" xmlns:local="clr-namespace:App">
                <old:Gauge d:Width="3" design:Note="x" Tag="Note: pack://application:,,,/Theme.xaml 12:30" />
                <Button Style="{StaticResource {x:Type missing:Thing}}" Content="{b:Text}" />
            </UserControl>
            """);
        Assert.Equal(["http://behaviors", "http://missing/unused", "http://missing/used", "http://vendor/2019"], use.Declared.Order(StringComparer.Ordinal));
        Assert.Equal(["http://behaviors", "http://missing/used", "http://vendor/2019"], use.Used.Order(StringComparer.Ordinal));

        var selection = XamlNamespaceIndex.Select(use, libraries, defined: ["http://behaviors"]);
        // The retired namespace maps to the current one, and the first copy of each assembly wins.
        Assert.Equal(["/out/Vendor.Controls.dll", "/out/Vendor.Charts.dll"], selection.Libraries.Select(library => library.Path));
        Assert.Equal(["http://missing/used"], selection.Unresolved);
    }

    private string Emit(string name, string source, params string[] references)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(file => Path.GetFileName(file) is "System.Runtime.dll" or "System.Private.CoreLib.dll");
        var path = Path.Combine(_root, name + ".dll");
        var result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            platform.Concat(references).Select(file => MetadataReference.CreateFromFile(file)), new(OutputKind.DynamicallyLinkedLibrary)).Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
