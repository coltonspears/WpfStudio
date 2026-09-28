using System.Text;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlSymbolRefactoringTests
{
    private const string Code = "namespace Demo; public class Customer { public string Name { get; set; } = \"\"; }";
    private const string Markup = "<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:vm=\"clr-namespace:Demo\" d:DataContext=\"{d:DesignInstance Type=vm:Customer}\" Text=\"{Binding Name}\" />";
    private const string OtherMarkup = "<TextBlock Text=\"unchanged\" />";
    private static string Hash(string text) => DocumentStore.Hash(Encoding.UTF8.GetBytes(text));

    private static WorkspaceEditResult Rename(EditorViewModel source, string code, string prerequisite, bool editSource = true) => new([
        new(source.State.Path, source.State.Version, editSource ? [new(Markup.IndexOf("Name}", StringComparison.Ordinal), 4, "FullName")] : [], Hash(source.State.Content)),
        new(code, 0, [new(Code.IndexOf("Name", StringComparison.Ordinal), 4, "FullName")], Hash(Code)),
        new(prerequisite, 0, [], Hash(OtherMarkup))], ["Bindings with an unknown runtime DataContext are excluded."]);

    private static async Task PreviewReadyAsync(ShellTestContext test, Task operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!test.Shell.IsPreviewOpen && !operation.IsCompleted) await Task.Delay(10, timeout.Token);
        Assert.True(test.Shell.IsPreviewOpen, test.Shell.Status + string.Join("\n", test.Dialogs.Errors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossLanguageRenamePreviewsOnlyChangesAndCommitsUnsavedBuffersWithOneUndo(bool codeAlreadyOpen)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("Customer.cs", Code);
        string prerequisite = await test.CreateFileAsync("Other.xaml", OtherMarkup);
        if (codeAlreadyOpen) await test.Shell.OpenDocumentAsync(code);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        Task operation = test.Shell.PreviewSymbolRenameAsync(editor, Rename(editor, code, prerequisite));
        await PreviewReadyAsync(test, operation);

        Assert.Equal(2, test.Shell.PreviewChanges.Count);
        Assert.DoesNotContain(test.Shell.PreviewChanges, change => change.Path == prerequisite);
        Assert.Contains("unknown runtime", test.Shell.PreviewWarnings);
        Assert.Equal(Markup, editor.State.Content);
        Assert.DoesNotContain(test.Shell.Documents, document => document.State.Path == prerequisite);
        if (!codeAlreadyOpen) Assert.DoesNotContain(test.Shell.Documents, document => document.State.Path == code);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation;

        Assert.Contains("{Binding FullName}", editor.State.Content);
        Assert.Contains("string FullName", test.Store.Find(code)!.Content);
        Assert.Contains(test.Shell.Documents, document => document.State.Path == code);
        Assert.Null(test.Store.Find(prerequisite));
        Assert.DoesNotContain(test.Shell.Documents, document => document.State.Path == prerequisite);
        Assert.Equal(Code, await File.ReadAllTextAsync(code));
        Assert.Equal(Markup, await File.ReadAllTextAsync(view));
        Assert.Contains("Partial coverage", test.Shell.Status);
        // A no-op prerequisite has no undo dependency.
        await test.Shell.OpenDocumentAsync(prerequisite);
        test.Store.Find(prerequisite)!.Content += " ";
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(Markup, editor.State.Content);
        Assert.Equal(Code, test.Store.Find(code)?.Content ?? await File.ReadAllTextAsync(code));
        Assert.Equal(OtherMarkup + " ", test.Store.Find(prerequisite)!.Content);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task NoOpActiveXamlStaysCleanAndCancelDoesNotOpenAffectedTabs()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("Customer.cs", Code);
        string prerequisite = await test.CreateFileAsync("Other.xaml", OtherMarkup);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        long version = editor.State.Version;
        var result = Rename(editor, code, prerequisite, editSource: false);
        Task cancelled = test.Shell.PreviewSymbolRenameAsync(editor, result);
        await PreviewReadyAsync(test, cancelled);
        Assert.Single(test.Shell.PreviewChanges);
        test.Shell.CancelPreviewCommand.Execute(null);
        await cancelled;
        Assert.Single(test.Shell.Documents);
        Assert.Null(test.Store.Find(code));
        Task applied = test.Shell.PreviewSymbolRenameAsync(editor, result);
        await PreviewReadyAsync(test, applied);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await applied;
        Assert.False(editor.State.IsDirty);
        Assert.Equal(version, editor.State.Version);
        editor.State.Content += " ";
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(Markup + " ", editor.State.Content);
        Assert.Equal(Code, test.Store.Find(code)!.Content);
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("target-version")]
    [InlineData("other-buffer")]
    [InlineData("buffer-identity")]
    [InlineData("caret")]
    [InlineData("selection")]
    [InlineData("project-context")]
    [InlineData("configuration")]
    [InlineData("workspace")]
    [InlineData("source-disk")]
    [InlineData("target-disk")]
    [InlineData("prerequisite-disk")]
    [InlineData("read-only")]
    public async Task ReviewCannotApplyAfterItsSnapshotChanges(string change)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("Customer.cs", Code);
        string prerequisite = await test.CreateFileAsync("Other.xaml", OtherMarkup);
        string unrelated = await test.CreateFileAsync("Notes.txt", "notes");
        await test.Shell.OpenDocumentAsync(unrelated);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        Task operation = test.Shell.PreviewSymbolRenameAsync(editor, Rename(editor, code, prerequisite));
        await PreviewReadyAsync(test, operation);
        var targetState = test.Store.Find(code)!;
        switch (change)
        {
            case "source-version": editor.State.Content += " "; editor.State.Content = Markup; break;
            case "target-version": test.Store.Find(code)!.Content += " "; test.Store.Find(code)!.Content = Code; break;
            case "other-buffer": test.Store.Find(unrelated)!.Content += "new"; break;
            case "buffer-identity": test.Store.Close(test.Store.Find(code)!); await test.Store.OpenAsync(code); break;
            case "caret": editor.State.CaretOffset = 1; editor.State.CaretOffset = 0; break;
            case "selection": editor.UpdateSelection(1, 2); editor.UpdateSelection(0, 0); break;
            case "project-context": editor.SetXamlContextUnavailable("reload"); editor.SetXamlContextUnavailable(null); break;
            case "configuration": test.Shell.Configuration = "Release"; break;
            case "workspace":
                string project = await test.CreateFileAsync("Changed.csproj", "<Project />");
                test.Shell.Workspace = new(project, "Test", [], []); break;
            case "source-disk": await File.WriteAllTextAsync(view, Markup + "<!-- external -->"); break;
            case "target-disk": await File.WriteAllTextAsync(code, Code + "// external"); break;
            case "prerequisite-disk": await File.WriteAllTextAsync(prerequisite, OtherMarkup + "<!-- external -->"); break;
            case "read-only": File.SetAttributes(code, File.GetAttributes(code) | FileAttributes.ReadOnly); break;
        }
        try
        {
            test.Shell.AcceptPreviewCommand.Execute(null);
            await Assert.ThrowsAnyAsync<Exception>(() => operation);
            Assert.Equal(Markup, editor.State.Content);
            Assert.Equal(Code, targetState.Content);
            await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
            Assert.Equal(Code, targetState.Content);
        }
        finally { if (change == "read-only") File.SetAttributes(code, File.GetAttributes(code) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task CancelReleasesClosedSnapshotsSoNextPreviewReadsNewDiskBytes()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("Customer.cs", Code);
        string prerequisite = await test.CreateFileAsync("Other.xaml", OtherMarkup);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var result = Rename(editor, code, prerequisite);
        Task first = test.Shell.PreviewSymbolRenameAsync(editor, result);
        await PreviewReadyAsync(test, first);
        test.Shell.CancelPreviewCommand.Execute(null);
        await first;
        Assert.Null(test.Store.Find(code));
        Assert.Null(test.Store.Find(prerequisite));

        string newCode = Code + "\n// changed after cancel";
        string newPrerequisite = OtherMarkup + "<!-- changed after cancel -->";
        await File.WriteAllTextAsync(code, newCode);
        await File.WriteAllTextAsync(prerequisite, newPrerequisite);
        var fresh = result with { Documents = result.Documents.Select(edit => edit with
        { ExpectedTextHash = edit.Path == code ? Hash(newCode) : edit.Path == prerequisite ? Hash(newPrerequisite) : edit.ExpectedTextHash }).ToArray() };
        Task second = test.Shell.PreviewSymbolRenameAsync(editor, fresh);
        await PreviewReadyAsync(test, second);
        Assert.Contains(test.Shell.PreviewChanges, change => change.Path == code && change.Before == newCode);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await second;
        Assert.Contains("changed after cancel", test.Store.Find(code)!.Content);
        Assert.Contains("string FullName", test.Store.Find(code)!.Content);
        Assert.Null(test.Store.Find(prerequisite));
        Assert.Equal(newCode, await File.ReadAllTextAsync(code));
        Assert.Equal(newPrerequisite, await File.ReadAllTextAsync(prerequisite));
    }

    [Theory]
    [InlineData("ContactView.g.cs")]
    [InlineData("ContactView.g.i.cs")]
    public async Task GeneratedNameFieldCannotBecomeAnAuthoredRenameTarget(string generatedName)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("ContactView.xaml", Markup);
        string generated = await test.CreateFileAsync(generatedName, "partial class ContactView { object ContactEmail; }");
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new(project, "Test", [new("Fixture", "Fixture", project, "net10.0-windows", null, false,
            [new(view, "ContactView.xaml", "Page"), new(generated, generatedName, "Compile", true)])], []);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        string generatedText = await File.ReadAllTextAsync(generated);
        var proposed = new WorkspaceEditResult([
            new(view, editor.State.Version, [new(Markup.IndexOf("Name}", StringComparison.Ordinal), 4, "EmailInput")], Hash(Markup)),
            new(generated, 0, [new(generatedText.IndexOf("ContactEmail", StringComparison.Ordinal), "ContactEmail".Length, "EmailInput")], Hash(generatedText))], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Shell.PreviewSymbolRenameAsync(editor, proposed));

        Assert.False(test.Shell.IsPreviewOpen);
        Assert.Equal(Markup, editor.State.Content);
        Assert.Equal(generatedText, await File.ReadAllTextAsync(generated));
        Assert.DoesNotContain(test.Shell.Documents, document => document.State.Path == generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyAndMismatchedSnapshotTargetsAreRejectedBeforePreview(bool readOnly)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("Customer.cs", readOnly ? Code : Code + "// external");
        string prerequisite = await test.CreateFileAsync("Other.xaml", OtherMarkup);
        await test.Shell.OpenDocumentAsync(view);
        if (readOnly) File.SetAttributes(code, File.GetAttributes(code) | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Shell.PreviewSymbolRenameAsync(test.Shell.ActiveDocument!, Rename(test.Shell.ActiveDocument!, code, prerequisite)));
            Assert.False(test.Shell.IsPreviewOpen);
            Assert.Equal(Markup, test.Store.Find(view)!.Content);
        }
        finally { if (readOnly) File.SetAttributes(code, File.GetAttributes(code) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task ReferenceNavigationPreservesLinkedProjectContextAndRejectsChangedText()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("Shared.xaml", Markup);
        string first = await test.CreateFileAsync("First.csproj", "<Project />");
        string second = await test.CreateFileAsync("Second.csproj", "<Project />");
        WorkspaceProject Project(string path, string name) => new(name, name, path, "net10.0", null, false, [new(view, "Shared.xaml", "Page")]);
        test.Shell.Workspace = new(first, "Test", [Project(first, "First"), Project(second, "Second")], []);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        editor.XamlProject = editor.XamlProjects[0];
        int at = Markup.IndexOf("Name}", StringComparison.Ordinal);
        test.Shell.PublishSymbolReferences(new([new(view, at, 4, 1, at + 1, "Name", Hash(Markup), second, "Second")], ["Unknown DataContext omitted"]));
        var result = Assert.Single(test.Shell.SearchResults);
        Assert.Equal(second, result.ProjectPath);
        Assert.Contains("Second", result.Location);
        Assert.Contains("Unknown DataContext", test.Shell.Status);

        await test.Shell.NavigateSearchCommand.ExecuteAsync(result);
        Assert.Equal(second, editor.XamlProjectPath);
        Assert.Equal(at, editor.State.CaretOffset);
        editor.State.Content = "<!-- new -->" + Markup;
        editor.State.CaretOffset = 0;
        await test.Shell.NavigateSearchCommand.ExecuteAsync(result);
        Assert.Equal(0, editor.State.CaretOffset);
        Assert.Contains("Find references again", test.Shell.Status);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task ReferenceQuerySettlesClosedTypesAndInvalidatesOtherXamlEditors()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string code = await test.CreateFileAsync("Customer.cs", Code);
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string other = await test.CreateFileAsync("Other.xaml", Markup.Replace("Name}", "Label}"));
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        // Omit the shell's disk watcher: this isolates refresh performed by the
        // symbol command itself instead of relying on a periodic project scan.
        await test.Workspace.LoadAsync(new(project, "Release"));
        await test.Shell.OpenDocumentAsync(other);
        var otherEditor = test.Shell.ActiveDocument!;
        await otherEditor.RefreshAnalysisAsync();
        Assert.Contains(otherEditor.Diagnostics, diagnostic => diagnostic.Id == "XAMLBIND001");
        long previousContext = otherEditor.XamlContextRevision;
        await test.Shell.OpenDocumentAsync(view);
        test.Shell.ActiveDocument!.State.CaretOffset = Markup.IndexOf("Name}", StringComparison.Ordinal) + 1;
        await File.WriteAllTextAsync(code, Code.Insert(Code.LastIndexOf('}'), " public string Label => Name; "));

        await test.Shell.FindReferencesCommand.ExecuteAsync(null);

        Assert.Contains(test.Shell.SearchResults, result => result.Path == code);
        Assert.Contains(test.Shell.SearchResults, result => result.Path == view);
        Assert.True(otherEditor.XamlContextRevision > previousContext);
        await otherEditor.RefreshAnalysisAsync();
        Assert.DoesNotContain(otherEditor.Diagnostics, diagnostic => diagnostic.Id == "XAMLBIND001");
        Assert.Contains("symbol reference(s)", test.Shell.Status);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task WorkerReferencesIncludeUnsavedXamlAndCSharpAndSemanticChangesInvalidateCSharpRenameReview()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string code = await test.CreateFileAsync("Customer.cs", Code + "\nclass Consumer { string Read(Demo.Customer c) => c.Name; }");
        string view = await test.CreateFileAsync("View.xaml", Markup.Replace("Name}", "Missing}"));
        string closed = await test.CreateFileAsync("Closed.xaml", Markup);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(project));
        await test.Shell.OpenDocumentAsync(code);
        var codeEditor = test.Shell.ActiveDocument!;
        await codeEditor.SyncAsync();
        await test.Shell.OpenDocumentAsync(view);
        var xamlEditor = test.Shell.ActiveDocument!;
        xamlEditor.State.Content = Markup;
        xamlEditor.State.CaretOffset = Markup.IndexOf("Name}", StringComparison.Ordinal) + 1;

        await test.Shell.FindReferencesCommand.ExecuteAsync(null);
        Assert.Contains(test.Shell.SearchResults, result => result.Path == code);
        Assert.Contains(test.Shell.SearchResults, result => result.Path == view);
        Assert.Contains(test.Shell.SearchResults, result => result.Path == closed);
        Assert.All(test.Shell.SearchResults, result => Assert.NotNull(result.ExpectedTextHash));

        test.Shell.ActiveDocument = codeEditor;
        codeEditor.State.CaretOffset = Code.IndexOf("Name", StringComparison.Ordinal) + 1;
        test.Dialogs.Prompts.Enqueue("FullName");
        Task operation = test.Shell.RenameSymbolCommand.ExecuteAsync(null);
        await PreviewReadyAsync(test, operation);
        Assert.Equal(3, test.Shell.PreviewChanges.Count);
        // Simulate a different C# buffer reaching the worker while review is open.
        // The source editor is C#, so XAML-only editor epochs cannot guard this.
        string changedModel = codeEditor.State.Content + "\n// refreshed semantic model";
        await test.Workspace.UpdateDocumentAsync(new(code, changedModel, codeEditor.State.Version + 1, Analyze: false));
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation;
        Assert.DoesNotContain("FullName", codeEditor.State.Content);
        Assert.Equal(Markup, xamlEditor.State.Content);
        Assert.Equal(Markup, await File.ReadAllTextAsync(closed));
        Assert.Contains("changed", test.Shell.Status);
    }
}
