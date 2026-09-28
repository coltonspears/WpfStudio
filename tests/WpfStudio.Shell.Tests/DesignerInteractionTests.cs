using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    private static PreviewSnapshot InteractiveSnapshot(long version, string name = "Greeting") =>
        Snapshot(version, name) with { Surface = new("test-session", version, "surface-" + version) };

    private static FakePreview InteractiveClient() => new()
    {
        Render = request => Task.FromResult(InteractiveSnapshot(request.Version)),
        Capture = request => Task.FromResult(InteractiveSnapshot(request.Version, "After interaction"))
    };

    [Fact]
    public async Task InteractionRequiresCurrentSurfaceAndAnAvailableSession()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        Assert.False(model.EnterInteractionCommand.CanExecute(null));
        await model.OpenAsync(Document("interaction.xaml"));
        Assert.False(model.EnterInteractionCommand.CanExecute(null));
        model.EnterInteractionCommand.Execute(null);
        Assert.False(model.IsInteracting);

        client.Render = request => Task.FromResult(InteractiveSnapshot(request.Version));
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.EnterInteractionCommand.CanExecute(null));
        model.EnterInteractionCommand.Execute(null);
        Assert.False(model.IsInteracting);
        Assert.Null(model.InteractionSession);
        Assert.Contains("unavailable", model.Status);

        var session = new FakeInteraction(InteractiveSnapshot(client.Requests[^1].Version).Surface!);
        client.Interaction = _ => session;
        model.EnterInteractionCommand.Execute(null);
        Assert.True(model.IsInteracting);
        Assert.Same(session, model.InteractionSession);
        Assert.Equal(1, session.SubscriberCount);
        Assert.False(model.EnterInteractionCommand.CanExecute(null));
        Assert.True(model.ExitInteractionCommand.CanExecute(null));
        Assert.Equal(100, model.InteractionWidth);
        Assert.Equal(30, model.InteractionHeight);
    }

    [Fact]
    public async Task InspectDetachesThenCapturesSameInstanceAndPreservesPropertyDraft()
    {
        var client = InteractiveClient();
        FakeInteraction? session = null;
        client.Interaction = surface => session ??= new(surface);
        client.Inspect = request => Task.FromResult(new PreviewInspection(request.Version, Node(),
            [CaptureSizeProperty("Width", "100")], []));
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var document = Document("interaction.xaml");
        await model.OpenAsync(document);
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0];
        model.EditedValue = "145";
        model.EnterInteractionCommand.Execute(null);
        var detached = new TaskCompletionSource();
        session!.Deactivate = () => detached.Task;

        var inspect = model.ExitInteractionCommand.ExecuteAsync(null);
        Assert.False(model.IsInteracting);
        Assert.False(model.EnterInteractionCommand.CanExecute(null));
        Assert.Empty(client.Captures);
        detached.SetResult();
        await inspect;

        Assert.Single(client.Requests);
        Assert.Single(client.Captures);
        Assert.Equal(client.Requests[0].Version, client.Captures[0].Version);
        Assert.Equal(1, session.Deactivations);
        Assert.Equal("After interaction", model.SelectedNode!.Node.Name);
        Assert.Equal("Width", model.SelectedProperty!.Name);
        Assert.Equal("145", model.EditedValue);
        Assert.Equal("<TextBlock Text=\"Hello\" />", document.Content);
        Assert.True(model.EnterInteractionCommand.CanExecute(null));
        model.EnterInteractionCommand.Execute(null);
        Assert.Same(session, model.InteractionSession);
        Assert.Equal(1, session.SubscriberCount);
        Assert.True(model.IsInteracting);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task SessionFromAnOlderSurfaceCannotEnterInteraction()
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface with { Version = surface.Version - 1 });
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("interaction.xaml"));
        model.EnterInteractionCommand.Execute(null);
        Assert.False(model.IsInteracting);
        Assert.Null(model.InteractionSession);
        Assert.True(model.IsCurrent);
        Assert.Contains("unavailable", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedDetachCannotCaptureOrReplaceNewerSource(bool refresh)
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface);
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("interaction.xaml");
        await model.OpenAsync(document);
        model.EnterInteractionCommand.Execute(null);
        var session = Assert.IsType<FakeInteraction>(model.InteractionSession);
        var detached = new TaskCompletionSource();
        session.Deactivate = () => detached.Task;
        var inspect = model.ExitInteractionCommand.ExecuteAsync(null);
        document.Content = "<Button />";
        if (refresh)
        {
            await model.RefreshCommand.ExecuteAsync(null);
            model.EnterInteractionCommand.Execute(null);
        }
        var status = model.Status;
        detached.SetResult();
        await inspect;

        Assert.Empty(client.Captures);
        Assert.Equal(status, model.Status);
        Assert.Equal(refresh, model.IsCurrent);
        Assert.Equal(refresh, model.IsInteracting);
        Assert.Equal(0, session.SubscriberCount);
        if (refresh) Assert.NotSame(session, model.InteractionSession);
        else
        {
            Assert.Null(model.InteractionSession);
            Assert.Equal(0, model.InteractionWidth);
            Assert.Equal(0, model.InteractionHeight);
        }
        Assert.True(session.Deactivations >= 2);
    }

    [Fact]
    public async Task RetiredSessionNotificationCannotInvalidateNewPreview()
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface);
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("interaction.xaml"));
        model.EnterInteractionCommand.Execute(null);
        var first = Assert.IsType<FakeInteraction>(model.InteractionSession);
        var delayedNotification = first.CaptureNotification(new(false, "Old watchdog"));
        await model.RefreshCommand.ExecuteAsync(null);
        model.EnterInteractionCommand.Execute(null);
        var second = model.InteractionSession;
        var status = model.Status;

        delayedNotification();

        Assert.Equal(0, first.SubscriberCount);
        Assert.Same(second, model.InteractionSession);
        Assert.True(model.IsInteracting);
        Assert.True(model.IsCurrent);
        Assert.Equal(status, model.Status);
    }

    [Fact]
    public async Task SourceEditWithAutoRefreshPausedDetachesWithoutRelyingOnAView()
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface);
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("interaction.xaml");
        await model.OpenAsync(document);
        model.EnterInteractionCommand.Execute(null);
        var session = Assert.IsType<FakeInteraction>(model.InteractionSession);
        document.Content = "<Grid />";
        Assert.Equal(1, session.Deactivations);
        Assert.Null(model.InteractionSession);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task CurrentSessionFailureInvalidatesNativeAndSnapshotObservations()
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface);
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("interaction.xaml"));
        model.SelectedNode = model.Tree[0];
        model.EnterInteractionCommand.Execute(null);
        var session = Assert.IsType<FakeInteraction>(model.InteractionSession);

        session.CaptureNotification(new(false, "The preview stopped responding."))();

        Assert.False(model.IsCurrent);
        Assert.False(model.IsInteracting);
        Assert.Null(model.InteractionSession);
        Assert.Empty(model.Tree);
        Assert.Empty(model.Properties);
        Assert.Null(model.Image);
        Assert.False(model.EnterInteractionCommand.CanExecute(null));
        Assert.Equal("The preview stopped responding.", model.Status);
        Assert.Equal(0, session.SubscriberCount);
    }

    [Fact]
    public async Task DisposeDropsInteractionSubscriptionAndLateNotifications()
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface);
        var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("interaction.xaml"));
        model.EnterInteractionCommand.Execute(null);
        var session = Assert.IsType<FakeInteraction>(model.InteractionSession);
        var delayedNotification = session.CaptureNotification(new(false, "Disposed callback"));
        await model.DisposeAsync();
        var status = model.Status;
        delayedNotification();
        Assert.Equal(0, session.SubscriberCount);
        Assert.Null(model.InteractionSession);
        Assert.False(model.IsInteracting);
        Assert.False(model.EnterInteractionCommand.CanExecute(null));
        Assert.Equal(status, model.Status);
    }

    [Fact]
    public async Task SnapshotFailureAfterInspectRetainsCurrentViewForRetry()
    {
        var client = InteractiveClient();
        client.Interaction = surface => new FakeInteraction(surface);
        client.Capture = request => Task.FromResult(new PreviewSnapshot(request.Version, false, null, 0, 0, [], [], Status: "Capture unavailable"));
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("interaction.xaml"));
        model.EnterInteractionCommand.Execute(null);
        var session = model.InteractionSession;
        await model.ExitInteractionCommand.ExecuteAsync(null);
        Assert.False(model.IsInteracting);
        Assert.True(model.IsCurrent);
        Assert.Same(session, model.InteractionSession);
        Assert.True(model.EnterInteractionCommand.CanExecute(null));
        Assert.Equal("Capture unavailable", model.Status);
        Assert.Single(client.Requests);
    }

    private sealed class FakeInteraction(PreviewSurfaceIdentity surface) : IPreviewInteractionSession
    {
        private EventHandler<PreviewInteractionEvent>? _changed;
        public PreviewSurfaceIdentity Surface { get; } = surface;
        public bool IsAvailable { get; set; } = true;
        public bool IsAttached { get; set; }
        public int SubscriberCount { get; private set; }
        public int Deactivations { get; private set; }
        public Func<Task> Deactivate { get; set; } = () => Task.CompletedTask;
        public event EventHandler<PreviewInteractionEvent>? Changed
        {
            add { _changed += value; SubscriberCount++; }
            remove { _changed -= value; SubscriberCount--; }
        }
        public Action CaptureNotification(PreviewInteractionEvent update)
        {
            var changed = _changed;
            return () => changed?.Invoke(this, update);
        }
        public Task<PreviewSurfaceResponse> UpdateAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PreviewSurfaceResponse(request, true));
        public Task DeactivateAsync(CancellationToken cancellationToken = default) { Deactivations++; return Deactivate(); }
        public void Abort(string reason) { IsAvailable = false; IsAttached = false; }
    }
}
