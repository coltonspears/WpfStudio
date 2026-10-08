using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Instance grouping ("which instances are alive for the same reason") and the nested dominator tree behind the
/// sunburst. Both are bounded so a type with millions of instances stays responsive.</summary>
public sealed partial class HeapAnalysis
{
    private const int MaxOwnerSteps = 10;
    private const string Repeated = " (repeated)";

    /// <summary>Instances of one type, largest retained first.</summary>
    internal ReadOnlySpan<int> InstancesOf(string typeKey) => TryGetTypeId(typeKey, out var typeId) ? InstancesOf(typeId) : [];

    /// <summary>Groups by Retention (shortest root path shape), Owner (immediate dominator type and field) or Generation.
    /// Value grouping needs field contents and is handled by the snapshot.</summary>
    public MemoryInstanceGroups GetInstanceGroups(MemoryGroupRequest request, CancellationToken token = default)
    {
        var instances = InstancesOf(request.TypeKey);
        var sampled = instances[..Math.Min(instances.Length, Math.Clamp(request.MaxInstances, 1, 200_000))];
        var groups = new Dictionary<string, InstanceGroupBuilder>(StringComparer.Ordinal);
        var by = request.By is "Owner" or "Generation" ? request.By : "Retention";
        bool[]? reachable = null; int[]? parents = null;
        if (by == "Retention")
        {
            var hidden = request.HiddenRootKinds is { Count: > 0 } h ? h.ToHashSet(StringComparer.Ordinal) : null;
            (reachable, parents) = FilteredWalk(hidden, token);
        }
        for (var i = 0; i < sampled.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var id = sampled[i];
            var shape = by switch
            {
                "Owner" => OwnerShape(id),
                "Generation" => GenerationShape(id),
                _ => RetentionShape(id, reachable!, parents!)
            };
            if (!groups.TryGetValue(shape.Key, out var group)) groups[shape.Key] = group = new(shape.Key, shape.Title, shape.Detail, shape.Kind, shape.Steps);
            else if (shape.Steps.Count == group.Steps.Count && shape.Steps.Any(s => s.EndsWith(Repeated, StringComparison.Ordinal)))
                // Paths of different lengths share a group; show a step as repeated if it repeats for any member.
                group.Steps = group.Steps.Zip(shape.Steps, (a, b) => b.EndsWith(Repeated, StringComparison.Ordinal) ? b : a).ToArray();
            AddToGroup(group, id);
        }
        var noun = by switch { "Owner" => "owners", "Generation" => "generations", _ => "retention paths" };
        return FinishGroups(request, by, instances.Length, sampled.Length, groups.Values, count => $"{count:N0} distinct {noun}");
    }

    internal sealed class InstanceGroupBuilder(string key, string title, string detail, string kind, IReadOnlyList<string> steps)
    {
        public string Key => key;
        public string Title { get; set; } = title;
        public string Detail { get; set; } = detail;
        public string Kind => kind;
        public IReadOnlyList<string> Steps { get; set; } = steps;
        public List<int> Members { get; } = [];
        public long Retained { get; set; }
        public long Shallow { get; set; }
        public long Wasted { get; set; }
    }

    internal void AddToGroup(InstanceGroupBuilder group, int id)
    {
        group.Members.Add(id);
        group.Retained += RetainedBytes[id] > 0 ? RetainedBytes[id] : _graph.Objects[id].Size;
        group.Shallow += _graph.Objects[id].Size;
    }

    /// <summary>Largest groups first (by wasted bytes for Value grouping); the tail folds into Other.</summary>
    internal MemoryInstanceGroups FinishGroups(MemoryGroupRequest request, string by, int instances, int grouped,
        IEnumerable<InstanceGroupBuilder> groups, Func<int, string> describe)
    {
        var ordered = (by == "Value"
            ? groups.OrderBy(g => g.Kind == "Unique" ? 1 : 0).ThenByDescending(g => g.Wasted).ThenByDescending(g => g.Members.Count)
            : groups.OrderByDescending(g => g.Retained).ThenByDescending(g => g.Members.Count)).ToArray();
        var maxGroups = Math.Clamp(request.MaxGroups, 1, 500);
        var samples = Math.Clamp(request.SamplesPerGroup, 0, 200);
        var shown = ordered.Take(maxGroups).Select(g => new MemoryInstanceGroup(g.Key, g.Title, g.Detail, g.Kind, g.Members.Count, g.Retained, g.Shallow,
            g.Steps, g.Members.Take(samples).Select(Describe).ToArray(), g.Wasted)).ToArray();
        var rest = ordered.Skip(maxGroups).ToArray();
        var truncated = grouped < instances || rest.Length > 0;
        var description = describe(ordered.Length) + (grouped < instances ? $" among the {grouped:N0} largest of {instances:N0} instances" : $" across {instances:N0} instance{(instances == 1 ? "" : "s")}");
        return new(request.TypeKey, by, instances, grouped, shown, rest.Sum(g => g.Members.Count), rest.Sum(g => g.Retained), truncated, description);
    }

    private readonly record struct GroupShape(string Key, string Title, string Detail, string Kind, IReadOnlyList<string> Steps);

    /// <summary>The root and the chain of owner types and fields down to the instance. Consecutive repeats (linked lists,
    /// trees) collapse into one step, and very deep chains keep their two ends, so similar paths share a group.</summary>
    private GroupShape RetentionShape(int id, bool[] reachable, int[] parents)
    {
        if (!reachable[id])
            return Reachable[id]
                ? new("hidden", "Only hidden roots", "Reachable only through root kinds hidden by the current filter.", "Hidden", [])
                : new("unrooted", "No GC root", _graph.IsComplete ? "Not reachable from any GC root: the next collection frees these." : "No captured root path; the capture is incomplete.", "Unrooted", []);
        var path = ShortestPath(id, parents);
        string root, kind, detail;
        if (path.Terminal >= _graph.Edges.Length)
        {
            var info = _graph.Roots[path.Terminal - _graph.Edges.Length];
            var display = MemoryLabels.RootDisplay(info.Kind, info.Label);
            var isStatic = display.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal);
            root = isStatic ? MemoryLabels.ShortStatic(display) : display;
            kind = isStatic ? "Static" : "Root";
            detail = isStatic ? display : MemoryLabels.RootKindName(info.Kind);
        }
        else if (path.Terminal >= 0)
        {
            var label = _graph.Label(_graph.Edges[path.Terminal].LabelId);
            root = MemoryLabels.ShortStatic(label); kind = "Static"; detail = label;
        }
        else { root = "Unknown root"; kind = "Root"; detail = "The root of this path was not captured."; }

        var owners = new List<string>(); string? last = null; var repeated = false;
        for (var i = path.Edges.Count - 1; i >= 0; i--)
        {
            var edge = _graph.Edges[path.Edges[i]];
            var owner = MemoryLabels.ShortType(_graph.Types[_graph.Objects[edge.From].TypeId].Name);
            var field = FlowLabel(edge.LabelId);
            var step = field.Length == 0 ? owner : owner + " · " + field;
            if (step == last)
            {
                if (!repeated) { owners[^1] += Repeated; repeated = true; }
                continue;
            }
            owners.Add(step); last = step; repeated = false;
        }
        if (owners.Count > MaxOwnerSteps) owners = [.. owners.Take(2), "…", .. owners.Skip(owners.Count - (MaxOwnerSteps - 3))];
        var steps = new List<string>(owners.Count + 1) { root };
        steps.AddRange(owners);
        // The key ignores repeat markers so a list of two nodes and a list of fifty share one group.
        return new(kind + "|" + string.Join('\u001f', steps.Select(s => s.EndsWith(Repeated, StringComparison.Ordinal) ? s[..^Repeated.Length] : s)), root, detail, kind, steps);
    }

    /// <summary>The object that exclusively owns the instance (its immediate dominator) and the field it uses.</summary>
    private GroupShape OwnerShape(int id)
    {
        if (!Reachable[id]) return new("unrooted", "No GC root", "Already eligible for collection.", "Unrooted", []);
        var dominator = ImmediateDominators[id];
        if (dominator < 0 || _staticsHolder[dominator])
        {
            var via = Via(id, dominator < 0 ? null : dominator) ?? "GC root";
            var isStatic = via.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal);
            return new("root|" + via, isStatic ? MemoryLabels.ShortStatic(via) : via, isStatic ? via : "Held directly by a GC root", isStatic ? "Static" : "Root", []);
        }
        var type = _graph.Types[_graph.Objects[dominator].TypeId];
        var field = Via(id, dominator) is { } label ? FlowLabel(label) : null;
        return new("owner|" + type.Key + "|" + field, MemoryLabels.ShortType(type.Name),
            field is null ? $"{type.Name} · owns it through several paths" : $"{type.Name} · via {field}", "Owner", []);
    }

    private GroupShape GenerationShape(int id)
    {
        var generation = HeapGraph.GenerationName(_graph.Objects[id].Generation);
        var detail = generation switch
        {
            "Generation0" => "Recently allocated; the cheapest to collect.",
            "Generation1" => "Survived one collection.",
            "Generation2" => "Long-lived. Leaked objects end up here.",
            "Large" => "85,000 bytes or more; collected only with generation 2.",
            "Pinned" => "Allocated on the pinned object heap.",
            "Frozen" => "Read-only data that is never collected.",
            _ => ""
        };
        return new("gen|" + generation, MemoryLabels.GenerationName(generation), detail, "Generation", []);
    }

    /// <summary>A slice of the dominator tree, nested a few levels deep, for the sunburst. Node keys match the dominator
    /// tree rows so drilling into a ring segment can focus the same node in the tree.</summary>
    public MemorySunburst GetDominatorTree(MemoryDominatorTreeRequest request, CancellationToken token = default)
    {
        var depth = Math.Clamp(request.Depth, 1, 6);
        var maxChildren = Math.Clamp(request.MaxChildren, 2, 64);
        var budget = 3_000; var truncated = false; var others = 0;
        if (request.ParentId is int parentId) ValidateObject(parentId);
        MemorySunburstNode centre; long total;
        if (request.TypeKey is not null)
        {
            var scope = request.ParentId is int p ? DominatedBy(p).ToArray() : TopLevelDominated();
            var members = TryGetTypeId(request.TypeKey, out var typeId) ? scope.Where(m => _graph.Objects[m].TypeId == typeId).ToArray() : [];
            total = members.Sum(m => RetainedBytes[m]);
            var threshold = Threshold(total);
            var name = typeId < _graph.Types.Length && members.Length > 0 ? _graph.Types[typeId].Name : request.TypeKey;
            centre = new($"g{request.ParentId}|{request.TypeKey}", request.TypeKey, $"{members.Length:N0} × {MemoryLabels.ShortType(name)}", name,
                members.Length, total, null, Instances(members, 1, threshold));
        }
        else if (request.ParentId is int p)
        {
            total = RetainedBytes[p];
            var type = _graph.Types[_graph.Objects[p].TypeId];
            centre = new("o" + p, type.Key, MemoryLabels.ShortType(type.Name), $"{type.Name}\n0x{_graph.Objects[p].Address:X}", 1, total, p,
                Grouped(DominatedBy(p).ToArray(), 1, p, Threshold(total)));
        }
        else
        {
            var members = TopLevelDominated();
            total = members.Sum(m => RetainedBytes[m]);
            centre = new("heap", "", "Heap", "Everything reachable from the GC roots", members.Length, total, null, Grouped(members, 1, null, Threshold(total)));
        }
        token.ThrowIfCancellationRequested();
        return new(centre, total, truncated);

        long Threshold(long bytes) => (long)(Math.Max(1, bytes) * Math.Clamp(request.MinShare, 0, 0.5));

        List<MemorySunburstNode> Grouped(int[] members, int level, int? parent, long threshold)
        {
            var result = new List<MemorySunburstNode>(); long otherBytes = 0; var otherCount = 0;
            foreach (var group in members.GroupBy(m => _graph.Objects[m].TypeId)
                .Select(g => (TypeId: g.Key, Members: g.ToArray(), Bytes: g.Sum(m => RetainedBytes[m]))).OrderByDescending(g => g.Bytes))
            {
                if (group.Bytes <= 0) continue;
                if (group.Bytes < threshold || result.Count >= maxChildren || budget <= 0) { otherBytes += group.Bytes; otherCount += group.Members.Length; continue; }
                budget--;
                if (group.Members.Length == 1) result.Add(ObjectNode(group.Members[0], level, parent, threshold));
                else
                {
                    var type = _graph.Types[group.TypeId];
                    var vias = group.Members.Take(32).Select(m => Via(m, ParentFor(m, parent))).Distinct().Take(2).ToArray();
                    result.Add(new($"g{parent}|{type.Key}", type.Key, $"{group.Members.Length:N0} × {MemoryLabels.ShortType(type.Name)}",
                        type.Name + (vias.Length == 1 && vias[0] is { } via ? "\n" + ViaText(via) : ""), group.Members.Length, group.Bytes, null,
                        level < depth ? Instances(group.Members, level + 1, threshold) : []));
                }
            }
            if (otherBytes > 0) { truncated = true; result.Add(Other(otherCount, otherBytes, level, parent)); }
            return result;
        }

        List<MemorySunburstNode> Instances(int[] members, int level, long threshold)
        {
            var result = new List<MemorySunburstNode>(); long otherBytes = 0; var otherCount = 0;
            foreach (var id in members.OrderByDescending(m => RetainedBytes[m]))
            {
                if (RetainedBytes[id] <= 0) continue;
                if (RetainedBytes[id] < threshold || result.Count >= maxChildren || budget <= 0) { otherBytes += RetainedBytes[id]; otherCount++; continue; }
                budget--;
                result.Add(ObjectNode(id, level, ImmediateDominators[id] >= 0 ? ImmediateDominators[id] : null, threshold));
            }
            if (otherBytes > 0) { truncated = true; result.Add(Other(otherCount, otherBytes, level, null)); }
            return result;
        }

        MemorySunburstNode ObjectNode(int id, int level, int? parent, long threshold)
        {
            var obj = _graph.Objects[id]; var type = _graph.Types[obj.TypeId];
            var via = Via(id, ParentFor(id, parent));
            return new("o" + id, type.Key, MemoryLabels.ShortType(type.Name), $"{type.Name}\n0x{obj.Address:X}" + (via is null ? "" : "\n" + ViaText(via)),
                1, RetainedBytes[id], id, level < depth ? Grouped(DominatedBy(id).ToArray(), level + 1, id, threshold) : []);
        }

        MemorySunburstNode Other(int count, long bytes, int level, int? parent) =>
            new($"other|{level}|{parent}|{others++}", "", $"{count:N0} smaller", "Owners below the size threshold", count, bytes, null, [], IsOther: true);

        static string ViaText(string via) => via.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal) ? via : "via " + via;
    }
}
