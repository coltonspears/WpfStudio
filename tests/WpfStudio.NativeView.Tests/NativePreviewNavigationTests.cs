using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;

namespace WpfStudio.NativeView.Tests;

public sealed partial class NativePreviewPaneTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReinsertingTheSameSessionAfterClearingItReleasesTheOldAttachment(bool pendingAttach) => RunStaAsync(async () =>
    {
        var session = new FakeSession { HoldAttach = pendingAttach };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        PreviewSurfaceRequest? pendingUpdate = null;
        try
        {
            if (pendingAttach) await Until(pane, () => session.Requests.Any(request => request.Action == PreviewSurfaceAction.Attach));
            else
            {
                await Until(pane, () => pane.IsAttached);
                await Idle();
                session.OnUpdate = _ => response.Task;
                pane.Surface.SetScrollOffsets(20, 20);
                pendingUpdate = session.Requests.Last();
                Assert.Equal(PreviewSurfaceAction.Update, pendingUpdate.Action);
            }
            var first = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            pane.Session = null;
            pane.Session = session;
            Assert.False(pane.IsAttached);
            session.OnUpdate = null;
            if (pendingAttach) session.ReleaseAttach();
            else response.SetResult(new(pendingUpdate!, true));
            await Until(pane, () => pane.IsAttached);
            var attaches = session.Requests.Where(request => request.Action == PreviewSurfaceAction.Attach).ToArray();
            Assert.Equal(2, attaches.Length);
            Assert.Equal(1, session.Deactivations);
            Assert.NotEqual(first.NavigationToken, attaches[1].NavigationToken);
            Assert.Equal(0, session.Aborts);
            Assert.Same(session, pane.Session);
            Assert.All(session.Requests.SkipWhile(request => request.Sequence < attaches[1].Sequence),
                request => Assert.Equal(attaches[1].NavigationToken, request.NavigationToken));
        }
        finally
        {
            session.ReleaseAttach();
            if (pendingUpdate is not null) response.TrySetResult(new(pendingUpdate, true));
            window.Close(); await Idle();
        }
    });

    [Fact]
    public Task RapidSuppressionDuringPendingAttachDetachesBeforeIssuingANewToken() => RunStaAsync(async () =>
    {
        var session = new FakeSession { HoldAttach = true };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => session.Requests.Any(request => request.Action == PreviewSurfaceAction.Attach));
            var first = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint bridge = pane.Surface.BridgeHandle;
            Assert.False(pane.IsAttached);
            Assert.True(session.IsAttached);
            pane.IsSuppressed = true;
            pane.IsSuppressed = false;
            session.ReleaseAttach();
            await Until(pane, () => pane.IsAttached);
            var attaches = session.Requests.Where(request => request.Action == PreviewSurfaceAction.Attach).ToArray();
            Assert.Equal(2, attaches.Length);
            Assert.Equal(1, session.Deactivations);
            Assert.NotEqual(first.NavigationToken, attaches[1].NavigationToken);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            Assert.Equal(0, session.Aborts);
            Assert.All(session.Requests.SkipWhile(request => !ReferenceEquals(request, attaches[1])),
                request => Assert.Equal(attaches[1].NavigationToken, request.NavigationToken));
        }
        finally { session.ReleaseAttach(); window.Close(); await Idle(); }
    });

    [Fact]
    public Task RapidSuppressionDuringAnUpdateRetainsTheAcknowledgedAttachmentLease() => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => pane.IsAttached);
            await Idle();
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnUpdate = _ => response.Task;
            pane.Surface.SetScrollOffsets(20, 20);
            var update = session.Requests.Last();
            Assert.Equal(PreviewSurfaceAction.Update, update.Action);
            pane.IsSuppressed = true;
            pane.IsSuppressed = false;
            session.OnUpdate = null;
            response.SetResult(new(update, true));
            await Until(pane, () => pane.IsAttached && session.Requests.Count(request => request.Action == PreviewSurfaceAction.Update) >= 2);
            Assert.Equal(0, session.Aborts);
            Assert.Single(session.Requests, request => request.Action == PreviewSurfaceAction.Attach);
            Assert.All(session.Requests, request => Assert.Equal(attach.NavigationToken, request.NavigationToken));
            Assert.True(session.IsAvailable);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task NativeBoundaryValidatesItsLeaseAndMovesOnlyOnce() => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 500, ContentHeight = 400 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint child = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            nint owner = new WindowInteropHelper(window).Handle;
            nint destination = CreateNativeChild(owner);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink) { Navigate = request =>
            {
                Assert.False(sink.TabInto(request)); // No asynchronous same-host wrap during a synchronous handoff.
                // Focusing the WPF root can restore focus to its previous
                // hosted child. Use a distinct owned native sibling here.
                NavigationTestNative.SetFocus(destination);
                Assert.Equal(destination, NavigationTestNative.GetFocus());
                return true;
            } };
            sink.KeyboardInputSite = site;
            NavigationTestNative.SetFocus(child); // Owned local HWND, not simulated physical keyboard input.
            Assert.True(sink.HasFocusWithin());
            Assert.Equal(0, SendBoundary(pane, child, 5)); // No call-scoped sender property.
            Assert.Empty(site.Directions);
            string property = PreviewNativeNavigation.PropertyPrefix + attach.NavigationToken;
            NavigationTestNative.SetProp(child, property, 5);
            Assert.Equal(1, SendBoundary(pane, child, 5));
            Assert.Equal(FocusNavigationDirection.Next, Assert.Single(site.Directions));
            Assert.False(sink.HasFocusWithin());
            NavigationTestNative.SetFocus(child);
            Assert.Equal(0, SendBoundary(pane, child, 5)); // Duplicate sequence, even with restored native focus.
            NavigationTestNative.SetProp(child, property, 10);
            Assert.Equal(1, SendBoundary(pane, child, 10));
            Assert.Equal(new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous }, site.Directions);
            Assert.DoesNotContain(session.Requests, IsFocus);
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Fact]
    public Task ReportedSuccessWithoutActualFocusMovementIsRejected() => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 500, ContentHeight = 400 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint child = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            NavigationTestNative.SetFocus(child);
            NavigationTestNative.SetProp(child, PreviewNativeNavigation.PropertyPrefix + attach.NavigationToken, 5);
            Assert.Equal(0, SendBoundary(pane, child, 5));
            Assert.Single(site.Directions);
            Assert.True(sink.HasFocusWithin());
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Fact]
    public Task ReattachedBridgeRejectsItsPreviousNavigationToken() => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 500, ContentHeight = 400 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            var first = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint firstChild = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            pane.IsSuppressed = true;
            await Until(pane, () => !pane.IsAttached);
            pane.IsSuppressed = false;
            await Until(pane, () => pane.IsAttached);
            var second = session.Requests.Last(request => request.Action == PreviewSurfaceAction.Attach);
            Assert.NotEqual(first.NavigationToken, second.NavigationToken);
            nint child = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            originalSite = sink.KeyboardInputSite;
            var site = new RecordingKeyboardSite(sink);
            sink.KeyboardInputSite = site;
            NavigationTestNative.SetFocus(child);
            NavigationTestNative.SetProp(child, PreviewNativeNavigation.PropertyPrefix + first.NavigationToken, 5);
            Assert.Equal(0, SendBoundary(pane, child, 5));
            NavigationTestNative.SetProp(firstChild, PreviewNativeNavigation.PropertyPrefix + second.NavigationToken, 5);
            Assert.Equal(0, SendBoundary(pane, firstChild, 5));
            Assert.Empty(site.Directions);
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("parent")]
    [InlineData("owner")]
    public Task AuthenticatedSessionRejectsAnUnverifiedNativeWindow(string invalid) => RunStaAsync(async () =>
    {
        var session = new FakeSession { NativeProcessId = Environment.ProcessId };
        nint orphan = 0;
        session.OnAttach = request =>
        {
            if (invalid == "owner") session.NativeProcessId = int.MaxValue;
            nint child = invalid == "missing" ? 0 : CreateNativeChild(invalid == "parent" ? 0 : new nint(request.ParentHandle));
            if (invalid == "parent") orphan = child;
            return Task.FromResult(new PreviewSurfaceResponse(request, true, NativeHandle: child));
        };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => session.Aborts == 1);
            Assert.False(pane.IsAttached);
            Assert.Contains("native window", pane.Status);
        }
        finally { window.Close(); if (orphan != 0) NavigationTestNative.DestroyWindow(orphan); await Idle(); }
    });

    [Fact]
    public Task FocusGeometryScrollsMinimallyAndDoesNotUndoLaterManualScrolling() => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => pane.IsAttached);
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint child = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            NavigationTestNative.SetFocus(child);
            var horizontal = pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
            var vertical = pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
            var observation = Observation(pane, attach, child, 1, 1, new(840, 640, 40, 30));
            session.RaiseFocus(observation);
            await Until(pane, () => horizontal.Value > 0 && vertical.Value > 0);
            Assert.Equal(880 - pane.Surface.ViewportWidth, horizontal.Value, 3);
            Assert.Equal(670 - pane.Surface.ViewportHeight, vertical.Value, 3);
            double scale = GetDpiForWindow(pane.Surface.BridgeHandle) / 96d;
            Assert.Equal((int)Math.Round(horizontal.Value * scale), session.Requests.Last().OffsetX);
            Assert.Equal((int)Math.Round(vertical.Value * scale), session.Requests.Last().OffsetY);

            horizontal.Value = 0; vertical.Value = 0;
            session.RaiseFocus(observation); // Same geometry sequence cannot replay.
            session.RaiseFocus(observation with { Sequence = 2 }); // Same focused object cannot override manual scrolling.
            await Idle();
            Assert.Equal(0, horizontal.Value); Assert.Equal(0, vertical.Value);
            await Task.Delay(25);
            var next = Observation(pane, attach, child, 3, 2, new(840, 640, 40, 30));
            session.RaiseFocus(next);
            await Until(pane, () => horizontal.Value > 0 && vertical.Value > 0);
            session.RaiseFocus(next with { Sequence = 2, Bounds = new(0, 0, 10, 10) });
            session.RaiseFocus(next with { Sequence = 4, Bounds = new(double.NaN, 0, 10, 10) });
            session.RaiseFocus(next with { Sequence = 5, Dpi = next.Dpi + 1, Bounds = new(0, 0, 10, 10) });
            session.RaiseFocus(next with { Sequence = 6, AttachmentSequence = attach.Sequence + 999, Bounds = new(0, 0, 10, 10) });
            await Idle();
            Assert.True(horizontal.Value > 0); Assert.True(vertical.Value > 0);
            session.RaiseFocus(next with { Sequence = 7, Bounds = new(-50, -50, 2000, 2000) });
            await Idle(); // Oversized overlapping target retains the visible portion.
            Assert.Equal(880 - pane.Surface.ViewportWidth, horizontal.Value, 3);
            Assert.Equal(670 - pane.Surface.ViewportHeight, vertical.Value, 3);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task FocusObservationBeforeAttachReplyIsAppliedOnlyToThatExactAttachment() => RunStaAsync(async () =>
    {
        var response = new TaskCompletionSource<PreviewSurfaceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint child = 0;
        var session = new FakeSession { NativeProcessId = Environment.ProcessId, OnAttach = request =>
        {
            child = CreateNativeChild(new nint(request.ParentHandle));
            return response.Task;
        } };
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => child != 0);
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            NavigationTestNative.SetFocus(child);
            session.RaiseFocus(Observation(pane, attach, child, 1, 1, new(840, 640, 40, 30)));
            await Idle();
            Assert.False(pane.IsAttached);
            response.SetResult(new(attach, true, NativeHandle: child));
            var horizontal = pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
            await Until(pane, () => pane.IsAttached && horizontal.Value > 0);
        }
        finally { window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData(false, 500, 1000, 0, 500)]
    [InlineData(false, -500, 600, 400, 0)]
    [InlineData(false, 100, 1000, 400, 400)]
    [InlineData(true, 400, 1000, 0, 400)]
    [InlineData(true, -500, 600, 300, 0)]
    [InlineData(true, 100, 1000, 300, 300)]
    public Task OversizedFocusTargetsRevealTheNearestEdgeOrPreserveOverlap(bool verticalAxis, double start, double size,
        double initial, double expected) => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        try
        {
            await Until(pane, () => pane.IsAttached);
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint child = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            NavigationTestNative.SetFocus(child);
            var bar = pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == (verticalAxis ? Orientation.Vertical : Orientation.Horizontal));
            bar.Value = initial;
            await Task.Delay(25); // The new focus follows, rather than overrides, the manual scroll.
            var bounds = verticalAxis ? new PreviewBounds(0, start, 10, size) : new PreviewBounds(start, 0, size, 10);
            session.RaiseFocus(Observation(pane, attach, child, 1, 1, bounds));
            await Idle();
            Assert.Equal(expected, bar.Value, 3);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task ThrowingTraversalReportsUnknownAfterPossiblePartialMovement() => RunStaAsync(async () =>
    {
        var session = NativeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 500, ContentHeight = 400 };
        var window = Open(pane);
        var sink = (IKeyboardInputSink)pane.Surface;
        IKeyboardInputSite? originalSite = null;
        try
        {
            await Until(pane, () => pane.IsAttached);
            var attach = session.Requests.Single(request => request.Action == PreviewSurfaceAction.Attach);
            nint child = NavigationTestNative.GetWindow(pane.Surface.BridgeHandle, 5);
            originalSite = sink.KeyboardInputSite;
            sink.KeyboardInputSite = new RecordingKeyboardSite(sink) { Navigate = _ => throw new InvalidOperationException("Traversal failed.") };
            NavigationTestNative.SetFocus(child);
            NavigationTestNative.SetProp(child, PreviewNativeNavigation.PropertyPrefix + attach.NavigationToken, 5);
            Assert.Equal(PreviewNativeNavigation.Unknown, SendBoundary(pane, child, 5));
        }
        finally { sink.KeyboardInputSite = originalSite; window.Close(); await Idle(); }
    });

    private static FakeSession NativeSession() => new()
    {
        NativeProcessId = Environment.ProcessId,
        OnAttach = request => Task.FromResult(new PreviewSurfaceResponse(request, true, NativeHandle: CreateNativeChild(new nint(request.ParentHandle))))
    };

    private static nint CreateNativeChild(nint parent)
    {
        nint child = NavigationTestNative.CreateWindowEx(0, "STATIC", "Owned native test child", parent == 0 ? unchecked((int)0x80000000) : 0x50000000,
            0, 0, 200, 100, parent, 0, 0, 0);
        Assert.NotEqual(nint.Zero, child);
        return child;
    }

    private static PreviewSurfaceFocus Observation(NativePreviewPane pane, PreviewSurfaceRequest attach, nint child,
        long sequence, long epoch, PreviewBounds bounds) => new(attach.BridgeToken, attach.Sequence, sequence, epoch,
            Environment.TickCount64, child, GetDpiForWindow(pane.Surface.BridgeHandle), bounds);

    private static long SendBoundary(NativePreviewPane pane, nint child, nint value) => NavigationTestNative.SendMessage(
        pane.Surface.BridgeHandle, NavigationTestNative.RegisterWindowMessage(PreviewNativeNavigation.MessageName), child, value).ToInt64();

    private static class NavigationTestNative
    {
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32")] public static extern nint SetFocus(nint hwnd);
        [DllImport("user32")] public static extern nint GetFocus();
        [DllImport("user32")] public static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool SetProp(nint hwnd, string property, nint value);
        [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint CreateWindowEx(int exStyle, string className, string name, int style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    }
}
