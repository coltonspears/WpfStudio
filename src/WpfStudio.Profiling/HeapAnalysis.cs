using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Reachability, immediate dominators, and counterfactual reference removal on an immutable heap.
/// Uses the Lengauer-Tarjan algorithm and compact adjacency indexes; never enumerates every root path.</summary>
public sealed class HeapAnalysis
{
    private readonly HeapGraph _graph;
    private readonly int[] _parentReference;
    private readonly bool[] _pinned;
    private readonly int[] _rankedObjects;
    public bool[] Reachable { get; }
    public int[] ImmediateDominators { get; }
    public long[] RetainedBytes { get; }
    public int[] RetainedObjects { get; }

    public HeapAnalysis(HeapGraph graph, CancellationToken cancellationToken = default)
    {
        _graph = graph;
        var count = graph.Objects.Length;
        (Reachable, _parentReference) = Walk(null, null, cancellationToken);
        (ImmediateDominators, RetainedBytes, RetainedObjects) = ComputeDominators(cancellationToken);
        foreach (var permanent in graph.Roots.Where(r => r.IsPermanent))
        { RetainedBytes[permanent.ObjectId] = 0; RetainedObjects[permanent.ObjectId] = 0; }
        foreach (var edge in graph.Edges.Where(e => IsFrozenReference(e) && Reachable[e.From]))
        { RetainedBytes[edge.To] = 0; RetainedObjects[edge.To] = 0; }
        _pinned = new bool[count];
        foreach (var root in graph.Roots) if (root.IsPinned) _pinned[root.ObjectId] = true;
        _rankedObjects = Enumerable.Range(0, count).OrderByDescending(i => RetainedBytes[i])
            .ThenByDescending(i => graph.Objects[i].Size).ToArray();
    }

    public IReadOnlyList<MemoryTypeSummary> SummarizeTypes() => _graph.Objects.Select((o, i) => (o, i))
        .GroupBy(x => x.o.TypeId).Select(g =>
        {
            var type = _graph.Types[g.Key];
            return new MemoryTypeSummary(type.Key, type.Name, type.Module, g.Count(), g.Sum(x => x.o.Size),
                g.Count(x => Reachable[x.i]), g.Max(x => RetainedBytes[x.i]));
        }).OrderByDescending(t => t.Bytes).ToArray();

    public MemoryObjectInfo Describe(int id)
    {
        ValidateObject(id);
        var obj = _graph.Objects[id]; var type = _graph.Types[obj.TypeId];
        return new(id, $"0x{obj.Address:X}", type.Key, type.Name, type.Module, obj.Size,
            RetainedBytes[id], Reachable[id], obj.Generation, _pinned[id]);
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

    public (MemoryRootPath[] Paths, bool Truncated) GetRootPaths(int objectId, CancellationToken token = default)
    {
        ValidateObject(objectId);
        if (!Reachable[objectId]) return ([], false);
        var paths = new List<MemoryRootPath>();
        var next = new Dictionary<int, int> { [objectId] = -1 };
        var queue = new Queue<int>(); queue.Enqueue(objectId);
        var truncated = false;
        while (queue.TryDequeue(out var node) && paths.Count < 8)
        {
            token.ThrowIfCancellationRequested();
            if (_graph.RootIndexes.TryGetValue(node, out var roots))
                foreach (var root in roots)
                {
                    if (paths.Count == 8) { truncated = true; break; }
                    var refs = new List<MemoryReferenceInfo> { DescribeReference(_graph.Edges.Length + root) };
                    var at = node;
                    while (at != objectId && refs.Count < 128) { var e = next[at]; refs.Add(DescribeReference(e)); at = _graph.Edges[e].To; }
                    if (at == objectId) paths.Add(new(refs)); else truncated = true;
                }
            foreach (var edgeId in _graph.Incoming.For(node))
            {
                var from = _graph.Edges[edgeId].From;
                if (!Reachable[from] || next.ContainsKey(from)) continue;
                if (next.Count >= 50_000) { truncated = true; continue; }
                next.Add(from, edgeId); queue.Enqueue(from);
            }
        }
        return (paths.ToArray(), truncated || queue.Count > 0);
    }

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
        if (paths.SelectMany(p => p.References).Any(e => e.Label.StartsWith("Static ", StringComparison.Ordinal)))
            evidence.Add("A static field occurs on a captured root path. Check cache and singleton ownership against the expected lifetime.");
        if (paths.Any(p => p.References[0].Kind.Contains("Finalizer", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("A finalizer root retains this object. It may remain alive until finalization completes.");
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
        var (after, parent) = Walk(request.ReferenceId is null ? request.ObjectId : null, request.ReferenceId, token);
        var released = new List<int>(); long bytes = 0;
        for (var i = 0; i < after.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!Reachable[i] || after[i]) continue;
            released.Add(i); bytes = checked(bytes + _graph.Objects[i].Size);
        }
        var types = released.GroupBy(i => _graph.Objects[i].TypeId).Select(g =>
            new MemoryReleasedType(_graph.Types[g.Key].Name, g.Count(), g.Sum(i => _graph.Objects[i].Size)))
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

    public MemoryGraph BuildGraph(MemoryGraphRequest request, CancellationToken token = default)
    {
        ValidateObject(request.ObjectId);
        var limit = Math.Clamp(request.MaxNodes, 10, 200); var depth = Math.Clamp(request.Depth, 1, 3);
        var columns = new Dictionary<int, int> { [request.ObjectId] = 0 };
        var (paths, pathsTruncated) = GetRootPaths(request.ObjectId, token);
        var truncated = pathsTruncated;
        foreach (var path in paths)
            for (var i = path.References.Count - 1; i >= 0; i--)
            {
                var id = path.References[i].ToId;
                if (columns.ContainsKey(id)) continue;
                if (columns.Count >= limit) { truncated = true; break; }
                columns.Add(id, Math.Max(-16, i - path.References.Count + 1));
            }
        var queue = new Queue<(int Id, int Depth)>(); queue.Enqueue((request.ObjectId, 0));
        var scheduled = new HashSet<int> { request.ObjectId };
        var expanded = new HashSet<int>();
        while (queue.TryDequeue(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!expanded.Add(item.Id) || item.Depth >= depth) continue;
            AddNeighbors(item, true); AddNeighbors(item, false);
        }
        var edgeIds = new List<int>();
        foreach (var node in columns.Keys)
        {
            foreach (var edgeId in _graph.Outgoing.For(node))
                if (columns.ContainsKey(_graph.Edges[edgeId].To))
                { if (edgeIds.Count < 400) edgeIds.Add(edgeId); else truncated = true; }
            if (_graph.RootIndexes.TryGetValue(node, out var roots))
                foreach (var root in roots) { if (edgeIds.Count < 400) edgeIds.Add(_graph.Edges.Length + root); else truncated = true; }
        }
        return new(columns.Select(kv => new MemoryGraphNode(Describe(kv.Key), kv.Value, kv.Key == request.ObjectId,
                _graph.RootIndexes.ContainsKey(kv.Key))).ToArray(), edgeIds.Select(DescribeReference).ToArray(), truncated,
            $"{columns.Count} objects shown. Arrows point from owner to referenced object. Root examples and nearby references are bounded; double-click an object to explore it.");

        void AddNeighbors((int Id, int Depth) item, bool incoming)
        {
            foreach (var edgeId in (incoming ? _graph.Incoming : _graph.Outgoing).For(item.Id))
            {
                var edge = _graph.Edges[edgeId]; var id = incoming ? edge.From : edge.To;
                if (!columns.ContainsKey(id))
                {
                    if (columns.Count >= limit) { truncated = true; break; }
                    columns.Add(id, columns[item.Id] + (incoming ? -1 : 1));
                }
                if (scheduled.Add(id)) queue.Enqueue((id, item.Depth + 1));
            }
        }
    }

    private (bool[] Seen, int[] Parent) Walk(int? removedObject, int? removedReference, CancellationToken token)
    {
        var seen = new bool[_graph.Objects.Length];
        var parent = new int[seen.Length]; Array.Fill(parent, -1);
        var queue = new Queue<int>();
        for (var i = 0; i < _graph.Roots.Length; i++)
        {
            var id = _graph.Roots[i].ObjectId;
            if ((!_graph.Roots[i].IsPermanent && (id == removedObject || _graph.Edges.Length + i == removedReference)) || seen[id]) continue;
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

    public MemoryRootPath? GetShortestRootPath(int objectId) { ValidateObject(objectId); return PathFromParents(objectId, _parentReference); }
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
