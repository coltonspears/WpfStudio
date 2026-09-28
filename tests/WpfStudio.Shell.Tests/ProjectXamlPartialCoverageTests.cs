using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class ProjectXamlPartialCoverageTests
{
    private const string Markup = "<View xmlns:vm=\"clr-namespace:Demo\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" d:DataContext=\"{d:DesignInstance vm:Model}\"><Label Text=\"{Binding Name}\" /></View>";

    [Fact]
    public async Task WatchedModelChangeRestoresHealthyEditorWhileAnotherProjectRemainsUnavailable()
    {
        await using var test = new ShellTestContext();
        var solution = await test.CreateFileAsync("Fixture.slnx",
            "<Solution><Project Path=\"Unavailable/Unavailable.csproj\"/><Project Path=\"Healthy/Healthy.csproj\"/></Solution>");
        var models = new Dictionary<string, string>();
        var views = new Dictionary<string, string>();
        foreach (var name in new[] { "Unavailable", "Healthy" })
        {
            var project = await test.CreateFileAsync($"{name}/{name}.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            models[name] = Path.GetFullPath(await test.CreateFileAsync($"{name}/Model.cs",
                "namespace Demo; public class Model { public string Name => \"original\"; }"));
            views[name] = Path.GetFullPath(await test.CreateFileAsync($"{name}/View.xaml", Markup));
            Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        }
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(solution));
        await test.Shell.OpenDocumentAsync(views["Healthy"]);
        var editor = test.Shell.ActiveDocument!;
        int position = Markup.IndexOf("Name}", StringComparison.Ordinal);
        await test.Shell.RefreshProjectXamlAnalysisAsync();
        Assert.Contains((await editor.CompleteAsync(position)).Items, item => item.DisplayText == "Name");

        // Make A unavailable without changing project membership. Then a real
        // watched write in B must enter the global pending state before refresh.
        await using var locked = new FileStream(models["Unavailable"], FileMode.Open, FileAccess.Read, FileShare.None);
        var unavailable = await test.Workspace.RefreshDiskDocumentsAsync(new([models["Unavailable"]]));
        Assert.True(unavailable.Accepted, unavailable.Status);
        Assert.True(unavailable.Truncated);
        Assert.Equal(0, unavailable.PendingFiles);
        await File.WriteAllTextAsync(models["Healthy"],
            "namespace Demo; public class Model { public string Name => \"updated\"; public string Label => \"new member\"; }");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (editor.LanguageStatus != "Checking changed project source files…") await Task.Delay(10, deadline.Token);
        Assert.Empty((await editor.CompleteAsync(position)).Items);

        await test.Shell.RefreshProjectXamlAnalysisAsync();

        var completions = await editor.CompleteAsync(position);
        Assert.Contains(completions.Items, item => item.DisplayText == "Name");
        Assert.Contains(completions.Items, item => item.DisplayText == "Label");
        await editor.RefreshAnalysisAsync();
        Assert.Empty(editor.Diagnostics);
        Assert.DoesNotContain("Checking changed", editor.LanguageStatus);
        Assert.Contains("1 unavailable", test.Shell.XamlAnalysisStatus);
        Assert.Contains("partial coverage", test.Shell.XamlAnalysisStatus);
        var batch = await test.Workspace.AnalyzeXamlProjectAsync(new(100, [new(editor.State.Path, editor.State.Content, editor.State.Version)]));
        Assert.True(batch.Accepted, batch.Status);
        Assert.Equal("Unavailable", Assert.Single(batch.Files, file => file.ProjectName == "Unavailable").State);
        Assert.Equal("Analyzed", Assert.Single(batch.Files, file => file.ProjectName == "Healthy").State);
        var affected = await test.Workspace.AnalyzeXamlAsync(new(views["Unavailable"], Markup, 1));
        Assert.False(affected.Accepted);
        Assert.NotNull(affected.Status);
        Assert.Equal("Healthy", editor.XamlProject?.Name);
        Assert.Equal(Markup, editor.State.Content);
    }
}
