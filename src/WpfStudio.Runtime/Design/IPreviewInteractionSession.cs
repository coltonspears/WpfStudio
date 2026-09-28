using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Design;

public sealed record PreviewInteractionEvent(bool Available, string? Status = null,
    PreviewSurfaceNavigation Navigation = PreviewSurfaceNavigation.None, long NavigationSequence = 0,
    PreviewSurfaceFocus? Focus = null);

/// <summary>Process/surface lifetime operations used by a native view; contains no WPF controls.</summary>
public interface IPreviewInteractionSession
{
    PreviewSurfaceIdentity Surface { get; }
    /// <summary>The authenticated process for this exact launch; zero means native handoff is unsupported.</summary>
    int NativeProcessId => 0;
    bool IsAvailable { get; }
    /// <summary>Includes a pending attach until a detach or process exit is confirmed.</summary>
    bool IsAttached { get; }
    event EventHandler<PreviewInteractionEvent>? Changed;
    Task<PreviewSurfaceResponse> UpdateAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default);
    Task DeactivateAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Revokes this exact session and confirms termination of its owned process when attached.
    /// Must bypass ordinary RPC gates. Throws if safe teardown cannot be confirmed; the caller
    /// must retain its local native bridge until the remote lifetime ends.
    /// </summary>
    void Abort(string reason);
}
