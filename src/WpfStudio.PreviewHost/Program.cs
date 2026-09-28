using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using StreamJsonRpc;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int pipeIndex = Array.IndexOf(args, "--pipe");
        if (pipeIndex < 0 || pipeIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: WpfStudio.PreviewHost --pipe <name>");
            return 2;
        }
        string? Argument(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string? session = Argument("--session");
        string? parentArgument = Argument("--parent-process");
        int parentProcessId = 0;
        if ((session is not null || parentArgument is not null) &&
            (!Guid.TryParseExact(session, "N", out _) || !int.TryParse(parentArgument, out parentProcessId) || parentProcessId <= 0))
        {
            Console.Error.WriteLine("Native preview requires --session <Guid N> and --parent-process <PID>.");
            return 2;
        }

        // The dedicated WPF dispatcher owns every preview object. A disconnected
        // editor ends this process, including all user control state and assemblies.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var cancellation = new CancellationTokenSource();
        int shadowIndex = Array.IndexOf(args, "--shadow-directory");
        int tokenIndex = Array.IndexOf(args, "--shadow-token");
        string? shadowDirectory = shadowIndex >= 0 && shadowIndex + 1 < args.Length ? args[shadowIndex + 1] : null;
        string? shadowToken = tokenIndex >= 0 && tokenIndex + 1 < args.Length ? args[tokenIndex + 1] : null;
        using var engine = new PreviewEngine(application.Dispatcher, shadowDirectory, shadowToken, session, parentProcessId);
        application.DispatcherUnhandledException += (_, e) =>
        {
            engine.ReportUnhandledException(e.Exception);
            e.Handled = true;
        };
        var connection = Task.Run(async () =>
        {
            try
            {
                using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var pipe = new NamedPipeServerStream(args[pipeIndex + 1], PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(startupTimeout.Token);
                if (parentProcessId > 0 && (!PreviewNativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientProcessId)
                    || clientProcessId != parentProcessId))
                    throw new InvalidOperationException("The preview pipe client is not the launching editor process.");
                using var rpc = new JsonRpc(pipe);
                rpc.AddLocalRpcTarget<IPreviewRpc>(engine, null);
                rpc.StartListening();
                await rpc.Completion;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                Console.Error.WriteLine(exception.Message);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                // A control can block its dispatcher even when no RPC is pending. EOF must
                // still end this owned process, including callbacks in engine.Dispose after
                // Shutdown returns. Only actual process exit cancels this final deadline.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                    Environment.Exit(0);
                });
                try
                {
                    await application.Dispatcher.InvokeAsync(() => application.Shutdown(), DispatcherPriority.Send)
                        .Task.WaitAsync(TimeSpan.FromSeconds(1.5));
                }
                catch (Exception exception) when (exception is TimeoutException or TaskCanceledException or InvalidOperationException)
                {
                    Environment.Exit(0);
                }
            }
        });
        application.Run();
        cancellation.Cancel();
        return 0;
    }
}
