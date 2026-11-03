using System.Collections.Concurrent;
using System.Diagnostics;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>Drives the real process and transport handling against a scripted adapter; netcoredbg itself is not involved.</summary>
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

    private sealed class AdapterRun : IAsyncDisposable
    {
        private Process? _adapter;
        public DebugSession Session { get; } = new() { AdapterPath = LocateAdapter() };
        public ConcurrentBag<string> Errors { get; } = [];

        public AdapterRun(string scenario)
        {
            // The adapter inherits this. The tests in this class run one at a time.
            Environment.SetEnvironmentVariable("WPFSTUDIO_FAKE_DAP_SCENARIO", scenario);
            Session.Error += Errors.Add;
        }

        public async Task AttachAsync(int seconds)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var attach = Session.AttachAsync(Environment.ProcessId, [], cancellationToken: deadline.Token);
            // The process starts before the session's first real await.
            if (Session.AdapterProcessId is { } id) _adapter = Process.GetProcessById(id);
            await attach;
        }

        public bool AdapterEnded() => _adapter?.WaitForExit(10_000) == true;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            if (_adapter is null) return;
            try { if (!_adapter.HasExited) _adapter.Kill(); } catch (InvalidOperationException) { }
            _adapter.Dispose();
        }

        private static string LocateAdapter()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = directory.Parent!.Name;
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfStudio.Runtime.Tests.csproj"))) directory = directory.Parent;
            return Path.Combine(directory!.FullName, "Fixtures", "FakeDapAdapter", "bin", configuration, "net10.0", "WpfStudio.FakeDapAdapter.exe");
        }
    }
}
