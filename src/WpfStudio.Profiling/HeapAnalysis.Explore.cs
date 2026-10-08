using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Views that organize the heap for people: the dominator tree, per-type retention, retained composition
/// and aggregated retention paths. Every query is bounded and runs in the worker.</summary>
public sealed partial class HeapAnalysis
{
    private (int[] Offsets, int[] Children) BuildDominatorTree(CancellationToken token)
    {
        var count = _graph.Objects.Length; var root = count;
        var offsets = new int[count + 2];
        for (var i = 0; i < count; i++)
            if (Reachable[i]) offsets[(ImmediateDominators[i] < 0 ? root : ImmediateDominators[i]) + 1]++;
        for (var i = 1; i < offsets.Length; i++) offsets[i] += offsets[i - 1];
        var cursor = (int[])offsets.Clone();
        var children = new int[offsets[^1]];
        // Filling in ranked order leaves every parent's children largest retained first, with no per-parent sort.
        for (var n = 0; n < _rankedObjects.Length; n++)
        {
            if ((n & 65535) == 0) token.ThrowIfCancellationRequested();
            var i = _rankedObjects[n];
            if (Reachable[i]) children[cursor[ImmediateDominators[i] < 0 ? root : ImmediateDominators[i]]++] = i;
        }
        return (offsets, children);
    }

    private ReadOnlySpan<int> DominatedBy(int parent) => parent < 0
        ? _dominatorChildren.AsSpan(_dominatorOffsets[_graph.Objects.Length], _dominatorOffsets[_graph.Objects.Length + 1] - _dominatorOffsets[_graph.Objects.Length])
        : _dominatorChildren.AsSpan(_dominatorOffsets[parent], _dominatorOffsets[parent + 1] - _dominatorOffsets[parent]);

    /// <summary>Bytes kept alive by all instances of each type together. An instance inside the dominator subtree of
    /// another instance of the same type is not counted twice.</summary>
    private long[] ComputeTypeRetained(CancellationToken token)
    {
        var types = _graph.Types.Length; var count = _graph.Objects.Length;
        var inside = new int[types]; var retained = new long[types];
        var stack = new int[count + 1]; var position = new int[count + 1]; var top = 0;
        stack[0] = count; position[0] = _dominatorOffsets[count];
        var steps = 0;
        while (top >= 0)
        {
            if ((steps++ & 4095) == 0) token.ThrowIfCancellationRequested();
            var node = stack[top];
            if (position[top] < _dominatorOffsets[node + 1])
            {
                var child = _dominatorChildren[position[top]++];
                var type = _graph.Objects[child].TypeId;
                if (inside[type]++ == 0) retained[type] += RetainedBytes[child];
                stack[++top] = child; position[top] = _dominatorOffsets[child];
            }
            else
            {
                if (node != count) inside[_graph.Objects[node].TypeId]--;
                top--;
            }
        }
        return retained;
    }

    /// <summary>The objects that own the most memory. Pass-through wrappers (a holder whose single dominated child
    /// owns nearly everything) are skipped in favour of that child, unless the wrapper is an application object holding a
    /// framework one (a page holding its byte[] is the owner people recognise). Permanent objects are excluded.</summary>
    public IReadOnlyList<MemoryObjectInfo> GetTopRetainers(int take = 12)
    {
        var result = new List<MemoryObjectInfo>();
        foreach (var id in _rankedObjects)
        {
            if (result.Count >= take || RetainedBytes[id] <= 0) break;
            if (!Reachable[id] || _graph.Objects[id].Generation == HeapGeneration.Frozen || _staticsHolder[id]) continue;
            var children = DominatedBy(id);
            if (children.Length > 0 && RetainedBytes[children[0]] >= RetainedBytes[id] * 0.85 &&
                !(IsApplicationObject(id) && !IsApplicationObject(children[0]))) continue;
            result.Add(Describe(id));
        }
        return result;
    }

    private bool IsApplicationObject(int id) => !MemoryLabels.IsFrameworkModule(_graph.Types[_graph.Objects[id].TypeId].Module);

    public MemoryDominatorPage GetDominators(MemoryDominatorQuery query, CancellationToken token = default)
    {
        if (query.ParentId is int parentId) ValidateObject(parentId);
        var take = Math.Clamp(query.Take, 1, 500);
        var children = query.ParentId is null ? TopLevelDominated() : DominatedBy(query.ParentId.Value).ToArray();
        long total = 0; foreach (var child in children) total += RetainedBytes[child];
        IEnumerable<int> scoped = children;
        if (query.TypeKey is not null)
            scoped = TryGetTypeId(query.TypeKey, out var typeId) ? children.Where(c => _graph.Objects[c].TypeId == typeId) : [];
        List<MemoryDominatorNode> nodes;
        if (query.GroupByType && query.TypeKey is null)
        {
            nodes = scoped.GroupBy(c => _graph.Objects[c].TypeId).Select(g =>
            {
                var members = g.ToArray(); var type = _graph.Types[g.Key];
                long retained = 0, shallow = 0; foreach (var m in members) { retained += RetainedBytes[m]; shallow += _graph.Objects[m].Size; }
                if (members.Length == 1) return Single(members[0], query.ParentId);
                var vias = members.Take(64).Select(m => Via(m, ParentFor(m, query.ParentId))).Distinct().Take(2).ToArray();
                return new MemoryDominatorNode(type.Key, type.Name, members.Length, retained, shallow, members.Length, null, vias.Length == 1 ? vias[0] : "several owners");
            }).OrderByDescending(n => n.RetainedBytes).ThenByDescending(n => n.ShallowBytes).ToList();
        }
        else nodes = scoped.Select(c => Single(c, ParentFor(c, query.ParentId))).ToList();
        token.ThrowIfCancellationRequested();
        var shown = nodes.Take(take).ToArray(); var rest = nodes.Skip(take).ToArray();
        return new(query.ParentId, query.TypeKey, nodes.Count, query.ParentId is int p ? RetainedBytes[p] : total, shown,
            rest.Sum(n => n.Count), rest.Sum(n => n.RetainedBytes));

        MemoryDominatorNode Single(int id, int? parent)
        {
            var obj = Describe(id);
            return new(obj.TypeKey, obj.Type, 1, obj.RetainedBytes, obj.ShallowBytes, DominatedBy(id).Length, obj, Via(id, ParentFor(id, parent)));
        }
    }

    /// <summary>At the top level, a statics holder's children are listed in its place so each static field shows as its
    /// own owner ("Static Cache.Pages") instead of one runtime object[] that owns most of the heap.</summary>
    private int? ParentFor(int id, int? parent) =>
        parent is null && ImmediateDominators[id] >= 0 && _staticsHolder[ImmediateDominators[id]] ? ImmediateDominators[id] : parent;

    private int[] TopLevelDominated()
    {
        var result = new List<int>();
        foreach (var child in DominatedBy(-1))
        {
            if (_staticsHolder[child]) foreach (var held in DominatedBy(child)) result.Add(held);
            else result.Add(child);
        }
        return result.OrderByDescending(id => RetainedBytes[id]).ToArray();
    }

    /// <summary>The field or root through which the dominator reaches an object, for readable tree labels.</summary>
    private string? Via(int id, int? dominator)
    {
        if (dominator is int owner)
        {
            foreach (var edgeId in _graph.Incoming.For(id))
                if (_graph.Edges[edgeId].From == owner) return _graph.Label(_graph.Edges[edgeId].LabelId);
            return null;
        }
        var parent = _parentReference[id];
        if (parent < 0) return null;
        if (parent >= _graph.Edges.Length)
        {
            var root = _graph.Roots[parent - _graph.Edges.Length];
            return MemoryLabels.RootDisplay(root.Kind, root.Label);
        }
        var label = _graph.Label(_graph.Edges[parent].LabelId);
        return label.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal) ? label : null;
    }

    /// <summary>What an object keeps alive: its dominator subtree grouped by type.</summary>
    public MemoryRetainedComposition GetRetained(int objectId, CancellationToken token = default)
    {
        ValidateObject(objectId);
        var byType = new Dictionary<int, (int Count, long Bytes)>();
        var stack = new Stack<int>(); stack.Push(objectId);
        var visited = 0; var truncated = false;
        while (stack.TryPop(out var node))
        {
            if ((++visited & 4095) == 0) token.ThrowIfCancellationRequested();
            if (visited > 5_000_000) { truncated = true; break; }
            var type = _graph.Objects[node].TypeId;
            byType.TryGetValue(type, out var sum);
            byType[type] = (sum.Count + 1, sum.Bytes + _graph.Objects[node].Size);
            foreach (var child in DominatedBy(node)) stack.Push(child);
        }
        var types = byType.OrderByDescending(kv => kv.Value.Bytes).Take(200)
            .Select(kv => new MemoryReleasedType(_graph.Types[kv.Key].Name, kv.Value.Count, kv.Value.Bytes, _graph.Types[kv.Key].Key)).ToArray();
        return new(objectId, RetainedBytes[objectId], RetainedObjects[objectId], types, truncated || byType.Count > 200);
    }

    /// <summary>Aggregates retention paths into a Sankey: the target on level 0, owners on higher levels, ending in GC
    /// roots. Each instance contributes its shortest path to every distinct root (up to three), so an object held by both a
    /// cache and an event subscription shows on both branches. Static fields are shown as roots rather than as their
    /// runtime holder arrays.</summary>
    public MemoryRetentionFlow GetRetentionFlow(MemoryFlowRequest request, CancellationToken token = default)
    {
        var maxDepth = Math.Clamp(request.MaxDepth, 1, 12);
        var perLevel = Math.Clamp(request.MaxNodesPerLevel, 2, 20);
        int[] instances; string title, targetLabel, targetDetail; string? targetKey;
        if (request.ObjectId is int objectId)
        {
            ValidateObject(objectId);
            instances = [objectId];
            var type = _graph.Types[_graph.Objects[objectId].TypeId];
            title = $"{MemoryLabels.ShortType(type.Name)} @ 0x{_graph.Objects[objectId].Address:X}";
            targetLabel = MemoryLabels.ShortType(type.Name); targetDetail = type.Name; targetKey = type.Key;
        }
        else if (request.TypeKey is not null && TryGetTypeId(request.TypeKey, out var typeId))
        {
            instances = InstancesOf(typeId).ToArray();
            var type = _graph.Types[typeId];
            title = $"{instances.Length:N0} instances of {MemoryLabels.ShortType(type.Name)}";
            targetLabel = MemoryLabels.ShortType(type.Name); targetDetail = type.Name; targetKey = type.Key;
        }
        else return new("No type selected", [], [], 0, 0, 0, false);

        var hidden = request.HiddenRootKinds is { Count: > 0 } h ? h.ToHashSet(StringComparer.Ordinal) : null;
        var (reachable, parents) = FilteredWalk(hidden, token);
        var sampled = instances.Take(Math.Clamp(request.MaxInstances, 1, 50_000)).ToArray();
        var nodes = new Dictionary<string, FlowNodeBuilder>(StringComparer.Ordinal);
        var links = new Dictionary<(string From, string To), FlowLinkBuilder>();
        var target = Get("target", 0, targetLabel, targetDetail, "Target", targetKey);
        var unrooted = 0; var multiPathBudget = 1_000;
        var search = new PathSearch();
        foreach (var instance in sampled)
        {
            token.ThrowIfCancellationRequested();
            var bytes = RetainedBytes[instance] > 0 ? RetainedBytes[instance] : _graph.Objects[instance].Size;
            target.Add(instance, bytes, instance);
            if (!reachable[instance])
            {
                var hiddenOnly = Reachable[instance];
                if (!hiddenOnly) unrooted++;
                var terminal = hiddenOnly
                    ? Get("hidden", 1, "Hidden roots only", "Reachable only through root kinds hidden by the current filter.", "Hidden", null)
                    : Get("unrooted", 1, "No GC root", _graph.IsComplete ? "Not reachable from any GC root: already eligible for collection." : "No captured root path; reachability is unknown because the capture is incomplete.", "Unrooted", null);
                terminal.Add(instance, bytes, instance);
                Link(terminal, target, "", instance, bytes);
                continue;
            }
            // The largest instances get every distinct root; the long tail keeps its shortest path only.
            var paths = multiPathBudget-- > 0 ? InstancePaths(instance, hidden, maxPaths: 3, search, token) : [];
            if (paths.Count == 0) paths.Add(ShortestPath(instance, parents));
            foreach (var path in paths) AddPath(instance, bytes, path);
        }
        var truncated = sampled.Length < instances.Length;
        // Keep the largest nodes in each column readable; fold the rest into one "Other" node per column.
        var folded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var level in nodes.Values.GroupBy(n => n.Level).ToArray())
        {
            var ordered = level.OrderByDescending(n => n.Count).ThenByDescending(n => n.Bytes).ToArray();
            if (ordered.Length <= perLevel) continue;
            static bool Pinned(FlowNodeBuilder n) => n.Kind is "Target" or "Unrooted" or "Hidden";
            var keep = ordered.Where(Pinned).Concat(ordered.Where(n => !Pinned(n))).Take(perLevel - 1).ToHashSet();
            var fold = ordered.Where(n => !keep.Contains(n)).ToArray();
            var other = Get($"other|{level.Key}", level.Key, $"{fold.Length} other types", string.Join(", ", fold.Take(12).Select(n => n.Label)) + (fold.Length > 12 ? ", …" : ""), "Other", null);
            foreach (var node in fold) { other.Merge(node); folded[node.Key] = other.Key; nodes.Remove(node.Key); }
            truncated = true;
        }
        var merged = new Dictionary<(string From, string To), FlowLinkBuilder>();
        foreach (var link in links.Values)
        {
            var from = folded.GetValueOrDefault(link.From, link.From); var to = folded.GetValueOrDefault(link.To, link.To);
            if (!merged.TryGetValue((from, to), out var into)) merged[(from, to)] = into = new(from, to);
            into.Merge(link);
        }
        var ids = nodes.Values.OrderBy(n => n.Level).ThenByDescending(n => n.Count).Select((n, i) => (n, i)).ToDictionary(x => x.n.Key, x => x.i);
        return new(title,
            nodes.Values.OrderBy(n => ids[n.Key]).Select(n => new MemoryFlowNode(ids[n.Key], n.Level, n.Label, n.Detail, n.Kind, n.Count, n.Bytes, n.TypeKey, n.Sample)).ToArray(),
            merged.Values.Where(l => ids.ContainsKey(l.From) && ids.ContainsKey(l.To))
                .Select(l => new MemoryFlowLink(ids[l.From], ids[l.To], l.LabelText, l.Count, l.Bytes)).ToArray(),
            instances.Length, sampled.Length, unrooted, truncated);

        void AddPath(int instance, long bytes, FlowPath path)
        {
            var below = target;
            for (var i = 0; i < path.Edges.Count; i++)
            {
                var level = i + 1;
                var edge = _graph.Edges[path.Edges[i]];
                var label = FlowLabel(edge.LabelId);
                if (level > maxDepth)
                {
                    var more = Get($"more|{level}", level, "More owners…", $"The path continues beyond {maxDepth} owners. Inspect an owner to follow it further.", "Truncated", null);
                    more.Add(instance, bytes, edge.From);
                    Link(more, below, label, instance, bytes);
                    return;
                }
                var ownerType = _graph.Types[_graph.Objects[edge.From].TypeId];
                var owner = Get($"type|{level}|{ownerType.Key}", level, MemoryLabels.ShortType(ownerType.Name), ownerType.Name, "Owner", ownerType.Key);
                owner.Add(instance, bytes, edge.From);
                Link(owner, below, label, instance, bytes);
                below = owner;
            }
            var terminalLevel = path.Edges.Count + 1;
            if (terminalLevel > maxDepth + 1 || path.Terminal < 0) return;
            if (path.Terminal >= _graph.Edges.Length)
            {
                var root = _graph.Roots[path.Terminal - _graph.Edges.Length];
                var display = MemoryLabels.RootDisplay(root.Kind, root.Label);
                var isStatic = display.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal);
                var node = Get($"root|{terminalLevel}|{display}", terminalLevel, isStatic ? MemoryLabels.ShortStatic(display) : display,
                    isStatic ? display : $"{MemoryLabels.RootKindName(root.Kind)} · {root.Label}", isStatic ? "Static" : "Root", null);
                node.Add(instance, bytes, root.ObjectId);
                Link(node, below, "", instance, bytes);
            }
            else
            {
                var label = _graph.Label(_graph.Edges[path.Terminal].LabelId);
                var node = Get($"static|{terminalLevel}|{label}", terminalLevel, MemoryLabels.ShortStatic(label), label, "Static", null);
                node.Add(instance, bytes, _graph.Edges[path.Terminal].To);
                Link(node, below, "", instance, bytes);
            }
        }
        FlowNodeBuilder Get(string key, int level, string label, string detail, string kind, string? typeKey)
        {
            if (!nodes.TryGetValue(key, out var node)) nodes[key] = node = new(key, level, label, detail, kind, typeKey);
            return node;
        }
        void Link(FlowNodeBuilder from, FlowNodeBuilder to, string label, int instance, long bytes)
        {
            if (!links.TryGetValue((from.Key, to.Key), out var link)) links[(from.Key, to.Key)] = link = new(from.Key, to.Key);
            link.Add(instance, bytes, label);
        }
    }

    /// <summary>Owner edges from the instance upward (each edge's From is the next owner) and the terminal: a root
    /// reference id, or a static-field edge whose holder array is not shown.</summary>
    private sealed record FlowPath(List<int> Edges, int Terminal);

    private FlowPath ShortestPath(int instance, int[] parents)
    {
        var edges = new List<int>(); var at = instance;
        while (edges.Count < 1024)
        {
            var reference = parents[at];
            if (reference < 0 || reference >= _graph.Edges.Length) return new(edges, reference);
            if (IsStaticEdge(_graph.Edges[reference])) return new(edges, reference);
            edges.Add(reference); at = _graph.Edges[reference].From;
        }
        return new(edges, parents[at]);
    }

    /// <summary>Breadth-first search upward from one instance, collecting the shortest path to each distinct root (by
    /// display name), longest-lived first. Bounded per instance.</summary>
    private List<FlowPath> InstancePaths(int instance, HashSet<string>? hidden, int maxPaths, PathSearch search, CancellationToken token)
    {
        // The collections are reused across the instances of one request.
        var (via, queue, labels) = (search.Via, search.Queue, search.Labels);
        via.Clear(); queue.Clear(); labels.Clear();
        via[instance] = -1; queue.Enqueue(instance);
        var found = new List<(FlowPath Path, string Label, int Priority)>();
        while (queue.TryDequeue(out var node) && labels.Count < maxPaths * 2 && via.Count < 4_000)
        {
            foreach (var root in _graph.RootsOf(node))
            {
                var info = _graph.Roots[root];
                if (hidden is not null && hidden.Contains(info.Kind)) continue;
                var display = MemoryLabels.RootDisplay(info.Kind, info.Label);
                if (labels.Add(display)) found.Add((new(Chain(node), _graph.Edges.Length + root), display, MemoryLabels.RootPriority(info.Kind, info.Label)));
            }
            foreach (var edgeId in _graph.Incoming.For(node))
            {
                var edge = _graph.Edges[edgeId];
                if (!Reachable[edge.From]) continue;
                if (IsStaticEdge(edge))
                {
                    var label = _graph.Labels[edge.LabelId];
                    if (labels.Add(label)) found.Add((new(Chain(node), edgeId), label, 0));
                    continue;
                }
                if (via.ContainsKey(edge.From)) continue;
                via[edge.From] = edgeId; queue.Enqueue(edge.From);
            }
        }
        token.ThrowIfCancellationRequested();
        return found.OrderBy(f => f.Priority).ThenBy(f => f.Path.Edges.Count).Take(maxPaths).Select(f => f.Path).ToList();

        List<int> Chain(int top)
        {
            // Edges from the instance upward to `top`.
            var chain = new List<int>();
            for (var at = top; at != instance && chain.Count < 1024; at = _graph.Edges[via[at]].To) chain.Add(via[at]);
            chain.Reverse();
            return chain;
        }
    }

    private sealed class PathSearch
    {
        public Dictionary<int, int> Via { get; } = [];
        public Queue<int> Queue { get; } = new();
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Array slots aggregate as one label so a Sankey band reads "[…]" rather than listing indexes.</summary>
    private string FlowLabel(int labelId) => HeapGraph.IsArrayIndexLabel(labelId) ? "[…]" : FlowLabel(_graph.Labels[labelId]);

    private static string FlowLabel(string label) =>
        label.Length > 2 && label[0] == '[' && label[^1] == ']' && label.AsSpan(1, label.Length - 2).IndexOfAnyExceptInRange('0', '9') < 0 ? "[…]" : label;

    private sealed class FlowNodeBuilder(string key, int level, string label, string detail, string kind, string? typeKey)
    {
        private readonly HashSet<int> _instances = [];
        private long _sampleBytes = -1;
        public string Key => key; public int Level => level; public string Label => label; public string Detail => detail;
        public string Kind => kind; public string? TypeKey => typeKey;
        public int Count => _instances.Count;
        public long Bytes { get; private set; }
        public int? Sample { get; private set; }
        public void Add(int instance, long bytes, int sample)
        {
            if (_instances.Add(instance)) Bytes += bytes;
            if (bytes > _sampleBytes) { _sampleBytes = bytes; Sample = sample; }
        }
        public void Merge(FlowNodeBuilder other)
        {
            foreach (var instance in other._instances) _instances.Add(instance);
            Bytes += other.Bytes;
            if (other._sampleBytes > _sampleBytes) { _sampleBytes = other._sampleBytes; Sample = other.Sample; }
        }
    }

    private sealed class FlowLinkBuilder(string from, string to)
    {
        private readonly Dictionary<int, long> _instances = [];
        public string From => from; public string To => to;
        public int Count => _instances.Count;
        public long Bytes { get; private set; }
        public Dictionary<string, int> Labels { get; } = new(StringComparer.Ordinal);
        public void Add(int instance, long bytes, string label)
        {
            if (_instances.TryAdd(instance, bytes)) Bytes += bytes;
            if (label.Length > 0) Labels[label] = Labels.GetValueOrDefault(label) + 1;
        }
        public void Merge(FlowLinkBuilder other)
        {
            foreach (var (instance, bytes) in other._instances) if (_instances.TryAdd(instance, bytes)) Bytes += bytes;
            foreach (var (name, uses) in other.Labels) Labels[name] = Labels.GetValueOrDefault(name) + uses;
        }
        public string LabelText => Labels.Count == 0 ? "" : string.Join(", ", Labels.OrderByDescending(l => l.Value).Take(2).Select(l => l.Key)) +
            (Labels.Count > 2 ? $" +{Labels.Count - 2}" : "");
    }
}
