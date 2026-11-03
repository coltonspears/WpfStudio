using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>The view model against the scripted adapter process, so the paused-state flow is covered where netcoredbg is not installed.</summary>
[Collection(AdapterRun.Collection)]
public sealed class DebuggerFlowTests
{
    private const string Source = "C:/src/Program.cs";

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
        model.SourceRequested += (path, line) => requested.Add((path, line));
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
        Assert.Equal(Source.Replace('/', '\\'), breakpoint.Path);
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
        Assert.Null(await new DebuggerViewModel(new DebugSession(), new DispatcherPost(Dispatcher.CurrentDispatcher), new NoDebugAdapter()).EvaluateDataTipAsync("x"));
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
        await Until(() => model.Frames.Count == 2);
        Assert.Equal("Paused · pause", model.Status);
        await Until(() => breakpoint.Status == "moved away");
        Assert.Equal(40, breakpoint.BoundLine);
    });

    [Fact]
    public Task TerminationEndsTheSession() => WithAttachedModelAsync("terminated", async model =>
    {
        await Until(() => model.Status == "Session ended");
        Assert.False(model.IsActive);
        Assert.False(model.IsStopped);
        Assert.Equal("Pending", model.Breakpoints.Single().Status);
        Assert.Empty(model.Threads);
    });

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
    private static Task WithAttachedModelAsync(string scenario, Func<DebuggerViewModel, Task> test, Action<BreakpointViewModel>? breakpoint = null)
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
                    await using var model = new DebuggerViewModel(new DebugSession { AdapterPath = AdapterRun.LocateAdapter() }, new DispatcherPost(dispatcher), new NoDebugAdapter());
                    var item = new BreakpointViewModel { Path = Path.GetFullPath(Source), Line = 12 };
                    breakpoint?.Invoke(item);
                    model.Breakpoints.Add(item);
                    await model.AddWatchAsync("a");
                    model.AttachProcessId = Environment.ProcessId;
                    await model.AttachCommand.ExecuteAsync(null);
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

    private sealed class DispatcherPost(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }
}
