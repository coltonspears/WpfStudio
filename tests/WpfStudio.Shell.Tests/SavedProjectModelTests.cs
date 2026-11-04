using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class SavedProjectModelTests
{
    [Fact]
    public async Task AFailedRefreshKeepsTheSavedModelAnsweringAndTheStatusSaysSo()
    {
        await using var test = new ShellTestContext();
        var previous = Environment.GetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE");
        var cache = Path.Combine(test.Root, "cache");
        Environment.SetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE", cache);
        try
        {
            var solution = await test.CreateFileAsync("Fixture.slnx", "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Library/Library.csproj\" /></Solution>");
            var app = await test.CreateFileAsync("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await test.CreateFileAsync("App/Program.cs", "System.Console.WriteLine();");
            var library = await test.CreateFileAsync("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await test.CreateFileAsync("Library/OrderStore.cs", "namespace Demo; public sealed class OrderStore { }");
            foreach (var project in new[] { app, library }) Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
            await test.Shell.LoadWorkspaceAsync(solution);
            Assert.False(test.Shell.Workspace!.Cached);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            // The worker saves the model after answering. Other tests' workers may save here too, so look for this fixture's.
            while (!Directory.Exists(cache) || !Directory.EnumerateFiles(cache, "*.json").Any(file => File.ReadAllText(file).Contains(Path.GetFileName(test.Root), StringComparison.Ordinal)))
                await Task.Delay(100, deadline.Token);

            await File.WriteAllTextAsync(library, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>");
            await test.Shell.LoadWorkspaceAsync(solution);
            Assert.True(test.Shell.Workspace!.Cached);
            while (!test.Shell.Status.Contains("refresh failed", StringComparison.Ordinal)) await Task.Delay(50, deadline.Token);

            Assert.True(test.Shell.Workspace!.Cached);
            Assert.Contains("Project model refresh:", test.Shell.Output, StringComparison.Ordinal);
            Assert.Contains(await test.Workspace.SearchSymbolsAsync(new("OrderSto")), symbol => symbol.Name == "OrderStore");
        }
        finally { Environment.SetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE", previous); }
    }
}
