using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.NativeView.Tests;

public sealed class PreviewLeaseTests
{
    [Fact]
    public void AnAttachmentIsPendingUntilAcceptedAndItsValuesEndWithIt()
    {
        var lease = new PreviewLease();
        lease.BeginAttachment();
        string? token = lease.NavigationToken;
        Assert.True(lease.IsAttaching);
        Assert.NotNull(token);

        lease.AcceptAttachment(42, 7);
        lease.BoundarySequence = 3;
        lease.FocusObservationSequence = 4;
        Assert.False(lease.IsAttaching);
        Assert.Equal(42, lease.NativeHandle);
        Assert.Equal(7, lease.AttachmentSequence);

        lease.BeginAttachment();
        Assert.True(lease.IsAttaching);
        Assert.NotEqual(token, lease.NavigationToken);
        Assert.Equal(0, lease.NativeHandle);
        Assert.Equal(0, lease.AttachmentSequence);
        Assert.Equal(0, lease.BoundarySequence);
        Assert.Equal(0, lease.FocusObservationSequence);

        lease.ResetAttachment();
        Assert.False(lease.IsAttaching);
        Assert.Null(lease.NavigationToken);
    }

    [Fact]
    public void ResettingAnAttachmentKeepsTheLeaseOnItsPossibleChild()
    {
        var session = new FakeSession();
        var lease = new PreviewLease();
        lease.BeginAttachment();
        lease.Publish(session);
        lease.ResetAttachment();
        Assert.Same(session, lease.Session);
    }

    [Fact]
    public async Task AnUnfinishedDetachIsAbortedAndAFinishedOneIsNot()
    {
        var unfinished = new FakeSession();
        var lease = new PreviewLease();
        lease.Publish(unfinished);
        _ = lease.BeginDeactivation(unfinished);
        Assert.Same(unfinished, lease.Session);
        lease.Abort("removed");
        Assert.Equal(["removed"], unfinished.Aborts);
        Assert.Null(lease.Session);

        // The continuation that would end this lease is still queued, and another bridge may already hold the session.
        var finished = new FakeSession();
        lease.Publish(finished);
        Task detach = lease.BeginDeactivation(finished);
        finished.CompleteDetach();
        await detach;
        lease.Abort("removed");
        Assert.Empty(finished.Aborts);
        Assert.Null(lease.Session);
    }

    [Fact]
    public async Task AFailedDetachStillNeedsAnAbort()
    {
        var session = new FakeSession();
        var lease = new PreviewLease();
        lease.Publish(session);
        Task detach = lease.BeginDeactivation(session);
        session.FailDetach();
        await Assert.ThrowsAsync<InvalidOperationException>(() => detach);
        lease.Abort("communication failed");
        Assert.Equal(["communication failed"], session.Aborts);
    }

    [Fact]
    public void AnAbortThatCannotConfirmTeardownKeepsTheLease()
    {
        var session = new FakeSession { AbortFailure = new InvalidOperationException("still attached") };
        var lease = new PreviewLease();
        lease.Publish(session);
        Assert.Throws<InvalidOperationException>(() => lease.Abort("removed"));
        Assert.Same(session, lease.Session);

        session.AbortFailure = null;
        lease.Abort("removed");
        Assert.Null(lease.Session);
    }

    [Fact]
    public void EndingADetachLeavesANewerLeaseAlone()
    {
        FakeSession earlier = new(), newer = new();
        var lease = new PreviewLease();
        lease.Publish(earlier);
        _ = lease.BeginDeactivation(earlier);
        lease.Abort("removed");
        lease.Publish(newer);
        lease.EndDeactivation(earlier);
        Assert.Same(newer, lease.Session);

        lease.EndDeactivation(newer);
        Assert.Null(lease.Session);
    }

    [Fact]
    public async Task PublishingForgetsAnEarlierDetachOfTheSameSession()
    {
        var session = new FakeSession();
        var lease = new PreviewLease();
        lease.Publish(session);
        Task detach = lease.BeginDeactivation(session);
        session.CompleteDetach();
        await detach;

        lease.Publish(session);
        lease.Abort("removed");
        Assert.Equal(["removed"], session.Aborts);
    }

    private sealed class FakeSession : IPreviewInteractionSession
    {
        private readonly TaskCompletionSource _detach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Aborts { get; } = [];
        public Exception? AbortFailure { get; set; }
        public PreviewSurfaceIdentity Surface => throw new NotSupportedException();
        public bool IsAvailable => true;
        public bool IsAttached => true;
#pragma warning disable CS0067 // The lease never listens to the session.
        public event EventHandler<PreviewInteractionEvent>? Changed;
#pragma warning restore CS0067
        public Task<PreviewSurfaceResponse> UpdateAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeactivateAsync(CancellationToken cancellationToken = default) => _detach.Task;
        public void Abort(string reason)
        {
            if (AbortFailure is not null) throw AbortFailure;
            Aborts.Add(reason);
        }
        public void CompleteDetach() => _detach.SetResult();
        public void FailDetach() => _detach.SetException(new InvalidOperationException("detach failed"));
    }
}
