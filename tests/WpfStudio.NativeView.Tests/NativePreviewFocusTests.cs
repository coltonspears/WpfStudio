using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;

namespace WpfStudio.NativeView.Tests;

public sealed partial class NativePreviewPaneTests
{
    // These exercise the keyboard sink and input pipeline directly. They are
    // control/protocol regressions, not evidence of physical keyboard fidelity.
    [Theory]
    [InlineData(FocusNavigationDirection.Next, PreviewSurfaceAction.FocusFirst)]
    [InlineData(FocusNavigationDirection.Previous, PreviewSurfaceAction.FocusLast)]
    public Task EmptyPreviewContinuesOnlyFromItsExactFocusResponse(FocusNavigationDirection direction, PreviewSurfaceAction action) => RunStaAsync(async () =>
    {
        var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { OnFocus = _ => response.Task };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            Assert.True(sink.TabInto(new TraversalRequest(direction)));
            await Until(pane, () => session.Requests.Any(request => request.Action == action));
            var request = session.Requests.Last(request => request.Action == action);
            Assert.True(Guid.TryParseExact(request.FocusToken, "N", out _));
            Assert.Equal(new nint(1), GetProp(pane.Surface.BridgeHandle, FocusProperty(request)));
            Assert.Empty(site.Directions);
            response.SetResult(new(request, true, Focused: false));
            await Until(pane, () => site.Directions.Count == 1);
            Assert.Equal(direction, Assert.Single(site.Directions));
            Assert.Equal(nint.Zero, GetProp(pane.Surface.BridgeHandle, FocusProperty(request)));
            await Idle();
            Assert.Single(site.Directions);
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public Task SuccessfulOrSupersededEntryDoesNotContinueNavigation(bool? focused) => RunStaAsync(async () =>
    {
        var session = new FakeSession { OnFocus = request => Task.FromResult(new PreviewSurfaceResponse(request, true, Focused: focused)) };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            Assert.True(sink.TabInto(new TraversalRequest(FocusNavigationDirection.Next)));
            await Until(pane, () => session.Requests.Any(IsFocus));
            await Idle();
            Assert.Empty(site.Directions);
            Assert.Equal(nint.Zero, GetProp(pane.Surface.BridgeHandle, FocusProperty(session.Requests.Single(IsFocus))));
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData("key")]
    [InlineData("focus")]
    [InlineData("suppression")]
    [InlineData("session")]
    public Task LaterInputOrContextRevokesTheGrantBeforeAnOldReply(string change) => RunStaAsync(async () =>
    {
        var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { OnFocus = _ => response.Task };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            Assert.True(sink.TabInto(new TraversalRequest(FocusNavigationDirection.Next)));
            await Until(pane, () => session.Requests.Any(IsFocus));
            var request = session.Requests.Single(IsFocus);
            nint bridge = pane.Surface.BridgeHandle;
            Assert.Equal(new nint(1), GetProp(bridge, FocusProperty(request)));
            switch (change)
            {
                case "key":
                    InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(pane)!, Environment.TickCount, Key.Space)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = pane });
                    break;
                case "focus":
                    InputManager.Current.ProcessInput(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,
                        Environment.TickCount, pane.Surface, new TextBox())
                    { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent, Source = pane });
                    break;
                case "suppression": pane.IsSuppressed = true; break;
                case "session": pane.Session = new FakeSession(); break;
            }
            Assert.Equal(nint.Zero, GetProp(bridge, FocusProperty(request)));
            response.SetResult(new(request, true, Focused: false));
            await Idle();
            Assert.Empty(site.Directions);
            Assert.Equal(0, session.Aborts);
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Fact]
    public Task OlderSameDirectionReplyCannotClearANewerFocusGrant() => RunStaAsync(async () =>
    {
        var replies = new List<TaskCompletionSource<PreviewSurfaceResponse>>();
        var session = new FakeSession { OnFocus = _ =>
        {
            var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            replies.Add(response); return response.Task;
        } };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            Assert.True(sink.TabInto(new TraversalRequest(FocusNavigationDirection.Next)));
            await Until(pane, () => replies.Count == 1);
            var first = session.Requests.Single(IsFocus);
            Assert.True(sink.TabInto(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.Equal(nint.Zero, GetProp(pane.Surface.BridgeHandle, FocusProperty(first)));
            replies[0].SetResult(new(first, true, Focused: false));
            await Until(pane, () => replies.Count == 2);
            var second = session.Requests.Last(IsFocus);
            Assert.NotEqual(first.FocusToken, second.FocusToken);
            Assert.Equal(new nint(1), GetProp(pane.Surface.BridgeHandle, FocusProperty(second)));
            Assert.Empty(site.Directions);
            replies[1].SetResult(new(second, true, Focused: false));
            await Until(pane, () => site.Directions.Count == 1);
            Assert.Equal(FocusNavigationDirection.Next, Assert.Single(site.Directions));
            Assert.Equal(nint.Zero, GetProp(pane.Surface.BridgeHandle, FocusProperty(second)));
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Fact]
    public Task FocusGrantExpiresWhileTheResponseIsPending() => RunStaAsync(async () =>
    {
        var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { OnFocus = _ => response.Task };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            Assert.True(sink.TabInto(new TraversalRequest(FocusNavigationDirection.Next)));
            await Until(pane, () => session.Requests.Any(IsFocus));
            var request = session.Requests.Single(IsFocus);
            Assert.Equal(new nint(1), GetProp(pane.Surface.BridgeHandle, FocusProperty(request)));
            await Until(pane, () => GetProp(pane.Surface.BridgeHandle, FocusProperty(request)) == nint.Zero);
            response.SetResult(new(request, true, Focused: false));
            await Idle();
            Assert.Empty(site.Directions);
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData(FocusNavigationDirection.Left)]
    [InlineData(FocusNavigationDirection.Right)]
    [InlineData(FocusNavigationDirection.Up)]
    [InlineData(FocusNavigationDirection.Down)]
    public Task ArrowNavigationDoesNotRequestRemoteFocus(FocusNavigationDirection direction) => RunStaAsync(async () =>
    {
        var session = new FakeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => pane.IsAttached);
            Assert.False(((IKeyboardInputSink)pane.Surface).TabInto(new TraversalRequest(direction)));
            await Idle();
            Assert.DoesNotContain(session.Requests, IsFocus);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task DelayedBoundaryNavigationCannotMoveFocusOutsideThePreview() => RunStaAsync(async () =>
    {
        var session = new FakeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            Assert.False(sink.HasFocusWithin());
            session.RaiseNavigation(PreviewSurfaceNavigation.Next, 1);
            session.RaiseNavigation(PreviewSurfaceNavigation.Previous, 2);
            await Idle();
            Assert.Empty(site.Directions);
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    private static bool IsFocus(PreviewSurfaceRequest request) => request.Action is PreviewSurfaceAction.FocusFirst or PreviewSurfaceAction.FocusLast;
    private static string FocusProperty(PreviewSurfaceRequest request) => "WpfStudio.PreviewFocus." + request.FocusToken;

    private sealed class RecordingKeyboardSite(IKeyboardInputSink sink) : IKeyboardInputSite
    {
        public IKeyboardInputSink Sink => sink;
        public List<FocusNavigationDirection> Directions { get; } = [];
        public Func<TraversalRequest, bool>? Navigate { get; set; }
        public void Unregister() { }
        public bool OnNoMoreTabStops(TraversalRequest request) { Directions.Add(request.FocusNavigationDirection); return Navigate?.Invoke(request) ?? true; }
    }
}
