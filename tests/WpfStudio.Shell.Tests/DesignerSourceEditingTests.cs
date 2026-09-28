using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    private const string BoundSource = "<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Text=\"{Binding Name}\" />";

    private static FakePreview WritablePreview()
    {
        PreviewNode? node = null;
        return new FakePreview
        {
            Render = request =>
            {
                node = Node(source: new(request.Path, 0, request.Text.IndexOf('>') + 1, 1, 1, "TextBlock"));
                return Task.FromResult(new PreviewSnapshot(request.Version, true, [1], 100, 30, [node], []));
            },
            Inspect = request => Task.FromResult(new PreviewInspection(request.Version, node,
                [new("Text", "System.String", "Original", "Local", true, false, false, true, "Name", "Active",
                    OwnerType: "System.Windows.Controls.TextBlock", OwnerAssembly: "PresentationFramework", EditableValue: "Original", CanWriteSource: true,
                    ContentProperty: "Inlines")], []))
        };
    }

    private static void SelectWritableProperty(DesignerViewModel model)
    {
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = Assert.Single(model.Properties);
        Assert.True(model.WritePropertyToSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task SourceWriteShowsBindingReplacementDiffThenAppliesInBufferWithUndo()
    {
        var client = WritablePreview();
        await using var context = new ShellTestContext(client);
        var path = await context.CreateFileAsync("View.xaml", BoundSource);
        await context.Shell.OpenDocumentAsync(path);
        var document = context.Shell.ActiveDocument!.State;
        var model = context.Shell.Designer;
        model.AutoRefresh = false;
        await model.OpenAsync(document);
        SelectWritableProperty(model);
        model.EditedValue = "A & B";

        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.True(context.Shell.IsPreviewOpen, model.Status);
        Assert.Equal(BoundSource, document.Content);
        Assert.Empty(client.Edits); // Validation and the diff never mutate the runtime value.
        Assert.Contains("binding", context.Shell.PreviewWarnings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Text=\"A &amp; B\"", Assert.Single(context.Shell.PreviewChanges).After);
        context.Shell.AcceptPreviewCommand.Execute(null);
        await pending;

        Assert.Contains("Text=\"A &amp; B\"", document.Content);
        Assert.Equal(BoundSource, await File.ReadAllTextAsync(path));
        Assert.False(model.IsCurrent);
        await context.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(BoundSource, document.Content);
        Assert.Empty(context.Dialogs.Errors);
    }

    [Fact]
    public async Task CancelledAndStaleDiffNeverOverwriteSource()
    {
        await using var context = new ShellTestContext(WritablePreview());
        var path = await context.CreateFileAsync("View.xaml", BoundSource);
        await context.Shell.OpenDocumentAsync(path);
        var document = context.Shell.ActiveDocument!.State;
        var model = context.Shell.Designer;
        model.AutoRefresh = false;
        await model.OpenAsync(document);
        SelectWritableProperty(model);
        model.EditedValue = "Replacement";
        var cancelled = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.True(context.Shell.IsPreviewOpen, model.Status);
        context.Shell.CancelPreviewCommand.Execute(null);
        await cancelled;
        Assert.Equal(BoundSource, document.Content);

        var stale = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.True(context.Shell.IsPreviewOpen, model.Status);
        document.Content += "\n<!-- newer edit -->";
        context.Shell.AcceptPreviewCommand.Execute(null);
        await stale;
        Assert.Equal(BoundSource + "\n<!-- newer edit -->", document.Content);
        Assert.Contains("changed", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExplicitRemovalUsesDiffAndUndo()
    {
        await using var context = new ShellTestContext(WritablePreview());
        var path = await context.CreateFileAsync("View.xaml", BoundSource);
        await context.Shell.OpenDocumentAsync(path);
        var document = context.Shell.ActiveDocument!.State;
        var model = context.Shell.Designer;
        model.AutoRefresh = false;
        await model.OpenAsync(document);
        SelectWritableProperty(model);
        var pending = model.RemovePropertyFromSourceCommand.ExecuteAsync(null);
        Assert.True(context.Shell.IsPreviewOpen, model.Status);
        Assert.DoesNotContain("{Binding Name}", Assert.Single(context.Shell.PreviewChanges).After);
        context.Shell.AcceptPreviewCommand.Execute(null);
        await pending;
        Assert.DoesNotContain("Text=", document.Content);
        await context.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(BoundSource, document.Content);
    }

    [Fact]
    public async Task InvalidLiteralOrChangedSelectionCannotProduceSourceDiff()
    {
        var client = WritablePreview();
        var validation = new TaskCompletionSource<PreviewPropertyValidation>();
        client.Validate = _ => validation.Task;
        await using var context = new ShellTestContext(client);
        var path = await context.CreateFileAsync("View.xaml", BoundSource);
        await context.Shell.OpenDocumentAsync(path);
        var model = context.Shell.Designer;
        await model.OpenAsync(context.Shell.ActiveDocument!.State);
        SelectWritableProperty(model);
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        model.EditedValue = "Newer input";
        validation.SetResult(new(true));
        await pending;
        Assert.False(context.Shell.IsPreviewOpen);

        client.Validate = _ => Task.FromResult(new PreviewPropertyValidation(false, "Invalid test literal"));
        await model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.Equal("Invalid test literal", model.Status);
        Assert.False(context.Shell.IsPreviewOpen);
        Assert.Equal(BoundSource, context.Shell.ActiveDocument.State.Content);
    }

    [Fact]
    public async Task SelectingAnotherNodeClearsOldInspectorBeforeNewInspectionArrives()
    {
        var client = WritablePreview();
        await using var context = new ShellTestContext(client);
        var path = await context.CreateFileAsync("View.xaml", BoundSource);
        await context.Shell.OpenDocumentAsync(path);
        var model = context.Shell.Designer;
        await model.OpenAsync(context.Shell.ActiveDocument!.State);
        SelectWritableProperty(model);
        var pending = new TaskCompletionSource<PreviewInspection>();
        client.Inspect = _ => pending.Task;
        model.SelectedNode = new DesignerNode(Node() with { Id = "another-node" });
        Assert.Empty(model.Properties);
        Assert.Null(model.SelectedProperty);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Empty(client.Edits);
        model.SelectedNode = null;
        pending.SetResult(new(client.Requests[0].Version, Node() with { Id = "another-node" }, [new("Width", "System.Double", "123", "Local", false, false, false, true)], []));
        await Task.Yield();
        Assert.Empty(model.Properties);
    }

    [Fact]
    public async Task CompiledModeCarriesExplicitBuildInputsAndNeverTreatsEditsAsLiveCompiledChanges()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var document = new DocumentState(Path.Combine(Path.GetTempPath(), "Compiled.xaml"),
            "<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:lang='http://schemas.microsoft.com/winfx/2006/xaml' lang:Class='Demo.View' />");
        await model.OpenAsync(document, "C:/project/bin/View.dll", "C:/project");
        model.Mode = PreviewMode.Compiled;
        Assert.False(model.IsCurrent);
        Assert.Equal("Demo.View", model.ViewTypeName);
        await model.RefreshCommand.ExecuteAsync(null);
        var request = client.Requests.Last();
        Assert.Equal(PreviewMode.Compiled, request.Mode);
        Assert.Equal("Demo.View", request.ViewTypeName);
        Assert.Equal("App.xaml", request.ApplicationResourcePath);
        Assert.False(model.SupportsLivePreview);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        document.Content += " ";
        Assert.Equal(2, client.Requests.Count);
        Assert.False(model.IsCurrent);
        Assert.Contains("Rebuild", model.Status);
    }
}
