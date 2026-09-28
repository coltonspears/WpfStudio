using WpfStudio.App.Features.Appearance;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

internal static class AppearanceTestData
{
    internal static AppearanceSnapshot Snapshot(string value) => new(true,
        [new("Effective value", value), new("Base value source", "Style"), new("Expression", "False")],
        [new("setter", "Setter", "Foreground candidate; activation is not established.", "Foreground")],
        [new("element", null, "Element resources", null, ["\"Accent\""])],
        [new(1, DateTimeOffset.UtcNow, "TextBlock", "Foreground", "\"Accent\"", "Colors.xaml")],
        ["Exact active setter attribution is unavailable."], Truncated: true);

    internal static AppearanceResponse Response(AppearanceRequest request, string value) => new(request, Snapshot(value));
}

public sealed class AppearanceInspectorTests
{
    [Fact]
    public void ObservationsCandidatesAndHistoryRemainSeparateAndClearTogether()
    {
        var model = new AppearanceInspectorViewModel();
        model.Begin("Local (design baseline)", true);
        model.Apply(AppearanceTestData.Snapshot("Blue"));
        Assert.Equal(["Effective value", "Base value source"], model.KeyFacts.Select(fact => fact.Name));
        Assert.Single(model.OtherFacts);
        Assert.Single(model.Declarations);
        Assert.Single(model.ResourceScopes);
        Assert.Single(model.ResourceEvents);
        Assert.Single(model.Notices);
        Assert.Contains("temporary override", model.SelectionContext);
        Assert.Contains("design baseline", model.SelectionContext);
        Assert.True(model.Truncated);
        model.Clear("Selection changed");
        Assert.Null(model.Snapshot);
        Assert.Empty(model.Facts);
        Assert.Empty(model.Declarations);
        Assert.Empty(model.ResourceScopes);
        Assert.Empty(model.ResourceEvents);
        Assert.Empty(model.Notices);
        Assert.Empty(model.SelectionContext);
        Assert.False(model.Available);
        Assert.False(model.Truncated);
    }
}

public sealed partial class DesignerTests
{
    private static PreviewInspection AppearanceInspection(long version) => Inspection(version) with
    {
        Properties = [new("Text", "System.String", "Hello", "Local (design baseline)", false, false, false, true,
            OwnerType: "System.Windows.Controls.TextBlock", OwnerAssembly: "PresentationFramework", EditableValue: "Hello"),
            new("Tag", "System.Object", "Tag value", "Local", false, false, false, true,
                OwnerType: "System.Windows.FrameworkElement", OwnerAssembly: "PresentationFramework", EditableValue: "Tag value")]
    };
    private static FakePreview AppearancePreview() => new()
    {
        Inspect = request => Task.FromResult(AppearanceInspection(request.Version)),
        Appearance = request => Task.FromResult(AppearanceTestData.Response(request, request.Property))
    };

    [Fact]
    public async Task PreviewAppearanceIsOnDemandAndPreservesThePropertyDraft()
    {
        var client = AppearancePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        await model.OpenAsync(Document("appearance.xaml"));
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Unsaved draft";
        Assert.Empty(client.AppearanceRequests);
        model.AppearanceTabIsActive = true;
        var request = Assert.Single(client.AppearanceRequests);
        Assert.Equal(client.Requests.Last().Version, request.Revision);
        Assert.Equal("1", request.NodeId);
        Assert.Equal("Text", request.Property);
        Assert.Equal("System.Windows.Controls.TextBlock", request.OwnerType);
        Assert.Equal("PresentationFramework", request.OwnerAssembly);
        Assert.Null(request.PropertyId);
        Assert.True(model.AppearanceDetails.Available);
        Assert.Equal("Unsaved draft", model.EditedValue);
        Assert.Contains("design baseline", model.AppearanceDetails.SelectionContext);
        model.AppearanceTabIsActive = false;
        Assert.Null(model.AppearanceDetails.Snapshot);
        model.SelectedProperty = model.Properties[1];
        Assert.Single(client.AppearanceRequests);
        await model.RefreshAppearanceCommand.ExecuteAsync(null);
        Assert.Equal("Tag", client.AppearanceRequests.Last().Property);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("node")]
    [InlineData("source")]
    [InlineData("scenario")]
    [InlineData("disconnect")]
    public async Task PreviewAppearanceRejectsLateResultsAfterContextChanges(string change)
    {
        var pending = new TaskCompletionSource<AppearanceResponse>();
        var client = AppearancePreview();
        client.Appearance = _ => pending.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("appearance-stale.xaml");
        await model.OpenAsync(document);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        var reading = model.RefreshAppearanceCommand.ExecuteAsync(null);
        Assert.True(model.AppearanceDetails.IsBusy);
        switch (change)
        {
            case "property": model.SelectedProperty = model.Properties[1]; break;
            case "node": model.SelectedNode = null; break;
            case "source": document.Content = "<Button/>"; break;
            case "scenario": model.UseDesignTimeValues = false; break;
            case "disconnect": client.Exit("Host ended"); break;
        }
        Assert.False(client.AppearanceTokens[0].IsCancellationRequested);
        pending.SetResult(AppearanceTestData.Response(client.AppearanceRequests[0], "Stale"));
        await reading;
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.Empty(model.AppearanceDetails.Facts);
        Assert.False(model.AppearanceDetails.IsBusy);
    }

    [Fact]
    public async Task PreviewAppearanceRejectsMismatchedAcknowledgementsAndWaitsForEdits()
    {
        var client = AppearancePreview();
        client.Appearance = request => Task.FromResult(AppearanceTestData.Response(request with { OwnerAssembly = "Other" }, "Wrong"));
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        await model.OpenAsync(Document("appearance-edit.xaml"));
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.AppearanceTabIsActive = true;
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.Contains("did not match", model.AppearanceDetails.Status);
        client.Appearance = request => Task.FromResult(AppearanceTestData.Response(request, "Updated"));
        var edit = new TaskCompletionSource<PreviewEditResult>();
        client.Edit = _ => edit.Task;
        var changing = model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.False(model.RefreshAppearanceCommand.CanExecute(null));
        var version = client.Requests.Last().Version;
        edit.SetResult(new(true, Snapshot(version), AppearanceInspection(version)));
        await changing;
        Assert.True(model.AppearanceDetails.Available);
        Assert.Equal("Updated", model.AppearanceDetails.Facts[0].Value);
    }
}

public sealed partial class InspectionViewModelTests
{
    private static InspectionElement AppearanceElement(InspectionNodeRequest request, string value) => Element(request, value) with
    {
        Properties = [new("Text", "System.Windows.Controls.TextBlock", "PresentationFramework", "System.String", value,
            "Style", false, false, false, PropertyId: "text-id", EditToken: "token-" + value, CanEdit: true, EditableValue: value),
            new("Tag", "System.Windows.FrameworkElement", "PresentationFramework", "System.Object", "Tag value",
                "Local", false, false, false, PropertyId: "tag-id", EditToken: "tag-token", CanEdit: true, EditableValue: "Tag value")]
    };
    private static FakeSession AppearanceSession(bool supported = true)
    {
        var session = new FakeSession
        {
            Inspect = request => Task.FromResult(AppearanceElement(request, "Hello")),
            Appearance = request => Task.FromResult(AppearanceTestData.Response(request, request.Property))
        };
        if (supported) session.Hello = session.Hello with { Capabilities = [.. session.Hello.Capabilities, "appearance"] };
        return session;
    }

    [Fact]
    public async Task LiveAppearanceRefreshUsesNewObservationIdentityWithoutOverwritingDraft()
    {
        var session = AppearanceSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Draft";
        Assert.Empty(session.AppearanceRequests);
        model.AppearanceTabIsActive = true;
        var first = Assert.Single(session.AppearanceRequests);
        Assert.Equal("text-id", first.PropertyId);
        Assert.Equal("System.Windows.Controls.TextBlock", first.OwnerType);
        Assert.Equal("Draft", model.EditedValue);
        session.Inspect = request => Task.FromResult(AppearanceElement(request, "Changed"));
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, session.AppearanceRequests.Count);
        Assert.True(session.AppearanceRequests.Last().Revision > first.Revision);
        Assert.Equal("Text", model.SelectedProperty!.Name);
        Assert.Equal("Draft", model.EditedValue);
        Assert.True(model.AppearanceDetails.Available);
        model.AppearanceTabIsActive = false;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, session.AppearanceRequests.Count);
        Assert.Null(model.AppearanceDetails.Snapshot);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("node")]
    [InlineData("pause")]
    [InlineData("disconnect")]
    [InlineData("replacement")]
    [InlineData("edit")]
    public async Task LiveAppearanceRejectsLateResultsAcrossSelectionAndSessionTransitions(string change)
    {
        var pending = new TaskCompletionSource<AppearanceResponse>();
        var session = AppearanceSession(); session.Appearance = _ => pending.Task;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        var reading = model.RefreshAppearanceCommand.ExecuteAsync(null);
        Assert.True(model.AppearanceDetails.IsBusy);
        switch (change)
        {
            case "property": model.SelectedProperty = model.Properties[1]; break;
            case "node": model.SelectedNode = model.Tree[1]; break;
            case "pause": model.SetDebuggerState(true); break;
            case "disconnect": await model.DisconnectCommand.ExecuteAsync(null); break;
            case "replacement": await model.AttachAsync(AppearanceSession()); break;
            case "edit": model.IsPropertyOperationRunning = true; break;
        }
        pending.SetResult(AppearanceTestData.Response(session.AppearanceRequests[0], "Stale"));
        await reading;
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.False(model.AppearanceDetails.IsBusy);
        if (change == "pause")
        {
            Assert.False(model.RefreshAppearanceCommand.CanExecute(null));
            model.SetDebuggerState(false);
            Assert.False(model.RefreshAppearanceCommand.CanExecute(null));
            Assert.DoesNotContain("Debugger paused", model.AppearanceDetails.Status);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(model.RefreshAppearanceCommand.CanExecute(null));
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LiveAppearanceDoesNotRequestUnsupportedOrUnavailableElements(bool supported, bool available)
    {
        var session = AppearanceSession(supported);
        session.Inspect = request => Task.FromResult(AppearanceElement(request, "Hello") with { Available = available });
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.AppearanceTabIsActive = true;
        await model.RefreshAppearanceCommand.ExecuteAsync(null);
        Assert.False(model.RefreshAppearanceCommand.CanExecute(null));
        Assert.Empty(session.AppearanceRequests);
        Assert.False(model.AppearanceDetails.Available);
    }

    [Fact]
    public async Task LiveAppearanceRejectsOpaqueIdentityMismatchAndClearsForSourceReview()
    {
        var session = AppearanceSession();
        session.Appearance = request => Task.FromResult(AppearanceTestData.Response(request with { PropertyId = "other-id" }, "Wrong"));
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.AppearanceTabIsActive = true;
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.Contains("did not match", model.AppearanceDetails.Status);
        session.Appearance = request => Task.FromResult(AppearanceTestData.Response(request, "Current"));
        await model.RefreshAppearanceCommand.ExecuteAsync(null);
        Assert.True(model.AppearanceDetails.Available);
        model.IsSourceEditRunning = true;
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.False(model.RefreshAppearanceCommand.CanExecute(null));
    }
}
