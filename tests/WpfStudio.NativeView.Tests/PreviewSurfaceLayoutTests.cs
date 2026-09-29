using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.NativeView.Tests;

public sealed partial class NativePreviewPaneTests
{
    // Exercise the real control's input adapters on an owned STA. These are
    // coordinate/capture tests, not a claim of physical mouse/keyboard fidelity.
    [Theory]
    [InlineData(.1, XamlLayoutHandle.Move)]
    [InlineData(.65, XamlLayoutHandle.Move)]
    [InlineData(2, XamlLayoutHandle.Move)]
    [InlineData(.1, XamlLayoutHandle.BottomRight)]
    [InlineData(.65, XamlLayoutHandle.BottomRight)]
    [InlineData(2, XamlLayoutHandle.BottomRight)]
    public Task PreviewLayoutPointerPacketsUseInitialRootDipsAndCommitOnlyOnce(double zoom, XamlLayoutHandle handle) => RunStaAsync(async () =>
    {
        var (surface, command) = LayoutSurface(zoom);
        var window = OpenLayoutSurface(surface);
        try
        {
            await Idle();
            var bounds = surface.LayoutEditing!.Bounds!;
            Point start = handle == XamlLayoutHandle.Move
                ? new((bounds.X + bounds.Width / 2) * zoom, (bounds.Y + bounds.Height / 2) * zoom)
                : new((bounds.X + bounds.Width) * zoom, (bounds.Y + bounds.Height) * zoom);
            Assert.Equal(handle, surface.LayoutHandleAt(start));
            Assert.True(surface.HandlePointerDown(start, ModifierKeys.None));
            Assert.True(surface.IsMouseCaptured);
            Assert.Empty(command.Packets);
            surface.HandlePointerMove(start + new Vector(1, 1), ModifierKeys.None);
            Assert.Empty(command.Packets);
            surface.HandlePointerMove(start + new Vector(24, 16), ModifierKeys.Alt);
            surface.HandlePointerUp(start + new Vector(24, 16), ModifierKeys.Alt);
            Assert.False(surface.IsMouseCaptured);
            Assert.Equal(new[] { PreviewLayoutGesturePhase.Begin, PreviewLayoutGesturePhase.Update, PreviewLayoutGesturePhase.Commit }, command.Packets.Select(packet => packet.Phase));
            var commit = command.Packets[^1];
            Assert.Equal(handle, commit.Handle);
            Assert.Equal(24 / zoom, commit.DeltaX, 8); Assert.Equal(16 / zoom, commit.DeltaY, 8);
            Assert.Equal(zoom, commit.Zoom); Assert.True(commit.BypassSnap);
            Assert.False(surface.HandlePointerUp(start, ModifierKeys.None));
            Assert.Equal(3, command.Packets.Count);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task PreviewLayoutScrolledCoordinatesAreNotOffsetTwice() => RunStaAsync(async () =>
    {
        var (surface, command) = LayoutSurface(2);
        var scroll = new ScrollViewer { Content = surface, Padding = new Thickness(16), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = OpenLayoutSurface(scroll);
        try
        {
            await Idle();
            scroll.ScrollToHorizontalOffset(400); scroll.ScrollToVerticalOffset(360);
            await Idle();
            Assert.True(scroll.HorizontalOffset > 0); Assert.True(scroll.VerticalOffset > 0);
            var start = new Point(640, 480);
            Point viewport = surface.TranslatePoint(start, scroll);
            Point local = scroll.TranslatePoint(viewport, surface);
            Assert.Equal(start, local);
            surface.HandlePointerDown(local, ModifierKeys.None);
            surface.HandlePointerMove(local + new Vector(30, -20), ModifierKeys.None);
            surface.HandlePointerUp(local + new Vector(30, -20), ModifierKeys.None);
            var committed = Assert.Single(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Commit);
            Assert.Equal(15, committed.DeltaX); Assert.Equal(-10, committed.DeltaY);
        }
        finally { window.Close(); await Idle(); }
    });

    [Theory]
    [InlineData("image")]
    [InlineData("selection")]
    [InlineData("context")]
    [InlineData("zoom")]
    [InlineData("disable")]
    [InlineData("capture")]
    [InlineData("unload")]
    public Task PreviewLayoutChangingItsObservationCancelsCapturedDraft(string change) => RunStaAsync(async () =>
    {
        var (surface, command) = LayoutSurface();
        var window = OpenLayoutSurface(surface);
        try
        {
            await Idle();
            surface.HandlePointerDown(new(320, 240), ModifierKeys.None);
            surface.HandlePointerMove(new(340, 260), ModifierKeys.None);
            Assert.Contains(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Update);
            switch (change)
            {
                case "image": surface.ImageBytes = SurfacePng(); break;
                case "selection": surface.Selection = new(0, 0, 30, 30); break;
                case "context": surface.LayoutEditing = surface.LayoutEditing! with { Token = "another observation" }; break;
                case "zoom": surface.Scale = 2; break;
                case "disable": surface.IsLayoutEditingEnabled = false; break;
                case "capture": surface.ReleaseMouseCapture(); break;
                case "unload": window.Close(); break;
            }
            await Idle();
            Assert.Single(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Cancel);
            Assert.DoesNotContain(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Commit);
            Assert.False(surface.IsMouseCaptured);
            Assert.False(surface.HandlePointerUp(new(340, 260), ModifierKeys.None));
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task PreviewLayoutKeyboardAccumulatesNudgesAndResizeWithoutCapturingTab() => RunStaAsync(async () =>
    {
        var (surface, command) = LayoutSurface();
        var other = new TextBox();
        var grid = new DockPanel(); DockPanel.SetDock(other, Dock.Top); grid.Children.Add(other); grid.Children.Add(surface);
        var window = OpenLayoutSurface(grid);
        try
        {
            await Idle(); Keyboard.Focus(surface);
            Assert.True(surface.IsKeyboardFocused);
            Assert.True(surface.HandleLayoutKey(Key.Right, ModifierKeys.None));
            Assert.True(surface.HandleLayoutKey(Key.Down, ModifierKeys.Shift));
            Assert.DoesNotContain(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Commit);
            Assert.True(surface.HandleLayoutKey(Key.Enter, ModifierKeys.None));
            var move = command.Packets[^1];
            Assert.Equal(PreviewLayoutGesturePhase.Commit, move.Phase);
            Assert.Equal(XamlLayoutHandle.Move, move.Handle);
            Assert.Equal(1, move.DeltaX); Assert.Equal(10, move.DeltaY); Assert.True(move.BypassSnap);
            command.Packets.Clear();
            surface.HandleLayoutKey(Key.Right, ModifierKeys.Control);
            surface.HandleLayoutKey(Key.Up, ModifierKeys.Control | ModifierKeys.Shift);
            surface.HandleLayoutKey(Key.Enter, ModifierKeys.None);
            var resize = command.Packets[^1];
            Assert.Equal(XamlLayoutHandle.BottomRight, resize.Handle);
            Assert.Equal(1, resize.DeltaX); Assert.Equal(-10, resize.DeltaY);
            command.Packets.Clear();
            surface.HandleLayoutKey(Key.Right, ModifierKeys.None);
            Assert.False(surface.HandleLayoutKey(Key.Tab, ModifierKeys.None));
            Keyboard.Focus(other);
            Assert.Equal(PreviewLayoutGesturePhase.Cancel, command.Packets[^1].Phase);
            int count = command.Packets.Count;
            Assert.False(surface.HandleLayoutKey(Key.Right, ModifierKeys.None));
            Assert.Equal(count, command.Packets.Count);
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task PreviewLayoutDraftChangesDoNotCancelAndEscapeDoesNotReview() => RunStaAsync(async () =>
    {
        var (surface, command) = LayoutSurface();
        var window = OpenLayoutSurface(surface);
        try
        {
            await Idle(); Keyboard.Focus(surface);
            surface.HandleLayoutKey(Key.Right, ModifierKeys.None);
            surface.LayoutDraft = new(true, new(201, 160, 240, 160), []);
            Assert.DoesNotContain(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Cancel);
            Assert.True(surface.HandleLayoutKey(Key.Escape, ModifierKeys.None));
            Assert.Equal(PreviewLayoutGesturePhase.Cancel, command.Packets[^1].Phase);
            Assert.DoesNotContain(command.Packets, packet => packet.Phase == PreviewLayoutGesturePhase.Commit);
            Assert.False(surface.HandleLayoutKey(Key.Enter, ModifierKeys.None));
        }
        finally { window.Close(); await Idle(); }
    });

    [Fact]
    public Task PreviewLayoutHandlesAndGuidesRemainVisibleWithLayoutOverlays() => RunStaAsync(async () =>
    {
        var (surface, _) = LayoutSurface();
        surface.LayoutOverlays = [new LayoutOverlay("render", [new(200, 160), new(440, 160), new(440, 320), new(200, 320)], "render")];
        surface.IsLayoutEditingEnabled = false;
        var window = OpenLayoutSurface(surface);
        try
        {
            await Idle(); var plain = SurfacePixels(surface);
            surface.IsLayoutEditingEnabled = true;
            await Idle(); var handles = SurfacePixels(surface);
            Assert.False(plain.SequenceEqual(handles));
            surface.LayoutDraft = new(true, new(220, 180, 240, 160), [new(true, 220, 40, 460), new(false, 180, 100, 640), new(true, double.NaN, 0, 20)]);
            await Idle(); Assert.False(handles.SequenceEqual(SurfacePixels(surface)));
            var peer = UIElementAutomationPeer.CreatePeerForElement(surface)!;
            Assert.Equal("Preview canvas", peer.GetName()); Assert.Contains("Enter reviews", peer.GetHelpText());
            surface.LayoutEditing = surface.LayoutEditing! with { Bounds = new(double.NaN, 0, 100, 100) };
            Assert.Null(surface.LayoutHandleAt(new(20, 20)));
            await Idle(); _ = SurfacePixels(surface);
        }
        finally { window.Close(); await Idle(); }
    });

    private static (PreviewSurface Surface, LayoutPacketCommand Command) LayoutSurface(double scale = 1)
    {
        var command = new LayoutPacketCommand();
        var bounds = new PreviewBounds(200, 160, 240, 160);
        return (new PreviewSurface { ImageBytes = SurfacePng(), Scale = scale, Selection = bounds,
            IsLayoutEditingEnabled = true, LayoutEditing = new(true, 1, "selected", Token: "observation", Bounds: bounds),
            LayoutGestureCommand = command, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }, command);
    }
    private static Window OpenLayoutSurface(FrameworkElement content)
    {
        var window = new Window { Content = content, Width = 360, Height = 300, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false };
        // CaptureMouse synchronously synchronizes the actual desktop pointer.
        // These adapter tests supply their own coordinates; exclude that unrelated
        // routed move without replacing the real capture or lifecycle behavior.
        window.PreviewMouseMove += (_, args) => args.Handled = true;
        window.Show(); return window;
    }
    private static byte[] SurfacePng()
    {
        var pixels = Enumerable.Repeat((byte)255, 800 * 600 * 4).ToArray();
        var bitmap = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Pbgra32, null, pixels, 800 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static byte[] SurfacePixels(PreviewSurface surface)
    {
        surface.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(surface.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(surface.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var bytes = new byte[width * height * 4]; bitmap.CopyPixels(bytes, width * 4, 0); return bytes;
    }
    private sealed class LayoutPacketCommand : ICommand
    {
        public List<PreviewLayoutGesture> Packets { get; } = [];
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is PreviewLayoutGesture;
        public void Execute(object? parameter) => Packets.Add((PreviewLayoutGesture)parameter!);
    }
}
