using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

public sealed class CodeLensIntegrationTests
{
    private const string Model = """
        using CommunityToolkit.Mvvm.ComponentModel;
        using CommunityToolkit.Mvvm.Input;
        namespace Fixture;
        public partial class ViewModel : ObservableObject
        {
            [ObservableProperty] private string _title = "";
            public string Name { get; set; } = "";
            [RelayCommand] private void Save() { }
            [RelayCommand] private async System.Threading.Tasks.Task LoadAsync() { await System.Threading.Tasks.Task.Yield(); }
            public void Use() { Name = Title; Name = Name.Trim(); }
            private int Unused() => 0;
        }
        """;
    private const string Markup = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:vm="clr-namespace:Fixture"
                x:Class="Fixture.View" d:DataContext="{d:DesignInstance vm:ViewModel}">
          <StackPanel>
            <TextBlock Text="{Binding Title}"/>
            <TextBox Text="{Binding Name}"/>
            <TextBlock Text="{Binding Name}"/>
            <Button Command="{Binding SaveCommand}" Click="OnHelp"/>
            <Button Command="{Binding LoadCommand}"/>
          </StackPanel>
        </Window>
        """;
    private const string CodeBehind = "using System.Windows; namespace Fixture; public partial class View : Window { public View() { InitializeComponent(); } private void OnHelp(object sender, RoutedEventArgs e) { } }";

    [Fact]
    public async Task LensesCountCSharpAndXamlUsesIncludingToolkitGeneratedMembers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-CodeLens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = Path.Combine(directory, "Fixture.csproj");
            await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup>"
                + "<ItemGroup><PackageReference Include='CommunityToolkit.Mvvm' Version='8.4.2'/></ItemGroup></Project>");
            var model = Path.Combine(directory, "ViewModel.cs"); var view = Path.Combine(directory, "View.xaml"); var code = view + ".cs";
            await File.WriteAllTextAsync(model, Model); await File.WriteAllTextAsync(view, Markup); await File.WriteAllTextAsync(code, CodeBehind);
            Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
            await using var client = new WorkspaceClient();
            await client.LoadAsync(new(project));
            Assert.True((await client.UpdateDocumentAsync(new(model, Model, 1, Analyze: false))).Accepted);
            var lenses = await client.GetCodeLensAsync(new(model, 1));
            CodeLensEntry Lens(string name) => Assert.Single(lenses.Entries, entry => entry.Name == name);

            Assert.Equal((1, "Title"), (Lens("_title").XamlReferences, Lens("_title").XamlName));
            Assert.True(Lens("_title").References >= 1); // Use() reads the generated Title property.
            Assert.Equal(2, Lens("Name").XamlReferences);
            Assert.Equal(3, Lens("Name").References);
            Assert.Equal((1, "SaveCommand"), (Lens("Save").XamlReferences, Lens("Save").XamlName));
            Assert.Equal((1, "LoadCommand"), (Lens("LoadAsync").XamlReferences, Lens("LoadAsync").XamlName));
            Assert.Equal((0, 0), (Lens("Unused").References, Lens("Unused").XamlReferences));
            Assert.Equal(Model.Split('\n').ToList().FindIndex(line => line.Contains("Unused")) + 1, Lens("Unused").Line);

            Assert.True((await client.UpdateDocumentAsync(new(code, CodeBehind, 1, Analyze: false))).Accepted);
            var handler = Assert.Single((await client.GetCodeLensAsync(new(code, 1))).Entries, entry => entry.Name == "OnHelp");
            Assert.Equal(1, handler.XamlReferences);
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
