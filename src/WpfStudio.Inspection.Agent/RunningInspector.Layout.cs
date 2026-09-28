using System.Windows;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    private LayoutSnapshot CaptureLayout(InspectionNodeRequest request, DependencyObject target)
    {
        // The same weak presentation identity is used by property reads and edits.
        // Recheck after property callbacks: application code can remove an element.
        if (request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var entry) ||
            !entry.Target.TryGetTarget(out var observed) || !ReferenceEquals(target, observed) ||
            !entry.Source.TryGetTarget(out var source) || source.IsDisposed ||
            !entry.Root.TryGetTarget(out var root) || source.RootVisual != root || !IsAttached(target, root))
            return new(false, [], [], [], "The element has left its presentation tree. Refresh the tree.");
        return LayoutReader.Capture(target, root);
    }
}
