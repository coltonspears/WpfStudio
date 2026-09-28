using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.NativeView.Tests;

public sealed partial class NativePreviewIntegrationTests
{
    [Fact]
    public Task RealHostBoundaryMovesToActualIdeTabStopsAndScrollsFocusedControls() => RunNavigationStaAsync(async () =>
    {
        await using var fixture = await NavigationFixture.OpenAsync();
        var next = await fixture.CommandAsync("next");
        Assert.True(next.Success, next.Error);
        Assert.Equal("True", next.Inspection.Properties.Single(property => property.Name.EndsWith(".Observation", StringComparison.Ordinal)).Value);
        Assert.Same(fixture.After, Keyboard.FocusedElement);
        Assert.False(((IKeyboardInputSink)fixture.Pane.Surface).HasFocusWithin());
        var previous = await fixture.CommandAsync("previous");
        Assert.True(previous.Success, previous.Error);
        Assert.Same(fixture.Before, Keyboard.FocusedElement);
        Assert.True(fixture.Session.IsAvailable);

        // The fixture invokes real WPF focus/site APIs inside the child. This
        // verifies interop routing and geometry, not physical keyboard fidelity.
        var focused = await fixture.CommandAsync("bottom");
        Assert.True(focused.Success, focused.Error);
        var horizontal = fixture.Pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
        var vertical = fixture.Pane.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
        await Until(fixture.Pane, () => horizontal.Value > 0 && vertical.Value > 0, () => fixture.Pane.Status);
        Assert.True(fixture.Session.IsAvailable);
        Assert.True(((IKeyboardInputSink)fixture.Pane.Surface).HasFocusWithin());
    });

    [Fact]
    public Task NestedFocusLossPumpingHeartbeatsCannotOutliveTheHandoffDeadline() => RunNavigationStaAsync(async () =>
    {
        await using var fixture = await NavigationFixture.OpenAsync();
        string? unavailable = null;
        fixture.Session.Changed += (_, args) => { if (!args.Available) Volatile.Write(ref unavailable, args.Status); };
        using var process = Process.GetProcessById(fixture.Session.NativeProcessId);
        var watch = Stopwatch.StartNew();
        Exception? failure = await Record.ExceptionAsync(() => fixture.CommandAsync("nested"));
        await Until(fixture.Pane, () => !fixture.Session.IsAvailable, () => fixture.Pane.Status);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(failure);
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.5, 12);
        Assert.Contains("keyboard handoff", Volatile.Read(ref unavailable) ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("three seconds", Volatile.Read(ref unavailable) ?? "", StringComparison.OrdinalIgnoreCase);
        bool dispatcherUsable = false;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => dispatcherUsable = true);
        Assert.True(dispatcherUsable);
        Assert.True(process.HasExited);
    });

    private sealed class NavigationFixture : IAsyncDisposable
    {
        public PreviewClient Client { get; }
        public NativePreviewPane Pane { get; }
        public Window Window { get; }
        public Button Before { get; } = new() { Content = "Before preview" };
        public Button After { get; } = new() { Content = "After preview" };
        public IPreviewInteractionSession Session { get; private set; } = null!;
        private PreviewSnapshot _snapshot = null!;

        private NavigationFixture()
        {
            Client = new PreviewClient(Environment.GetEnvironmentVariable("WPFSTUDIO_PREVIEW_HOST_UNDER_TEST"));
            Pane = new NativePreviewPane { ContentWidth = 900, ContentHeight = 700 };
            var content = new DockPanel();
            DockPanel.SetDock(Before, Dock.Top);
            DockPanel.SetDock(After, Dock.Bottom);
            content.Children.Add(Before); content.Children.Add(After); content.Children.Add(Pane);
            KeyboardNavigation.SetTabIndex(Before, 0);
            KeyboardNavigation.SetTabIndex(Pane, 1);
            KeyboardNavigation.SetTabIndex(After, 2);
            KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Continue);
            foreach (var bar in Pane.Children.OfType<ScrollBar>()) bar.IsTabStop = false;
            Window = new Window { Content = content, Width = 500, Height = 380,
                Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false, ShowActivated = false };
        }

        public static async Task<NavigationFixture> OpenAsync()
        {
            var fixture = new NavigationFixture();
            try
            {
                fixture.Window.Show();
                fixture._snapshot = await fixture.Client.RenderAsync(new(
                    Path.Combine(Path.GetTempPath(), "Native-navigation.xaml"),
                    "<probe:NativeViewNavigationFixture xmlns:probe='clr-namespace:WpfStudio.NativeView.Tests;assembly=WpfStudio.NativeView.Tests'/>",
                    1, 900, 700, typeof(NativeViewNavigationFixture).Assembly.Location, AppContext.BaseDirectory,
                    ApplicationResourcePath: null));
                Assert.True(fixture._snapshot.Success, fixture._snapshot.Status);
                fixture.Session = Assert.IsAssignableFrom<IPreviewInteractionSession>(fixture.Client.CreateInteractionSession(fixture._snapshot.Surface!));
                fixture.Pane.Session = fixture.Session;
                fixture.Pane.IsActive = true;
                await Until(fixture.Pane, () => fixture.Pane.IsAttached, () => fixture.Pane.Status);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public Task<PreviewEditResult> CommandAsync(string command) => Client.SetPropertyAsync(new(
            _snapshot.Version, _snapshot.Nodes[0].Id, "Tag", command));

        public async ValueTask DisposeAsync()
        {
            Pane.IsActive = false;
            if (Session is not null && Session.IsAvailable) await Session.DeactivateAsync();
            await Client.DisposeAsync();
            Window.Close();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
    }

    private static async Task RunNavigationStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint previous = SetThreadDpiAwarenessContext(new nint(-4));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }) { IsBackground = true, Name = "Native preview navigation integration" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}

/// <summary>Loaded only inside the owned preview process by these interop tests.</summary>
public sealed class NativeViewNavigationFixture : Canvas
{
    private readonly TextBox _input = new() { Width = 150, Height = 30 };
    private readonly TextBox _bottom = new() { Width = 70, Height = 30 };
    private static readonly DependencyPropertyKey ObservationKey = DependencyProperty.RegisterReadOnly(nameof(Observation), typeof(string),
        typeof(NativeViewNavigationFixture), new PropertyMetadata(""));
    public static readonly DependencyProperty ObservationProperty = ObservationKey.DependencyProperty;
    public string Observation => (string)GetValue(ObservationProperty);

    static NativeViewNavigationFixture() => TagProperty.OverrideMetadata(typeof(NativeViewNavigationFixture),
        new FrameworkPropertyMetadata(null, (target, args) => ((NativeViewNavigationFixture)target).Command(args.NewValue as string)));

    public NativeViewNavigationFixture()
    {
        SetLeft(_bottom, 810); SetTop(_bottom, 630);
        Children.Add(_input); Children.Add(_bottom);
    }

    private void Command(string? command)
    {
        if (command == "bottom") { _bottom.Focus(); return; }
        if (command is not ("next" or "previous" or "nested")) return;
        _input.Focus();
        if (command == "nested") _input.LostKeyboardFocus += (_, _) => System.Windows.Threading.Dispatcher.PushFrame(new DispatcherFrame());
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        bool? moved = ((IKeyboardInputSink)source).KeyboardInputSite?.OnNoMoreTabStops(new TraversalRequest(
            command == "previous" ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        SetValue(ObservationKey, moved?.ToString() ?? "Unavailable");
    }
}
