using System.Collections.Concurrent;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts.Diagnostics;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

/// <summary>The UI thread monitor on a real dispatcher: what it records, and under which names.</summary>
public sealed class UiThreadMonitorTests
{
    private const string Class = nameof(UiThreadMonitorTests);
    private sealed record Observed(IReadOnlyList<PerfStat> Stats, IReadOnlyList<SlowOperation> Slow)
    {
        public PerfStat? Find(string area, string name) => Stats.FirstOrDefault(stat => stat.Area == area && stat.Name == name);
        public string Names => string.Join(Environment.NewLine, Stats.Select(stat => $"{stat.Area}: {stat.Name} ({stat.MaxMilliseconds:0} ms)"));
    }

    [Fact]
    public async Task APostedCallbackIsTimedUnderTheMethodThatPostedIt()
    {
        var observed = await ObserveAsync(async dispatcher => await dispatcher.InvokeAsync(() => Thread.Sleep(40)));
        var posted = observed.Find(PerfAreas.Ui, Class + "." + nameof(APostedCallbackIsTimedUnderTheMethodThatPostedIt) + " (lambda)");
        Assert.True(posted is { MaxMilliseconds: >= 35 }, observed.Names);
    }

    [Fact]
    public async Task AnAwaitThatResumesOnTheUiThreadIsTimedUnderItsAsyncMethod()
    {
        var observed = await ObserveAsync(_ => WorkAfterAwaitAsync());
        var resumed = observed.Find(PerfAreas.Ui, Class + "." + nameof(WorkAfterAwaitAsync) + " (resumed)");
        Assert.True(resumed is { MaxMilliseconds: >= 35 }, observed.Names);
    }
    private static async Task WorkAfterAwaitAsync()
    {
        await Task.Delay(20);
        Thread.Sleep(40);
    }

    [Fact]
    public async Task TypingIsTimedFromBeforeItsHandlersToAfterThemUnderTheEventsName()
    {
        var observed = await ObserveAsync(_ =>
        {
            var box = new TextBox();
            box.PreviewTextInput += (_, _) => Thread.Sleep(60);
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, "a"));
            return Task.CompletedTask;
        });
        var typed = observed.Find(PerfAreas.Input, "PreviewTextInput");
        Assert.True(typed is { Count: 1, MaxMilliseconds: >= 55 }, observed.Names);
        // Over the limit for input, so it is reported by itself, and nothing in the report says what was typed.
        Assert.Contains(observed.Slow, operation => operation is { Area: PerfAreas.Input, Name: "PreviewTextInput", Milliseconds: >= 55 });
    }

    [Fact]
    public async Task ABusyUiThreadIsReportedWithWhatKeptItBusy()
    {
        var observed = await ObserveAsync(async dispatcher => await dispatcher.InvokeAsync(() => Thread.Sleep(600)));
        // The longest, because a cold start can keep the thread from its first answer too.
        var stall = observed.Slow.Where(operation => operation.Area == PerfAreas.UiLatency).MaxBy(operation => operation.Milliseconds);
        Assert.True(stall is { Name: "UI thread", Milliseconds: >= 400 }, observed.Names);
        Assert.Contains("the longest", stall.Detail);
        Assert.EndsWith(Class + "." + nameof(ABusyUiThreadIsReportedWithWhatKeptItBusy) + " (lambda)", stall.Detail!.Split(" · ")[0]);
        // The wait is measured whether or not it is long: most answers are prompt.
        Assert.True(observed.Find(PerfAreas.UiLatency, "UI thread") is { Count: >= 3 }, observed.Names);
    }

    [Fact]
    public async Task AnOperationThatRunsItsOwnMessageLoopIsNotMistakenForALongOne()
    {
        var observed = await ObserveAsync(async dispatcher => await dispatcher.InvokeAsync(() =>
        {
            // As a dialog does: the operation stays open while the thread goes on serving others.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) => frame.Continue = false, dispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }));
        Assert.Null(observed.Find(PerfAreas.Ui, Class + "." + nameof(AnOperationThatRunsItsOwnMessageLoopIsNotMistakenForALongOne) + " (lambda)"));
        Assert.DoesNotContain(observed.Slow, operation => operation is { Area: PerfAreas.UiLatency, Milliseconds: >= 400 });
    }

    [Fact]
    public async Task SlowOperationsReachTheLogAndALongStallIsNoticedOnce()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-PerfLog-").FullName;
        try
        {
            var perf = new PerfMonitor();
            var notices = new List<string>();
            string report;
            using (var log = new PerfLog(perf, directory))
            {
                log.Noticed += notices.Add;
                perf.Record(PerfAreas.Input, "TextInput", TimeSpan.FromMilliseconds(80));
                perf.Record(PerfAreas.WorkerCall, "GetCompletionsAsync", TimeSpan.FromMilliseconds(40));
                perf.Publish(new(DateTimeOffset.Now, "worker", PerfAreas.Worker, "UpdateDocumentAsync", 900, "GC paused 610 ms (1 gen2)"));
                perf.Record(PerfAreas.UiLatency, "UI thread", TimeSpan.FromMilliseconds(700), "3 operations and input events took 690 ms");
                perf.Record(PerfAreas.UiLatency, "UI thread", TimeSpan.FromMilliseconds(800));
                for (int index = 0; index < 100; index++) perf.Record(PerfAreas.Input, "KeyDown", TimeSpan.FromMilliseconds(60));
                report = await log.ReportAsync(workspace: null, reset: true);
                Assert.Equal(Path.Combine(directory, "Logs", "performance.log"), log.Path);
            }
            var lines = File.ReadAllLines(Path.Combine(directory, "Logs", "performance.log"));
            Assert.StartsWith("==== WpfStudio ", lines[1]);
            Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} shell  input\s+80 ms  TextInput$", lines[2]);
            Assert.EndsWith("worker worker           900 ms  UpdateDocumentAsync · GC paused 610 ms (1 gen2)", lines[3]);
            // A burst is cut off, so one slow thing cannot fill the log, and the call that was fast is not in it at all.
            Assert.Equal(60, lines.Count(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} (shell |worker) ")));
            Assert.DoesNotContain(lines, line => line.Contains("GetCompletionsAsync") && !line.Contains("worker call "));
            Assert.Equal("The UI did not respond for 700 ms: 3 operations and input events took 690 ms. Help > Performance report shows where the time goes.", Assert.Single(notices));
            // The report is in the log too, with what was fast as well as what was slow.
            Assert.StartsWith("IDE, last ", report);
            Assert.Matches(@"(?m)^\s+worker call\s+1\s.*\sGetCompletionsAsync\r?$", report);
            Assert.Contains(lines, line => line.EndsWith(" report", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.EndsWith("GetCompletionsAsync", StringComparison.Ordinal));
            Assert.Empty(perf.Snapshot());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ATextChangeIsTimedInTheEditorsPartsOnceTheEditorHasAMonitor()
    {
        var perf = new PerfMonitor();
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Timed.cs"), "class Timed { }");
        // The constructor's own first analysis runs before the monitor is assigned, and is not counted.
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { }) { Performance = perf };
        Assert.Empty(perf.Snapshot());
        state.Content += " ";
        state.Content += " ";
        var parts = perf.Snapshot().Where(stat => stat.Area == PerfAreas.Edit).ToArray();
        Assert.Equal(["text changed: Git margin and blame", "text changed: analysis start", "text changed: context subscribers"], parts.Select(stat => stat.Name).Order(StringComparer.Ordinal));
        Assert.All(parts, stat => Assert.Equal(2, stat.Count));
    }

    /// <summary>Runs a scenario on a dispatcher thread of its own with a monitor attached, and returns what the monitor recorded.</summary>
    private static Task<Observed> ObserveAsync(Func<Dispatcher, Task> scenario)
    {
        var completion = new TaskCompletionSource<Observed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var perf = new PerfMonitor();
            var slow = new ConcurrentQueue<SlowOperation>();
            perf.Slow += slow.Enqueue;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    using (var monitor = new UiThreadMonitor(dispatcher, perf))
                    {
                        monitor.Start();
                        await scenario(dispatcher);
                        // Long enough for the probe to be answered after the scenario's last work.
                        await Task.Delay(400);
                    }
                    completion.SetResult(new(perf.Snapshot(), slow.ToArray()));
                }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }
}
