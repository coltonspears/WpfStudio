using Microsoft.CodeAnalysis;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlResourceIndexTests
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:vm=\"clr-namespace:Demo\"";
    private static Compilation Compilation => XamlResourceResolutionTests.SemanticFixture;
    private static string Assembly => Compilation.Assembly.Identity.GetDisplayName();
    private static XamlResourceDocument Identity(string logical, string? path = null, string kind = "Page") =>
        new(path ?? "C:/Project/" + logical, null, Assembly, logical, kind);
    private static string Dictionary(string body) => $"<ResourceDictionary {Ns}>{body}</ResourceDictionary>";
    private static string View(string dictionaries, string path = "Nmae") => $"<Window {Ns}><Window.Resources>{dictionaries}</Window.Resources><TextBlock Text=\"{{Binding {path}, Source={{StaticResource Model}}}}\"/></Window>";
    private static string Merge(params string[] sources) => "<ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
        string.Concat(sources.Select(source => $"<ResourceDictionary Source=\"{source}\"/>")) + "</ResourceDictionary.MergedDictionaries></ResourceDictionary>";
    private static XamlResourceContext Context(XamlResourceDocument main, string text, XamlResourceIndex index,
        params XamlResourceDocument[] captured) => new(main.Path, Assembly, new[] { main with { Text = text } }.Concat(captured).ToArray(), Index: index);

    [Fact]
    public void LargeIdentityCatalogDoesNotConsumeTheLoadedDocumentBudget()
    {
        var main = Identity("Views/View.xaml");
        var target = Identity("Models.xaml");
        var identities = new[] { main }.Concat(Enumerable.Range(0, 1200).Select(number => Identity($"Unused/{number}.xaml"))).Append(target).ToArray();
        var index = new XamlResourceIndex(identities);
        var text = View("<ResourceDictionary Source=\"../Models.xaml\"/>");
        var model = target with { Text = Dictionary("<vm:Customer x:Key=\"Model\"/>"), Version = 17 };
        var context = Context(main, text, index, model);
        var service = new XamlLanguageService();
        var issue = Assert.Single(service.Analyze(main.Path, text, 4, Compilation, resources: context));
        Assert.Contains("Name", issue.Message);
        var action = Assert.Single(service.GetCodeActions(main.Path, text, text.IndexOf("Nmae", StringComparison.Ordinal), 4, Compilation, resources: context));
        var prerequisite = Assert.Single(action.AdditionalEdits!);
        Assert.Equal(model.Path, prerequisite.Path);
        Assert.Equal(17, prerequisite.Version);
        Assert.All(index.Documents, document => Assert.Null(document.Text));
        Assert.Equal(1202, index.Documents.Count);
        Assert.True(index.IsComplete);
    }

    [Fact]
    public void UnreadDuplicateIdentityStillMakesTheUriAmbiguous()
    {
        var main = Identity("Views/View.xaml");
        var first = Identity("Models.xaml");
        var duplicate = Identity("Models.xaml", "C:/Another/Models.xaml");
        var index = new XamlResourceIndex([main, first, duplicate]);
        var text = View("<ResourceDictionary Source=\"../Models.xaml\"/>");
        var context = Context(main, text, index, first with { Text = Dictionary("<vm:Customer x:Key=\"Model\"/>") });
        Assert.Null(index.ResolveSource(main, "../Models.xaml", Assembly).Document);
        Assert.Empty(new XamlLanguageService().Analyze(main.Path, text, 1, Compilation, resources: context));
        Assert.Equal(3, index.Documents.Count);
    }

    [Fact]
    public void ConsultedUncapturedDictionaryIsUnknownRatherThanEmpty()
    {
        var main = Identity("Views/View.xaml");
        var known = Identity("Known.xaml");
        var unobserved = Identity("Later.xaml");
        var index = new XamlResourceIndex([main, known, unobserved]);
        var text = View(Merge("../Known.xaml", "../Later.xaml"));
        var knownText = known with { Text = Dictionary("<vm:Customer x:Key=\"Model\"/>") };
        var context = Context(main, text, index, knownText);
        var service = new XamlLanguageService();
        Assert.Empty(service.Analyze(main.Path, text, 1, Compilation, resources: context));
        Assert.True(service.GetSymbolOccurrences(text, Compilation, resources: context).CoverageLimited);
        // Capturing an actually empty higher-priority dictionary is new evidence that
        // permits lookup to continue to the earlier known dictionary.
        Assert.Single(service.Analyze(main.Path, text, 1, Compilation,
            resources: Context(main, text, index, knownText, unobserved with { Text = Dictionary("") })));
    }

    [Fact]
    public void IndexTextNeverOverridesTheCapturedSnapshot()
    {
        var main = Identity("Views/View.xaml");
        var target = Identity("Models.xaml");
        var index = new XamlResourceIndex([main, target with { Text = Dictionary("<vm:Customer x:Key=\"Model\"/>") }]);
        var text = View("<ResourceDictionary Source=\"../Models.xaml\"/>", "Title");
        var context = Context(main, text, index, target with { Text = Dictionary("<vm:Order x:Key=\"Model\"/>") });
        Assert.Empty(new XamlLanguageService().Analyze(main.Path, text, 1, Compilation, resources: context));
        var occurrence = Assert.Single(new XamlLanguageService().GetSymbolOccurrences(text, Compilation, resources: context).Occurrences);
        Assert.Equal("Order", occurrence.Symbol.ContainingType.Name);
    }

    [Fact]
    public void MetadataTruncationCannotMakeAnOmittedPhysicalDuplicateAppearUnique()
    {
        var app = Identity("App.xaml", kind: "ApplicationDefinition");
        var main = Identity("Views/View.xaml");
        var entries = new[] { app, main }.Concat(Enumerable.Range(0, XamlResourceIndex.MaximumDocuments - 2).Select(number => Identity($"Unused/{number}.xaml")))
            .Append(app with { ResourcePath = "Alias/App.xaml" }).ToArray();
        var index = new XamlResourceIndex(entries);
        Assert.False(index.IsComplete);
        Assert.Equal(XamlResourceIndex.MaximumDocuments, index.Documents.Count);
        Assert.Null(index.FindDocument(app.Path, Assembly, "ApplicationDefinition").Document);
        Assert.True(index.FindDocument(app.Path, Assembly).CoverageLimited);
        var text = View("<ResourceDictionary/>");
        var appText = $"<Application {Ns}><Application.Resources><vm:Customer x:Key=\"Model\"/></Application.Resources></Application>";
        var context = new XamlResourceContext(main.Path, Assembly, [main with { Text = text }, app with { Text = appText }], app.Path, Index: index);
        Assert.Empty(new XamlLanguageService().Analyze(main.Path, text, 1, Compilation, resources: context));
    }

    [Fact]
    public void LogicalOriginsOfOnePhysicalFileDiscoverDifferentDependencies()
    {
        var first = Identity("A/Shared.xaml", "C:/Shared/Shared.xaml");
        var second = Identity("B/Shared.xaml", first.Path);
        var dataA = Identity("A/Data.xaml");
        var dataB = Identity("B/Data.xaml");
        var index = new XamlResourceIndex([first, second, dataA, dataB]);
        var text = Dictionary("<ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"Data.xaml\"/></ResourceDictionary.MergedDictionaries>");
        var resultA = XamlResourceDependencies.Discover(text, first, Assembly, Compilation, index);
        var resultB = XamlResourceDependencies.Discover(text, second, Assembly, Compilation, index);
        Assert.Equal(dataA.ResourcePath, Assert.Single(resultA.Documents).ResourcePath);
        Assert.Equal(dataB.ResourcePath, Assert.Single(resultB.Documents).ResourcePath);
        Assert.True(resultA.IsComplete);
        Assert.True(resultB.IsComplete);
        Assert.Null(index.FindDocument(first.Path, Assembly).Document);
    }

    [Fact]
    public void InheritedXmlBaseIsTheSameBoundaryForDiscoveryAndSemanticLookup()
    {
        var main = Identity("Views/View.xaml");
        var target = Identity("Models.xaml");
        var index = new XamlResourceIndex([main, target]);
        var text = View("<ResourceDictionary><ResourceDictionary.MergedDictionaries xml:base=\"../Other/\"><ResourceDictionary Source=\"../Models.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary>");
        var dependencies = XamlResourceDependencies.Discover(text, main, Assembly, Compilation, index);
        Assert.Empty(dependencies.Documents);
        Assert.False(dependencies.IsComplete);
        Assert.Contains(dependencies.Warnings, warning => warning.Contains("xml:base", StringComparison.Ordinal));
        var context = Context(main, text, index, target with { Text = Dictionary("<vm:Customer x:Key=\"Model\"/>") });
        Assert.Empty(new XamlLanguageService().Analyze(main.Path, text, 1, Compilation, resources: context));
    }

    [Fact]
    public void TypingDiscoveryRetainsClosedImportsBeforeAnUnfinishedControl()
    {
        var main = Identity("Views/View.xaml");
        var target = Identity("Models.xaml");
        var index = new XamlResourceIndex([main, target]);
        var text = $"<Window {Ns}><Window.Resources><ResourceDictionary Source=\"../Models.xaml\"/></Window.Resources><TextBlock Text=\"{{Binding ";
        Assert.Empty(XamlResourceDependencies.Discover(text, main, Assembly, Compilation, index).Documents);
        var tolerant = XamlResourceDependencies.Discover(text, main, Assembly, Compilation, index, allowIncompleteDocument: true);
        Assert.Equal(target.ResourcePath, Assert.Single(tolerant.Documents).ResourcePath);
        Assert.True(tolerant.IsComplete);
    }

    [Fact]
    public void IgnoredAndCustomDictionaryObjectsDoNotTriggerDependencyReads()
    {
        var main = Identity("Views/View.xaml");
        var target = Identity("Models.xaml");
        var index = new XamlResourceIndex([main, target]);
        var text = $"<Window {Ns} xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"><d:Window.Resources><ResourceDictionary Source=\"../Models.xaml\"/></d:Window.Resources><vm:Customer Source=\"../Models.xaml\"/></Window>";
        Assert.Empty(XamlResourceDependencies.Discover(text, main, Assembly, Compilation, index).Documents);
    }

    [Theory]
    [InlineData("../Models.xaml")]
    [InlineData("/Models.xaml")]
    [InlineData("pack://application:,,,/Models.xaml")]
    [InlineData("/ResourceApp;component/Models.xaml")]
    public void DependencyDiscoveryAndGraphShareUriResolution(string uri)
    {
        var main = Identity("Views/View.xaml");
        var target = Identity("Models.xaml");
        var index = new XamlResourceIndex([main, target]);
        var text = View($"<ResourceDictionary Source=\"{uri}\"/>");
        Assert.Equal(target.ResourcePath, Assert.Single(XamlResourceDependencies.Discover(text, main, Assembly, Compilation, index).Documents).ResourcePath);
        Assert.Single(new XamlLanguageService().Analyze(main.Path, text, 1, Compilation,
            resources: Context(main, text, index, target with { Text = Dictionary("<vm:Customer x:Key=\"Model\"/>") })));
    }

    [Fact]
    public void MetadataAndDiscoveryAreCancellableAndBounded()
    {
        var origin = Identity("View.xaml");
        var index = new XamlResourceIndex([origin, Identity(new string('x', 4097))]);
        Assert.False(index.IsComplete);
        Assert.Single(index.Documents);
        Assert.True(index.ResolveSource(origin, "Other.xaml", Assembly).CoverageLimited);
        var deep = $"<Window {Ns}>" + string.Concat(Enumerable.Repeat("<TextBlock>", 260));
        Assert.True(XamlResourceDependencies.Discover(deep, origin, Assembly, Compilation, new([origin]), allowIncompleteDocument: true).CoverageLimited);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new XamlResourceIndex([origin], token: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => XamlResourceDependencies.Discover("", origin, Assembly, Compilation, index, cancellation.Token));
    }
}
