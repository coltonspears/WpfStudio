using System.Runtime.InteropServices;
using System.Windows;
using WpfStudio.App.Features.Designer;

namespace WpfStudio.NativeView.Tests;

internal static class NativePreviewTestState
{
    public static string Describe(NativePreviewPane pane)
    {
        nint bridge = pane.Surface.BridgeHandle;
        var application = Application.Current;
        string appState = application is null ? "none" : application.Dispatcher.HasShutdownStarted ? "dispatcher shutting down" : "present";
        return $"Pane loaded={pane.IsLoaded}, visible={pane.IsVisible}/{pane.Visibility}, active={pane.IsActive}, suppressed={pane.IsSuppressed}; "
            + $"surface loaded={pane.Surface.IsLoaded}, visible={pane.Surface.IsVisible}/{pane.Surface.Visibility}, "
            + $"bridge=0x{bridge.ToInt64():X}, valid={bridge != 0 && IsWindow(bridge)}, "
            + $"DPI={(bridge == 0 ? 0 : GetDpiForWindow(bridge))}, "
            + $"PMv2={bridge != 0 && AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(bridge), new nint(-4))}, "
            + $"thread PMv2={AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new nint(-4))}; "
            + $"session available={pane.Session?.IsAvailable}, attached={pane.Session?.IsAttached}; "
            + $"Application={appState}; status={pane.Status}";
    }

    [DllImport("user32")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32")] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
