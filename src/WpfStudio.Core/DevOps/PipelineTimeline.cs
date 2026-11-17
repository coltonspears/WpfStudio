namespace WpfStudio.Core.DevOps;

/// <summary>A record of a run's timeline with the records under it.</summary>
public sealed record DevOpsTimelineNode(DevOpsTimelineRecord Record, IReadOnlyList<DevOpsTimelineNode> Children)
{
    public bool HasChildren => Children.Count > 0;
}

public static class PipelineTimeline
{
    private const int MaxDepth = 10;

    /// <summary>
    /// Turns a flat timeline into Stage → Job → Task. Phase records, and the implicit stage single-stage and classic pipelines
    /// have, add nesting without information, so their children take their place. Checkpoints (approval bookkeeping) are left
    /// out; pending approvals are listed on their own. A record whose parent is missing is shown at the top.
    /// </summary>
    public static IReadOnlyList<DevOpsTimelineNode> Build(IEnumerable<DevOpsTimelineRecord> records)
    {
        var list = records.ToList();
        var ids = list.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        var byParent = list.GroupBy(record => record.ParentId is { } parent && ids.Contains(parent) ? parent : "", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(record => record.Order).ThenBy(record => record.Started ?? DateTimeOffset.MaxValue).ToList(), StringComparer.Ordinal);
        return Expand("", 0);

        List<DevOpsTimelineNode> Expand(string parent, int depth)
        {
            var result = new List<DevOpsTimelineNode>();
            if (depth > MaxDepth || !byParent.TryGetValue(parent, out var children)) return result;
            foreach (var record in children)
            {
                if (record.Type.StartsWith("Checkpoint", StringComparison.OrdinalIgnoreCase)) continue;
                if (record.Type.Equals("Phase", StringComparison.OrdinalIgnoreCase) || (record.Type.Equals("Stage", StringComparison.OrdinalIgnoreCase) && record.Name == "__default"))
                    result.AddRange(Expand(record.Id, depth + 1));
                else result.Add(new(record, Expand(record.Id, depth + 1)));
            }
            return result;
        }
    }

    /// <summary>True while any part of the run is still running or a job is waiting for an agent.</summary>
    public static bool IsActive(IEnumerable<DevOpsTimelineRecord> records) =>
        records.Any(record => record.State == RunStatus.Running || (record.State == RunStatus.Queued && record.Type.Equals("Job", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The first failed leaf, so a failed run opens on the step that broke it.</summary>
    public static DevOpsTimelineNode? FirstFailure(IEnumerable<DevOpsTimelineNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (FirstFailure(node.Children) is { } child) return child;
            if (node.Record.Result == RunResult.Failed && node.Record.HasLog) return node;
        }
        return null;
    }
}
