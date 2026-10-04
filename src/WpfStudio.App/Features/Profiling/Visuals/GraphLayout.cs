using System.Windows;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling.Visuals;

public enum GraphNodeKind { Object, Root, Static, Group }

public sealed class GraphDisplayNode
{
    public required string Key { get; init; }
    public required GraphNodeKind Kind { get; init; }
    public MemoryGraphNode? Node { get; init; }
    public List<MemoryGraphNode> Members { get; } = [];
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Caption { get; init; } = "";
    public bool IsVirtual { get; init; }
    public int Layer { get; set; }
    public double Order { get; set; }
    public Size Size { get; set; }
    public Point Position { get; set; }
    public int HiddenIncoming { get; set; }
    public int HiddenOutgoing { get; set; }
    public Rect Bounds => new(Position, Size);
    public Point Center => new(Position.X + Size.Width / 2, Position.Y + Size.Height / 2);
    public int? ObjectId => Node?.Object.Id;
    public override string ToString() => Key;
}

public sealed class GraphDisplayEdge
{
    public required GraphDisplayNode From { get; init; }
    public required GraphDisplayNode To { get; init; }
    public List<MemoryReferenceInfo> References { get; } = [];
    public bool IsRoot { get; init; }
    public List<Point> Route { get; } = [];
    public string Label => References.Count == 0 ? "" : References.Count == 1 ? References[0].Label
        : References.Select(r => r.Label).Distinct().Count() == 1 ? $"{References[0].Label} ×{References.Count}" : $"{References[0].Label} +{References.Count - 1}";
}

/// <summary>Builds and lays out the vertical retention graph: GC roots on top, owners above the investigated object
/// and referenced objects below it. Static-field holders collapse into "static field" roots, repeated sibling leaves
/// fold into stacked groups, and a layered (Sugiyama-style) layout with virtual nodes keeps edges readable.</summary>
public sealed class GraphLayout
{
    public const double ObjectWidth = 216, ObjectHeight = 66, PillHeight = 30, LayerGap = 58, NodeGap = 26;
    public List<GraphDisplayNode> Nodes { get; } = [];
    public List<GraphDisplayEdge> Edges { get; } = [];
    public Rect Extent { get; private set; }
    public GraphDisplayNode? Focus { get; private set; }

    public static GraphLayout Build(MemoryGraph graph, ISet<string> expandedGroups, Func<string, double, double> measureText)
    {
        var layout = new GraphLayout();
        layout.Create(graph, expandedGroups, measureText);
        layout.Arrange();
        return layout;
    }

    private void Create(MemoryGraph graph, ISet<string> expandedGroups, Func<string, double, double> measure)
    {
        var objects = new Dictionary<int, GraphDisplayNode>();
        foreach (var node in graph.Nodes)
        {
            var display = new GraphDisplayNode
            {
                Key = "o" + node.Object.Id, Kind = GraphNodeKind.Object, Node = node,
                Title = MemoryLabels.ShortType(node.Object.Type), Subtitle = MemoryLabels.Namespace(node.Object.Type),
                Size = new(ObjectWidth, ObjectHeight)
            };
            objects[node.Object.Id] = display; Nodes.Add(display);
            if (node.IsFocus) Focus = display;
        }
        var pills = new Dictionary<string, GraphDisplayNode>(StringComparer.Ordinal);
        var edges = new Dictionary<(string, string), GraphDisplayEdge>();
        var staticHolders = new HashSet<int>();
        foreach (var reference in graph.References)
        {
            if (!objects.TryGetValue(reference.ToId, out var target)) continue;
            GraphDisplayNode source;
            if (reference.IsRoot || reference.Label.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal))
            {
                var display = reference.IsRoot ? MemoryLabels.RootDisplay(reference.Kind, reference.Label) : reference.Label;
                var isStatic = display.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal);
                if (!reference.IsRoot && reference.FromId is int holder) staticHolders.Add(holder);
                var title = isStatic ? display[MemoryLabels.StaticPrefix.Length..] : display;
                var shortTitle = isStatic ? MemoryLabels.ShortStatic(title) : title;
                // Stacks and handles are per target; a static field is one root whatever it references.
                var key = isStatic ? "s" + title : "r" + display + "|" + reference.ToId;
                if (!pills.TryGetValue(key, out source!))
                {
                    var caption = isStatic ? "static field" : reference.IsPinned ? "GC root · pinned" : "GC root";
                    source = new GraphDisplayNode
                    {
                        Key = key, Kind = isStatic ? GraphNodeKind.Static : GraphNodeKind.Root, Title = shortTitle, Subtitle = title,
                        Caption = caption, Size = new(Math.Clamp(Math.Max(measure(shortTitle, 11.5), measure(caption, 9.5)) + 36, 112, 260), PillHeight + 8)
                    };
                    pills[key] = source; Nodes.Add(source);
                }
            }
            else if (reference.FromId is int from && objects.TryGetValue(from, out var owner)) source = owner;
            else continue;
            if (source == target) continue; // e.g. a multicast delegate's _target pointing at itself
            if (!edges.TryGetValue((source.Key, target.Key), out var edge))
                edges[(source.Key, target.Key)] = edge = new GraphDisplayEdge { From = source, To = target, IsRoot = source.Kind is GraphNodeKind.Root };
            edge.References.Add(reference);
        }
        Edges.AddRange(edges.Values);
        // The runtime keeps static fields in a pinned object[]. Once its slots are shown as static roots the holder and
        // its pinned handle only add noise, so drop holders whose remaining edges are all static.
        foreach (var holder in staticHolders)
        {
            if (!objects.TryGetValue(holder, out var node) || node == Focus) continue;
            if (Edges.Any(e => e.From == node) || Edges.Any(e => e.To == node && e.From.Kind == GraphNodeKind.Object)) continue;
            RemoveNode(node);
        }
        foreach (var orphan in Nodes.Where(n => n.Kind is GraphNodeKind.Root or GraphNodeKind.Static && !Edges.Any(e => e.From == n)).ToArray()) RemoveNode(orphan);
        FoldRepeatedSiblings(expandedGroups, children: true);
        FoldRepeatedSiblings(expandedGroups, children: false);
        foreach (var node in Nodes.Where(n => n.Kind == GraphNodeKind.Object))
        {
            var shownIn = Edges.Where(e => e.To == node).Sum(e => e.References.Count);
            var shownOut = Edges.Where(e => e.From == node).Sum(e => e.References.Count);
            node.HiddenIncoming = Math.Max(0, node.Node!.IncomingCount - shownIn);
            node.HiddenOutgoing = Math.Max(0, node.Node.OutgoingCount - shownOut);
        }
    }

    private void RemoveNode(GraphDisplayNode node)
    {
        Nodes.Remove(node);
        Edges.RemoveAll(e => e.From == node || e.To == node);
    }

    /// <summary>Four or more leaves of one type hanging off the same node (and nothing else) become one stacked group.</summary>
    private void FoldRepeatedSiblings(ISet<string> expanded, bool children)
    {
        foreach (var anchor in Nodes.ToArray())
        {
            if (!Nodes.Contains(anchor)) continue;
            var candidates = Edges.Where(e => children ? e.From == anchor : e.To == anchor).Select(e => children ? e.To : e.From)
                .Where(n => n.Kind == GraphNodeKind.Object && n != Focus && Edges.Count(e => e.To == n) == (children ? 1 : 0) && Edges.Count(e => e.From == n) == (children ? 0 : 1))
                .GroupBy(n => n.Node!.Object.TypeKey).Where(g => g.Count() >= 4);
            foreach (var group in candidates.ToArray())
            {
                var key = $"g{anchor.Key}|{(children ? "out" : "in")}|{group.Key}";
                if (expanded.Contains(key)) continue;
                var members = group.ToArray();
                var first = members[0].Node!.Object;
                var folded = new GraphDisplayNode
                {
                    Key = key, Kind = GraphNodeKind.Group, Title = $"{members.Length} × {MemoryLabels.ShortType(first.Type)}",
                    Subtitle = MemoryLabels.Namespace(first.Type), Size = new(ObjectWidth, ObjectHeight)
                };
                folded.Members.AddRange(members.Select(m => m.Node!));
                var edge = new GraphDisplayEdge { From = children ? anchor : folded, To = children ? folded : anchor };
                foreach (var member in members)
                {
                    foreach (var old in Edges.Where(e => e.From == member || e.To == member)) edge.References.AddRange(old.References);
                    RemoveNode(member);
                }
                Nodes.Add(folded); Edges.Add(edge);
            }
        }
    }

    private void Arrange()
    {
        if (Nodes.Count == 0) { Extent = Rect.Empty; return; }
        var successors = Nodes.ToDictionary(n => n, _ => new List<GraphDisplayNode>());
        var predecessors = Nodes.ToDictionary(n => n, _ => new List<GraphDisplayNode>());
        // Break cycles with a DFS from the sources (roots first) and treat back edges as reversed for layering only.
        var state = Nodes.ToDictionary(n => n, _ => 0);
        var forward = new List<(GraphDisplayNode From, GraphDisplayNode To)>();
        var adjacency = Nodes.ToDictionary(n => n, n => Edges.Where(e => e.From == n).Select(e => e.To).ToList());
        foreach (var start in Nodes.OrderBy(n => Edges.Any(e => e.To == n) ? 1 : 0).ThenBy(n => n.Kind == GraphNodeKind.Object ? 1 : 0))
        {
            if (state[start] != 0) continue;
            var stack = new Stack<(GraphDisplayNode Node, int Next)>(); stack.Push((start, 0)); state[start] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next < adjacency[node].Count)
                {
                    stack.Push((node, next + 1));
                    var to = adjacency[node][next];
                    if (state[to] == 0) { forward.Add((node, to)); state[to] = 1; stack.Push((to, 0)); }
                    else if (state[to] == 2) forward.Add((node, to));
                    else forward.Add((to, node)); // back edge: reverse
                }
                else state[node] = 2;
            }
        }
        foreach (var (from, to) in forward.Distinct()) { if (from == to) continue; successors[from].Add(to); predecessors[to].Add(from); }
        // Longest-path layering, then pull every node down to just above its nearest successor so edges stay short.
        var order = TopologicalOrder(successors, predecessors);
        foreach (var node in order) node.Layer = predecessors[node].Count == 0 ? 0 : predecessors[node].Max(p => p.Layer) + 1;
        foreach (var node in Enumerable.Reverse(order))
            if (successors[node].Count > 0) node.Layer = Math.Max(node.Layer, successors[node].Min(s => s.Layer) - 1);
        // Virtual nodes split long edges so crossings can be minimised and routes avoid real nodes.
        var chains = new Dictionary<GraphDisplayEdge, List<GraphDisplayNode>>();
        var allNodes = new List<GraphDisplayNode>(Nodes);
        var links = new List<(GraphDisplayNode Upper, GraphDisplayNode Lower)>();
        foreach (var edge in Edges)
        {
            var (upper, lower) = edge.From.Layer <= edge.To.Layer ? (edge.From, edge.To) : (edge.To, edge.From);
            var chain = new List<GraphDisplayNode> { upper };
            for (var layer = upper.Layer + 1; layer < lower.Layer; layer++)
            {
                var dummy = new GraphDisplayNode { Key = $"v{allNodes.Count}", Kind = GraphNodeKind.Object, Title = "", IsVirtual = true, Layer = layer, Size = new(12, 12) };
                allNodes.Add(dummy); chain.Add(dummy);
            }
            chain.Add(lower);
            if (upper.Layer == lower.Layer) chain.Clear();
            for (var i = 0; i + 1 < chain.Count; i++) links.Add((chain[i], chain[i + 1]));
            chains[edge] = chain;
        }
        var layers = allNodes.GroupBy(n => n.Layer).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var up = allNodes.ToDictionary(n => n, _ => new List<GraphDisplayNode>());
        var down = allNodes.ToDictionary(n => n, _ => new List<GraphDisplayNode>());
        foreach (var (upper, lower) in links) { down[upper].Add(lower); up[lower].Add(upper); }
        // Initial order: depth-first from the top so related nodes start together; roots by priority.
        var seen = new HashSet<GraphDisplayNode>(); var counter = 0;
        foreach (var start in layers[0].OrderBy(n => n.Kind == GraphNodeKind.Static ? 0 : n.Kind == GraphNodeKind.Root ? 1 : 2))
            Visit(start);
        foreach (var node in allNodes) if (!seen.Contains(node)) Visit(node);
        void Visit(GraphDisplayNode start)
        {
            var stack = new Stack<GraphDisplayNode>(); stack.Push(start);
            while (stack.TryPop(out var node))
            {
                if (!seen.Add(node)) continue;
                node.Order = counter++;
                foreach (var child in Enumerable.Reverse(down[node].OrderByDescending(c => c.Node?.Object.RetainedBytes ?? 0).ToList())) stack.Push(child);
            }
        }
        foreach (var layer in layers) layer.Sort((a, b) => a.Order.CompareTo(b.Order));
        for (var sweep = 0; sweep < 8; sweep++)
        {
            var downward = sweep % 2 == 0;
            var sequence = downward ? layers.Skip(1) : Enumerable.Reverse(layers).Skip(1);
            foreach (var layer in sequence)
            {
                foreach (var node in layer)
                {
                    var neighbours = downward ? up[node] : down[node];
                    if (neighbours.Count > 0) node.Order = neighbours.Average(n => IndexOf(n));
                }
                layer.Sort((a, b) => a.Order.CompareTo(b.Order));
            }
        }
        double IndexOf(GraphDisplayNode node) => layers[LayerIndex(node)].IndexOf(node);
        int LayerIndex(GraphDisplayNode node) => layers.FindIndex(l => l.Count > 0 && l[0].Layer == node.Layer);
        // Coordinates: pack each layer, then pull nodes toward the mean of their neighbours while keeping order.
        var y = 0d;
        foreach (var layer in layers)
        {
            var height = layer.Max(n => n.Size.Height);
            var x = 0d;
            foreach (var node in layer) { node.Position = new(x, y + (height - node.Size.Height) / 2); x += node.Size.Width + (node.IsVirtual ? 10 : NodeGap); }
            y += height + LayerGap;
        }
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var downward = iteration % 2 == 0;
            foreach (var layer in downward ? layers : Enumerable.Reverse(layers))
            {
                var desired = layer.Select(node =>
                {
                    var neighbours = up[node].Concat(down[node]).ToList();
                    var weight = node.IsVirtual ? 1.0 : 0.85;
                    return neighbours.Count == 0 ? node.Center.X : node.Center.X * (1 - weight) + neighbours.Average(n => n.Center.X) * weight;
                }).ToArray();
                // Left-to-right then right-to-left passes resolve overlaps around the desired centres.
                var centers = desired.ToArray();
                for (var i = 1; i < layer.Count; i++)
                {
                    var min = centers[i - 1] + (layer[i - 1].Size.Width + layer[i].Size.Width) / 2 + Gap(layer[i - 1], layer[i]);
                    if (centers[i] < min) centers[i] = min;
                }
                for (var i = layer.Count - 2; i >= 0; i--)
                {
                    var max = centers[i + 1] - (layer[i + 1].Size.Width + layer[i].Size.Width) / 2 - Gap(layer[i], layer[i + 1]);
                    if (centers[i] > max) centers[i] = (centers[i] + max) / 2 > max ? max : (centers[i] + max) / 2;
                }
                for (var i = 1; i < layer.Count; i++)
                {
                    var min = centers[i - 1] + (layer[i - 1].Size.Width + layer[i].Size.Width) / 2 + Gap(layer[i - 1], layer[i]);
                    if (centers[i] < min) centers[i] = min;
                }
                for (var i = 0; i < layer.Count; i++) layer[i].Position = new(centers[i] - layer[i].Size.Width / 2, layer[i].Position.Y);
            }
        }
        static double Gap(GraphDisplayNode a, GraphDisplayNode b) => a.IsVirtual || b.IsVirtual ? 10 : NodeGap;
        // Route every edge through its virtual nodes, from the bottom of the upper node to the top of the lower one.
        foreach (var edge in Edges)
        {
            edge.Route.Clear();
            var chain = chains[edge];
            if (chain.Count == 0)
            {
                // Same layer (rare: a cycle inside one layer). Arc over the top.
                var a = edge.From.Center; var b = edge.To.Center;
                edge.Route.Add(new(a.X, edge.From.Position.Y)); edge.Route.Add(new((a.X + b.X) / 2, Math.Min(a.Y, b.Y) - LayerGap * 0.6)); edge.Route.Add(new(b.X, edge.To.Position.Y));
                continue;
            }
            var reversed = chain[0] != edge.From;
            var points = chain.Select((node, i) => i == 0 ? new Point(node.Center.X, node.Position.Y + node.Size.Height)
                : i == chain.Count - 1 ? new Point(node.Center.X, node.Position.Y) : node.Center).ToList();
            if (reversed) points.Reverse();
            edge.Route.AddRange(points);
        }
        var real = Nodes.Select(n => n.Bounds).ToArray();
        var extent = real[0];
        foreach (var r in real.Skip(1)) extent.Union(r);
        foreach (var edge in Edges) foreach (var p in edge.Route) extent.Union(p);
        Extent = extent;
    }

    private List<GraphDisplayNode> TopologicalOrder(Dictionary<GraphDisplayNode, List<GraphDisplayNode>> successors, Dictionary<GraphDisplayNode, List<GraphDisplayNode>> predecessors)
    {
        var remaining = Nodes.ToDictionary(n => n, n => predecessors[n].Count);
        var queue = new Queue<GraphDisplayNode>(Nodes.Where(n => remaining[n] == 0));
        var order = new List<GraphDisplayNode>();
        while (queue.TryDequeue(out var node))
        {
            order.Add(node);
            foreach (var next in successors[node]) if (--remaining[next] == 0) queue.Enqueue(next);
        }
        foreach (var node in Nodes) if (!order.Contains(node)) order.Add(node); // defensive: cycles already broken
        return order;
    }
}
