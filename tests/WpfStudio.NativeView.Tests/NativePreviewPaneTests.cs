using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.NativeView.Tests;

[CollectionDefinition("Native preview view", DisableParallelization = true)]
public sealed class NativePreviewViewCollection { }

[Collection("Native preview view")]
public sealed partial class NativePreviewPaneTests
{
    [Fact]
    public Task ContainerLeaseScrollingAndSuppressionPreserveTheSession() => RunStaAsync(async () =>
    {
        var session = new FakeSession();
        var pane = new NativePreviewPane { Session = session, ContentWidth = 900, ContentHeight = 700 };
        var window = Open(pane);
        try
        {
            await Idle();
            pane.IsActive = true;
            await Until(pane, () => pane.IsAttached);
            nint bridge = pane.Surface.BridgeHandle;
            string token = pane.Surface.BridgeToken;
            Assert.NotEqual(nint.Zero, bridge);
            Assert.Equal(new nint(1), GetProp(bridge, "WpfStudio.PreviewBridge." + token));
            GetWindowThreadProcessId(bridge, out uint owner);
            Assert.Equal((uint)Environment.ProcessId, owner);
            Assert.True(session.Requests.All(request => request.ParentHandle == bridge.ToInt64()
                && request.ParentProcessId == Environment.ProcessId && request.BridgeToken == token));

            var horizontal = pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Horizontal);
            var vertical = pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
            Assert.True(horizontal.Maximum > 0); Assert.True(vertical.Maximum > 0);
            horizontal.Value = horizontal.Maximum; vertical.Value = vertical.Maximum;
            double scale = GetDpiForWindow(bridge) / 96d;
            await Until(pane, () => session.Requests.Last().OffsetX == (int)Math.Round(horizontal.Value * scale)
                && session.Requests.Last().OffsetY == (int)Math.Round(vertical.Value * scale));
            var position = session.Requests.Last();
            Assert.Equal((int)Math.Round(pane.Surface.ViewportWidth * scale), position.PixelWidth);
            Assert.Equal((int)Math.Round(pane.Surface.ViewportHeight * scale), position.PixelHeight);

            pane.IsSuppressed = true;
            await Until(pane, () => !pane.IsAttached && session.Deactivations > 0);
            Assert.Equal(Visibility.Hidden, pane.Surface.Visibility);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            Assert.Equal(0, session.Aborts);
            pane.IsSuppressed = false;
            await Until(pane, () => pane.IsAttached);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            Assert.Equal(token, pane.Surface.BridgeToken);

            pane.IsActive = false;
            await Until(pane, () => !pane.IsAttached);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            pane.IsActive = true;
            await Until(pane, () => pane.IsAttached);
            Assert.Equal(session.Surface, session.Requests.Last().Surface);
            pane.ContentWidth = double.NaN;
            Assert.Equal(0, horizontal.Maximum);
            Assert.Equal(0, horizontal.Value);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task PendingAttachIsRevokedBeforeDestroyingItsLocalParent() => RunStaAsync(async () =>
    {
        var session = new FakeSession { HoldAttach = true };
        var pane = new NativePreviewPane { Session = session, ContentWidth = 400, ContentHeight = 300, IsActive = true };
        var window = Open(pane);
        await Until(pane, () => session.Requests.Count > 0);
        nint bridge = pane.Surface.BridgeHandle;
        string token = pane.Surface.BridgeToken;
        session.OnAbort = () =>
        {
            Assert.True(IsWindow(bridge));
            Assert.Equal(new nint(1), GetProp(bridge, "WpfStudio.PreviewBridge." + token));
        };
        window.Close();
        await Idle();
        Assert.Equal(1, session.Aborts);
        Assert.False(pane.IsAttached);
        Assert.False(session.IsAttached);
        Assert.False(IsWindowVisible(bridge));
        // HwndHost can retain a hidden local bridge after source removal so it
        // can be docked into another window. Disposal owns its final teardown.
        pane.Surface.Dispose();
        Assert.False(IsWindow(bridge));
        Assert.Equal(1, session.Aborts);
    });

    [Fact]
    public Task ReplacingPendingSessionRejectsItsLaterReplyAndNavigation() => RunStaAsync(async () =>
    {
        var first = new FakeSession { HoldAttach = true };
        var second = new FakeSession();
        int exits = 0;
        var pane = new NativePreviewPane { Session = first, ContentWidth = 400, ContentHeight = 300,
            IsActive = true, ExitInteractionCommand = new ActionCommand(() => exits++) };
        var window = Open(pane);
        try
        {
            await Until(pane, () => first.Requests.Count > 0);
            pane.Session = second;
            first.ReleaseAttach();
            await Until(pane, () => pane.IsAttached && second.Requests.Count > 0);
            Assert.Equal(1, first.Deactivations);
            Assert.Equal(0, first.Aborts);
            first.RaiseNavigation(PreviewSurfaceNavigation.Inspect, 1);
            await Idle();
            Assert.Equal(0, exits);
            second.RaiseNavigation(PreviewSurfaceNavigation.Inspect, 1);
            second.RaiseNavigation(PreviewSurfaceNavigation.Inspect, 1);
            await Idle();
            Assert.Equal(1, exits);
            pane.IsActive = false;
            second.RaiseNavigation(PreviewSurfaceNavigation.Inspect, 2);
            await Idle();
            Assert.Equal(1, exits);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task CancelledOwnerCloseAllowsAFreshSessionInTheSameControl() => RunStaAsync(async () =>
    {
        var first = new FakeSession();
        var pane = new NativePreviewPane { Session = first, ContentWidth = 400, ContentHeight = 300, IsActive = true };
        var window = Open(pane);
        System.ComponentModel.CancelEventHandler cancel = (_, args) => args.Cancel = true;
        try
        {
            await Until(pane, () => pane.IsAttached);
            nint bridge = pane.Surface.BridgeHandle;
            window.Closing += cancel;
            window.Close();
            Assert.True(IsWindow(bridge));
            Assert.Equal(1, first.Aborts);
            var replacement = new FakeSession();
            pane.Session = replacement;
            await Until(pane, () => pane.IsAttached && replacement.Requests.Count > 0);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
        }
        finally { window.Closing -= cancel; window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OldAttachExceptionCannotDiscardANewerSession(bool closeWasCancelled) => RunStaAsync(async () =>
    {
        var first = new FakeSession { HoldAttach = true, ThrowWhenAborted = true };
        var replacement = new FakeSession();
        var pane = new NativePreviewPane { Session = first, ContentWidth = 400, ContentHeight = 300, IsActive = true };
        var window = Open(pane);
        System.ComponentModel.CancelEventHandler cancel = (_, args) => args.Cancel = true;
        try
        {
            await Until(pane, () => first.Requests.Count > 0);
            nint bridge = pane.Surface.BridgeHandle;
            if (closeWasCancelled)
            {
                window.Closing += cancel;
                window.Close();
            }
            pane.Session = replacement;
            if (!closeWasCancelled) first.FailAttach();
            await Until(pane, () => pane.IsAttached && replacement.Requests.Count > 0);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            Assert.Equal("", pane.Status);
            Assert.Equal(1, first.Aborts);
            Assert.Equal(0, replacement.Aborts);
        }
        finally { window.Closing -= cancel; window.Close(); await Idle(); }
    });

    [Fact]
    public Task MismatchedDpiNeverAttachesForeignContent() => RunStaAsync(async () =>
    {
        var session = new FakeSession();
        var pane = new NativePreviewPane { Session = session, IsActive = true, ContentWidth = 300, ContentHeight = 200 };
        var window = Open(pane);
        try
        {
            await Idle();
            Assert.Empty(session.Requests);
            Assert.False(pane.IsAttached);
            Assert.Contains("Per-Monitor V2", pane.Status);
        }
        finally { window.Close(); await Idle(); }
    }, new nint(-2));

    private static Window Open(NativePreviewPane pane)
    {
        var window = new Window { Content = pane, Width = 340, Height = 260,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        return window;
    }

    private static async Task Idle()
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    private static async Task Until(NativePreviewPane pane, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Native preview did not settle. " + NativePreviewTestState.Describe(pane));
            await Task.Delay(10); await Idle();
        }
    }

    private static async Task RunStaAsync(Func<Task> action, nint dpiContext = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint previous = SetThreadDpiAwarenessContext(dpiContext == 0 ? new nint(-4) : dpiContext);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }) { IsBackground = true, Name = "Native preview view tests" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class FakeSession : IPreviewInteractionSession
    {
        private readonly TaskCompletionSource _attach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _sequence;
        public PreviewSurfaceIdentity Surface { get; } = new(Guid.NewGuid().ToString("N"), 1, Guid.NewGuid().ToString("N"));
        public int NativeProcessId { get; set; }
        public bool IsAvailable { get; private set; } = true;
        public bool IsAttached { get; private set; }
        public bool HoldAttach { get; init; }
        public bool ThrowWhenAborted { get; init; }
        public List<PreviewSurfaceRequest> Requests { get; } = [];
        public int Deactivations { get; private set; }
        public int Aborts { get; private set; }
        public Action? OnAbort { get; set; }
        public Func<PreviewSurfaceRequest, Task<PreviewSurfaceResponse>>? OnFocus { get; set; }
        public Func<PreviewSurfaceRequest, Task<PreviewSurfaceResponse>>? OnAttach { get; set; }
        public Func<PreviewSurfaceRequest, Task<PreviewSurfaceResponse>>? OnUpdate { get; set; }
        public event EventHandler<PreviewInteractionEvent>? Changed;
        public async Task<PreviewSurfaceResponse> UpdateAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default)
        {
            request = request with { Sequence = ++_sequence };
            Requests.Add(request);
            IsAttached = true;
            if (request.Action == PreviewSurfaceAction.Attach && OnAttach is not null) return await OnAttach(request);
            if (request.Action == PreviewSurfaceAction.Update && OnUpdate is not null) return await OnUpdate(request);
            if (HoldAttach && request.Action == PreviewSurfaceAction.Attach) await _attach.Task;
            if (request.Action is PreviewSurfaceAction.FocusFirst or PreviewSurfaceAction.FocusLast)
                return OnFocus is null ? new(request, IsAvailable, Focused: true) : await OnFocus(request);
            return new(request, IsAvailable);
        }
        public Task DeactivateAsync(CancellationToken cancellationToken = default)
        {
            Deactivations++; IsAttached = false; return Task.CompletedTask;
        }
        public void Abort(string reason)
        {
            OnAbort?.Invoke(); Aborts++; IsAvailable = false; IsAttached = false;
            if (ThrowWhenAborted) FailAttach(); else _attach.TrySetResult();
        }
        public void ReleaseAttach() => _attach.TrySetResult();
        public void FailAttach() => _attach.TrySetException(new OperationCanceledException("The earlier attach was aborted."));
        public void RaiseNavigation(PreviewSurfaceNavigation navigation, long sequence) => Changed?.Invoke(this, new(true, Navigation: navigation, NavigationSequence: sequence));
        public void RaiseFocus(PreviewSurfaceFocus focus) => Changed?.Invoke(this, new(true, Focus: focus));
    }

    private sealed class ActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint hwnd, string property);
}
