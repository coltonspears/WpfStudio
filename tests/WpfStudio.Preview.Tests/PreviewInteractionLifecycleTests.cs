using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class PreviewInteractionLifecycleTests
{
    private static PreviewRequest Request(long version = 1) => new("C:/preview/NativeLifetime.xaml",
        "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBox Name='Subject' Text='Retained'/></Grid>",
        version, 400, 300);

    [Fact]
    public async Task HandshakeAndSurfaceAuthoritySurviveDetachButNotReplacement()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        using var bridge = await OwnedNativeBridge.CreateAsync();
        var rendered = await client.RenderAsync(Request());
        Assert.True(rendered.Success, rendered.Status);
        var surface = Assert.IsType<PreviewSurfaceIdentity>(rendered.Surface);
        var handshake = await client.GetSessionAsync();
        Assert.Equal(client.ProcessId, handshake.ProcessId);
        Assert.Equal(Environment.ProcessId, handshake.ParentProcessId);
        Assert.Equal(surface.SessionId, handshake.SessionId);
        Assert.Equal(PreviewNativeNavigation.ProtocolVersion, handshake.ProtocolVersion);
        Assert.True(handshake.NativeInteractionAvailable);
        Assert.Null(client.CreateInteractionSession(surface with { SessionId = Guid.NewGuid().ToString("N") }));
        Assert.Null(client.CreateInteractionSession(surface with { Version = surface.Version + 1 }));
        var session = Assert.IsAssignableFrom<IPreviewInteractionSession>(client.CreateInteractionSession(surface));
        try
        {
            var first = await session.UpdateAsync(bridge.Request(surface));
            Assert.True(first.Success, first.Status);
            Assert.True(first.Request.Sequence > 0);
            Assert.True(session.IsAttached);
            Assert.Equal(surface, (await client.CaptureAsync(new(surface.Version))).Surface);
            await Task.WhenAll(session.DeactivateAsync(), session.DeactivateAsync());
            Assert.False(session.IsAttached);
            var detachedSession = session;
            detachedSession.Abort("The detached native view was disposed.");
            session = Assert.IsAssignableFrom<IPreviewInteractionSession>(client.CreateInteractionSession(surface));
            var again = await session.UpdateAsync(bridge.Request(surface));
            Assert.True(again.Success, again.Status);
            Assert.True(again.Request.Sequence > first.Request.Sequence);
            detachedSession.Abort("Delayed cleanup of the already detached native view.");
            Assert.True(session.IsAttached);
            int originalPid = client.ProcessId!.Value;

            // Render must detach under its existing request gate, then revoke the old
            // authority without terminating a healthy same-process source preview.
            var next = await client.RenderAsync(Request(2));
            Assert.True(next.Success, next.Status);
            Assert.Equal(originalPid, client.ProcessId);
            Assert.NotEqual(surface, next.Surface);
            Assert.False(session.IsAvailable);
            Assert.False(session.IsAttached);
            session.Abort("A stale native view is being destroyed.");
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.UpdateAsync(bridge.Request(surface)));
            Assert.Equal(originalPid, client.ProcessId);
            Assert.NotNull((await client.InspectAsync(new(next.Version, next.Nodes[0].Id))).Node);
        }
        finally { session.Abort("Native lifetime test cleanup."); }
    }

    [Fact]
    public async Task SupersededKeyboardEntryDoesNotAbortTheAttachedPreview()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        using var bridge = await OwnedNativeBridge.CreateAsync();
        var snapshot = await client.RenderAsync(Request());
        Assert.True(snapshot.Success, snapshot.Status);
        var session = Assert.IsAssignableFrom<IPreviewInteractionSession>(client.CreateInteractionSession(snapshot.Surface!));
        try
        {
            Assert.True((await session.UpdateAsync(bridge.Request(snapshot.Surface!))).Success);
            int originalProcessId = client.ProcessId!.Value;
            var before = await client.HeartbeatSurfaceAsync(new(snapshot.Surface!));
            var response = await session.UpdateAsync(bridge.Request(snapshot.Surface!) with
            {
                Action = PreviewSurfaceAction.FocusFirst,
                FocusToken = Guid.NewGuid().ToString("N") // Its local entry intention has already been revoked.
            });
            Assert.True(response.Success, response.Status);
            Assert.Null(response.Focused);
            Assert.True(session.IsAvailable);
            Assert.True(session.IsAttached);
            Assert.Equal(originalProcessId, client.ProcessId);
            var after = await client.HeartbeatSurfaceAsync(new(snapshot.Surface!));
            Assert.Equal(before.NavigationSequence, after.NavigationSequence);
            Assert.True((await client.CaptureAsync(new(snapshot.Version))).Success);
            await session.DeactivateAsync();
            Assert.False(session.IsAttached);
        }
        finally { session.Abort("Superseded keyboard entry test cleanup."); }
    }

    [Fact]
    public async Task DispatcherWatchdogBypassesAnOccupiedOrdinaryRequestGateAndLeavesOtherHostAlive()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.NativeWatchdog.").FullName;
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath, TimeSpan.FromMinutes(1));
        await using var independent = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        using var bridge = await OwnedNativeBridge.CreateAsync();
        IPreviewInteractionSession? session = null;
        try
        {
            string marker = Path.Combine(directory, "callback.txt");
            var rendered = await client.RenderAsync(Request() with
            {
                AssemblyPath = typeof(NativeCallbackHangControl).Assembly.Location,
                ProjectDirectory = AppContext.BaseDirectory,
                Text = "<probe:NativeCallbackHangControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' Name='HangTarget' MarkerPath='" +
                    SecurityElement.Escape(marker) + "'/>"
            });
            Assert.True(rendered.Success, string.Join("\n", rendered.Diagnostics.Select(d => d.Message)));
            Assert.True((await independent.RenderAsync(Request())).Success);
            using var originalProcess = Process.GetProcessById(client.ProcessId!.Value);
            using var independentProcess = Process.GetProcessById(independent.ProcessId!.Value);
            _ = originalProcess.Handle;
            _ = independentProcess.Handle;
            session = Assert.IsAssignableFrom<IPreviewInteractionSession>(client.CreateInteractionSession(rendered.Surface!));
            var attached = await session.UpdateAsync(bridge.Request(rendered.Surface!));
            Assert.True(attached.Success, attached.Status);
            var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += (_, change) => { if (!change.Available) disconnected.TrySetResult(change.Status ?? ""); };
            string node = Assert.Single(rendered.Nodes, n => n.Name == "HangTarget").Id;
            var pending = client.SetPropertyAsync(new(rendered.Version, node, nameof(NativeCallbackHangControl.Hang), "True",
                OwnerType: typeof(NativeCallbackHangControl).FullName, OwnerAssembly: typeof(NativeCallbackHangControl).Assembly.GetName().Name));
            using var entered = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker)) await Task.Delay(20, entered.Token);
            await originalProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("heartbeat", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)), StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAnyAsync<Exception>(() => pending);
            Assert.False(session.IsAttached);
            Assert.False(independentProcess.HasExited);
            Assert.True((await independent.CaptureAsync(new(1))).Success);
            var replacement = await client.RenderAsync(Request(2));
            Assert.True(replacement.Success, replacement.Status);
            int replacementPid = client.ProcessId!.Value;
            session.Abort("Late cleanup for the hung surface.");
            Assert.Equal(replacementPid, client.ProcessId);
            Assert.NotNull((await client.InspectAsync(new(replacement.Version, replacement.Nodes[0].Id))).Node);
        }
        finally
        {
            session?.Abort("Watchdog test cleanup.");
            await client.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentAbortAndStopConfirmOnlyThePinnedHostEnds()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        await using var independent = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        using var bridge = await OwnedNativeBridge.CreateAsync();
        var snapshot = await client.RenderAsync(Request());
        Assert.True(snapshot.Success);
        Assert.True((await independent.RenderAsync(Request())).Success);
        var session = Assert.IsAssignableFrom<IPreviewInteractionSession>(client.CreateInteractionSession(snapshot.Surface!));
        using var process = Process.GetProcessById(client.ProcessId!.Value);
        using var other = Process.GetProcessById(independent.ProcessId!.Value);
        _ = process.Handle;
        _ = other.Handle;
        try
        {
            Assert.True((await session.UpdateAsync(bridge.Request(snapshot.Surface!))).Success);
            await Task.WhenAll(Task.Run(() => session.Abort("Native bridge destruction.")), client.StopAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
            Assert.False(session.IsAttached);
            Assert.False(other.HasExited);
            var restarted = await client.RenderAsync(Request(2));
            Assert.True(restarted.Success);
            session.Abort("Repeated old bridge cleanup.");
            Assert.NotNull((await client.InspectAsync(new(restarted.Version, restarted.Nodes[0].Id))).Node);
        }
        finally { session.Abort("Concurrent teardown test cleanup."); }
    }

    private sealed class OwnedNativeBridge : IDisposable
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;
        private readonly nint _handle;
        private readonly string _token;
        private readonly string _navigationToken = Guid.NewGuid().ToString("N");
        private bool _disposed;

        private OwnedNativeBridge(Thread thread, Dispatcher dispatcher, nint parent, nint handle, string token)
        { _thread = thread; _dispatcher = dispatcher; _handle = handle; _token = token; }

        public static async Task<OwnedNativeBridge> CreateAsync()
        {
            var ready = new TaskCompletionSource<OwnedNativeBridge>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                nint parent = 0, handle = 0;
                string token = Guid.NewGuid().ToString("N");
                try
                {
                    if (SetThreadDpiAwarenessContext(new nint(-4)) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    parent = CreateWindowEx(0, "STATIC", "Preview lifetime test owner", unchecked((int)0x80000000), -32000, -32000, 400, 300, 0, 0, 0, 0);
                    handle = CreateWindowEx(0, "STATIC", "Preview lifetime test bridge", 0x40000000, 0, 0, 400, 300, parent, 0, 0, 0);
                    if (parent == 0 || handle == 0 || !SetProp(handle, "WpfStudio.PreviewBridge." + token, new nint(1)))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    ready.TrySetResult(new(thread!, Dispatcher.CurrentDispatcher, parent, handle, token));
                    Dispatcher.Run();
                }
                catch (Exception exception) { ready.TrySetException(exception); }
                finally
                {
                    if (handle != 0) { RemoveProp(handle, "WpfStudio.PreviewBridge." + token); DestroyWindow(handle); }
                    if (parent != 0) DestroyWindow(parent);
                }
            }) { IsBackground = true, Name = "Owned native preview test bridge" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public PreviewSurfaceRequest Request(PreviewSurfaceIdentity surface) =>
            new(surface, _token, _handle.ToInt64(), Environment.ProcessId, 400, 300, NavigationToken: _navigationToken);

        public void Dispose()
        {
            if (_disposed) return;
            if (GetWindow(_handle, 5) != 0)
                throw new InvalidOperationException("The fixture retains its native bridge until its foreign child has detached or exited.");
            _disposed = true;
            // Every caller first detaches or confirms exact host termination.
            // Only this fixture's own dispatcher destroys its own native HWNDs.
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "The owned native bridge dispatcher did not stop.");
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint value);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(int extended, string type, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint window, string name, nint value);
        [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    }
}

/// <summary>A real WPF property callback in the isolated host, never invoked in the test process.</summary>
public sealed class NativeCallbackHangControl : TextBlock
{
    public static readonly DependencyProperty MarkerPathProperty = DependencyProperty.Register(nameof(MarkerPath), typeof(string),
        typeof(NativeCallbackHangControl), new PropertyMetadata(""));
    public static readonly DependencyProperty HangProperty = DependencyProperty.Register(nameof(Hang), typeof(bool),
        typeof(NativeCallbackHangControl), new PropertyMetadata(false, (target, args) =>
        {
            if (!(bool)args.NewValue) return;
            File.WriteAllText((string)target.GetValue(MarkerPathProperty), "entered");
            Thread.Sleep(Timeout.Infinite);
        }));
    public string MarkerPath { get => (string)GetValue(MarkerPathProperty); set => SetValue(MarkerPathProperty, value); }
    public bool Hang { get => (bool)GetValue(HangProperty); set => SetValue(HangProperty, value); }
}
