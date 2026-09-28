using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlNameIntegrationTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task NamedElementAuthoringUsesRealWpfMetadataAndCurrentBuffers(string framework)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlNames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Fixture.csproj");
        string view = Path.Combine(directory, "View.xaml");
        const string text = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Fixture.View">
                <Grid>
                    <TextBox x:Name="CustomerName" Text="Ada" />
                    <TextBlock Text="{Binding ElementName=CustmerName, Path=Text}" />
                </Grid>
            </UserControl>
            """;
        try
        {
            await File.WriteAllTextAsync(project, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{{framework}}</TargetFramework>
                    <UseWPF>true</UseWPF>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(view, text);
            await File.WriteAllTextAsync(Path.Combine(directory, "View.xaml.cs"),
                "namespace Fixture; public partial class View : System.Windows.Controls.UserControl { }");
            var restore = await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"));
            Assert.Equal(0, restore.ExitCode);
            string? hostPath = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST");
            if (hostPath is not null) Assert.True(File.Exists(hostPath), "The selected packaged workspace worker must exist.");
            await using var client = new WorkspaceClient(hostPath);
            var loaded = await client.LoadAsync(new(project, "Release"));
            Assert.DoesNotContain(loaded.Issues, issue => issue.Severity == "Error");

            var analysis = await client.AnalyzeXamlAsync(new(view, text, 11, project));
            Assert.True(analysis.Accepted, analysis.Status);
            var diagnostic = Assert.Single(analysis.Diagnostics);
            Assert.StartsWith("XAMLNAME", diagnostic.Id);
            Assert.Equal("CustmerName", text.Substring(diagnostic.Start, diagnostic.Length));
            Assert.Equal("Warning", diagnostic.Severity);

            var completion = await client.GetXamlCompletionsAsync(new(view, text, diagnostic.Start + 4, 11, project));
            Assert.True(completion.Available, completion.Status);
            Assert.NotNull(completion.Completion);
            Assert.Equal(diagnostic.Start, completion.Completion.Start);
            Assert.Equal(diagnostic.Length, completion.Completion.Length);
            Assert.Contains(completion.Completion.Items, item => item.DisplayText == "CustomerName");

            var action = Assert.Single(await client.GetXamlCodeActionsAsync(new(view, text, diagnostic.Start + 1, 11, project)));
            Assert.Equal(view, action.Edit.Path);
            Assert.Equal(11, action.Edit.Version);
            Assert.Equal(Hash(text), action.Edit.ExpectedTextHash);
            var edit = Assert.Single(action.Edit.Edits);
            Assert.Equal("CustomerName", edit.NewText);
            string corrected = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
            Assert.Empty((await client.AnalyzeXamlAsync(new(view, corrected, 12, project))).Diagnostics);
            int reference = corrected.IndexOf("ElementName=CustomerName", StringComparison.Ordinal) + "ElementName=".Length;
            var definition = Assert.Single(await client.GetXamlDefinitionAsync(new(view, corrected, reference + 2, 12, project)));
            Assert.Equal(view, definition.Path);
            Assert.Equal(corrected.IndexOf("CustomerName", StringComparison.Ordinal), definition.Start);
            Assert.Equal("CustomerName", corrected.Substring(definition.Start, definition.Length));
            var hover = await client.GetXamlHoverAsync(new(view, corrected, reference + 2, 12, project));
            Assert.NotNull(hover);
            Assert.Contains("CustomerName", hover.Text);
            Assert.Contains("TextBox", hover.Text);

            // The Name alias is proven by the framework's RuntimeNameProperty metadata.
            string alias = corrected.Replace("x:Name=", "Name=", StringComparison.Ordinal);
            Assert.Empty((await client.AnalyzeXamlAsync(new(view, alias, 13, project))).Diagnostics);
            int pathStart = alias.IndexOf("Path=Text", StringComparison.Ordinal) + "Path=".Length;
            var pathCompletion = await client.GetXamlCompletionsAsync(new(view, alias, pathStart + 2, 13, project));
            Assert.NotNull(pathCompletion.Completion);
            Assert.Contains(pathCompletion.Completion.Items, item => item.DisplayText == "Text");

            var diskBatch = await client.AnalyzeXamlProjectAsync(new(21, [], project));
            Assert.True(diskBatch.Accepted, diskBatch.Status);
            var disk = Assert.Single(diskBatch.Files);
            Assert.Null(disk.Version);
            Assert.Equal(Hash(text), disk.TextHash);
            Assert.Equal(diagnostic.Id, Assert.Single(disk.Diagnostics).Id);
            var openBatch = await client.AnalyzeXamlProjectAsync(new(22, [new(view, corrected, 12)], project));
            Assert.True(openBatch.Accepted, openBatch.Status);
            var open = Assert.Single(openBatch.Files);
            Assert.Equal(12, open.Version);
            Assert.Equal(Hash(corrected), open.TextHash);
            Assert.Empty(open.Diagnostics);
            Assert.Equal(text, await File.ReadAllTextAsync(view));

            // Renaming only the declaration in an unsaved buffer must invalidate the old lookup.
            string changed = corrected.Replace("x:Name=\"CustomerName\"", "x:Name=\"OrderName\"", StringComparison.Ordinal);
            var changedAnalysis = await client.AnalyzeXamlAsync(new(view, changed, 14, project));
            Assert.True(changedAnalysis.Accepted, changedAnalysis.Status);
            Assert.StartsWith("XAMLNAME", Assert.Single(changedAnalysis.Diagnostics).Id);
            int changedReference = changed.IndexOf("ElementName=CustomerName", StringComparison.Ordinal) + "ElementName=".Length;
            Assert.Empty(await client.GetXamlDefinitionAsync(new(view, changed, changedReference + 2, 14, project)));
            Assert.NotEmpty(await client.GetXamlDefinitionAsync(new(view, corrected, reference + 2, 12, project)));

            await client.RestartAsync();
            var replay = await client.AnalyzeXamlProjectAsync(new(23, [new(view, corrected, 12)], project));
            Assert.True(replay.Accepted, replay.Status);
            Assert.Empty(Assert.Single(replay.Files).Diagnostics);

            // The proposed fix remains ordinary authored XAML accepted by the real markup compiler.
            await File.WriteAllTextAsync(view, corrected);
            var build = await new BuildService().RunAsync(new(project, BuildOperation.Build, "Release"));
            Assert.Equal(0, build.ExitCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
