using System.Text;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlEventCodeActionTests
{
    private const string Markup = "<local:View xmlns:local=\"clr-namespace:Demo\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"Demo.View\" Activated=\"OnActivated\" />";
    private const string Code = "namespace Demo;\npublic partial class View\n{\n    public event System.EventHandler? Activated;\n}\n";
    private const string Handler = "    private void OnActivated(object? sender, System.EventArgs e)\n    {\n    }\n";
    private static string Hash(string text) => DocumentStore.Hash(Encoding.UTF8.GetBytes(text));

    private static XamlCodeAction Action(EditorViewModel editor, string codePath, string code = Code, long codeVersion = 0) => new("Create event handler 'OnActivated'",
        new DocumentEdits(editor.State.Path, editor.State.Version, [], Hash(editor.State.Content)),
        [new DocumentEdits(codePath, codeVersion, [new(code.LastIndexOf('}'), 0, Handler)], Hash(code))]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerEditsUseOneUndoAndOpenCodeBehindWithoutDirtyingUnchangedXaml(bool alreadyOpen)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        if (alreadyOpen) await test.Shell.OpenDocumentAsync(code);
        var previousCodeState = test.Store.Find(code);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        long version = editor.State.Version;
        var action = Action(editor, code, codeVersion: previousCodeState?.Version ?? 0);

        await test.Shell.ApplyXamlCodeActionAsync(editor, action);

        Assert.Equal(code, test.Shell.ActiveDocument!.State.Path);
        var codeState = test.Shell.ActiveDocument.State;
        if (alreadyOpen) Assert.Same(previousCodeState, codeState);
        Assert.Contains(Handler, codeState.Content);
        Assert.True(codeState.IsDirty);
        Assert.False(editor.State.IsDirty);
        Assert.Equal(version, editor.State.Version);
        Assert.Equal(Code, await File.ReadAllTextAsync(code));
        Assert.Equal(Markup, await File.ReadAllTextAsync(view));
        Assert.Equal(codeState.Content.IndexOf("private void OnActivated", StringComparison.Ordinal), codeState.CaretOffset);

        // The XAML prerequisite is not an edit: later XAML typing must not
        // block undo of the generated C# member.
        editor.State.Content += " ";
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(Code, codeState.Content);
        Assert.Equal(Markup + " ", editor.State.Content);
        Assert.False(codeState.IsDirty);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task XamlAndHandlerChangesCommitAndUndoTogether()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup.Replace("OnActivated", ""));
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        string original = editor.State.Content;
        int insert = original.IndexOf("Activated=\"", StringComparison.Ordinal) + "Activated=\"".Length;
        var action = Action(editor, code);
        action = action with { Edit = action.Edit with { Edits = [new(insert, 0, "OnActivated")] } };

        await test.Shell.ApplyXamlCodeActionAsync(editor, action);
        Assert.Equal(Markup, editor.State.Content);
        Assert.Contains(Handler, test.Store.Find(code)!.Content);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(original, editor.State.Content);
        Assert.Equal(Code, test.Store.Find(code)!.Content);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("source-hash")]
    [InlineData("target-version")]
    [InlineData("target-hash")]
    [InlineData("source-disk")]
    [InlineData("target-disk")]
    [InlineData("active-editor")]
    public async Task ObsoletePrerequisitesLeaveEveryBufferUntouched(string change)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        await test.Shell.OpenDocumentAsync(code);
        var target = test.Shell.ActiveDocument!;
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var action = Action(editor, code, codeVersion: target.State.Version);
        switch (change)
        {
            case "source-version": editor.State.Content += " "; editor.State.Content = Markup; break;
            case "source-hash": action = action with { Edit = action.Edit with { ExpectedTextHash = Hash("different source") } }; break;
            case "target-version": target.State.Content += " "; target.State.Content = Code; break;
            case "target-hash": action = action with { AdditionalEdits = [action.AdditionalEdits![0] with { ExpectedTextHash = Hash("different target") }] }; break;
            case "source-disk": await File.WriteAllTextAsync(view, Markup + "<!-- external -->"); break;
            case "target-disk": await File.WriteAllTextAsync(code, Code + "// external"); break;
            case "active-editor": test.Shell.ActiveDocument = target; break;
        }
        string sourceBefore = editor.State.Content, targetBefore = target.State.Content;

        await Assert.ThrowsAnyAsync<Exception>(() => test.Shell.ApplyXamlCodeActionAsync(editor, action));

        Assert.Equal(sourceBefore, editor.State.Content);
        Assert.Equal(targetBefore, target.State.Content);
        Assert.DoesNotContain(Handler, target.State.Content);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(sourceBefore, editor.State.Content);
        Assert.Equal(targetBefore, target.State.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyCodeBehindIsRejectedBeforeAnyXamlMutation(bool alreadyOpen)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        if (alreadyOpen) await test.Shell.OpenDocumentAsync(code);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var action = Action(editor, code);
        action = action with { Edit = action.Edit with { Edits = [new(0, 0, "<!-- should not apply -->")] } };
        File.SetAttributes(code, File.GetAttributes(code) | FileAttributes.ReadOnly);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Shell.ApplyXamlCodeActionAsync(editor, action));
            Assert.Contains("read-only", error.Message);
            Assert.Equal(Markup, editor.State.Content);
            Assert.Equal(Code, test.Store.Find(code)?.Content ?? await File.ReadAllTextAsync(code));
        }
        finally { File.SetAttributes(code, File.GetAttributes(code) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task DuplicateOrUnguardedTargetsAreRejectedBeforeMutation()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var action = Action(editor, code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Shell.ApplyXamlCodeActionAsync(editor,
            action with { AdditionalEdits = [action.AdditionalEdits![0], action.AdditionalEdits[0]] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Shell.ApplyXamlCodeActionAsync(editor,
            action with { AdditionalEdits = [action.AdditionalEdits![0] with { ExpectedTextHash = null }] }));
        Assert.Equal(Markup, editor.State.Content);
        Assert.Equal(Code, await File.ReadAllTextAsync(code));
    }

    [Fact]
    public async Task WorkerHandlerActionRejectsChangedSelectionAndContextThenAppliesCurrentProposal()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(project));
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        int handler = Markup.IndexOf("OnActivated", StringComparison.Ordinal);
        editor.State.CaretOffset = handler + 1;
        editor.UpdateSelection(handler + 1, 0);
        await editor.RefreshQuickFixesAsync(handler + 1);
        var oldSelection = Assert.Single(editor.QuickFixes, item => item.Action.AdditionalEdits?.Count > 0);
        editor.UpdateSelection(handler, 3);
        editor.UpdateSelection(handler + 1, 0);
        await oldSelection.ApplyCommand.ExecuteAsync(oldSelection.Action);
        Assert.Equal(Code, test.Store.Find(code)?.Content ?? await File.ReadAllTextAsync(code));

        await editor.RefreshQuickFixesAsync(handler + 1);
        var oldContext = Assert.Single(editor.QuickFixes, item => item.Action.AdditionalEdits?.Count > 0);
        editor.SetXamlContextUnavailable("Reload required");
        editor.SetXamlContextUnavailable(null);
        await oldContext.ApplyCommand.ExecuteAsync(oldContext.Action);
        Assert.Equal(Code, test.Store.Find(code)?.Content ?? await File.ReadAllTextAsync(code));

        await editor.RefreshQuickFixesAsync(handler + 1);
        var current = Assert.Single(editor.QuickFixes, item => item.Action.AdditionalEdits?.Count > 0);
        Assert.Empty(current.Action.Edit.Edits);
        Assert.Equal(Hash(Markup), current.Action.Edit.ExpectedTextHash);
        Assert.All(current.Action.AdditionalEdits!, edit => Assert.NotNull(edit.ExpectedTextHash));
        await current.ApplyCommand.ExecuteAsync(current.Action);
        Assert.Equal(code, test.Shell.ActiveDocument!.State.Path);
        Assert.Contains("OnActivated(", test.Shell.ActiveDocument.State.Content);
        Assert.False(editor.State.IsDirty);
        Assert.Equal(Code, await File.ReadAllTextAsync(code));
        var definition = Assert.Single(await editor.XamlDefinitionAsync(handler + 1));
        Assert.Equal(code, definition.Path);
        Assert.Equal(test.Shell.ActiveDocument.State.Content.IndexOf("OnActivated", StringComparison.Ordinal), definition.Start);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(Code, test.Shell.ActiveDocument.State.Content);
        Assert.Empty(test.Dialogs.Errors);
    }
}
