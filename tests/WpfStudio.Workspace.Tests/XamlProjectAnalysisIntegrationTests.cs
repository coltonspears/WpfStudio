using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlProjectAnalysisIntegrationTests
{
    [Fact]
    public async Task ClosedFilesAreAnalyzedWithoutOpeningThemAndEqualTimestampChangesAreReread()
    {
        string original = View("Nmae");
        await using var fixture = await Fixture.CreateAsync(new() { ["Broken.xaml"] = original, ["Healthy.xaml"] = View("Name") });
        string path = fixture.View("Broken.xaml");
        var first = await fixture.Client.AnalyzeXamlProjectAsync(new(41, []));
        Assert.True(first.Accepted, first.Status);
        Assert.Equal(41, first.Generation);
        Assert.Equal(2, first.TotalFiles);
        Assert.False(first.Truncated);
        var broken = FileResult(first, path, fixture.Project);
        Assert.Equal("Analyzed", broken.State);
        Assert.Null(broken.Version);
        Assert.Equal(Hash(original), broken.TextHash);
        AssertMissingMember(broken, original, "Nmae");
        Assert.Empty(FileResult(first, fixture.View("Healthy.xaml"), fixture.Project).Diagnostics);

        // Length and write time remain identical; metadata-only cache keys would miss this fix.
        var timestamp = File.GetLastWriteTimeUtc(path);
        string fixedText = View("Name");
        Assert.Equal(original.Length, fixedText.Length);
        await File.WriteAllTextAsync(path, fixedText);
        File.SetLastWriteTimeUtc(path, timestamp);
        var second = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(42, [])), path, fixture.Project);
        Assert.Equal("Analyzed", second.State);
        Assert.Null(second.Version);
        Assert.Equal(Hash(fixedText), second.TextHash);
        Assert.Empty(second.Diagnostics);

        await File.WriteAllTextAsync(path, original);
        File.SetLastWriteTimeUtc(path, timestamp);
        AssertMissingMember(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(43, [])), path, fixture.Project), original, "Nmae");
        Assert.True(fixture.Client.IsConnected);
    }

    [Fact]
    public async Task LinkedXamlKeepsDistinctProjectDiagnosticsAndExplicitScope()
    {
        string xaml = View("Name");
        await using var fixture = await Fixture.CreateAsync(new() { ["Shared.xaml"] = xaml }, linked: true);
        string path = fixture.View("Shared.xaml");
        var all = await fixture.Client.AnalyzeXamlProjectAsync(new(1, []));
        Assert.True(all.Accepted, all.Status);
        Assert.Equal(2, all.TotalFiles);
        Assert.Equal(2, all.Files.Count);
        Assert.Empty(FileResult(all, path, fixture.Projects[0]).Diagnostics);
        AssertMissingMember(FileResult(all, path, fixture.Projects[1]), xaml, "Name");
        Assert.Equal("First", FileResult(all, path, fixture.Projects[0]).ProjectName);
        Assert.Equal("Second", FileResult(all, path, fixture.Projects[1]).ProjectName);

        var scoped = await fixture.Client.AnalyzeXamlProjectAsync(new(2, [], fixture.Projects[1]));
        Assert.True(scoped.Accepted, scoped.Status);
        Assert.Equal(fixture.Projects[1], Assert.Single(scoped.Files).ProjectPath);
        AssertMissingMember(scoped.Files[0], xaml, "Name");

        string overlay = View("Title");
        var changed = await fixture.Client.AnalyzeXamlProjectAsync(new(3, [new(path, overlay, 7)]));
        AssertMissingMember(FileResult(changed, path, fixture.Projects[0]), overlay, "Title");
        Assert.Empty(FileResult(changed, path, fixture.Projects[1]).Diagnostics);
        Assert.All(changed.Files, file => { Assert.Equal(7, file.Version); Assert.Equal(Hash(overlay), file.TextHash); });
        Assert.Equal(xaml, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OpenModelAndXamlOverlaysSurviveRestartWithoutBecomingClosedFileState()
    {
        string diskXaml = View("Name");
        await using var fixture = await Fixture.CreateAsync(new() { ["View.xaml"] = diskXaml });
        string path = fixture.View("View.xaml");
        string model = fixture.Models[0];
        string unsavedModel = Model("Title");
        Assert.True((await fixture.Client.UpdateDocumentAsync(new(model, unsavedModel, 5, Analyze: false))).Accepted);
        string openXaml = View("Title");
        var overlays = new[] { new XamlDocumentOverlay(path, openXaml, 9) };
        var open = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(1, overlays)), path, fixture.Project);
        Assert.Empty(open.Diagnostics);
        Assert.Equal(9, open.Version);
        Assert.Equal(Hash(openXaml), open.TextHash);

        var closed = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(2, [])), path, fixture.Project);
        Assert.Null(closed.Version);
        AssertMissingMember(closed, diskXaml, "Name");
        await fixture.Client.RestartAsync();
        Assert.Empty(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(3, overlays)), path, fixture.Project).Diagnostics);
        AssertMissingMember(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(4, [])), path, fixture.Project), diskXaml, "Name");

        await fixture.Client.CloseDocumentAsync(model);
        var restored = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(5, [])), path, fixture.Project);
        Assert.Empty(restored.Diagnostics);
        Assert.Null(restored.Version);
        Assert.Equal(diskXaml, await File.ReadAllTextAsync(path));
        Assert.Equal(Model("Name"), await File.ReadAllTextAsync(model));
    }

    [Fact]
    public async Task ClosedModelDiskRefreshUpdatesSemanticsAndPreservesUnsavedModels()
    {
        string xaml = View("Name");
        await using var fixture = await Fixture.CreateAsync(new() { ["View.xaml"] = xaml });
        string path = fixture.View("View.xaml");
        string model = fixture.Models[0];
        var before = await fixture.Client.AnalyzeXamlProjectAsync(new(1, []));
        Assert.Empty(FileResult(before, path, fixture.Project).Diagnostics);
        var timestamp = File.GetLastWriteTimeUtc(model);
        await File.WriteAllTextAsync(model, Model("Code"));
        File.SetLastWriteTimeUtc(model, timestamp);
        var refresh = await fixture.Client.RefreshDiskDocumentsAsync(new([model]));
        Assert.True(refresh.Accepted, refresh.Status);
        Assert.True(refresh.Changed);
        var after = await fixture.Client.AnalyzeXamlProjectAsync(new(2, []));
        Assert.True(after.SemanticRevision > before.SemanticRevision);
        AssertMissingMember(FileResult(after, path, fixture.Project), xaml, "Name");

        Assert.True((await fixture.Client.UpdateDocumentAsync(new(model, Model("Name"), 1, Analyze: false))).Accepted);
        await fixture.Client.RefreshDiskDocumentsAsync(new([model]));
        Assert.Empty(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(3, [])), path, fixture.Project).Diagnostics);
        Assert.Equal(Model("Code"), await File.ReadAllTextAsync(model));
        await fixture.Client.CloseDocumentAsync(model);
        AssertMissingMember(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(4, [])), path, fixture.Project), xaml, "Name");

        // A deleted model must never leave a trustworthy result based on its cached type.
        File.Delete(model);
        await fixture.Client.RefreshDiskDocumentsAsync(new([model]));
        var missing = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(5, [])), path, fixture.Project);
        Assert.Equal("Unavailable", missing.State);
        Assert.Empty(missing.Diagnostics);
        Assert.False(string.IsNullOrWhiteSpace(missing.Status));
        var unavailableEditor = await fixture.Client.AnalyzeXamlAsync(new(path, xaml, 20, fixture.Project));
        Assert.False(unavailableEditor.Accepted);
        Assert.Empty(unavailableEditor.Diagnostics);
        Assert.False(string.IsNullOrWhiteSpace(unavailableEditor.Status));
        await File.WriteAllTextAsync(model, Model("Name"));
        Assert.True((await fixture.Client.RefreshDiskDocumentsAsync(new([model]))).Accepted);
        var recovered = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(6, [])), path, fixture.Project);
        Assert.Equal("Analyzed", recovered.State);
        Assert.Empty(recovered.Diagnostics);
        var recoveredEditor = await fixture.Client.AnalyzeXamlAsync(new(path, xaml, 20, fixture.Project));
        Assert.True(recoveredEditor.Accepted, recoveredEditor.Status);
        Assert.Empty(recoveredEditor.Diagnostics);
    }

    [Fact]
    public async Task BoundedModelRefreshContinuesItsSweepAndWithholdsIncompleteTypeContexts()
    {
        string xaml = View("Name");
        await using var fixture = await Fixture.CreateAsync(new() { ["View.xaml"] = xaml }, additionalModel: true);
        var first = await fixture.Client.RefreshDiskDocumentsAsync(new(MaximumFiles: 1));
        Assert.True(first.Accepted, first.Status);
        Assert.Equal(1, first.RefreshedFiles);
        Assert.True(first.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(first.Status));
        var partial = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(1, [])), fixture.View("View.xaml"), fixture.Project);
        Assert.Equal("Unavailable", partial.State);
        Assert.Empty(partial.Diagnostics);
        var editor = await fixture.Client.AnalyzeXamlAsync(new(fixture.View("View.xaml"), xaml, 1, fixture.Project));
        Assert.False(editor.Accepted);
        Assert.Empty(editor.Diagnostics);

        var second = await fixture.Client.RefreshDiskDocumentsAsync(new(MaximumFiles: 1));
        Assert.True(second.Accepted, second.Status);
        Assert.Equal(1, second.RefreshedFiles);
        Assert.False(second.Truncated);
        var complete = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(2, [])), fixture.View("View.xaml"), fixture.Project);
        Assert.Equal("Analyzed", complete.State);
        Assert.Empty(complete.Diagnostics);
    }

    [Fact]
    public async Task UnavailableReferencedModelWithholdsDependentXamlUntilItsSourceRecovers()
    {
        string xaml = View("Nmae").Replace("clr-namespace:Fixture", "clr-namespace:Fixture;assembly=Models", StringComparison.Ordinal);
        await using var fixture = await Fixture.CreateReferencedAsync(xaml);
        string path = fixture.View("View.xaml");
        string model = fixture.Models[0];
        AssertMissingMember(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(1, [])), path, fixture.Project), xaml, "Nmae");
        long generation = 1;

        async Task AssertUnavailableAsync()
        {
            var refresh = await fixture.Client.RefreshDiskDocumentsAsync(new([model]));
            Assert.True(refresh.Accepted, refresh.Status);
            Assert.True(refresh.Truncated);
            var unavailable = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(++generation, [])), path, fixture.Project);
            Assert.Equal("Unavailable", unavailable.State);
            Assert.Empty(unavailable.Diagnostics);
            Assert.Contains("referenced project", unavailable.Status);
            var editor = await fixture.Client.AnalyzeXamlAsync(new(path, xaml, 1, fixture.Project));
            Assert.False(editor.Accepted);
            Assert.Empty(editor.Diagnostics);
            Assert.False(string.IsNullOrWhiteSpace(editor.Status));
        }

        async Task AssertRecoveredAsync()
        {
            var refresh = await fixture.Client.RefreshDiskDocumentsAsync(new([model]));
            Assert.True(refresh.Accepted, refresh.Status);
            Assert.False(refresh.Truncated);
            AssertMissingMember(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(++generation, [])), path, fixture.Project), xaml, "Nmae");
            var editor = await fixture.Client.AnalyzeXamlAsync(new(path, xaml, 1, fixture.Project));
            Assert.True(editor.Accepted, editor.Status);
            Assert.Equal("XAMLBIND001", Assert.Single(editor.Diagnostics).Id);
        }

        File.Delete(model);
        await AssertUnavailableAsync();
        await File.WriteAllTextAsync(model, Model("Name"));
        await AssertRecoveredAsync();
        using (var locked = new FileStream(model, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await AssertUnavailableAsync();
        await AssertRecoveredAsync();
        Assert.True(fixture.Client.IsConnected);
    }

    [Fact]
    public async Task MissingOrOversizedClosedXamlHasExplicitUnavailableEvidenceAndCanRecover()
    {
        string original = View("Nmae");
        await using var fixture = await Fixture.CreateAsync(new() { ["View.xaml"] = original });
        string path = fixture.View("View.xaml");
        File.Delete(path);
        var missing = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(1, [])), path, fixture.Project);
        Assert.Equal("Missing", missing.State);
        Assert.Empty(missing.Diagnostics);
        Assert.Null(missing.TextHash);
        Assert.False(string.IsNullOrWhiteSpace(missing.Status));

        // An explicit open overlay remains authoritative even after its disk file disappears.
        string overlay = View("Name");
        var open = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(2, [new(path, overlay, 11)])), path, fixture.Project);
        Assert.Equal("Analyzed", open.State);
        Assert.Equal(11, open.Version);
        Assert.Empty(open.Diagnostics);

        await File.WriteAllTextAsync(path, original + new string(' ', 1024));
        var large = await fixture.Client.AnalyzeXamlProjectAsync(new(3, [], MaximumFileCharacters: original.Length));
        Assert.True(large.Truncated);
        var omitted = FileResult(large, path, fixture.Project);
        Assert.Equal("TooLarge", omitted.State);
        Assert.Empty(omitted.Diagnostics);
        Assert.False(string.IsNullOrWhiteSpace(omitted.Status));
        await File.WriteAllTextAsync(path, original, Encoding.Unicode);
        var recovered = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(4, [])), path, fixture.Project);
        AssertMissingMember(recovered, original, "Nmae");
        Assert.Equal(Hash(original), recovered.TextHash); // Hash the decoded text, independently of disk encoding/BOM.
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(5, [])), path, fixture.Project);
            Assert.Equal("Unavailable", unavailable.State);
            Assert.Empty(unavailable.Diagnostics);
            Assert.False(string.IsNullOrWhiteSpace(unavailable.Status));
        }
        AssertMissingMember(FileResult(await fixture.Client.AnalyzeXamlProjectAsync(new(6, [])), path, fixture.Project), original, "Nmae");
    }

    [Fact]
    public async Task FileTextAndDiagnosticBudgetsRemainVisibleAndCancellationLeavesWorkerUsable()
    {
        string xaml = View("MissingOne", "MissingTwo", "MissingThree");
        await using var fixture = await Fixture.CreateAsync(new() { ["A.xaml"] = xaml, ["B.xaml"] = xaml, ["C.xaml"] = xaml });
        var fileLimited = await fixture.Client.AnalyzeXamlProjectAsync(new(1, [], MaximumFiles: 1));
        Assert.True(fileLimited.Accepted, fileLimited.Status);
        Assert.True(fileLimited.Truncated);
        Assert.Equal(3, fileLimited.TotalFiles);
        Assert.True(fileLimited.Files.Count <= 1);
        Assert.False(string.IsNullOrWhiteSpace(fileLimited.Status));

        var textLimited = await fixture.Client.AnalyzeXamlProjectAsync(new(2, [], MaximumTotalCharacters: xaml.Length));
        Assert.True(textLimited.Truncated);
        Assert.True(textLimited.Files.Count(file => file.State == "Analyzed") <= 1);
        Assert.False(string.IsNullOrWhiteSpace(textLimited.Status));

        var diagnosticLimited = await fixture.Client.AnalyzeXamlProjectAsync(new(3, [], MaximumDiagnostics: 2));
        Assert.True(diagnosticLimited.Truncated);
        Assert.Equal(2, diagnosticLimited.Files.Sum(file => file.Diagnostics.Count));
        Assert.False(string.IsNullOrWhiteSpace(diagnosticLimited.Status));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.AnalyzeXamlProjectAsync(new(4, []), cancelled.Token));
        Assert.True(fixture.Client.IsConnected);
        var complete = await fixture.Client.AnalyzeXamlProjectAsync(new(5, []));
        Assert.True(complete.Accepted, complete.Status);
        Assert.False(complete.Truncated);
        Assert.Equal(3, complete.Files.Count);
        Assert.Equal(9, complete.Files.Sum(file => file.Diagnostics.Count));
    }

    private static XamlFileAnalysisResult FileResult(XamlProjectAnalysisResult result, string path, string project)
    {
        Assert.True(result.Accepted, result.Status);
        return Assert.Single(result.Files, file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
            && file.ProjectPath.Equals(project, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertMissingMember(XamlFileAnalysisResult file, string text, string member)
    {
        Assert.Equal("Analyzed", file.State);
        var diagnostic = Assert.Single(file.Diagnostics);
        Assert.Equal("XAMLBIND001", diagnostic.Id);
        Assert.Equal(file.Path, diagnostic.Path);
        Assert.Equal(file.ProjectPath, diagnostic.ProjectPath);
        Assert.Equal(file.ProjectName, diagnostic.ProjectName);
        Assert.Equal(member, text.Substring(diagnostic.Start, diagnostic.Length));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Model(string property) => $"namespace Fixture; public class ViewModel {{ public string {property} => \"value\"; }}";
    private static string View(params string[] bindings) =>
        "<View xmlns:vm='clr-namespace:Fixture' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' d:DataContext='{d:DesignInstance vm:ViewModel}'>"
        + string.Concat(bindings.Select(binding => "<Label Text='{Binding " + binding + "}'/>")) + "</View>";

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string directory, string[] projects, string[] models, WorkspaceClient client)
        { DirectoryPath = directory; Projects = projects; Models = models; Client = client; }
        private string DirectoryPath { get; }
        public string[] Projects { get; }
        public string[] Models { get; }
        public string Project => Projects[0];
        public WorkspaceClient Client { get; }
        public string View(string name) => Path.Combine(DirectoryPath, "Views", name);

        public static async Task<Fixture> CreateAsync(Dictionary<string, string> views, bool linked = false, bool additionalModel = false)
        {
            string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlProject-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "Views"));
            foreach (var (name, text) in views) await File.WriteAllTextAsync(Path.Combine(directory, "Views", name), text);
            var projects = new List<string>();
            var models = new List<string>();
            foreach (string name in linked ? new[] { "First", "Second" } : ["First"])
            {
                string folder = Path.Combine(directory, name);
                Directory.CreateDirectory(folder);
                string project = Path.Combine(folder, name + ".csproj");
                string model = Path.Combine(folder, "ViewModel.cs");
                projects.Add(project); models.Add(model);
                await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><None Include='../Views/*.xaml' Link='Views/%(Filename)%(Extension)' /></ItemGroup></Project>");
                await File.WriteAllTextAsync(model, Model(name == "First" ? "Name" : "Title"));
                if (additionalModel)
                    await File.WriteAllTextAsync(Path.Combine(folder, "Additional.cs"), "namespace Fixture; public class AdditionalModel { public int Count => 1; }");
                Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
            }
            string workspace = projects[0];
            if (linked)
            {
                workspace = Path.Combine(directory, "Fixture.slnx");
                await File.WriteAllTextAsync(workspace, "<Solution><Project Path='First/First.csproj'/><Project Path='Second/Second.csproj'/></Solution>");
            }
            var client = new WorkspaceClient();
            try
            {
                var loaded = await client.LoadAsync(new(workspace));
                Assert.DoesNotContain(loaded.Issues, issue => issue.Severity == "Error");
                return new(directory, projects.ToArray(), models.ToArray(), client);
            }
            catch { await client.DisposeAsync(); throw; }
        }

        public static async Task<Fixture> CreateReferencedAsync(string xaml)
        {
            string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlReferenced-" + Guid.NewGuid().ToString("N"));
            string consumerDirectory = Path.Combine(directory, "Consumer");
            string modelDirectory = Path.Combine(directory, "Models");
            Directory.CreateDirectory(consumerDirectory);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(Path.Combine(directory, "Views"));
            string consumer = Path.Combine(consumerDirectory, "Consumer.csproj");
            string models = Path.Combine(modelDirectory, "Models.csproj");
            string model = Path.Combine(modelDirectory, "ViewModel.cs");
            await File.WriteAllTextAsync(models, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(model, Model("Name"));
            await File.WriteAllTextAsync(consumer, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include='../Models/Models.csproj'/><None Include='../Views/*.xaml' Link='Views/%(Filename)%(Extension)' /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(consumerDirectory, "Anchor.cs"), "namespace Consumer; public class Anchor { }");
            await File.WriteAllTextAsync(Path.Combine(directory, "Views", "View.xaml"), xaml);
            Assert.Equal(0, (await new BuildService().RunAsync(new(consumer, BuildOperation.Restore))).ExitCode);
            var client = new WorkspaceClient();
            try
            {
                var loaded = await client.LoadAsync(new(consumer));
                Assert.DoesNotContain(loaded.Issues, issue => issue.Severity == "Error");
                Assert.Equal(2, loaded.Projects.Count);
                return new(directory, [consumer, models], [model], client);
            }
            catch { await client.DisposeAsync(); throw; }
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}
