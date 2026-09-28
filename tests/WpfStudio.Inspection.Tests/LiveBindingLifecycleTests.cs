using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveBindingLifecycleTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task FullScanTracksRealBindingRecoveryReplacementAndUnloadingWithoutSelectingElements(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await session.WaitForConnectionAsync(timeout.Token);
        var tracker = new InspectionBindingIssueTracker();
        var initial = await RunningApplicationTests.WaitForTreeAsync(session, tree => tree.BindingObservations is not null &&
            tree.ScannedBindingNodes is not null && !tree.Truncated && !tree.BindingScanTruncated, timeout.Token);
        Assert.False(initial.Truncated);
        Assert.False(initial.BindingScanTruncated);
        Assert.NotNull(initial.BindingObservations);
        Assert.NotNull(initial.ScannedBindingNodes);
        tracker.Apply(initial);

        var lifecycle = Observation(initial, "LifecycleText");
        Assert.Equal("MissingProperty", lifecycle.Binding.Category);
        Assert.EndsWith("MissingValueSource", lifecycle.Binding.SourceType);
        Assert.Equal("Active", Issue(tracker, lifecycle).State);
        Assert.Equal(1, Issue(tracker, lifecycle).Occurrences);
        Assert.Equal("MissingProperty", Observation(initial, "StyleBindingText").Binding.Category);
        Assert.Equal("MissingProperty", Observation(initial, "AttachedStyleBindingText", "AttachedBindingProbe.Value").Binding.Category);
        Assert.NotEqual("MissingProperty", Observation(initial, "NullIntermediateText").Binding.Category);
        Assert.NotEqual("MissingProperty", Observation(initial, "DelayedText").Binding.Category);
        Assert.Equal("MissingProperty", Observation(initial, "PopupText", "Tag").Binding.Category);
        Assert.Equal("MissingProperty", Observation(initial, "ChildDispatcherText", "Tag").Binding.Category);
        Assert.NotEqual(Node(initial, "LifecycleText").DispatcherId, Node(initial, "ChildDispatcherText").DispatcherId);
        var replacement = Observation(initial, "ReplacementText");
        var removal = Observation(initial, "RemovalText");
        var detachable = Observation(initial, "DetachableText");
        var delayed = Observation(initial, "DelayedText");

        await app.SendAsync("binding-success", timeout.Token);
        var healthy = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.Equal(lifecycle.BindingId, Observation(healthy, "LifecycleText").BindingId);
        Assert.Equal("Active", Observation(healthy, "LifecycleText").Binding.Status);
        tracker.Apply(healthy);
        Assert.Equal("Resolved", Issue(tracker, lifecycle).State);
        Assert.Equal(1, Issue(tracker, lifecycle).Occurrences);
        Assert.NotEmpty(healthy.Traces); // Historical trace messages are not current failures.

        await app.SendAsync("binding-failure", timeout.Token);
        var failedAgain = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.Equal(lifecycle.BindingId, Observation(failedAgain, "LifecycleText").BindingId);
        tracker.Apply(failedAgain);
        Assert.Equal("Active", Issue(tracker, lifecycle).State);
        Assert.Equal(2, Issue(tracker, lifecycle).Occurrences);

        await app.SendAsync("binding-pending", timeout.Token);
        var pending = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.Equal(lifecycle.BindingId, Observation(pending, "LifecycleText").BindingId);
        Assert.NotEqual("MissingProperty", Observation(pending, "LifecycleText").Binding.Category);
        tracker.Apply(pending);
        Assert.NotEqual("Resolved", Issue(tracker, lifecycle).State);
        Assert.Equal(2, Issue(tracker, lifecycle).Occurrences);

        await app.SendAsync("binding-delayed-ready", timeout.Token);
        await app.SendAsync("binding-null-ready", timeout.Token);
        var contextsArrived = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.Equal(delayed.BindingId, Observation(contextsArrived, "DelayedText").BindingId);
        Assert.Equal("Active", Observation(contextsArrived, "DelayedText").Binding.Status);
        Assert.Equal("Active", Observation(contextsArrived, "NullIntermediateText").Binding.Status);
        tracker.Apply(contextsArrived);
        // Inspect only after the scan assertions: the scan itself must discover these
        // bindings without priming a property inspector or selecting each element.
        var delayedElement = await session.InspectAsync(new(contextsArrived.Revision, delayed.NodeId), timeout.Token);
        Assert.Equal("Binding now resolves", RunningApplicationTests.Property(delayedElement, "Text").Value);

        await app.SendAsync("binding-replace", timeout.Token);
        await app.SendAsync("binding-remove", timeout.Token);
        await app.SendAsync("binding-unload", timeout.Token);
        var removed = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var replacedWith = Observation(removed, "ReplacementText");
        Assert.NotEqual(replacement.BindingId, replacedWith.BindingId);
        Assert.Equal("Active", replacedWith.Binding.Status);
        Assert.Contains(removal.NodeId, removed.ScannedBindingNodes!);
        Assert.DoesNotContain(removed.BindingObservations!, value => value.BindingId == removal.BindingId);
        Assert.DoesNotContain(removed.Nodes, value => value.Id == detachable.NodeId);
        tracker.Apply(removed);
        Assert.Equal("BindingRemoved", Issue(tracker, replacement).State);
        Assert.Equal("BindingRemoved", Issue(tracker, removal).State);
        Assert.Equal("TargetUnavailable", Issue(tracker, detachable).State);

        await app.SendAsync("binding-reload", timeout.Token);
        await app.SendAsync("binding-failure", timeout.Token);
        var returned = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.Equal("MissingProperty", Observation(returned, "DetachableText").Binding.Category);
        tracker.Apply(returned);
        Assert.Equal("Active", Issue(tracker, lifecycle).State);
        Assert.Equal(2, Issue(tracker, lifecycle).Occurrences);
        var limited = await session.SnapshotAsync(new InspectionTreeRequest(MaximumNodes: 1), timeout.Token);
        Assert.True(limited.Truncated);
        tracker.Apply(limited);
        Assert.Equal("NotObserved", Issue(tracker, lifecycle).State);
        Assert.True(tracker.IsTruncated);
        tracker.EndSession();
        Assert.Equal("SessionEnded", Issue(tracker, lifecycle).State);
        await session.DisconnectAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }

    [Fact]
    public async Task RepeatedBoundedScansReachLateBindingsWithoutStarvingOtherPresentationSources()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await session.WaitForConnectionAsync(timeout.Token);
        await app.SendAsync("binding-bulk", timeout.Token);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        bool partial = false;
        bool popupObserved = false;
        bool childObserved = false;
        for (int scan = 0; scan < 30 && observed.Count < 600; scan++)
        {
            var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
            Assert.False(tree.Truncated);
            partial |= tree.BindingScanTruncated;
            var names = tree.Nodes.ToDictionary(node => node.Id, node => node.Name);
            foreach (var binding in tree.BindingObservations!)
            {
                var name = names[binding.NodeId];
                if (name?.StartsWith("BulkBinding", StringComparison.Ordinal) == true && binding.TargetProperty == "Text")
                {
                    Assert.Equal("MissingProperty", binding.Binding.Category);
                    observed.Add(name);
                }
                if (name == "PopupText" && binding.TargetProperty == "Tag") popupObserved = true;
                if (name == "ChildDispatcherText" && binding.TargetProperty == "Tag") childObserved = true;
            }
        }
        Assert.True(partial, "The fixture must exceed a single source's binding scan share.");
        Assert.Equal(600, observed.Count);
        Assert.Contains("BulkBinding599", observed);
        Assert.True(popupObserved, "The large main window must not starve the popup's binding scan.");
        Assert.True(childObserved, "The large main window must not starve the child dispatcher's binding scan.");
    }

    private static InspectionNode Node(InspectionTree tree, string name) => Assert.Single(tree.Nodes, node => node.Name == name);

    private static InspectionBindingObservation Observation(InspectionTree tree, string name, string property = "Text")
    {
        var node = Node(tree, name);
        Assert.Contains(node.Id, tree.ScannedBindingNodes!);
        return Assert.Single(tree.BindingObservations!, observation => observation.NodeId == node.Id && observation.TargetProperty == property);
    }

    private static InspectionBindingIssue Issue(InspectionBindingIssueTracker tracker, InspectionBindingObservation observation) =>
        Assert.Single(tracker.Issues, issue => issue.Id == observation.BindingId);
}
