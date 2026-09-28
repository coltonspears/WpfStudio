using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Runtime.Inspection;

public sealed record InspectionBindingIssue(string Id, string NodeId, string ElementLabel, string TargetProperty,
    string? Path, string? SourceType, string Category, string Explanation, string State, int Occurrences,
    DateTimeOffset FirstObserved, DateTimeOffset LastObserved);

/// <summary>
/// Tracks observed binding failures. Absence, inactivity and incomplete scans are never
/// evidence of recovery; only a later healthy observation of the same expression is.
/// Callers apply snapshots serially and clear the tracker before using a new session.
/// </summary>
public sealed class InspectionBindingIssueTracker
{
    private sealed class Tracked(InspectionBindingIssue issue)
    {
        public InspectionBindingIssue Issue = issue;
        public bool FailureEpisodeOpen = true;
        public string FailureExplanation = issue.Explanation;
    }

    private readonly int _maximumIssues;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Tracked> _issues = new(StringComparer.Ordinal);
    private long _lastRevision = long.MinValue;
    private bool _ended;
    private bool _retentionTruncated;

    public InspectionBindingIssueTracker(int maximumIssues = 500, TimeProvider? timeProvider = null)
    {
        if (maximumIssues is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumIssues));
        _maximumIssues = maximumIssues;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<InspectionBindingIssue> Issues { get; private set; } = [];
    public bool IsTruncated { get; private set; }
    public bool IsRetentionTruncated => _retentionTruncated;

    public void Apply(InspectionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (_ended || tree.Revision <= _lastRevision) return;
        _lastRevision = tree.Revision;
        var now = _timeProvider.GetUtcNow();
        var nodes = new Dictionary<string, InspectionNode>(StringComparer.Ordinal);
        foreach (var node in tree.Nodes.Take(10_000)) nodes.TryAdd(node.Id, node);
        var scanned = tree.ScannedBindingNodes?.Take(10_000).ToHashSet(StringComparer.Ordinal) ?? [];
        var observations = tree.BindingObservations ?? [];
        bool incomplete = tree.Truncated || tree.BindingScanTruncated || tree.BindingObservations is null
            || tree.ScannedBindingNodes is null || observations.Count > 10_000 || tree.Nodes.Count > 10_000;
        if (observations.Count > 10_000) scanned.Clear(); // Dropped evidence cannot prove expression removal.
        var uniqueObservations = new Dictionary<string, InspectionBindingObservation>(StringComparer.Ordinal);
        foreach (var observation in observations.Take(10_000))
        {
            if (string.IsNullOrEmpty(observation.BindingId)) continue;
            // A Popup child can also be reached through its owner's logical tree.
            // Sources are sampled separately, so conflicting duplicate reads never
            // establish recovery: failing > unverified > healthy evidence.
            if (!uniqueObservations.TryGetValue(observation.BindingId, out var prior)
                || EvidenceRank(observation.Binding) >= EvidenceRank(prior.Binding))
                uniqueObservations[observation.BindingId] = observation;
        }
        var observed = uniqueObservations.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var observation in uniqueObservations.Values)
        {
            var binding = observation.Binding;
            bool failure = IsFailure(binding);
            if (!_issues.TryGetValue(observation.BindingId, out var tracked))
            {
                if (!failure) continue; // Healthy/inactive bindings do not create issues.
                MakeRoom();
                var issue = new InspectionBindingIssue(observation.BindingId, observation.NodeId,
                    Label(nodes.GetValueOrDefault(observation.NodeId), observation.NodeId), observation.TargetProperty,
                    binding.Path, binding.SourceType, binding.Category, binding.Explanation, "Active", 1, now, now);
                _issues.Add(issue.Id, new Tracked(issue));
                continue;
            }

            var previous = tracked.Issue;
            string state;
            string explanation;
            int occurrences = previous.Occurrences;
            if (failure)
            {
                if (!tracked.FailureEpisodeOpen && occurrences < int.MaxValue) occurrences++;
                tracked.FailureEpisodeOpen = true;
                tracked.FailureExplanation = binding.Explanation;
                state = "Active";
                explanation = binding.Explanation;
            }
            else if (IsHealthy(binding))
            {
                tracked.FailureEpisodeOpen = false;
                state = "Resolved";
                explanation = "This same binding expression was later observed active without a binding or validation error. " + tracked.FailureExplanation;
            }
            else
            {
                // A previously confirmed recovery remains historical evidence, but a
                // pending/inactive binding never turns an unresolved episode into one.
                state = tracked.FailureEpisodeOpen ? "NotObserved" : previous.State;
                explanation = tracked.FailureEpisodeOpen
                    ? $"Binding currently reports {binding.Status}; recovery has not been verified. " + tracked.FailureExplanation
                    : previous.Explanation;
            }
            tracked.Issue = previous with
            {
                NodeId = observation.NodeId,
                ElementLabel = Label(nodes.GetValueOrDefault(observation.NodeId), previous.ElementLabel),
                TargetProperty = observation.TargetProperty,
                Path = binding.Path ?? previous.Path,
                SourceType = binding.SourceType ?? previous.SourceType,
                Category = failure ? binding.Category : previous.Category,
                Explanation = explanation,
                State = state,
                Occurrences = occurrences,
                LastObserved = now
            };
        }

        foreach (var tracked in _issues.Values)
        {
            var issue = tracked.Issue;
            if (observed.Contains(issue.Id) || issue.State is "Resolved" or "BindingRemoved" or "TargetUnavailable") continue;
            string state;
            string context;
            if (nodes.ContainsKey(issue.NodeId) && scanned.Contains(issue.NodeId) && tree.BindingObservations is not null)
            {
                state = "BindingRemoved";
                context = "The target was fully scanned and this binding expression is no longer present. Removal or replacement is not a verified recovery.";
            }
            else if (!nodes.ContainsKey(issue.NodeId) && !incomplete && string.IsNullOrWhiteSpace(tree.Status))
            {
                state = "TargetUnavailable";
                context = "The target is absent from a complete presentation-tree snapshot. It may have been unloaded or virtualized; recovery has not been verified.";
            }
            else
            {
                state = "NotObserved";
                context = "This binding was not observed in the latest scan. Partial scans and unavailable targets do not prove recovery or removal.";
            }
            tracked.Issue = issue with { State = state, Explanation = context + " " + tracked.FailureExplanation };
        }
        IsTruncated = _retentionTruncated || incomplete;
        Publish();
    }

    public void EndSession()
    {
        if (_ended) return;
        _ended = true;
        foreach (var tracked in _issues.Values)
            if (tracked.Issue.State is "Active" or "NotObserved")
                tracked.Issue = tracked.Issue with
                {
                    State = "SessionEnded",
                    Explanation = "The inspection session ended before recovery was verified. " + tracked.FailureExplanation
                };
        Publish();
    }

    public void Clear()
    {
        _issues.Clear();
        _lastRevision = long.MinValue;
        _ended = false;
        _retentionTruncated = false;
        IsTruncated = false;
        Issues = [];
    }

    private void MakeRoom()
    {
        if (_issues.Count < _maximumIssues) return;
        var oldest = _issues.Values.OrderBy(item => item.Issue.State is "Active" or "NotObserved" ? 1 : 0)
            .ThenBy(item => item.Issue.LastObserved).ThenBy(item => item.Issue.FirstObserved).First();
        _issues.Remove(oldest.Issue.Id);
        _retentionTruncated = true;
    }

    private void Publish() => Issues = _issues.Values.Select(item => item.Issue)
        .OrderBy(item => item.State is "Active" ? 0 : item.State is "NotObserved" ? 1 : 2)
        .ThenByDescending(item => item.LastObserved).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();

    private static string Label(InspectionNode? node, string fallback) => node is null ? fallback
        : string.IsNullOrWhiteSpace(node.Name) ? node.Type : node.Name + " (" + node.Type + ")";

    private static bool IsFailure(InspectionBinding binding) => binding.HasValidationError
        || binding.Status is "PathError" or "UpdateTargetError" or "UpdateSourceError"
        || binding.Category is "Validation" or "ChildBindingError" or "MissingProperty" or "PathError" or "UpdateTargetError" or "UpdateSourceError";

    private static bool IsHealthy(InspectionBinding binding) => !IsFailure(binding)
        && binding.Status == "Active" && binding.Category == "Active";

    private static int EvidenceRank(InspectionBinding binding) => IsFailure(binding) ? 2 : IsHealthy(binding) ? 0 : 1;
}
