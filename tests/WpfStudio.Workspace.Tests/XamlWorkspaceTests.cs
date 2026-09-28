using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlWorkspaceTests
{
    [Fact]
    public async Task BindingIntelligenceUsesGeneratedSymbolsUnsavedTypesAndWorkerRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlWorkspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Fixture.csproj");
        var view = Path.Combine(directory, "View.xaml");
        var model = Path.Combine(directory, "ViewModel.cs");
        const string source = "using CommunityToolkit.Mvvm.ComponentModel; using CommunityToolkit.Mvvm.Input; namespace Fixture; public partial class ViewModel : ObservableObject { [ObservableProperty] private string name = \"Ada\"; [RelayCommand] private void Refresh() { } }";
        const string xaml = "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:vm=\"clr-namespace:Fixture\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\" d:DataContext=\"{d:DesignInstance vm:ViewModel}\"><TextBlock Text=\"{Binding Nmae}\" /></UserControl>";
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include=\"CommunityToolkit.Mvvm\" Version=\"8.4.0\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(view, xaml);
        await File.WriteAllTextAsync(model, source);
        try
        {
            Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            await using var client = new WorkspaceClient();
            var snapshot = await client.LoadAsync(new LoadWorkspaceRequest(project));
            Assert.DoesNotContain(snapshot.Issues, issue => issue.Severity == "Error");

            var analysis = await client.AnalyzeXamlAsync(new(view, xaml, 1));
            Assert.True(analysis.Accepted, analysis.Status);
            var issue = Assert.Single(analysis.Diagnostics);
            Assert.Equal("Nmae", xaml.Substring(issue.Start, issue.Length));
            Assert.Contains("Name", issue.Message);

            var typed = xaml[..xaml.IndexOf("Nmae", StringComparison.Ordinal)];
            var completions = await client.GetXamlCompletionsAsync(new(view, typed, typed.Length, 2));
            Assert.True(completions.Available, completions.Status);
            Assert.NotNull(completions.Completion);
            Assert.Contains(completions.Completion.Items, item => item.DisplayText == "Name");
            Assert.Contains(completions.Completion.Items, item => item.DisplayText == "RefreshCommand");

            var fixedXaml = xaml.Replace("Nmae", "Name", StringComparison.Ordinal);
            Assert.Empty((await client.AnalyzeXamlAsync(new(view, fixedXaml, 3))).Diagnostics);

            // The XAML buffer stays unchanged while its C# source acquires a new generated property.
            int semanticChanges = 0;
            client.SemanticStateChanged += (_, _) => Interlocked.Increment(ref semanticChanges);
            var unsaved = source.Replace("string name", "string title", StringComparison.Ordinal);
            Assert.True((await client.UpdateDocumentAsync(new(model, unsaved, 1, Analyze: false))).Accepted);
            Assert.True(semanticChanges > 0);
            int synchronizedChanges = semanticChanges;
            await client.UpdateDocumentAsync(new(model, unsaved, 1, Analyze: false));
            Assert.Equal(synchronizedChanges, semanticChanges);
            var changed = await client.AnalyzeXamlAsync(new(view, fixedXaml, 3));
            Assert.True(changed.Accepted, changed.Status);
            Assert.Equal("Name", fixedXaml.Substring(Assert.Single(changed.Diagnostics).Start, 4));
            var updated = (await client.GetXamlCompletionsAsync(new(view, typed, typed.Length, 4))).Completion!;
            Assert.Contains(updated.Items, item => item.DisplayText == "Title");
            Assert.DoesNotContain(updated.Items, item => item.DisplayText == "Name");

            await client.RestartAsync();
            Assert.Single((await client.AnalyzeXamlAsync(new(view, fixedXaml, 3))).Diagnostics);
            await client.CloseDocumentAsync(model);
            Assert.Empty((await client.AnalyzeXamlAsync(new(view, fixedXaml, 3))).Diagnostics);

            var unowned = await client.AnalyzeXamlAsync(new(Path.Combine(directory, "NotInProject.xaml"), xaml, 1));
            Assert.False(unowned.Accepted);
            Assert.Empty(unowned.Diagnostics);
            Assert.NotNull(unowned.Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LinkedXamlRequiresAnUnambiguousProjectContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlLinked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var view = Path.Combine(directory, "Shared.xaml");
        var solution = Path.Combine(directory, "Fixture.slnx");
        const string xaml = "<View xmlns:vm=\"clr-namespace:Fixture\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" d:DataContext=\"{d:DesignInstance vm:ViewModel}\"><Label Text=\"{Binding Name}\" /></View>";
        await File.WriteAllTextAsync(view, xaml);
        var projects = new List<string>();
        foreach (var name in new[] { "First", "Second" })
        {
            var folder = Path.Combine(directory, name);
            Directory.CreateDirectory(folder);
            var project = Path.Combine(folder, name + ".csproj");
            projects.Add(project);
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><None Include=\"../Shared.xaml\" Link=\"Shared.xaml\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(folder, "ViewModel.cs"), $"namespace Fixture; public class ViewModel {{ public string {(name == "First" ? "Name" : "Title")} => \"value\"; }}");
        }
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"First/First.csproj\" /><Project Path=\"Second/Second.csproj\" /></Solution>");
        try
        {
            // Restore projects independently so a reusable solution-build node cannot
            // retain the redirected restore output stream after the parent exits.
            foreach (var project in projects)
                Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            await using var client = new WorkspaceClient();
            await client.LoadAsync(new LoadWorkspaceRequest(solution));
            var ambiguous = await client.AnalyzeXamlAsync(new(view, xaml, 1));
            Assert.False(ambiguous.Accepted);
            Assert.Contains("multiple project", ambiguous.Status);
            var first = await client.AnalyzeXamlAsync(new(view, xaml, 1, projects[0]));
            Assert.True(first.Accepted, first.Status);
            Assert.Empty(first.Diagnostics);
            var second = await client.AnalyzeXamlAsync(new(view, xaml, 1, projects[1]));
            Assert.True(second.Accepted, second.Status);
            Assert.Single(second.Diagnostics);
        }
        finally { Directory.Delete(directory, true); }
    }
}
