using System.Text;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.Runtime.Tests;

/// <summary>What a task needs from the pseudoconsole: its own variables, its exit code, its output seen as it comes, and no stall without a renderer.</summary>
public sealed class TerminalTaskTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task ATaskSeesItsVariablesAndReportsItsExitCode()
    {
        var seen = new StringBuilder();
        var gate = new object();
        var options = new ConPtyOptions
        {
            Environment = new Dictionary<string, string> { ["WPFSTUDIO_TASK_VALUE"] = "from the task" },
            ObserveOutput = bytes => { lock (gate) seen.Append(Encoding.UTF8.GetString(bytes)); },
            KeepOutputFlowing = true
        };
        await using var session = new ConPtySession(Cmd, "/d /s /c \"echo value=%WPFSTUDIO_TASK_VALUE%& exit /b 3\"", Path.GetTempPath(), options);

        Assert.Equal(3, await session.Exited.WaitAsync(TimeSpan.FromSeconds(15)));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Contains(seen, gate, "value=from the task") && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(Contains(seen, gate, "value=from the task"), "The task's output did not include its variable.");
        // The IDE's own environment is still there underneath.
        Assert.Contains("PATH=", ConPtySession.EnvironmentBlock(new Dictionary<string, string>()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutputKeepsFlowingWithNoRendererReadingIt()
    {
        // Thousands of writes would fill the bounded channel many times over and stop the program if it waited for a reader.
        var lines = 0;
        var options = new ConPtyOptions { KeepOutputFlowing = true, ObserveOutput = bytes => Interlocked.Add(ref lines, bytes.Count(b => b == (byte)'\n')) };
        await using var session = new ConPtySession(Cmd, "/d /s /c \"for /l %i in (1,1,3000) do @echo line %i\"", Path.GetTempPath(), options);
        Assert.Equal(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(Volatile.Read(ref lines) > 100, $"Only {lines} lines were observed.");
    }

    [Fact]
    public async Task TerminateEndsTheProgramButKeepsTheSessionReadable()
    {
        await using var session = new ConPtySession(Cmd, "/d /s /c \"ping -n 60 127.0.0.1\"", Path.GetTempPath(), new ConPtyOptions { KeepOutputFlowing = true });
        session.Terminate();
        var code = await session.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, code);
        Assert.False(session.Output.Completion.IsCompleted, "The output closed with the program.");
    }

    [Fact]
    public async Task DisposingBeforeTheProgramEndsStillCompletesItsExit()
    {
        var session = new ConPtySession(Cmd, "/d /s /c \"ping -n 60 127.0.0.1\"", Path.GetTempPath(), new ConPtyOptions { KeepOutputFlowing = true });
        await session.DisposeAsync();
        Assert.True(session.Exited.IsCompleted);
    }

    [Fact]
    public Task ARerunTakesTheFinishedTabsPlaceAndClosesIt() => OnDispatcherAsync(async dispatcher =>
    {
        await using var terminal = new TerminalViewModel(new DispatcherPost(dispatcher)) { WorkingDirectory = Path.GetTempPath() };
        terminal.SelectedShell = terminal.Shells.Single(shell => shell.Name == "Command Prompt");
        await terminal.NewSessionCommand.ExecuteAsync(null);
        var start = new TerminalTaskStart("build", "Task: build", Cmd, "/d /s /c \"exit /b 0\"", Path.GetTempPath());
        var first = await terminal.StartTaskAsync(start);
        Assert.NotNull(first);
        Assert.Equal(("build", true), (first.TaskLabel, first.IsTask));
        Assert.Same(first, terminal.SelectedSession);
        Assert.Equal(0, await first.Session.Exited.WaitAsync(TimeSpan.FromSeconds(15)));

        var second = await terminal.StartTaskAsync(start with { Replacing = first });
        Assert.NotNull(second);
        Assert.Equal(2, terminal.Sessions.Count);
        Assert.Same(second, terminal.Sessions[1]);
        Assert.DoesNotContain(first, terminal.Sessions);
        await terminal.CloseAsync(second);
        Assert.Single(terminal.Sessions);
        Assert.False(terminal.Sessions[0].IsTask);
    });

    private static bool Contains(StringBuilder text, object gate, string value) { lock (gate) return text.ToString().Contains(value, StringComparison.Ordinal); }

    private static Task OnDispatcherAsync(Func<Dispatcher, Task> test)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(dispatcher); result.TrySetResult(); }
                catch (Exception exception) { result.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Terminal task test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private sealed class DispatcherPost(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }
}
