using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlPageSemanticProjectionTests
{
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:ProjectionFixture'";
    private static readonly Lazy<MetadataReference[]> References = new(CreateReferences);

    [Fact]
    public async Task DirectRenameAndTypeChangeReplaceStaleConnectorBodiesWithoutChangingAuthoredDocuments()
    {
        using var fixture = new Fixture("public partial class View { public void Use() { InitializeComponent(); NewButton.IsDefault = true; } }");
        var authored = fixture.WithGenerated();
        string current = Page("<Button x:Name='NewButton'/>");
        var result = await XamlPageSemanticProjection.BuildAsync(authored, [fixture.Input(current)]);
        var page = Assert.Single(result.Pages);
        Assert.True(page.IsComplete, page.Status);
        Assert.Equal(fixture.GeneratedId, Assert.Single(page.ReplacedDocuments));
        Assert.Null(result.Solution.GetDocument(fixture.GeneratedId));
        var field = Assert.Single(page.Fields);
        Assert.Equal("NewButton", field.Name);
        Assert.Contains("global::System.Windows.Controls.Button", field.TypeIdentity);
        Assert.Equal("NewButton", current.Substring(field.Start, field.Length));
        Assert.Equal("Button", current.Substring(field.TypeStart, field.TypeLength));
        Assert.Equal(await authored.GetDocument(fixture.AuthoredId)!.GetTextAsync(), await result.Solution.GetDocument(fixture.AuthoredId)!.GetTextAsync());
        Assert.NotNull(authored.GetDocument(fixture.GeneratedId));
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
        Assert.Empty(compilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("OldInput"));
        string synthetic = (await result.Solution.GetDocument(page.ProjectionDocumentId!)!.GetTextAsync()).ToString();
        Assert.DoesNotContain("StaleHandler", synthetic);
        Assert.DoesNotContain("PresentationBuildTasks", synthetic);
    }

    [Fact]
    public async Task RemovingNameProducesRealAuthoredCSharpErrorInsteadOfKeepingCompiledField()
    {
        using var fixture = new Fixture("public partial class View { public string Use() => OldInput.Text; }");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.WithGenerated(), [fixture.Input(Page("<TextBox/>"))]);
        Assert.Empty(Assert.Single(result.Pages).Fields);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.Contains(compilation.GetDiagnostics(), issue => issue.Id == "CS0103" && issue.GetMessage().Contains("OldInput", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnbuiltPageIncludesRootResourceRuntimeAliasAndScopeOwnerButExcludesScopeChildren()
    {
        using var fixture = new Fixture("""
            [System.Windows.Markup.RuntimeNameProperty("Identifier")]
            public class NamedObject : System.Windows.DependencyObject { public string Identifier { get; set; } = ""; }
            public class ScopeGrid : System.Windows.Controls.Grid, System.Windows.Markup.INameScope
            {
                public object FindName(string name) => null;
                public void RegisterName(string name, object value) { }
                public void UnregisterName(string name) { }
            }
            public partial class View { }
            """);
        string text = $"""
            <UserControl {Namespaces} x:Class='ProjectionFixture.View' x:Name='Root'>
              <UserControl.Resources>
                <SolidColorBrush x:Name='Accent' x:Key='Brush' Color='Red'/>
                <ControlTemplate x:Key='Template' x:Name='TemplateOwner'><Button x:Name='TemplateChild'/></ControlTemplate>
              </UserControl.Resources>
              <StackPanel><TextBox Name='Input'/><local:NamedObject Identifier='Aliased'/>
                <local:ScopeGrid x:Name='ScopeOwner'><TextBox x:Name='ScopeChild'/></local:ScopeGrid>
              </StackPanel>
            </UserControl>
            """;
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.Solution, [fixture.Input(text)]);
        var page = Assert.Single(result.Pages);
        Assert.True(page.IsComplete, page.Status);
        Assert.Equal(new[] { "Accent", "Aliased", "Input", "Root", "ScopeOwner", "TemplateOwner" }, page.Fields.Select(field => field.Name).Order().ToArray());
        Assert.Contains("global::ProjectionFixture.View", page.Fields.Single(field => field.Name == "Root").TypeIdentity);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("private", Accessibility.Private)]
    [InlineData("internal", Accessibility.Internal)]
    [InlineData("public", Accessibility.Public)]
    [InlineData("protected", Accessibility.Protected)]
    [InlineData("protected internal", Accessibility.ProtectedOrInternal)]
    public async Task FieldModifierAndKeywordNamesUseCSharpSemantics(string modifier, Accessibility expected)
    {
        using var fixture = new Fixture("public partial class View { }");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.Solution,
            [fixture.Input(Page($"<Button x:Name='class' x:FieldModifier='{modifier}'/>"))]);
        Assert.True(Assert.Single(result.Pages).IsComplete);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        var field = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(compilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("class")));
        Assert.Equal(expected, field.DeclaredAccessibility);
        Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task EntitySpellingMapsToRawAuthoredSpanAndProjectionIdsStayStable()
    {
        using var fixture = new Fixture("public partial class View { }");
        string text = Page("<Button x:Name='S&#x61;ve'/>");
        var first = await XamlPageSemanticProjection.BuildAsync(fixture.Solution, [fixture.Input(text)]);
        var second = await XamlPageSemanticProjection.BuildAsync(fixture.Solution, [fixture.Input(text.Replace("Button", "TextBox", StringComparison.Ordinal))]);
        var field = Assert.Single(Assert.Single(first.Pages).Fields);
        Assert.Equal("Save", field.Name);
        Assert.Equal("S&#x61;ve", text.Substring(field.Start, field.Length));
        Assert.Equal(first.Pages[0].ProjectionDocumentId, second.Pages[0].ProjectionDocumentId);
    }

    [Fact]
    public async Task LinkedXamlProjectsHaveIndependentStableSyntheticPathsAndRenameContexts()
    {
        using var fixture = new Fixture("public partial class View { }");
        var otherId = ProjectId.CreateNewId();
        var authored = fixture.Solution.AddProject(ProjectInfo.Create(otherId, VersionStamp.Create(), "Other", "Other", LanguageNames.CSharp,
            filePath: Path.Combine(Path.GetDirectoryName(fixture.XamlPath)!, "Other.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: References.Value));
        string text = Page("<Button x:Name='Input'/>");
        XamlPageProjectionInput[] inputs = [fixture.Input(text), new(otherId, fixture.XamlPath, text)];
        var result = await XamlPageSemanticProjection.BuildAsync(authored, inputs);
        var firstPage = result.Pages.Single(page => page.Input.ProjectId == fixture.ProjectId);
        var otherPage = result.Pages.Single(page => page.Input.ProjectId == otherId);
        string firstPath = result.Solution.GetDocument(firstPage.ProjectionDocumentId!)!.FilePath!;
        string otherPath = result.Solution.GetDocument(otherPage.ProjectionDocumentId!)!.FilePath!;
        Assert.NotEqual(firstPath, otherPath);
        Assert.EndsWith(".g.cs", firstPath);
        Assert.EndsWith(".g.cs", otherPath);
        var rebuilt = await XamlPageSemanticProjection.BuildAsync(authored, inputs);
        Assert.Equal(firstPath, rebuilt.Solution.GetDocument(firstPage.ProjectionDocumentId!)!.FilePath);
        Assert.Equal(otherPath, rebuilt.Solution.GetDocument(otherPage.ProjectionDocumentId!)!.FilePath);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        var field = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(compilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("Input")));
        var renamed = await Renamer.RenameSymbolAsync(result.Solution, field, new SymbolRenameOptions(), "Renamed");
        var otherCompilation = (await renamed.GetProject(otherId)!.GetCompilationAsync())!;
        Assert.Single(otherCompilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("Input"));
        Assert.Empty(otherCompilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("Renamed"));
        var firstCompilation = (await renamed.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.Single(firstCompilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("Renamed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<UserControl")]
    [InlineData("<UserControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='ProjectionFixture.View'><Button x:Name='New'")]
    public async Task IncompleteTextWithholdsStaleCompiledFieldsAndReportsCoverage(string text)
    {
        using var fixture = new Fixture("public partial class View { }");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.WithGenerated(), [fixture.Input(text)]);
        var page = Assert.Single(result.Pages);
        Assert.False(page.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(page.Status));
        Assert.Empty(page.Fields);
        Assert.Equal(fixture.GeneratedId, Assert.Single(page.ReplacedDocuments));
        Assert.Null(result.Solution.GetDocument(fixture.GeneratedId));
    }

    [Fact]
    public async Task ChangedClassDoesNotRetainOldCompilerClassOrField()
    {
        using var fixture = new Fixture("");
        string text = Page("<Button x:Name='NewButton'/>").Replace("ProjectionFixture.View", "ProjectionFixture.Renamed", StringComparison.Ordinal);
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.WithGenerated(), [fixture.Input(text)]);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.Null(compilation.GetTypeByMetadataName("ProjectionFixture.View"));
        Assert.NotNull(compilation.GetTypeByMetadataName("ProjectionFixture.Renamed")!.GetMembers("NewButton").Single());
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("path")]
    [InlineData("header")]
    [InlineData("authored-file")]
    [InlineData("producer-lookalike")]
    [InlineData("connector-lookalike")]
    public async Task CompilerOwnershipDoesNotRemoveUnprovenDocuments(string defect)
    {
        using var fixture = new Fixture("public partial class View { }");
        var authored = fixture.WithGenerated(defect);
        var result = await XamlPageSemanticProjection.BuildAsync(authored, [fixture.Input(Page("<Button x:Name='Current'/>"))]);
        Assert.Empty(Assert.Single(result.Pages).ReplacedDocuments);
        Assert.NotNull(result.Solution.GetDocument(fixture.GeneratedId));
        Assert.Equal(await authored.GetDocument(fixture.GeneratedId)!.GetTextAsync(), await result.Solution.GetDocument(fixture.GeneratedId)!.GetTextAsync());
    }

    [Theory]
    [InlineData("<Button x:Name='Same'/><TextBox x:Name='Same'/>")]
    [InlineData("<Button x:Name='First' Name='Second'/>")]
    [InlineData("<Button x:Name='Bad-Name'/>")]
    [InlineData("<Button x:Name='Bad' x:FieldModifier='static'/>")]
    [InlineData("<local:Missing x:Name='Bad'/>")]
    public async Task InvalidOrUnresolvedNamesDoNotInventFields(string body)
    {
        using var fixture = new Fixture("public partial class View { }");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.Solution, [fixture.Input(Page("<Grid>" + body + "</Grid>"))]);
        var page = Assert.Single(result.Pages);
        Assert.False(page.IsComplete);
        Assert.Empty(page.Fields);
    }

    [Fact]
    public async Task QualifiedRuntimeNamePropertyDoesNotInventCompilerField()
    {
        using var fixture = new Fixture("public partial class View { }");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.Solution,
            [fixture.Input(Page("<Button FrameworkElement.Name='AttachedName'/>"))]);
        Assert.Empty(Assert.Single(result.Pages).Fields);
    }

    [Fact]
    public async Task AuthoredMemberCollisionStaysVisibleAsCompilerError()
    {
        using var fixture = new Fixture("public partial class View { public string Input; }");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.Solution, [fixture.Input(Page("<Button x:Name='Input'/>"))]);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.Contains(compilation.GetDiagnostics(), issue => issue.Id == "CS0102");
        Assert.Equal(2, compilation.GetTypeByMetadataName("ProjectionFixture.View")!.GetMembers("Input").Length);
    }

    [Fact]
    public async Task UnbuiltPagesBootstrapCurrentBaseTypesAcrossTheProject()
    {
        using var fixture = new Fixture("public partial class View { } public partial class Other { }");
        string otherPath = Path.Combine(Path.GetDirectoryName(fixture.XamlPath)!, "Other.xaml");
        string other = $"<local:View {Namespaces} x:Class='ProjectionFixture.Other'><Button x:Name='Child'/></local:View>";
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.Solution,
            [new(fixture.ProjectId, otherPath, other), fixture.Input(Page("<Button x:Name='BaseField'/>"))]);
        Assert.All(result.Pages, page => Assert.True(page.IsComplete, page.Status));
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.Equal("ProjectionFixture.View", compilation.GetTypeByMetadataName("ProjectionFixture.Other")!.BaseType!.ToDisplayString());
        Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ClasslessResourcesAreCompleteNoOpsAndDoNotConsumeThePageClassBudget()
    {
        using var fixture = new Fixture("public partial class View { public void Use() => Current.IsDefault = true; }");
        var inputs = Enumerable.Range(0, 600).Select(index => new XamlPageProjectionInput(fixture.ProjectId,
            Path.Combine(Path.GetDirectoryName(fixture.XamlPath)!, "Resources" + index + ".xaml"),
            $"<ResourceDictionary {Namespaces}><SolidColorBrush x:Key='Accent' Color='Red'/></ResourceDictionary>")).ToList();
        inputs.Add(fixture.Input(Page("<Button x:Name='Current'/>")));
        var authored = fixture.WithGenerated();
        var result = await XamlPageSemanticProjection.BuildAsync(authored, inputs);
        Assert.Equal(601, result.Pages.Count);
        Assert.All(result.Pages.Take(600), page =>
        {
            Assert.True(page.IsComplete, page.Status);
            Assert.Null(page.Status);
            Assert.Null(page.ProjectionDocumentId);
            Assert.Empty(page.ReplacedDocuments);
            Assert.Empty(page.Fields);
        });
        Assert.Equal("Current", Assert.Single(result.Pages[^1].Fields).Name);
        Assert.Null(result.Solution.GetDocument(fixture.GeneratedId));
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
        var onlyResources = await XamlPageSemanticProjection.BuildAsync(fixture.Solution, inputs.Take(600).ToArray());
        Assert.Same(fixture.Solution, onlyResources.Solution);
    }

    [Fact]
    public async Task RemovingClassRetiresPreviouslyOwnedPartialWithoutInventingAnIncompletePage()
    {
        using var fixture = new Fixture("");
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.WithGenerated(),
            [fixture.Input($"<UserControl {Namespaces}><TextBox x:Name='Input'/></UserControl>")]);
        var page = Assert.Single(result.Pages);
        Assert.True(page.IsComplete, page.Status);
        Assert.Null(page.ProjectionDocumentId);
        Assert.Equal(fixture.GeneratedId, Assert.Single(page.ReplacedDocuments));
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.Null(compilation.GetTypeByMetadataName("ProjectionFixture.View"));
    }

    [Fact]
    public async Task WorkspaceTextBudgetStillWithholdsKnownStaleCompilerDeclarations()
    {
        using var fixture = new Fixture("");
        string oversized = new(' ', 16_000_001);
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.WithGenerated(), [fixture.Input(oversized)]);
        var page = Assert.Single(result.Pages);
        Assert.False(page.IsComplete);
        Assert.Contains("budget", page.Status);
        Assert.Equal(fixture.GeneratedId, Assert.Single(page.ReplacedDocuments));
        Assert.Null(result.Solution.GetDocument(fixture.GeneratedId));
        Assert.Empty(page.Fields);
    }

    [Fact]
    public async Task ProvenCompilerStyleConnectorAndDelegateHelperKeepInertSemanticSignatures()
    {
        using var fixture = new Fixture("""
            public partial class View {
                public System.Windows.Markup.IStyleConnector Connector() => this;
                public System.Delegate Delegate() => _CreateDelegate(typeof(System.Action), "Handler");
            }
            """);
        var result = await XamlPageSemanticProjection.BuildAsync(fixture.WithGenerated(helpers: true),
            [fixture.Input(Page("<Grid/>"))]);
        var page = Assert.Single(result.Pages);
        Assert.True(page.IsComplete, page.Status);
        var compilation = (await result.Solution.GetProject(fixture.ProjectId)!.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
        string synthetic = (await result.Solution.GetDocument(page.ProjectionDocumentId!)!.GetTextAsync()).ToString();
        Assert.Contains("IStyleConnector.Connect", synthetic);
        Assert.Contains("_CreateDelegate", synthetic);
        Assert.DoesNotContain("StaleHandler", synthetic);
        Assert.DoesNotContain("CreateDelegate(delegateType, this", synthetic);
    }

    private static string Page(string body) => $"<UserControl {Namespaces} x:Class='ProjectionFixture.View'>{body}</UserControl>";

    private sealed class Fixture : IDisposable
    {
        private readonly AdhocWorkspace _workspace = new();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "WpfStudio-PageModel-" + Guid.NewGuid().ToString("N"));
        internal ProjectId ProjectId { get; } = ProjectId.CreateNewId();
        internal DocumentId AuthoredId { get; }
        internal DocumentId GeneratedId { get; }
        internal Solution Solution { get; }
        internal string XamlPath => Path.Combine(_directory, "View.xaml");
        internal Fixture(string authored)
        {
            AuthoredId = DocumentId.CreateNewId(ProjectId); GeneratedId = DocumentId.CreateNewId(ProjectId);
            Solution = _workspace.CurrentSolution.AddProject(ProjectInfo.Create(ProjectId, VersionStamp.Create(), "ProjectionFixture", "ProjectionFixture", LanguageNames.CSharp,
                filePath: Path.Combine(_directory, "ProjectionFixture.csproj"), compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: References.Value))
                .AddDocument(AuthoredId, "Authored.cs", SourceText.From("namespace ProjectionFixture { " + authored + " }"), filePath: Path.Combine(_directory, "Authored.cs"));
        }
        internal XamlPageProjectionInput Input(string text) => new(ProjectId, XamlPath, text);
        internal Solution WithGenerated(string? defect = null, bool helpers = false)
        {
            string mapped = (defect == "path" ? Path.Combine(_directory, "Other.xaml") : XamlPath).Replace("\\", "\\\\", StringComparison.Ordinal);
            string code = $$"""
                {{(defect == "header" ? "// authored" : "// <auto-generated />")}}
                #pragma checksum "{{mapped}}" "{ff1816ec-aa5e-4d10-87f7-6f4963833460}" "0000000000000000000000000000000000000000"
                namespace ProjectionFixture {
                    public partial class View : System.Windows.Controls.UserControl, System.Windows.Markup.IComponentConnector{{(helpers ? ", System.Windows.Markup.IStyleConnector" : "")}} {
                        internal System.Windows.Controls.TextBox OldInput;
                        [System.CodeDom.Compiler.GeneratedCode("{{(defect == "producer" ? "OtherGenerator" : "PresentationBuildTasks")}}", "10.0.0.0")]
                        public void InitializeComponent() { }
                        void System.Windows.Markup.IComponentConnector.Connect(int id, object target) {
                            OldInput = (System.Windows.Controls.TextBox)target;
                            OldInput.TextChanged += StaleHandler;
                        }
                        {{(helpers ? "[System.CodeDom.Compiler.GeneratedCode(\"PresentationBuildTasks\", \"10.0.0.0\")] void System.Windows.Markup.IStyleConnector.Connect(int id, object target) { StaleHandler(); } [System.CodeDom.Compiler.GeneratedCode(\"PresentationBuildTasks\", \"10.0.0.0\")] internal System.Delegate _CreateDelegate(System.Type delegateType, string handler) => System.Delegate.CreateDelegate(delegateType, this, handler);" : "")}}
                    }
                }
                """;
            var solution = Solution.AddDocument(GeneratedId, "View.g.cs", SourceText.From(code),
                filePath: Path.Combine(_directory, "obj", defect == "authored-file" ? "Authored.cs" : "View.g.cs"));
            if (defect is "producer-lookalike" or "connector-lookalike")
                solution = solution.AddDocument(DocumentId.CreateNewId(ProjectId), "Lookalike.cs", SourceText.From(defect == "producer-lookalike"
                    ? "namespace System.CodeDom.Compiler { public sealed class GeneratedCodeAttribute : System.Attribute { public GeneratedCodeAttribute(string tool,string version) {} } }"
                    : "namespace System.Windows.Markup { public interface IComponentConnector { void Connect(int id, object target); } }"));
            return solution;
        }
        public void Dispose() => _workspace.Dispose();
    }

    private static MetadataReference[] CreateReferences()
    {
        var dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.WindowsDesktop.App.Ref");
        var pack = Directory.GetDirectories(packRoot).OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version()).First();
        var referenceDirectory = Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(path => path, StringComparer.Ordinal).First();
        var paths = Directory.GetFiles(referenceDirectory, "*.dll");
        var names = paths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !names.Contains(Path.GetFileName(path))).Concat(paths).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }
}
