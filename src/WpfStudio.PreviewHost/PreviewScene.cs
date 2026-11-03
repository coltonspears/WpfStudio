using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfStudio.PreviewHost;

/// <summary>The objects of the current render and the hidden window that hosts them. Dispatcher-owned; every part of the engine reads this one instance.</summary>
internal sealed class PreviewScene
{
    private Window? _previewWindow;
    public long Version { get; set; }
    public int Width { get; set; } = 960;
    public int Height { get; set; } = 640;
    public FrameworkElement? Root { get; set; }
    public PreviewDocument? Document { get; set; }
    public PreviewScenarioActivation? ScenarioActivation { get; set; }
    public HwndSource? Surface { get; private set; }
    public FrameworkElement? Viewport { get; private set; }
    public bool IsClosed { get; set; }

    /// <summary>Uses the root's explicit Width/Height (including applied design-time sizes) for the artboard.</summary>
    public void ApplyRootSize()
    {
        static int? Dimension(double value) => double.IsFinite(value) && value > 0 ? (int)Math.Ceiling(Math.Clamp(value, 32, 4096)) : null;
        if (Dimension(Root!.Width) is { } width) Width = width;
        if (Dimension(Root.Height) is { } height) Height = height;
    }

    public void Host()
    {
        if (Root is Window window)
        {
            _previewWindow = window;
            Viewport = window;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000;
            window.Width = Width; window.Height = Height;
            window.WindowState = WindowState.Normal;
            window.SizeToContent = SizeToContent.Manual;
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false;
            window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
            window.Show();
            // Keep the managed Window and its ancestor/resource scopes;
            // hide only its native surface after offscreen initialization.
            var handle = new WindowInteropHelper(window).Handle;
            PreviewNativeMethods.ShowWindow(handle, 0);
            Surface = HwndSource.FromHwnd(handle);
        }
        else
        {
            UIElement content = Root is Page page ? new Frame { Content = page, NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden } : Root!;
            Viewport = new Border { Width = Width, Height = Height, Background = Brushes.White, Child = content, ClipToBounds = true };
            // A real, hidden presentation source activates bindings, templates,
            // resource inheritance and Loaded without showing a second window.
            Surface = new HwndSource(new HwndSourceParameters("WpfStudio isolated preview")
            {
                Width = Width, Height = Height, WindowStyle = unchecked((int)0x80000000),
                PositionX = -32000, PositionY = -32000
            }) { RootVisual = Viewport };
        }
    }

    public async Task SettleLayoutAsync(Dispatcher dispatcher)
    {
        if (Viewport is null) return;
        Viewport.Measure(new Size(Width, Height));
        Viewport.Arrange(new Rect(0, 0, Width, Height));
        Viewport.UpdateLayout();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Viewport.UpdateLayout();
    }

    public byte[] CapturePng()
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Viewport);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Close()
    {
        if (_previewWindow is not null)
        {
            _previewWindow.Close();
            _previewWindow = null;
        }
        if (Surface is not null)
        {
            if (!Surface.IsDisposed) { Surface.RootVisual = null; Surface.Dispose(); }
            Surface = null;
        }
        if (Viewport is Border border) border.Child = null;
        Viewport = null;
        Root = null;
        Document = null;
        ScenarioActivation = null;
    }
}
