using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlNameProjectionIntegrationTests
{
    private const string Markup = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="ProjectionFixture.View">
          <StackPanel>
            <TextBox x:Name="Input"/>
            <TextBlock Text="{Binding Text, ElementName=Input}"/>
            <TextBox x:Name="Other"/>
            <TextBlock Text="{Binding Text, ElementName=Other}"/>
          </StackPanel>
        </Window>
        """;
    private const string Code = """
        using System.Windows;
        namespace ProjectionFixture;
        public partial class View : Window
        {
            public View() { InitializeComponent(); }
            public string Read() => this.Input.Text + this.Other.Text;
        }
        """;

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task ReviewedProjectionSupportsBothLanguagesRepeatRenameOtherFieldsAndUndo(string framework)
    {
        await using var fixture = await Fixture.CreateAsync(framework);
        var initial = fixture.Initial;
        await fixture.SynchronizeAsync(initial, 1);
        var first = await fixture.RenameAsync(initial, 1, "Input", "ContactEmail");
        Assert.NotNull(first.NameProjection);
        Assert.DoesNotContain(first.Documents, edit => IsGenerated(edit.Path));

        // Merely requesting (or cancelling the review of) a rename must not change
        // the compiler or register the proposed authored buffers.
        await fixture.AssertFieldsAsync(initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        await fixture.AssertNoErrorsAsync(initial, 1);
        var contact = fixture.Apply(initial, first);
        await fixture.CommitAsync(first, contact, 2);
        await fixture.AssertHealthyAsync(contact, 2, "ContactEmail", "Other", absent: ["Input"]);
        await fixture.AssertNavigationAsync(contact, 2, "ContactEmail");

        var second = await fixture.RenameAsync(contact, 2, "ContactEmail", "Destination", fromCode: true);
        var destination = fixture.Apply(contact, second);
        await fixture.CommitAsync(second, destination, 3);
        await fixture.AssertHealthyAsync(destination, 3, "Destination", "Other", absent: ["Input", "ContactEmail"]);
        await fixture.AssertNavigationAsync(destination, 3, "Destination");

        // A different generated field must remain bridged after the first field
        // has changed length and shifted subsequent authored name spans.
        var third = await fixture.RenameAsync(destination, 3, "Other", "BackupContact");
        var both = fixture.Apply(destination, third);
        await fixture.CommitAsync(third, both, 4);
        await fixture.AssertHealthyAsync(both, 4, "Destination", "BackupContact", absent: ["Input", "Other"]);
        await fixture.AssertNavigationAsync(both, 4, "BackupContact");

        // Replay is reconciliation of current authoritative buffers, not another
        // mutation of an already projected generated document.
        await fixture.ReconcileAsync(destination, 5);
        await fixture.AssertHealthyAsync(destination, 5, "Destination", "Other", absent: ["BackupContact"]);
        await fixture.ReconcileAsync(contact, 6);
        await fixture.AssertHealthyAsync(contact, 6, "ContactEmail", "Other", absent: ["Destination"]);
        await fixture.ReconcileAsync(initial, 7);
        await fixture.AssertHealthyAsync(initial, 7, "Input", "Other", absent: ["ContactEmail", "Destination"]);
        await fixture.AssertGeneratedUnchangedAsync();
        Assert.Equal(Markup, await File.ReadAllTextAsync(fixture.XamlPath));
        Assert.All(fixture.CodePaths, path => Assert.Equal(Code, File.ReadAllText(path)));
    }

    [Fact]
    public async Task AcceptedProjectionReplaysAcrossWorkerRestartBeforeAndAfterSavingAndClosing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var changed = fixture.Apply(fixture.Initial, rename);
        await fixture.CommitAsync(rename, changed, 2);

        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(changed, 2, "ContactEmail", "Other", absent: ["Input"]);
        await fixture.AssertNavigationAsync(changed, 2, "ContactEmail");
        foreach (var (path, text) in changed) await File.WriteAllTextAsync(path, text);
        await fixture.Client.CloseDocumentAsync(fixture.XamlPath);
        foreach (string path in fixture.CodePaths) await fixture.Client.CloseDocumentAsync(path);
        await fixture.AssertGeneratedUnchangedAsync();
        await fixture.Client.RestartAsync();
        // Loading a saved WPF project may legitimately regenerate its compiler
        // output. Treat that MSBuild boundary as a new baseline, then prove that
        // observation and a subsequent rename query do not write generated code.
        await fixture.CaptureGeneratedBaselineAsync();

        // No open buffer is available to rescue replay here. The worker must
        // derive its current fields from the saved page rather than old history.
        await fixture.AssertFieldsAsync(changed, 0, "ContactEmail", "Other", absent: ["Input"]);
        await fixture.SynchronizeAsync(changed, 1);
        await fixture.AssertHealthyAsync(changed, 1, "ContactEmail", "Other", absent: ["Input"]);
        var next = await fixture.RenameAsync(changed, 1, "ContactEmail", "Destination", fromCode: true);
        Assert.NotNull(next.NameProjection);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task DiscardingCodeOrXamlIndependentlyFollowsXamlAndPreservesRealCompilerErrors()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var changed = fixture.Apply(fixture.Initial, rename);
        await fixture.CommitAsync(rename, changed, 2);

        await fixture.Client.CloseDocumentAsync(fixture.CodePath);
        var discardedCode = new Dictionary<string, string>(changed, StringComparer.OrdinalIgnoreCase) { [fixture.CodePath] = Code };
        await fixture.AssertFieldsAsync(discardedCode, 0, "ContactEmail", "Other", absent: ["Input"]);
        var mismatch = await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, Code, 3));
        Assert.True(mismatch.Accepted);
        Assert.Contains(mismatch.Diagnostics, MissingField);

        Assert.True((await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, changed[fixture.CodePath], 4))).Accepted);
        await fixture.Client.CloseDocumentAsync(fixture.XamlPath);
        await fixture.AssertFieldsAsync(changed, 4, "Input", "Other", absent: ["ContactEmail"]);
        mismatch = await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, changed[fixture.CodePath], 4));
        Assert.Contains(mismatch.Diagnostics, MissingField);

        await fixture.Client.CloseDocumentAsync(fixture.CodePath);
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task ClosedCodeRefreshUpdatesTheAuthoredSolutionWithoutLosingGeneratedProjection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var changed = fixture.Apply(fixture.Initial, rename);
        await fixture.CommitAsync(rename, changed, 2);
        await fixture.Client.CloseDocumentAsync(fixture.CodePath);

        string diskCode = changed[fixture.CodePath].Replace("public string Read()", "public string ExternalMember => \"disk\";\n    public string Read()", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.CodePath, diskCode);
        var refresh = await fixture.Client.RefreshDiskDocumentsAsync(new(Paths: [fixture.CodePath]));
        Assert.True(refresh.Accepted, refresh.Status);
        Assert.True(refresh.Changed);
        var current = new Dictionary<string, string>(changed, StringComparer.OrdinalIgnoreCase) { [fixture.CodePath] = diskCode };
        await fixture.AssertFieldsAsync(current, 0, "ContactEmail", "ExternalMember", absent: ["Input"]);
        Assert.True((await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, diskCode, 3))).Accepted);
        await fixture.AssertNoErrorsAsync(current, 3);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task CancelledOrInvalidCurrentSourcePlansDoNotCommitAuthoredBuffersOrVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var plan = Assert.IsType<XamlNameProjectionPlan>(rename.NameProjection);
        Assert.True(plan.CurrentSource);
        Assert.Empty(plan.Baselines);
        var changed = fixture.Apply(fixture.Initial, rename);
        var documents = fixture.Documents(changed, 2);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.ApplyXamlNameProjectionAsync(new(plan, documents), cancellation.Token));
        }
        await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);

        var badHash = plan with { ExpectedDocuments = plan.ExpectedDocuments.Select(document => document with { TextHash = new string('0', 64) }).ToArray() };
        var unownedPage = plan with { XamlPath = Path.Combine(fixture.Root, "Unowned.xaml") };
        var legacyPlan = plan with { CurrentSource = false };
        var badBytes = plan with { BaselineSourceBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(Markup + "\n<!-- forged baseline -->")) };
        foreach (var bad in new[] { badHash, unownedPage, legacyPlan, badBytes })
        {
            var result = await fixture.Client.ApplyXamlNameProjectionAsync(new(bad, documents));
            Assert.False(result.Accepted);
            Assert.False(string.IsNullOrWhiteSpace(result.Status));
            // Version 1 remaining valid proves rejected requests did not first
            // synchronize their version-2 authored C# buffers as a side effect.
            await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        }
        var missingAuthoredDocument = await fixture.Client.ApplyXamlNameProjectionAsync(new(plan,
            documents.Where(document => document.Path != fixture.CodePath).ToArray()));
        Assert.False(missingAuthoredDocument.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(missingAuthoredDocument.Status));
        var unownedSource = new UpdateDocumentRequest(Path.Combine(fixture.Root, "Unowned.cs"), "class Unowned { }", 2, Analyze: false);
        var foreignPrerequisite = plan with
        {
            ExpectedDocuments = plan.ExpectedDocuments.Append(new XamlNameProjectionExpectedDocument(unownedSource.Path,
                Hash(Encoding.UTF8.GetBytes(unownedSource.Text)))).ToArray()
        };
        var foreignResult = await fixture.Client.ApplyXamlNameProjectionAsync(new(foreignPrerequisite, documents.Append(unownedSource).ToArray()));
        Assert.False(foreignResult.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(foreignResult.Status));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.ApplyXamlNameProjectionAsync(new(plan, fixture.Documents(changed, 0))));
        await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task ChangedGeneratedDiskOutputDoesNotBlockCurrentFieldsAndIsNeverOverwritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var plan = Assert.IsType<XamlNameProjectionPlan>(rename.NameProjection);
        var changed = fixture.Apply(fixture.Initial, rename);
        Assert.True(plan.CurrentSource);
        Assert.Empty(plan.Baselines);
        string generated = Directory.EnumerateFiles(fixture.Root, "View.g*.cs", SearchOption.AllDirectories).First();
        byte[] before = await File.ReadAllBytesAsync(generated);
        byte[] externallyChanged = before.Concat(Encoding.UTF8.GetBytes("\n// A newer build replaced this compiler output.\n")).ToArray();
        try
        {
            await File.WriteAllBytesAsync(generated, externallyChanged);
            var accepted = await fixture.Client.ApplyXamlNameProjectionAsync(new(plan, fixture.Documents(changed, 2)));
            Assert.True(accepted.Accepted, accepted.Status);
            await fixture.AssertHealthyAsync(changed, 2, "ContactEmail", "Other", absent: ["Input"]);
            await fixture.AssertNavigationAsync(changed, 2, "ContactEmail");
            Assert.Equal(externallyChanged, await File.ReadAllBytesAsync(generated));
            Assert.Equal(Markup, await File.ReadAllTextAsync(fixture.XamlPath));
            Assert.Equal(Code, await File.ReadAllTextAsync(fixture.CodePath));
        }
        finally { await File.WriteAllBytesAsync(generated, before); }
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task LinkedPageProjectionUpdatesEveryOwnedCompilerContextAndReplaysTogether()
    {
        await using var fixture = await Fixture.CreateAsync(linked: true);
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var plan = Assert.IsType<XamlNameProjectionPlan>(rename.NameProjection);
        Assert.True(plan.CurrentSource);
        Assert.Empty(plan.Baselines);
        foreach (string path in fixture.CodePaths)
            Assert.Contains(plan.ExpectedDocuments, document => document.Path == path);
        var changed = fixture.Apply(fixture.Initial, rename);
        // Every authored consumer in every owning context is a transaction
        // prerequisite even though generated compiler baselines are obsolete.
        var incomplete = fixture.Documents(changed, 2).Where(document => document.Path != fixture.CodePaths[1]).ToArray();
        var refused = await fixture.Client.ApplyXamlNameProjectionAsync(new(plan, incomplete));
        Assert.False(refused.Accepted);
        await fixture.AssertHealthyAsync(fixture.Initial, 1, "Input", "Other", absent: ["ContactEmail"]);
        await fixture.CommitAsync(rename, changed, 2);
        await fixture.AssertHealthyAsync(changed, 2, "ContactEmail", "Other", absent: ["Input"]);
        await fixture.Client.RestartAsync();
        await fixture.AssertHealthyAsync(changed, 2, "ContactEmail", "Other", absent: ["Input"]);
        foreach (string path in fixture.CodePaths)
        {
            int usage = changed[path].IndexOf("this.ContactEmail.Text", StringComparison.Ordinal) + 5;
            var definition = Assert.Single(await fixture.Client.GetDefinitionAsync(new(path, usage, 2)));
            Assert.Equal(fixture.XamlPath, definition.Path);
            Assert.Equal("ContactEmail", changed[fixture.XamlPath].Substring(definition.Start, definition.Length));
        }
        var next = await fixture.RenameAsync(changed, 2, "ContactEmail", "Destination");
        var nextPlan = Assert.IsType<XamlNameProjectionPlan>(next.NameProjection);
        Assert.True(nextPlan.CurrentSource);
        foreach (string path in fixture.CodePaths)
            Assert.Contains(nextPlan.ExpectedDocuments, document => document.Path == path);
        var destination = fixture.Apply(changed, next);
        await fixture.CommitAsync(next, destination, 3);
        await fixture.AssertHealthyAsync(destination, 3, "Destination", "Other", absent: ["Input", "ContactEmail"]);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task AuthoredFieldCanReuseARetiredGeneratedNameWithoutBeingCapturedByProjection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var changed = fixture.Apply(fixture.Initial, rename);
        await fixture.CommitAsync(rename, changed, 2);
        string code = changed[fixture.CodePath].Replace("public string Read()", """
            public System.Windows.Controls.TextBox Input = new();
                public string ReadRetiredName() => this.Input.Text;
                public string Read()
            """, StringComparison.Ordinal);
        var update = await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, code, 3));
        Assert.True(update.Accepted);
        Assert.DoesNotContain(update.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        int usage = code.IndexOf("this.Input.Text", StringComparison.Ordinal) + 5;
        var definitions = await fixture.Client.GetDefinitionAsync(new(fixture.CodePath, usage, 3));
        var definition = Assert.Single(definitions);
        Assert.Equal(fixture.CodePath, definition.Path);
        Assert.Equal("Input", code.Substring(definition.Start, definition.Length));
        Assert.True(definition.Start < usage);
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.CodePath, usage, 3, code, fixture.Project,
            [new(fixture.XamlPath, changed[fixture.XamlPath], 2)]));
        Assert.True(references.SymbolFound, string.Join("\n", references.Warnings));
        Assert.NotEmpty(references.Locations);
        Assert.All(references.Locations, location => Assert.Equal(fixture.CodePath, location.Path));
        var authoredRename = await fixture.Client.RenameAsync(new(fixture.CodePath, usage, 3, "AuthoredInput", code, fixture.Project,
            [new(fixture.XamlPath, changed[fixture.XamlPath], 2)]));
        Assert.Null(authoredRename.NameProjection);
        Assert.DoesNotContain(authoredRename.Documents, document => document.Path == fixture.XamlPath && document.Edits.Count != 0);
        var edit = Assert.Single(authoredRename.Documents, document => document.Path == fixture.CodePath);
        string after = ApplyText(code, edit.Edits);
        Assert.Contains("TextBox AuthoredInput =", after);
        Assert.Contains("this.AuthoredInput.Text", after);
        Assert.Contains("this.ContactEmail.Text", after);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task CustomRootNamescopeKeepsCompilerFieldNavigationButWithholdsUnprovenNameRefactoring()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var changed = fixture.Apply(fixture.Initial, rename);
        await fixture.CommitAsync(rename, changed, 2);
        await fixture.AssertNavigationAsync(changed, 2, "ContactEmail");
        string code = changed[fixture.CodePath]
            .Replace("class View : Window", "class View : Window, System.Windows.Markup.INameScope", StringComparison.Ordinal)
            .Replace("public string Read()", """
                object System.Windows.Markup.INameScope.FindName(string name) => null;
                    void System.Windows.Markup.INameScope.RegisterName(string name, object value) { }
                    void System.Windows.Markup.INameScope.UnregisterName(string name) { }
                    public string Read()
                """, StringComparison.Ordinal);
        var update = await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, code, 3));
        Assert.True(update.Accepted);
        Assert.DoesNotContain(update.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        int usage = code.IndexOf("this.ContactEmail.Text", StringComparison.Ordinal) + 5;
        // A root implementing INameScope changes runtime name lookup, but WPF
        // still generates its named child fields. C# F12 uses field provenance.
        var definition = Assert.Single(await fixture.Client.GetDefinitionAsync(new(fixture.CodePath, usage, 3)));
        Assert.Equal(fixture.XamlPath, definition.Path);
        Assert.Equal("ContactEmail", changed[fixture.XamlPath].Substring(definition.Start, definition.Length));
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(changed[fixture.XamlPath])), definition.ExpectedTextHash);
        var references = await fixture.Client.FindSymbolReferencesAsync(new(fixture.CodePath, usage, 3, code, fixture.Project,
            [new(fixture.XamlPath, changed[fixture.XamlPath], 2)]));
        Assert.DoesNotContain(references.Locations, location => location.Path == fixture.XamlPath);
        Assert.NotEmpty(references.Warnings);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.CodePath, usage, 3, "Destination", code, fixture.Project,
            [new(fixture.XamlPath, changed[fixture.XamlPath], 2)])));

        Assert.True((await fixture.Client.UpdateDocumentAsync(new(fixture.CodePath, changed[fixture.CodePath], 4))).Accepted);
        int recoveredUsage = changed[fixture.CodePath].IndexOf("this.ContactEmail.Text", StringComparison.Ordinal) + 5;
        var recovered = Assert.Single(await fixture.Client.GetDefinitionAsync(new(fixture.CodePath, recoveredUsage, 4)));
        Assert.Equal(fixture.XamlPath, recovered.Path);
        Assert.Equal("ContactEmail", changed[fixture.XamlPath].Substring(recovered.Start, recovered.Length));
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Fact]
    public async Task CurrentSourceReplayRejectsIncomingFieldCollisionWithoutAcceptingItsBufferVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SynchronizeAsync(fixture.Initial, 1);
        var rename = await fixture.RenameAsync(fixture.Initial, 1, "Input", "ContactEmail");
        var changed = fixture.Apply(fixture.Initial, rename);
        await fixture.CommitAsync(rename, changed, 2);
        var conflicting = new Dictionary<string, string>(changed, StringComparer.OrdinalIgnoreCase)
        {
            [fixture.CodePath] = changed[fixture.CodePath].Replace("public string Read()",
                "public System.Windows.Controls.TextBox ContactEmail;\n    public string Read()", StringComparison.Ordinal)
        };

        // Replay intentionally accepts current authored text rather than the
        // review's original expected hashes. Its final candidate compilation
        // must still reject a newly introduced generated/authored collision.
        var replay = await fixture.Client.ApplyXamlNameProjectionAsync(new(
            Assert.IsType<XamlNameProjectionPlan>(rename.NameProjection), fixture.Documents(conflicting, 3), Replay: true));
        Assert.False(replay.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(replay.Status));
        await fixture.AssertHealthyAsync(changed, 2, "ContactEmail", "Other", absent: ["Input"]);
        await fixture.AssertNavigationAsync(changed, 2, "ContactEmail");
        await fixture.AssertGeneratedUnchangedAsync();
    }

    private static bool MissingField(WorkspaceDiagnostic diagnostic) => diagnostic.Severity == "Error" && diagnostic.Id is "CS0103" or "CS1061";
    private static bool IsGenerated(string path) => path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string ApplyText(string text, IEnumerable<TextEdit> edits)
    { foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText); return text; }

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
        public Dictionary<string, string> Initial => CodePaths.ToDictionary(path => path, _ => Code, StringComparer.OrdinalIgnoreCase)
            .Append(new KeyValuePair<string, string>(XamlPath, Markup)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        public UpdateDocumentRequest[] Documents(IReadOnlyDictionary<string, string> state, long version)
            => state.Select(pair => new UpdateDocumentRequest(pair.Key, pair.Value, version, Analyze: false)).ToArray();

        public async Task SynchronizeAsync(IReadOnlyDictionary<string, string> state, long version)
        {
            foreach (string path in CodePaths)
                Assert.True((await Client.UpdateDocumentAsync(new(path, state[path], version, Analyze: false))).Accepted);
        }

        public async Task ReconcileAsync(IReadOnlyDictionary<string, string> state, long version)
        {
            var result = await Client.ReconcileNameProjectionsAsync(Documents(state, version));
            Assert.True(result.Accepted, result.Status);
        }

        public async Task<WorkspaceEditResult> RenameAsync(IReadOnlyDictionary<string, string> state, long version,
            string oldName, string newName, bool fromCode = false)
        {
            string path = fromCode ? CodePath : XamlPath;
            int position = fromCode ? state[path].IndexOf("this." + oldName + ".", StringComparison.Ordinal) + 5
                : state[path].IndexOf("x:Name=\"" + oldName + "\"", StringComparison.Ordinal) + 8;
            Assert.True(position >= (fromCode ? 5 : 8));
            return await Client.RenameAsync(new(path, position, version, newName, state[path], Project,
                [new(XamlPath, state[XamlPath], version)]));
        }

        public Dictionary<string, string> Apply(IReadOnlyDictionary<string, string> state, WorkspaceEditResult rename)
        {
            var next = state.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var document in rename.Documents)
            {
                Assert.False(IsGenerated(document.Path));
                Assert.Equal(Hash(Encoding.UTF8.GetBytes(next[document.Path])), document.ExpectedTextHash);
                next[document.Path] = ApplyText(next[document.Path], document.Edits);
            }
            return next;
        }

        public async Task CommitAsync(WorkspaceEditResult rename, IReadOnlyDictionary<string, string> state, long version)
        {
            var plan = Assert.IsType<XamlNameProjectionPlan>(rename.NameProjection);
            Assert.True(plan.CurrentSource);
            Assert.Empty(plan.Baselines);
            var applied = await Client.ApplyXamlNameProjectionAsync(new(plan, Documents(state, version)));
            Assert.True(applied.Accepted, applied.Status);
        }

        public async Task AssertHealthyAsync(IReadOnlyDictionary<string, string> state, long version, string first, string second, string[] absent)
        {
            await AssertNoErrorsAsync(state, version);
            await AssertFieldsAsync(state, version, first, second, absent);
        }

        public async Task AssertNoErrorsAsync(IReadOnlyDictionary<string, string> state, long version)
        {
            foreach (string path in CodePaths)
            {
                var result = await Client.UpdateDocumentAsync(new(path, state[path], version));
                Assert.True(result.Accepted);
                Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == "Error");
            }
        }

        public async Task AssertFieldsAsync(IReadOnlyDictionary<string, string> state, long version, string first, string second, string[] absent)
        {
            foreach (string path in CodePaths)
            {
                int position = state[path].IndexOf("this.", StringComparison.Ordinal) + 5;
                var completion = await Client.GetCompletionsAsync(new(path, position, version));
                Assert.Contains(completion.Items, item => item.DisplayText == first);
                Assert.Contains(completion.Items, item => item.DisplayText == second);
                foreach (string name in absent) Assert.DoesNotContain(completion.Items, item => item.DisplayText == name);
            }
        }

        public async Task AssertNavigationAsync(IReadOnlyDictionary<string, string> state, long version, string name)
        {
            int position = state[CodePath].IndexOf("this." + name + ".", StringComparison.Ordinal) + 5;
            var definitions = await Client.GetDefinitionAsync(new(CodePath, position, version));
            var definition = Assert.Single(definitions, location => location.Path == XamlPath);
            int expected = state[XamlPath].IndexOf("x:Name=\"" + name + "\"", StringComparison.Ordinal) + 8;
            Assert.Equal(expected, definition.Start);
            Assert.Equal(name.Length, definition.Length);
            var references = await Client.FindSymbolReferencesAsync(new(CodePath, position, version, state[CodePath], Project,
                [new(XamlPath, state[XamlPath], version)]));
            Assert.True(references.SymbolFound, string.Join("\n", references.Warnings));
            Assert.Equal(2, references.Locations.Count(location => location.Path == XamlPath));
            Assert.Contains(references.Locations, location => location.Path == CodePath);
            Assert.All(references.Locations, location => Assert.Equal(name, state[location.Path].Substring(location.Start, location.Length)));
        }

        public async Task AssertGeneratedUnchangedAsync()
        {
            foreach (var (path, hash) in _generatedHashes) Assert.Equal(hash, Hash(await File.ReadAllBytesAsync(path)));
        }

        public async Task CaptureGeneratedBaselineAsync()
        {
            _generatedHashes.Clear();
            foreach (string path in Directory.EnumerateFiles(Root, "*.g*.cs", SearchOption.AllDirectories))
                _generatedHashes[path] = Hash(await File.ReadAllBytesAsync(path));
            Assert.NotEmpty(_generatedHashes);
        }

        public static async Task<Fixture> CreateAsync(string framework = "net10.0-windows", bool linked = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "WpfStudio-NameProjection-" + Guid.NewGuid().ToString("N"));
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
                foreach (var operation in new[] { BuildOperation.Restore, BuildOperation.Build })
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
                Assert.NotEmpty(hashes);
                return new(root, projects[0], xaml, codePaths.ToArray(), client, hashes);
            }
            catch { await client.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        { await Client.DisposeAsync(); Directory.Delete(Root, recursive: true); }
    }
}
