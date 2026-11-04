using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

public sealed class SolutionCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("WpfStudio-SolutionCache-").FullName;
    private readonly string? _previousCache = Environment.GetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE");

    public SolutionCacheTests() => Environment.SetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE", Path.Combine(_root, "cache"));

    [Fact]
    public async Task ASavedModelStartsLanguageServicesAndARefreshReplacesItWithoutLosingUnsavedText()
    {
        var project = Path.Combine(_root, "Shop", "Shop.csproj");
        var source = Path.Combine(_root, "Shop", "OrderStore.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(source, "namespace Shop;\npublic interface IStore { void Save(); }\npublic sealed class OrderStore : IStore { public void Save() { } }\n");
        var restore = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(Path.GetDirectoryName(project)!, "restore", project), CancellationToken.None);
        Assert.True(restore.ExitCode == 0, restore.Output + restore.Error);

        await using (var first = new WorkspaceClient())
        {
            var full = await first.LoadAsync(new LoadWorkspaceRequest(project, UseCache: true));
            Assert.False(full.Cached);
            await WaitForSavedModelAsync(project);
        }
        Assert.Null(await SolutionCache.ReadAsync(new LoadWorkspaceRequest(project), "0.0.0", CancellationToken.None));

        await using var client = new WorkspaceClient();
        var cached = await client.LoadAsync(new LoadWorkspaceRequest(project, UseCache: true));
        Assert.True(cached.Cached);
        Assert.DoesNotContain(cached.Issues, issue => issue.Message.Contains("out of date", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(cached.Projects).Files, file => file.Path == source);
        var text = await File.ReadAllTextAsync(source) + "public sealed class Unsaved : IStore { public void Save() { } }\n";
        await client.UpdateDocumentAsync(new(source, text, 1, Analyze: false));
        Assert.Contains(await client.SearchSymbolsAsync(new("OrderSto")), symbol => symbol.Name == "OrderStore");

        var refreshed = await client.RefreshAsync(new LoadWorkspaceRequest(project));
        Assert.False(refreshed.Cached);
        // The open buffer is replayed into the MSBuild-loaded solution.
        var implementations = await client.FindImplementationsAsync(new(source, text.IndexOf("void Save", StringComparison.Ordinal) + 6, 1));
        Assert.Equal(2, implementations.Count);
    }

    [Fact]
    public async Task AnOutOfDateSavedModelStillStartsAndTheRefreshCorrectsIt()
    {
        var project = Path.Combine(_root, "Lib", "Lib.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_root, "Lib", "Value.cs"), "namespace Lib; public sealed class Value { }");
        Assert.Equal(0, (await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(Path.GetDirectoryName(project)!, "restore", project), CancellationToken.None)).ExitCode);
        await using (var first = new WorkspaceClient())
        {
            await first.LoadAsync(new LoadWorkspaceRequest(project));
            await WaitForSavedModelAsync(project);
        }

        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>Changed</RootNamespace></PropertyGroup></Project>");
        var added = Path.Combine(_root, "Lib", "Added.cs");
        await File.WriteAllTextAsync(added, "namespace Lib; public sealed class Added { }");
        await using var client = new WorkspaceClient();
        var stale = await client.LoadAsync(new LoadWorkspaceRequest(project, UseCache: true));
        Assert.True(stale.Cached);
        Assert.Contains(stale.Issues, issue => issue.Message.Contains("out of date", StringComparison.Ordinal));
        Assert.DoesNotContain(Assert.Single(stale.Projects).Files, file => file.Path == added);
        Assert.Contains(await client.SearchSymbolsAsync(new("Value")), symbol => symbol.Name == "Value");

        var refreshed = await client.RefreshAsync(new LoadWorkspaceRequest(project));
        Assert.False(refreshed.Cached);
        Assert.Contains(Assert.Single(refreshed.Projects).Files, file => file.Path == added);
        Assert.Contains(await client.SearchSymbolsAsync(new("Added")), symbol => symbol.Name == "Added");
    }

    [Fact]
    public async Task AProjectMSBuildCannotOpenDoesNotTakeItsAnswersAwayFromTheSavedModel()
    {
        var solution = Path.Combine(_root, "Shop.slnx");
        var broken = Path.Combine(_root, "Broken", "Broken.csproj");
        var healthy = Path.Combine(_root, "Healthy", "Healthy.csproj");
        foreach (var project in new[] { broken, healthy })
        {
            var name = Path.GetFileNameWithoutExtension(project);
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(_root, name, name + "Type.cs"), $"namespace {name}; public sealed class {name}Type {{ }}");
            Assert.Equal(0, (await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(_root, "restore", project), CancellationToken.None)).ExitCode);
        }
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Broken/Broken.csproj\" /><Project Path=\"Healthy/Healthy.csproj\" /></Solution>");
        await using (var first = new WorkspaceClient())
        {
            await first.LoadAsync(new LoadWorkspaceRequest(solution));
            await WaitForSavedModelAsync(solution);
        }

        await File.WriteAllTextAsync(broken, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>");
        await using var client = new WorkspaceClient();
        Assert.True((await client.LoadAsync(new LoadWorkspaceRequest(solution, UseCache: true))).Cached);
        await Assert.ThrowsAnyAsync<Exception>(() => client.RefreshAsync(new LoadWorkspaceRequest(solution)));

        Assert.Contains(await client.SearchSymbolsAsync(new("BrokenType")), symbol => symbol.Name == "BrokenType");
        Assert.Contains(await client.SearchSymbolsAsync(new("HealthyType")), symbol => symbol.Name == "HealthyType");
    }

    [Fact]
    public async Task ASolutionThatLostAndGainedProjectsStillStartsFromItsSavedModel()
    {
        var solution = Path.Combine(_root, "Shop.slnx");
        string Project(string name, string body = "") => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{body}</Project>";
        async Task<string> WriteProjectAsync(string name, string body = "")
        {
            var path = Path.Combine(_root, name, name + ".csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, Project(name, body));
            await File.WriteAllTextAsync(Path.Combine(_root, name, name + "Type.cs"), $"namespace {name}; public sealed class {name}Type {{ }}");
            return path;
        }
        var kept = await WriteProjectAsync("Kept", "<ItemGroup><ProjectReference Include=\"..\\Gone\\Gone.csproj\" /></ItemGroup>");
        var gone = await WriteProjectAsync("Gone");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Kept/Kept.csproj\" /><Project Path=\"Gone/Gone.csproj\" /></Solution>");
        Assert.Equal(0, (await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(_root, "restore", kept), CancellationToken.None)).ExitCode);
        await using (var first = new WorkspaceClient())
        {
            await first.LoadAsync(new LoadWorkspaceRequest(solution));
            await WaitForSavedModelAsync(solution);
        }

        Directory.Delete(Path.GetDirectoryName(gone)!, true);
        await WriteProjectAsync("Kept");
        var added = await WriteProjectAsync("Added");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Kept/Kept.csproj\" /><Project Path=\"Added/Added.csproj\" /></Solution>");
        Assert.Equal(0, (await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(_root, "restore", added), CancellationToken.None)).ExitCode);
        await using var client = new WorkspaceClient();
        var stale = await client.LoadAsync(new LoadWorkspaceRequest(solution, UseCache: true));
        Assert.True(stale.Cached);
        Assert.Equal(["Added", "Kept"], stale.Projects.Select(project => project.Name).Order());
        Assert.Contains(await client.SearchSymbolsAsync(new("KeptType")), symbol => symbol.Name == "KeptType");

        var refreshed = await client.RefreshAsync(new LoadWorkspaceRequest(solution));
        Assert.Equal(["Added", "Kept"], refreshed.Projects.Select(project => project.Name).Order());
        Assert.Contains(await client.SearchSymbolsAsync(new("AddedType")), symbol => symbol.Name == "AddedType");
    }

    // The worker saves the model after answering the load, so it must stay alive until the file appears.
    // Other test classes may start workers while this class's cache folder is set, so wait for this project's file.
    private static async Task WaitForSavedModelAsync(string project)
    {
        var saved = SolutionCache.FileFor(new LoadWorkspaceRequest(project));
        for (var attempt = 0; attempt < 600 && !File.Exists(saved); attempt++) await Task.Delay(100);
        Assert.True(File.Exists(saved), "The worker did not save the project model.");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE", _previousCache);
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
