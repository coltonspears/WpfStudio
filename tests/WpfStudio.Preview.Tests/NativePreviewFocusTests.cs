using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfStudio.Contracts;
using WpfStudio.PreviewHost;

namespace WpfStudio.Preview.Tests;

// Native keyboard focus is shared input state. Keep these observations out of
// parallel preview scenarios that can acquire or release another window's focus.
[Collection("WPF preview")]
public sealed class NativePreviewFocusTests
{
    [Fact]
    public async Task FocusBoundsUseRootDipsTrackGeometryWithoutChangingFocusEpochAndExcludePopupSources()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await host.Rpc.RenderAsync(new("C:/preview/Focus.xaml",
            "<probe:NativeFocusGeometryFixture xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>",
            1, 800, 600, typeof(NativeFocusGeometryFixture).Assembly.Location, AppContext.BaseDirectory), default);
        Assert.True(snapshot.Success, snapshot.Status + "\n" + string.Join("\n", snapshot.Diagnostics.Select(item => item.Message)));
        var attach = bridge.Request(snapshot.Surface!, 1);
        var response = await host.Rpc.UpdateSurfaceAsync(attach, default);
        Assert.True(response.Success, response.Status);
        NativeFocusExpectation? lastExpected = null;
        var expected = await Command("focus");
        Assert.True(expected.Focused, "The fixture must acquire actual WPF/native focus before testing observation.");
        var first = await WaitForFocus(sample => sample.Bounds is not null && sample.NativeFocusHandle == expected.NativeHandle);
        AssertBounds(expected.Bounds!, first.Bounds!);
        Assert.Equal(attach.BridgeToken, first.BridgeToken);
        Assert.Equal(attach.Sequence, first.AttachmentSequence);
        Assert.Equal(HostNativeTestMethods.GetDpiForWindow(new nint(response.NativeHandle)), first.Dpi);

        await Command("move");
        expected = await Command("observe-moved");
        var moved = await WaitForFocus(sample => sample.Sequence > first.Sequence && sample.Bounds is { } box &&
            Math.Abs(box.X - expected.Bounds!.X) < .01);
        AssertBounds(expected.Bounds!, moved.Bounds!);
        Assert.Equal(first.FocusEpoch, moved.FocusEpoch);
        Assert.Equal(first.FocusChangedAtTick, moved.FocusChangedAtTick);

        var scrolled = await host.Rpc.UpdateSurfaceAsync(attach with { Action = PreviewSurfaceAction.Update,
            Sequence = 2, OffsetX = 180, OffsetY = 140 }, default);
        Assert.True(scrolled.Success, scrolled.Status);
        var afterScroll = await WaitForFocus(sample => sample.Bounds is not null);
        AssertBounds(moved.Bounds!, afterScroll.Bounds!); // No viewport-offset subtraction or pixel scaling.

        await Command("popup");
        var popup = await Command("focus-popup");
        Assert.True(popup.Focused, "The popup fixture must acquire focus to prove cross-source exclusion.");
        var outside = await WaitForFocus(sample => sample.FocusEpoch > moved.FocusEpoch && sample.Bounds is null);
        Assert.Contains("presentation root", outside.Status!, StringComparison.OrdinalIgnoreCase);
        var detached = await host.Rpc.UpdateSurfaceAsync(attach with { Action = PreviewSurfaceAction.Detach, Sequence = 3 }, default);
        Assert.True(detached.Success, detached.Status);
        Assert.Null((await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default)).Focus);

        async Task<NativeFocusExpectation> Command(string command)
        {
            var result = await host.Rpc.SetPropertyAsync(new(snapshot.Version, snapshot.Nodes[0].Id, "Tag", command), default);
            Assert.True(result.Success, result.Error);
            var property = Assert.Single(result.Inspection.Properties, item => item.Name == nameof(NativeFocusGeometryFixture) + ".Observation");
            return lastExpected = JsonSerializer.Deserialize<NativeFocusExpectation>(property.Value)!;
        }

        async Task<PreviewSurfaceFocus> WaitForFocus(Func<PreviewSurfaceFocus, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            PreviewSurfaceHeartbeat? lastHeartbeat = null;
            int observations = 0;
            try
            {
                while (true)
                {
                    lastHeartbeat = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), timeout.Token);
                    observations++;
                    Assert.True(lastHeartbeat.Available, lastHeartbeat.Status);
                    if (lastHeartbeat.Focus is { } sample && predicate(sample)) return sample;
                    await Task.Delay(25, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"No matching focus observation after {observations} heartbeats. " +
                    $"Attached native handle: {response.NativeHandle}. " +
                    $"Last fixture expectation: {JsonSerializer.Serialize(lastExpected)}. " +
                    $"Last heartbeat: {JsonSerializer.Serialize(lastHeartbeat)}.");
            }
        }
    }

    private static void AssertBounds(PreviewBounds expected, PreviewBounds actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }
}

public sealed record NativeFocusExpectation(bool Focused, long NativeHandle, PreviewBounds? Bounds);

[Collection("WPF preview")]
public sealed class NativePreviewFocusSafetyTests(PreviewFixture fixture)
{
    [Fact]
    public void AnimatedFrameworkTransformIsWithheldWithoutCallingItsCustomAnimationEvaluator() => fixture.OnDispatcher(() =>
    {
        var transform = new TranslateTransform();
        var clock = new CountingFocusAnimation { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever }.CreateClock();
        transform.ApplyAnimationClock(TranslateTransform.XProperty, clock);
        var element = new Border { Width = 100, Height = 40,
            RenderTransform = new TransformGroup { Children = new TransformCollection { transform } } };
        var root = new Canvas { Width = 400, Height = 300 };
        root.Children.Add(element);
        root.Measure(new Size(400, 300));
        root.Arrange(new Rect(0, 0, 400, 300));
        root.UpdateLayout();
        Assert.True(transform.HasAnimatedProperties);
        int before = CountingFocusAnimation.Evaluations;
        var capture = typeof(PreviewEngine).Assembly.GetType("WpfStudio.PreviewHost.NativeFocusGeometry", throwOnError: true)!
            .GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments = [element, root, null];
        Assert.Null(capture.Invoke(null, arguments));
        Assert.Contains("passively", Assert.IsType<string>(arguments[2]), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, CountingFocusAnimation.Evaluations);
        transform.ApplyAnimationClock(TranslateTransform.XProperty, null);
        return true;
    });

    private sealed class CountingFocusAnimation : DoubleAnimationBase
    {
        internal static int Evaluations;
        protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
        { Evaluations++; return defaultOriginValue; }
        protected override Freezable CreateInstanceCore() => new CountingFocusAnimation();
    }
}

/// <summary>Owned offscreen controls; only their real WPF Focus APIs are invoked, never desktop input injection.</summary>
public sealed class NativeFocusGeometryFixture : Canvas
{
    private readonly TextBox _input = new() { Name = "FarInput", Width = 120, Height = 32, Text = "Focusable" };
    private readonly TextBox _popupInput = new() { Width = 100, Height = 30, Text = "Popup", Opacity = 0 };
    private readonly Popup _popup;
    private static readonly DependencyPropertyKey ObservationKey = DependencyProperty.RegisterReadOnly(nameof(Observation), typeof(string),
        typeof(NativeFocusGeometryFixture), new PropertyMetadata(""));
    public static readonly DependencyProperty ObservationProperty = ObservationKey.DependencyProperty;
    public string Observation => (string)GetValue(ObservationProperty);

    static NativeFocusGeometryFixture() => TagProperty.OverrideMetadata(typeof(NativeFocusGeometryFixture),
        new FrameworkPropertyMetadata(null, (owner, args) => ((NativeFocusGeometryFixture)owner).Command(args.NewValue as string)));

    public NativeFocusGeometryFixture()
    {
        SetLeft(_input, 450); SetTop(_input, 350);
        _input.RenderTransform = new RotateTransform(15);
        Children.Add(_input);
        _popup = new Popup { PlacementTarget = _input, Placement = PlacementMode.Absolute,
            HorizontalOffset = -32000, VerticalOffset = -32000, StaysOpen = true, AllowsTransparency = true, Child = _popupInput };
    }

    private void Command(string? command)
    {
        TextBox focused = _input;
        if (command == "focus") _input.Focus();
        else if (command == "move") _input.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection { new RotateTransform(15), new TranslateTransform(40, 15) }
        };
        else if (command == "popup")
        {
            _popup.IsOpen = true;
        }
        else if (command == "focus-popup")
        {
            _popupInput.Focus();
            focused = _popupInput;
        }
        PreviewBounds? expected = null;
        if (PresentationSource.FromVisual(focused)?.RootVisual is Visual root)
        {
            var box = focused.TransformToAncestor(root).TransformBounds(new Rect(new Point(), focused.RenderSize));
            expected = new(box.X, box.Y, box.Width, box.Height);
        }
        SetValue(ObservationKey, JsonSerializer.Serialize(new NativeFocusExpectation(
            ReferenceEquals(Keyboard.FocusedElement, focused), GetFocus().ToInt64(), expected)));
    }

    [DllImport("user32")] private static extern nint GetFocus();
}
