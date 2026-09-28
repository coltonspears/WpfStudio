using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InteractivePreviewProbe;

internal static class Native
{
    public const int Child = 0x40000000, Visible = 0x10000000, ClipChildren = 0x02000000, ClipSiblings = 0x04000000;
    public const uint NoZOrder = 0x0004, NoActivate = 0x0010, FrameChanged = 0x0020;
    public const uint MouseMove = 0x0200, MouseDown = 0x0201, MouseUp = 0x0202, KeyDown = 0x0100, KeyUp = 0x0101, Character = 0x0102;
    public static readonly nint PerMonitorV2 = new(-4);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(int exStyle, string className, string name, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32", SetLastError = true)] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] public static extern nint GetParent(nint hwnd);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32")] public static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32")] public static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32")] public static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32", SetLastError = true)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32", SetLastError = true)] public static extern nint SetParent(nint child, nint parent);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32")] public static extern nint SetFocus(nint hwnd);
    [DllImport("user32")] public static extern nint GetFocus();
    [DllImport("user32")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32", SetLastError = true)] public static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public static string LeaseProperty(string token) => "WpfStudio.InteractiveProbe." + token;
    public static void Require(bool success, string action)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), action);
    }
    public static void RequireWindow(nint hwnd, int pid)
    {
        if (!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd, out uint actual) == 0 || actual != (uint)pid)
            throw new InvalidOperationException("HWND is not owned by the expected process.");
    }
    public static void RequireDpi(nint parent)
    {
        nint context = GetWindowDpiAwarenessContext(parent);
        if (!AreDpiAwarenessContextsEqual(context, PerMonitorV2) ||
            !AreDpiAwarenessContextsEqual(context, GetThreadDpiAwarenessContext()))
            throw new InvalidOperationException("Refusing cross-process parenting with mismatched DPI awareness.");
    }
}
