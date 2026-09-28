using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlEventServiceTests
{
    private static readonly Lazy<MetadataReference[]> References = new(CreateReferences);
    private static readonly Lazy<CSharpCompilation> Fixture = new(() => Compilation());
    private static readonly XamlEventService Service = new();
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:lang='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:c='clr-namespace:EventFixture'";
    private static string Xaml(string body) => $"<Window {Ns} lang:Class='EventFixture.View'>{body}</Window>";

    [Theory]
    [InlineData("<Button Click='PrivateClick'/>")]
    [InlineData("<Button Click='ProtectedClick'/>")]
    [InlineData("<Button Click='GenericClick'/>")]
    [InlineData("<Grid Button.Click='PrivateClick'/>")]
    [InlineData("<Grid Mouse.MouseEnter='MouseEnter'/>")]
    [InlineData("<Grid c:EventProvider.Completed='PrivateClick'/>")]
    [InlineData("<c:Probe Covariant='Covariant'/>")]
    [InlineData("<c:Probe RefEvent='RefHandler'/>")]
    [InlineData("<Button Click='  PrivateClick  '/>")]
    [InlineData("<Button Click='event'/>")]
    [InlineData("<Window.Resources><Style TargetType='Button'><EventSetter Event='Click' Handler='PrivateClick'/></Style></Window.Resources>")]
    [InlineData("<Window.Resources><Style TargetType='Button'><EventSetter Event='Button.Click' Handler='PrivateClick'/></Style></Window.Resources>")]
    public void UsesCompilerDelegateConversionAndActualWpfEventProviders(string body)
    {
        var result = Service.AnalyzeDetailed("View.xaml", Xaml(body), 1, Fixture.Value);
        Assert.True(result.IsComplete, result.Status);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("Click", "Missing", "XAMLEVENT001")]
    [InlineData("Click", "StaticClick", "XAMLEVENT002")]
    [InlineData("Click", "BasePrivate", "XAMLEVENT002")]
    [InlineData("Click", "WrongClick", "XAMLEVENT002")]
    [InlineData("Click", "this.PrivateClick", "XAMLEVENT003")]
    [InlineData("Click", "Other.PrivateClick", "XAMLEVENT003")]
    public void ReportsOnlyDeterminateHandlerErrorsAtRawValue(string evt, string handler, string id)
    {
        var text = Xaml($"<Button Tag='A&amp;B 😀' {evt}='  {handler}  '/>");
        var issue = Assert.Single(Service.Analyze("View.xaml", text, 1, Fixture.Value));
        Assert.Equal(id, issue.Id);
        Assert.Equal("Error", issue.Severity);
        Assert.Equal(handler, text.Substring(issue.Start, issue.Length));
    }

    [Fact]
    public void AmbiguousOverloadIsDistinctFromMissingAndIncompatible()
    {
        var issue = Assert.Single(Service.Analyze("View.xaml", Xaml("<c:Probe Ambiguous='Ambiguous'/>"), 1, Fixture.Value));
        Assert.Equal("XAMLEVENT004", issue.Id);
    }

    [Fact]
    public void RefParameterMismatchUsesCompilerRules()
    {
        var issue = Assert.Single(Service.Analyze("View.xaml", Xaml("<c:Probe RefEvent='OutHandler'/>"), 1, Fixture.Value));
        Assert.Equal("XAMLEVENT002", issue.Id);
    }

    [Fact]
    public void HoverAndNavigationSelectOriginalSourceOverload()
    {
        var text = Xaml("<Button Click='Overload'/>");
        int position = text.IndexOf("Overload", StringComparison.Ordinal);
        var location = Assert.Single(Service.GetDefinition("View.xaml", text, position, Fixture.Value));
        var tree = Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == location.Path);
        Assert.Equal("Overload", tree.GetText().ToString().Substring(location.Start, location.Length));
        Assert.Contains("RoutedEventArgs", Service.GetHover("View.xaml", text, position, Fixture.Value)!.Text);
        Assert.Equal(tree.GetText().ToString().IndexOf("Overload(object sender, RoutedEventArgs", StringComparison.Ordinal), location.Start);
    }

    [Theory]
    [InlineData("Pri$$vateClick", "PrivateClick")]
    [InlineData("Pri$$vate&#67;lick", "PrivateClick")]
    [InlineData("$$", "PrivateClick")]
    public void CompletionFiltersByPrefixAndReplacesWholeRawHandler(string marked, string expected)
    {
        string markedText = Xaml($"<Button Click='{marked}'/>");
        int position = markedText.IndexOf("$$", StringComparison.Ordinal);
        string text = markedText.Remove(position, 2);
        var result = Service.Complete(text, position, 7, Fixture.Value);
        Assert.NotNull(result);
        Assert.Equal(marked.Replace("$$", ""), text.Substring(result.Start, result.Length));
        Assert.Contains(result.Items, item => item.InsertText == expected);
        Assert.DoesNotContain(result.Items, item => item.InsertText is "StaticClick" or "BasePrivate" or "WrongClick");
    }

    [Fact]
    public void EmptyValueTargetIncludesWhitespaceForGeneration()
    {
        var text = Xaml("<Button Click=' &#32; '/>");
        var target = Service.GetTarget(text, text.IndexOf("Click", StringComparison.Ordinal), Fixture.Value)!;
        Assert.Equal("", target.HandlerName);
        Assert.Equal(" &#32; ", text.Substring(target.Start, target.Length));
    }

    [Theory]
    [InlineData("<Button Click='{DynamicResource Handler}'/>")]
    [InlineData("<c:Unknown Click='Missing'/>")]
    [InlineData("<Grid xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' xmlns:i='clr-namespace:Ignored' mc:Ignorable='i'><i:Button Click='Missing'/></Grid>")]
    public void UnknownDynamicAndIgnoredContextsDoNotGuess(string body)
        => Assert.Empty(Service.Analyze("View.xaml", Xaml(body), 1, Fixture.Value));

    [Fact]
    public void InlineCodeAndSubclassExplicitlyReportUnavailableCoverage()
    {
        foreach (string text in new[] { Xaml("<lang:Code><![CDATA[void Missing() {}]]></lang:Code><Button Click='Missing'/>") , Xaml("<Button Click='Missing'/>").Replace("lang:Class=", "lang:Subclass='Derived' lang:Class=") })
        {
            var result = Service.AnalyzeDetailed("View.xaml", text, 1, Fixture.Value);
            Assert.Empty(result.Diagnostics);
            Assert.False(result.IsComplete);
            Assert.NotNull(result.Status);
        }
    }

    [Fact]
    public void BrokenAuthoredPartialDoesNotProduceMissingErrorsFromCleanGeneratedPartial()
    {
        var compilation = Compilation("public partial class View { private void NewClick( }");
        var result = Service.AnalyzeDetailed("View.xaml", Xaml("<Button Click='NewClick'/><Button Click='Missing'/>"), 1, compilation);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void FileLocalExtensionMethodsAreNotRootClassHandlers()
    {
        var compilation = Compilation("public static class Extensions { public static void ExtensionClick(this View view, object sender, RoutedEventArgs args) {} }");
        var result = Service.AnalyzeDetailed("View.xaml", Xaml("<Button Click='ExtensionClick'/>"), 1, compilation);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.IsComplete);
        var text = Xaml("<Button Click='ExtensionClick'/>");
        Assert.Null(Service.GetHover("View.xaml", text, text.IndexOf("ExtensionClick", StringComparison.Ordinal), compilation));
    }

    [Theory]
    [InlineData("private void Pending(object sender, System.Collections.Generic.List<MissingType> args) {}", "<Button Click='Pending'/>")]
    [InlineData("private void Pending(object sender, MissingType[] args) {}", "<Button Click='Pending'/>")]
    [InlineData("public delegate void PendingDelegate(object sender, System.Collections.Generic.List<MissingType[]> args); public event PendingDelegate PendingEvent;", "<c:View PendingEvent='Absent'/>")]
    public void NestedUnresolvedSignatureTypesRemainUnknown(string declaration, string body)
    {
        var compilation = Compilation("public partial class View { " + declaration + " }");
        var result = Service.AnalyzeDetailed("View.xaml", Xaml(body), 1, compilation);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.IsComplete);
        Assert.NotNull(result.Status);
    }

    [Fact]
    public void AttachedEventsDoNotBecomePropertyElements()
    {
        var schema = new XamlSchemaService();
        var text = Xaml("<Grid><c:EventProvider.Completed /></Grid>");
        Assert.Contains(schema.Analyze("View.xaml", text, 1, Fixture.Value), issue => issue.Id == "XAMLSCHEMA002");
        var prefix = Xaml("<Grid><c:EventProvider.Com$$ /></Grid>");
        int position = prefix.IndexOf("$$", StringComparison.Ordinal);
        var completion = schema.Complete(prefix.Remove(position, 2), position, 1, Fixture.Value);
        Assert.NotNull(completion);
        Assert.DoesNotContain(completion.Items, item => item.InsertText == "c:EventProvider.Completed");
    }

    [Fact]
    public void AttachedAdderWithoutRegisteredRoutedEventDoesNotClaimAHandlerTarget()
    {
        var text = Xaml("<Grid c:EventProvider.NotRegistered='PrivateClick'/>");
        int position = text.IndexOf("PrivateClick", StringComparison.Ordinal);
        Assert.Null(Service.GetTarget(text, position, Fixture.Value));
        Assert.Contains(new XamlSchemaService().Analyze("View.xaml", text, 1, Fixture.Value), issue => issue.Id == "XAMLSCHEMA002");
    }

    [Fact]
    public void ProbeBudgetBoundsManyValidHandlersAndReportsIncompleteCoverage()
    {
        string methods = string.Join("\n", Enumerable.Range(0, 600).Select(index => $"private void Handler{index}(object sender, RoutedEventArgs args) {{ }}"));
        string elements = string.Join("", Enumerable.Range(0, 600).Select(index => $"<Button Click='Handler{index}'/>"));
        var compilation = Compilation("public partial class View { " + methods + " }");
        var watch = Stopwatch.StartNew();
        var result = Service.AnalyzeDetailed("View.xaml", Xaml(elements), 1, compilation);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.IsComplete);
        Assert.Contains("incomplete", result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Bounded batch took {watch.Elapsed}.");
    }

    private static CSharpCompilation Compilation(string extra = "")
    {
        var text = """
            using System;
            using System.Windows;
            using System.Windows.Input;
            namespace EventFixture;
            public class Base : Window {
                protected void ProtectedClick(object sender, EventArgs args) {}
                private void BasePrivate(object sender, RoutedEventArgs args) {}
            }
            public partial class View : Base {
                private void PrivateClick(object sender, RoutedEventArgs args) {}
                private static void StaticClick(object sender, RoutedEventArgs args) {}
                private void WrongClick(int sender, RoutedEventArgs args) {}
                private void GenericClick<T>(object sender, T args) where T : EventArgs {}
                private void MouseEnter(object sender, MouseEventArgs args) {}
                private string Covariant(object sender) => "";
                private void RefHandler(ref int value) {}
                private void OutHandler(out int value) { value = 0; }
                private void Ambiguous(EventArgs first, RoutedEventArgs second) {}
                private void Ambiguous(RoutedEventArgs first, EventArgs second) {}
                private void Overload(object sender, RoutedEventArgs args) {}
                private void Overload(int sender, EventArgs args) {}
                private void @event(object sender, RoutedEventArgs args) {}
            }
            public delegate object CovariantDelegate(string sender);
            public delegate void RefDelegate(ref int value);
            public delegate void AmbiguousDelegate(RoutedEventArgs first, RoutedEventArgs second);
            public class Probe : FrameworkElement {
                public event CovariantDelegate Covariant;
                public event RefDelegate RefEvent;
                public event AmbiguousDelegate Ambiguous;
            }
            public static class EventProvider {
                public static readonly RoutedEvent CompletedEvent = EventManager.RegisterRoutedEvent("Completed", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EventProvider));
                public static void AddCompletedHandler(DependencyObject target, RoutedEventHandler handler) {}
                public static void RemoveCompletedHandler(DependencyObject target, RoutedEventHandler handler) {}
                public static void AddNotRegisteredHandler(DependencyObject target, RoutedEventHandler handler) {}
            }
            """;
        var trees = new[] { CSharpSyntaxTree.ParseText(text, path: "View.xaml.cs"), CSharpSyntaxTree.ParseText("using System.Windows; namespace EventFixture; " + extra, path: "View.Extra.cs") };
        return CSharpCompilation.Create("EventApp", trees, References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static MetadataReference[] CreateReferences()
    {
        string dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        string packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.WindowsDesktop.App.Ref");
        string pack = Directory.GetDirectories(packRoot).OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version()).First();
        string referenceDirectory = Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(path => path, StringComparer.Ordinal).First();
        string[] wpf = Directory.GetFiles(referenceDirectory, "*.dll");
        var names = wpf.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !names.Contains(Path.GetFileName(path))).Concat(wpf).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }
}
