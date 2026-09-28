using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlResourceBindingIntegrationTests
{
    private const string Models = """
        namespace Fixture;
        public sealed class Customer { public string Name { get; set; } = "Ada"; }
        public sealed class Order { public string Title { get; set; } = "Draft"; }
        public sealed class Supplier { public string Code { get; set; } = "A1"; }
        """;

    [Fact]
    public async Task DictionaryOverlaysDriveEveryLanguageOperationAndRenamePrerequisites()
    {
        string original = View("Name"), dictionary = Dictionary("Customer");
        await using var fixture = await Fixture.CreateAsync(new()
        {
            ["View.xaml"] = original, ["Data.xaml"] = dictionary, ["Models.cs"] = Models
        });
        string view = fixture.PathFor("View.xaml"), data = fixture.PathFor("Data.xaml"), model = fixture.PathFor("Models.cs");

        string typoText = View("Nmae");
        var diagnostic = Assert.Single((await fixture.Analyze(typoText)).Diagnostics, item => item.Id == "XAMLBIND001");
        Assert.Equal("Nmae", typoText.Substring(diagnostic.Start, diagnostic.Length));
        Assert.Contains("Name", diagnostic.Message);
        var position = original.IndexOf("Path=Name", StringComparison.Ordinal) + 5;
        var completion = await fixture.Client.GetXamlCompletionsAsync(new(view, original, position, 1, fixture.Project));
        Assert.True(completion.Available, completion.Status);
        Assert.Contains(completion.Completion!.Items, item => item.DisplayText == "Name");
        var hover = await fixture.Client.GetXamlHoverAsync(new(view, original, position + 1, 1, fixture.Project));
        Assert.Contains("Name", Assert.IsType<XamlHoverInfo>(hover).Text);
        var definition = Assert.Single(await fixture.Client.GetXamlDefinitionAsync(new(view, original, position + 1, 1, fixture.Project)));
        Assert.Equal(model, definition.Path);
        Assert.Equal("Name", Models.Substring(definition.Start, definition.Length));
        var actions = await fixture.Client.GetXamlCodeActionsAsync(new(view, typoText, diagnostic.Start + 1, 2, fixture.Project));
        var fix = Assert.Single(actions, action => action.Edit.Edits.Any(edit => edit.NewText == "Name"));
        var fixPrerequisite = Assert.Single(fix.AdditionalEdits ?? [], edit => edit.Path == data);
        Assert.Empty(fixPrerequisite.Edits);
        Assert.Equal(Hash(dictionary), fixPrerequisite.ExpectedTextHash);

        var references = await fixture.Client.FindSymbolReferencesAsync(new(view, position + 1, 0, original, fixture.Project));
        Assert.True(references.SymbolFound);
        Assert.Contains(references.Locations, location => location.Path == view && location.Start == position);
        var rename = await fixture.Client.RenameAsync(new(view, position + 1, 0, "DisplayName", original, fixture.Project));
        Assert.Contains(Assert.Single(rename.Documents, item => item.Path == view).Edits, edit => edit.NewText == "DisplayName");
        var prerequisite = Assert.Single(rename.Documents, item => item.Path == data);
        Assert.Empty(prerequisite.Edits);
        Assert.Equal(Hash(dictionary), prerequisite.ExpectedTextHash);
        Assert.Equal(dictionary, await File.ReadAllTextAsync(data));

        XamlDocumentOverlay[] overlays = [new(data, Dictionary("Order"), 7)];
        var changed = await fixture.Analyze(original, overlays);
        Assert.Contains(changed.Diagnostics, item => item.Id == "XAMLBIND001" && original.Substring(item.Start, item.Length) == "Name");
        var newCompletion = await fixture.Client.GetXamlCompletionsAsync(new(view, original, position, 1, fixture.Project, overlays));
        Assert.Contains(newCompletion.Completion!.Items, item => item.DisplayText == "Title");
        Assert.DoesNotContain(newCompletion.Completion.Items, item => item.DisplayText == "Name");
        var orderView = View("Title");
        int orderPosition = orderView.IndexOf("Path=Title", StringComparison.Ordinal) + 6;
        var orderDefinition = Assert.Single(await fixture.Client.GetXamlDefinitionAsync(new(view, orderView, orderPosition, 3, fixture.Project, overlays)));
        Assert.Equal(model, orderDefinition.Path);
        Assert.Equal("Title", Models.Substring(orderDefinition.Start, orderDefinition.Length));

        var batch = await fixture.Client.AnalyzeXamlProjectAsync(new(10, overlays, fixture.Project));
        Assert.True(batch.Accepted, batch.Status);
        Assert.Contains(Assert.Single(batch.Files, item => item.Path == view).Diagnostics, item => item.Id == "XAMLBIND001");
        var restoredBatch = await fixture.Client.AnalyzeXamlProjectAsync(new(11, [], fixture.Project));
        Assert.Empty(Assert.Single(restoredBatch.Files, item => item.Path == view).Diagnostics);
        Assert.Empty((await fixture.Analyze(original)).Diagnostics); // Closing/discarding the dictionary overlay restores disk evidence.

        string unsavedModel = Models.Replace("string Title", "string Caption", StringComparison.Ordinal);
        Assert.True((await fixture.Client.UpdateDocumentAsync(new(model, unsavedModel, 9, Analyze: false))).Accepted);
        var modelCompletion = await fixture.Client.GetXamlCompletionsAsync(new(view, original, position, 1, fixture.Project, overlays));
        Assert.Contains(modelCompletion.Completion!.Items, item => item.DisplayText == "Caption");
        Assert.DoesNotContain(modelCompletion.Completion.Items, item => item.DisplayText == "Title");
        Assert.Equal(Models, await File.ReadAllTextAsync(model));
        await fixture.Client.CloseDocumentAsync(model);
        Assert.Contains((await fixture.Client.GetXamlCompletionsAsync(new(view, original, position, 1, fixture.Project, overlays))).Completion!.Items,
            item => item.DisplayText == "Title");

        var conflict = await fixture.Client.AnalyzeXamlAsync(new(view, original, 1, fixture.Project,
            [new(view, View("Title"), 1)]));
        Assert.False(conflict.Accepted);
    }

    [Fact]
    public async Task DictionaryDependencyHashesAndUnavailableLaterMergesInvalidateConsumerAnalysis()
    {
        string viewText = View("Name"), original = Dictionary("Customer");
        await using var fixture = await Fixture.CreateAsync(new()
        {
            ["View.xaml"] = viewText, ["Data.xaml"] = original, ["Models.cs"] = Models
        });
        string view = fixture.PathFor("View.xaml"), data = fixture.PathFor("Data.xaml");
        var first = await fixture.Client.AnalyzeXamlProjectAsync(new(1, [], fixture.Project));
        Assert.Empty(Assert.Single(first.Files, item => item.Path == view).Diagnostics);
        DateTime stamp = File.GetLastWriteTimeUtc(data);
        string replacement = Dictionary("Supplier");
        Assert.Equal(original.Length, replacement.Length);
        await File.WriteAllTextAsync(data, replacement);
        File.SetLastWriteTimeUtc(data, stamp);
        var second = await fixture.Client.AnalyzeXamlProjectAsync(new(2, [], fixture.Project));
        Assert.Contains(Assert.Single(second.Files, item => item.Path == view).Diagnostics, item => item.Id == "XAMLBIND001");
        await File.WriteAllTextAsync(data, original);
        File.SetLastWriteTimeUtc(data, stamp);
        Assert.Empty(Assert.Single((await fixture.Client.AnalyzeXamlProjectAsync(new(3, [], fixture.Project))).Files,
            item => item.Path == view).Diagnostics);

        string fallback = View("Nmae").Replace("<ResourceDictionary Source=\"Data.xaml\" />",
            "<ResourceDictionary Source=\"Data.xaml\" /><ResourceDictionary Source=\"Later.xaml\" />", StringComparison.Ordinal);
        // The unresolvable later import could override Current. Do not infer the earlier Customer.
        Assert.DoesNotContain((await fixture.Analyze(fallback)).Diagnostics, item => item.Id == "XAMLBIND001");
        using (var locked = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.DoesNotContain((await fixture.Analyze(View("Nmae"))).Diagnostics, item => item.Id == "XAMLBIND001");
            const string local = """
                <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:m="clr-namespace:Fixture"
                    d:DataContext="{d:DesignInstance m:Customer}"><TextBlock Text="{Binding Nmae}" /></UserControl>
                """;
            var localFix = Assert.Single(await fixture.Client.GetXamlCodeActionsAsync(new(view, local,
                local.IndexOf("Nmae", StringComparison.Ordinal) + 1, 5, fixture.Project)));
            Assert.Contains(localFix.Edit.Edits, edit => edit.NewText == "Name");
            Assert.Empty(localFix.AdditionalEdits ?? []); // An unrelated locked resource is not a prerequisite.
        }
        Assert.Contains((await fixture.Analyze(View("Nmae"))).Diagnostics, item => item.Id == "XAMLBIND001");
        File.Delete(data);
        Assert.DoesNotContain((await fixture.Analyze(View("Nmae"))).Diagnostics, item => item.Id == "XAMLBIND001");
        await File.WriteAllTextAsync(data, original);
        Assert.Contains((await fixture.Analyze(View("Nmae"))).Diagnostics, item => item.Id == "XAMLBIND001");
    }

    [Fact]
    public async Task ReferencedAndLinkedDictionariesKeepDeclaringNamespacesAndApplicationScope()
    {
        string externalView = View("LibraryName", "/FixtureResources;component/Themes/Data.xaml");
        string applicationView = View("AppName", key: "AppOnly", source: null);
        await using var fixture = await Fixture.CreateAsync(new()
        {
            ["App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><OutputType>WinExe</OutputType></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
                </Project>
                """,
            ["App/App.xaml"] = """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:Fixture" x:Class="Fixture.App">
                    <Application.Resources><m:AppModel x:Key="AppOnly" /></Application.Resources>
                </Application>
                """,
            ["App/App.xaml.cs"] = "namespace Fixture; public partial class App : System.Windows.Application { }",
            ["App/Models.cs"] = Models + " public sealed class AppModel { public string AppName => \"App\"; }",
            ["App/View.xaml"] = externalView,
            ["App/ApplicationView.xaml"] = applicationView,
            ["Library/Library.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><AssemblyName>FixtureResources</AssemblyName></PropertyGroup>
                  <ItemGroup><Page Include="../Shared/Linked.xaml"><Link>Themes/Linked.xaml</Link></Page></ItemGroup>
                </Project>
                """,
            ["Library/Models.cs"] = "namespace LibraryModels; public sealed class Customer { public string LibraryName => \"Library\"; }",
            ["Library/OwnView.xaml"] = applicationView,
            ["Library/Themes/Data.xaml"] = """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Linked.xaml" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
                """,
            ["Shared/Linked.xaml"] = """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:LibraryModels">
                  <m:Customer x:Key="Current" />
                </ResourceDictionary>
                """
        }, "App/App.csproj");
        var analysis = await fixture.Client.AnalyzeXamlAsync(new(fixture.PathFor("App/View.xaml"), externalView, 1, fixture.Project));
        Assert.True(analysis.Accepted, analysis.Status);
        Assert.Empty(analysis.Diagnostics);
        int position = externalView.IndexOf("Path=LibraryName", StringComparison.Ordinal) + 5;
        var completion = await fixture.Client.GetXamlCompletionsAsync(new(fixture.PathFor("App/View.xaml"), externalView, position, 1, fixture.Project));
        Assert.Contains(completion.Completion!.Items, item => item.DisplayText == "LibraryName");
        Assert.DoesNotContain(completion.Completion.Items, item => item.DisplayText == "Name");
        var definition = Assert.Single(await fixture.Client.GetXamlDefinitionAsync(new(fixture.PathFor("App/View.xaml"), externalView, position + 1, 1, fixture.Project)));
        Assert.Equal(fixture.PathFor("Library/Models.cs"), definition.Path);

        int appPosition = applicationView.IndexOf("Path=AppName", StringComparison.Ordinal) + 5;
        var appCompletion = await fixture.Client.GetXamlCompletionsAsync(new(fixture.PathFor("App/ApplicationView.xaml"), applicationView, appPosition, 1, fixture.Project));
        Assert.Contains(appCompletion.Completion!.Items, item => item.DisplayText == "AppName");
        var libraryCompletion = await fixture.Client.GetXamlCompletionsAsync(new(fixture.PathFor("Library/OwnView.xaml"), applicationView,
            appPosition, 1, fixture.PathFor("Library/Library.csproj")));
        Assert.True(libraryCompletion.Available, libraryCompletion.Status);
        Assert.DoesNotContain(libraryCompletion.Completion!.Items, item => item.DisplayText == "AppName");
    }

    private static string Dictionary(string type) => $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:Fixture">
            <m:{{type}} x:Key="Current" />
        </ResourceDictionary>
        """;

    private static string View(string member, string? source = "Data.xaml", string key = "Current") => $$"""
        <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:Fixture">
            {{(source is null ? "" : $"<UserControl.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"{source}\" /></ResourceDictionary.MergedDictionaries></ResourceDictionary></UserControl.Resources>")}}
            <TextBlock Text="{Binding Source={StaticResource {{key}}}, Path={{member}}}" />
        </UserControl>
        """;

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; }
        public string Project { get; }
        public WorkspaceClient Client { get; }
        private Fixture(string directory, string project, WorkspaceClient client) { Directory = directory; Project = project; Client = client; }
        public string PathFor(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
        public async Task<XamlAnalysisResult> Analyze(string text, IReadOnlyList<XamlDocumentOverlay>? overlays = null)
        {
            var result = await Client.AnalyzeXamlAsync(new(PathFor("View.xaml"), text, 1, Project, overlays));
            Assert.True(result.Accepted, result.Status);
            return result;
        }
        public static async Task<Fixture> CreateAsync(Dictionary<string, string> files, string entry = "Fixture.csproj")
        {
            string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-ResourceBinding-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar));
            string? host = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST");
            if (host is not null) Assert.True(File.Exists(host), "The explicitly selected workspace worker must exist.");
            var fixture = new Fixture(directory, project, new WorkspaceClient(host));
            try
            {
                if (!files.ContainsKey(entry)) files.Add(entry, """
                    <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>
                    """);
                foreach (var (relative, text) in files)
                {
                    string path = fixture.PathFor(relative);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, text);
                }
                Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
                var workspace = await fixture.Client.LoadAsync(new LoadWorkspaceRequest(project, "Release"));
                Assert.DoesNotContain(workspace.Issues, issue => issue.Severity == "Error");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
