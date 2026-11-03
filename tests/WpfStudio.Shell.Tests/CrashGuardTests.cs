using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core;

namespace WpfStudio.Shell.Tests;

/// <summary>Faults in background callbacks and unreadable profile files must be reported, never allowed to end the process.</summary>
public sealed class CrashGuardTests
{
    [Fact]
    public void SourceWatcherReportsACallbackFailureInsteadOfThrowing()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-WatcherGuard-").FullName;
        try
        {
            var source = Path.Combine(directory, "Main.xaml"); File.WriteAllText(source, "<Grid />");
            Exception? reported = null;
            using var watcher = new KnownSourceFileWatcher([source], _ => throw new InvalidOperationException("consumer failed"), TimeSpan.Zero,
                failed: exception => reported = exception);
            watcher.Reconcile();
            Assert.IsType<InvalidOperationException>(reported);
            Assert.Equal("consumer failed", reported.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SourceWatcherSurvivesAFaultingFailureHandler()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-WatcherGuard-").FullName;
        try
        {
            var source = Path.Combine(directory, "Main.xaml"); File.WriteAllText(source, "<Grid />");
            using var watcher = new KnownSourceFileWatcher([source], _ => throw new InvalidOperationException(), TimeSpan.Zero,
                failed: _ => throw new NotSupportedException());
            watcher.Reconcile();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LogProviderSwallowsALockedLogFileAndResumesLater()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-LogGuard-").FullName;
        try
        {
            var logs = Path.Combine(directory, "Logs"); Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "studio.log");
            using var provider = new StudioLogProvider(directory);
            var logger = provider.CreateLogger("Tests");
            using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                logger.LogWarning("while locked");
                provider.Dispose(); // Waits for the writer, which must fail against the lock and carry on.
            }
            logger.LogWarning("after unlock");
            Assert.DoesNotContain("while locked", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LogLinesReachTheFileInOrderOnceTheProviderIsDisposed()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-LogFlush-").FullName;
        try
        {
            var provider = new StudioLogProvider(directory);
            var logger = provider.CreateLogger("Tests");
            for (var line = 0; line < 500; line++) logger.LogWarning("line {Line}", line);
            logger.LogInformation("below the threshold");
            provider.Dispose();
            var lines = File.ReadAllLines(Path.Combine(directory, "Logs", "studio.log"));
            Assert.Equal(500, lines.Length);
            Assert.Contains("line 0 ", lines[0]);
            Assert.Contains("line 499 ", lines[^1]);
            provider.CreateLogger("Tests").LogWarning("after dispose"); // Too late to write, but must not throw.
            provider.Dispose();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LoggingDoesNotWaitForALockedLogFile()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-LogQueue-").FullName;
        try
        {
            var logs = Path.Combine(directory, "Logs"); Directory.CreateDirectory(logs);
            using var provider = new StudioLogProvider(directory);
            var logger = provider.CreateLogger("Tests");
            using var locked = new FileStream(Path.Combine(logs, "studio.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var line = 0; line < 10_000; line++) logger.LogError("line {Line}", line); // More than the queue holds: the oldest lines give way.
            Assert.InRange(clock.Elapsed.TotalSeconds, 0, 5);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PromptWorksWithoutAnOwnerOrTheThemeBrushes()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void Answer()
            {
                // CurrentSources spans every thread, so a window another test owns can be listed here.
                var prompt = System.Windows.PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().Where(source => source.CheckAccess())
                    .Select(source => source.RootVisual).OfType<System.Windows.Window>().FirstOrDefault(window => window.Title == "No theme");
                if (prompt is null) { dispatcher.BeginInvoke(Answer, System.Windows.Threading.DispatcherPriority.Background); return; }
                prompt.DialogResult = true;
            }
            dispatcher.BeginInvoke(Answer, System.Windows.Threading.DispatcherPriority.Background);
            try { completion.TrySetResult(new DesktopDialogs(() => null).PromptAsync("No theme", "Name", "kept").Result); }
            catch (Exception exception) { completion.TrySetException(exception); }
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.Equal("kept", await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task SettingsStoreReturnsDefaultsWhenTheFileCannotBeRead()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-SettingsGuard-").FullName;
        try
        {
            var store = new SettingsStore(directory);
            await store.SaveAsync(new StudioSettings { Theme = "Light" });
            var path = Path.Combine(directory, "settings.json");
            using (DenyRead(path))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllTextAsync(path));
                var loaded = await store.LoadAsync();
                Assert.Equal("Dark", loaded.Theme);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WorkspaceStateStoreReturnsAnEmptyListWhenTheFileCannotBeRead()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-StateGuard-").FullName;
        try
        {
            var store = new WorkspaceStateStore<string>("tasks", directory);
            await store.SaveAsync(@"C:\work\App.sln", ["one"]);
            var path = Directory.EnumerateFiles(Path.Combine(directory, "tasks"), "*.json").Single();
            using (DenyRead(path)) Assert.Empty(await store.LoadAsync(@"C:\work\App.sln"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ForgottenTaskFaultNeverReachesTheFinalizer()
    {
        var marker = "forgotten-" + Guid.NewGuid().ToString("N");
        var unobserved = 0;
        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args) { if (args.Exception.ToString().Contains(marker)) Interlocked.Increment(ref unobserved); }
        TaskScheduler.UnobservedTaskException += Unobserved;
        try
        {
            await StartAndForgetAsync(marker);
            for (var pass = 0; pass < 3; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally { TaskScheduler.UnobservedTaskException -= Unobserved; }
    }
    // Not inlined, so the JIT keeps no reference to the faulted task in the caller's frame.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task StartAndForgetAsync(string marker)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run(() => { try { throw new InvalidOperationException(marker); } finally { finished.SetResult(); } }).Forget();
        await finished.Task;
        await Task.Delay(50);
    }

    [Fact]
    public async Task ForgetReportsTheFaultButNotCancellation()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.FromException(new InvalidOperationException("failed")).Forget(reported.SetResult);
        Assert.Equal("failed", (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);

        Exception? cancelled = null;
        Task.FromCanceled(new CancellationToken(true)).Forget(exception => cancelled = exception);
        Assert.Null(cancelled);

        Task.FromException(new InvalidOperationException("failed")).Forget(_ => throw new NotSupportedException()); // A failing reporter stays contained.
    }

    [Fact]
    public async Task FaultReporterShowsOnTheUiThreadWhenCalledFromAnotherThread()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-FaultGuard-").FullName;
        var ui = await StartDispatcherAsync();
        try
        {
            var shown = new TaskCompletionSource<(bool OnUiThread, string Status, string Details)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reporter = new UnhandledFaultReporter(ui, directory, () => null, (status, details) => shown.SetResult((ui.CheckAccess(), status, details)));

            // The finalizer thread is simply a thread that does not own the dispatcher.
            Assert.True(await Task.Run(() => reporter.Report(new InvalidOperationException("lens request failed"), "background task")));

            var (onUiThread, status, details) = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(onUiThread);
            Assert.Equal("Unexpected error: lens request failed", status);
            Assert.StartsWith("[background task]", details);
            Assert.Contains("lens request failed", File.ReadAllText(Path.Combine(directory, "unhandled-error.log")));
        }
        finally { ui.InvokeShutdown(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FaultReporterContainsAFailureToShowTheFault()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-FaultGuard-").FullName;
        var ui = await StartDispatcherAsync();
        try
        {
            var dispatcherFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.UnhandledException += (_, args) => { args.Handled = true; dispatcherFault.TrySetResult(); };
            var reporter = new UnhandledFaultReporter(ui, directory, () => null, (_, _) => throw new InvalidOperationException("window is gone"));
            Assert.True(reporter.Report(new InvalidOperationException("first"), "process"));
            await ui.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(dispatcherFault.Task.IsCompleted);
        }
        finally { ui.InvokeShutdown(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void FaultReporterLetsRuntimeFatalFaultsTerminate()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-FaultGuard-").FullName;
        try
        {
            var reporter = new UnhandledFaultReporter(System.Windows.Threading.Dispatcher.CurrentDispatcher, directory, () => null, (_, _) => { });
#pragma warning disable CA2201 // The guard under test classifies exactly this type; nothing throws it.
            Assert.False(reporter.Report(new OutOfMemoryException(), "process"));
#pragma warning restore CA2201
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<System.Windows.Threading.Dispatcher> StartDispatcherAsync()
    {
        var started = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            started.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "Fault reporter UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await started.Task;
    }

    /// <summary>Denies the current user read access to <paramref name="path"/> until disposed, so the file can be deleted afterwards.</summary>
    private static IDisposable DenyRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Read, AccessControlType.Deny);
        security.AddAccessRule(rule); file.SetAccessControl(security);
        return new Restore(file, rule);
    }
    private sealed class Restore(FileInfo file, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose() { var security = file.GetAccessControl(); security.RemoveAccessRule(rule); file.SetAccessControl(security); }
    }
}
