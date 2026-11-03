using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// What this bridge holds on a session's native child. The lease is published before an attach is awaited, so it
/// covers a child that may exist but was never acknowledged, and it ends only when the session has confirmed that
/// the child is gone. The attachment values describe the current attach and are discarded with it; the lease can
/// outlive them, because a reset attachment may still have a child to release.
/// </summary>
internal sealed class PreviewLease
{
    private IPreviewInteractionSession? _detaching;
    private Task? _deactivation;

    public IPreviewInteractionSession? Session { get; private set; }
    public bool IsAttaching { get; private set; }
    public string? NavigationToken { get; private set; }
    public nint NativeHandle { get; private set; }
    public long AttachmentSequence { get; private set; }
    public long BoundarySequence { get; set; }
    public long FocusObservationSequence { get; set; }
    public PreviewSurfaceFocus? PendingFocusObservation { get; set; }

    /// <summary>The session's detach finished, though the continuation that would end the lease may still be queued.</summary>
    private bool IsDetached => _deactivation?.IsCompletedSuccessfully == true && ReferenceEquals(_detaching, Session);

    public void ResetAttachment()
    {
        NavigationToken = null;
        NativeHandle = 0;
        AttachmentSequence = BoundarySequence = FocusObservationSequence = 0;
        IsAttaching = false;
        PendingFocusObservation = null;
    }

    public void BeginAttachment()
    {
        ResetAttachment();
        NavigationToken = Guid.NewGuid().ToString("N");
        IsAttaching = true;
    }

    public void AcceptAttachment(nint nativeHandle, long sequence)
    {
        NativeHandle = nativeHandle;
        AttachmentSequence = sequence;
        IsAttaching = false;
    }

    public void Publish(IPreviewInteractionSession session)
    {
        _deactivation = null; _detaching = null;
        Session = session;
    }

    /// <summary>Asks the holder to detach. The lease stands until <see cref="EndDeactivation"/>, since the detach may not finish.</summary>
    public Task BeginDeactivation(IPreviewInteractionSession session)
    {
        _detaching = session;
        return _deactivation = session.DeactivateAsync();
    }

    public void EndDeactivation(IPreviewInteractionSession session)
    {
        if (ReferenceEquals(Session, session)) Session = null;
    }

    /// <summary>
    /// Ends the lease without waiting. Abort confirms that no remote child can remain attached; a finished detach is
    /// not aborted, because another bridge may already have reused the session. If the abort throws, the lease stands.
    /// </summary>
    public void Abort(string reason)
    {
        if (!IsDetached) Session?.Abort(reason);
        Session = null;
    }
}
