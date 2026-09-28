namespace WpfStudio.Contracts;

/// <summary>Opaque identity for one rendered native surface in one launched process.</summary>
public sealed record PreviewSurfaceIdentity(string SessionId, long Version, string SurfaceId);

public sealed record PreviewHostSession(string SessionId, int ProcessId, int ParentProcessId,
    int ProtocolVersion, bool NativeInteractionAvailable);

public enum PreviewSurfaceAction { Attach, Update, Detach, FocusFirst, FocusLast }
public enum PreviewSurfaceNavigation { None, Next, Previous, Inspect }

/// <summary>Scalar native messages only; no pointers or cross-process memory are exchanged.</summary>
public static class PreviewNativeNavigation
{
    public const int ProtocolVersion = 2;
    public const string MessageName = "WpfStudio.Preview.TabBoundary.v2";
    public const string PropertyPrefix = "WpfStudio.PreviewNavigation.";
    public const int Unknown = -1;
    public const int Rejected = 0;
    public const int Moved = 1;
}

/// <summary>
/// PixelWidth/Height describe the bridge client viewport; OffsetX/Y are positive scroll offsets in physical pixels.
/// The host sizes its content from the rendered DIP dimensions and the actual parent DPI.
/// Sequence is strictly increasing for all commands on this surface. BridgeToken is a Guid in N format;
/// the IDE-owned HWND carries property WpfStudio.PreviewBridge.&lt;BridgeToken&gt; with value 1.
/// FocusFirst/Last also require a short-lived FocusToken (Guid N format); the IDE-owned bridge
/// carries WpfStudio.PreviewFocus.&lt;FocusToken&gt; with value 1 while keyboard entry is still intended.
/// NavigationToken is a fresh Guid N value per attachment. During a synchronous Tab boundary call,
/// the host-owned child exposes WpfStudio.PreviewNavigation.&lt;NavigationToken&gt; with the encoded call value.
/// </summary>
public sealed record PreviewSurfaceRequest(PreviewSurfaceIdentity Surface, string BridgeToken,
    long ParentHandle, int ParentProcessId, int PixelWidth, int PixelHeight,
    int OffsetX = 0, int OffsetY = 0, long Sequence = 0, PreviewSurfaceAction Action = PreviewSurfaceAction.Attach,
    string? FocusToken = null, string? NavigationToken = null);

/// <summary>Focused reports an attempted keyboard entry; null means no entry was attempted or requested.</summary>
public sealed record PreviewSurfaceResponse(PreviewSurfaceRequest Request, bool Success, string? Status = null,
    bool? Focused = null, long NativeHandle = 0, PreviewSurfaceFocus? Focus = null);
public sealed record PreviewSurfaceHeartbeatRequest(PreviewSurfaceIdentity Surface);

/// <summary>
/// Cached focus bounds in the native presentation root's DIPs, without viewport offsets.
/// FocusEpoch and FocusChangedAtTick change with the focused object; Sequence also changes with geometry.
/// A null Bounds is an explicit unavailable/cleared observation. Tick values use system uptime milliseconds.
/// </summary>
public sealed record PreviewSurfaceFocus(string BridgeToken, long AttachmentSequence, long Sequence,
    long FocusEpoch, long FocusChangedAtTick, long NativeFocusHandle, uint Dpi,
    PreviewBounds? Bounds, string? Status = null);

/// <summary>A dispatcher acknowledgement, independent of the ordinary request semaphore.</summary>
public sealed record PreviewSurfaceHeartbeat(PreviewSurfaceIdentity Surface, bool Available,
    long NavigationSequence = 0, PreviewSurfaceNavigation Navigation = PreviewSurfaceNavigation.None, string? Status = null,
    PreviewSurfaceFocus? Focus = null, long NavigationPendingSinceTick = 0);
