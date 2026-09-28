using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Win32.SafeHandles;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

public static class AgentEntry
{
    private static int _initialized;

    public static void Initialize(string pipe, string token, int ownerPid)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        var context = AssemblyLoadContext.GetLoadContext(typeof(AgentEntry).Assembly);
        BindingTraceBuffer? traces = null;
        StaticResourceEvidenceCollector? resources = null;
        try
        {
            traces = new BindingTraceBuffer();
            resources = new StaticResourceEvidenceCollector();
            var captured = traces;
            var capturedResources = resources;
            // Loaded assemblies do not strongly root their collectible managed
            // context. Retain it across every await, including before lazy feature
            // assemblies load, until dispatcher cleanup and trace disposal finish.
            _ = Task.Run(() => RunAsync(pipe, token, ownerPid, captured, capturedResources, context));
        }
        catch (Exception)
        {
            traces?.Dispose();
            resources?.Dispose();
            if (context?.IsCollectible == true) context.Unload();
        }
    }

    private static async Task RunAsync(string pipeName, string token, int ownerPid, BindingTraceBuffer traces, StaticResourceEvidenceCollector resources,
        AssemblyLoadContext? context)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var connect = new HandshakeDeadline(ownerPid);
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != (uint)ownerPid)
                return;
            var hello = new InspectionHello(InspectionProtocol.Version, token, Environment.ProcessId,
                Environment.Version.ToString(), typeof(AgentEntry).Assembly.GetName().Version?.ToString() ?? "1",
                ["tree", "properties", "bindings", "early-binding-traces", "source-hints", "multiple-dispatchers", "pick", "highlight", "property-edit", "modules", "source-property-validation", "layout", "layout-overlay", "appearance", "binding-source"]);
            await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0, hello), connect.Token).ConfigureAwait(false);
            var welcomeMessage = await InspectionWire.ReadAsync(pipe, connect.Token).ConfigureAwait(false);
            if (welcomeMessage is not { Kind: "hello", Id: 0, Error: null } ||
                welcomeMessage.GetPayload<InspectionWelcome>().ProtocolVersion != InspectionProtocol.Version) return;
            await using var inspector = new RunningInspector(traces, resources);
            using var lifetime = new CancellationTokenSource();
            var nextRequest = ReadNextAsync(pipe, lifetime);
            try
            {
                while (true)
                {
                    // Read one frame ahead while dispatcher work runs. Closing the
                    // pipe cancels queued edits immediately instead of being hidden
                    // behind an unresponsive application's dispatcher callback.
                    var request = await nextRequest.ConfigureAwait(false);
                    if (request is null) return;
                    nextRequest = ReadNextAsync(pipe, lifetime);
                    InspectionMessage response;
                    try
                    {
                        // Responsive dispatchers restore owned overrides and remove
                        // input listeners/adorners before successful detach replies.
                        if (request.Kind == "detach") await inspector.DisposeAsync().ConfigureAwait(false);
                        response = request.Kind switch
                        {
                            "tree" => InspectionMessage.Create("tree", request.Id,
                                await inspector.SnapshotAsync(request.GetPayload<InspectionTreeRequest>()).ConfigureAwait(false)),
                            "inspect" => InspectionMessage.Create("inspect", request.Id,
                                await inspector.InspectAsync(request.GetPayload<InspectionNodeRequest>()).ConfigureAwait(false)),
                            "modules" => InspectionMessage.Create("modules", request.Id, inspector.GetModuleCatalog()),
                            "appearance" => InspectionMessage.Create("appearance", request.Id,
                                await inspector.GetAppearanceAsync(request.GetPayload<AppearanceRequest>()).ConfigureAwait(false)),
                            "binding-source" => InspectionMessage.Create("binding-source", request.Id,
                                await inspector.GetBindingSourceAsync(request.GetPayload<BindingSourceRequest>()).ConfigureAwait(false)),
                            "pick" => InspectionMessage.Create("pick", request.Id,
                                await inspector.SetPickingAsync(request.GetPayload<InspectionPickRequest>()).ConfigureAwait(false)),
                            "highlight" => InspectionMessage.Create("highlight", request.Id,
                                await inspector.HighlightAsync(request.GetPayload<InspectionHighlightRequest>()).ConfigureAwait(false)),
                            "validate-property" => InspectionMessage.Create("validate-property", request.Id,
                                await inspector.ValidatePropertyAsync(request.GetPayload<InspectionPropertyEdit>(), lifetime.Token).ConfigureAwait(false)),
                            "validate-source-property" => InspectionMessage.Create("validate-source-property", request.Id,
                                await inspector.ValidateSourcePropertyAsync(request.GetPayload<InspectionSourcePropertyRequest>(), lifetime.Token).ConfigureAwait(false)),
                            "set-property" => InspectionMessage.Create("set-property", request.Id,
                                await inspector.SetPropertyAsync(request.GetPayload<InspectionPropertyEdit>(), lifetime.Token).ConfigureAwait(false)),
                            "edit-status" => InspectionMessage.Create("edit-status", request.Id,
                                await inspector.GetEditStatusAsync(request.GetPayload<InspectionEditStatusRequest>()).ConfigureAwait(false)),
                            "detach" => InspectionMessage.Create("detach", request.Id, new { }),
                            _ => InspectionMessage.Create(request.Kind, request.Id, new { }, "Unsupported inspection request.")
                        };
                    }
                    catch (Exception exception)
                    {
                        response = InspectionMessage.Create(request.Kind, request.Id, new { },
                            RunningInspector.Limit(exception.GetBaseException().Message, 1500));
                    }
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    write.CancelAfter(TimeSpan.FromSeconds(5));
                    await InspectionWire.WriteAsync(pipe, response, write.Token).ConfigureAwait(false);
                    if (request.Kind == "detach") return;
                }
            }
            finally
            {
                lifetime.Cancel();
                await nextRequest.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Connection, protocol and inspector failures never reach application
            // dispatchers or alter the application's exception handling policy.
        }
        finally
        {
            traces.Dispose();
            resources.Dispose();
            if (context?.IsCollectible == true) context.Unload();
            GC.KeepAlive(context);
        }
    }

    private static async Task<InspectionMessage?> ReadNextAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        // There is no idle deadline; the debugger may suspend all agent threads.
        try
        {
            var message = await InspectionWire.ReadAsync(pipe, lifetime.Token).ConfigureAwait(false);
            if (message is not null) return message;
        }
        catch (Exception) { }
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        return null;
    }

    /// <summary>
    /// Counts responsive-agent time, with a bounded scheduling allowance per
    /// sample. A debugger suspends this monitor with the other managed threads;
    /// the wall-clock gap must not immediately expire the handshake on resume.
    /// The IDE additionally owns a deadline paused by actual debugger events.
    /// </summary>
    private sealed class HandshakeDeadline : IDisposable
    {
        private readonly CancellationTokenSource _deadline = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Process _owner;
        public CancellationToken Token => _deadline.Token;

        public HandshakeDeadline(int ownerPid)
        {
            _owner = Process.GetProcessById(ownerPid);
            _ = MonitorAsync();
        }

        private async Task MonitorAsync()
        {
            try
            {
                double remainingMilliseconds = 10_000;
                long previous = Stopwatch.GetTimestamp();
                while (remainingMilliseconds > 0 && !_owner.HasExited)
                {
                    await Task.Delay(250, _stop.Token).ConfigureAwait(false);
                    long now = Stopwatch.GetTimestamp();
                    remainingMilliseconds -= Math.Min(500, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                    previous = now;
                }
                if (!_stop.IsCancellationRequested) _deadline.Cancel();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception) { try { _deadline.Cancel(); } catch (ObjectDisposedException) { } }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _owner.Dispose();
            _deadline.Dispose();
            _stop.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
