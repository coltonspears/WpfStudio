using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlDiagnosticBudgetTests
{
    private static readonly CSharpCompilation Compilation = CSharpCompilation.Create("BudgetFixture",
        [CSharpSyntaxTree.ParseText("namespace BudgetFixture; public class Model { public string Name => \"value\"; } public class Widget { public string Text { get; set; } }")],
        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void BindingBudgetStopsAtExactPrefixWithoutChangingUnlimitedEditorAnalysis()
    {
        string text = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:vm='clr-namespace:BudgetFixture' "
            + "xmlns:d='http://schemas.microsoft.com/expression/blend/2008' d:DataContext='{d:DesignInstance vm:Model}'>"
            + string.Concat(Enumerable.Range(0, 100).Select(index => $"<TextBlock Text='{{Binding Missing{index}}}'/>")) + "</Grid>";
        var service = new XamlLanguageService();

        var bounded = service.AnalyzeBounded("View.xaml", text, 1, Compilation, 3, CancellationToken.None);
        var complete = service.Analyze("View.xaml", text, 1, Compilation);

        Assert.Equal(100, complete.Count);
        Assert.Equal(complete.Take(3), bounded);
        Assert.Equal(new[] { "Missing0", "Missing1", "Missing2" }, bounded.Select(issue => text.Substring(issue.Start, issue.Length)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaBudgetAppliesToAttributesAndElements(bool attributes)
    {
        string text = attributes
            ? "<Widget xmlns='clr-namespace:BudgetFixture' " + string.Join(" ", Enumerable.Range(0, 100).Select(index => $"Unknown{index}='value'")) + "/>"
            : "<Widget xmlns='clr-namespace:BudgetFixture'>" + string.Concat(Enumerable.Range(0, 100).Select(index => $"<Unknown{index}/>")) + "</Widget>";
        var service = new XamlSchemaService();

        var bounded = service.AnalyzeBounded("View.xaml", text, 1, Compilation, 3, CancellationToken.None);
        var complete = service.Analyze("View.xaml", text, 1, Compilation);

        Assert.Equal(100, complete.Count);
        Assert.Equal(complete.Take(3), bounded);
        Assert.Equal(new[] { "Unknown0", "Unknown1", "Unknown2" }, bounded.Select(issue => text.Substring(issue.Start, issue.Length)));
    }
}
