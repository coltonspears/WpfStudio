using System.Windows.Input;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    private static readonly uint NativeNavigationMessage = PreviewNativeMethods.RegisterWindowMessage(PreviewNativeNavigation.MessageName);

    private bool NavigateNativeBoundary(FocusNavigationDirection direction)
    {
        if (_attachment is not { } attachment || _nativeNavigationToken is not { } token ||
            _navigationPendingSinceTick != 0 || _nativeFailure is not null) return false;
        nint hwnd = 0;
        long encoded = 0;
        object? handoff = null;
        string property = PreviewNativeNavigation.PropertyPrefix + token;
        bool granted = false;
        try
        {
            ValidateBridge(attachment);
            RequireAttachedBridge(attachment);
            hwnd = RequireOwnedSurface(attachment.Surface);
            if (NativeNavigationMessage == 0 || _nativeCallSequence >= (nint.MaxValue.ToInt64() - 2) / 4)
                return Fail("The native keyboard navigation sequence is unavailable.");
            encoded = ++_nativeCallSequence * 4 + (direction == FocusNavigationDirection.Previous ? 2 : 1);
            if (!PreviewNativeMethods.SetProp(hwnd, property, new nint(encoded)))
                return Fail("The native keyboard navigation grant could not be created.");
            granted = true;
            _nativeHandoff = handoff = new object();
            _navigationPendingSinceTick = Math.Max(1, Environment.TickCount64);

            // Focus changes synchronously send WM_KILLFOCUS back to this thread.
            // SMTO_BLOCK would prevent that reentrancy. Attached input queues can
            // ignore the nominal timeout, so the independent client watchdog also
            // bounds NavigationPendingSinceTick even during a nested message pump.
            nint completed = PreviewNativeMethods.SendMessageTimeout(_bridgeHandle, NativeNavigationMessage,
                unchecked((nuint)hwnd), new nint(encoded), 0x0002 | 0x0020, 1500, out nuint result);
            if (!CurrentSurface(attachment.Surface) || !ReferenceEquals(_attachment, attachment) || !ReferenceEquals(_nativeHandoff, handoff))
                return true; // The old traversal has no authority over a replacement view.
            if (completed == 0)
                return Fail("Native keyboard handoff did not complete. Its outcome is unknown; the preview must restart.");
            if (result == PreviewNativeNavigation.Rejected) return false;
            if (result == PreviewNativeNavigation.Moved) return true;
            return Fail("Native keyboard handoff returned an unknown acknowledgement; the preview must restart.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Fail("Native keyboard handoff failed: " + exception.GetBaseException().Message);
        }
        finally
        {
            // Only touch the same still-owned HWND. Destruction removes its
            // properties automatically; a recycled handle is never cleanup authority.
            if (granted && CurrentSurface(attachment.Surface) && _surface!.Handle == hwnd &&
                PreviewNativeMethods.OwnedWindow(hwnd, _session.ProcessId) &&
                PreviewNativeMethods.GetProp(hwnd, property) == new nint(encoded))
                PreviewNativeMethods.RemoveProp(hwnd, property);
            if (handoff is not null && ReferenceEquals(_nativeHandoff, handoff))
            {
                _nativeHandoff = null;
                _navigationPendingSinceTick = 0;
            }
        }

        bool Fail(string status)
        {
            if (CurrentSurface(attachment.Surface) && ReferenceEquals(_attachment, attachment) &&
                (handoff is null || ReferenceEquals(_nativeHandoff, handoff))) _nativeFailure = status;
            // Never wrap or replay after a callback may already have moved focus.
            // A failed heartbeat revokes the exact owned process through the client.
            return true;
        }
    }
}
