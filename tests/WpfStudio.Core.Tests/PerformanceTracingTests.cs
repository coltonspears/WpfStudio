using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using WpfStudio.Contracts.Diagnostics;
using WpfStudio.Core.IO;

namespace WpfStudio.Core.Tests;

public sealed class PerformanceTracingTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void EveryMeasurementIsCountedAndPercentilesStayWithinAQuarterOfTheTruth()
    {
        var perf = new PerfMonitor();
        for (int index = 0; index < 100; index++) perf.Record(PerfAreas.WorkerCall, "GetCompletionsAsync", Ms(10));
        for (int index = 0; index < 5; index++) perf.Record(PerfAreas.WorkerCall, "GetCompletionsAsync", Ms(400));
        var stat = Assert.Single(perf.Snapshot());
        Assert.Equal((PerfAreas.WorkerCall, "GetCompletionsAsync", 105L), (stat.Area, stat.Name, stat.Count));
        Assert.Equal(3000, stat.TotalMilliseconds, 3);
        Assert.InRange(stat.MedianMilliseconds, 10, 12.5);
        Assert.InRange(stat.P95Milliseconds, 10, 12.5);
        Assert.Equal(400, stat.MaxMilliseconds, 3);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(3)]
    [InlineData(47)]
    [InlineData(999)]
    [InlineData(65_000)]
    public void APercentileIsNeverBelowTheMeasurementAndNeverAboveTheLongest(double milliseconds)
    {
        var perf = new PerfMonitor();
        perf.Record("area", "one", Ms(milliseconds));
        var stat = Assert.Single(perf.Snapshot());
        Assert.Equal(stat.MaxMilliseconds, stat.P95Milliseconds, 6);
        perf.Record("area", "one", Ms(milliseconds * 100));
        stat = Assert.Single(perf.Snapshot());
        Assert.InRange(stat.MedianMilliseconds, milliseconds, Math.Max(milliseconds * 1.25, 0.064));
    }

    [Fact]
    public void OnlyAMeasurementOverItsAreasLimitIsReportedAndOnlyThenIsItsDetailAskedFor()
    {
        var perf = new PerfMonitor("worker");
        var reported = new List<SlowOperation>();
        perf.Slow += reported.Add;
        int described = 0;
        string? Detail() { described++; return "GC paused 80 ms (1 gen2)"; }
        perf.Record(PerfAreas.Input, "TextInput", Ms(49), Detail);
        Assert.Empty(reported);
        Assert.Equal(0, described);
        perf.Record(PerfAreas.Input, "TextInput", Ms(51), Detail);
        perf.Record(PerfAreas.WorkerCall, "GetCompletionsAsync", Ms(51));
        var slow = Assert.Single(reported);
        Assert.Equal(("worker", PerfAreas.Input, "TextInput", "GC paused 80 ms (1 gen2)"), (slow.Source, slow.Area, slow.Name, slow.Detail));
        Assert.Equal(51, slow.Milliseconds, 3);
        Assert.Equal(1, described);
        Assert.Equal(2, perf.Snapshot().Single(stat => stat.Area == PerfAreas.Input).Count);
    }

    [Fact]
    public void AFailingListenerOrDetailDoesNotReachTheCodeThatWasMeasured()
    {
        var perf = new PerfMonitor(slowAfter: _ => TimeSpan.Zero);
        perf.Slow += _ => throw new InvalidOperationException("listener");
        perf.Record("area", "name", Ms(1));
        perf.Record("area", "name", Ms(1), () => throw new InvalidOperationException("detail"));
        Assert.Equal(2, Assert.Single(perf.Snapshot()).Count);
    }

    [Fact]
    public void AMeasurementReportedFromElsewhereIsPassedOnWithoutBeingCountedHere()
    {
        var perf = new PerfMonitor();
        var reported = new List<SlowOperation>();
        perf.Slow += reported.Add;
        var fromWorker = new SlowOperation(DateTimeOffset.Now, "worker", PerfAreas.Worker, "UpdateDocumentAsync", 900);
        perf.Publish(fromWorker);
        Assert.Same(fromWorker, Assert.Single(reported));
        Assert.Empty(perf.Snapshot());
    }

    [Fact]
    public async Task ACallIsTimedUnderItsOutcomeAndItsResultOrFailureComesBackUnchanged()
    {
        var perf = new PerfMonitor();
        Assert.Equal(7, await perf.TimeAsync("call", "Answered", PerfMonitor.Now, Task.FromResult(7)));
        Assert.Equal(8, await perf.TimeAsync("call", "Answered", PerfMonitor.Now, Later(8)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => perf.TimeAsync("call", "Abandoned", PerfMonitor.Now, Task.FromCanceled<int>(cancelled.Token)));
        Assert.Equal(cancelled.Token, cancellation.CancellationToken);
        var failure = new InvalidOperationException("lost");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => perf.TimeAsync("call", "Broken", PerfMonitor.Now, Task.FromException<int>(failure))));
        await perf.TimeAsync("call", "Untyped", PerfMonitor.Now, Task.Delay(1));
        Assert.Equal(["Abandoned (cancelled)", "Answered", "Broken (failed)", "Untyped"], perf.Snapshot().Select(stat => stat.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, perf.Snapshot().Single(stat => stat.Name == "Answered").Count);

        static async Task<int> Later(int value) { await Task.Delay(5); return value; }
    }

    [Fact]
    public void ResetStartsCountingAgainAndABlockIsTimedByItsScope()
    {
        var perf = new PerfMonitor();
        using (perf.Measure("area", "block")) Thread.Sleep(20);
        var stat = Assert.Single(perf.Snapshot());
        Assert.InRange(stat.MaxMilliseconds, 15, 5000);
        perf.Reset();
        Assert.Empty(perf.Snapshot());
        Assert.InRange(perf.Elapsed.TotalSeconds, 0, 5);
        default(PerfScope).Dispose();
    }

    [Fact]
    public void AResetWhileOthersRecordLeavesEveryMeasurementWholeInOnePeriod()
    {
        var perf = new PerfMonitor();
        var counter = perf.Counter("area", "busy");
        var one = Ms(1);
        for (int round = 0; round < 200; round++)
        {
            bool stop = false;
            var recorders = Enumerable.Range(0, 3).Select(_ => new Thread(() => { while (!Volatile.Read(ref stop)) counter.Record(one); })).ToArray();
            foreach (var recorder in recorders) recorder.Start();
            Thread.SpinWait(20_000);
            perf.Reset();
            Volatile.Write(ref stop, true);
            foreach (var recorder in recorders) recorder.Join();
            // Nothing records now, so what the new period holds is final. Every measurement was one millisecond: a count
            // that the total or the longest does not match is a measurement that the reset cut in two.
            if (perf.Snapshot().SingleOrDefault() is not { } stat) continue;
            Assert.Equal(stat.Count, stat.TotalMilliseconds, 6);
            Assert.Equal(1, stat.MaxMilliseconds, 6);
        }
    }

    [Fact]
    public void NamesBeyondTheLimitShareOneCounterSoAMistakeCannotGrowWithoutBound()
    {
        var perf = new PerfMonitor();
        for (int index = 0; index < 1500; index++) perf.Record("area", "name " + index, Ms(1));
        var stats = perf.Snapshot();
        Assert.InRange(stats.Count, 1000, 1025);
        Assert.Equal(1500, stats.Sum(stat => stat.Count));
        Assert.Contains(stats, stat => stat.Name == "(other)" && stat.Count > 400);
    }

    [Fact]
    public void ReportListsEachOperationUnderItsAreaWithTheLargestTotalFirst()
    {
        var perf = new PerfMonitor();
        perf.Record(PerfAreas.Ui, "EditorViewModel.AnalyzeAsync (resumed)", Ms(20));
        for (int index = 0; index < 3; index++) perf.Record(PerfAreas.Ui, "MediaContext.RenderMessageHandler", Ms(30));
        perf.Record(PerfAreas.Input, "TextInput", Ms(1500));
        string[] lines = PerfReport.Format(perf, "IDE").Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("IDE, last ", lines[0]);
        Assert.Contains("GC: ", lines[0]);
        Assert.Matches(@"^\s+area\s+count\s+total\s+p50\s+p95\s+max\s+name$", lines[1]);
        Assert.Matches(@"^\s+input\s+1\s+1\.50 s\s+1\.50 s\s+1\.50 s\s+1\.50 s\s+TextInput$", lines[2]);
        Assert.Matches(@"^\s+ui\s+3\s+90 ms\s+3\d ms\s+3\d ms\s+30 ms\s+MediaContext\.RenderMessageHandler$", lines[3]);
        Assert.EndsWith("EditorViewModel.AnalyzeAsync (resumed)", lines[4]);
        Assert.Contains("nothing recorded", PerfReport.Format(new PerfMonitor(), "Language worker"));
        Assert.Equal("worker worker           900 ms  UpdateDocumentAsync · GC paused 610 ms (1 gen2)",
            PerfReport.Line(new(DateTimeOffset.Now, "worker", PerfAreas.Worker, "UpdateDocumentAsync", 900, "GC paused 610 ms (1 gen2)")));
    }

    [Fact]
    public void ADelegateIsNamedAfterTheMethodAPersonWrote()
    {
        int captured = 1;
        Action capturing = () => captured++;
        Action plain = static () => { };
        Assert.Equal("PerformanceTracingTests.ADelegateIsNamedAfterTheMethodAPersonWrote (lambda)", PerfNames.Of(capturing));
        Assert.Equal("PerformanceTracingTests.ADelegateIsNamedAfterTheMethodAPersonWrote (lambda)", PerfNames.Of(plain));
        Assert.Equal("PerformanceTracingTests.ADelegateIsNamedAfterTheMethodAPersonWrote.Local", PerfNames.Of((Action)Local));
        Assert.Equal("PerformanceTracingTests.Ms", PerfNames.Of((Func<double, TimeSpan>)Ms));
        Assert.Equal("List.Clear", PerfNames.Of((Action)new List<int>().Clear));
        Assert.Null(PerfNames.AsyncMethod(typeof(List<int>)));
        Assert.Null(PerfNames.AsyncMethod(typeof(string)));

        static void Local() { }
    }

    [Fact]
    public async Task AnAwaitsContinuationIsNamedAfterTheAsyncMethodItResumes()
    {
        var suspended = new Suspended();
        var running = ResumeLaterAsync(suspended);
        var continuation = suspended.Continuation!;
        Assert.Equal("PerformanceTracingTests.ResumeLaterAsync (resumed)", PerfNames.AsyncMethod(continuation.Target!.GetType()));
        Assert.Equal("PerformanceTracingTests.ResumeLaterAsync (resumed)", PerfNames.Of(continuation));
        continuation();
        await running;

        // An async lambda and an async local function have state machines of their own, named after what contains them.
        Func<Suspended, Task> lambda = async awaited => await awaited;
        var inLambda = new Suspended();
        running = lambda(inLambda);
        Assert.Equal("PerformanceTracingTests.AnAwaitsContinuationIsNamedAfterTheAsyncMethodItResumes (lambda, resumed)", PerfNames.Of(inLambda.Continuation!));
        inLambda.Continuation!();
        await running;
        var inLocal = new Suspended();
        running = LocalAsync(inLocal);
        Assert.Equal("PerformanceTracingTests.AnAwaitsContinuationIsNamedAfterTheAsyncMethodItResumes.LocalAsync (resumed)", PerfNames.Of(inLocal.Continuation!));
        inLocal.Continuation!();
        await running;

        static async Task LocalAsync(Suspended awaited) => await awaited;
    }
    private static async Task ResumeLaterAsync(Suspended suspended) => await suspended;
    private sealed class Suspended : INotifyCompletion
    {
        public Action? Continuation { get; private set; }
        public bool IsCompleted => false;
        public Suspended GetAwaiter() => this;
        public void OnCompleted(Action continuation) => Continuation = continuation;
        public void GetResult() { }
    }

    [Fact]
    public void TheCollectorsWorkBetweenTwoMomentsIsDescribedOnlyWhenItRan()
    {
        var before = GcSnapshot.Take();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        Assert.Matches(@"^GC paused \d+ ms \(.*1 gen2.*\)$", before.Since());
        // Another test's allocations can collect at any moment, so a quiet interval is looked for, not assumed.
        Assert.Contains(Enumerable.Range(0, 50), _ => GcSnapshot.Take().Since() is null);
    }

    [Fact]
    public async Task AProbeRecordsHowLongItsTargetTookToAnswer()
    {
        var perf = new PerfMonitor(slowAfter: _ => Ms(200));
        var slow = new TaskCompletionSource<SlowOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        perf.Slow += operation => slow.TrySetResult(operation);
        var posted = new BlockingCollection<Action>();
        int windows = 0;
        using var probe = new ResponsivenessProbe(perf, PerfAreas.UiLatency, "UI thread", posted.Add, answered => answered ? "3 operations took 400 ms" : "in Editor.Layout",
            () => windows++, interval: Ms(10), silentAfter: TimeSpan.FromMinutes(1));
        probe.Start();
        // A prompt answer is counted and not reported.
        Assert.True(posted.TryTake(out var first, TimeSpan.FromSeconds(10)));
        first();
        // The next is kept waiting, as a busy UI thread would keep it.
        Assert.True(posted.TryTake(out var second, TimeSpan.FromSeconds(10)));
        await Task.Delay(500);
        Assert.Empty(posted); // Nothing more is handed over while one is outstanding.
        second();
        var reported = await slow.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((PerfAreas.UiLatency, "UI thread", "3 operations took 400 ms"), (reported.Area, reported.Name, reported.Detail));
        Assert.InRange(reported.Milliseconds, 400, 60_000);
        Assert.Equal(2, windows);
        Assert.Equal(2, perf.Snapshot().Single().Count);
    }

    [Fact]
    public async Task AProbeReportsATargetThatStaysSilentOnceWhileItIsStillSilent()
    {
        var perf = new PerfMonitor();
        var reported = new ConcurrentQueue<SlowOperation>();
        perf.Slow += reported.Enqueue;
        using var probe = new ResponsivenessProbe(perf, PerfAreas.UiLatency, "UI thread", _ => { }, answered => answered ? "answered" : "in Editor.Layout for 3.00 s",
            interval: Ms(10), silentAfter: Ms(100));
        probe.Start();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (reported.IsEmpty && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        await Task.Delay(200);
        var silent = Assert.Single(reported);
        Assert.Equal(("UI thread (no answer yet)", "in Editor.Layout for 3.00 s"), (silent.Name, silent.Detail));
        Assert.InRange(silent.Milliseconds, 100, 60_000);
        Assert.Empty(perf.Snapshot()); // An unanswered probe is not a measurement yet.
    }

    [Fact]
    public async Task TheThreadPoolProbeSeesAPoolThatAnswers()
    {
        var perf = new PerfMonitor();
        using var probe = ResponsivenessProbe.ForThreadPool(perf);
        probe.Start();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (perf.Snapshot().Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        var stat = Assert.Single(perf.Snapshot());
        Assert.Equal((PerfAreas.PoolLatency, "thread pool"), (stat.Area, stat.Name));
    }

    [Fact]
    public void QueuedLogLinesReachTheFileInOrderAndAFullFileIsSetAside()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-QueuedLog-").FullName;
        try
        {
            var log = new QueuedLogFile(directory, "performance.log");
            Assert.Equal(Path.Combine(directory, "Logs", "performance.log"), log.Path);
            for (int line = 0; line < 500; line++) log.Write("line " + line + Environment.NewLine);
            log.Dispose();
            var lines = File.ReadAllLines(log.Path);
            Assert.Equal(500, lines.Length);
            Assert.Equal(("line 0", "line 499"), (lines[0], lines[^1]));
            log.Write("after dispose"); // Too late to be written, and must not throw.
            log.Dispose();

            File.WriteAllText(log.Path, new string('x', 2 * 1024 * 1024 + 1));
            using (var next = new QueuedLogFile(directory, "performance.log")) next.Write("fresh" + Environment.NewLine);
            Assert.Equal("fresh" + Environment.NewLine, File.ReadAllText(log.Path));
            Assert.Equal(2 * 1024 * 1024 + 1, new FileInfo(log.Path + ".previous").Length);
        }
        finally { Directory.Delete(directory, true); }
    }
}
