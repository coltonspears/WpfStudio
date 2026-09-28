using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlGeneratedNameBridgeTests
{
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private const string Sha1 = "ff1816ec-aa5e-4d10-87f7-6f4963833460";
    private const string Sha256 = "8829d00f-11b8-4213-878b-770e8597ac16";
    private static readonly Lazy<MetadataReference[]> References = new(CreateReferences);

    [Theory]
    [InlineData("utf8", Sha1)]
    [InlineData("utf8-bom", Sha1)]
    [InlineData("utf16", Sha1)]
    [InlineData("utf16-be", Sha256)]
    [InlineData("utf32", Sha256)]
    [InlineData("utf32-be", Sha256)]
    public async Task ExactCompilerBytesAndMappedAttributeLineBridgeBothDirections(string encoding, string algorithm)
    {
        await using var fixture = await Fixture.CreateAsync(encoding: encoding, algorithm: algorithm);
        var result = await fixture.FromFieldAsync();
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, result.State);
        Assert.Equal(fixture.Source.Path, result.Path);
        Assert.Equal("Input", fixture.Source.Text.Substring(result.Start, result.Length));
        Assert.True(SymbolEqualityComparer.Default.Equals(fixture.Field, result.Field));
        var reverse = await fixture.FromDeclarationAsync();
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, reverse.State);
        Assert.True(SymbolEqualityComparer.Default.Equals(fixture.Field, reverse.Field));
        // The suppression attribute occupies the first mapped line. The identifier
        // is deliberately on the next line, matching actual WPF generated output.
        var identifier = fixture.Field.Locations.Single().GetMappedLineSpan();
        int attributeLine = SourceText.From(fixture.Source.Text).Lines.GetLineFromPosition(fixture.Source.Text.IndexOf("x:Name", StringComparison.Ordinal)).LineNumber;
        Assert.Equal(attributeLine + 1, identifier.StartLinePosition.Line);
    }

    [Fact]
    public async Task RuntimeNameAliasUsesTheSameVerifiedFieldBridge()
    {
        await using var fixture = await Fixture.CreateAsync(alias: true);
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, (await fixture.FromFieldAsync()).State);
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, (await fixture.FromDeclarationAsync()).State);
    }

    [Fact]
    public async Task RootNameUsesTheActualPartialClassFieldType()
    {
        await using var fixture = await Fixture.CreateAsync(rootName: true);
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, (await fixture.FromFieldAsync()).State);
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, (await fixture.FromDeclarationAsync()).State);
    }

    [Fact]
    public async Task AnUnsupportedDeclaredEncodingIsNotSilentlyReadAsUtf8()
    {
        await using var fixture = await Fixture.CreateAsync(xmlDeclaration: "<?xml version='1.0' encoding='windows-1252'?>\n");
        var result = await fixture.FromFieldAsync();
        AssertUnavailable(result);
        Assert.Contains("encoding", result.Status);
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("bom")]
    [InlineData("overlay")]
    public async Task SameDecodedNamesDoNotBypassTheExactCompilerSourceSnapshot(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Source;
        if (changed == "disk") await File.AppendAllTextAsync(source.Path, " ");
        else if (changed == "bom") await File.WriteAllTextAsync(source.Path, source.Text, new UTF8Encoding(true));
        else source = source with { Text = source.Text + " " };
        var result = await XamlGeneratedNameBridge.FromFieldAsync(fixture.Project, fixture.Compilation, fixture.Field, [source]);
        AssertUnavailable(result);
        Assert.Contains(changed == "overlay" ? "buffer differs" : "bytes differ", result.Status);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("path")]
    [InlineData("duplicate-checksum")]
    [InlineData("unknown-algorithm")]
    [InlineData("wrong-type")]
    [InlineData("static")]
    [InlineData("producer")]
    [InlineData("attribute-lookalike")]
    [InlineData("connector-lookalike")]
    [InlineData("missing-header")]
    [InlineData("wrong-class")]
    public async Task UnsupportedOrConflictingGeneratedEvidenceNeverBecomesAnAuthoredEdit(string defect)
    {
        await using var fixture = await Fixture.CreateAsync(defect: defect);
        AssertUnavailable(await fixture.FromFieldAsync());
    }

    [Fact]
    public async Task AnAuthoredFieldIsNotMistakenForGeneratedCode()
    {
        await using var fixture = await Fixture.CreateAsync(defect: "authored-field");
        Assert.Equal(XamlGeneratedNameBridgeState.NotGeneratedField, (await fixture.FromFieldAsync()).State);
        AssertUnavailable(await fixture.FromDeclarationAsync());
    }

    [Fact]
    public async Task DuplicateAuthoredNamesAndDuplicateEvaluatedMappingsAreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var duplicate = fixture.Source with { Text = fixture.Source.Text.Replace("</Grid>", "<TextBox x:Name='Input'/></Grid>", StringComparison.Ordinal) };
        AssertUnavailable(await XamlGeneratedNameBridge.FromFieldAsync(fixture.Project, fixture.Compilation, fixture.Field, [duplicate]));
        AssertUnavailable(await XamlGeneratedNameBridge.FromFieldAsync(fixture.Project, fixture.Compilation, fixture.Field, [fixture.Source, fixture.Source]));
    }

    [Fact]
    public async Task AFieldFromAnEarlierCompilationCannotBridgeTheCurrentProject()
    {
        await using var fixture = await Fixture.CreateAsync();
        var generated = Assert.Single(fixture.Project.Documents, document => document.FilePath!.EndsWith(".g.cs", StringComparison.Ordinal));
        string previousText = (await generated.GetTextAsync()).ToString();
        var changed = generated.WithText(SourceText.From(previousText.Replace(" Input;", " Changed;", StringComparison.Ordinal))).Project;
        var compilation = (await changed.GetCompilationAsync())!;
        AssertUnavailable(await XamlGeneratedNameBridge.FromFieldAsync(changed, compilation, fixture.Field, [fixture.Source]));
    }

    [Fact]
    public async Task TemplateAndClasslessNamesHaveNoPageFieldButMissingPageFieldsRemainUnproven()
    {
        await using var fixture = await Fixture.CreateAsync();
        string templateText = $"<UserControl {Namespaces} x:Class='BridgeFixture.View'><UserControl.Resources><DataTemplate x:Key='T'><TextBox x:Name='Inside'/></DataTemplate></UserControl.Resources><TextBox/></UserControl>";
        var template = new XamlGeneratedNameSource(fixture.Source.Path, templateText);
        var local = await XamlGeneratedNameBridge.FromDeclarationAsync(fixture.Project, fixture.Compilation, template, templateText.IndexOf("Inside", StringComparison.Ordinal));
        Assert.Equal(XamlGeneratedNameBridgeState.NoGeneratedField, local.State);
        string classlessText = $"<UserControl {Namespaces}><TextBox x:Name='Loose'/></UserControl>";
        var classless = await XamlGeneratedNameBridge.FromDeclarationAsync(fixture.Project, fixture.Compilation,
            new(fixture.Source.Path, classlessText), classlessText.IndexOf("Loose", StringComparison.Ordinal));
        Assert.Equal(XamlGeneratedNameBridgeState.NoGeneratedField, classless.State);
        string pageText = fixture.Source.Text.Replace("Input", "NotBuilt", StringComparison.Ordinal);
        AssertUnavailable(await XamlGeneratedNameBridge.FromDeclarationAsync(fixture.Project, fixture.Compilation,
            new(fixture.Source.Path, pageText), pageText.IndexOf("NotBuilt", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TemplateNamesDoNotMakeTheSameNamedPageFieldAmbiguous(int count)
    {
        string templates = string.Concat(Enumerable.Range(0, count).Select(index => $"<DataTemplate x:Key='T{index}'><TextBox x:Name='Input'/></DataTemplate>"));
        await using var fixture = await Fixture.CreateAsync(extra: "<UserControl.Resources>" + templates + "</UserControl.Resources>");
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, (await fixture.FromFieldAsync()).State);
        Assert.Equal(XamlGeneratedNameBridgeState.Verified, (await fixture.FromDeclarationAsync()).State);
    }

    [Fact]
    public async Task CancellationAndByteBoundsDoNotReturnPartialProof()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XamlGeneratedNameBridge.FromFieldAsync(fixture.Project,
            fixture.Compilation, fixture.Field, [fixture.Source], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XamlGeneratedNameBridge.FromDeclarationAsync(fixture.Project,
            fixture.Compilation, fixture.Source, fixture.DeclarationStart, cancellation.Token));
        await File.WriteAllBytesAsync(fixture.Source.Path, new byte[XamlGeneratedNameBridge.MaximumSourceBytes + 1]);
        var result = await fixture.FromFieldAsync();
        AssertUnavailable(result);
        Assert.Contains("byte budget", result.Status);
    }

    private static void AssertUnavailable(XamlGeneratedNameBridgeResult result)
    {
        Assert.Equal(XamlGeneratedNameBridgeState.Unavailable, result.State);
        Assert.Null(result.Field);
        Assert.False(string.IsNullOrWhiteSpace(result.Status));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AdhocWorkspace _workspace;
        private readonly string _directory;
        internal Project Project { get; }
        internal Compilation Compilation { get; }
        internal IFieldSymbol Field { get; }
        internal XamlGeneratedNameSource Source { get; }
        internal int DeclarationStart => Source.Text.LastIndexOf("Input", StringComparison.Ordinal);
        private Fixture(AdhocWorkspace workspace, string directory, Project project, Compilation compilation, IFieldSymbol field, XamlGeneratedNameSource source)
        { _workspace = workspace; _directory = directory; Project = project; Compilation = compilation; Field = field; Source = source; }

        internal Task<XamlGeneratedNameBridgeResult> FromFieldAsync() => XamlGeneratedNameBridge.FromFieldAsync(Project, Compilation, Field, [Source]);
        internal Task<XamlGeneratedNameBridgeResult> FromDeclarationAsync() => XamlGeneratedNameBridge.FromDeclarationAsync(Project, Compilation, Source, DeclarationStart);

        internal static async Task<Fixture> CreateAsync(string encoding = "utf8", string algorithm = Sha1, bool alias = false, string? defect = null, string extra = "", bool rootName = false, string xmlDeclaration = "")
        {
            string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-NameBridge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var workspace = new AdhocWorkspace();
            try
            {
                string sourcePath = Path.Combine(directory, "View.xaml"), generatedPath = Path.Combine(directory, "obj", "View.g.cs");
                string text = rootName
                    ? $"<UserControl {Namespaces} x:Class='BridgeFixture.View'\n x:Name='Input'><Grid/></UserControl>"
                    : $"<UserControl {Namespaces} x:Class='BridgeFixture.View'>\n{extra}<Grid>\n<TextBox\n {(alias ? "Name" : "x:Name")}='Input'/>\n</Grid>\n</UserControl>";
                text = xmlDeclaration + text;
                Encoding codec = encoding switch
                {
                    "utf8-bom" => new UTF8Encoding(true, true), "utf16" => new UnicodeEncoding(false, true, true),
                    "utf16-be" => new UnicodeEncoding(true, true, true), "utf32" => new UTF32Encoding(false, true, true),
                    "utf32-be" => new UTF32Encoding(true, true, true), _ => new UTF8Encoding(false, true)
                };
                byte[] bytes = codec.GetPreamble().Concat(codec.GetBytes(text)).ToArray();
                await File.WriteAllBytesAsync(sourcePath, bytes);
                string hash = Convert.ToHexString(algorithm == Sha1 ? SHA1.HashData(bytes) : SHA256.HashData(bytes));
                string mapped = (defect == "path" ? Path.Combine(directory, "Other.xaml") : sourcePath).Replace("\\", "\\\\", StringComparison.Ordinal);
                string checksum = $"#pragma checksum \"{mapped}\" \"{{{(defect == "unknown-algorithm" ? Guid.Empty.ToString() : algorithm)}}}\" \"{hash}\"";
                int namePosition = text.LastIndexOf(alias ? "Name='Input'" : "x:Name='Input'", StringComparison.Ordinal);
                int line = SourceText.From(text).Lines.GetLineFromPosition(namePosition).LineNumber + 1;
                string generated = $$"""
                    {{checksum}}
                    {{(defect == "duplicate-checksum" ? checksum : "")}}
                    {{(defect == "missing-header" ? "// authored" : "// <auto-generated />")}}
                    namespace BridgeFixture
                    {
                        public partial class {{(defect == "wrong-class" ? "Other" : "View")}} : System.Windows.Controls.UserControl, System.Windows.Markup.IComponentConnector
                        {
                    #line {{(defect == "line" ? 1 : line)}} "{{mapped}}"
                            [System.Diagnostics.CodeAnalysis.SuppressMessage("Test", "Field")]
                            internal {{(defect == "static" ? "static " : "")}}{{(defect == "wrong-type" ? "System.Windows.Controls.Button" : rootName ? "BridgeFixture.View" : "System.Windows.Controls.TextBox")}} Input;
                    #line default
                    #line hidden
                            [System.CodeDom.Compiler.GeneratedCode("{{(defect == "producer" ? "UserGenerator" : "PresentationBuildTasks")}}", "10.0.0.0")]
                            public void InitializeComponent() { }
                            void System.Windows.Markup.IComponentConnector.Connect(int id, object target) { }
                        }
                    }
                    """;
                if (defect == "authored-field") generatedPath = Path.Combine(directory, "View.cs");
                ProjectId id = ProjectId.CreateNewId();
                var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), "BridgeFixture", "BridgeFixture", LanguageNames.CSharp,
                    filePath: Path.Combine(directory, "BridgeFixture.csproj"), compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: References.Value));
                solution = solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(generatedPath), SourceText.From(generated), filePath: generatedPath);
                solution = solution.AddDocument(DocumentId.CreateNewId(id), "Authored.cs", SourceText.From("namespace BridgeFixture; public partial class View : System.Windows.Controls.UserControl { }"), filePath: Path.Combine(directory, "Authored.cs"));
                if (defect is "attribute-lookalike" or "connector-lookalike")
                {
                    string lookalike = defect == "attribute-lookalike"
                        ? "namespace System.CodeDom.Compiler; public sealed class GeneratedCodeAttribute : System.Attribute { public GeneratedCodeAttribute(string tool, string version) { } }"
                        : "namespace System.Windows.Markup; public interface IComponentConnector { void Connect(int id, object target); }";
                    solution = solution.AddDocument(DocumentId.CreateNewId(id), "Lookalike.cs", SourceText.From(lookalike), filePath: Path.Combine(directory, "Lookalike.cs"));
                }
                Assert.True(workspace.TryApplyChanges(solution));
                var project = workspace.CurrentSolution.GetProject(id)!;
                var compilation = (await project.GetCompilationAsync())!;
                Assert.DoesNotContain(compilation.GetDiagnostics(), issue => issue.Severity == DiagnosticSeverity.Error);
                var owner = compilation.GetTypeByMetadataName(defect == "wrong-class" ? "BridgeFixture.Other" : "BridgeFixture.View")!;
                var field = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(owner.GetMembers("Input")));
                return new(workspace, directory, project, compilation, field, new(sourcePath, text));
            }
            catch { workspace.Dispose(); Directory.Delete(directory, true); throw; }
        }

        public ValueTask DisposeAsync() { _workspace.Dispose(); Directory.Delete(_directory, true); return ValueTask.CompletedTask; }
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
