using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace WpfStudio.Workspace.Tests;

/// <summary>An in-memory C# project whose first document marks the position under test with <c>$$</c>.</summary>
internal sealed class RoslynFixture : IDisposable
{
    private static readonly IReadOnlyList<MetadataReference> PlatformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    private RoslynFixture(AdhocWorkspace workspace, Document document, int position) { Workspace = workspace; Document = document; Position = position; }
    public AdhocWorkspace Workspace { get; }
    public Document Document { get; }
    public int Position { get; }
    public Solution Solution => Document.Project.Solution;

    public static RoslynFixture Create(params string[] sources)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Fixture", "Fixture", LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            metadataReferences: PlatformReferences));
        Document? first = null;
        var position = -1;
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            var marker = source.IndexOf("$$", StringComparison.Ordinal);
            if (marker >= 0) { position = marker; source = source.Remove(marker, 2); }
            var path = Path.Combine(Path.GetTempPath(), "WpfStudio.Fixture", $"Source{i}.cs");
            var info = DocumentInfo.Create(DocumentId.CreateNewId(project.Id), Path.GetFileName(path),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), path)), filePath: path);
            var document = workspace.AddDocument(info);
            if (marker >= 0 || first is null) first = marker >= 0 ? document : first ?? document;
        }
        // Return the document holding the marker, from the workspace's final solution.
        var final = workspace.CurrentSolution.GetDocument(first!.Id)!;
        return new(workspace, final, Math.Max(0, position));
    }

    public void Dispose() => Workspace.Dispose();
}
