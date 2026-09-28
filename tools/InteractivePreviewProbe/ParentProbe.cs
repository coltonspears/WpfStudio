using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace InteractivePreviewProbe;

internal sealed class ParentProbe : IDisposable
{
    private readonly Application _app;
    private readonly Options _options;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly string _pipeName = "WpfStudio-interactive-probe-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Packet>> _requests = new();
    private readonly TaskCompletionSource<Packet> _attached = NewCompletion();
    private readonly TaskCompletionSource<Packet> _hangEntered = NewCompletion();
    private readonly TaskCompletionSource<Packet> _killed = NewCompletion();
    private readonly TaskCompletionSource<Packet> _focusReturned = NewCompletion();
    private readonly object _logGate = new();
    private readonly object _lifecycleGate = new();
    private readonly StreamWriter _log;
    private readonly TextBox _status;
    private readonly BridgeHost _bridge;
    private readonly DispatcherTimer _uiTimer;
    private NamedPipeServerStream? _pipe;
    private Wire? _wire;
    private Process? _child;
    private nint _bridgeHandle, _childHandle;
    private long _nextId, _lastPong, _lastUiTick, _maximumUiGap;
    private int _width = 640, _height = 360, _killStarted, _ready, _disposed;

    public Window Window { get; }

    public ParentProbe(Application app, Options options)
    {
        _app = app;
        _options = options;
        Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath)!);
        _log = new StreamWriter(options.LogPath, append: false) { AutoFlush = true };
        Window = new Window { Title = "Interactive preview native embedding probe", Width = 850, Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (options.Automated)
        {
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            Window.Left = -32000;
            Window.Top = -32000;
            Window.ShowActivated = false;
            Window.ShowInTaskbar = false;
        }
        var panel = new DockPanel { Margin = new Thickness(12) };
        var controls = new WrapPanel();
        AddButton(controls, "Focus first child control", () => Send(new("focus", Text: "First")));
        AddButton(controls, "Open popup", () => Send(new("popup", Number: 1)));
        AddButton(controls, "Hide child / close popup", () => Send(new("hide")));
        AddButton(controls, "Show child", () => Send(new("show")));
        AddButton(controls, "Hang child", () => Send(new("hang")));
        AddButton(controls, "Stop owned child", () => KillOwnedChild("manual stop"));
        DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Add(controls);
        var before = new TextBox { Text = "Parent tab stop before preview", Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(before, Dock.Top);
        panel.Children.Add(before);
        var after = new TextBox { Text = "Parent tab stop after preview", Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(after, Dock.Bottom);
        panel.Children.Add(after);
        _status = new TextBox { IsReadOnly = true, Height = 190, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(_status, Dock.Bottom);
        panel.Children.Add(_status);
        _bridge = new BridgeHost(_token) { Focusable = true, MinHeight = 330 };
        _bridge.Created += hwnd => { _bridgeHandle = hwnd; _ = Task.Run(StartAsync); };
        _bridge.Destroying += () => KillOwnedChild("bridge destroying");
        _bridge.Resized += (width, height) =>
        {
            Volatile.Write(ref _width, width);
            Volatile.Write(ref _height, height);
            if (Volatile.Read(ref _ready) != 0) Send(new("size", Width: width, Height: height));
        };
        _bridge.EnterRequested += direction => Send(new("focus", Text:
            direction is FocusNavigationDirection.Last or FocusNavigationDirection.Previous ? "Last" : "First"));
        panel.Children.Add(_bridge);
        Window.Content = panel;
        Window.Deactivated += (_, _) => { if (!options.Automated) Send(new("popup", Number: 0)); };
        Window.Closing += (_, _) => KillOwnedChild("parent closing");
        Window.Closed += (_, _) => _app.Shutdown();
        _lastUiTick = Stopwatch.GetTimestamp();
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            long gap = (long)Stopwatch.GetElapsedTime(Interlocked.Exchange(ref _lastUiTick, now), now).TotalMilliseconds;
            if (gap > Interlocked.Read(ref _maximumUiGap)) Interlocked.Exchange(ref _maximumUiGap, gap);
            if (gap > 600) Log("parent-dispatcher-gap", new { milliseconds = gap });
        }, app.Dispatcher);
        Log("starting", new { parentPid = Environment.ProcessId, options.Surface, options.Automated,
            limitation = "Posted messages bypass real input state. This probe does not certify physical keyboard, IME, pointer, or mixed-DPI behavior." });
        if (options.Automated) _ = Task.Run(ExitDeadlineAsync);
    }

    private static TaskCompletionSource<Packet> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void AddButton(Panel panel, string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(2), Padding = new Thickness(5) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private async Task StartAsync()
    {
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "InteractivePreviewProbe.exe");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--child", "--pipe", _pipeName, "--token", _token, "--parent", Environment.ProcessId.ToString(), "--surface", _options.Surface })
                start.ArgumentList.Add(arg);
            if (_options.Automated) start.ArgumentList.Add("--auto-message-smoke");
            lock (_lifecycleGate)
            {
                // The bridge's Created notification is asynchronous. Closing before it runs
                // must revoke both a pending spawn and publication of a newly launched process.
                if (_closed.IsCancellationRequested || _disposed != 0 || _killStarted != 0) return;
                _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _child = Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
                _ = _child.SafeHandle; // Retain the exact OS process identity for termination.
            }
            Log("child-started", new { pid = _child.Id, exe });
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await _pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            Native.Require(Native.GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint clientPid), "Get pipe client PID");
            if (clientPid != _child.Id) throw new InvalidOperationException("Connected pipe is not the owned child process.");
            _wire = new Wire(_pipe);
            _ = ObserveWriterAsync(_wire.WriteLoopAsync());
            Packet hello = await _wire.ReadAsync(connect.Token).ConfigureAwait(false);
            if (hello.Kind != "hello" || hello.Token != _token || hello.Number != _child.Id)
                throw new InvalidOperationException("Child hello failed identity checks.");
            Native.RequireWindow(_bridgeHandle, Environment.ProcessId);
            Send(new("attach", Handle: _bridgeHandle.ToInt64(), Width: Volatile.Read(ref _width), Height: Volatile.Read(ref _height)));
            _lastPong = Stopwatch.GetTimestamp();
            _ = Task.Run(WatchdogAsync);
            if (_options.Automated) _ = Task.Run(RunAutomatedAsync);
            while (!_closed.IsCancellationRequested)
            {
                Packet packet = await _wire.ReadAsync(_closed.Token).ConfigureAwait(false);
                if (packet.Token != _token) throw new InvalidDataException("Unexpected session token.");
                if (packet.Kind != "pong") Log("child-" + packet.Kind, packet with { Token = null });
                switch (packet.Kind)
                {
                    case "pong": Interlocked.Exchange(ref _lastPong, Stopwatch.GetTimestamp()); break;
                    case "attached":
                        _childHandle = new nint(packet.Handle);
                        Native.RequireWindow(_childHandle, _child.Id);
                        if (Native.GetParent(_childHandle) != _bridgeHandle) throw new InvalidOperationException("Child is not parented to our bridge.");
                        if (!Native.AreDpiAwarenessContextsEqual(Native.GetWindowDpiAwarenessContext(_childHandle), Native.PerMonitorV2))
                            throw new InvalidOperationException("Child HWND is not PerMonitorV2.");
                        Volatile.Write(ref _ready, 1);
                        _attached.TrySetResult(packet);
                        break;
                    case "hang-entered": _hangEntered.TrySetResult(packet); break;
                    case "tab-out":
                        _ = _app.Dispatcher.BeginInvoke(() => _bridge.Leave(packet.Text is "Previous" or "Last"
                            ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                        break;
                    case "error": throw new InvalidOperationException(packet.Text);
                }
                if (packet.Id != 0 && packet.Kind == "state" && _requests.TryRemove(packet.Id, out var pending))
                    pending.TrySetResult(packet);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed.IsCancellationRequested && Volatile.Read(ref _killStarted) == 0)
            {
                Log("connection-failed", new { type = ex.GetType().Name, ex.Message });
                _attached.TrySetException(ex);
                KillOwnedChild("connection failed");
                if (_options.Automated) Finish(2, "Connection failed.");
            }
        }
    }

    private async Task ObserveWriterAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_closed.IsCancellationRequested) Log("writer-ended", new { ex.Message }); }
    }

    private async Task WatchdogAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested && Volatile.Read(ref _killStarted) == 0)
            {
                await Task.Delay(250, _closed.Token).ConfigureAwait(false);
                Send(new("ping"));
                double elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPong)).TotalMilliseconds;
                if (elapsed > 3000)
                {
                    Log("watchdog-expired", new { elapsedMilliseconds = elapsed, threadId = Environment.CurrentManagedThreadId });
                    KillOwnedChild("dispatcher heartbeat expired");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAutomatedAsync()
    {
        try
        {
            await _attached.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            ProbeState first = (await GetStateAsync().ConfigureAwait(false)).State!;
            Native.RequireWindow(_childHandle, _child!.Id);
            Send(new("focus", Text: "First"));
            await Task.Delay(200).ConfigureAwait(false);
            Send(new("post-text", Text: "posted-text"));
            await Task.Delay(200).ConfigureAwait(false);
            ProbeState text = (await GetStateAsync().ConfigureAwait(false)).State!;
            Send(new("post-tab"));
            await Task.Delay(200).ConfigureAwait(false);
            ProbeState tab = (await GetStateAsync().ConfigureAwait(false)).State!;
            Send(new("post-click"));
            await Task.Delay(200).ConfigureAwait(false);
            ProbeState click = (await GetStateAsync().ConfigureAwait(false)).State!;
            Send(new("popup", Number: 1));
            await Task.Delay(300).ConfigureAwait(false);
            ProbeState popup = (await GetStateAsync().ConfigureAwait(false)).State!;
            bool popupOwned = popup.PopupHandle != 0 && Native.IsWindow(new nint(popup.PopupHandle)) &&
                Native.GetWindowThreadProcessId(new nint(popup.PopupHandle), out uint popupPid) != 0 && popupPid == _child.Id;
            Send(new("hide"));
            await Task.Delay(150).ConfigureAwait(false);
            ProbeState hidden = (await GetStateAsync().ConfigureAwait(false)).State!;
            Send(new("show"));
            Log("posted-message-observations", new { textChanged = text.Text == "posted-text", tabFocusChanged = tab.Focus != text.Focus,
                clickIncremented = click.Clicks > first.Clicks, popupOwned, popupClosedWhenHidden = !hidden.PopupOpen,
                nativeMessagesObserved = click.NativeMessages - first.NativeMessages,
                limitation = "These observations are not equivalent to physical input. False posted-input observations are recorded, not hidden or treated as OS-input failures." });

            // Establish focus using the child's real keyboard sink, then block its dispatcher.
            // Focus on our local bridge may itself block because parenting attaches input queues.
            Send(new("focus", Text: "First"));
            await Task.Delay(200).ConfigureAwait(false);
            Send(new("hang"));
            await _hangEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            long focusStart = Stopwatch.GetTimestamp();
            _ = _app.Dispatcher.BeginInvoke(() =>
            {
                Log("parent-focus-start", new { hwnd = _bridgeHandle.ToInt64() });
                Native.RequireWindow(_bridgeHandle, Environment.ProcessId);
                if (Native.GetProp(_bridgeHandle, Native.LeaseProperty(_token)) != new nint(1))
                    throw new InvalidOperationException("The local bridge lease has ended.");
                Native.SetFocus(_bridgeHandle); // Only our own bridge; deliberately exposes coupling.
                var result = new Packet("focus-returned", Text: Stopwatch.GetElapsedTime(focusStart).TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Log("parent-focus-returned", new { elapsedMilliseconds = result.Text });
                _focusReturned.TrySetResult(result);
            });
            await _killed.Task.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            await _child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _focusReturned.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _app.Dispatcher.InvokeAsync(() => Log("parent-dispatcher-recovered", new { maximumGapMilliseconds = Interlocked.Read(ref _maximumUiGap) }),
                DispatcherPriority.ContextIdle).Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            bool childGone = _child.HasExited;
            bool hwndGone = !Native.IsWindow(_childHandle);
            bool popupGone = popup.PopupHandle == 0 || !Native.IsWindow(new nint(popup.PopupHandle));
            Log("automated-result", new { childGone, hwndGone, popupGone, popupOwned,
                scope = "Parent/child ownership, DPI equality, posted-message observations, popup lifecycle and bounded watchdog recovery only.",
                physicalInput = "unverified", ime = "unverified", mixedDpi = "unverified" });
            Finish(childGone && hwndGone && popupGone && popupOwned ? 0 : 2, "Automated probe complete.");
        }
        catch (Exception ex)
        {
            Log("automated-failed", new { type = ex.GetType().Name, ex.Message });
            KillOwnedChild("automated probe failed");
            Finish(2, "Automated probe failed.");
        }
    }

    private async Task<Packet> GetStateAsync()
    {
        long id = Interlocked.Increment(ref _nextId);
        var completion = NewCompletion();
        _requests[id] = completion;
        Send(new("state", id));
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        finally { _requests.TryRemove(id, out _); }
    }

    private void Send(Packet packet) => _wire?.Send(packet with { Token = _token });
    private void KillOwnedChild(string reason)
    {
        lock (_lifecycleGate)
        {
            if (_killStarted != 0) return;
            Volatile.Write(ref _killStarted, 1); // Also revokes a not-yet-started bridge callback.
            Process? child = _child;
            if (child is null) return;
            try
            {
                // Process keeps the original OS process handle. Never reacquire by PID and never
                // recurse into descendants, which may be unrelated tools launched by target code.
                if (!child.HasExited) child.Kill(entireProcessTree: false);
                Log("owned-child-killed", new { pid = child.Id, reason, threadId = Environment.CurrentManagedThreadId });
                _killed.TrySetResult(new("killed", Text: reason));
            }
            catch (InvalidOperationException) { _killed.TrySetResult(new("already-exited")); }
            catch (Exception ex) { Log("kill-failed", new { ex.Message }); _killed.TrySetException(ex); }
        }
    }

    private async Task ExitDeadlineAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0) return;
        Log("parent-deadline-expired", new { limitation = "Background exit does not require the parent dispatcher." });
        KillOwnedChild("parent hard deadline");
        Environment.Exit(3);
    }

    private void Finish(int code, string reason)
    {
        Log("finish", new { code, reason });
        _app.Dispatcher.BeginInvoke(() => _app.Shutdown(code));
    }

    private void Log(string kind, object? data = null)
    {
        lock (_logGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _log.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind, data }));
        }
        if (!_options.Automated)
            _app.Dispatcher.BeginInvoke(() => { _status.AppendText(kind + Environment.NewLine); _status.ScrollToEnd(); });
    }

    public void Dispose()
    {
        KillOwnedChild("parent disposed");
        _closed.Cancel();
        _uiTimer.Stop();
        _wire?.Dispose();
        _pipe?.Dispose();
        _child?.Dispose();
        lock (_logGate) { Interlocked.Exchange(ref _disposed, 1); _log.Dispose(); }
    }
}
