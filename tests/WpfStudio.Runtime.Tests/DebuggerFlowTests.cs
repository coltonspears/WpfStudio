using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>The view model against the scripted adapter process, so the paused-state flow is covered where netcoredbg is not installed.</summary>
[Collection(AdapterRun.Collection)]
public sealed class DebuggerFlowTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private const string Source = @"C:\src\Program.cs";
    private const string Workspace = @"C:\src";

    [Fact]
    public Task AStopFillsThreadsFramesLocalsAndWatches() => WithAttachedModelAsync("debug-step", async model =>
    {
        await Until(() => model.Watches.Count == 1 && model.Watches[0].Type == "int");
        Assert.True(model.IsStopped);
        Assert.True(model.IsActive);
        Assert.Equal("Paused · step", model.Status);
        Assert.Equal("", model.Error);
        Assert.Equal(["Main Thread", "Worker"], model.Threads.Select(thread => thread.Name));
        Assert.Equal(7, model.SelectedThread?.Id);
        Assert.Equal([new DebugFrame(1, "Program.Main", Source, 12), new DebugFrame(2, "[External Code]", null, 0)], model.Frames);
        Assert.Equal(1, model.SelectedFrame?.Id);
        Assert.Equal(["answer", "items"], model.Locals.Select(local => local.Name));
        Assert.Equal("value of a", model.Watches[0].Value);
        Assert.Equal("", model.ExceptionDetails);
    });

    [Fact]
    public Task SelectingAFrameRequestsItsSourceOnlyWhenItHasOne() => WithAttachedModelAsync("debug-step", async model =>
    {
        var requested = new List<(string Path, int Line)>();
        _messenger.Register<DebuggerSourceMessage>(this, (_, source) => requested.Add((source.Path, source.Line)));
        await Until(() => model.Frames.Count == 2 && model.Locals.Count == 2);
        requested.Clear();
        model.SelectedFrame = model.Frames[1];
        Assert.Empty(requested);
        model.SelectedFrame = model.Frames[0];
        Assert.Equal((Source, 12), Assert.Single(requested));
    });

    [Fact]
    public Task AppliedBreakpointsTakeTheAdaptersIdAndResolvedLine() => WithAttachedModelAsync("debug-step", async model =>
    {
        var breakpoint = model.Breakpoints.Single();
        await Until(() => breakpoint.Status == "Bound");
        await Until(() => breakpoint.AdapterId == 100);
        Assert.Equal(13, breakpoint.BoundLine);
        Assert.Equal(Source, breakpoint.Path);
    });

    [Fact]
    public Task VariablesExpandInPagesOfTwoHundred() => WithAttachedModelAsync("debug-step", async model =>
    {
        await Until(() => model.Locals.Count == 2);
        var answer = model.Locals[0];
        Assert.Equal(("42", "int", 0), (answer.Value, answer.Type, answer.Reference));
        Assert.Empty(answer.Children);
        var items = model.Locals[1];
        Assert.Equal("Expand to load", Assert.Single(items.Children).Name);
        items.IsExpanded = true;
        await Until(() => items.Children.Count == 201);
        Assert.Equal("[0]", items.Children[0].Name);
        Assert.Equal("398", items.Children[199].Value);
        var next = items.Children[200];
        Assert.Equal(("Next 200 values…", "Expand to load"), (next.Name, next.Value));
        next.IsExpanded = true;
        await Until(() => next.Children.Count == 5);
        Assert.Equal("[204]", next.Children[4].Name);
    });

    [Fact]
    public Task AnExceptionStopShowsItsDetails() => WithAttachedModelAsync("debug-exception", async model =>
    {
        await Until(() => model.ExceptionDetails.Length > 0);
        Assert.Equal("System.Exception\nboom\n{\"message\":\"boom\"}", model.ExceptionDetails);
        Assert.Equal("Paused · exception", model.Status);
    });

    [Fact]
    public Task DataTipsDescribeTheValueAndAreEmptyWhenNotPaused() => WithAttachedModelAsync("debug-step", async model =>
    {
        Assert.Null(await new DebuggerViewModel(new DebugSession(), new DispatcherPost(Dispatcher.CurrentDispatcher), new NoDebugAdapter(), _messenger).EvaluateDataTipAsync("x"));
        await Until(() => model.SelectedFrame is not null);
        Assert.Equal("x = value of x  (int)", await model.EvaluateDataTipAsync("x"));
    });

    [Fact]
    public Task HitCountsResumeUntilTheConditionIsMet() => WithAttachedModelAsync("debug-breakpoint", async model =>
    {
        var breakpoint = model.Breakpoints.Single();
        await Until(() => model.IsStopped);
        Assert.Equal(2, breakpoint.HitCount);
        Assert.Equal("Paused · breakpoint", model.Status);
    }, breakpoint => breakpoint.HitCondition = "2");

    [Fact]
    public Task TracepointsLogAndResumeWithoutPausing() => WithAttachedModelAsync("debug-breakpoint", async model =>
    {
        var breakpoint = model.Breakpoints.Single();
        await Until(() => model.IsStopped);
        await Until(() => model.Output.Contains("hit 2", StringComparison.Ordinal));
        Assert.Equal(2, breakpoint.HitCount);
        Assert.Equal("Paused · step", model.Status);
        Assert.Contains("value of answer in Program.Main at Program.cs(12) on 7 hit 1", model.Output);
    }, breakpoint => breakpoint.LogMessage = "{answer} in $FUNCTION at $FILEPOS on $TID hit $HITCOUNT");

    [Fact]
    public Task AdapterEventsReachOutputAndBreakpoints() => WithAttachedModelAsync("debug-events", async model =>
    {
        var breakpoint = model.Breakpoints.Single();
        await Until(() => model.Output.Contains("hello from the debuggee", StringComparison.Ordinal));
        await Until(() => model.SelectedFrame is not null && breakpoint.AdapterId == 100);
        Assert.Equal("Paused · pause", model.Status);
        await model.EvaluateDataTipAsync("send-breakpoint-change");
        await Until(() => breakpoint.Status == "moved away");
        Assert.Equal(40, breakpoint.BoundLine);
    });

    [Fact]
    public Task ClearingOutputEmptiesTheShownText() => WithAttachedModelAsync("debug-events", async model =>
    {
        await Until(() => model.Output.Length > 0);
        model.ClearOutputCommand.Execute(null);
        Assert.Empty(model.Output);
    });

    [Fact]
    public Task AnAdapterThatOmitsFieldsStillPauses() => WithAttachedModelAsync("partial", async model =>
    {
        await Until(() => model.Watches.Count == 1 && model.Watches[0].Value == "");
        Assert.True(model.IsStopped);
        Assert.Equal("Paused · breakpoint", model.Status);
        Assert.Equal("", model.Error);
        Assert.Equal(new DebugThread(7, ""), Assert.Single(model.Threads));
        Assert.Equal(new DebugFrame(1, "", null, 0), Assert.Single(model.Frames));
        Assert.Equal("x", Assert.Single(model.Locals).Name);
        await Until(() => model.Breakpoints.Single().Status != "Pending");
        var breakpoint = model.Breakpoints.Single();
        Assert.Equal(("Unbound · symbols not loaded", null, null), (breakpoint.Status, breakpoint.AdapterId, breakpoint.BoundLine));
    });

    [Fact]
    public Task AnAdapterThatSendsTheWrongKindsStillPauses() => WithAttachedModelAsync("malformed", async model =>
    {
        await Until(() => model.ExceptionDetails.Length > 0);
        Assert.True(model.IsStopped);
        Assert.Equal("Paused · exception", model.Status);
        Assert.Equal("", model.Error);
        Assert.Equal(["9", "Main"], model.Threads.Select(thread => thread.Name));
        Assert.Empty(model.Frames);
        Assert.Equal("\n", model.ExceptionDetails);
    });

    [Fact]
    public Task EventsAndResponsesWithoutABodyStillPause() => WithAttachedModelAsync("no-body-events", async model =>
    {
        await Until(() => model.IsStopped);
        Assert.Equal("Paused · breakpoint", model.Status);
        await Until(() => model.SelectedThread is null);
        Assert.Empty(model.Threads);
        Assert.Equal("", model.Error);
        Assert.Equal("Pending", model.Breakpoints.Single().Status);
    });

    [Fact]
    public Task AnAdapterThatEndsDuringAttachLeavesTheSessionEnded() => WithAttachedModelAsync("debug-terminate-starting", model =>
    {
        AssertSessionEnded(model);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnAdapterThatEndsDuringLaunchLeavesTheSessionEnded() => WithAttachedModelAsync("debug-terminate-starting", model =>
    {
        AssertSessionEnded(model);
        return Task.CompletedTask;
    }, start: model => model.LaunchAsync(new DebugLaunchConfiguration(@"C:\src\App.exe", @"C:\src")));

    [Fact]
    public async Task OverlappingBreakpointTogglesLeaveTheNewestStateOnDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-DebuggerFlow-" + Guid.NewGuid().ToString("N"));
        TaskCompletionSource? hold = null;
        var store = new BreakpointStore(directory)
        {
            WriteFile = async (path, text) =>
            {
                if (Interlocked.Exchange(ref hold, null) is { } held) await held.Task;
                await File.WriteAllTextAsync(path, text);
            }
        };
        try
        {
            await WithAttachedModelAsync("debug-step", async model =>
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hold = release;
                var older = model.ToggleBreakpointAsync(Source, 20);
                var newer = model.ToggleBreakpointAsync(Source, 21);
                // Unserialized, the newer save finishes here and the older one then moves over it.
                await Task.WhenAny(newer, Task.Delay(300));
                release.SetResult();
                await Task.WhenAll(older, newer);
                Assert.Equal("", model.Error);
            }, store: store);
            var saved = new BreakpointStore(directory);
            await saved.LoadAsync(Workspace);
            Assert.Equal([12, 20, 21], saved.Breakpoints.Select(item => item.Line));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public Task ABreakpointChangeSeenBeforeTheAnswerIsNotLost() => WithAttachedModelAsync("debug-change-before-answer", model =>
    {
        var breakpoint = model.Breakpoints.Single();
        Assert.Equal(("moved away", 100, 40), (breakpoint.Status, breakpoint.AdapterId, breakpoint.BoundLine));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ABreakpointChangeThatOvertakesTheAnswerIsNotOverwritten() => WithAttachedModelAsync("debug-change-after-answer", async model =>
    {
        var breakpoint = model.Breakpoints.Single();
        var applying = model.ApplyBreakpointsCommand.ExecuteAsync(null);
        // The answer and then the event arrive while this thread is busy. The event's post outranks the answer's continuation, so it runs first, as it can with a real adapter.
        Thread.Sleep(500);
        await applying;
        await Until(() => breakpoint.Status == "moved away");
        Assert.Equal(40, breakpoint.BoundLine);
    }, posts: DispatcherPriority.Send);

    [Fact]
    public Task ABreakpointChangeHeldWhenTheSessionEndsIsDropped() => WithAttachedModelAsync("debug-change-then-terminate", async model =>
    {
        Assert.Equal(100, model.Breakpoints.Single().AdapterId);
        await model.ApplyBreakpointsCommand.ExecuteAsync(null);
        await Until(() => model.Status == "Session ended");
        Assert.False(model.IsActive);
        Assert.Equal("Pending", model.Breakpoints.Single().Status);
    });

    [Fact]
    public Task TerminationEndsTheSession() => WithAttachedModelAsync("debug-terminate", async model =>
    {
        await model.PauseCommand.ExecuteAsync(null);
        await Until(() => model.Status == "Session ended");
        Assert.False(model.IsActive);
        Assert.False(model.IsStopped);
        Assert.Equal("Pending", model.Breakpoints.Single().Status);
        Assert.Empty(model.Threads);
    });

    private static void AssertSessionEnded(DebuggerViewModel model)
    {
        Assert.False(model.IsActive);
        Assert.False(model.IsStopped);
        Assert.Equal("Session ended", model.Status);
        Assert.Equal("", model.Error);
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The debugger did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    /// <param name="breakpoint">Shapes the one breakpoint before the session starts.</param>
    /// <param name="start">How the session starts, when not by attaching.</param>
    private Task WithAttachedModelAsync(string scenario, Func<DebuggerViewModel, Task> test, Action<BreakpointViewModel>? breakpoint = null, Func<DebuggerViewModel, Task>? start = null, BreakpointStore? store = null, DispatcherPriority posts = DispatcherPriority.Normal)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    AdapterRun.UseScenario(scenario);
                    await using var model = new DebuggerViewModel(new DebugSession { AdapterPath = AdapterRun.LocateAdapter() }, new DispatcherPost(dispatcher, posts), new NoDebugAdapter(), _messenger, store ?? new BreakpointStore());
                    if (store is not null) await model.SetWorkspaceAsync(Workspace);
                    var item = new BreakpointViewModel { Path = Source, Line = 12 };
                    breakpoint?.Invoke(item);
                    model.Breakpoints.Add(item);
                    await model.AddWatchAsync("a");
                    model.AttachProcessId = Environment.ProcessId;
                    await (start is null ? model.AttachCommand.ExecuteAsync(null) : start(model));
                    await test(model);
                    result.TrySetResult();
                }
                catch (Exception exception) { result.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Debugger flow test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class DispatcherPost(Dispatcher dispatcher, DispatcherPriority priority = DispatcherPriority.Normal) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action, priority);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }
}
