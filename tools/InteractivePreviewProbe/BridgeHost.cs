using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace InteractivePreviewProbe;

// View-only native interop harness. This owns only its local bridge HWND, never the remote HWND.
internal sealed class BridgeHost(string token) : HwndHost
{
    public event Action<nint>? Created;
    public event Action? Destroying;
    public event Action<int, int>? Resized;
    public event Action<FocusNavigationDirection>? EnterRequested;
    public nint Bridge => Handle;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Native.RequireDpi(hwndParent.Handle);
        nint hwnd = Native.CreateWindowEx(0, "STATIC", "Interactive preview probe bridge",
            Native.Child | Native.Visible | Native.ClipChildren | Native.ClipSiblings,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        Native.Require(hwnd != 0, "Create local bridge HWND");
        Native.Require(Native.SetProp(hwnd, Native.LeaseProperty(token), new nint(1)), "Set bridge lease");
        Dispatcher.BeginInvoke(() => Created?.Invoke(hwnd));
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Destroying?.Invoke(); // Parent kills the pinned child first; no remote window destruction.
        if (Native.IsWindow(hwnd.Handle))
        {
            Native.RemoveProp(hwnd.Handle, Native.LeaseProperty(token));
            Native.DestroyWindow(hwnd.Handle);
        }
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (Handle != 0 && Native.GetClientRect(Handle, out Native.Rect rect))
            Resized?.Invoke(Math.Max(1, rect.Right), Math.Max(1, rect.Bottom));
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        EnterRequested?.Invoke(request.FocusNavigationDirection);
        return true; // Asynchronous host focus acknowledgement is logged separately.
    }

    public void Leave(FocusNavigationDirection direction) =>
        ((IKeyboardInputSink)this).KeyboardInputSite?.OnNoMoreTabStops(new TraversalRequest(direction));
}
