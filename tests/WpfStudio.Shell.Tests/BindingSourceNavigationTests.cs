using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

internal static class BindingNavigationTestData
{
    internal const string Text = """
        <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="{Binding Nmae}">
          <TextBlock.Tag>
            <MultiBinding>
              <Binding Path="First" />
              <Binding Path="Second" />
            </MultiBinding>
          </TextBlock.Tag>
        </TextBlock>
        """;
    internal static BindingSourcesSnapshot Single(string uri, string id = "text-expression") => new(id,
        [new(id, "text-declaration", null, null, "Binding", "Nmae", null, "PathError", Hint(uri, "Text=\""))]);
    internal static BindingSourcesSnapshot Composite(string uri) => new("tag-expression",
        [new("tag-expression", "multi-declaration", null, null, "MultiBinding", null, null, "Active", Hint(uri, "<MultiBinding>")),
         new("first-expression", "first-declaration", "tag-expression", 0, "Binding", "First", null, "Active", Hint(uri, "<Binding Path=\"First\"")),
         new("second-expression", "second-declaration", "tag-expression", 1, "Binding", "Second", null, "Active", Hint(uri, "<Binding Path=\"Second\""))]);
    internal static InspectionSourceHint Hint(string uri, string marker)
    {
        int offset = Text.IndexOf(marker, StringComparison.Ordinal);
        string preceding = Text[..offset];
        return new(uri, preceding.Count(character => character == '\n') + 1, offset - preceding.LastIndexOf('\n'));
    }
    internal static BindingSourceResponse Response(BindingSourceRequest request, BindingSourcesSnapshot single, BindingSourcesSnapshot composite) =>
        new(request, true, single.Declarations.Concat(composite.Declarations).Single(declaration => declaration.ExpressionId == request.ExpressionId));
}

public sealed partial class DesignerTests
{
    private static FakePreview BindingSourcePreview(string path)
    {
        string uri = new Uri(path).AbsoluteUri;
        var single = BindingNavigationTestData.Single(uri); var composite = BindingNavigationTestData.Composite(uri);
        return new FakePreview
        {
            Inspect = request => Task.FromResult(new PreviewInspection(request.Version, Node(),
                [new("Text", "System.String", "", "Local", true, false, false, true, BindingPath: "Nmae", BindingStatus: "PathError",
                    OwnerType: "System.Windows.Controls.TextBlock", OwnerAssembly: "PresentationFramework", EditableValue: "", BindingSources: single),
                 new("Tag", "System.Object", "", "Local", true, false, false, false, BindingPath: "(MultiBinding)", BindingStatus: "Active",
                    OwnerType: "System.Windows.FrameworkElement", OwnerAssembly: "PresentationFramework", BindingSources: composite)], [])),
            BindingSource = request => Task.FromResult(BindingNavigationTestData.Response(request, single, composite))
        };
    }

    [Fact]
    public async Task PreviewBindingNavigationSelectsAnExactCompositeChildAndPreservesDraft()
    {
        await using var files = new ShellTestContext();
        string path = await files.CreateFileAsync("Binding.xaml", BindingNavigationTestData.Text);
        var client = BindingSourcePreview(path);
        await using var context = new ShellTestContext(client);
        await context.Shell.OpenDocumentAsync(path);
        var editor = context.Shell.ActiveDocument!;
        var model = context.Shell.Designer;
        await model.OpenAsync(editor.State);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Keep this draft";
        var selectedProperty = model.SelectedProperty;
        model.SelectedBindingDeclaration = model.BindingDeclarations.Single(item => item.Declaration.ExpressionId == "second-expression");
        await model.ShowBindingSourceCommand.ExecuteAsync(model.SelectedBindingDeclaration);
        Assert.Equal("<Binding Path=\"Second\" />", editor.SelectedText);
        Assert.StartsWith("Opened verified preview binding declaration:", model.BindingSourceStatus);
        Assert.Equal(2, client.BindingSourceRequests.Count);
        Assert.All(client.BindingSourceRequests, request =>
        {
            Assert.Equal("tag-expression", request.BindingId);
            Assert.Equal("second-expression", request.ExpressionId);
            Assert.Equal("second-declaration", request.DeclarationId);
            Assert.Equal("Tag", request.Property);
        });
        Assert.Same(selectedProperty, model.SelectedProperty);
        Assert.Equal("Keep this draft", model.EditedValue);
        Assert.False(editor.State.IsDirty);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("selection-round-trip")]
    [InlineData("property-round-trip")]
    [InlineData("scenario")]
    public async Task PreviewBindingFinalRevalidationRejectsChangedContext(string change)
    {
        await using var files = new ShellTestContext();
        string path = await files.CreateFileAsync("Binding.xaml", BindingNavigationTestData.Text);
        var client = BindingSourcePreview(path);
        var reply = new TaskCompletionSource<BindingSourceResponse>();
        var respond = client.BindingSource;
        client.BindingSource = request => client.BindingSourceRequests.Count == 2 ? reply.Task : respond(request);
        await using var context = new ShellTestContext(client);
        await context.Shell.OpenDocumentAsync(path);
        var editor = context.Shell.ActiveDocument!; var model = context.Shell.Designer; model.AutoRefresh = false;
        await model.OpenAsync(editor.State);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        var selected = model.SelectedBindingDeclaration!;
        var pending = model.ShowBindingSourceCommand.ExecuteAsync(selected);
        Assert.Equal(2, client.BindingSourceRequests.Count);
        switch (change)
        {
            case "source": editor.State.Content += "<!-- changed -->"; break;
            case "selection-round-trip": model.SelectedBindingDeclaration = model.BindingDeclarations.Last(); model.SelectedBindingDeclaration = selected; break;
            case "property-round-trip": model.SelectedProperty = model.Properties[1]; model.SelectedProperty = model.Properties[0]; break;
            case "scenario": model.UseDesignTimeValues = false; break;
        }
        reply.SetResult(await respond(client.BindingSourceRequests.Last()));
        await pending;
        Assert.Equal(0, editor.SelectionLength);
        Assert.DoesNotContain("Opened verified", model.BindingSourceStatus);
    }

    [Fact]
    public async Task PreviewBindingUnavailableOriginsAndTruncationRemainExplicit()
    {
        var client = BindingSourcePreview(Path.Combine(Path.GetTempPath(), "Binding.xaml"));
        var inspect = client.Inspect;
        client.Inspect = async request =>
        {
            var result = await inspect(request);
            var property = result.Properties[0]; var sources = property.BindingSources!;
            return result with { Properties = [property with { BindingSources = sources with { Truncated = true,
                Declarations = [sources.Declarations[0] with { Source = null, UnavailableReason = "Template clone origin is unavailable." }] } }] };
        };
        client.BindingSource = request => Task.FromResult(new BindingSourceResponse(request, false, Status: "Template clone origin is unavailable."));
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        int navigations = 0;
        model.BindingSourceRequested += (_, _) => { navigations++; return Task.FromResult("Unexpected navigation"); };
        await model.OpenAsync(Document("Binding.xaml")); model.SelectedNode = model.Tree[0];
        Assert.Contains("omitted", model.BindingDeclarationCoverage);
        await model.ShowBindingSourceCommand.ExecuteAsync(model.SelectedBindingDeclaration);
        Assert.Contains("Template clone", model.BindingSourceStatus);
        Assert.Equal(0, navigations);
        model.Mode = PreviewMode.Compiled;
        await model.RefreshCommand.ExecuteAsync(null); model.SelectedNode = model.Tree[0];
        Assert.False(model.ShowBindingSourceCommand.CanExecute(model.SelectedBindingDeclaration));
        Assert.Contains("Compiled preview", model.BindingSourceStatus);
    }
}

public sealed partial class InspectionViewModelTests
{
    private static FakeSession BindingSourceSession(string bindingId = "text-expression", bool truncated = false)
    {
        const string uri = "pack://application:,,,/Demo;component/View.xaml";
        var single = BindingNavigationTestData.Single(uri, bindingId) with { Truncated = truncated };
        var composite = BindingNavigationTestData.Composite(uri);
        var session = SourceSession();
        session.Hello = session.Hello with { Capabilities = [.. session.Hello.Capabilities, "binding-source"] };
        session.Inspect = request => Task.FromResult(new InspectionElement(request.Revision, request.NodeId,
            [new("Text", "System.Windows.Controls.TextBlock", "PresentationFramework", "System.String", "", "Local", true, false, false,
                new("Nmae", "PathError", "MissingProperty", "Missing Nmae", Sources: single), "text-id", "token", true, ""),
             new("Tag", "System.Windows.FrameworkElement", "PresentationFramework", "System.Object", "", "Local", true, false, false,
                new("(MultiBinding)", "Active", "Active", "Active", Sources: composite), "tag-id")], null, true));
        session.BindingSource = request => Task.FromResult(BindingNavigationTestData.Response(request, single, composite));
        return session;
    }

    [Fact]
    public async Task LiveBindingNavigationSurvivesUnchangedPollAndRevalidatesLatestRevision()
    {
        var session = BindingSourceSession(truncated: true);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.BindingSourceRequested += async (request, token) =>
        {
            Assert.Equal("second-expression", request.Declaration.ExpressionId);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(request.IsCurrent());
            var response = await request.RevalidateAsync(token);
            Assert.True(response.Available);
            navigations++;
            return "Opened verified binding declaration";
        };
        await model.AttachAsync(session); model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Keep draft";
        Assert.Contains("omitted", model.BindingDeclarationCoverage);
        model.SelectedBindingDeclaration = model.BindingDeclarations.Last();
        await model.ShowBindingSourceCommand.ExecuteAsync(model.SelectedBindingDeclaration);
        Assert.Equal(1, navigations);
        Assert.Equal(2, session.BindingSourceRequests.Count);
        Assert.True(session.BindingSourceRequests[1].Revision > session.BindingSourceRequests[0].Revision);
        Assert.Equal("tag-id", session.BindingSourceRequests[1].PropertyId);
        Assert.Equal("Keep draft", model.EditedValue);
        Assert.Equal("Text", model.SelectedProperty!.Name);
    }

    [Theory]
    [InlineData("selection-round-trip")]
    [InlineData("property-round-trip")]
    [InlineData("pause-resume")]
    [InlineData("disconnect")]
    [InlineData("node")]
    public async Task LiveBindingVerificationCannotNavigateAfterContextChanges(string change)
    {
        var session = BindingSourceSession();
        var reply = new TaskCompletionSource<BindingSourceResponse>();
        var respond = session.BindingSource; session.BindingSource = _ => reply.Task;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.BindingSourceRequested += (_, _) => { navigations++; return Task.FromResult("Unexpected navigation"); };
        await model.AttachAsync(session, debugging: true); model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        var selected = model.SelectedBindingDeclaration;
        var pending = model.ShowBindingSourceCommand.ExecuteAsync(selected);
        switch (change)
        {
            case "selection-round-trip": model.SelectedBindingDeclaration = model.BindingDeclarations.Last(); model.SelectedBindingDeclaration = selected; break;
            case "property-round-trip": model.SelectedProperty = model.Properties[1]; model.SelectedProperty = model.Properties[0]; break;
            case "pause-resume": model.SetDebuggerState(true); model.SetDebuggerState(false); break;
            case "disconnect": await model.DisconnectCommand.ExecuteAsync(null); break;
            case "node": model.SelectedNode = model.Tree[1]; break;
        }
        reply.SetResult(await respond(session.BindingSourceRequests.Single()));
        await pending;
        Assert.Equal(0, navigations);
        Assert.DoesNotContain("Unexpected", model.BindingSourceStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueNavigationRequiresTheSameExpressionIdentityEvenWhenThePathMatches(bool replaced)
    {
        var session = BindingSourceSession(replaced ? "replacement-expression" : "text-expression");
        session.Snapshot = () => new(1, session.Nodes, [], BindingObservations:
            [new("text-expression", "first", "Text", "System.Windows.Controls.TextBlock", "PresentationFramework",
                new("Nmae", "PathError", "MissingProperty", "Missing Nmae"))], ScannedBindingNodes: ["first"]);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.BindingSourceRequested += async (request, token) =>
        { Assert.True((await request.RevalidateAsync(token)).Available); navigations++; return "Opened current binding declaration"; };
        await model.AttachAsync(session);
        var issue = Assert.Single(Assert.Single(model.IssueGroups).Instances);
        await model.ShowBindingIssueSourceCommand.ExecuteAsync(issue);
        Assert.Equal(replaced ? 0 : 1, navigations);
        if (replaced)
        {
            Assert.Empty(session.BindingSourceRequests);
            Assert.Contains("no longer present", model.BindingSourceStatus);
        }
    }

    [Fact]
    public async Task BindingSourceAcknowledgementMustMatchTheExactDeclaration()
    {
        var session = BindingSourceSession();
        var respond = session.BindingSource;
        session.BindingSource = async request => (await respond(request)) with { Request = request with { DeclarationId = "other" } };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.BindingSourceRequested += (_, _) => { navigations++; return Task.FromResult("Unexpected"); };
        await model.AttachAsync(session); model.SelectedNode = model.Tree[0];
        await model.ShowBindingSourceCommand.ExecuteAsync(model.SelectedBindingDeclaration);
        Assert.Equal(0, navigations);
        Assert.Contains("did not match", model.BindingSourceStatus);
    }

    [Fact]
    public async Task SlowIssueInspectionCannotOverrideANewerBindingSelection()
    {
        var session = BindingSourceSession();
        session.Snapshot = () => new(1, session.Nodes, [], BindingObservations:
            [new("text-expression", "first", "Text", "System.Windows.Controls.TextBlock", "PresentationFramework",
                new("Nmae", "PathError", "MissingProperty", "Missing Nmae"))], ScannedBindingNodes: ["first"]);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.BindingSourceRequested += (_, _) => { navigations++; return Task.FromResult("Unexpected"); };
        await model.AttachAsync(session); model.SelectedNode = model.Tree[0];
        var read = new TaskCompletionSource<InspectionElement>();
        var inspect = session.Inspect;
        session.Inspect = _ => read.Task;
        var pending = model.ShowBindingIssueSourceCommand.ExecuteAsync(Assert.Single(Assert.Single(model.IssueGroups).Instances));
        model.SelectedBindingDeclaration = model.BindingDeclarations.Last();
        read.SetResult(await inspect(new(1, "first")));
        await pending;
        Assert.Equal("second-expression", model.SelectedBindingDeclaration!.Declaration.ExpressionId);
        Assert.Empty(session.BindingSourceRequests);
        Assert.Equal(0, navigations);
    }
}
