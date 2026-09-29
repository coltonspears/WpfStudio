using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

/// <summary>Live preview continuity: last frame, render errors, selection memory, zoom and artboard size.</summary>
public sealed partial class DesignerTests
{
    private const string Text = "System.Windows.Controls.TextBlock", Grid = "System.Windows.Controls.Grid",
        Button = "System.Windows.Controls.Button", Border = "System.Windows.Controls.Border";

    /// <summary>Each render creates new runtime ids, as the preview host does.</summary>
    private static PreviewNode[] Nodes(long version, string path, params (string Type, string? Name, int Parent, bool Authored)[] shape) =>
        shape.Select((node, index) => new PreviewNode($"{version}-{index}", node.Parent < 0 ? null : $"{version}-{node.Parent}", null, node.Type, node.Name,
            new(index * 10, index * 5, 40, 20), node.Authored ? new SourceLocation(path, index * 10, 8, index + 1, 1) : null, true)).ToArray();

    private static PreviewSnapshot Frame(long version, byte marker, IReadOnlyList<PreviewNode> nodes, int width = 200, int height = 120) =>
        new(version, true, [marker], width, height, nodes, []);

    private static void Serve(FakePreview client, Func<long, IReadOnlyList<PreviewNode>> nodes, params PreviewProperty[] properties)
    {
        var rendered = new Dictionary<string, PreviewNode>();
        client.Render = request =>
        {
            var current = nodes(request.Version);
            foreach (var node in current) rendered[node.Id] = node;
            return Task.FromResult(Frame(request.Version, (byte)request.Version, current));
        };
        client.Inspect = request => Task.FromResult(new PreviewInspection(request.Version, rendered[request.NodeId], properties, []));
    }

    private static PreviewProperty Property(string name, string value, string source = "Local") =>
        new(name, "System.String", value, source, false, false, false, true, OwnerType: "System.Windows.Controls.TextBlock");

    private static IEnumerable<DesignerNode> Flatten(IEnumerable<DesignerNode> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    [Fact]
    public async Task SourceEditKeepsTheLastFrameVisibleUntilANewRenderReplacesIt()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        var frame = model.DisplayImage;
        Assert.NotNull(frame);
        Assert.False(model.IsStale);

        document.Content = "<TextBlock Text=\"Changed\" />";
        // Picking and inspection are invalid, but the canvas does not go blank.
        Assert.Null(model.Image);
        Assert.False(model.IsCurrent);
        Assert.Same(frame, model.DisplayImage);
        Assert.True(model.IsStale);
        Assert.True(model.ShowCanvasNotice);
        Assert.Null(model.CanvasNodes);

        client.Render = request => Task.FromResult(Snapshot(request.Version) with { PngBytes = [9] });
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(new byte[] { 9 }, model.DisplayImage);
        Assert.False(model.IsStale);
        Assert.NotNull(model.CanvasNodes);
    }

    [Fact]
    public async Task FailedRenderKeepsTheLastFrameAndNavigatesToTheError()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        var frame = model.DisplayImage;
        client.Render = request => Task.FromResult(new PreviewSnapshot(request.Version, false, null, 0, 0, [],
            [new("Unexpected end of file", "Error", 3, 7)], "Preview could not render"));
        document.Content = "<TextBlock";
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.False(model.IsCurrent);
        Assert.Same(frame, model.DisplayImage);
        Assert.True(model.HasRenderError);
        Assert.False(model.ShowCanvasNotice);
        Assert.Equal("Unexpected end of file", model.RenderError);
        SourceLocation? destination = null;
        model.SourceRequested += location => destination = location;
        Assert.True(model.GoToRenderErrorCommand.CanExecute(null));
        model.GoToRenderErrorCommand.Execute(null);
        Assert.Equal((document.Path, 3, 7), (destination!.Path, destination.Line, destination.Column));

        client.Render = request => Task.FromResult(Snapshot(request.Version) with { PngBytes = [4] });
        document.Content = "<TextBlock />";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.HasRenderError);
        Assert.Null(model.RenderError);
        Assert.Equal(new byte[] { 4 }, model.DisplayImage);
    }

    [Fact]
    public async Task OpeningAnotherDocumentStartsWithAnEmptyCanvas()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("first.xaml"));
        Assert.NotNull(model.DisplayImage);
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        client.Render = _ => pending.Task;
        var open = model.OpenAsync(Document("second.xaml"));
        Assert.Null(model.DisplayImage);
        Assert.False(model.HasCanvasContent);
        pending.SetResult(Snapshot(client.Requests[^1].Version));
        await open;
        Assert.NotNull(model.DisplayImage);
    }

    [Fact]
    public async Task RefreshRestoresTheSelectedElementAndPropertyByName()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        Serve(client, version => Nodes(version, document.Path, (Grid, "Root", -1, true), (Text, "Title", 0, true), (Text, "Greeting", 0, true)),
            Property("Text", "Hello"), Property("Width", "Auto", "Default"));
        await model.OpenAsync(document);
        model.SelectedNode = Flatten(model.Tree).Single(node => node.Node.Name == "Greeting");
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
        var oldId = model.SelectedNode.Node.Id;

        document.Content = "<Grid><TextBlock x:Name=\"Title\"/><TextBlock x:Name=\"Greeting\" Text=\"Hi\"/></Grid>";
        Assert.Null(model.SelectedNode);
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(model.SelectedNode);
        Assert.Equal("Greeting", model.SelectedNode!.Node.Name);
        Assert.NotEqual(oldId, model.SelectedNode.Node.Id);
        Assert.True(model.SelectedNode.IsSelected);
        Assert.Equal("Text", model.SelectedProperty?.Name);
        Assert.NotNull(model.SelectionBounds);
    }

    [Fact]
    public async Task RefreshRestoresAnUnnamedSelectionByItsAuthoredPosition()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        Serve(client, version => Nodes(version, document.Path, (Grid, null, -1, true), (Text, null, 0, true), (Border, null, 0, false), (Text, null, 0, true)));
        await model.OpenAsync(document);
        var second = Flatten(model.Tree).Where(node => node.Node.Type == Text).ElementAt(1);
        model.SelectedNode = second;

        document.Content = "<Grid><TextBlock/><TextBlock Text=\"Edited\"/></Grid>";
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(Flatten(model.Tree).Where(node => node.Node.Type == Text).ElementAt(1), model.SelectedNode);
    }

    [Fact]
    public async Task OnlyUserSelectionsAreMirroredInTheEditor()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        Serve(client, version => Nodes(version, document.Path, (Grid, "Root", -1, true), (Text, "Greeting", 0, true)));
        await model.OpenAsync(document);
        var revealed = new List<SourceLocation>();
        model.SelectionSourceChanged += revealed.Add;

        model.SelectedNode = Flatten(model.Tree).Single(node => node.Node.Name == "Greeting");
        Assert.Equal(10, Assert.Single(revealed).Start);

        // The editor caret selecting the same element does not echo back.
        model.SelectedNode = null;
        revealed.Clear();
        model.SelectSource(document.Path, 11);
        Assert.Equal("Greeting", model.SelectedNode?.Node.Name);
        Assert.Empty(revealed);

        // Restoring the selection after a render never moves the editor caret.
        document.Content = "<Grid />";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Greeting", model.SelectedNode?.Node.Name);
        Assert.Empty(revealed);
    }

    [Fact]
    public async Task CanvasPicksPreferAuthoredElementsUnlessExact()
    {
        var client = new FakePreview();
        var picks = new List<PreviewPickRequest>();
        client.Pick = request => { picks.Add(request); return Task.FromResult(Inspection(request.Version)); };
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        await model.PickElementCommand.ExecuteAsync(new PreviewPoint(4, 5));
        await model.PickElementCommand.ExecuteAsync(new PreviewPoint(4, 5, Exact: true));
        Assert.Equal(new[] { true, false }, picks.Select(pick => pick.PreferAuthored));
    }

    [Fact]
    public async Task OutlineFoldsTemplatePartsAndExpandsToASelectedPart()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        Serve(client, version => Nodes(version, document.Path, (Grid, null, -1, true), (Button, "Save", 0, true), (Border, null, 1, false), (Text, null, 2, false)));
        await model.OpenAsync(document);
        var nodes = Flatten(model.Tree).ToArray();
        Assert.True(nodes[0].IsExpanded);
        Assert.False(nodes[1].IsExpanded);
        Assert.Equal(new[] { "Grid", "Button", "Border", "TextBlock" }, nodes.Select(node => node.TypeName));
        Assert.Equal(new[] { "Grid", "Button", "Frame", "Text" }, nodes.Select(node => node.IconKind));

        model.SelectedNode = nodes[3];
        Assert.True(nodes[1].IsExpanded);
        Assert.True(nodes[2].IsExpanded);
    }

    [Fact]
    public async Task ZoomToFitFollowsTheViewportUntilTheUserZooms()
    {
        var client = new FakePreview { Render = request => Task.FromResult(Frame(request.Version, 1, [Node()], 2000, 1000)) };
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        model.CanvasViewportWidth = 1072;
        model.CanvasViewportHeight = 792;
        await model.OpenAsync(Document("view.xaml"));
        Assert.True(model.IsZoomToFit);
        Assert.Equal(0.5, model.Zoom, 3);
        Assert.Equal("2000 × 1000", model.ArtboardSizeText);

        model.CanvasViewportWidth = 2072;
        Assert.Equal(0.7, model.Zoom, 3);
        model.ZoomInCommand.Execute(null);
        Assert.False(model.IsZoomToFit);
        Assert.Equal(0.75, model.Zoom, 3);
        model.CanvasViewportWidth = 1072;
        Assert.Equal(0.75, model.Zoom, 3);
        model.ZoomToFitCommand.Execute(null);
        Assert.True(model.IsZoomToFit);
        Assert.Equal(0.5, model.Zoom, 3);
        model.ZoomToActualSizeCommand.Execute(null);
        Assert.Equal(1, model.Zoom);
        Assert.Equal(0.1, DesignerViewModel.StepZoom(0.1, -1));
        Assert.Equal(3, DesignerViewModel.StepZoom(3, 1));
    }

    [Fact]
    public async Task ArtboardPresetsChooseBetweenRootSizeAndFixedSize()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        await model.OpenAsync(Document("view.xaml"));
        Assert.True(client.Requests[^1].SizeToRoot);

        model.SelectedSizePreset = model.SizePresets.Single(preset => preset.Name == "1280 × 720");
        Assert.False(model.AutoSize);
        Assert.Equal((1280d, 720d), (model.PreviewWidth, model.PreviewHeight));
        Assert.False(model.IsCurrent);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal((false, 1280d, 720d), (client.Requests[^1].SizeToRoot, client.Requests[^1].Width, client.Requests[^1].Height));

        model.PreviewWidth = 1000;
        Assert.Same(DesignerViewModel.CustomSizePreset, model.SelectedSizePreset);
        model.AutoSize = true;
        Assert.Same(DesignerViewModel.AutoSizePreset, model.SelectedSizePreset);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(client.Requests[^1].SizeToRoot);
    }

    [Fact]
    public async Task PropertySearchMatchesNamesValuesAndSetValuesButKeepsTheSelection()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        Serve(client, version => Nodes(version, document.Path, (Text, "Greeting", -1, true)),
            Property("Text", "Hello"), Property("Width", "NaN", "Default"), Property("Margin", "4", "Style"), Property("FontSize", "12", "Inherited"));
        await model.OpenAsync(document);
        model.SelectedNode = model.Tree[0];
        Assert.Equal("4 properties", model.PropertyCountText);
        int refreshes = 0;
        model.PropertyFilterChanged += () => refreshes++;

        model.PropertyFilter = "wid";
        Assert.Equal(new[] { "Width" }, model.Properties.Where(model.MatchesPropertyFilter).Select(property => property.Name));
        Assert.Equal("1 of 4", model.PropertyCountText);
        model.PropertyFilter = "hello";
        Assert.Equal(new[] { "Text" }, model.Properties.Where(model.MatchesPropertyFilter).Select(property => property.Name));

        model.PropertyFilter = "";
        model.ShowSetPropertiesOnly = true;
        Assert.Equal(new[] { "Text", "Margin" }, model.Properties.Where(model.MatchesPropertyFilter).Select(property => property.Name));
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Width");
        Assert.True(model.MatchesPropertyFilter(model.SelectedProperty));
        Assert.True(refreshes >= 3);
        model.ClearPropertyFilterCommand.Execute(null);
        Assert.Equal("4 properties", model.PropertyCountText);
    }
}
