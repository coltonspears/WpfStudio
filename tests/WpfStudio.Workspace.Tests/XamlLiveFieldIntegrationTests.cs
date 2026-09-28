using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

/// <summary>Exercises ordinary editor synchronization against a real language worker.</summary>
public sealed class XamlLiveFieldIntegrationTests
{
    private const string Markup = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:LiveFieldFixture"
                x:Class="LiveFieldFixture.View">
          <StackPanel>
            <TextBox x:Name="Input"/>
          </StackPanel>
        </Window>
        """;
    private const string Code = """
        using System.Windows;
        namespace LiveFieldFixture;
        public partial class View : Window
        {
            public View() { InitializeComponent(); }
            public string Read() => this.Input.Text;
        }
        """;

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task DirectAddTypeChangeRenameAndRemovalUpdateBothLanguagesWithoutSaving(string framework)
    {
        await using var fixture = await Fixture.CreateAsync(framework);
        await fixture.SynchronizeAsync(Markup, Code, 1);
        await fixture.AssertHealthyAsync(Code, 1, ["Input"], ["Added", "Email"]);
        await fixture.AssertDefinitionAsync(Code, 1, "Input", Markup);

        string added = Markup.Replace("<TextBox x:Name=\"Input\"/>", "<TextBox x:Name=\"Input\"/>\n    <TextBox x:Name=\"Added\"/>", StringComparison.Ordinal);
        string withAdded = Code.Replace("this.Input.Text", "this.Input.Text + this.Added.Text", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(added, withAdded, 2);
        await fixture.AssertHealthyAsync(withAdded, 2, ["Input", "Added"], ["Email"]);
        await fixture.AssertDefinitionAsync(withAdded, 2, "Added", added);

        string changedType = added.Replace("<TextBox x:Name=\"Added\"/>", "<PasswordBox x:Name=\"Added\"/>", StringComparison.Ordinal);
        await fixture.UpdateXamlAsync(changedType, 3);
        await fixture.AssertErrorAsync(withAdded, 2, "CS1061", "Text");
        var members = await fixture.Client.GetCompletionsAsync(new(fixture.CodePath,
            withAdded.IndexOf("this.Added.", StringComparison.Ordinal) + "this.Added.".Length, 2));
        Assert.Contains(members.Items, item => item.DisplayText == "Password");
        Assert.DoesNotContain(members.Items, item => item.DisplayText == "Text");
        string withPassword = withAdded.Replace("this.Added.Text", "this.Added.Password", StringComparison.Ordinal);
        await fixture.UpdateCodeAsync(withPassword, 3);
        await fixture.AssertHealthyAsync(withPassword, 3, ["Input", "Added"], ["Email"]);

        string renamed = changedType.Replace("x:Name=\"Input\"", "x:Name=\"Email\"", StringComparison.Ordinal);
        await fixture.UpdateXamlAsync(renamed, 4);
        await fixture.AssertFieldsAsync(withPassword, 3, ["Email", "Added"], ["Input"]);
        await fixture.AssertErrorAsync(withPassword, 3, "CS1061", "Input");
        string withEmail = withPassword.Replace("this.Input.Text", "this.Email.Text", StringComparison.Ordinal);
        await fixture.UpdateCodeAsync(withEmail, 4);
        await fixture.AssertHealthyAsync(withEmail, 4, ["Email", "Added"], ["Input"]);
        await fixture.AssertDefinitionAsync(withEmail, 4, "Email", renamed);

        string removed = renamed.Replace("<PasswordBox x:Name=\"Added\"/>", "", StringComparison.Ordinal);
        await fixture.UpdateXamlAsync(removed, 5);
        await fixture.AssertErrorAsync(withEmail, 4, "CS1061", "Added");
        string finalCode = withEmail.Replace(" + this.Added.Password", "", StringComparison.Ordinal);
        await fixture.UpdateCodeAsync(finalCode, 5);
        await fixture.AssertHealthyAsync(finalCode, 5, ["Email"], ["Input", "Added"]);
        await fixture.AssertDefinitionAsync(finalCode, 5, "Email", removed);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task UnsavedFieldsSurviveWorkerRestartUndoAndIndependentDiscard()
    {
        await using var fixture = await Fixture.CreateAsync();
        string changed = Markup.Replace("x:Name=\"Input\"", "x:Name=\"ContactEmail\"", StringComparison.Ordinal);
        string changedCode = Code.Replace("this.Input.Text", "this.ContactEmail.Text", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(changed, changedCode, 1);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(changedCode, 1, ["ContactEmail"], ["Input"]);
        await fixture.AssertDefinitionAsync(changedCode, 1, "ContactEmail", changed);

        // Undo and redo are just new authoritative source snapshots. No rename
        // plan or previously recognized history is supplied to the worker.
        await fixture.SynchronizeAsync(Markup, Code, 2);
        await fixture.AssertHealthyAsync(Code, 2, ["Input"], ["ContactEmail"]);
        await fixture.SynchronizeAsync(changed, changedCode, 3);
        await fixture.Client.CloseDocumentAsync(fixture.XamlPath);
        await fixture.AssertFieldsAsync(changedCode, 3, ["Input"], ["ContactEmail"]);
        await fixture.AssertErrorAsync(changedCode, 3, "CS1061", "ContactEmail");

        foreach (string path in fixture.CodePaths) await fixture.Client.CloseDocumentAsync(path);
        await fixture.UpdateCodeAsync(Code, 1);
        await fixture.AssertHealthyAsync(Code, 1, ["Input"], ["ContactEmail"]);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(Code, 1, ["Input"], ["ContactEmail"]);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task RuntimeNameAliasRootFieldAccessibilityAndTemplateIsolationFollowCurrentMarkup()
    {
        await using var fixture = await Fixture.CreateAsync();
        string changed = Markup.Replace("x:Class=\"LiveFieldFixture.View\"", "x:Class=\"LiveFieldFixture.View\" x:Name=\"PageRoot\"", StringComparison.Ordinal)
            .Replace("<TextBox x:Name=\"Input\"/>", """
                <TextBox Name="PublicAlias" x:FieldModifier="public"/>
                <TextBox x:Name="PrivateField" x:FieldModifier="private"/>
                <ContentControl>
                  <ContentControl.ContentTemplate>
                    <DataTemplate><TextBox x:Name="TemplateOnly"/></DataTemplate>
                  </ContentControl.ContentTemplate>
                </ContentControl>
                """, StringComparison.Ordinal);
        string changedCode = Code.Replace("this.Input.Text", "this.PageRoot.Title + this.PublicAlias.Text + this.PrivateField.Text", StringComparison.Ordinal)
            + "\npublic static class Observer { public static string Read(View view) => view.PublicAlias.Text; }\n";
        await fixture.SynchronizeAsync(changed, changedCode, 1);
        await fixture.AssertHealthyAsync(changedCode, 1, ["PageRoot", "PublicAlias", "PrivateField"], ["Input", "TemplateOnly"]);
        await fixture.AssertDefinitionAsync(changedCode, 1, "PageRoot", changed);
        await fixture.AssertDefinitionAsync(changedCode, 1, "PublicAlias", changed, "Name");

        string externalPrivateRead = changedCode.Replace("=> view.PublicAlias.Text", "=> view.PrivateField.Text", StringComparison.Ordinal);
        await fixture.UpdateCodeAsync(externalPrivateRead, 2);
        await fixture.AssertErrorAsync(externalPrivateRead, 2, "CS0122", "PrivateField");
        string publicField = changed.Replace("x:FieldModifier=\"private\"", "x:FieldModifier=\"public\"", StringComparison.Ordinal);
        await fixture.UpdateXamlAsync(publicField, 2);
        await fixture.AssertHealthyAsync(externalPrivateRead, 2, ["PublicAlias", "PrivateField"], ["TemplateOnly"]);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task AuthoredFieldCollisionRemainsACompilerErrorAndRecoversWhenRemoved()
    {
        await using var fixture = await Fixture.CreateAsync();
        string changed = Markup.Replace("x:Name=\"Input\"", "x:Name=\"Email\"", StringComparison.Ordinal);
        string changedCode = Code.Replace("this.Input.Text", "this.Email.Text", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(changed, changedCode, 1);
        string collision = changedCode.Replace("public string Read()", "public System.Windows.Controls.TextBox Email = new();\n    public string Read()", StringComparison.Ordinal);
        var result = await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, collision, 2));
        Assert.True(result.Accepted);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "Error" && diagnostic.Id is "CS0102" or "CS0229");
        await fixture.UpdateCodeAsync(changedCode, 3);
        await fixture.AssertHealthyAsync(changedCode, 3, ["Email"], ["Input"]);
        await fixture.AssertDefinitionAsync(changedCode, 3, "Email", changed);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task LinkedXamlUpdatesEveryOwningCompilation()
    {
        await using var fixture = await Fixture.CreateAsync(linked: true);
        string changed = Markup.Replace("<TextBox x:Name=\"Input\"/>", "<PasswordBox x:Name=\"Secret\"/>", StringComparison.Ordinal);
        string changedCode = Code.Replace("this.Input.Text", "this.Secret.Password", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(changed, changedCode, 1);
        await fixture.AssertHealthyAsync(changedCode, 1, ["Secret"], ["Input"]);
        foreach (string path in fixture.CodePaths)
            await fixture.AssertDefinitionAsync(changedCode, 1, "Secret", changed, codePath: path);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(changedCode, 1, ["Secret"], ["Input"]);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task UnsavedCustomElementTypeAndItsChangingBaseAreUsedByGeneratedFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        string changedCode = Code + "\npublic class EmailEditor : System.Windows.Controls.TextBox { }\n";
        await fixture.UpdateCodeAsync(changedCode, 1);
        string changed = Markup.Replace("<TextBox x:Name=\"Input\"/>", "<local:EmailEditor x:Name=\"Input\"/>", StringComparison.Ordinal);
        await fixture.UpdateXamlAsync(changed, 1);
        await fixture.AssertHealthyAsync(changedCode, 1, ["Input"], []);
        await fixture.AssertDefinitionAsync(changedCode, 1, "Input", changed);

        string changedBase = changedCode.Replace("EmailEditor : System.Windows.Controls.TextBox", "EmailEditor : System.Windows.Controls.Button", StringComparison.Ordinal);
        await fixture.UpdateCodeAsync(changedBase, 2);
        await fixture.AssertErrorAsync(changedBase, 2, "CS1061", "Text");
        string corrected = changedBase.Replace("this.Input.Text", "this.Input.Content.ToString()", StringComparison.Ordinal);
        await fixture.UpdateCodeAsync(corrected, 3);
        await fixture.AssertHealthyAsync(corrected, 3, ["Input"], []);
        await fixture.AssertDefinitionAsync(corrected, 3, "Input", changed);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task EvaluatedPageNeedsNoExplicitBuildBeforeLiveFieldAuthoring()
    {
        // Workspace loading is allowed to perform its ordinary MSBuild design-
        // time evaluation. This fixture never explicitly builds the application.
        await using var fixture = await Fixture.CreateAsync(build: false);
        await fixture.AssertFieldsAsync(Code, 0, ["Input"], []);
        await fixture.AssertDefinitionAsync(Code, 0, "Input", Markup);
        string changed = Markup.Replace("<TextBox x:Name=\"Input\"/>", "<TextBox x:Name=\"FreshField\"/>", StringComparison.Ordinal);
        string changedCode = Code.Replace("this.Input.Text", "this.FreshField.Text", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(changed, changedCode, 1);
        await fixture.AssertHealthyAsync(changedCode, 1, ["FreshField"], ["Input"]);
        await fixture.AssertDefinitionAsync(changedCode, 1, "FreshField", changed);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task FirstUnchangedXamlSynchronizationPreservesSemanticRevisionAndCompletionUntilAnActualEdit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = await fixture.Client.AnalyzeXamlProjectAsync(new(1, [], fixture.Project));
        Assert.True(initial.Accepted, initial.Status);
        int position = Code.IndexOf("this.", StringComparison.Ordinal) + "this.".Length;
        var completions = await fixture.Client.GetCompletionsAsync(new(fixture.CodePath, position, 0));
        var input = Assert.Single(completions.Items, item => item.DisplayText == "Input");
        Assert.NotNull(await fixture.Client.GetCompletionEditAsync(new(fixture.CodePath, 0, input.Id)));

        // Merely opening an evaluated page must not invalidate work already
        // based on exactly the same saved XAML and C# semantic snapshot.
        foreach (long version in new long[] { 1, 1, 2 })
        {
            await fixture.UpdateXamlAsync(Markup, version);
            var unchanged = await fixture.Client.AnalyzeXamlProjectAsync(new(version + 1, [], fixture.Project));
            Assert.True(unchanged.Accepted, unchanged.Status);
            Assert.Equal(initial.SemanticRevision, unchanged.SemanticRevision);
            Assert.NotNull(await fixture.Client.GetCompletionEditAsync(new(fixture.CodePath, 0, input.Id)));
        }

        string changed = Markup.Replace("x:Name=\"Input\"", "x:Name=\"Email\"", StringComparison.Ordinal);
        await fixture.UpdateXamlAsync(changed, 3);
        var edited = await fixture.Client.AnalyzeXamlProjectAsync(new(4, [new(fixture.XamlPath, changed, 3)], fixture.Project));
        Assert.True(edited.Accepted, edited.Status);
        Assert.True(edited.SemanticRevision > initial.SemanticRevision);
        Assert.Null(await fixture.Client.GetCompletionEditAsync(new(fixture.CodePath, 0, input.Id)));
        await fixture.AssertFieldsAsync(Code, 0, ["Email"], ["Input"]);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task OpeningCompilerOutputDoesNotRegisterAnAuthoredBufferOrBreakCurrentFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(Markup, Code, 1);
        string generated = Directory.EnumerateFiles(fixture.Root, "View.g*.cs", SearchOption.AllDirectories).First();
        string generatedText = await File.ReadAllTextAsync(generated);
        var opened = await fixture.Client.UpdateDocumentAsync(new(generated, generatedText, 1));
        Assert.False(opened.Accepted);
        Assert.Empty(opened.Diagnostics);
        var edited = await fixture.Client.UpdateDocumentAsync(new(generated, "This is not valid C# compiler output.", 2));
        Assert.False(edited.Accepted);
        Assert.Empty(edited.Diagnostics);

        int position = generatedText.IndexOf("InitializeComponent", StringComparison.Ordinal);
        Assert.True(position >= 0);
        var completionError = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.GetCompletionsAsync(new(generated, position, 2)));
        Assert.Contains("generated compiler output", completionError.Message, StringComparison.OrdinalIgnoreCase);
        var definitionError = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.GetDefinitionAsync(new(generated, position, 2)));
        Assert.Contains("generated compiler output", definitionError.Message, StringComparison.OrdinalIgnoreCase);
        await fixture.AssertHealthyAsync(Code, 1, ["Input"], []);
        await fixture.AssertDefinitionAsync(Code, 1, "Input", Markup);

        await fixture.Client.CloseDocumentAsync(generated);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(Code, 1, ["Input"], []);
        await fixture.AssertDefinitionAsync(Code, 1, "Input", Markup);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task ReviewedRenameAfterDirectNameTypeAndFieldEditsUpdatesAllAuthoredReferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        string changed = Markup.Replace("<TextBox x:Name=\"Input\"/>", """
            <PasswordBox x:Name="PasswordInput"/>
            <TextBox x:Name="Other"/>
            <TextBlock Text="{Binding Password, ElementName=PasswordInput}"/>
            """, StringComparison.Ordinal);
        string changedCode = Code.Replace("this.Input.Text", "this.PasswordInput.Password + this.Other.Text", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(changed, changedCode, 1);
        await fixture.AssertHealthyAsync(changedCode, 1, ["PasswordInput", "Other"], ["Input"]);
        int usage = changedCode.IndexOf("this.PasswordInput.", StringComparison.Ordinal) + "this.".Length;
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.CodePath, usage, 1, changedCode, fixture.Project,
            [new(fixture.XamlPath, changed, 1)]));
        Assert.True(references.SymbolFound, string.Join("\n", references.Warnings));
        Assert.Equal(2, references.Locations.Count(location => location.Path == fixture.XamlPath));
        Assert.Single(references.Locations, location => location.Path == fixture.CodePath);

        var rename = await fixture.Client.RenameAsync(new(fixture.CodePath, usage, 1, "SecretInput", changedCode, fixture.Project,
            [new(fixture.XamlPath, changed, 1)]));
        Assert.Contains(rename.Documents, document => document.Path == fixture.XamlPath);
        Assert.Contains(rename.Documents, document => document.Path == fixture.CodePath);
        Assert.All(rename.Documents, document => Assert.True(document.Path == fixture.XamlPath || document.Path == fixture.CodePath,
            "A reviewed XAML field rename must return authored document edits only."));
        // Merely displaying the review must leave both language states intact.
        await fixture.AssertHealthyAsync(changedCode, 1, ["PasswordInput", "Other"], ["SecretInput", "Input"]);

        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [fixture.XamlPath] = changed, [fixture.CodePath] = changedCode };
        foreach (var document in rename.Documents)
        {
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(current[document.Path])), document.ExpectedTextHash);
            foreach (var edit in document.Edits.OrderByDescending(edit => edit.Start))
                current[document.Path] = current[document.Path].Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        }
        var result = await fixture.Client.ReconcileNameProjectionsAsync(current.Select(pair => new UpdateDocumentRequest(pair.Key, pair.Value, 2, Analyze: false)).ToArray());
        Assert.True(result.Accepted, result.Status);
        await fixture.AssertHealthyAsync(current[fixture.CodePath], 2, ["SecretInput", "Other"], ["PasswordInput", "Input"]);
        await fixture.AssertDefinitionAsync(current[fixture.CodePath], 2, "SecretInput", current[fixture.XamlPath]);
        Assert.Contains("ElementName=SecretInput", current[fixture.XamlPath], StringComparison.Ordinal);
        await fixture.AssertDiskUnchangedAsync();
    }

    [Fact]
    public async Task IncompleteTypingDoesNotKeepAnOldFieldAndRecoversOnTheNextValidBuffer()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(Markup, Code, 1);
        string incomplete = Markup[..Markup.IndexOf("<TextBox", StringComparison.Ordinal)] + "<TextBox x:Name=\"Cont";
        await fixture.UpdateXamlAsync(incomplete, 2);
        var analysis = await fixture.Client.AnalyzeXamlAsync(new(fixture.XamlPath, incomplete, 2, fixture.Project));
        Assert.True(analysis.Accepted);
        Assert.True(analysis.Diagnostics.Any(diagnostic => diagnostic.Severity == "Error") || !string.IsNullOrWhiteSpace(analysis.Status),
            "An incomplete field model must explain why current XAML cannot be fully analyzed.");
        await fixture.AssertFieldsAsync(Code, 1, [], ["Input", "Cont"]);

        string recovered = Markup.Replace("x:Name=\"Input\"", "x:Name=\"ContactEmail\"", StringComparison.Ordinal);
        string recoveredCode = Code.Replace("this.Input.Text", "this.ContactEmail.Text", StringComparison.Ordinal);
        await fixture.SynchronizeAsync(recovered, recoveredCode, 3);
        await fixture.AssertHealthyAsync(recoveredCode, 3, ["ContactEmail"], ["Input", "Cont"]);
        await fixture.AssertDefinitionAsync(recoveredCode, 3, "ContactEmail", recovered);
        await fixture.AssertDiskUnchangedAsync();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _generatedHashes;
        private Fixture(string root, string project, string xaml, string[] codePaths, WorkspaceClient client, Dictionary<string, string> generatedHashes)
        { Root = root; Project = project; XamlPath = xaml; CodePaths = codePaths; Client = client; _generatedHashes = generatedHashes; }
        public string Root { get; }
        public string Project { get; }
        public string XamlPath { get; }
        public string[] CodePaths { get; }
        public string CodePath => CodePaths[0];
        public WorkspaceClient Client { get; }

        public async Task SynchronizeAsync(string xaml, string code, long version)
        { await UpdateXamlAsync(xaml, version); await UpdateCodeAsync(code, version); }

        public async Task UpdateXamlAsync(string text, long version)
        { Assert.True((await Client.UpdateDocumentAsync(new(XamlPath, text, version, Analyze: false))).Accepted); }

        public async Task UpdateCodeAsync(string text, long version)
        {
            foreach (string path in CodePaths)
                Assert.True((await Client.UpdateDocumentAsync(new(path, text, version, Analyze: false))).Accepted);
        }

        public async Task AssertHealthyAsync(string code, long version, string[] present, string[] absent)
        {
            foreach (string path in CodePaths)
            {
                var result = await Client.UpdateDocumentAsync(new(path, code, version));
                Assert.True(result.Accepted);
                Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == "Error");
            }
            await AssertFieldsAsync(code, version, present, absent);
        }

        public async Task AssertErrorAsync(string code, long version, string id, string member)
        {
            foreach (string path in CodePaths)
            {
                var result = await Client.UpdateDocumentAsync(new(path, code, version));
                Assert.True(result.Accepted);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "Error" && diagnostic.Id == id && diagnostic.Message.Contains(member, StringComparison.Ordinal));
            }
        }

        public async Task AssertFieldsAsync(string code, long version, string[] present, string[] absent)
        {
            int position = code.IndexOf("this.", StringComparison.Ordinal) + "this.".Length;
            Assert.True(position >= "this.".Length);
            foreach (string path in CodePaths)
            {
                var result = await Client.GetCompletionsAsync(new(path, position, version));
                foreach (string field in present) Assert.Contains(result.Items, item => item.DisplayText == field);
                foreach (string field in absent) Assert.DoesNotContain(result.Items, item => item.DisplayText == field);
            }
        }

        public async Task AssertDefinitionAsync(string code, long version, string field, string xaml, string nameAttribute = "x:Name", string? codePath = null)
        {
            int position = code.IndexOf("this." + field + ".", StringComparison.Ordinal) + "this.".Length;
            Assert.True(position >= "this.".Length);
            var definitions = await Client.GetDefinitionAsync(new(codePath ?? CodePath, position, version));
            var definition = Assert.Single(definitions);
            Assert.Equal(XamlPath, definition.Path);
            string prefix = nameAttribute + "=\"";
            int expected = xaml.IndexOf(prefix + field + "\"", StringComparison.Ordinal) + prefix.Length;
            Assert.Equal(expected, definition.Start);
            Assert.Equal(field.Length, definition.Length);
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(xaml)), definition.ExpectedTextHash);
        }

        public async Task AssertDiskUnchangedAsync()
        {
            var current = Directory.EnumerateFiles(Root, "*.g*.cs", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal(_generatedHashes.Keys.Order(StringComparer.OrdinalIgnoreCase), current);
            foreach (var (path, hash) in _generatedHashes) Assert.Equal(hash, Hash(await File.ReadAllBytesAsync(path)));
            Assert.Equal(Markup, await File.ReadAllTextAsync(XamlPath));
            foreach (string path in CodePaths) Assert.Equal(Code, await File.ReadAllTextAsync(path));
        }

        public static async Task<Fixture> CreateAsync(string framework = "net10.0-windows", bool linked = false, bool build = true)
        {
            string root = Path.Combine(Path.GetTempPath(), "WpfStudio-LiveFields-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string xaml = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(xaml, Markup);
            var projects = new List<string>(); var codePaths = new List<string>();
            foreach (string name in linked ? new[] { "First", "Second" } : ["First"])
            {
                string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
                string project = Path.Combine(directory, name + ".csproj"); projects.Add(project);
                string code = Path.Combine(directory, "View.xaml.cs"); codePaths.Add(code);
                await File.WriteAllTextAsync(project, $"<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>{framework}</TargetFramework><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><Page Include='../View.xaml' Link='View.xaml'/></ItemGroup></Project>");
                await File.WriteAllTextAsync(code, Code);
                foreach (var operation in build ? new[] { BuildOperation.Restore, BuildOperation.Build } : [BuildOperation.Restore])
                {
                    var result = await new BuildService().RunAsync(new(project, operation, "Release"));
                    Assert.True(result.ExitCode == 0, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
                }
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
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in Directory.EnumerateFiles(root, "*.g*.cs", SearchOption.AllDirectories))
                    hashes[path] = Hash(await File.ReadAllBytesAsync(path));
                if (build) Assert.NotEmpty(hashes);
                return new(root, projects[0], xaml, codePaths.ToArray(), client, hashes);
            }
            catch { await client.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullRoot = Path.GetFullPath(Root);
            if (!fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("WpfStudio-LiveFields-", StringComparison.Ordinal))
                throw new InvalidOperationException("The test fixture root is outside its owned temporary directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
