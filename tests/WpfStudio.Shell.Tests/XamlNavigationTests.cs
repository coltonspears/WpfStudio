using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlNavigationTests
{
    private const string Model = "namespace Demo; public class CustomerViewModel { public string Name { get; set; } = \"\"; }";
    private const string Markup = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:vm=\"clr-namespace:Demo\" d:DataContext=\"{d:DesignInstance Type=vm:CustomerViewModel}\"><TextBlock Text=\"{Binding Name}\" /></Window>";

    [Fact]
    public async Task BindingNavigationAndExplicitFixesUseLiveTypesAndRejectObsoleteActions()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string model = await test.CreateFileAsync("CustomerViewModel.cs", Model);
        string view = await test.CreateFileAsync("View.xaml", Markup);
        Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
        await test.Workspace.LoadAsync(new LoadWorkspaceRequest(project));
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        int member = Markup.IndexOf("Name}", StringComparison.Ordinal);
        editor.State.CaretOffset = member + 1;

        var hover = await editor.HoverAsync(member + 1);

        Assert.NotNull(hover);
        Assert.Contains("Name", hover.Text);
        Assert.Contains("CustomerViewModel", hover.Text);
        Assert.Equal("Name", Markup.Substring(hover.Start, hover.Length));
        await test.Shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Equal(model, test.Shell.ActiveDocument!.State.Path);
        Assert.Equal(Model.IndexOf("Name", StringComparison.Ordinal), test.Shell.ActiveDocument.State.CaretOffset);

        await test.Shell.OpenDocumentAsync(view);
        editor.State.Content = Markup.Replace("Name}", "Nmae}", StringComparison.Ordinal);
        editor.State.CaretOffset = member + 1;
        await editor.RefreshQuickFixesAsync(editor.State.CaretOffset);
        var fix = Assert.Single(editor.QuickFixes);
        Assert.Contains("Name", fix.Title);
        Assert.Equal(editor.State.Version, fix.Action.Edit.Version);
        Assert.NotNull(fix.Action.Edit.ExpectedTextHash);
        Assert.Contains("Nmae}", editor.State.Content); // Merely opening the menu never applies a change.

        await fix.ApplyCommand.ExecuteAsync(fix.Action);

        Assert.Equal(Markup, editor.State.Content);
        Assert.Empty(editor.QuickFixes);
        Assert.Empty(test.Dialogs.Errors);
        Assert.Empty(test.Dialogs.Confirmations);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Contains("Nmae}", editor.State.Content);

        editor.State.Content = Markup.Replace("Name}", "Nmae}", StringComparison.Ordinal);
        await editor.RefreshQuickFixesAsync(member + 1);
        var obsoleteBufferFix = Assert.Single(editor.QuickFixes);
        editor.State.Content += " ";
        Assert.Empty(editor.QuickFixes);
        string changedText = editor.State.Content;
        await obsoleteBufferFix.ApplyCommand.ExecuteAsync(obsoleteBufferFix.Action);
        Assert.Equal(changedText, editor.State.Content);
        Assert.Contains("changed", editor.LanguageStatus);

        await editor.RefreshQuickFixesAsync(member + 1);
        var obsoleteTypeFix = Assert.Single(editor.QuickFixes);
        var modelEditor = Assert.Single(test.Shell.Documents, document => document.State.Path == model);
        modelEditor.State.Content = Model.Replace(" Name ", " Nmae ", StringComparison.Ordinal);
        await modelEditor.SyncAsync();
        Assert.Empty(editor.QuickFixes);
        await obsoleteTypeFix.ApplyCommand.ExecuteAsync(obsoleteTypeFix.Action);
        Assert.Equal(changedText, editor.State.Content);
        await editor.RefreshQuickFixesAsync(member + 1);
        Assert.Empty(editor.QuickFixes); // Nmae is now an actual member in the unsaved C# buffer.

        // An in-flight hover must not survive a buffer edit.
        Task<XamlHoverInfo?> pendingHover = editor.HoverAsync(member + 1);
        editor.State.Content += " ";
        Assert.Null(await pendingHover);
        await test.Workspace.DisposeAsync();
        Assert.Null(await editor.HoverAsync(member + 1));
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task UnindexedOfflineXamlDefinitionDoesNotInvokeTheCSharpService()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", "<TextBlock Text=\"{Binding Name}\" />");
        await test.Shell.OpenDocumentAsync(view);

        await test.Shell.GoToDefinitionCommand.ExecuteAsync(null);

        Assert.Equal("This XAML reference could not be resolved statically", test.Shell.Status);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task OfflineResourceDefinitionUsesOnlyAnIndexMatchingTheCurrentBuffer()
    {
        await using var test = new ShellTestContext();
        const string markup = "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n<Grid.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"Red\" /></Grid.Resources>\n<Border Background=\"{StaticResource Accent}\" /></Grid>";
        string view = await test.CreateFileAsync("View.xaml", markup);
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new WorkspaceSnapshot(project, "Test", [new("fixture", "Fixture", project, "net10.0-windows", null, false, [new(view, "View.xaml", "Page")])], []);
        await test.Shell.RefreshWpfCommand.ExecuteAsync(null);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        int usage = markup.LastIndexOf("Accent", StringComparison.Ordinal);
        editor.State.CaretOffset = usage;

        await test.Shell.GoToDefinitionCommand.ExecuteAsync(null);

        Assert.Equal(markup.IndexOf('\n') + 1, editor.State.CaretOffset);
        editor.State.Content += " ";
        editor.State.CaretOffset = usage;
        await test.Shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Equal(usage, editor.State.CaretOffset);
        Assert.Equal("This XAML reference could not be resolved statically", test.Shell.Status);
        Assert.Empty(test.Dialogs.Errors);
    }
}
