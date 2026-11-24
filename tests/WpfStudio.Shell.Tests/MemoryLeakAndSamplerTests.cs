using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using WpfStudio.App.Controls;
using WpfStudio.App.Controls.Diff;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Documents;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;
using WpfStudio.Core.Documents;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Shell.Tests;

public sealed class MemoryLeakAndSamplerTests
{
    [Fact]
    public Task EditorSurfaceUnsubscribesWindowDeactivatedEvenAfterDetachment() => OnDispatcherAsync(() =>
    {
        var window = new Window { Width = 200, Height = 150, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var surface = new EditorSurface();
        window.Content = surface;
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        var windowField = typeof(EditorSurface).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(window, windowField.GetValue(surface));

        // Detach from visual tree before Unloaded fires, so Window.GetWindow(surface) returns null during Unloaded.
        window.Content = null;
        Assert.Null(Window.GetWindow(surface));
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        Assert.Null(windowField.GetValue(surface));
        return Task.CompletedTask;
    });

    [Fact]
    public Task MemoryProfilerPaneUnloadedDetachesModelAndResetsMonitorActive() => OnDispatcherAsync(async () =>
    {
        EnsureThemeResources();
        await using var model = new MemoryProfilerViewModel(new FakeMemoryProfiler(), new FakeFileDialogs(), new FakeDesktop(), new FakeMemorySampler());
        var pane = new MemoryProfilerPane { DataContext = model };
        var modelField = typeof(MemoryProfilerPane).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!;

        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Same(model, modelField.GetValue(pane));

        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        Assert.Null(modelField.GetValue(pane));
        Assert.False(model.Timeline.IsMonitorActive);
        Assert.False(HasSubscriber(model, "PropertyChanged", pane));
    });

    [Fact]
    public Task DiffViewUnloadedCancelsAndDisposesPendingCompute() => OnDispatcherAsync(() =>
    {
        EnsureThemeResources();
        var view = new DiffView();
        var computeField = typeof(DiffView).GetField("_compute", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var interruptedField = typeof(DiffView).GetField("_computeInterrupted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cts = new CancellationTokenSource();
        computeField.SetValue(view, cts);

        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        Assert.Null(computeField.GetValue(view));
        Assert.True((bool)interruptedField.GetValue(view)!);
        Assert.Throws<ObjectDisposedException>(() => _ = cts.Token);

        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.False((bool)interruptedField.GetValue(view)!);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NativePreviewSurfaceReleaseForRemovalUnsubscribesSessionChanged() => OnDispatcherAsync(() =>
    {
        var session = new TrackingPreviewSession();
        using var surface = new NativePreviewSurface();
        surface.SetSession(session);
        Assert.Equal(1, session.SubscriberCount);

        Assert.True(surface.ReleaseForRemoval());
        Assert.Equal(0, session.SubscriberCount);

        // Re-loading the surface must re-subscribe cleanly without duplicating handlers.
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(1, session.SubscriberCount);
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(1, session.SubscriberCount);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task ShellViewModelUnsubscribesDocumentAndSettingsEventsAndDisposesProjectRefresh()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("LeakCheck.cs", "class LeakCheck { }");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        var state = editor.State;

        var contentChangedField = typeof(DocumentState).GetField("ContentChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var beforeClose = (Delegate?)contentChangedField.GetValue(state);
        Assert.NotNull(beforeClose);

        Assert.True(await test.Shell.CloseDocumentAsync(editor));
        var afterClose = (Delegate?)contentChangedField.GetValue(state);
        Assert.Null(afterClose);

        var projectRefreshField = typeof(ShellViewModel).GetField("_projectRefreshDelay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var settingsSaveField = typeof(ShellViewModel).GetField("_settingsSave", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var projectRefreshWork = (WpfStudio.Core.Threading.SerialDebouncedWork)projectRefreshField.GetValue(test.Shell)!;
        var settingsSaveWork = (WpfStudio.Core.Threading.SerialDebouncedWork)settingsSaveField.GetValue(test.Shell)!;
        Assert.False(projectRefreshWork.IsDisposed);
        Assert.False(settingsSaveWork.IsDisposed);

        await test.Shell.DisposeAsync();

        Assert.True(projectRefreshWork.IsDisposed);
        Assert.True(settingsSaveWork.IsDisposed);

        // Mutating preferences after DisposeAsync must not throw or schedule pending work.
        test.Shell.CodeAssist.Enabled = !test.Shell.CodeAssist.Enabled;
        test.Shell.Debugger.JustMyCode = !test.Shell.Debugger.JustMyCode;
        test.Shell.Network.Load(new());
        test.Shell.Editor.FontSize = 15;

        var pendingField = typeof(WpfStudio.Core.Threading.SerialDebouncedWork).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(pendingField.GetValue(settingsSaveWork));
    }

    [Fact]
    public async Task ExternalChangesViewModelDisposeUnsubscribesHostAndActiveItems()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("External.cs", "class External { }");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;

        var host = new TrackingExternalChangesHost([editor]);
        var watcher = new FakeOpenDocumentWatcher();
        var vm = new ExternalChangesViewModel(host, new DocumentStore(test.Root), new InlineDispatcher(), _ => watcher);
        Assert.Equal(1, host.DocumentsChangedSubscriberCount);

        var change = new ExternalChange(editor);
        vm.Changes.Add(change);
        Assert.Single(vm.Changes);
        Assert.True(HasSubscriber(editor.State, "PropertyChanged", change));

        vm.Dispose();

        Assert.Equal(0, host.DocumentsChangedSubscriberCount);
        Assert.Empty(vm.Changes);
        Assert.True(watcher.Disposed);
        Assert.False(HasSubscriber(editor.State, "PropertyChanged", change));
    }

    [Fact]
    public async Task ProcessMemoryTimelineTakesInitialSampleOffUiThreadAndHandlesConcurrentWatchStop()
    {
        var sampledOnThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = new ThreadRecordingSampler(sampledOnThread);
        using var host = new FakeMemorySessionHost();
        var timeline = new ProcessMemoryTimeline(sampler, host, () => { });

        var callingThread = Environment.CurrentManagedThreadId;
        timeline.IsMonitorActive = true;
        timeline.Watch(new ProfileProcess(100, "Target", 1024, 42, "10.0"));

        var workerThread = await sampledOnThread.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(callingThread, workerThread);

        // Rapid Watch/Stop cycles must not throw or leave stale CTS references.
        for (int i = 0; i < 20; i++)
        {
            timeline.Watch(new ProfileProcess(100 + i, "Target", 1024, 42 + i, "10.0"));
            timeline.Stop();
        }
    }

    [Fact]
    public async Task ProcessMemorySamplerConcurrentSampleAndDisposeIsThreadSafe()
    {
        using var current = Process.GetCurrentProcess();
        int pid = current.Id;
        long startTicks = current.StartTime.ToUniversalTime().Ticks;

        var sampler = new ProcessMemorySampler();
        using var gate = new ManualResetEventSlim(false);

        var workers = Enumerable.Range(0, 6).Select(index => Task.Run(() =>
        {
            gate.Wait();
            for (int i = 0; i < 30; i++)
            {
                if (index == 0 && i == 15) sampler.Dispose();
                _ = sampler.Sample(pid, startTicks);
            }
        })).ToArray();

        gate.Set();
        await Task.WhenAll(workers);

        Assert.Null(sampler.Sample(pid, startTicks));
    }

    [Fact]
    public async Task AppDisposeStartupResourcesDisposesMonitorsAndBothSyncAndAsyncHosts()
    {
        var ui1 = new TrackingDisposable(throwOnDispose: true);
        var pool1 = new TrackingDisposable();
        var syncHost = new SyncOnlyHost();

        await global::WpfStudio.App.App.DisposeStartupResourcesAsync(ui1, pool1, syncHost);

        Assert.True(ui1.Disposed);
        Assert.True(pool1.Disposed);
        Assert.True(syncHost.Disposed);

        var ui2 = new TrackingDisposable();
        var pool2 = new TrackingDisposable();
        var asyncHost = new AsyncOnlyHost();

        await global::WpfStudio.App.App.DisposeStartupResourcesAsync(ui2, pool2, asyncHost);

        Assert.True(ui2.Disposed);
        Assert.True(pool2.Disposed);
        Assert.True(asyncHost.AsyncDisposed);
        Assert.False(asyncHost.SyncDisposed);
    }

    private static readonly Lazy<Dispatcher> SharedDispatcher = new(() =>
    {
        var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "MemoryLeakAndSamplerTests STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    private static bool _themeLoaded;

    private static void EnsureThemeResources()
    {
        if (_themeLoaded) return;
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var themeUri = new Uri("pack://application:,,,/WpfStudio;component/Resources/StudioTheme.xaml", UriKind.Absolute);
        if (!app.Resources.MergedDictionaries.Any(d => d.Source == themeUri))
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
        _themeLoaded = true;
    }

    private static bool HasSubscriber(object source, string eventName, object target)
    {
        for (Type? type = source.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic) is { } field
                && field.GetValue(source) is Delegate multicast)
            {
                return multicast.GetInvocationList().Any(d => ReferenceEquals(d.Target, target));
            }
        }
        return false;
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = SharedDispatcher.Value.BeginInvoke(async () =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private sealed class FakeMemoryProfiler : IMemoryProfiler
    {
        public Task<IReadOnlyList<ProfileProcess>> GetProcessesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProfileProcess>>([]);
        public Task<IMemorySession> OpenAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeFileDialogs : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? defaultFileName = null) => Task.FromResult<string?>(null);
    }

    private sealed class FakeMemorySampler : IProcessMemorySampler
    {
        public ProcessMemorySample? Sample(int processId, long startTimeUtcTicks) => new(DateTimeOffset.Now, 1024, 2048);
    }

    private sealed class ThreadRecordingSampler(TaskCompletionSource<int> firstThread) : IProcessMemorySampler
    {
        public ProcessMemorySample? Sample(int processId, long startTimeUtcTicks)
        {
            firstThread.TrySetResult(Environment.CurrentManagedThreadId);
            return new(DateTimeOffset.Now, 4096, 8192);
        }
    }

    private sealed class FakeMemorySessionHost : IMemorySessionHost, IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        public IMemorySession? Session => null;
        public long Revision => 1;
        public HeapSummary? Summary => null;
        public bool IsDisposed { get; private set; }
        public CancellationToken Lifetime => _lifetime.Token;
        public void ReportStatus(string status) { }
        public void ReportFault(Exception fault) { }
        public void ShowObject(int objectId) { }
        public void Dispose() { IsDisposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
    }

    private sealed class TrackingPreviewSession : IPreviewInteractionSession
    {
        private event EventHandler<PreviewInteractionEvent>? _changed;
        public int SubscriberCount { get; private set; }
        public PreviewSurfaceIdentity Surface { get; } = new("session-1", 1, "test");
        public bool IsAvailable => true;
        public bool IsAttached => false;
        public event EventHandler<PreviewInteractionEvent>? Changed
        {
            add { _changed += value; SubscriberCount++; }
            remove
            {
                if (_changed is null) return;
                var before = _changed.GetInvocationList().Length;
                _changed -= value;
                var after = _changed?.GetInvocationList().Length ?? 0;
                SubscriberCount -= before - after;
            }
        }
        public Task<PreviewSurfaceResponse> UpdateAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new PreviewSurfaceResponse(request, false, "stub"));
        public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Abort(string reason) { }
    }

    private sealed class TrackingExternalChangesHost(IReadOnlyList<EditorViewModel> documents) : IExternalChangesHost
    {
        private event Action? _documentsChanged;
        public int DocumentsChangedSubscriberCount { get; private set; }
        public IReadOnlyList<EditorViewModel> Documents { get; } = documents;
        public event Action? DocumentsChanged
        {
            add { _documentsChanged += value; DocumentsChangedSubscriberCount++; }
            remove
            {
                if (_documentsChanged is null) return;
                var before = _documentsChanged.GetInvocationList().Length;
                _documentsChanged -= value;
                var after = _documentsChanged?.GetInvocationList().Length ?? 0;
                DocumentsChangedSubscriberCount -= before - after;
            }
        }
        public CancellationToken Lifetime => CancellationToken.None;
        public Task GuardAsync(Func<Task> action) => action();
        public void ReportStatus(string status) { }
        public void AppendOutput(string line) { }
        public Task<bool> CloseDocumentAsync(EditorViewModel document, bool discardChanges) => Task.FromResult(true);
        public void DiskVersionsChecked() { }
        public void SettingChanged() { }
    }

    private sealed class FakeOpenDocumentWatcher : IOpenDocumentWatcher
    {
        public bool Disposed { get; private set; }
        public void Watch(IEnumerable<string> paths) { }
        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingDisposable(bool throwOnDispose = false) : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            if (throwOnDispose) throw new InvalidOperationException("dispose failed");
        }
    }

    private sealed class SyncOnlyHost : IHost
    {
        public bool Disposed { get; private set; }
        public IServiceProvider Services => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private sealed class AsyncOnlyHost : IHost, IAsyncDisposable
    {
        public bool SyncDisposed { get; private set; }
        public bool AsyncDisposed { get; private set; }
        public IServiceProvider Services => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => SyncDisposed = true;
        public ValueTask DisposeAsync() { AsyncDisposed = true; return ValueTask.CompletedTask; }
    }
}
