using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WpfStudio.PreviewHost;

internal static class PreviewNativeMethods
{
    internal const long Child = 0x40000000, Popup = 0x80000000, Visible = 0x10000000;
    internal const long Caption = 0x00C00000, ThickFrame = 0x00040000, ClipChildren = 0x02000000, ClipSiblings = 0x04000000;
    internal const uint NoZOrder = 0x0004, NoActivate = 0x0010, FrameChanged = 0x0020;
    internal static readonly nint PerMonitorV2 = new(-4);

    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(nint hwnd, int index, int value);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    internal static long Style(nint hwnd) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, -16).ToInt64() : (uint)GetWindowLong32(hwnd, -16);

    internal static void SetStyle(nint hwnd, long value)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, -16, new nint(value)) : new nint(SetWindowLong32(hwnd, -16, unchecked((int)value)));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not update the preview window style.");
    }

    internal static void Reparent(nint child, nint parent)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = SetParent(child, parent);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not attach the preview window.");
    }

    internal static void Position(nint hwnd, int x, int y, int width, int height)
    {
        if (!SetWindowPos(hwnd, 0, x, y, width, height, NoZOrder | NoActivate | FrameChanged))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not position the preview window.");
    }

    internal static bool OwnedWindow(nint hwnd, int processId) => hwnd != 0 && IsWindow(hwnd) &&
        GetWindowThreadProcessId(hwnd, out uint actual) != 0 && actual == processId;
}
