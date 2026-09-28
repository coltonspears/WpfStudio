using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StreamJsonRpc;
using WpfStudio.Contracts;

namespace WpfStudio.Preview.Tests;

public sealed class NativePreviewHostTests
{
    private static PreviewRequest Source(long version = 1) => new("C:/preview/Native.xaml",
        "<probe:NativePreviewFixtureControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>",
        version, 400, 300, typeof(NativePreviewFixtureControl).Assembly.Location, AppContext.BaseDirectory);

    [Theory]
    [InlineData("Source")]
    [InlineData("FixtureWindow")]
    [InlineData("FixtureView")]
    [InlineData("FixturePage")]
    public async Task AttachDetachAndCaptureRetainTheSameSurfaceAndManagedView(string view)
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        PreviewRequest request = view == "Source" ? Source() : new("C:/preview/Compiled.xaml", "", 1, 400, 300,
            Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll"), AppContext.BaseDirectory,
            PreviewMode.Compiled, "WpfStudio.PreviewFixture." + view);
        var first = await host.Rpc.RenderAsync(request, default);
        AssertRendered(first);
        var identity = Assert.IsType<PreviewSurfaceIdentity>(first.Surface);
        Assert.Equal(host.SessionId, identity.SessionId);
        var attach = bridge.Request(identity, 1);
        Assert.True((await host.Rpc.UpdateSurfaceAsync(attach, default)).Success);
        nint hwnd = bridge.ChildFor(host.Process.Id);
        Assert.NotEqual(nint.Zero, hwnd);
        Assert.Equal(bridge.Handle, HostNativeTestMethods.GetParent(hwnd));
        var selected = view == "Source" ? Assert.Single(first.Nodes, n => n.Name == "Input") : Assert.Single(first.Nodes, n => n.Name == "Message");
        var edited = await host.Rpc.SetPropertyAsync(new(first.Version, selected.Id, "Text", "Retained interaction state"), default);
        Assert.True(edited.Success, edited.Error);
        Assert.Equal(identity, edited.Snapshot.Surface);

        var detach = attach with { Action = PreviewSurfaceAction.Detach, Sequence = 2 };
        var detached = await host.Rpc.UpdateSurfaceAsync(detach, default);
        Assert.True(detached.Success, detached.Status);
        Assert.Equal(detach, detached.Request);
        Assert.Equal(nint.Zero, HostNativeTestMethods.GetParent(hwnd));
        Assert.True(HostNativeTestMethods.IsWindow(hwnd));
        Assert.False(HostNativeTestMethods.IsWindowVisible(hwnd));
        var capture = await host.Rpc.CaptureAsync(new(first.Version), default);
        AssertRendered(capture);
        Assert.Equal(identity, capture.Surface);
        Assert.Contains(capture.Nodes, n => n.Id == selected.Id);
        Assert.Contains((await host.Rpc.InspectAsync(new(first.Version, selected.Id), default)).Properties,
            p => p.Name == "Text" && p.Value == "Retained interaction state");

        var reattached = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 3 }, default);
        Assert.True(reattached.Success, reattached.Status);
        Assert.Equal(hwnd, bridge.ChildFor(host.Process.Id));
        if (view == "FixtureWindow")
        {
            // The original Window is still an ancestor: resetting its Text binding
            // after reparenting must resolve the same Window.Title.
            var reset = await host.Rpc.SetPropertyAsync(new(first.Version, selected.Id, "Text", null, Reset: true), default);
            Assert.True(reset.Success, reset.Error);
            Assert.Contains(reset.Inspection.Properties, p => p.Name == "Text" && p.Value == "Compiled window ancestor");
        }
        Assert.True((await host.Rpc.UpdateSurfaceAsync(detach with { Sequence = 4 }, default)).Success);
    }

    [Fact]
    public async Task SurfaceCommandsRejectWrongIdentityOwnerLeaseAndSequenceAndClampPixelScroll()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await host.Rpc.RenderAsync(Source(), default);
        AssertRendered(snapshot);
        var surface = Assert.IsType<PreviewSurfaceIdentity>(snapshot.Surface);
        var attach = bridge.Request(surface, 1);
        var wrongToken = attach with { BridgeToken = Guid.NewGuid().ToString("N") };
        var rejected = await host.Rpc.UpdateSurfaceAsync(wrongToken, default);
        Assert.False(rejected.Success);
        Assert.Equal(wrongToken, rejected.Request);
        Assert.False((await host.Rpc.UpdateSurfaceAsync(attach with { ParentProcessId = Environment.ProcessId + 1 }, default)).Success);
        Assert.False((await host.Rpc.UpdateSurfaceAsync(attach with { Surface = surface with { SurfaceId = Guid.NewGuid().ToString("N") } }, default)).Success);
        Assert.True((await host.Rpc.UpdateSurfaceAsync(attach, default)).Success);
        nint hwnd = bridge.ChildFor(host.Process.Id);
        Assert.False((await host.Rpc.UpdateSurfaceAsync(attach, default)).Success);

        var update = attach with { Action = PreviewSurfaceAction.Update, PixelWidth = 100, PixelHeight = 80, OffsetX = 65000, OffsetY = 65000, Sequence = 2 };
        Assert.True((await host.Rpc.UpdateSurfaceAsync(update, default)).Success);
        var rectangle = bridge.ChildRectangle(hwnd);
        int expectedWidth = (int)Math.Ceiling(400 * HostNativeTestMethods.GetDpiForWindow(bridge.Handle) / 96d);
        int expectedHeight = (int)Math.Ceiling(300 * HostNativeTestMethods.GetDpiForWindow(bridge.Handle) / 96d);
        Assert.Equal(expectedWidth, rectangle.Right - rectangle.Left);
        Assert.Equal(expectedHeight, rectangle.Bottom - rectangle.Top);
        Assert.Equal(-(expectedWidth - 100), rectangle.Left);
        Assert.Equal(-(expectedHeight - 80), rectangle.Top);
        bridge.Revoke();
        Assert.False((await host.Rpc.UpdateSurfaceAsync(update with { Sequence = 3 }, default)).Success);
        Assert.Equal(bridge.Handle, HostNativeTestMethods.GetParent(hwnd));
        bridge.Restore();
        Assert.True((await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 4, Action = PreviewSurfaceAction.Detach }, default)).Success);

        var next = await host.Rpc.RenderAsync(Source(2), default);
        AssertRendered(next);
        Assert.NotEqual(surface, next.Surface);
        Assert.False((await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 5 }, default)).Success);
        var oldHeartbeat = await host.Rpc.HeartbeatSurfaceAsync(new(surface), default);
        Assert.False(oldHeartbeat.Available);
        Assert.Equal(surface, oldHeartbeat.Surface);
        Assert.True((await host.Rpc.HeartbeatSurfaceAsync(new(next.Surface!), default)).Available);
    }

    [Fact]
    public async Task DetachClosesAnOwnedPopupAndKeyboardSiteHandsOffSynchronouslyWithoutRecreatingView()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await host.Rpc.RenderAsync(Source(), default);
        AssertRendered(snapshot);
        var surface = Assert.IsType<PreviewSurfaceIdentity>(snapshot.Surface);
        var attach = bridge.Request(surface, 1);
        Assert.True((await host.Rpc.UpdateSurfaceAsync(attach, default)).Success);
        string rootId = snapshot.Nodes[0].Id;
        async Task Command(string value)
        {
            var result = await host.Rpc.SetPropertyAsync(new(snapshot.Version, rootId, "Tag", value), default);
            Assert.True(result.Success, result.Error);
        }
        await Command("next");
        var next = await host.Rpc.HeartbeatSurfaceAsync(new(surface), default);
        Assert.True(next.Available);
        Assert.Equal(PreviewSurfaceNavigation.None, next.Navigation);
        Assert.Equal(PreviewSurfaceNavigation.Next, Assert.Single(bridge.NavigationCalls).Direction);
        Assert.True((await Observation()).NavigationHandled);
        await Command("previous");
        var previous = await host.Rpc.HeartbeatSurfaceAsync(new(surface), default);
        Assert.Equal(next.NavigationSequence, previous.NavigationSequence);
        Assert.Equal(PreviewSurfaceNavigation.Previous, bridge.NavigationCalls.Last().Direction);
        Assert.True((await Observation()).NavigationHandled);
        await Command("popup");
        await Command("observe");
        var open = await Observation();
        Assert.True(open.PopupOpen);
        Assert.NotEqual(0, open.PopupHandle);
        HostNativeTestMethods.GetWindowThreadProcessId(new nint(open.PopupHandle), out uint popupOwner);
        Assert.Equal((uint)host.Process.Id, popupOwner);
        Assert.True((await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 2, Action = PreviewSurfaceAction.Detach }, default)).Success);
        await Command("observe-again");
        var closed = await Observation();
        Assert.False(closed.PopupOpen);
        Assert.Equal(open.Constructions, closed.Constructions);
        Assert.Equal(surface, (await host.Rpc.CaptureAsync(new(snapshot.Version), default)).Surface);

        async Task<NativePreviewObservation> Observation()
        {
            var inspected = await host.Rpc.InspectAsync(new(snapshot.Version, rootId), default);
            // Read-only CLR wrappers are exposed with their owner-qualified DP name.
            var value = Assert.Single(inspected.Properties, p => p.Name == nameof(NativePreviewFixtureControl) + ".Observation" &&
                p.OwnerType == typeof(NativePreviewFixtureControl).FullName);
            return JsonSerializer.Deserialize<NativePreviewObservation>(value.Value)!;
        }
    }

    [Fact]
    public async Task KeyboardSiteDoesNotTranslateDirectionalNavigationIntoTabExit()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await host.Rpc.RenderAsync(Source(), default);
        AssertRendered(snapshot);
        var attach = bridge.Request(snapshot.Surface!, 1);
        var attached = await host.Rpc.UpdateSurfaceAsync(attach, default);
        Assert.True(attached.Success, attached.Status);
        var before = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        foreach (string direction in new[] { "left", "right", "up", "down" })
        {
            // Exercise WPF's real site boundary in the preview process. This
            // does not synthesize physical keyboard input or claim that coverage.
            var result = await host.Rpc.SetPropertyAsync(new(snapshot.Version, snapshot.Nodes[0].Id, "Tag", direction), default);
            Assert.True(result.Success, result.Error);
            var value = Assert.Single(result.Inspection.Properties, p => p.Name == nameof(NativePreviewFixtureControl) + ".Observation" &&
                p.OwnerType == typeof(NativePreviewFixtureControl).FullName);
            var observation = JsonSerializer.Deserialize<NativePreviewObservation>(value.Value)!;
            Assert.False(observation.NavigationHandled);
            var after = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
            Assert.True(after.Available);
            Assert.Equal(before.NavigationSequence, after.NavigationSequence);
            Assert.Equal(before.Navigation, after.Navigation);
        }
        var detached = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 2, Action = PreviewSurfaceAction.Detach }, default);
        Assert.True(detached.Success, detached.Status);
    }

    [Fact]
    public async Task HostAuthenticatesParentPidAndTerminatesAfterEofEvenWhenTheDispatcherIsBlocked()
    {
        await using (var wrong = await RawNativePreviewHost.LaunchAsync(Environment.ProcessId + 1))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => wrong.Rpc.GetSessionAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
            await wrong.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        string directory = Directory.CreateTempSubdirectory("WpfStudio.NativeEof.").FullName;
        try
        {
            await using var host = await RawNativePreviewHost.StartAsync();
            string marker = Path.Combine(directory, "entered.txt");
            var pending = host.Rpc.RenderAsync(Source() with
            {
                Text = "<probe:SignaledHangingPreviewControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' MarkerPath='" +
                    SecurityElement.Escape(marker) + "'/>"
            }, default);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker)) await Task.Delay(20, wait.Token);
            host.Disconnect();
            await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            await Assert.ThrowsAnyAsync<Exception>(() => pending);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FocusingAViewWithoutTabStopsIsASuccessfulNoOp()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await host.Rpc.RenderAsync(new("C:/preview/NoFocus.xaml",
            "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>No interactive controls</TextBlock>", 1, 400, 300), default);
        AssertRendered(snapshot);
        var attach = bridge.Request(snapshot.Surface!, 1);
        Assert.True((await host.Rpc.UpdateSurfaceAsync(attach, default)).Success);
        var before = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        string firstGrant = bridge.GrantFocus();
        var firstResponse = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 2, Action = PreviewSurfaceAction.FocusFirst, FocusToken = firstGrant }, default);
        bridge.RevokeFocus(firstGrant);
        Assert.True(firstResponse.Success, firstResponse.Status);
        Assert.False(firstResponse.Focused);
        var first = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        Assert.True(first.Available);
        Assert.Equal(before.Navigation, first.Navigation);
        Assert.Equal(before.NavigationSequence, first.NavigationSequence);
        string lastGrant = bridge.GrantFocus();
        var lastResponse = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 3, Action = PreviewSurfaceAction.FocusLast, FocusToken = lastGrant }, default);
        bridge.RevokeFocus(lastGrant);
        Assert.True(lastResponse.Success, lastResponse.Status);
        Assert.False(lastResponse.Focused);
        var last = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        Assert.True(last.Available);
        Assert.Equal(before.Navigation, last.Navigation);
        Assert.Equal(before.NavigationSequence, last.NavigationSequence);
        Assert.False(host.Process.HasExited);
        var detached = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 4, Action = PreviewSurfaceAction.Detach }, default);
        Assert.True(detached.Success, detached.Status);
    }

    [Fact]
    public async Task SupersededFocusGrantsDoNotEnterTheKeyboardSinkOrPublishNavigation()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await host.Rpc.RenderAsync(Source() with
        {
            Text = "<probe:NativeFocusAttemptControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>"
        }, default);
        AssertRendered(snapshot);
        var attach = bridge.Request(snapshot.Surface!, 1);
        var attached = await host.Rpc.UpdateSurfaceAsync(attach, default);
        Assert.True(attached.Success, attached.Status);
        Assert.Null(attached.Focused);
        var before = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        string revoked = bridge.GrantFocus();
        bridge.RevokeFocus(revoked);
        string current = bridge.GrantFocus();
        long sequence = 1;
        foreach (string? token in new[] { null, "not-a-grant", Guid.NewGuid().ToString("N"), revoked })
        {
            var request = attach with { Sequence = ++sequence, Action = PreviewSurfaceAction.FocusFirst, FocusToken = token };
            var response = await host.Rpc.UpdateSurfaceAsync(request, default);
            Assert.Equal(request, response.Request);
            Assert.True(response.Success, response.Status);
            Assert.Null(response.Focused);
            Assert.Contains("superseded", response.Status!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await ReadAttempts());
            var after = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
            Assert.True(after.Available);
            Assert.Equal(before.NavigationSequence, after.NavigationSequence);
            Assert.Equal(before.Navigation, after.Navigation);
        }

        // A public IKeyboardInputSink fixture records the actual WPF TabInto
        // invocation without moving desktop focus. Its return value is controllable.
        var declined = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = ++sequence,
            Action = PreviewSurfaceAction.FocusFirst, FocusToken = current }, default);
        bridge.RevokeFocus(current);
        Assert.True(declined.Success, declined.Status);
        Assert.False(declined.Focused);
        int declinedAttempts = await ReadAttempts();
        Assert.True(declinedAttempts > 0, "A valid focus grant must reach the fixture's real keyboard sink.");
        var changed = await host.Rpc.SetPropertyAsync(new(snapshot.Version, snapshot.Nodes[0].Id, "AcceptEntry", "True"), default);
        Assert.True(changed.Success, changed.Error);
        string next = bridge.GrantFocus();
        var accepted = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = ++sequence,
            Action = PreviewSurfaceAction.FocusLast, FocusToken = next }, default);
        bridge.RevokeFocus(next);
        Assert.True(accepted.Success, accepted.Status);
        Assert.True(accepted.Focused);
        Assert.True(await ReadAttempts() > declinedAttempts, "The new valid grant must enter the sink again.");
        var heartbeat = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        Assert.Equal(before.NavigationSequence, heartbeat.NavigationSequence);
        var detached = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = ++sequence, Action = PreviewSurfaceAction.Detach }, default);
        Assert.True(detached.Success, detached.Status);

        async Task<int> ReadAttempts()
        {
            var inspection = await host.Rpc.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id), default);
            var property = Assert.Single(inspection.Properties, p => p.Name == nameof(NativeFocusAttemptControl) + ".EntryAttempts");
            return int.Parse(property.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public async Task EofDeadlineAlsoCoversAnApplicationCallbackDuringFinalOverrideRestoration()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.NativeDisposeEof.").FullName;
        try
        {
            await using var host = await RawNativePreviewHost.StartAsync();
            string marker = Path.Combine(directory, "reset-entered.txt");
            var snapshot = await host.Rpc.RenderAsync(Source() with
            {
                Text = "<probe:DisposalHangPreviewControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' MarkerPath='" +
                    SecurityElement.Escape(marker) + "'/>"
            }, default);
            AssertRendered(snapshot);
            var changed = await host.Rpc.SetPropertyAsync(new(snapshot.Version, snapshot.Nodes[0].Id, "ProbeValue", "override"), default);
            Assert.True(changed.Success, changed.Error);
            host.Disconnect();
            await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(File.Exists(marker), "The target reset callback must have run before the final process deadline.");
            Assert.Contains("TemporaryPropertyEdits", File.ReadAllText(marker), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertRendered(PreviewSnapshot snapshot) => Assert.True(snapshot.Success,
        snapshot.Status + "\n" + string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
}

public sealed record NativePreviewObservation(int Constructions, bool PopupOpen, long PopupHandle, bool? NavigationHandled = null);

public sealed class NativeFocusAttemptControl : FrameworkElement, IKeyboardInputSink
{
    private static readonly DependencyPropertyKey EntryAttemptsKey = DependencyProperty.RegisterReadOnly(nameof(EntryAttempts), typeof(int),
        typeof(NativeFocusAttemptControl), new PropertyMetadata(0));
    public static readonly DependencyProperty EntryAttemptsProperty = EntryAttemptsKey.DependencyProperty;
    public int EntryAttempts => (int)GetValue(EntryAttemptsProperty);
    public static readonly DependencyProperty AcceptEntryProperty = DependencyProperty.Register(nameof(AcceptEntry), typeof(bool),
        typeof(NativeFocusAttemptControl), new PropertyMetadata(false));
    public bool AcceptEntry { get => (bool)GetValue(AcceptEntryProperty); set => SetValue(AcceptEntryProperty, value); }
    public NativeFocusAttemptControl() => Focusable = true;
    public IKeyboardInputSite? KeyboardInputSite { get; set; }
    public bool TabInto(TraversalRequest request) { SetValue(EntryAttemptsKey, EntryAttempts + 1); return AcceptEntry; }
    public bool HasFocusWithin() => false;
    public bool TranslateAccelerator(ref MSG message, ModifierKeys modifiers) => false;
    public bool OnMnemonic(ref MSG message, ModifierKeys modifiers) => false;
    public bool TranslateChar(ref MSG message, ModifierKeys modifiers) => false;
    public IKeyboardInputSite RegisterKeyboardInputSink(IKeyboardInputSink sink) => throw new NotSupportedException();
}

public sealed class DisposalHangPreviewControl : FrameworkElement
{
    private bool _overridden;
    public static readonly DependencyProperty MarkerPathProperty = DependencyProperty.Register(nameof(MarkerPath), typeof(string),
        typeof(DisposalHangPreviewControl), new PropertyMetadata(null));
    public string MarkerPath { get => (string)GetValue(MarkerPathProperty); set => SetValue(MarkerPathProperty, value); }
    public static readonly DependencyProperty ProbeValueProperty = DependencyProperty.Register(nameof(ProbeValue), typeof(string),
        typeof(DisposalHangPreviewControl), new PropertyMetadata("original", (target, args) =>
        {
            var control = (DisposalHangPreviewControl)target;
            if (Equals(args.NewValue, "override")) control._overridden = true;
            else if (control._overridden && Equals(args.NewValue, "original"))
            {
                File.WriteAllText(control.MarkerPath, Environment.StackTrace);
                Thread.Sleep(Timeout.Infinite);
            }
        }));
    public string ProbeValue { get => (string)GetValue(ProbeValueProperty); set => SetValue(ProbeValueProperty, value); }
}

/// <summary>Loaded only inside the child process. Native popup visibility is suppressed for unattended tests.</summary>
public sealed class NativePreviewFixtureControl : StackPanel
{
    private static int _constructions;
    private readonly Popup _popup;
    private readonly TextBox _input;
    private static readonly DependencyPropertyKey ObservationKey = DependencyProperty.RegisterReadOnly(nameof(Observation), typeof(string),
        typeof(NativePreviewFixtureControl), new PropertyMetadata(""));
    public static readonly DependencyProperty ObservationProperty = ObservationKey.DependencyProperty;
    public string Observation => (string)GetValue(ObservationProperty);

    static NativePreviewFixtureControl() => TagProperty.OverrideMetadata(typeof(NativePreviewFixtureControl),
        new FrameworkPropertyMetadata(null, (target, args) => ((NativePreviewFixtureControl)target).Command(args.NewValue as string)));

    public NativePreviewFixtureControl()
    {
        _constructions++;
        _input = new TextBox { Name = "Input", Text = "Original", Height = 30 };
        Children.Add(_input);
        _popup = new Popup { PlacementTarget = _input, StaysOpen = true, AllowsTransparency = true,
            Child = new Border { Width = 100, Height = 50, Background = Brushes.Transparent, Opacity = 0, IsHitTestVisible = false } };
        _popup.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(_popup.Child) is HwndSource source) HostNativeTestMethods.ShowWindow(source.Handle, 0);
        };
    }

    private void Command(string? command)
    {
        bool? navigationHandled = null;
        if (command == "popup") _popup.IsOpen = true;
        else if (command is "focus-next" or "focus-previous" or "focus-next-nested")
        {
            _input.Focus();
            if (command == "focus-next-nested")
                _input.LostKeyboardFocus += HoldFocusLoss;
            if (PresentationSource.FromVisual(this) is HwndSource focusedSource)
                navigationHandled = ((IKeyboardInputSink)focusedSource).KeyboardInputSite?.OnNoMoreTabStops(new TraversalRequest(
                    command == "focus-previous" ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        else if (Enum.TryParse<FocusNavigationDirection>(command, ignoreCase: true, out var direction) &&
            PresentationSource.FromVisual(this) is HwndSource source)
            navigationHandled = ((IKeyboardInputSink)source).KeyboardInputSite?.OnNoMoreTabStops(new TraversalRequest(direction));
        nint popupHwnd = PresentationSource.FromVisual(_popup.Child) is HwndSource popupSource ? popupSource.Handle : 0;
        SetValue(ObservationKey, JsonSerializer.Serialize(new NativePreviewObservation(_constructions, _popup.IsOpen, popupHwnd.ToInt64(), navigationHandled)));
    }

    private static void HoldFocusLoss(object sender, KeyboardFocusChangedEventArgs args) => System.Windows.Threading.Dispatcher.PushFrame(new DispatcherFrame());
}

internal sealed class RawNativePreviewHost : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;
    private readonly Task<string> _stderr;
    private readonly Task<string> _stdout;
    private readonly string _shadowDirectory, _shadowToken;
    public Process Process { get; }
    public IPreviewRpc Rpc { get; }
    public string SessionId { get; }

    private RawNativePreviewHost(Process process, NamedPipeClientStream pipe, JsonRpc rpc, IPreviewRpc preview,
        string sessionId, Task<string> stderr, Task<string> stdout, string shadowDirectory, string shadowToken)
    { Process = process; _pipe = pipe; _rpc = rpc; Rpc = preview; SessionId = sessionId; _stderr = stderr; _stdout = stdout;
        _shadowDirectory = shadowDirectory; _shadowToken = shadowToken; }

    public static async Task<RawNativePreviewHost> StartAsync()
    {
        var host = await LaunchAsync(Environment.ProcessId);
        try
        {
            var session = await host.Rpc.GetSessionAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new PreviewHostSession(host.SessionId, host.Process.Id, Environment.ProcessId, PreviewNativeNavigation.ProtocolVersion, true), session);
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    public static async Task<RawNativePreviewHost> LaunchAsync(int parentPid)
    {
        string pipeName = "WpfStudio.NativeHost.Tests." + Guid.NewGuid().ToString("N");
        string session = Guid.NewGuid().ToString("N");
        string shadowDirectory = Directory.CreateTempSubdirectory("WpfStudio.Preview.").FullName;
        string shadowToken = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(shadowDirectory, "owner.token"), shadowToken);
        var start = new ProcessStartInfo(PreviewHostUnderTest.ExecutablePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--pipe", pipeName, "--session", session, "--parent-process", parentPid.ToString(),
            "--shadow-directory", shadowDirectory, "--shadow-token", shadowToken }) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        _ = process.SafeHandle;
        Task<string> stderr = process.StandardError.ReadToEndAsync(), stdout = process.StandardOutput.ReadToEndAsync();
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(10000);
            var rpc = new JsonRpc(pipe);
            var preview = rpc.Attach<IPreviewRpc>();
            rpc.StartListening();
            return new(process, pipe, rpc, preview, session, stderr, stdout, shadowDirectory, shadowToken);
        }
        catch
        {
            pipe.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync();
            process.Dispose();
            CleanupShadow(shadowDirectory, shadowToken);
            throw;
        }
    }

    public void Disconnect() { _rpc.Dispose(); _pipe.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        Disconnect();
        try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { if (!Process.HasExited) Process.Kill(entireProcessTree: false); await Process.WaitForExitAsync(); }
        await Task.WhenAll(_stderr, _stdout);
        Process.Dispose();
        CleanupShadow(_shadowDirectory, _shadowToken);
    }

    private static void CleanupShadow(string directory, string token)
    {
        string root = Path.GetFullPath(directory);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
        Assert.StartsWith("WpfStudio.Preview.", Path.GetFileName(root), StringComparison.Ordinal);
        if (!Directory.Exists(root)) return;
        Assert.Equal((FileAttributes)0, File.GetAttributes(root) & FileAttributes.ReparsePoint);
        Assert.Equal(token, File.ReadAllText(Path.Combine(root, "owner.token")));
        Delete(root);

        void Delete(string folder)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                string path = Path.GetFullPath(entry);
                Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0) Delete(path);
                    else Directory.Delete(path);
                }
                else
                {
                    if ((attributes & FileAttributes.ReadOnly) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
            }
            Directory.Delete(folder);
        }
    }
}

internal sealed class HostNativeTestBridge : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private readonly nint _owner;
    private readonly HostNativeTestMethods.SubclassProcedure _navigationProcedure;
    private readonly System.Collections.Concurrent.ConcurrentQueue<NativeBoundaryCall> _navigationCalls = new();
    private long _lastNavigationCall;
    private int _navigationReply = PreviewNativeNavigation.Moved;
    public nint Handle { get; }
    public string Token { get; } = Guid.NewGuid().ToString("N");
    public string NavigationToken { get; private set; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<NativeBoundaryCall> NavigationCalls => _navigationCalls.ToArray();
    public void ReplyToNavigation(int result) => Volatile.Write(ref _navigationReply, result);
    public void RenewNavigationToken() => _dispatcher.Invoke(() => NavigationToken = Guid.NewGuid().ToString("N"));

    public HostNativeTestBridge()
    {
        _navigationProcedure = NavigationProcedure;
        var ready = new TaskCompletionSource<(Dispatcher, nint, nint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                Assert.NotEqual(nint.Zero, HostNativeTestMethods.SetThreadDpiAwarenessContext(new nint(-4)));
                nint owner = HostNativeTestMethods.CreateWindowEx(0, "STATIC", "Preview native test owner", unchecked((int)0x80000000), -32000, -32000, 600, 400, 0, 0, 0, 0);
                nint child = HostNativeTestMethods.CreateWindowEx(0, "STATIC", "Preview native test bridge", 0x46000000, 0, 0, 200, 150, owner, 0, 0, 0);
                Assert.NotEqual(nint.Zero, owner);
                Assert.NotEqual(nint.Zero, child);
                Assert.True(HostNativeTestMethods.SetProp(child, "WpfStudio.PreviewBridge." + Token, new nint(1)));
                Assert.True(HostNativeTestMethods.SetWindowSubclass(child, _navigationProcedure, 1, 0));
                ready.TrySetResult((Dispatcher.CurrentDispatcher, owner, child));
                Dispatcher.Run();
                HostNativeTestMethods.RemoveWindowSubclass(child, _navigationProcedure, 1);
                HostNativeTestMethods.RemoveProp(child, "WpfStudio.PreviewBridge." + Token);
                HostNativeTestMethods.DestroyWindow(child);
                HostNativeTestMethods.DestroyWindow(owner);
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        (_dispatcher, _owner, Handle) = ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    public PreviewSurfaceRequest Request(PreviewSurfaceIdentity surface, long sequence) => new(surface, Token, Handle.ToInt64(),
        Environment.ProcessId, 200, 150, Sequence: sequence, NavigationToken: NavigationToken);

    private nint NavigationProcedure(nint hwnd, uint message, nuint childParameter, nint encoded, nuint id, nuint data)
    {
        if (message != HostNativeTestMethods.NavigationMessage)
            return HostNativeTestMethods.DefSubclassProc(hwnd, message, childParameter, encoded);
        long value = encoded.ToInt64(), sequence = value / 4, direction = value % 4;
        nint child = unchecked((nint)childParameter);
        if (hwnd != Handle || sequence <= _lastNavigationCall || direction is not (1 or 2) ||
            HostNativeTestMethods.GetParent(child) != hwnd ||
            HostNativeTestMethods.GetProp(child, PreviewNativeNavigation.PropertyPrefix + NavigationToken) != encoded)
            return PreviewNativeNavigation.Rejected;
        _lastNavigationCall = sequence;
        _navigationCalls.Enqueue(new(sequence, direction == 2 ? PreviewSurfaceNavigation.Previous : PreviewSurfaceNavigation.Next, child));
        return Volatile.Read(ref _navigationReply);
    }
    public void Revoke() => _dispatcher.Invoke(() => HostNativeTestMethods.RemoveProp(Handle, "WpfStudio.PreviewBridge." + Token));
    public void Restore() => _dispatcher.Invoke(() => HostNativeTestMethods.SetProp(Handle, "WpfStudio.PreviewBridge." + Token, new nint(1)));
    public string GrantFocus()
    {
        string token = Guid.NewGuid().ToString("N");
        _dispatcher.Invoke(() => Assert.True(HostNativeTestMethods.SetProp(Handle, "WpfStudio.PreviewFocus." + token, new nint(1))));
        return token;
    }
    public void RevokeFocus(string token) => _dispatcher.Invoke(() => HostNativeTestMethods.RemoveProp(Handle, "WpfStudio.PreviewFocus." + token));
    public nint ChildFor(int pid)
    {
        nint result = 0;
        HostNativeTestMethods.EnumChildWindows(Handle, (child, _) =>
        {
            HostNativeTestMethods.GetWindowThreadProcessId(child, out uint owner);
            if (owner != pid) return true;
            result = child;
            return false;
        }, 0);
        return result;
    }
    public HostNativeTestMethods.Rect ChildRectangle(nint hwnd)
    {
        Assert.True(HostNativeTestMethods.GetWindowRect(hwnd, out var rectangle));
        var origin = new HostNativeTestMethods.Point();
        Assert.True(HostNativeTestMethods.ClientToScreen(Handle, ref origin));
        rectangle.Left -= origin.X; rectangle.Right -= origin.X;
        rectangle.Top -= origin.Y; rectangle.Bottom -= origin.Y;
        return rectangle;
    }
    public void Dispose() { _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); Assert.True(_thread.Join(TimeSpan.FromSeconds(10))); GC.KeepAlive(_owner); }
}

internal sealed record NativeBoundaryCall(long Sequence, PreviewSurfaceNavigation Direction, nint Child);

internal static class HostNativeTestMethods
{
    internal delegate bool EnumWindow(nint hwnd, nint parameter);
    internal delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    internal static readonly uint NavigationMessage = RegisterWindowMessage(PreviewNativeNavigation.MessageName);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [DllImport("user32")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] internal static extern bool EnumChildWindows(nint hwnd, EnumWindow callback, nint parameter);
    [DllImport("user32")] internal static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("comctl32", SetLastError = true)] internal static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32")] internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id);
    [DllImport("comctl32")] internal static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
