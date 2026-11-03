using System.Collections.Concurrent;
using System.Diagnostics;

namespace WpfStudio.Runtime.Tests;

/// <summary>Drives the real process and transport handling against a scripted adapter; netcoredbg itself is not involved.</summary>
[Collection(AdapterRun.Collection)]
public sealed class DebugSessionTests
{
    [Fact]
    public async Task AFrameWithMissingFieldsIsSkippedAndTheSessionKeepsWorking()
    {
        await using var run = new AdapterRun("missing-fields");
        await run.AttachAsync(seconds: 10);
        Assert.True(run.Session.IsActive);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task AnUnreadableFrameStillEndsTheAdapterProcess()
    {
        await using var run = new AdapterRun("bad-header");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => run.AttachAsync(seconds: 1));
        Assert.True(failure is InvalidDataException or OperationCanceledException, failure.ToString());
        Assert.True(run.AdapterEnded(), "The adapter was left running.");
        Assert.False(run.Session.IsActive);
    }

    [Fact]
    public async Task AnAdapterThatIgnoresDisconnectIsEndedWithoutHanging()
    {
        await using var run = new AdapterRun("ignore-disconnect");
        await run.AttachAsync(seconds: 10);
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Session.StopAsync(cancellationToken: deadline.Token));
        Assert.True(run.AdapterEnded(), "The adapter was left running.");
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 15);
    }

    [Fact]
    public async Task ARejectedAttachIsReportedAndNotLeftUnobserved()
    {
        var unobserved = new ConcurrentBag<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> collect = (_, args) => unobserved.Add(args.Exception);
        TaskScheduler.UnobservedTaskException += collect;
        try
        {
            await using var run = new AdapterRun("reject-attach");
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run.AttachAsync(seconds: 1));
            Assert.Equal("attach rejected", failure.Message);
            Assert.True(run.AdapterEnded(), "The adapter was left running.");
        }
        finally
        {
            for (var pass = 0; pass < 3; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(20); }
            TaskScheduler.UnobservedTaskException -= collect;
        }
        Assert.DoesNotContain(unobserved, fault => fault.GetBaseException().Message == "attach rejected");
    }

    [Fact]
    public async Task AdapterExitAfterTerminatedIsNotReportedAsAnError()
    {
        await using var run = new AdapterRun("terminated");
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Session.EventReceived += message => { if (message.Name == "terminated") terminated.TrySetResult(); };
        await run.AttachAsync(seconds: 10);
        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run.AdapterEnded(), "The adapter did not exit by itself.");
        await Task.Delay(300); // The reader reports the closed stream just after the process exits.
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task AdapterExitWithoutTerminatedIsReportedAsAnError()
    {
        await using var run = new AdapterRun("crash");
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Session.Error += message => reported.TrySetResult(message);
        await run.AttachAsync(seconds: 10);
        Assert.Equal("The debugger connection closed.", await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
