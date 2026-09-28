using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlCompletionTests
{
    private static XamlCompletionService Create() => new()
    {
        Index = new WpfIndexSnapshot([], [new ResourceDeclaration("AccentBrush", "App.xaml", 1, 0, 11, "Application")], [], [], new Dictionary<string, string>
        {
            ["CustomerViewModel.cs"] = "namespace Demo; public partial class CustomerViewModel { [ObservableProperty] private string _title; public partial Customer Selected { get; set; } [CommunityToolkit.Mvvm.Input.RelayCommand] private async Task RefreshAsync() { } }",
            ["Customer.cs"] = "namespace Demo; public class Customer { public string Name { get; set; } }"
        })
    };
    [Fact]
    public void OfflineDesignDataContextDoesNotInferMembersFromCSharpText()
    {
        const string source = "<Window xmlns:vm=\"clr-namespace:Demo\" d:DataContext=\"{d:DesignInstance Type=vm:CustomerViewModel}\"><TextBlock Text=\"{Binding ";
        var result = Create().Complete("Window.xaml", source, source.Length, 3);
        Assert.Empty(result.Items);
        Assert.Equal(3, result.Version);
    }
    [Fact]
    public void OfflineNestedBindingRetainsTheFinalMemberSpanWithoutGuessing()
    {
        const string source = "<Window xmlns:vm=\"clr-namespace:Demo\"><Window.DataContext><vm:CustomerViewModel /></Window.DataContext><TextBlock Text=\"{Binding Selected.Na";
        var result = Create().Complete("Window.xaml", source, source.Length, 1);
        Assert.Empty(result.Items);
        Assert.Equal(source.Length - 2, result.Start);
        Assert.Equal(2, result.Length);
    }
    [Fact]
    public void UnknownRuntimeDataContextDoesNotInventBindings()
    {
        const string source = "<Window DataContext=\"{Binding Current}\"><TextBlock Text=\"{Binding ";
        Assert.Empty(Create().Complete("Window.xaml", source, source.Length, 1).Items);
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding Path = ")]
    [InlineData("<TextBlock Text=\"{Binding ElementName=Other, Path=")]
    [InlineData("<TextBlock Text=\"{Binding RelativeSource={RelativeSource Self}, ")]
    [InlineData("<TextBlock.Text><Binding Path=\"")]
    public void OfflineBindingContextsDoNotFallBackToControlProperties(string source)
    {
        Assert.Empty(Create().Complete("Window.xaml", source, source.Length, 1).Items);
    }

    [Theory]
    [InlineData("<But", "Button")]
    [InlineData("<Button Wid", "Width")]
    [InlineData("<Button Background=\"{StaticResource Ac", "AccentBrush")]
    [InlineData("<Button Background=\"{DynamicResource Ac", "AccentBrush")]
    public void FrameworkAndResourceCompletionsRemainAvailable(string source, string expected)
    {
        Assert.Contains(Create().Complete("Window.xaml", source, source.Length, 1).Items, item => item.DisplayText == expected);
    }

    [Fact]
    public async Task EditorClearsObsoleteXamlDiagnosticsImmediatelyOnEdit()
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Binding.xaml"), "<TextBlock Text=\"{Binding Nmae}\" />");
        using var editor = new EditorViewModel(state, workspace, Create(), new InlineDispatcher(), _ => { });
        editor.Diagnostics.Add(new WorkspaceDiagnostic("XAML1001", "Unknown member", "Warning", state.Path, 1, 27, 26, 4));

        state.Content = "<TextBlock Text=\"{Binding Name}\" />";

        Assert.Empty(editor.Diagnostics);
        await editor.RefreshAnalysisAsync();
        Assert.Contains("unavailable", editor.LanguageStatus);
    }

    [Fact]
    public async Task EditorUsesWorkerBindingDiagnosticsAndUnsavedCSharpCompletions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlEditor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Fixture.csproj");
            string viewModel = Path.Combine(directory, "CustomerViewModel.cs");
            string xaml = Path.Combine(directory, "View.xaml");
            const string source = "namespace Demo; public class CustomerViewModel { public string Name { get; set; } = \"\"; }";
            const string markup = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:vm=\"clr-namespace:Demo\" d:DataContext=\"{d:DesignInstance Type=vm:CustomerViewModel}\"><TextBlock Text=\"{Binding Nmae}\" /></Window>";
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(viewModel, source);
            await File.WriteAllTextAsync(xaml, markup);
            Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            await using var workspace = new WorkspaceClient();
            await workspace.LoadAsync(new LoadWorkspaceRequest(project));
            var state = new DocumentState(xaml, markup);
            using var editor = new EditorViewModel(state, workspace, Create(), new InlineDispatcher(), message => Assert.Fail(message));

            await editor.RefreshAnalysisAsync();

            var diagnostic = Assert.Single(editor.Diagnostics);
            Assert.Contains("Nmae", diagnostic.Message);
            Assert.Equal("Nmae", state.Content.Substring(diagnostic.Start, diagnostic.Length));

            // A C# update invalidates the old findings, even though the XAML version did not change.
            await workspace.UpdateDocumentAsync(new UpdateDocumentRequest(viewModel, source.Replace(" Name ", " Nmae ", StringComparison.Ordinal), 1, Analyze: false));
            Assert.Empty(editor.Diagnostics);
            await editor.RefreshAnalysisAsync();
            Assert.Empty(editor.Diagnostics);

            state.Content = markup.Replace("Nmae", "Nm", StringComparison.Ordinal);
            int position = state.Content.IndexOf("Nm}", StringComparison.Ordinal) + 2;
            var completions = await editor.CompleteAsync(position);
            Assert.Equal("Nmae", Assert.Single(completions.Items).DisplayText);
            Assert.Equal(position - 2, completions.Start);
            Assert.Equal(2, completions.Length);
            Assert.Equal(state.Version, completions.Version);

            // Disconnection clears findings and keeps framework suggestions usable.
            editor.Diagnostics.Add(diagnostic);
            await workspace.DisposeAsync();
            Assert.Empty(editor.Diagnostics);
            state.Content = "<But";
            Assert.Contains((await editor.CompleteAsync(state.Content.Length)).Items, item => item.DisplayText == "Button");
        }
        finally { Directory.Delete(directory, true); }
    }
}
