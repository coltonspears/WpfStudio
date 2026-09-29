using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// Keeps the designer selection across re-renders. Every render creates new runtime
/// objects and node ids, so the selection is matched structurally: by a unique x:Name,
/// then by the element's position among authored elements, then by its visual-tree position.
/// </summary>
public sealed partial class DesignerViewModel
{
    private sealed record SelectionMemento(string Type, string? Name, IReadOnlyList<string> AuthoredPath,
        IReadOnlyList<string> VisualPath, string? PropertyName, string? PropertyOwner);

    private SelectionMemento? _rememberedSelection;
    private SelectionMemento? _restoringSelection;

    private void RememberSelection()
    {
        // Several invalidations can precede one render; keep the element selected before the first.
        if (SelectedNode?.Node is not { } node || _snapshot is not { } snapshot) return;
        var paths = new NodePaths(snapshot.Nodes);
        _rememberedSelection = new(node.Type, node.Name, paths.Authored(node), paths.Visual(node),
            SelectedProperty?.Name, SelectedProperty?.OwnerType);
    }

    private void ForgetRememberedSelection() => _rememberedSelection = null;

    /// <summary>Selects the element that corresponds to the selection before the last render.</summary>
    private void RestoreSelection()
    {
        if (_rememberedSelection is not { } memento || _snapshot is not { } snapshot || SelectedNode is not null) return;
        _rememberedSelection = null;
        var candidates = snapshot.Nodes.Where(n => n.Type == memento.Type && (ShowLogicalTree || n.IsVisual)).ToArray();
        if (candidates.Length == 0) return;
        var paths = new NodePaths(snapshot.Nodes);
        PreviewNode? match = null;
        if (memento.Name is not null && candidates.Where(n => n.Name == memento.Name).ToArray() is [var named]) match = named;
        if (match is null && memento.AuthoredPath.Count > 0
            && candidates.Where(n => n.Source is not null && paths.Authored(n).SequenceEqual(memento.AuthoredPath)).ToArray() is [var authored])
            match = authored;
        match ??= candidates.FirstOrDefault(n => paths.Visual(n).SequenceEqual(memento.VisualPath));
        if (match is null || FindNode(Tree, match.Id) is not { } restored) return;
        _restoringSelection = memento;
        _suppressSourceSync++;
        try { SelectedNode = restored; }
        finally { _suppressSourceSync--; }
    }

    /// <summary>After the restored element is inspected, reselect the property row the user had open.</summary>
    private void RestoreSelectedProperty()
    {
        if (_restoringSelection is not { } memento) return;
        _restoringSelection = null;
        if (memento.PropertyName is null || SelectedProperty is not null) return;
        SelectedProperty = Properties.FirstOrDefault(p => p.Name == memento.PropertyName && p.OwnerType == memento.PropertyOwner)
            ?? Properties.FirstOrDefault(p => p.Name == memento.PropertyName);
    }

    /// <summary>Structural paths of snapshot nodes: type plus ordinal among same-type siblings, from the root.</summary>
    private sealed class NodePaths
    {
        private readonly Dictionary<string, PreviewNode> _nodes;
        private readonly Dictionary<string, (string? Parent, int Ordinal)> _visual = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string? Parent, int Ordinal)> _authored = new(StringComparer.Ordinal);

        public NodePaths(IReadOnlyList<PreviewNode> nodes)
        {
            _nodes = new(StringComparer.Ordinal);
            foreach (var node in nodes) _nodes.TryAdd(node.Id, node);
            var visualCounts = new Dictionary<(string?, string), int>();
            var authoredCounts = new Dictionary<(string?, string), int>();
            // Snapshot nodes are in document order, so sibling ordinals follow the tree.
            foreach (var node in nodes)
            {
                var parent = node.ParentId is { } id && _nodes.ContainsKey(id) ? id : null;
                _visual[node.Id] = (parent, Next(visualCounts, (parent, node.Type)));
                if (node.Source is null) continue;
                var authoredParent = AuthoredAncestor(node);
                _authored[node.Id] = (authoredParent, Next(authoredCounts, (authoredParent, node.Type)));
            }
        }

        public IReadOnlyList<string> Visual(PreviewNode node) => Walk(node.Id, _visual);
        public IReadOnlyList<string> Authored(PreviewNode node) => node.Source is null ? [] : Walk(node.Id, _authored);

        private string? AuthoredAncestor(PreviewNode node)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            for (var id = node.ParentId; id is not null && visited.Add(id) && _nodes.TryGetValue(id, out var parent); id = parent.ParentId)
                if (parent.Source is not null) return parent.Id;
            return null;
        }

        private List<string> Walk(string id, Dictionary<string, (string? Parent, int Ordinal)> index)
        {
            var segments = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (string? current = id; current is not null && visited.Add(current) && index.TryGetValue(current, out var entry); current = entry.Parent)
                segments.Add(_nodes[current].Type + "[" + entry.Ordinal + "]");
            segments.Reverse();
            return segments;
        }

        private static int Next(Dictionary<(string?, string), int> counts, (string?, string) key)
        {
            counts.TryGetValue(key, out var value);
            counts[key] = value + 1;
            return value;
        }
    }
}
