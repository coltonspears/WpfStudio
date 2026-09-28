using System.Text;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlNameProjectionShellTests
{
    private const string Markup = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="ProjectionShell.View">
          <StackPanel><TextBox x:Name="Input"/><TextBlock Text="{Binding Text, ElementName=Input}"/></StackPanel>
        </Window>
        """;
    private const string Code = "using System.Windows; namespace ProjectionShell; public partial class View : Window { public View() { InitializeComponent(); } public string Read() => this.Input.Text; }";
    private const string Observer = "namespace ProjectionShell; public partial class View { public string Observe() => this.Input.Text; }";
    private static string Hash(string text) => DocumentStore.Hash(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelNeverInstallsAPlanAndFailedPostApplyRefreshKeepsOneUndo(bool accept)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        string generated = await test.CreateFileAsync("obj/View.g.cs", "// compiler-owned bytes");
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var plan = new XamlNameProjectionPlan(view, Convert.ToBase64String(Encoding.UTF8.GetBytes(Markup)),
            [new(Path.Combine(test.Root, "Fixture.csproj"), generated, Hash("// compiler-owned bytes"))],
            [new(Markup.IndexOf("Input", StringComparison.Ordinal), "ContactEmail")], []);
        var result = new WorkspaceEditResult([
            new(view, editor.State.Version, [new(Markup.IndexOf("Input", StringComparison.Ordinal), 5, "ContactEmail")], Hash(Markup)),
            new(code, 0, [new(Code.IndexOf("Input", StringComparison.Ordinal), 5, "ContactEmail")], Hash(Code))], [], plan);
        int notifications = 0;
        test.Workspace.NameProjectionChanged += (_, _) => notifications++;
        Task operation = test.Shell.PreviewSymbolRenameAsync(editor, result);
        await ReadyAsync(test, operation);
        if (accept) test.Shell.AcceptPreviewCommand.Execute(null);
        else test.Shell.CancelPreviewCommand.Execute(null);
        await operation;

        Assert.Equal(0, notifications);
        Assert.Equal("// compiler-owned bytes", await File.ReadAllTextAsync(generated));
        Assert.Equal(Markup, await File.ReadAllTextAsync(view));
        Assert.Equal(Code, await File.ReadAllTextAsync(code));
        if (accept)
        {
            Assert.Contains("ContactEmail", editor.State.Content);
            Assert.Contains("ContactEmail", test.Store.Find(code)!.Content);
            Assert.Contains("Rename applied in editor buffers", test.Shell.Status);
            Assert.Contains("refresh is pending", test.Shell.Status);
            await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
            Assert.Equal(Code, test.Store.Find(code)!.Content);
        }
        else
        {
            Assert.Single(test.Shell.Documents);
            Assert.Null(test.Store.Find(code));
            Assert.DoesNotContain("refresh is pending", test.Shell.Status);
        }
        Assert.Equal(Markup, editor.State.Content);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task ReviewedRenameRefreshesUnchangedCsharpThenRepeatsRestartsAndUndoesWithoutGeneratedWrites()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        string view = await test.CreateFileAsync("View.xaml", Markup);
        string code = await test.CreateFileAsync("View.xaml.cs", Code);
        string observerPath = await test.CreateFileAsync("Observer.cs", Observer);
        foreach (var operation in new[] { BuildOperation.Restore, BuildOperation.Build })
        {
            var built = await new BuildService().RunAsync(new(project, operation, "Release"));
            Assert.True(built.ExitCode == 0, string.Join("\n", built.Diagnostics.Select(item => item.Message)));
        }
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(project, "Release"));
        // MSBuildWorkspace may normalize generated resource URIs during its
        // initial design-time build. The editing invariant starts after load.
        var generated = Directory.EnumerateFiles(Path.Combine(test.Root, "obj"), "*.g.cs", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => File.ReadAllBytes(path));
        Assert.NotEmpty(generated);
        await test.Shell.OpenDocumentAsync(observerPath);
        var observer = test.Shell.ActiveDocument!;
        observer.State.Content = Observer.Replace("Input", "ContactEmail", StringComparison.Ordinal);
        long observerVersion = observer.State.Version;
        await test.Shell.OpenDocumentAsync(code);
        var codeEditor = test.Shell.ActiveDocument!;
        await test.Shell.OpenDocumentAsync(view);
        var xaml = test.Shell.ActiveDocument!;
        await codeEditor.SyncAsync();
        await xaml.SyncAsync();
        await test.Shell.RefreshProjectXamlAnalysisAsync();
        await observer.RefreshAnalysisAsync();
        Assert.Contains(observer.Diagnostics, MissingField);

        var first = await RenameAsync(test, xaml, project, "Input", "ContactEmail");
        Assert.NotNull(first.NameProjection);
        Task cancelled = test.Shell.PreviewSymbolRenameAsync(xaml, first);
        await ReadyAsync(test, cancelled);
        test.Shell.CancelPreviewCommand.Execute(null);
        await cancelled;
        await FieldsAsync(codeEditor, "Input", "ContactEmail");
        Assert.Equal(Markup, xaml.State.Content);

        await ApplyAsync(test, xaml, first);
        Assert.DoesNotContain("refresh is pending", test.Shell.Status);
        Assert.Equal(observerVersion, observer.State.Version);
        // This editor was not changed by the rename. Its existing error must
        // disappear through the dedicated projection event, without an edit.
        await UntilAsync(() => !observer.Diagnostics.Any(MissingField));
        await FieldsAsync(codeEditor, "ContactEmail", "Input");
        Assert.Contains("ContactEmail", xaml.State.Content);

        test.Shell.ActiveDocument = xaml;
        var second = await RenameAsync(test, xaml, project, "ContactEmail", "Destination");
        await ApplyAsync(test, xaml, second);
        await FieldsAsync(codeEditor, "Destination", "ContactEmail");
        await test.Shell.RestartWorkspaceCommand.ExecuteAsync(null);
        Assert.True(test.Workspace.IsConnected, test.Shell.Status);
        await FieldsAsync(codeEditor, "Destination", "Input");
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        await FieldsAsync(codeEditor, "ContactEmail", "Destination");
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        await FieldsAsync(codeEditor, "Input", "ContactEmail");
        Assert.Equal(Markup, xaml.State.Content);
        Assert.Equal(Code, codeEditor.State.Content);
        Assert.Equal(Observer.Replace("Input", "ContactEmail", StringComparison.Ordinal), observer.State.Content);
        Assert.Equal(Markup, await File.ReadAllTextAsync(view));
        Assert.Equal(Code, await File.ReadAllTextAsync(code));
        foreach (var pair in generated) Assert.Equal(pair.Value, await File.ReadAllBytesAsync(pair.Key));
        Assert.Empty(test.Dialogs.Errors);
    }

    private static bool MissingField(WorkspaceDiagnostic diagnostic) => diagnostic.Id is "CS0103" or "CS1061";
    private static async Task<WorkspaceEditResult> RenameAsync(ShellTestContext test, EditorViewModel xaml, string project, string before, string after)
    {
        int position = xaml.State.Content.IndexOf("x:Name=\"" + before, StringComparison.Ordinal) + 8;
        Assert.True(position >= 8);
        return await test.Workspace.RenameAsync(new(xaml.State.Path, position, xaml.State.Version, after, xaml.State.Content, project,
            test.Shell.Documents.Where(document => document.IsXaml).Select(document => new XamlDocumentOverlay(document.State.Path, document.State.Content, document.State.Version)).ToArray()));
    }
    private static async Task ApplyAsync(ShellTestContext test, EditorViewModel xaml, WorkspaceEditResult result)
    {
        test.Shell.ActiveDocument = xaml;
        Task operation = test.Shell.PreviewSymbolRenameAsync(xaml, result);
        await ReadyAsync(test, operation);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation;
    }
    private static async Task FieldsAsync(EditorViewModel editor, string present, string absent)
    {
        int position = editor.State.Content.IndexOf("this.", StringComparison.Ordinal) + 5;
        var completion = await editor.CompleteAsync(position);
        Assert.Contains(completion.Items, item => item.DisplayText == present);
        Assert.DoesNotContain(completion.Items, item => item.DisplayText == absent);
    }
    private static async Task ReadyAsync(ShellTestContext test, Task operation)
    {
        await UntilAsync(() => test.Shell.IsPreviewOpen || operation.IsCompleted);
        if (operation.IsCompleted) await operation;
        Assert.True(test.Shell.IsPreviewOpen, test.Shell.Status);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
