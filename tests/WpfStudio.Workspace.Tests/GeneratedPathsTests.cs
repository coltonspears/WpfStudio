namespace WpfStudio.Workspace.Tests;

public sealed class GeneratedPathsTests : IDisposable
{
    // A workspace cloned under a folder that is itself named obj.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WpfStudio-Generated-" + Guid.NewGuid().ToString("N"), "obj", "Shop");

    private string File(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void AFolderNamedObjIsOutputOnlyWhereABuildPutsIt()
    {
        File("App", "App.csproj");
        // Source: under the outer obj folder, and in a folder of the project's own that is named Obj.
        Assert.False(GeneratedPaths.IsIntermediate(File("App", "Main.cs")));
        Assert.False(GeneratedPaths.IsIntermediate(File("App", "Models", "Obj", "MeshReader.cs")));
        Assert.False(ToolkitMembers.IsGeneratedPath(File("App", "Models", "Obj", "MeshReader.cs")));
        // Output: the folder beside the project file, restored or not, with either slash.
        var output = File("App", "obj", "Debug", "net10.0", "App.AssemblyInfo.cs");
        Assert.True(GeneratedPaths.IsIntermediate(output));
        Assert.True(GeneratedPaths.IsIntermediate(output.Replace('/', '\\')));
        // Output: one folder per project under a shared artifacts folder, which holds no project file itself.
        File("artifacts", "obj", "App", "project.assets.json");
        Assert.True(GeneratedPaths.IsIntermediate(File("artifacts", "obj", "App", "debug", "App.GlobalUsings.g.cs")));
        // A compiler's file is output by its name wherever it is.
        Assert.True(ToolkitMembers.IsGeneratedPath(Path.Combine(_root, "App", "View.g.i.cs")));
    }

    [Fact]
    public void AFolderThatIsNotOnDiskIsJudgedByItsNameAlone()
    {
        File("Kept.cs"); // So that the outer obj folder is there to be judged, and found to be no build's.
        Assert.True(GeneratedPaths.IsIntermediate(Path.Combine(_root, "Gone", "obj", "Debug", "Gone.cs")));
        Assert.False(GeneratedPaths.IsIntermediate(Path.Combine(_root, "Gone", "object", "Gone.cs")));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root))!, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
