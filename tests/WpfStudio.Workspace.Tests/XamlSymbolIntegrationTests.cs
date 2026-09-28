using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlSymbolIntegrationTests
{
    private const string Model = "namespace Fixture; public class ViewModel { public string Name => \"value\"; public string Use() => Name; }";
    private const string Code = "namespace Fixture; public partial class View { public event System.EventHandler Activated; private void OnActivated(object sender, System.EventArgs e) { } public void Use() => OnActivated(this, System.EventArgs.Empty); }";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropertyReferencesAndRenameWorkFromBothLanguagesWithClosedPrerequisites(bool fromXaml)
    {
        await using var fixture = await Fixture.CreateAsync();
        string markup = await File.ReadAllTextAsync(fixture.Xaml);
        string origin = fromXaml ? fixture.Xaml : fixture.Model;
        string text = fromXaml ? markup : Model;
        int position = text.IndexOf(fromXaml ? "Binding Name" : "Name =>", StringComparison.Ordinal) + (fromXaml ? 8 : 1);
        var references = await fixture.Client.FindSymbolReferencesAsync(new(origin, position, 0, text, fixture.Projects[0]));
        Assert.True(references.SymbolFound);
        Assert.Contains(references.Locations, location => location.Path == fixture.Model);
        Assert.Contains(references.Locations, location => location.Path == fixture.Xaml);
        Assert.Contains(references.Locations, location => location.Path == fixture.Second);
        Assert.All(references.Locations, location => Assert.False(string.IsNullOrEmpty(location.ExpectedTextHash)));
        Assert.Contains(references.Warnings, warning => warning.Contains("d:DesignInstance", StringComparison.Ordinal));
        var rename = await fixture.Client.RenameAsync(new(origin, position, 0, "DisplayName", text, fixture.Projects[0]));
        var model = Assert.Single(rename.Documents, document => document.Path == fixture.Model);
        Assert.Contains("DisplayName =>", Apply(Model, model.Edits));
        Assert.Contains("=> DisplayName;", Apply(Model, model.Edits));
        Assert.Contains("Binding DisplayName", Apply(markup, Assert.Single(rename.Documents, document => document.Path == fixture.Xaml).Edits));
        Assert.NotEmpty(Assert.Single(rename.Documents, document => document.Path == fixture.Second).Edits);
        Assert.Empty(Assert.Single(rename.Documents, document => document.Path == fixture.Idle).Edits);
        Assert.All(rename.Documents, document => Assert.Equal(Hash(File.ReadAllText(document.Path)), document.ExpectedTextHash));
        Assert.Equal(Model, await File.ReadAllTextAsync(fixture.Model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventHandlerReferencesAndRenameWorkFromBothLanguages(bool fromXaml)
    {
        await using var fixture = await Fixture.CreateAsync();
        string markup = await File.ReadAllTextAsync(fixture.Xaml);
        string path = fromXaml ? fixture.Xaml : fixture.Code;
        string text = fromXaml ? markup : Code;
        int position = text.IndexOf("OnActivated", StringComparison.Ordinal) + 2;
        var references = await fixture.Client.FindSymbolReferencesAsync(new(path, position, 0, text, fixture.Projects[0]));
        Assert.True(references.SymbolFound);
        Assert.Contains(references.Locations, location => location.Path == fixture.Xaml && markup.Substring(location.Start, location.Length) == "OnActivated");
        Assert.Contains(references.Locations, location => location.Path == fixture.Code);
        var rename = await fixture.Client.RenameAsync(new(path, position, 0, "OnReady", text, fixture.Projects[0]));
        Assert.Contains("Activated='OnReady'", Apply(markup, Assert.Single(rename.Documents, document => document.Path == fixture.Xaml).Edits));
        Assert.Contains("void OnReady", Apply(Code, Assert.Single(rename.Documents, document => document.Path == fixture.Code).Edits));
    }

    [Fact]
    public async Task UnsavedOverlaysWinPreserveRawEntitySpansAndCarryVersionsAndHashes()
    {
        await using var fixture = await Fixture.CreateAsync();
        string disk = await File.ReadAllTextAsync(fixture.Xaml);
        string text = disk.Replace("Binding Name", "Binding Na&#109;e", StringComparison.Ordinal);
        string code = Model.Replace("public string Use()", "public int Unsaved => 42; public string Use()", StringComparison.Ordinal);
        Assert.True((await fixture.Client.UpdateDocumentAsync(new(fixture.Model, code, 7, Analyze: false))).Accepted);
        XamlDocumentOverlay[] overlays = [new(fixture.Xaml, text, 9), new(fixture.Second, View("Other"), 2)];
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.Xaml, text.IndexOf("Na&#", StringComparison.Ordinal), 9, text, fixture.Projects[0], overlays));
        Assert.Contains(references.Locations, location => location.Path == fixture.Xaml && text.Substring(location.Start, location.Length) == "Na&#109;e");
        Assert.DoesNotContain(references.Locations, location => location.Path == fixture.Second);
        var result = await fixture.Client.RenameAsync(new(fixture.Xaml, text.IndexOf("Na&#", StringComparison.Ordinal), 9, "DisplayName", text, fixture.Projects[0], overlays));
        var xaml = Assert.Single(result.Documents, document => document.Path == fixture.Xaml);
        Assert.Equal(9, xaml.Version); Assert.Equal(Hash(text), xaml.ExpectedTextHash);
        Assert.Contains("Binding DisplayName", Apply(text, xaml.Edits));
        var model = Assert.Single(result.Documents, document => document.Path == fixture.Model);
        Assert.Equal(7, model.Version); Assert.Equal(Hash(code), model.ExpectedTextHash);
        Assert.Contains("Unsaved => 42", Apply(code, model.Edits));
        var other = Assert.Single(result.Documents, document => document.Path == fixture.Second);
        Assert.Equal(2, other.Version); Assert.Equal(Hash(View("Other")), other.ExpectedTextHash); Assert.Empty(other.Edits);
        Assert.Equal(disk, await File.ReadAllTextAsync(fixture.Xaml));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.Xaml, text.IndexOf("Na&#", StringComparison.Ordinal), 10, "DisplayName", text, fixture.Projects[0], overlays)));
    }

    [Fact]
    public async Task ReferencedProjectPropertyIdentityIsSharedAcrossCompilations()
    {
        await using var fixture = await Fixture.CreateAsync(referenced: true);
        string markup = await File.ReadAllTextAsync(fixture.Xaml);
        var result = await fixture.Client.RenameAsync(new(fixture.Xaml, markup.IndexOf("Binding Name", StringComparison.Ordinal) + 8, 0, "DisplayName", markup, fixture.Projects[0]));
        Assert.NotEmpty(Assert.Single(result.Documents, document => document.Path == fixture.Model).Edits);
        Assert.NotEmpty(Assert.Single(result.Documents, document => document.Path == fixture.Xaml).Edits);
    }

    [Fact]
    public async Task LinkedXamlWithDifferentSourceSymbolsCannotBeRenamedPartially()
    {
        await using var fixture = await Fixture.CreateAsync(linked: true);
        string markup = await File.ReadAllTextAsync(fixture.Xaml);
        var result = await fixture.Client.FindSymbolReferencesAsync(new(fixture.Xaml, markup.IndexOf("Binding Name", StringComparison.Ordinal) + 8, 0, markup, fixture.Projects[0]));
        Assert.True(result.SymbolFound);
        Assert.All(result.Locations.Where(location => location.Path == fixture.Xaml), location => Assert.Equal(fixture.Projects[0], location.ProjectPath));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.Xaml, markup.IndexOf("Binding Name", StringComparison.Ordinal) + 8, 0, "DisplayName", markup, fixture.Projects[0])));
        Assert.Contains("Linked XAML contexts disagree", error.Message);
        Assert.Equal(Model, await File.ReadAllTextAsync(fixture.Model));
    }

    [Fact]
    public async Task ClosedFileChangesAreRereadAndUnavailableOrMalformedFilesBlockRename()
    {
        await using var fixture = await Fixture.CreateAsync();
        string markup = await File.ReadAllTextAsync(fixture.Xaml);
        var request = new RenameRequest(fixture.Model, Model.IndexOf("Name =>", StringComparison.Ordinal), 0, "DisplayName", Model);
        await File.WriteAllTextAsync(fixture.Second, View("Other"));
        var first = await fixture.Client.RenameAsync(request);
        Assert.Empty(Assert.Single(first.Documents, document => document.Path == fixture.Second).Edits);
        await File.WriteAllTextAsync(fixture.Second, "<Broken");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(request));
        File.Delete(fixture.Second);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(request));
        await File.WriteAllTextAsync(fixture.Second, View("Name"));
        File.SetAttributes(fixture.Second, FileAttributes.ReadOnly);
        try { await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(request)); }
        finally { File.SetAttributes(fixture.Second, FileAttributes.Normal); }
        Assert.Equal(markup, await File.ReadAllTextAsync(fixture.Xaml));
    }

    [Fact]
    public async Task InternalModelRefreshNotifiesEditorsEvenWhenRenameIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var referenceNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler referenceHandler = (_, _) => referenceNotification.TrySetResult();
        fixture.Client.SemanticStateChanged += referenceHandler;
        try
        {
            await File.WriteAllTextAsync(fixture.Model, Model.Replace("public string Use()", "public int Marker => 1; public string Use()", StringComparison.Ordinal));
            var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.Code, Code.IndexOf("OnActivated", StringComparison.Ordinal), 0, Code));
            Assert.True(references.SymbolFound);
            await referenceNotification.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fixture.Client.IsConnected);
        }
        finally { fixture.Client.SemanticStateChanged -= referenceHandler; }

        var rejectionNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler rejectionHandler = (_, _) => rejectionNotification.TrySetResult();
        fixture.Client.SemanticStateChanged += rejectionHandler;
        try
        {
            string changed = Model.Replace("public string Use()", "public string DisplayName => \"collision\"; public string Use()", StringComparison.Ordinal);
            await File.WriteAllTextAsync(fixture.Model, changed);
            var rejection = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.Model, changed.IndexOf("Name =>", StringComparison.Ordinal), 0, "DisplayName", changed)));
            Assert.Contains("Rename introduces a compiler conflict:", rejection.Message);
            await rejectionNotification.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fixture.Client.IsConnected);
            Assert.Equal(changed, await File.ReadAllTextAsync(fixture.Model));
        }
        finally { fixture.Client.SemanticStateChanged -= rejectionHandler; }
    }

    [Fact]
    public async Task ExistingMemberConflictsAndChangedBindingIdentityRejectRename()
    {
        const string model = "namespace Fixture; public class Base { public string Name => \"name\"; } public class ViewModel : Base { public string DisplayName => \"other\"; public string Use() => Name; }";
        await using var fixture = await Fixture.CreateAsync(model: model);
        // Renaming Base.Name is legal C#, but binding against ViewModel would now
        // resolve its existing DisplayName rather than the renamed base property.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.Model, model.IndexOf("Name =>", StringComparison.Ordinal), 0, "DisplayName", model)));
        Assert.Contains("XAML symbol binding", error.Message);
        Assert.Equal(model, await File.ReadAllTextAsync(fixture.Model));
    }

    [Theory]
    [InlineData("namespace Fixture; public interface INamed { string Name { get; } } public class ViewModel : INamed { public string Name => \"value\"; }")]
    [InlineData("namespace Fixture; public class Base { public virtual string Name => \"base\"; } public class ViewModel : Base { public override string Name => \"value\"; }")]
    public async Task RoslynCascadedPropertyFamiliesAlsoUpdateXamlImplementations(string model)
    {
        await using var fixture = await Fixture.CreateAsync(model: model);
        var result = await fixture.Client.RenameAsync(new(fixture.Model, model.IndexOf("string Name", StringComparison.Ordinal) + 7, 0, "DisplayName", model));
        var source = Assert.Single(result.Documents, document => document.Path == fixture.Model);
        Assert.Equal(2, Apply(model, source.Edits).Split("DisplayName", StringSplitOptions.None).Length - 1);
        Assert.NotEmpty(Assert.Single(result.Documents, document => document.Path == fixture.Xaml).Edits);
        Assert.NotEmpty(Assert.Single(result.Documents, document => document.Path == fixture.Second).Edits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameCannotCaptureUntouchedBasePropertyBindings(bool separateFile)
    {
        const string model = "namespace Fixture; public class Base { public string DisplayName => \"base\"; } public class ViewModel : Base { public string Name => \"value\"; }";
        await using var fixture = await Fixture.CreateAsync(model: model);
        if (separateFile) await File.WriteAllTextAsync(fixture.Second, View("DisplayName"));
        else await File.WriteAllTextAsync(fixture.Xaml, View("Name").Replace("</local:View>", "<Label Text='{Binding DisplayName}'/></local:View>", StringComparison.Ordinal));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.Model, model.IndexOf("string Name", StringComparison.Ordinal) + 7, 0, "DisplayName", model)));
        Assert.Contains("XAML symbol binding", error.Message);
        Assert.Equal(model, await File.ReadAllTextAsync(fixture.Model));
    }

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task PropertyAndEventRenameProduceMarkupThatBuildsAsRealWpf(string framework)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlRenameBuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Fixture.csproj"), path = Path.Combine(directory, "View.xaml"), codePath = path + ".cs", modelPath = Path.Combine(directory, "ViewModel.cs");
        const string markup = "<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:Fixture' x:Class='Fixture.View'><Window.DataContext><local:ViewModel/></Window.DataContext><StackPanel><TextBlock Text='{Binding Name}'/><Button Click='OnActivated'/></StackPanel></Window>";
        const string code = "using System.Windows; namespace Fixture; public partial class View : Window { public View() { InitializeComponent(); } private void OnActivated(object sender, RoutedEventArgs e) { } }";
        await File.WriteAllTextAsync(project, $"<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>{framework}</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        await File.WriteAllTextAsync(path, markup); await File.WriteAllTextAsync(codePath, code); await File.WriteAllTextAsync(modelPath, Model);
        try
        {
            Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
            await using var client = new WorkspaceClient();
            await client.LoadAsync(new(project, "Release"));
            var property = await client.RenameAsync(new(path, markup.IndexOf("Binding Name", StringComparison.Ordinal) + 8, 1, "DisplayName", markup));
            string newMarkup = Apply(markup, Assert.Single(property.Documents, edit => edit.Path == path).Edits);
            string newModel = Apply(Model, Assert.Single(property.Documents, edit => edit.Path == modelPath).Edits);
            Assert.True((await client.UpdateDocumentAsync(new(modelPath, newModel, 1, Analyze: false))).Accepted);
            var handler = await client.RenameAsync(new(codePath, code.IndexOf("OnActivated", StringComparison.Ordinal), 0, "OnReady", code,
                XamlOverlays: [new(path, newMarkup, 2)]));
            string newCode = Apply(code, Assert.Single(handler.Documents, edit => edit.Path == codePath).Edits);
            newMarkup = Apply(newMarkup, Assert.Single(handler.Documents, edit => edit.Path == path).Edits);
            Assert.Contains("Binding DisplayName", newMarkup); Assert.Contains("Click='OnReady'", newMarkup);
            Assert.DoesNotContain(property.Documents.Concat(handler.Documents), edit => edit.Path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(path, newMarkup); await File.WriteAllTextAsync(modelPath, newModel); await File.WriteAllTextAsync(codePath, newCode);
            var build = await new BuildService().RunAsync(new(project, BuildOperation.Build, "Release"));
            Assert.True(build.ExitCode == 0, string.Join("\n", build.Diagnostics.Select(diagnostic => diagnostic.Message)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string View(string name, bool referenced = false) => "<local:View xmlns:local='clr-namespace:Fixture' xmlns:vm='clr-namespace:Fixture" + (referenced ? ";assembly=Models" : "")
        + "' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' x:Class='Fixture.View' d:DataContext='{d:DesignInstance vm:ViewModel}' Activated='OnActivated'><Label Text='{Binding " + name + "}'/></local:View>";
    private static string Apply(string text, IEnumerable<TextEdit> edits)
    { foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText); return text; }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string directory, string[] projects, string model, string code, WorkspaceClient client)
        { DirectoryPath = directory; Projects = projects; Model = model; Code = code; Client = client; }
        private string DirectoryPath { get; }
        public string[] Projects { get; }
        public string Model { get; }
        public string Code { get; }
        public string Xaml => Path.Combine(DirectoryPath, "Views", "View.xaml");
        public string Second => Path.Combine(DirectoryPath, "Views", "Second.xaml");
        public string Idle => Path.Combine(DirectoryPath, "Views", "Idle.xaml");
        public WorkspaceClient Client { get; }
        public static async Task<Fixture> CreateAsync(bool linked = false, bool referenced = false, string? model = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlSymbols-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "Views"));
            await File.WriteAllTextAsync(Path.Combine(directory, "Views", "View.xaml"), View("Name", referenced));
            await File.WriteAllTextAsync(Path.Combine(directory, "Views", "Second.xaml"), View("Name", referenced));
            await File.WriteAllTextAsync(Path.Combine(directory, "Views", "Idle.xaml"), "<Empty/>");
            var projects = new List<string>();
            string modelPath = "", codePath = "";
            if (referenced)
            {
                Directory.CreateDirectory(Path.Combine(directory, "Models"));
                string modelProject = Path.Combine(directory, "Models", "Models.csproj");
                await File.WriteAllTextAsync(modelProject, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                modelPath = Path.Combine(directory, "Models", "ViewModel.cs");
                await File.WriteAllTextAsync(modelPath, model ?? XamlSymbolIntegrationTests.Model);
            }
            foreach (string name in linked ? new[] { "First", "Second" } : ["First"])
            {
                string folder = Path.Combine(directory, name); Directory.CreateDirectory(folder);
                string project = Path.Combine(folder, name + ".csproj"); projects.Add(project);
                await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><None Include='../Views/*.xaml' Link='Views/%(Filename)%(Extension)'/>"
                    + (referenced ? "<ProjectReference Include='../Models/Models.csproj'/>" : "") + "</ItemGroup></Project>");
                string code = Path.Combine(folder, "View.cs"); await File.WriteAllTextAsync(code, XamlSymbolIntegrationTests.Code);
                if (codePath.Length == 0) codePath = code;
                if (!referenced)
                {
                    string source = Path.Combine(folder, "ViewModel.cs"); await File.WriteAllTextAsync(source, model ?? XamlSymbolIntegrationTests.Model);
                    if (modelPath.Length == 0) modelPath = source;
                }
                Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
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
                var loaded = await client.LoadAsync(new(workspace, "Release"));
                Assert.DoesNotContain(loaded.Issues, issue => issue.Severity == "Error");
                return new(directory, projects.ToArray(), modelPath, codePath, client);
            }
            catch { await client.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
