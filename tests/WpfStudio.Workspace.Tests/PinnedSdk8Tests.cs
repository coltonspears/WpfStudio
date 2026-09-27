using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class PinnedSdk8FactAttribute : FactAttribute
{
    public PinnedSdk8FactAttribute()
    {
        var root = Environment.GetEnvironmentVariable("WPFSTUDIO_DOTNET8_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "sdk")))
            Skip = "Set WPFSTUDIO_DOTNET8_ROOT and run under an isolated .NET host containing SDK 8 plus runtime 10.";
    }
}

public sealed class PinnedSdk8Tests
{
    [PinnedSdk8Fact]
    public async Task ActualSdk8BuildsAndLoadsWpfAndToolkitGeneratedMembers()
    {
        var toolchainRoot = Environment.GetEnvironmentVariable("WPFSTUDIO_DOTNET8_ROOT")!;
        var sdk = Directory.GetDirectories(Path.Combine(toolchainRoot, "sdk"), "8.*").Select(Path.GetFileName).Order(StringComparer.Ordinal).Last();
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), $"{{\"sdk\":{{\"version\":\"{sdk}\",\"rollForward\":\"disable\"}}}}");
        var project = Path.Combine(directory, "PinnedEight.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><LangVersion>12.0</LangVersion></PropertyGroup><ItemGroup><PackageReference Include=\"CommunityToolkit.Mvvm\" Version=\"8.4.0\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(directory, "View.xaml"), "<UserControl x:Class=\"PinnedEight.View\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><TextBlock x:Name=\"TitleLabel\" /></UserControl>");
        var path = Path.Combine(directory, "View.xaml.cs");
        const string source = "using System.Windows.Controls; namespace PinnedEight; public partial class View : UserControl { public View() { InitializeComponent(); TitleLabel.Text = new MainViewModel().Name; } }";
        await File.WriteAllTextAsync(path, source);
        await File.WriteAllTextAsync(Path.Combine(directory, "MainViewModel.cs"), "using CommunityToolkit.Mvvm.ComponentModel; namespace PinnedEight; public partial class MainViewModel : ObservableObject { [ObservableProperty] private string name = \"SDK eight\"; }");
        var selected = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--version"), CancellationToken.None);
        Assert.Equal(0, selected.ExitCode);
        Assert.Equal(sdk, selected.Output.Trim());
        var output = new List<string>();
        var result = await new BuildService().RunAsync(new BuildRequest(project), new DirectProgress<BuildOutputEvent>(line => { lock (output) output.Add(line.Text); }));
        Assert.True(result.ExitCode == 0, string.Join(Environment.NewLine, output));
        await using var client = new WorkspaceClient();
        var workspace = await client.LoadAsync(new LoadWorkspaceRequest(project));
        Assert.Equal(sdk, workspace.SdkVersion);
        Assert.DoesNotContain(workspace.Issues, issue => issue.Severity == "Error");
        var update = await client.UpdateDocumentAsync(new UpdateDocumentRequest(path, source, 1));
        Assert.True(update.Accepted);
        Assert.DoesNotContain(update.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        var changed = source.Replace(".Name", ".Na", StringComparison.Ordinal);
        await client.UpdateDocumentAsync(new UpdateDocumentRequest(path, changed, 2, Analyze: false));
        var completion = await client.GetCompletionsAsync(new DocumentPositionRequest(path, changed.IndexOf(".Na;", StringComparison.Ordinal) + 3, 2));
        Assert.Contains(completion.Items, item => item.DisplayText == "Name");
        Assert.Contains(Assert.Single(workspace.Projects).Files, file => file.IsGenerated && file.Name.EndsWith(".g.cs", StringComparison.Ordinal));
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
