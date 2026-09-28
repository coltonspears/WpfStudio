using System.Windows.Threading;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    public Task<PreviewSnapshot> CaptureAsync(PreviewCaptureRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(async () =>
        {
            var root = _root;
            var viewport = _viewport;
            var surface = _surface;
            bool Current() => !_disposed && request.Version == _version && root is not null && viewport is not null
                && surface is { IsDisposed: false } && ReferenceEquals(root, _root)
                && ReferenceEquals(viewport, _viewport) && ReferenceEquals(surface, _surface);
            PreviewSnapshot Unavailable() => new(request.Version, false, null, 0, 0, [], [],
                "The preview changed or is unavailable. Refresh the preview before updating its snapshot.");

            if (!Current()) return Unavailable();
            // Let ordinary binding/layout work run. Capturing must not reapply
            // scenario data, recreate controls, or force new layout constraints.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Current()) return Unavailable();
            return Snapshot();
        }, cancellationToken);
}
