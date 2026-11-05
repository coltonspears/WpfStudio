using System.Diagnostics;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

/// <summary>Go to definition from one project into the source of a project it references, and onward from there.</summary>
public sealed class CrossProjectNavigationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WpfStudio-CrossProject-" + Guid.NewGuid().ToString("N"));
    private string App => Path.Combine(_root, "App", "App.csproj");
    private string Library => Path.Combine(_root, "Library", "Library.csproj");
    private string Use => Path.Combine(_root, "App", "Use.cs");
    private string Catalog => Path.Combine(_root, "Library", "Catalog.cs");
    private string Helper => Path.Combine(_root, "Library", "Helper.cs");
    private const string UseText = "using Library;\nnamespace App;\npublic class Use\n{\n    public int Run(Catalog catalog) => new Catalog().Count() + catalog.Size;\n}\n";
    private const string CatalogText = "namespace Library;\npublic class Catalog\n{\n    public int Size { get; set; }\n    public int Count() => Helper.One();\n}\n";
    private const string HelperText = "namespace Library;\npublic static class Helper\n{\n    public static int One() => 1;\n}\n";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(App)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Library)!);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch (IOException) { } return Task.CompletedTask; }

    private async Task<string> WriteAsync(string libraryFrameworks, bool listLibrary = true)
    {
        var frameworks = libraryFrameworks.Contains(';') ? $"<TargetFrameworks>{libraryFrameworks}</TargetFrameworks>" : $"<TargetFramework>{libraryFrameworks}</TargetFramework>";
        await File.WriteAllTextAsync(Library, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{frameworks}<LangVersion>latest</LangVersion></PropertyGroup></Project>");
        await File.WriteAllTextAsync(App, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Library\\Library.csproj\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(Use, UseText);
        await File.WriteAllTextAsync(Catalog, CatalogText);
        await File.WriteAllTextAsync(Helper, HelperText);
        var solution = Path.Combine(_root, "Shop.slnx");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"App/App.csproj\" />" + (listLibrary ? "<Project Path=\"Library/Library.csproj\" />" : "") + "</Solution>");
        using var restore = Process.Start(new ProcessStartInfo("dotnet", "restore App.csproj --nologo -v q") { WorkingDirectory = Path.GetDirectoryName(App)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await restore.WaitForExitAsync();
        Assert.Equal(0, restore.ExitCode);
        return solution;
    }

    [Theory]
    [InlineData("net10.0", true, false)]
    [InlineData("netstandard2.0;net10.0", true, false)]
    [InlineData("netstandard2.0", true, false)]
    [InlineData("net10.0", false, false)]
    [InlineData("net10.0", true, true)]
    public async Task DefinitionsFollowProjectReferencesIntoTheirSourceAndOnward(string libraryFrameworks, bool listLibrary, bool openProject)
    {
        var solution = await WriteAsync(libraryFrameworks, listLibrary);
        await using var engine = new WorkspaceClient();
        var snapshot = await engine.LoadAsync(new LoadWorkspaceRequest(openProject ? App : solution));
        Assert.True((await engine.UpdateDocumentAsync(new(Use, UseText, 1, Analyze: false))).Accepted, string.Join(" | ", snapshot.Issues.Select(issue => issue.Message)));

        // A method, a type named by a constructor call, a parameter's type and a property, all declared in the referenced project.
        await AssertGoesToAsync(engine, Use, UseText, "Count()", 2, Catalog, CatalogText.IndexOf("Count()", StringComparison.Ordinal));
        await AssertGoesToAsync(engine, Use, UseText, "Catalog()", 2, Catalog, CatalogText.IndexOf("Catalog", StringComparison.Ordinal));
        await AssertGoesToAsync(engine, Use, UseText, "Catalog catalog", 2, Catalog, CatalogText.IndexOf("Catalog", StringComparison.Ordinal));
        await AssertGoesToAsync(engine, Use, UseText, "Size;", 1, Catalog, CatalogText.IndexOf("Size", StringComparison.Ordinal));
        // The caret rests after the last character of a name as often as inside it.
        await AssertGoesToAsync(engine, Use, UseText, "Count()", "Count".Length, Catalog, CatalogText.IndexOf("Count()", StringComparison.Ordinal));
        await AssertGoesToAsync(engine, Use, UseText, "Size;", "Size".Length, Catalog, CatalogText.IndexOf("Size", StringComparison.Ordinal));

        // The file it arrived in is opened as a buffer, and navigation continues from there.
        Assert.True((await engine.UpdateDocumentAsync(new(Catalog, CatalogText, 1, Analyze: false))).Accepted);
        await AssertGoesToAsync(engine, Catalog, CatalogText, "One()", 1, Helper, HelperText.IndexOf("One()", StringComparison.Ordinal));
        await AssertGoesToAsync(engine, Catalog, CatalogText, "Helper.", 3, Helper, HelperText.IndexOf("Helper", StringComparison.Ordinal));
    }

    private static async Task AssertGoesToAsync(WorkspaceClient engine, string path, string text, string marker, int into, string expectedPath, int expectedStart)
    {
        var definitions = await engine.GetDefinitionAsync(new DocumentPositionRequest(path, text.IndexOf(marker, StringComparison.Ordinal) + into, 1));
        Assert.True(definitions.Count > 0, $"No definition for '{marker}' +{into} in {Path.GetFileName(path)}");
        Assert.Equal(expectedPath, definitions[0].Path, ignoreCase: true);
        Assert.Equal(expectedStart, definitions[0].Start);
    }
}
