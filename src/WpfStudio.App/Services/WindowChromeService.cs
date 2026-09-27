using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WpfStudio.App.Controls;

namespace WpfStudio.App.Services;

/// <summary>
/// Window frame integration: matches the Windows 11 border, caption and dark-mode frame to the
/// studio palette, and lets the custom maximize button participate in Snap Layouts.
/// </summary>
public static class WindowChromeService
{
    private const int DwmUseImmersiveDarkMode = 20, DwmCornerPreference = 33, DwmBorderColor = 34, DwmCaptionColor = 35, DwmTextColor = 36;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies frame colours to a window that has a handle; later windows call <see cref="Track"/>.</summary>
    public static void ApplyFrame(Window window, bool light)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int dark = light ? 0 : 1; DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            int round = 2; DwmSetWindowAttribute(hwnd, DwmCornerPreference, ref round, sizeof(int));
            int border = ColorRef(light ? "#CBCED6" : "#2E3037"); DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
            int caption = ColorRef(light ? "#EDEEF1" : "#131417"); DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            int text = ColorRef(light ? "#1C1D21" : "#E4E5E9"); DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Applies the current theme once the window has a handle.</summary>
    public static void Track(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyFrame(window, ThemeService.IsLight);
        else window.SourceInitialized += (_, _) => ApplyFrame(window, ThemeService.IsLight);
    }

    private static int ColorRef(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        return color.R | (color.G << 8) | (color.B << 16);
    }
}

/// <summary>Caption buttons and maximized-bounds handling for a WindowChrome-based window.</summary>
public sealed class TitleBarController
{
    private const int WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, WmNcMouseLeave = 0x02A2, WmMouseMove = 0x0200;
    private const int HtMaxButton = 9;
    private readonly Window _window;
    private readonly ButtonBase _maximize;
    private readonly FrameworkElement _root;

    public TitleBarController(Window window, ButtonBase maximize, FrameworkElement root)
    {
        _window = window; _maximize = maximize; _root = root;
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => ToggleMaximize()));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => ToggleMaximize()));
        window.StateChanged += (_, _) => UpdateBounds();
        window.SourceInitialized += (_, _) =>
        {
            WindowChromeService.ApplyFrame(window, ThemeService.IsLight);
            // Added after WindowChrome's own hook, so this hook sees hit tests first.
            (PresentationSource.FromVisual(window) as HwndSource)?.AddHook(Hook);
        };
        UpdateBounds();
    }

    private void ToggleMaximize()
    {
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }

    private void UpdateBounds()
    {
        // A maximized WindowChrome window extends past the monitor by the resize frame.
        // Its side thickness is the resize frame plus padded border; the top has no native caption.
        var frame = SystemParameters.WindowNonClientFrameThickness;
        _root.Margin = _window.WindowState == WindowState.Maximized
            ? new Thickness(frame.Left, frame.Left, frame.Right, frame.Bottom)
            : new Thickness(0);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest:
                if (_maximize.IsVisible && _maximize.IsEnabled && OverMaximize(lParam))
                {
                    Ui.SetIsChromeHover(_maximize, true);
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }
                Clear();
                break;
            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                Ui.SetIsChromePressed(_maximize, true);
                handled = true;
                break;
            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                if (Ui.GetIsChromePressed(_maximize)) ToggleMaximize();
                Clear();
                handled = true;
                break;
            case WmNcMouseMove when wParam.ToInt32() != HtMaxButton:
            case WmNcMouseLeave:
            case WmMouseMove:
                Clear();
                break;
        }
        return IntPtr.Zero;
    }

    private void Clear()
    {
        if (Ui.GetIsChromeHover(_maximize)) Ui.SetIsChromeHover(_maximize, false);
        if (Ui.GetIsChromePressed(_maximize)) Ui.SetIsChromePressed(_maximize, false);
    }

    private bool OverMaximize(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        try
        {
            var local = _maximize.PointFromScreen(screen);
            return local.X >= 0 && local.Y >= 0 && local.X < _maximize.ActualWidth && local.Y < _maximize.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }
}
