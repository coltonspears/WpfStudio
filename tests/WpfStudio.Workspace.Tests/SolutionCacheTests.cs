using WpfStudio.Contracts;
using WpfStudio.Workspace;

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
        }
        // The worker saves the model after answering; wait for it rather than for a fixed time.
        var cache = Path.Combine(_root, "cache");
        for (var attempt = 0; attempt < 100 && !(Directory.Exists(cache) && Directory.EnumerateFiles(cache, "*.json").Any()); attempt++) await Task.Delay(100);
        Assert.NotEmpty(Directory.EnumerateFiles(cache, "*.json"));

        await using var client = new WorkspaceClient();
        var cached = await client.LoadAsync(new LoadWorkspaceRequest(project, UseCache: true));
        Assert.True(cached.Cached);
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
    public async Task AChangedProjectFileIgnoresTheSavedModel()
    {
        var project = Path.Combine(_root, "Lib", "Lib.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_root, "Lib", "Value.cs"), "namespace Lib; public sealed class Value { }");
        Assert.Equal(0, (await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(Path.GetDirectoryName(project)!, "restore", project), CancellationToken.None)).ExitCode);
        await using (var first = new WorkspaceClient()) await first.LoadAsync(new LoadWorkspaceRequest(project));
        var cache = Path.Combine(_root, "cache");
        for (var attempt = 0; attempt < 100 && !(Directory.Exists(cache) && Directory.EnumerateFiles(cache, "*.json").Any()); attempt++) await Task.Delay(100);

        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>Changed</RootNamespace></PropertyGroup></Project>");
        await using var client = new WorkspaceClient();
        Assert.False((await client.LoadAsync(new LoadWorkspaceRequest(project, UseCache: true))).Cached);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE", _previousCache);
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
