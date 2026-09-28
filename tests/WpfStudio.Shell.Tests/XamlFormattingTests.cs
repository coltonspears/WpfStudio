using System.Text;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlFormattingTests
{
    private const string Compact = "<Grid><Button /></Grid>";
    private const string Formatted = "<Grid>\n    <Button />\n</Grid>";
    private static string Hash(string text) => DocumentStore.Hash(Encoding.UTF8.GetBytes(text));
    private static WorkspaceEditResult Result(EditorViewModel editor) => new([
        new(editor.State.Path, editor.State.Version, [new(6, 0, "\n    "), new(16, 0, "\n")], Hash(editor.State.Content))], []);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormatMapsCaretAndSelectionAndUsesOneUnsavedWorkspaceUndo(bool reversed)
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("View.xaml", Compact);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        int start = Compact.IndexOf("Button", StringComparison.Ordinal);
        editor.State.CaretOffset = reversed ? start : start + 6;
        editor.UpdateSelection(start, 6);
        EditorSelection? restored = null;
        editor.SelectionRequested += selection => restored = selection;

        await test.Shell.FormatEditorAsync(editor, _ => Task.FromResult(Result(editor)));

        Assert.Equal(Formatted, editor.State.Content);
        Assert.Equal("Button", editor.SelectedText);
        int expectedStart = Formatted.IndexOf("Button", StringComparison.Ordinal);
        Assert.Equal(expectedStart, editor.SelectionStart);
        Assert.Equal(reversed ? expectedStart : expectedStart + 6, editor.State.CaretOffset);
        Assert.Equal(new EditorSelection(editor.State.CaretOffset, expectedStart, 6), restored);
        Assert.Equal(Compact, await File.ReadAllTextAsync(path));
        Assert.True(editor.State.IsDirty);
        Assert.False(test.Shell.IsPreviewOpen);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(Compact, editor.State.Content);
        Assert.False(editor.State.IsDirty);
    }

    [Fact]
    public async Task StandaloneXamlFormatsCurrentUnsavedTextWithoutWorkerOrAutosave()
    {
        await using var test = new ShellTestContext();
        const string saved = "<Grid />";
        const string unsaved = "<Grid   Width = '150'   Height = \"80\"  Tag = \"{Binding Name, StringFormat='  {0}  '}\"  />";
        string path = await test.CreateFileAsync("Loose.xaml", saved);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = unsaved;
        editor.State.CaretOffset = unsaved.IndexOf("Name", StringComparison.Ordinal) + 2;

        await test.Shell.FormatCommand.ExecuteAsync(null);

        Assert.False(test.Workspace.IsConnected);
        Assert.NotEqual(unsaved, editor.State.Content);
        Assert.Contains("'150'", editor.State.Content);
        Assert.Contains("\"{Binding Name, StringFormat='  {0}  '}\"", editor.State.Content);
        Assert.Equal(editor.State.Content.IndexOf("Name", StringComparison.Ordinal) + 2, editor.State.CaretOffset);
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        Assert.True(editor.State.IsDirty);
        long formattedVersion = editor.State.Version;
        await test.Shell.FormatCommand.ExecuteAsync(null);
        Assert.Equal(formattedVersion, editor.State.Version);
        Assert.Contains("already formatted", test.Shell.Status);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(unsaved, editor.State.Content);
        Assert.True(editor.State.IsDirty);
    }

    [Fact]
    public async Task UnavailableProjectMetadataDoesNotAssumeCanonicalNamespaceHasFrameworkContent()
    {
        await using var test = new ShellTestContext();
        const string markup = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'  Width = '100'><Button/><Button/></Grid>";
        string path = await test.CreateFileAsync("View.xaml", markup);
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new(project, "Test", [new("fixture", "Fixture", project, "net10.0-windows", null, false,
            [new(path, "View.xaml", "Page")])], []);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;

        await test.Shell.FormatCommand.ExecuteAsync(null);

        Assert.False(test.Workspace.IsConnected);
        Assert.NotEqual(markup, editor.State.Content); // Attribute trivia remains safe to format.
        Assert.Contains("><Button/><Button/></Grid>", editor.State.Content);
        Assert.Contains("Project content metadata unavailable; content spacing preserved.", test.Shell.Status);
        Assert.Contains("Project content metadata unavailable; content spacing preserved.", test.Shell.Output);
        Assert.Equal(markup, await File.ReadAllTextAsync(path));
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(markup, editor.State.Content);
    }

    [Fact]
    public async Task MalformedStandaloneXamlIsPreservedAndExplained()
    {
        await using var test = new ShellTestContext();
        const string incomplete = "<Grid><Button Content=\"unfinished";
        string path = await test.CreateFileAsync("Incomplete.xaml", incomplete);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        long version = editor.State.Version;

        await test.Shell.FormatCommand.ExecuteAsync(null);

        Assert.Equal(incomplete, editor.State.Content);
        Assert.Equal(version, editor.State.Version);
        Assert.False(editor.State.IsDirty);
        Assert.Contains("Formatting skipped:", test.Shell.Status);
        Assert.Contains("Formatting:", test.Shell.Output);
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("caret")]
    [InlineData("selection")]
    [InlineData("active-editor")]
    [InlineData("buffer-identity")]
    [InlineData("project-context")]
    [InlineData("workspace")]
    [InlineData("disk")]
    [InlineData("read-only")]
    public async Task AwaitedFormattingRejectsChangedInputOrContext(string change)
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("View.xaml", Compact);
        string other = await test.CreateFileAsync("Other.xaml", "<Grid />");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        var result = Result(editor);
        var reply = new TaskCompletionSource<WorkspaceEditResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = test.Shell.FormatEditorAsync(editor, _ => reply.Task);
        int restorations = 0;
        editor.SelectionRequested += _ => restorations++;
        switch (change)
        {
            case "source-version": editor.State.Content += " "; editor.State.Content = Compact; break;
            case "caret": editor.State.CaretOffset = 2; editor.State.CaretOffset = 0; break;
            case "selection": editor.UpdateSelection(1, 2); editor.UpdateSelection(0, 0); break;
            case "active-editor": await test.Shell.OpenDocumentAsync(other); break;
            case "buffer-identity": test.Store.Close(editor.State); await test.Store.OpenAsync(path); break;
            case "project-context": editor.SetXamlContextUnavailable("reload"); editor.SetXamlContextUnavailable(null); break;
            case "workspace": test.Shell.Workspace = new(await test.CreateFileAsync("Changed.csproj", "<Project />"), "Test", [], []); break;
            case "disk": await File.WriteAllTextAsync(path, "<!-- external -->" + Compact); break;
            case "read-only": File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); break;
        }
        try
        {
            reply.SetResult(result);
            await Assert.ThrowsAnyAsync<Exception>(() => pending);
            Assert.Equal(Compact, editor.State.Content);
            Assert.Equal(0, restorations);
            await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
            Assert.Equal(Compact, editor.State.Content);
        }
        finally { if (change == "read-only") File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("other-target")]
    [InlineData("extra-target")]
    public async Task UnverifiedFormatterTargetsCannotChangeBuffers(string mismatch)
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("View.xaml", Compact);
        string other = await test.CreateFileAsync("Other.xaml", Compact);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        var result = Result(editor);
        var edit = result.Documents[0];
        result = mismatch switch
        {
            "hash" => result with { Documents = [edit with { ExpectedTextHash = Hash("old snapshot") }] },
            "version" => result with { Documents = [edit with { Version = edit.Version + 1 }] },
            "other-target" => result with { Documents = [edit with { Path = other }] },
            _ => result with { Documents = [edit, edit with { Path = other }] }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Shell.FormatEditorAsync(editor, _ => Task.FromResult(result)));
        Assert.Equal(Compact, editor.State.Content);
        Assert.Equal(Compact, await File.ReadAllTextAsync(other));
        Assert.Null(test.Store.Find(other));
    }

    [Fact]
    public async Task FormattingWarningsRemainVisibleWithoutChangingTheEdit()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("View.xaml", Compact);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        var result = Result(editor) with { Warnings = ["Unknown content regions were preserved."] };

        await test.Shell.FormatEditorAsync(editor, _ => Task.FromResult(result));

        Assert.Equal(Formatted, editor.State.Content);
        Assert.Contains("Unknown content regions", test.Shell.Status);
        Assert.Contains("Unknown content regions", test.Shell.Output);
    }

    [Fact]
    public async Task DetailedFormattingWarningsAreBoundedInStatusAndRetainedInOutput()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("View.xaml", Compact);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        string warning = "Preserved content: " + new string('x', 400);
        var result = Result(editor) with { Warnings = [warning, "A second region was preserved."] };

        await test.Shell.FormatEditorAsync(editor, _ => Task.FromResult(result));

        Assert.True(test.Shell.Status.Length <= 250);
        Assert.Contains("See Output", test.Shell.Status);
        Assert.Contains(warning, test.Shell.Output);
        Assert.Contains("A second region", test.Shell.Output);
    }

    [Fact]
    public async Task XamlFormattingSynchronizesUnsavedNamespaceMetadataBeforeUsingProjectTypes()
    {
        await using var test = new ShellTestContext();
        const string presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        const string metadata = "using System.Windows.Markup;\n[assembly: XmlnsDefinition(\"urn:custom\", \"Shadow\")]\nnamespace Shadow { public class Grid { } }";
        const string inner = "<Grid><Button/><Button/></Grid>";
        string markup = "<Window xmlns=\"" + presentation + "\">" + inner + "</Window>";
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        string code = await test.CreateFileAsync("Namespaces.cs", metadata);
        string view = await test.CreateFileAsync("View.xaml", markup);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        await test.Workspace.LoadAsync(new(project, "Release"));
        await test.Shell.OpenDocumentAsync(code);
        var codeEditor = test.Shell.ActiveDocument!;
        await codeEditor.SyncAsync();
        await test.Shell.OpenDocumentAsync(view);
        var xamlEditor = test.Shell.ActiveDocument!;
        var before = await test.Workspace.FormatXamlAsync(new(view, markup, xamlEditor.State.Version));
        var initialEdits = Assert.Single(before.Documents);
        Assert.DoesNotContain(inner, WorkspaceEditTransaction.ApplyTextEdits(markup, initialEdits.Edits));

        // This unsaved mapping makes Grid ambiguous with the real WPF type.
        // Format immediately, without waiting for the normal analysis debounce.
        codeEditor.State.Content = metadata.Replace("urn:custom", presentation, StringComparison.Ordinal);
        await test.Shell.FormatCommand.ExecuteAsync(null);

        Assert.Contains(inner, xamlEditor.State.Content);
        Assert.DoesNotContain("conservative XAML formatting was used.", test.Shell.Output);
        Assert.DoesNotContain("Project content metadata unavailable", test.Shell.Output);
        Assert.Equal(metadata, await File.ReadAllTextAsync(code));
        Assert.True(codeEditor.State.IsDirty);
        var after = await test.Workspace.FormatXamlAsync(new(view, markup, xamlEditor.State.Version));
        Assert.Contains(inner, WorkspaceEditTransaction.ApplyTextEdits(markup, Assert.Single(after.Documents).Edits));
    }

    [Fact]
    public async Task CSharpFormatCommandStillPreservesSelectionAndWorkspaceUndo()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        const string source = "namespace Demo;class Widget{public int Value{get;set;}}";
        string path = await test.CreateFileAsync("Widget.cs", source);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        await test.Workspace.LoadAsync(new(project, "Release"));
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        int start = source.IndexOf("Value", StringComparison.Ordinal);
        editor.UpdateSelection(start, 5);
        editor.State.CaretOffset = start + 5;

        await test.Shell.FormatCommand.ExecuteAsync(null);

        Assert.NotEqual(source, editor.State.Content);
        Assert.Equal("Value", editor.SelectedText);
        Assert.Equal(editor.State.Content.IndexOf("Value", StringComparison.Ordinal) + 5, editor.State.CaretOffset);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(source, editor.State.Content);
    }
}
