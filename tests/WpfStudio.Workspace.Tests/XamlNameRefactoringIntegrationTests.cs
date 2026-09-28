using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlNameRefactoringIntegrationTests
{
    private const string Markup = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="NameFixture.View">
          <Window.Resources>
            <ControlTemplate x:Key="First" TargetType="Button">
              <StackPanel><TextBox x:Name="Shared"/><TextBlock Text="{Binding Text, ElementName=Shared}"/></StackPanel>
            </ControlTemplate>
            <ControlTemplate x:Key="Second" TargetType="Button">
              <StackPanel><TextBox x:Name="Shared"/><TextBlock Text="{Binding Text, ElementName=Shared}"/></StackPanel>
            </ControlTemplate>
          </Window.Resources>
          <StackPanel>
            <TextBox Name="Input"/>
            <TextBlock Text="{Binding Text, ElementName=Input}"/>
            <TextBox x:Name="Other"/>
          </StackPanel>
        </Window>
        """;
    private const string Code = """
        using System.Windows;
        namespace NameFixture;
        public partial class View : Window
        {
            public View() { InitializeComponent(); }
            public string Read() => Input.Text;
            public string NameCollision => "existing member";
        }
        """;

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task PageNamesAndGeneratedFieldUsesRenameTogetherFromBothLanguagesAndBuild(string framework)
    {
        await using var fixture = await Fixture.CreateAsync(framework);
        string unsaved = Code.Replace("Input.Text;", "Input.Text + Input.Text;", StringComparison.Ordinal);
        Assert.True((await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, unsaved, 7, Analyze: false))).Accepted);
        int xamlPosition = Markup.IndexOf("Name=\"Input\"", StringComparison.Ordinal) + 6;
        int codePosition = unsaved.IndexOf("Input.Text", StringComparison.Ordinal);
        var overlays = new[] { new XamlDocumentOverlay(fixture.XamlPath, Markup, 5) };
        var fromXaml = new SymbolReferenceRequest(fixture.XamlPath, xamlPosition, 5, Markup, fixture.Project, overlays);
        var fromCode = new SymbolReferenceRequest(fixture.CodePath, codePosition, 7, unsaved, fixture.Project, overlays);
        foreach (var request in new[] { fromXaml, fromCode })
        {
            var references = await fixture.Client.FindSymbolReferencesAsync(request);
            Assert.True(references.SymbolFound, string.Join("\n", references.Warnings));
            Assert.Equal(2, references.Locations.Count(location => location.Path == fixture.XamlPath));
            Assert.Equal(2, references.Locations.Count(location => location.Path == fixture.CodePath));
            Assert.All(references.Locations, location =>
            {
                Assert.False(IsGenerated(location.Path));
                Assert.Equal(fixture.Project, location.ProjectPath);
                Assert.Equal(Hash(location.Path == fixture.XamlPath ? Markup : unsaved), location.ExpectedTextHash);
                string text = location.Path == fixture.XamlPath ? Markup : unsaved;
                Assert.Equal("Input", text.Substring(location.Start, location.Length));
            });
        }
        var xamlRename = await fixture.Client.RenameAsync(new(fixture.XamlPath, xamlPosition, 5, "ContactEmail", Markup, fixture.Project, overlays));
        var codeRename = await fixture.Client.RenameAsync(new(fixture.CodePath, codePosition, 7, "ContactEmail", unsaved, fixture.Project, overlays));
        foreach (var result in new[] { xamlRename, codeRename })
        {
            Assert.DoesNotContain(result.Documents, edit => IsGenerated(edit.Path));
            var xaml = Assert.Single(result.Documents, edit => edit.Path == fixture.XamlPath);
            var code = Assert.Single(result.Documents, edit => edit.Path == fixture.CodePath);
            Assert.Equal(5, xaml.Version); Assert.Equal(7, code.Version);
            Assert.Equal(Hash(Markup), xaml.ExpectedTextHash); Assert.Equal(Hash(unsaved), code.ExpectedTextHash);
            Assert.Equal(2, xaml.Edits.Count);
            string changed = Apply(Markup, xaml.Edits);
            Assert.Contains("Name=\"ContactEmail\"", changed);
            Assert.Contains("ElementName=ContactEmail", changed);
            Assert.Equal(4, Count(changed, "Shared"));
            Assert.Contains("ContactEmail.Text + ContactEmail.Text", Apply(unsaved, code.Edits));
            Assert.Contains(result.Warnings, warning => warning.Contains("Generated files are not edited", StringComparison.Ordinal));
        }
        Assert.Equal(Markup, await File.ReadAllTextAsync(fixture.XamlPath));
        Assert.Equal(Code, await File.ReadAllTextAsync(fixture.CodePath));
        foreach (var edit in xamlRename.Documents)
            if (edit.Edits.Count > 0)
                await File.WriteAllTextAsync(edit.Path, Apply(edit.Path == fixture.CodePath ? unsaved : Markup, edit.Edits));
        var build = await new BuildService().RunAsync(new(fixture.Project, BuildOperation.Build, "Release"));
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Diagnostics.Select(item => item.Message)));
    }

    [Fact]
    public async Task TemplateNamesRemainLocalAndCanUseXamlOnlyIdentifiers()
    {
        await using var fixture = await Fixture.CreateAsync();
        int position = Markup.IndexOf("x:Name=\"Shared\"", StringComparison.Ordinal) + 8;
        string unsaved = Markup.Replace("<TextBox Name=\"Input\"/>", "<TextBox Name=\"Input\" Tag=\"unsaved\"/>", StringComparison.Ordinal);
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.XamlPath, position, 4, unsaved, fixture.Project));
        Assert.Equal(2, references.Locations.Count);
        Assert.All(references.Locations, location => Assert.Equal(fixture.XamlPath, location.Path));
        var result = await fixture.Client.RenameAsync(new(fixture.XamlPath, position, 4, "class", unsaved, fixture.Project));
        var edit = Assert.Single(result.Documents, document => document.Edits.Count > 0);
        Assert.Equal(fixture.XamlPath, edit.Path); Assert.Equal(4, edit.Version); Assert.Equal(Hash(unsaved), edit.ExpectedTextHash);
        string after = Apply(unsaved, edit.Edits);
        Assert.Contains("x:Name=\"class\"", after); Assert.Contains("ElementName=class", after);
        Assert.Equal(2, Count(after, "Shared"));
        Assert.Contains("Name=\"Input\"", after);
        Assert.DoesNotContain(result.Documents, document => document.Path == fixture.CodePath);
        await File.WriteAllTextAsync(fixture.XamlPath, after);
        var build = await new BuildService().RunAsync(new(fixture.Project, BuildOperation.Build, "Release"));
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Diagnostics.Select(item => item.Message)));
    }

    [Fact]
    public async Task SameScopeNamesAndAuthoredCodeMembersCannotBeCapturedByRename()
    {
        await using var fixture = await Fixture.CreateAsync();
        int position = Markup.IndexOf("Name=\"Input\"", StringComparison.Ordinal) + 6;
        foreach (string proposed in new[] { "Other", "NameCollision" })
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.XamlPath, position, 1,
                proposed, Markup, fixture.Project)));
            Assert.True(error.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("name", StringComparison.OrdinalIgnoreCase), error.Message);
            Assert.True(fixture.Client.IsConnected);
        }
        Assert.Equal(Markup, await File.ReadAllTextAsync(fixture.XamlPath));
        Assert.Equal(Code, await File.ReadAllTextAsync(fixture.CodePath));
    }

    [Fact]
    public async Task ATemplateNameWithTheSameSpellingDoesNotHideTheVerifiedPageField()
    {
        string markup = Markup.Replace("Shared", "Input", StringComparison.Ordinal);
        await using var fixture = await Fixture.CreateAsync(markup: markup);
        int position = Code.IndexOf("Input.Text", StringComparison.Ordinal);
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.CodePath, position, 0, Code, fixture.Project));
        Assert.Equal(2, references.Locations.Count(location => location.Path == fixture.XamlPath));
        Assert.Single(references.Locations, location => location.Path == fixture.CodePath);
        var result = await fixture.Client.RenameAsync(new(fixture.CodePath, position, 0, "ContactEmail", Code, fixture.Project));
        var edit = Assert.Single(result.Documents, document => document.Path == fixture.XamlPath);
        string after = Apply(markup, edit.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Equal(4, Count(after, "Input"));
        Assert.Contains("Name=\"ContactEmail\"", after);
        Assert.Contains("ElementName=ContactEmail", after);
    }

    [Fact]
    public async Task ChangedXamlBytesOrOverlayNeverDowngradeGeneratedNameToCsharpOnlyRename()
    {
        await using var fixture = await Fixture.CreateAsync();
        int xamlPosition = Markup.IndexOf("Name=\"Input\"", StringComparison.Ordinal) + 6;
        int codePosition = Code.IndexOf("Input.Text", StringComparison.Ordinal);
        string changed = Markup + "\n<!-- source changed after compilation -->";
        var overlays = new[] { new XamlDocumentOverlay(fixture.XamlPath, changed, 2) };
        foreach (var request in new[]
        {
            new RenameRequest(fixture.XamlPath, xamlPosition, 2, "Changed", changed, fixture.Project, overlays),
            new RenameRequest(fixture.CodePath, codePosition, 0, "Changed", Code, fixture.Project, overlays)
        })
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(request));
            Assert.Contains("buffer", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(fixture.Client.IsConnected);
        }
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.XamlPath, xamlPosition, 2, changed, fixture.Project, overlays));
        Assert.True(references.SymbolFound);
        Assert.Equal(2, references.Locations.Count);
        Assert.DoesNotContain(references.Locations, location => location.Path == fixture.CodePath);
        Assert.Contains(references.Warnings, warning => warning.Contains("buffer", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(fixture.XamlPath, changed);
        var diskError = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.CodePath, codePosition, 0,
            "Changed", Code, fixture.Project)));
        Assert.Contains("checksum", diskError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Client.IsConnected);
    }

    [Fact]
    public async Task UnsupportedNamedConsumersPreventPartialNameRename()
    {
        string markup = Markup.Replace("<TextBox Name=\"Input\"/>", """
            <TextBox Name="Input"><TextBox.Triggers><EventTrigger RoutedEvent="Loaded"><BeginStoryboard><Storyboard>
              <DoubleAnimation Storyboard.TargetName="Input" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:1"/>
            </Storyboard></BeginStoryboard></EventTrigger></TextBox.Triggers></TextBox>
            """, StringComparison.Ordinal);
        await using var fixture = await Fixture.CreateAsync(markup: markup);
        int position = markup.IndexOf("Name=\"Input\"", StringComparison.Ordinal) + 6;
        var refs = await fixture.Client.FindSymbolReferencesAsync(new(fixture.XamlPath, position, 1, markup, fixture.Project));
        Assert.True(refs.SymbolFound);
        Assert.Contains(refs.Warnings, warning => warning.Contains("TargetName", StringComparison.Ordinal));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.XamlPath, position, 1,
            "Changed", markup, fixture.Project)));
        Assert.Contains("complete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(markup, await File.ReadAllTextAsync(fixture.XamlPath));
    }

    [Fact]
    public async Task LinkedPageContextsRenameEveryVerifiedFieldAndAuthoredConsumer()
    {
        await using var fixture = await Fixture.CreateAsync(linked: true);
        int position = Markup.IndexOf("Name=\"Input\"", StringComparison.Ordinal) + 6;
        var result = await fixture.Client.RenameAsync(new(fixture.XamlPath, position, 3, "ContactEmail", Markup, fixture.Project));
        Assert.Equal(3, result.Documents.Count(document => document.Edits.Count > 0));
        Assert.Equal(2, Assert.Single(result.Documents, document => document.Path == fixture.XamlPath).Edits.Count);
        Assert.All(result.Documents, document => Assert.False(IsGenerated(document.Path)));
        foreach (string path in fixture.CodePaths)
            Assert.Contains("ContactEmail.Text", Apply(Code, Assert.Single(result.Documents, document => document.Path == path).Edits));
        string other = fixture.CodePaths[1];
        // An ordinary C# member collision in only one context likewise prevents
        // the complete transaction; no partial first-project edits are returned.
        string collision = Code.Replace("NameCollision", "ContactEmail", StringComparison.Ordinal);
        Assert.True((await fixture.Client.UpdateDocumentAsync(new(other, collision, 3, Analyze: false))).Accepted);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.XamlPath, position, 3,
            "ContactEmail", Markup, fixture.Project)));
        Assert.Contains("conflict", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Markup, await File.ReadAllTextAsync(fixture.XamlPath));
    }

    private static bool IsGenerated(string path) => path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Apply(string text, IEnumerable<TextEdit> edits)
    { foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText); return text; }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, string project, string xaml, string[] codePaths, WorkspaceClient client)
        { Root = root; Project = project; XamlPath = xaml; CodePaths = codePaths; Client = client; }
        public string Root { get; }
        public string Project { get; }
        public string XamlPath { get; }
        public string[] CodePaths { get; }
        public string CodePath => CodePaths[0];
        public WorkspaceClient Client { get; }

        public static async Task<Fixture> CreateAsync(string framework = "net10.0-windows", string? markup = null, bool linked = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "WpfStudio-NameRefactoring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string xaml = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(xaml, markup ?? Markup);
            var projects = new List<string>(); var codePaths = new List<string>();
            foreach (string name in linked ? new[] { "First", "Second" } : ["First"])
            {
                string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
                string project = Path.Combine(directory, name + ".csproj"); projects.Add(project);
                string code = Path.Combine(directory, "View.xaml.cs"); codePaths.Add(code);
                await File.WriteAllTextAsync(project, $"<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>{framework}</TargetFramework><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><Page Include='../View.xaml' Link='View.xaml'/></ItemGroup></Project>");
                await File.WriteAllTextAsync(code, Code);
                var restore = await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"));
                Assert.True(restore.ExitCode == 0, string.Join("\n", restore.Diagnostics.Select(item => item.Message)));
                var build = await new BuildService().RunAsync(new(project, BuildOperation.Build, "Release"));
                Assert.True(build.ExitCode == 0, string.Join("\n", build.Diagnostics.Select(item => item.Message)));
            }
            string workspace = projects[0];
            if (linked)
            {
                workspace = Path.Combine(root, "Fixture.slnx");
                await File.WriteAllTextAsync(workspace, "<Solution><Project Path='First/First.csproj'/><Project Path='Second/Second.csproj'/></Solution>");
            }
            string? hostPath = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST");
            if (hostPath is not null) Assert.True(File.Exists(hostPath), "The explicitly requested workspace host does not exist: " + hostPath);
            var client = new WorkspaceClient(hostPath);
            try
            {
                var loaded = await client.LoadAsync(new(workspace, "Release"));
                Assert.DoesNotContain(loaded.Issues, issue => issue.Severity == "Error");
                return new(root, projects[0], xaml, codePaths.ToArray(), client);
            }
            catch { await client.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        { await Client.DisposeAsync(); Directory.Delete(Root, recursive: true); }
    }
}
