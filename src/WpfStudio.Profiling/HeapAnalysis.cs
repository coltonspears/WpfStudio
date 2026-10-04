using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Reachability, immediate dominators, and counterfactual reference removal on an immutable heap.
/// Uses the Lengauer-Tarjan algorithm and compact adjacency indexes; never enumerates every root path.</summary>
public sealed partial class HeapAnalysis
{
    private readonly HeapGraph _graph;
    private readonly int[] _parentReference;
    private readonly bool[] _pinned;
    private readonly int[] _rankedObjects;
    // Dominator tree as a compact child index. Slot Objects.Length is the virtual root (the GC roots).
    private readonly int[] _dominatorOffsets;
    private readonly int[] _dominatorChildren;
    private readonly long[] _typeRetainedBytes;
    private readonly Dictionary<string, int> _typeIds;
    // Static fields live in runtime object[] holders. Their slots carry "Static Type.Field" labels.
    private readonly bool[] _staticLabel;
    private readonly bool[] _staticsHolder;
    private readonly Dictionary<string, (bool[] Reachable, int[] Parent)> _filteredWalks = new(StringComparer.Ordinal);
    public bool[] Reachable { get; }
    public int[] ImmediateDominators { get; }
    public long[] RetainedBytes { get; }
    public int[] RetainedObjects { get; }
    public HeapGraph Graph => _graph;

    public HeapAnalysis(HeapGraph graph, CancellationToken cancellationToken = default)
    {
        _graph = graph;
        var count = graph.Objects.Length;
        _staticLabel = graph.Labels.Select(l => l.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal)).ToArray();
        _staticsHolder = new bool[count];
        foreach (var edge in graph.Edges) if (_staticLabel[edge.LabelId]) _staticsHolder[edge.From] = true;
        (Reachable, _parentReference) = Walk(null, null, null, cancellationToken);
        (ImmediateDominators, RetainedBytes, RetainedObjects) = ComputeDominators(cancellationToken);
        foreach (var permanent in graph.Roots.Where(r => r.IsPermanent))
        { RetainedBytes[permanent.ObjectId] = 0; RetainedObjects[permanent.ObjectId] = 0; }
        foreach (var edge in graph.Edges.Where(e => IsFrozenReference(e) && Reachable[e.From]))
        { RetainedBytes[edge.To] = 0; RetainedObjects[edge.To] = 0; }
        _pinned = new bool[count];
        foreach (var root in graph.Roots) if (root.IsPinned) _pinned[root.ObjectId] = true;
        _rankedObjects = Enumerable.Range(0, count).OrderByDescending(i => RetainedBytes[i])
            .ThenByDescending(i => graph.Objects[i].Size).ToArray();
        (_dominatorOffsets, _dominatorChildren) = BuildDominatorTree(cancellationToken);
        _typeRetainedBytes = ComputeTypeRetained(cancellationToken);
        _typeIds = new(StringComparer.Ordinal);
        for (var i = 0; i < graph.Types.Length; i++) _typeIds.TryAdd(graph.Types[i].Key, i);
    }

    public IReadOnlyList<MemoryTypeSummary> SummarizeTypes() => _graph.Objects.Select((o, i) => (o, i))
        .GroupBy(x => x.o.TypeId).Select(g =>
        {
            var type = _graph.Types[g.Key];
            long reachableBytes = 0; var reachable = 0; long largest = 0, bytes = 0;
            foreach (var (o, i) in g)
            {
                bytes += o.Size; largest = Math.Max(largest, RetainedBytes[i]);
                if (Reachable[i]) { reachable++; reachableBytes += o.Size; }
            }
            return new MemoryTypeSummary(type.Key, type.Name, type.Module, g.Count(), bytes, reachable, largest,
                _typeRetainedBytes[g.Key], reachableBytes);
        }).OrderByDescending(t => t.Bytes).ToArray();

    public IReadOnlyList<MemoryGenerationSummary> SummarizeGenerations()
    {
        var order = new[] { "Generation0", "Generation1", "Generation2", "Large", "Pinned", "Frozen", "Unknown" };
        return _graph.Objects.GroupBy(o => o.Generation).Select(g => new MemoryGenerationSummary(g.Key, g.Count(), g.Sum(o => o.Size)))
            .OrderBy(g => Array.IndexOf(order, g.Generation) is var index && index < 0 ? order.Length : index).ToArray();
    }

    public IReadOnlyList<MemoryRootKindSummary> SummarizeRootKinds() => _graph.Roots.GroupBy(r => r.Kind)
        .Select(g => new MemoryRootKindSummary(g.Key, g.Count(), g.Select(r => r.ObjectId).Distinct().Count()))
        .OrderBy(r => MemoryLabels.RootPriority(r.Kind, "")).ThenByDescending(r => r.Count).ToArray();

    public MemoryObjectInfo Describe(int id)
    {
        ValidateObject(id);
        var obj = _graph.Objects[id]; var type = _graph.Types[obj.TypeId];
        return new(id, $"0x{obj.Address:X}", type.Key, type.Name, type.Module, obj.Size,
            RetainedBytes[id], Reachable[id], obj.Generation, _pinned[id], RetainedObjects[id]);
    }

    public MemoryReferenceInfo DescribeReference(int id)
    {
        if ((uint)id < _graph.Edges.Length)
        {
            var edge = _graph.Edges[id];
            return new(id, edge.From, edge.To, ObjectLabel(edge.From), ObjectLabel(edge.To),
                _graph.Labels[edge.LabelId], IsFrozenReference(edge) ? "Frozen object reference" : edge.IsDependent ? "Dependent handle (key → value)" : "Strong reference", false, false,
                IsPermanent: IsFrozenReference(edge));
        }
        var index = id - _graph.Edges.Length;
        if ((uint)index >= _graph.Roots.Length) throw new ArgumentOutOfRangeException(nameof(id));
        var root = _graph.Roots[index];
        return new(id, null, root.ObjectId, root.Label, ObjectLabel(root.ObjectId), root.Label, root.Kind, true, root.IsPinned, root.IsPermanent);
    }

    public MemoryObjectPage GetObjects(MemoryObjectQuery query, CancellationToken token = default)
    {
        var found = new List<MemoryObjectInfo>(); var total = 0;
        var skip = Math.Max(0, query.Skip); var take = Math.Clamp(query.Take, 1, 500);
        var search = query.Search.Trim();
        foreach (var i in _rankedObjects)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var obj = _graph.Objects[i]; var type = _graph.Types[obj.TypeId];
            if (query.TypeKey is not null && type.Key != query.TypeKey || query.ReachableOnly && !Reachable[i]) continue;
            if (search.Length != 0 && !type.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !$"0x{obj.Address:X}".Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (total++ >= skip && found.Count < take) found.Add(Describe(i));
        }
        return new(total, found);
    }

    /// <summary>Root-path examples, longest-lived owners first: static fields and handles before stacks and the finalizer queue.</summary>
    public (MemoryRootPath[] Paths, bool Truncated) GetRootPaths(int objectId, CancellationToken token = default) =>
        GetRootPaths(objectId, null, token);

    public (MemoryRootPath[] Paths, bool Truncated) GetRootPaths(int objectId, IReadOnlyCollection<string>? hiddenKinds, CancellationToken token = default)
    {
        ValidateObject(objectId);
        if (!Reachable[objectId]) return ([], false);
        const int display = 8, candidates = 24;
        var paths = new List<MemoryRootPath>();
        var next = new Dictionary<int, int> { [objectId] = -1 };
        var queue = new Queue<int>(); queue.Enqueue(objectId);
        var truncated = false;
        while (queue.TryDequeue(out var node) && paths.Count < candidates)
        {
            token.ThrowIfCancellationRequested();
            if (_graph.RootIndexes.TryGetValue(node, out var roots))
                foreach (var root in roots)
                {
                    if (hiddenKinds is not null && hiddenKinds.Contains(_graph.Roots[root].Kind)) continue;
                    if (paths.Count == candidates) { truncated = true; break; }
                    var refs = new List<MemoryReferenceInfo> { DescribeReference(_graph.Edges.Length + root) };
                    var at = node;
                    while (at != objectId && refs.Count < 128) { var e = next[at]; refs.Add(DescribeReference(e)); at = _graph.Edges[e].To; }
                    if (at == objectId) paths.Add(new(refs)); else truncated = true;
                }
            foreach (var edgeId in _graph.Incoming.For(node))
            {
                var edge = _graph.Edges[edgeId]; var from = edge.From;
                if (!Reachable[from]) continue;
                // A static field ends a path on its own, so two statics in the same runtime holder are two paths.
                if (_staticLabel[edge.LabelId] && _graph.RootIndexes.TryGetValue(from, out var holderRoots))
                {
                    var holderRoot = holderRoots.FirstOrDefault(r => hiddenKinds is null || !hiddenKinds.Contains(_graph.Roots[r].Kind), -1);
                    if (holderRoot < 0) continue;
                    if (paths.Count == candidates) { truncated = true; continue; }
                    var refs = new List<MemoryReferenceInfo> { DescribeReference(_graph.Edges.Length + holderRoot), DescribeReference(edgeId) };
                    var at = node;
                    while (at != objectId && refs.Count < 128) { var e = next[at]; refs.Add(DescribeReference(e)); at = _graph.Edges[e].To; }
                    if (at == objectId) paths.Add(new(refs)); else truncated = true;
                    continue;
                }
                if (next.ContainsKey(from)) continue;
                if (next.Count >= 50_000) { truncated = true; continue; }
                next.Add(from, edgeId); queue.Enqueue(from);
            }
        }
        var ordered = paths.OrderBy(PathPriority).ThenBy(p => p.References.Count).Take(display).ToArray();
        return (ordered, truncated || queue.Count > 0 || paths.Count > display);
    }

    private static int PathPriority(MemoryRootPath path) =>
        path.References.Any(r => r.Label.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal)) ? 0
            : MemoryLabels.RootPriority(path.References[0].Kind, path.References[0].Label);

    public MemoryObjectDetails Inspect(int id, string preview, IReadOnlyList<MemoryFieldInfo> fields,
        bool fieldsTruncated, CancellationToken token = default)
    {
        var obj = Describe(id);
        var incoming = _graph.Incoming.For(id).ToArray(); var outgoing = _graph.Outgoing.For(id).ToArray();
        var directRoots = _graph.RootIndexes.TryGetValue(id, out var r) ? r : [];
        var (paths, truncated) = GetRootPaths(id, token);
        var dominators = new List<MemoryObjectInfo>();
        for (var d = ImmediateDominators[id]; d >= 0 && dominators.Count < 64; d = ImmediateDominators[d]) dominators.Add(Describe(d));
        var evidence = new List<string>();
        if (obj.Generation == "Frozen") evidence.Add("This object occupies a frozen segment. It remains alive independently of ordinary references and cannot be released by clearing an owner.");
        if (!obj.IsReachable) evidence.Add(_graph.IsComplete
            ? "No strong GC-root path was found. This object is already eligible for collection in the captured graph."
            : "No captured root path was found. Incomplete data cannot establish that this object is collectible.");
        if (obj.IsPinned) evidence.Add("A pinned GC root points to this object. Pinning can prevent compaction while the root remains.");
        if (incoming.Length + directRoots.Length > 1) evidence.Add("Multiple incoming references exist. Removing one may leave another strong root path.");
        if (paths.SelectMany(p => p.References).Any(e => e.FromId is int owner && _graph.Types[_graph.Objects[owner].TypeId].IsDelegate ||
            e.Label.Contains("invocation", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("A root path passes through a delegate. Check event subscriptions and captured closures against the expected lifetime.");
        if (paths.SelectMany(p => p.References).Any(e => e.Owner.Contains("Timer", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("A root path passes through a timer. Check whether it should have been stopped or disposed.");
        if (paths.SelectMany(p => p.References).Any(e => e.Label.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal)))
            evidence.Add("A static field occurs on a captured root path. Check cache and singleton ownership against the expected lifetime.");
        if (paths.Length > 0 && paths.All(p => p.References[0].Kind.Contains("Finalizer", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("A finalizer root retains this object. It may remain alive until finalization completes.");
        else if (paths.Any(p => p.References[0].Kind.Contains("Finalizer", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("A finalizer root also reaches this object. It may remain alive until finalization completes.");
        if (!_graph.IsComplete) evidence.Add("Heap coverage is incomplete. Retained sizes and removal estimates describe only the captured graph.");
        evidence.Add("Retention is an observation, not proof of a leak. Repeat the workload and compare after the expected owner lifetime ends.");
        return new(obj, preview, fields,
            directRoots.Select(i => DescribeReference(_graph.Edges.Length + i)).Concat(incoming.Select(DescribeReference)).Take(200).ToArray(),
            outgoing.Take(200).Select(DescribeReference).ToArray(), paths, dominators, evidence,
            incoming.Length + directRoots.Length, outgoing.Length, fieldsTruncated, truncated);
    }

    public MemoryReleaseEstimate EstimateRelease(MemoryReleaseRequest request, CancellationToken token = default)
    {
        ValidateObject(request.ObjectId);
        if (request.ReferenceId is int reference)
        {
            var edge = DescribeReference(reference);
            if (edge.ToId != request.ObjectId && edge.FromId != request.ObjectId)
                throw new ArgumentException("The reference does not belong to the selected object.");
            if (edge.IsPermanent) throw new ArgumentException("Frozen segment roots and frozen-object references cannot be removed. Choose a mutable owner slot instead.");
        }
        // A single edge is removed by slot ID. Releasing an object models severing ALL incoming
        // references and roots to it, never mutating memory in the actual target.
        var (after, parent) = Walk(request.ReferenceId is null ? request.ObjectId : null, request.ReferenceId, null, token);
        var released = new List<int>(); long bytes = 0;
        for (var i = 0; i < after.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!Reachable[i] || after[i]) continue;
            released.Add(i); bytes = checked(bytes + _graph.Objects[i].Size);
        }
        var types = released.GroupBy(i => _graph.Objects[i].TypeId).Select(g =>
            new MemoryReleasedType(_graph.Types[g.Key].Name, g.Count(), g.Sum(i => _graph.Objects[i].Size), _graph.Types[g.Key].Key))
            .OrderByDescending(t => t.Bytes).Take(100).ToArray();
        var remaining = after[request.ObjectId] ? PathFromParents(request.ObjectId, parent) : null;
        var explanation = request.ReferenceId is null
            ? "Models removing every removable incoming strong reference and GC root to the selected object. Shared children remain alive when another root still reaches them."
            : "Models removing only the selected reference slot. Other references between the same objects and alternate GC-root paths remain intact.";
        explanation += " Eligible managed bytes are not a prediction of working-set reduction; finalizers and runtime heap policy can delay reclamation.";
        explanation += " Frozen objects remain permanently rooted.";
        if (!_graph.IsComplete) explanation += " The captured graph is incomplete, so this estimate is provisional and can overstate reclaimable memory.";
        return new(bytes, released.Count, _graph.IsComplete, after[request.ObjectId], explanation, types,
            released.OrderByDescending(i => _graph.Objects[i].Size).Take(200).ToArray(), remaining);
    }

    /// <summary>A bounded neighbourhood for the vertical retention graph: root-path examples above the object,
    /// its largest direct owners and its largest referenced objects. Further hops are expanded on demand.</summary>
    public MemoryGraph BuildGraph(MemoryGraphRequest request, CancellationToken token = default)
    {
        ValidateObject(request.ObjectId);
        var limit = Math.Clamp(request.MaxNodes, 10, 200);
        var hidden = request.HiddenRootKinds is { Count: > 0 } h ? h.ToHashSet(StringComparer.Ordinal) : null;
        var columns = new Dictionary<int, int> { [request.ObjectId] = 0 };
        var (paths, truncated) = GetRootPaths(request.ObjectId, hidden, token);
        foreach (var path in paths)
            for (var i = path.References.Count - 1; i >= 0; i--)
            {
                var id = path.References[i].ToId;
                if (columns.ContainsKey(id)) continue;
                if (columns.Count >= limit) { truncated = true; break; }
                columns.Add(id, Math.Max(-16, i - path.References.Count + 1));
            }
        var owners = Neighbors(request.ObjectId, incoming: true);
        foreach (var owner in owners.Take(Math.Clamp(request.MaxOwners, 0, 64)))
        {
            if (columns.ContainsKey(owner)) continue;
            if (columns.Count >= limit) { truncated = true; break; }
            columns.Add(owner, -1);
        }
        if (owners.Count > request.MaxOwners) truncated = true;
        var children = Neighbors(request.ObjectId, incoming: false);
        foreach (var child in children.Take(Math.Clamp(request.MaxChildren, 0, 64)))
        {
            if (columns.ContainsKey(child)) continue;
            if (columns.Count >= limit) { truncated = true; break; }
            columns.Add(child, 1);
        }
        if (children.Count > request.MaxChildren) truncated = true;
        token.ThrowIfCancellationRequested();
        var edgeIds = ReferencesAmong(columns.Keys, hidden, ref truncated);
        var hiddenNote = hidden is null ? "" : $" Root kinds hidden: {string.Join(", ", hidden.Select(MemoryLabels.RootKindName))}.";
        return new(columns.Select(kv => Node(kv.Key, kv.Value, kv.Key == request.ObjectId)).ToArray(), edgeIds.Select(DescribeReference).ToArray(), truncated,
            $"{columns.Count} objects shown. Roots are at the top; arrows point from owner to referenced object.{hiddenNote}");
    }

    /// <summary>One hop of owners or referenced objects, for expanding a node in the graph.</summary>
    public MemoryGraph GetNeighbors(MemoryNeighborRequest request, CancellationToken token = default)
    {
        ValidateObject(request.ObjectId);
        var take = Math.Clamp(request.Take, 1, 100);
        var all = Neighbors(request.ObjectId, request.Incoming);
        var ids = new List<int> { request.ObjectId };
        ids.AddRange(all.Take(take));
        var truncated = all.Count > take;
        var edges = ReferencesAmong(ids, null, ref truncated);
        // Keep only the slots that connect the expanded object, plus its own roots when expanding owners.
        var connected = edges.Where(e => e >= _graph.Edges.Length
            ? request.Incoming && _graph.Roots[e - _graph.Edges.Length].ObjectId == request.ObjectId
            : request.Incoming ? _graph.Edges[e].To == request.ObjectId : _graph.Edges[e].From == request.ObjectId).ToArray();
        return new(ids.Select(id => Node(id, id == request.ObjectId ? 0 : request.Incoming ? -1 : 1, false)).ToArray(),
            connected.Select(DescribeReference).ToArray(), truncated,
            $"{all.Count:N0} {(request.Incoming ? "owners" : "referenced objects")}; showing {Math.Min(take, all.Count):N0}, largest retained first.");
    }

    private MemoryGraphNode Node(int id, int column, bool focus)
    {
        var degreeIn = _graph.Incoming.Offsets[id + 1] - _graph.Incoming.Offsets[id] +
            (_graph.RootIndexes.TryGetValue(id, out var roots) ? roots.Length : 0);
        var degreeOut = _graph.Outgoing.Offsets[id + 1] - _graph.Outgoing.Offsets[id];
        return new(Describe(id), column, focus, _graph.RootIndexes.ContainsKey(id), degreeIn, degreeOut);
    }

    private List<int> Neighbors(int id, bool incoming)
    {
        var set = new HashSet<int>();
        foreach (var edgeId in (incoming ? _graph.Incoming : _graph.Outgoing).For(id))
        {
            var edge = _graph.Edges[edgeId]; var other = incoming ? edge.From : edge.To;
            if (other != id) set.Add(other);
        }
        return set.OrderByDescending(n => Reachable[n]).ThenByDescending(n => RetainedBytes[n]).ThenByDescending(n => _graph.Objects[n].Size).ToList();
    }

    private List<int> ReferencesAmong(IEnumerable<int> nodes, HashSet<string>? hiddenKinds, ref bool truncated)
    {
        var set = nodes as ICollection<int> ?? nodes.ToList();
        var lookup = set as HashSet<int> ?? set.ToHashSet();
        var edgeIds = new List<int>();
        foreach (var node in lookup)
        {
            foreach (var edgeId in _graph.Outgoing.For(node))
                if (lookup.Contains(_graph.Edges[edgeId].To))
                { if (edgeIds.Count < 400) edgeIds.Add(edgeId); else truncated = true; }
            if (_graph.RootIndexes.TryGetValue(node, out var roots))
                foreach (var root in roots)
                {
                    if (hiddenKinds is not null && hiddenKinds.Contains(_graph.Roots[root].Kind)) continue;
                    if (edgeIds.Count < 400) edgeIds.Add(_graph.Edges.Length + root); else truncated = true;
                }
        }
        return edgeIds;
    }

    private (bool[] Seen, int[] Parent) Walk(int? removedObject, int? removedReference, IReadOnlySet<string>? hiddenKinds, CancellationToken token)
    {
        var seen = new bool[_graph.Objects.Length];
        var parent = new int[seen.Length]; Array.Fill(parent, -1);
        var queue = new Queue<int>();
        // Visit longer-lived roots first so the shortest-path tree prefers statics and handles over stacks.
        foreach (var i in RootOrder)
        {
            var root = _graph.Roots[i]; var id = root.ObjectId;
            if (hiddenKinds is not null && hiddenKinds.Contains(root.Kind)) continue;
            if ((!root.IsPermanent && (id == removedObject || _graph.Edges.Length + i == removedReference)) || seen[id]) continue;
            seen[id] = true; parent[id] = _graph.Edges.Length + i; queue.Enqueue(id);
        }
        var iterations = 0;
        while (queue.TryDequeue(out var node))
        {
            if ((iterations++ & 1023) == 0) token.ThrowIfCancellationRequested();
            foreach (var e in _graph.Outgoing.For(node))
            {
                var to = _graph.Edges[e].To;
                if (e == removedReference || (to == removedObject && !IsFrozenReference(_graph.Edges[e])) || seen[to]) continue;
                seen[to] = true; parent[to] = e; queue.Enqueue(to);
            }
        }
        return (seen, parent);
    }

    private int[]? _rootOrder;
    private int[] RootOrder => _rootOrder ??= Enumerable.Range(0, _graph.Roots.Length)
        .OrderBy(i => MemoryLabels.RootPriority(_graph.Roots[i].Kind, _graph.Roots[i].Label)).ToArray();

    /// <summary>Reachability with some root kinds ignored (e.g. the finalizer queue), cached per kind set.</summary>
    internal (bool[] Reachable, int[] Parent) FilteredWalk(IReadOnlyCollection<string>? hiddenKinds, CancellationToken token)
    {
        if (hiddenKinds is null || hiddenKinds.Count == 0) return (Reachable, _parentReference);
        var key = string.Join("|", hiddenKinds.Order(StringComparer.Ordinal));
        lock (_filteredWalks)
        {
            if (_filteredWalks.TryGetValue(key, out var cached)) return cached;
            var walk = Walk(null, null, hiddenKinds.ToHashSet(StringComparer.Ordinal), token);
            if (_filteredWalks.Count > 8) _filteredWalks.Clear();
            _filteredWalks[key] = walk;
            return walk;
        }
    }

    public MemoryRootPath? GetShortestRootPath(int objectId) { ValidateObject(objectId); return PathFromParents(objectId, _parentReference); }
    internal int ParentReference(int objectId) => _parentReference[objectId];
    private MemoryRootPath? PathFromParents(int objectId, int[] parents)
    {
        if (parents[objectId] < 0) return null;
        var refs = new List<MemoryReferenceInfo>(); var at = objectId;
        while (refs.Count < 1024)
        {
            var e = DescribeReference(parents[at]); refs.Add(e);
            if (e.FromId is not int from) { refs.Reverse(); return new(refs); }
            at = from;
        }
        return null; // Do not display an unrooted fragment as a complete root path.
    }

    internal bool TryGetTypeId(string key, out int typeId) => _typeIds.TryGetValue(key, out typeId);
    private string ObjectLabel(int id) => _graph.Types[_graph.Objects[id].TypeId].Name + $" @ 0x{_graph.Objects[id].Address:X}";
    private bool IsFrozenReference(HeapEdge edge) => !edge.IsDependent && _graph.Objects[edge.From].Generation == "Frozen";
    private void ValidateObject(int id) { if ((uint)id >= _graph.Objects.Length) throw new ArgumentOutOfRangeException(nameof(id)); }

    private (int[] Dominators, long[] Bytes, int[] Counts) ComputeDominators(CancellationToken token)
    {
        var count = _graph.Objects.Length; var root = count;
        var number = new int[count + 1]; var vertex = new int[count + 2]; var parent = new int[count + 1];
        var semi = new int[count + 1]; var ancestor = new int[count + 1]; var label = new int[count + 1];
        var idom = new int[count + 1]; var bucketHead = new int[count + 1]; var bucketNext = new int[count + 1];
        Array.Fill(ancestor, -1); Array.Fill(idom, -1); Array.Fill(bucketHead, -1); Array.Fill(bucketNext, -1);
        var stackNodes = new int[count + 1]; var stackNext = new int[count + 1]; var top = 0; var visited = 1;
        number[root] = 1; semi[root] = 1; label[root] = root; vertex[1] = root; stackNodes[0] = root;
        while (top >= 0)
        {
            if ((visited & 1023) == 0) token.ThrowIfCancellationRequested();
            var node = stackNodes[top]; var next = stackNext[top]++;
            var degree = node == root ? _graph.Roots.Length : _graph.Outgoing.Offsets[node + 1] - _graph.Outgoing.Offsets[node];
            if (next >= degree) { top--; continue; }
            var to = node == root ? _graph.Roots[next].ObjectId : _graph.Edges[_graph.Outgoing.Indexes[_graph.Outgoing.Offsets[node] + next]].To;
            if (number[to] != 0) continue;
            number[to] = semi[to] = ++visited; vertex[visited] = to; label[to] = to; parent[to] = node;
            stackNodes[++top] = to; stackNext[top] = 0;
        }
        var compression = stackNodes; // DFS is finished; reuse its storage for iterative path compression.
        for (var i = visited; i > 1; i--)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var w = vertex[i];
            foreach (var e in _graph.Incoming.For(w))
            {
                var pred = _graph.Edges[e].From;
                if (number[pred] == 0) continue;
                semi[w] = Math.Min(semi[w], semi[Evaluate(pred)]);
            }
            if (_graph.RootIndexes.ContainsKey(w)) semi[w] = 1;
            var s = vertex[semi[w]]; bucketNext[w] = bucketHead[s]; bucketHead[s] = w;
            ancestor[w] = parent[w];
            var v = bucketHead[parent[w]]; bucketHead[parent[w]] = -1;
            while (v >= 0)
            {
                var u = Evaluate(v); idom[v] = semi[u] < semi[v] ? u : parent[w]; v = bucketNext[v];
            }
        }
        for (var i = 2; i <= visited; i++)
        {
            var w = vertex[i]; if (idom[w] != vertex[semi[w]]) idom[w] = idom[idom[w]];
        }
        var bytes = new long[count + 1]; var counts = new int[count + 1];
        for (var i = visited; i > 1; i--)
        {
            var w = vertex[i]; bytes[w] = checked(bytes[w] + _graph.Objects[w].Size); counts[w]++;
            bytes[idom[w]] = checked(bytes[idom[w]] + bytes[w]); counts[idom[w]] += counts[w];
        }
        for (var i = 0; i < count; i++) if (idom[i] == root) idom[i] = -1;
        return (idom[..count], bytes[..count], counts[..count]);

        int Evaluate(int v)
        {
            if (ancestor[v] < 0) return label[v];
            var at = v; var n = 0;
            while (ancestor[at] >= 0 && ancestor[ancestor[at]] >= 0) { compression[n++] = at; at = ancestor[at]; }
            for (var j = n - 1; j >= 0; j--)
            {
                at = compression[j]; var a = ancestor[at];
                if (semi[label[a]] < semi[label[at]]) label[at] = label[a];
                ancestor[at] = ancestor[a];
            }
            return label[v];
        }
    }
}
