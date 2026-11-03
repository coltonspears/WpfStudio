using System.Windows.Documents;
using System.Windows.Input;
using WpfStudio.App.Controls.Editor;

namespace WpfStudio.Shell.Tests;

public sealed class CompletionInputQueueTests
{
    private static CompletionInputQueue<string> Queue() => new(TimeSpan.FromMinutes(1));

    [Fact]
    public void NothingIsHeldWhileIdle()
    {
        var queue = Queue();
        Assert.False(queue.IsPending);
        Assert.False(queue.Queue("a"));
        Assert.False(queue.Queue(EditingCommands.Backspace, null));
    }

    [Fact]
    public void TextAndCommandsKeepTheirOrderAfterTheAcceptingPunctuation()
    {
        var queue = Queue();
        var pending = queue.Begin("target", "(");
        Assert.True(queue.IsPending);
        Assert.Equal("target", pending.Target);
        Assert.True(queue.Queue("4"));
        Assert.True(queue.Queue(EditingCommands.Backspace, "parameter"));
        Assert.True(queue.Queue("2"));
        Assert.Equal(
            [new QueuedInput("(", null, null), new QueuedInput("4", null, null), new QueuedInput(null, EditingCommands.Backspace, "parameter"), new QueuedInput("2", null, null)],
            pending.Input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AcceptingWithoutPunctuationQueuesNothing(string? punctuation) => Assert.Empty(Queue().Begin("target", punctuation).Input);

    [Fact]
    public void CopyAndUnroutedCommandsRunRatherThanWait()
    {
        var queue = Queue();
        var pending = queue.Begin("target", null);
        Assert.False(queue.Queue(ApplicationCommands.Copy, null));
        Assert.False(queue.Queue(new NotRouted(), null));
        Assert.Empty(pending.Input);
    }

    [Fact]
    public void ReplayEndsTheWaitAndPassesInputThrough()
    {
        var queue = Queue();
        var pending = queue.Begin("target", "(");
        bool replayed = false;
        queue.Replay(() =>
        {
            replayed = true;
            Assert.False(queue.IsPending);
            Assert.True(queue.IsReplaying);
            Assert.False(queue.Queue("typed by the replay"));
            Assert.False(queue.Queue(EditingCommands.Backspace, null));
        });
        Assert.True(replayed);
        Assert.False(queue.IsReplaying);
        Assert.Single(pending.Input);
    }

    [Fact]
    public void AFailedReplayLeavesTheQueueIdle()
    {
        var queue = Queue();
        var pending = queue.Begin("target", null);
        Assert.Throws<InvalidOperationException>(() => queue.Replay(() => throw new InvalidOperationException()));
        Assert.False(queue.IsReplaying);
        Assert.False(queue.IsPending);
        Assert.False(queue.Queue("a"));
    }

    [Fact]
    public void ALaterAcceptSupersedesTheEarlierOne()
    {
        var queue = Queue();
        var first = queue.Begin("first", null);
        Assert.True(queue.IsCurrent(first));
        queue.Replay(() => { });
        Assert.False(queue.IsCurrent(first));

        var second = queue.Begin("second", null);
        Assert.False(queue.IsCurrent(first));
        Assert.True(queue.IsCurrent(second));
        Assert.Same(second, queue.Current);
    }

    [Fact]
    public async Task AWaitThatTakesTooLongIsCancelled()
    {
        var pending = new CompletionInputQueue<string>(TimeSpan.FromMilliseconds(10)).Begin("target", null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromMinutes(1), pending.Cancellation.Token));
    }

    private sealed class NotRouted : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
