using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionBindingIssueTrackerTests
{
    [Fact]
    public void RepeatedFailurePollsAreOneEpisodeAndKeepTheFirstObservation()
    {
        var clock = new TestClock();
        var tracker = new InspectionBindingIssueTracker(timeProvider: clock);
        tracker.Apply(Tree(1, [Failure()]));
        var first = Assert.Single(tracker.Issues);
        clock.Advance();
        tracker.Apply(Tree(2, [Failure()]));
        var repeated = Assert.Single(tracker.Issues);
        Assert.Equal("Active", repeated.State);
        Assert.Equal(1, repeated.Occurrences);
        Assert.Equal(first.FirstObserved, repeated.FirstObserved);
        Assert.Equal(clock.GetUtcNow(), repeated.LastObserved);
        Assert.Equal("Example (System.Windows.Controls.TextBlock)", repeated.ElementLabel);
        Assert.Equal("Text", repeated.TargetProperty);
        Assert.Equal("Missing", repeated.Path);
    }

    [Fact]
    public void OnlyVerifiedSuccessOfTheSameExpressionClosesAFailureEpisode()
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        tracker.Apply(Tree(2, [Healthy()]));
        Assert.Equal("Resolved", Assert.Single(tracker.Issues).State);
        tracker.Apply(Tree(3, [Failure()]));
        var issue = Assert.Single(tracker.Issues);
        Assert.Equal("Active", issue.State);
        Assert.Equal(2, issue.Occurrences);
    }

    [Theory]
    [InlineData("AsyncRequestPending", "Pending")]
    [InlineData("Inactive", "Inactive")]
    [InlineData("Unattached", "Unattached")]
    [InlineData("Detached", "Detached")]
    [InlineData("Active", "Pending")]
    public void PendingInactiveAndUnverifiedMultiBindingParentsNeverResolve(string status, string category)
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        tracker.Apply(Tree(2, [Observation("binding", "n", new("Missing", status, category, "Not verified."))]));
        Assert.Equal("NotObserved", Assert.Single(tracker.Issues).State);
        tracker.Apply(Tree(3, [Failure()]));
        Assert.Equal(1, Assert.Single(tracker.Issues).Occurrences);
    }

    [Theory]
    [InlineData("Active", "ChildBindingError", false)]
    [InlineData("Active", "Validation", true)]
    [InlineData("PathError", "SourceUnavailable", false)]
    [InlineData("UpdateTargetError", "UpdateTargetError", false)]
    public void RuntimeErrorEvidenceCreatesAnIssueEvenWhenParentStatusIsActive(string status, string category, bool validation)
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Observation("binding", "n", new("Missing", status, category, "Observed error.", HasValidationError: validation))]));
        Assert.Equal("Active", Assert.Single(tracker.Issues).State);
        Assert.Equal(category, Assert.Single(tracker.Issues).Category);
    }

    [Fact]
    public void BindingReplacementHasASeparateIdentityAndDoesNotResolveTheOldIssue()
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        tracker.Apply(Tree(2, [Healthy("replacement")]));
        var issue = Assert.Single(tracker.Issues);
        Assert.Equal("binding", issue.Id);
        Assert.Equal("BindingRemoved", issue.State);
        Assert.Equal(1, issue.Occurrences);
        tracker.Apply(Tree(3, [Failure("replacement")]));
        Assert.Equal(2, tracker.Issues.Count);
        Assert.Contains(tracker.Issues, issue => issue.Id == "replacement" && issue.State == "Active" && issue.Occurrences == 1);
    }

    [Fact]
    public void FullyScannedTargetProvesRemovalEvenWhenAnotherSourceWasTruncated()
    {
        var clock = new TestClock();
        var tracker = new InspectionBindingIssueTracker(timeProvider: clock);
        tracker.Apply(Tree(1, [Failure()]));
        var lastObserved = Assert.Single(tracker.Issues).LastObserved;
        clock.Advance();
        tracker.Apply(Tree(2, [], truncated: true, bindingTruncated: true));
        var issue = Assert.Single(tracker.Issues);
        Assert.Equal("BindingRemoved", issue.State);
        Assert.Equal(lastObserved, issue.LastObserved);
        Assert.True(tracker.IsTruncated);
        Assert.False(tracker.IsRetentionTruncated);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "Dispatcher did not respond.")]
    public void MissingTargetInAnIncompleteSnapshotDoesNotProveRemovalOrRecovery(bool truncated, bool bindingTruncated, string? status)
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        tracker.Apply(Tree(2, [], nodes: [], scanned: [], truncated: truncated, bindingTruncated: bindingTruncated, status: status));
        Assert.Equal("NotObserved", Assert.Single(tracker.Issues).State);
        tracker.Apply(Tree(3, [Failure()]));
        Assert.Equal(1, Assert.Single(tracker.Issues).Occurrences);
    }

    [Fact]
    public void UnscannedPresentTargetAndLegacySnapshotNeverImplyRemoval()
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        tracker.Apply(Tree(2, [], scanned: []));
        Assert.Equal("NotObserved", Assert.Single(tracker.Issues).State);
        tracker.Apply(new InspectionTree(3, [Node()], []));
        Assert.Equal("NotObserved", Assert.Single(tracker.Issues).State);
        Assert.True(tracker.IsTruncated);
    }

    [Fact]
    public void TargetAbsentFromACompleteSnapshotIsUnavailableAndCanReappearWithoutRecovery()
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        tracker.Apply(Tree(2, [], nodes: [], scanned: []));
        Assert.Equal("TargetUnavailable", Assert.Single(tracker.Issues).State);
        tracker.Apply(Tree(3, [Failure()]));
        var issue = Assert.Single(tracker.Issues);
        Assert.Equal("Active", issue.State);
        Assert.Equal(1, issue.Occurrences);
    }

    [Fact]
    public void DuplicateAndOlderRevisionsCannotOverwriteNewerEvidence()
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(10, [Failure()]));
        tracker.Apply(Tree(11, [Healthy()]));
        tracker.Apply(Tree(10, [Failure()]));
        tracker.Apply(Tree(11, [Failure()]));
        Assert.Equal("Resolved", Assert.Single(tracker.Issues).State);
        Assert.Equal(1, Assert.Single(tracker.Issues).Occurrences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingDuplicateExpressionReadsNeverVerifyRecovery(bool reverse)
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        var observations = new[] { Healthy(), Failure() };
        if (reverse) Array.Reverse(observations);
        tracker.Apply(Tree(2, observations));
        Assert.Equal("Active", Assert.Single(tracker.Issues).State);
        Assert.Equal(1, Assert.Single(tracker.Issues).Occurrences);

        observations = [Healthy(), Observation("binding", "n", new("Missing", "Active", "Pending", "Child still pending."))];
        if (reverse) Array.Reverse(observations);
        tracker.Apply(Tree(3, observations));
        Assert.Equal("NotObserved", Assert.Single(tracker.Issues).State);
        tracker.Apply(Tree(4, [Healthy(), Healthy()]));
        Assert.Equal("Resolved", Assert.Single(tracker.Issues).State);
        Assert.Equal(1, Assert.Single(tracker.Issues).Occurrences);
    }

    [Fact]
    public void TrackerInputLimitCannotTurnDroppedObservationsIntoRemovalEvidence()
    {
        var tracker = new InspectionBindingIssueTracker();
        tracker.Apply(Tree(1, [Failure()]));
        var oversized = Enumerable.Range(0, 10_000).Select(index => Healthy("healthy-" + index)).Append(Failure()).ToArray();
        tracker.Apply(Tree(2, oversized));
        Assert.Equal("NotObserved", Assert.Single(tracker.Issues).State);
        Assert.True(tracker.IsTruncated);
        Assert.False(tracker.IsRetentionTruncated);
    }

    [Fact]
    public void SessionEndPreservesTerminalEvidenceAndDoesNotAcceptLateSnapshots()
    {
        var tracker = new InspectionBindingIssueTracker();
        var nodes = new[] { Node("active"), Node("resolved"), Node("removed"), Node("missing") };
        tracker.Apply(Tree(1, nodes.Select(node => Failure(node.Id, node.Id)).ToArray(), nodes));
        tracker.Apply(Tree(2, [Failure("active", "active"), Healthy("resolved", "resolved")], nodes[..3], ["active", "resolved", "removed"]));
        tracker.EndSession();
        Assert.Equal("SessionEnded", tracker.Issues.Single(issue => issue.Id == "active").State);
        Assert.Equal("Resolved", tracker.Issues.Single(issue => issue.Id == "resolved").State);
        Assert.Equal("BindingRemoved", tracker.Issues.Single(issue => issue.Id == "removed").State);
        Assert.Equal("TargetUnavailable", tracker.Issues.Single(issue => issue.Id == "missing").State);
        tracker.Apply(Tree(3, [Healthy("active", "active")]));
        Assert.Equal("SessionEnded", tracker.Issues.Single(issue => issue.Id == "active").State);
    }

    [Fact]
    public void RetentionPrefersHistoricalRowsAndReportsLostHistoryUntilClear()
    {
        var clock = new TestClock();
        var tracker = new InspectionBindingIssueTracker(2, clock);
        tracker.Apply(Tree(1, [Failure("old"), Failure("active")]));
        clock.Advance();
        tracker.Apply(Tree(2, [Healthy("old"), Failure("active")]));
        tracker.Apply(Tree(3, [Failure("active"), Failure("new")]));
        Assert.Equal(2, tracker.Issues.Count);
        Assert.DoesNotContain(tracker.Issues, issue => issue.Id == "old");
        Assert.True(tracker.IsRetentionTruncated);
        Assert.True(tracker.IsTruncated);
        tracker.EndSession();
        tracker.Clear();
        Assert.Empty(tracker.Issues);
        Assert.False(tracker.IsRetentionTruncated);
        Assert.False(tracker.IsTruncated);
        tracker.Apply(Tree(1, [Failure()]));
        Assert.Equal("Active", Assert.Single(tracker.Issues).State);
    }

    [Fact]
    public void HealthyAndPendingBindingsDoNotConsumeIssueRetention()
    {
        var tracker = new InspectionBindingIssueTracker(1);
        tracker.Apply(Tree(1, [Healthy("healthy"), Observation("pending", "n", new("Value", "AsyncRequestPending", "Pending", "Waiting.")), Failure()]));
        Assert.Single(tracker.Issues);
        Assert.False(tracker.IsRetentionTruncated);
    }

    private static InspectionTree Tree(long revision, InspectionBindingObservation[] observations,
        InspectionNode[]? nodes = null, string[]? scanned = null, bool truncated = false,
        bool bindingTruncated = false, string? status = null) =>
        new(revision, nodes ?? [Node()], [], truncated, status, observations, scanned ?? ["n"], bindingTruncated);

    private static InspectionNode Node(string id = "n") =>
        new(id, null, null, 1, "System.Windows.Controls.TextBlock", "Example", null, true);

    private static InspectionBindingObservation Failure(string id = "binding", string node = "n") =>
        Observation(id, node, new("Missing", "PathError", "MissingProperty", "The runtime source has no readable Missing property.", "Example.Model"));

    private static InspectionBindingObservation Healthy(string id = "binding", string node = "n") =>
        Observation(id, node, new("Missing", "Active", "Active", "WPF reports an active binding.", "Example.Model"));

    private static InspectionBindingObservation Observation(string id, string node, InspectionBinding binding) =>
        new(id, node, "Text", "System.Windows.Controls.TextBlock", "PresentationFramework", binding);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now += TimeSpan.FromSeconds(5);
    }
}
