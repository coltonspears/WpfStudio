using System.Text;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    private const string LayoutSource = "<Canvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='300' Height='200'><Button Name='Input' Canvas.Left='20' Canvas.Top='30' Width='100' Height='40' Content='Keep'/></Canvas>";

    private static FakePreview LayoutPreview()
    {
        PreviewNode? selected = null;
        PreviewLayoutEditContext? layout = null;
        var properties = new[] { "Width", "Height", "Margin", "HorizontalAlignment", "VerticalAlignment", "Left", "Top", "Right", "Bottom" }
            .Select(name => new PreviewProperty(name, "System.Double", "", "Local", false, false, false, true,
                OwnerType: name is "Left" or "Top" or "Right" or "Bottom" ? "System.Windows.Controls.Canvas" : "System.Windows.FrameworkElement",
                OwnerAssembly: "PresentationFramework", IsAttached: name is "Left" or "Top" or "Right" or "Bottom",
                CanWriteSource: true, ContentProperty: "Content")).ToArray();
        return new FakePreview
        {
            Render = request =>
            {
                int start = request.Text.IndexOf("<Button", StringComparison.Ordinal);
                var element = new SourceLocation(request.Path, start, request.Text.IndexOf("/>", start, StringComparison.Ordinal) + 2 - start, 1, start + 1, "Button");
                var parent = new SourceLocation(request.Path, 0, request.Text.IndexOf('>') + 1, 1, 1, "Canvas");
                selected = new("child", "parent", "parent", "System.Windows.Controls.Button", "Input", new(20, 30, 100, 40), element, true);
                layout = new(true, request.Version, "child", "layout-token", element, parent, "Canvas", selected.Bounds,
                    new(0, 0, 300, 200), new(20, 30, 100, 40), new(0, 0, 0, 0), Width: 100, Height: 40,
                    HorizontalAlignment: "Stretch", VerticalAlignment: "Stretch", CanvasLeft: 20, CanvasTop: 30,
                    EditProperties: properties, Siblings: [], SourceHash: DocumentStore.Hash(Encoding.UTF8.GetBytes(request.Text)));
                return Task.FromResult(new PreviewSnapshot(request.Version, true, [1], 300, 200,
                    [new("parent", null, null, "System.Windows.Controls.Canvas", null, new(0, 0, 300, 200), parent, true), selected], []));
            },
            Inspect = request => Task.FromResult(new PreviewInspection(request.Version, selected, properties, [], LayoutEditing: layout))
        };
    }

    private static async Task OpenLayoutAsync(ShellTestContext test)
    {
        string path = await test.CreateFileAsync("Layout.xaml", LayoutSource);
        await test.Shell.OpenDocumentAsync(path);
        var designer = test.Shell.Designer;
        designer.AutoRefresh = false;
        await designer.OpenAsync(test.Shell.ActiveDocument!.State);
        designer.LayoutGesture.IsEnabled = true;
        designer.SelectedNode = designer.Tree[0].Children[0];
        Assert.True(designer.LayoutGesture.Context?.Available);
    }

    private static async Task BeginLayoutAsync(DesignerViewModel model, XamlLayoutHandle handle, double x, double y)
    {
        await model.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Begin, handle));
        await model.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Update, handle, x, y, true));
        Assert.True(model.LayoutGesture.Draft?.Success, model.LayoutGesture.Status);
    }

    [Fact]
    public async Task ResizeReviewsOneAtomicSourceEditAndUndoRestoresAllProperties()
    {
        var client = LayoutPreview();
        await using var test = new ShellTestContext(client);
        await OpenLayoutAsync(test);
        var designer = test.Shell.Designer;
        var document = test.Shell.ActiveDocument!.State;
        await BeginLayoutAsync(designer, XamlLayoutHandle.TopLeft, -10, -5);
        Assert.Equal(LayoutSource, document.Content);
        Assert.Empty(client.Edits);
        var pending = designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.TopLeft, -10, -5, true));
        Assert.True(test.Shell.IsPreviewOpen, designer.Status);
        var change = Assert.Single(test.Shell.ChangeReview.Changes);
        Assert.Contains("Canvas.Left='10'", change.After);
        Assert.Contains("Canvas.Top='25'", change.After);
        Assert.Contains("Width='110'", change.After);
        Assert.Contains("Height='45'", change.After);
        Assert.Contains("Content='Keep'", change.After);
        Assert.Single(client.LayoutValidations);
        test.Shell.ChangeReview.AcceptCommand.Execute(null);
        await pending;
        Assert.Equal(change.After, document.Content);
        Assert.Equal(2, client.LayoutValidations.Count);
        Assert.Equal(LayoutSource, await File.ReadAllTextAsync(document.Path));
        Assert.Empty(client.Edits);
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(LayoutSource, document.Content);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task GestureEscapeAndCancelledReviewLeaveSourceRuntimeAndInspectorDraftUnchanged()
    {
        var client = LayoutPreview();
        await using var test = new ShellTestContext(client);
        await OpenLayoutAsync(test);
        var designer = test.Shell.Designer;
        var document = test.Shell.ActiveDocument!.State;
        designer.SelectedProperty = designer.Properties[0];
        designer.EditedValue = "Unapplied inspector draft";
        var property = designer.SelectedProperty;
        await BeginLayoutAsync(designer, XamlLayoutHandle.Move, 20, 15);
        await designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Cancel, XamlLayoutHandle.Move));
        Assert.Null(designer.LayoutGesture.Draft);
        Assert.Equal(LayoutSource, document.Content);
        Assert.Empty(client.LayoutValidations);
        await BeginLayoutAsync(designer, XamlLayoutHandle.Move, 20, 15);
        var pending = designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.Move, 20, 15, true));
        Assert.True(test.Shell.IsPreviewOpen, designer.Status);
        test.Shell.ChangeReview.CancelCommand.Execute(null);
        await pending;
        Assert.Equal(LayoutSource, document.Content);
        Assert.Same(property, designer.SelectedProperty);
        Assert.Equal("Unapplied inspector draft", designer.EditedValue);
        Assert.Empty(client.Edits);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("selection")]
    [InlineData("mode")]
    [InlineData("disabled")]
    public async Task SourceOrPreviewChangesDuringReviewRejectTheEntireGesture(string change)
    {
        await using var test = new ShellTestContext(LayoutPreview());
        await OpenLayoutAsync(test);
        var designer = test.Shell.Designer;
        var document = test.Shell.ActiveDocument!.State;
        await BeginLayoutAsync(designer, XamlLayoutHandle.BottomRight, 20, 10);
        var pending = designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.BottomRight, 20, 10, true));
        Assert.True(test.Shell.IsPreviewOpen, designer.Status);
        if (change == "source") document.Content += "<!-- newer source -->";
        else if (change == "selection") { var node = designer.SelectedNode; designer.SelectedNode = null; designer.SelectedNode = node; }
        else if (change == "mode") designer.Mode = PreviewMode.Compiled;
        else designer.LayoutGesture.IsEnabled = false;
        string expected = document.Content;
        test.Shell.ChangeReview.AcceptCommand.Execute(null);
        await pending;
        Assert.Equal(expected, document.Content);
        Assert.Contains("changed", designer.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenarioFileIsNotReadDuringMovesButStillGuardsTheCommit()
    {
        await using var test = new ShellTestContext(LayoutPreview());
        string path = await test.CreateFileAsync("Layout.xaml", LayoutSource);
        await test.Shell.OpenDocumentAsync(path);
        var designer = test.Shell.Designer;
        designer.AutoRefresh = false;
        string configuration = Path.Combine(test.Root, PreviewScenarioCatalog.FileName);
        await File.WriteAllTextAsync(configuration, "{\"version\":1,\"views\":[]}");
        var timestamp = File.GetLastWriteTimeUtc(configuration);
        await designer.OpenAsync(test.Shell.ActiveDocument!.State, projectDirectory: test.Root);
        designer.LayoutGesture.IsEnabled = true;
        designer.SelectedNode = designer.Tree[0].Children[0];
        Assert.True(designer.LayoutGesture.Context?.Available, designer.LayoutGesture.Status);
        await designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Begin, XamlLayoutHandle.Move));

        // The guard treats a file it cannot open as changed, so a move that read it would end the gesture.
        using (new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.None))
            await designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Update, XamlLayoutHandle.Move, 20, 15, true));
        Assert.True(designer.LayoutGesture.Draft?.Success, designer.LayoutGesture.Status);

        // A change that keeps the timestamp and is not seen by the watcher still fails the commit.
        await File.WriteAllTextAsync(configuration, "{\"version\":1,\"views\":[ ]}");
        File.SetLastWriteTimeUtc(configuration, timestamp);
        await designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.Move, 20, 15, true));
        Assert.False(test.Shell.IsPreviewOpen);
        Assert.Null(designer.LayoutGesture.Draft);
        Assert.Equal(LayoutSource, test.Shell.ActiveDocument!.State.Content);
    }

    [Fact]
    public async Task RuntimeLayoutChangeWithUnchangedSourceIsRecheckedAfterReview()
    {
        var client = LayoutPreview();
        int validations = 0;
        client.ValidateLayout = request => Task.FromResult(new PreviewLayoutValidationResult(request, ++validations == 1,
            validations == 1 ? null : "Observed layout changed during review."));
        await using var test = new ShellTestContext(client);
        await OpenLayoutAsync(test);
        var designer = test.Shell.Designer;
        await BeginLayoutAsync(designer, XamlLayoutHandle.Move, 15, 0);
        var pending = designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.Move, 15, 0, true));
        Assert.True(test.Shell.IsPreviewOpen, designer.Status);
        test.Shell.ChangeReview.AcceptCommand.Execute(null);
        await pending;
        Assert.Equal(2, validations);
        Assert.Equal(LayoutSource, test.Shell.ActiveDocument!.State.Content);
        Assert.Equal("Observed layout changed during review.", designer.Status);
        Assert.Empty(client.Edits);
    }

    [Fact]
    public async Task MismatchedValidationReplyCannotOpenAReview()
    {
        var client = LayoutPreview();
        client.ValidateLayout = request => Task.FromResult(new PreviewLayoutValidationResult(request with { NodeId = "another" }, true));
        await using var test = new ShellTestContext(client);
        await OpenLayoutAsync(test);
        var designer = test.Shell.Designer;
        await BeginLayoutAsync(designer, XamlLayoutHandle.Move, 15, 0);
        await designer.LayoutGesture.GestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.Move, 15, 0, true));
        Assert.False(test.Shell.IsPreviewOpen);
        Assert.Equal(LayoutSource, test.Shell.ActiveDocument!.State.Content);
        Assert.Contains("changed", designer.Status, StringComparison.OrdinalIgnoreCase);
    }
}
