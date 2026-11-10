using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using WpfStudio.Contracts;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

/// <summary>
/// The .NET Framework host's process, in the default domain. It never loads project code: the first render creates a
/// domain set up like the project's own process, and every call is passed to the engine there as JSON text.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int pipeIndex = Array.IndexOf(args, "--pipe");
        if (pipeIndex < 0 || pipeIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: WpfStudio.PreviewHost.Framework --pipe <name>");
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

        var bridge = new EngineBridge(Argument("--shadow-directory"), Argument("--shadow-token"), session, parentProcessId);
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = CreatePipe(args[pipeIndex + 1]);
            await pipe.WaitForConnectionAsync(startupTimeout.Token).ConfigureAwait(false);
            if (parentProcessId > 0 && (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientProcessId) || clientProcessId != parentProcessId))
                throw new InvalidOperationException("The preview pipe client is not the launching editor process.");
            using var rpc = new JsonRpc(pipe);
            bridge.Register(rpc);
            rpc.StartListening();
            await rpc.Completion.ConfigureAwait(false);
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
            // A control can block its dispatcher even when no RPC is pending. EOF must still end this owned process.
            await bridge.ShutdownAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            Environment.Exit(0);
        }
        return 0;
    }

    /// <summary>The equivalent of PipeOptions.CurrentUserOnly, which .NET Framework lacks. The client refuses a pipe owned by anyone else.</summary>
    private static NamedPipeServerStream CreatePipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no security identifier.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

/// <summary>Answers each preview RPC method by passing it to the engine in the project's domain.</summary>
internal sealed class EngineBridge(string? shadowDirectory, string? shadowToken, string? sessionId, int parentProcessId)
{
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    private readonly object _gate = new();
    private readonly PreviewHostSession _session = PreviewHostSessions.Create(sessionId, parentProcessId);
    private FrameworkEngineServer? _server;

    /// <summary>Every member of the contract, so a method added to it cannot be missed here.</summary>
    public void Register(JsonRpc rpc)
    {
        foreach (var method in typeof(IPreviewRpc).GetMethods())
        {
            string name = method.Name;
            if (name == nameof(IPreviewRpc.GetSessionAsync))
                rpc.AddLocalRpcMethod(name, new Func<CancellationToken, Task<JToken>>(_ => Task.FromResult(JToken.FromObject(_session, Serializer))));
            else if (method.GetParameters().Length == 2)
                rpc.AddLocalRpcMethod(name, new Func<JToken, CancellationToken, Task<JToken>>((argument, token) => CallAsync(name, argument, token)));
            else
                rpc.AddLocalRpcMethod(name, new Func<CancellationToken, Task<JToken>>(token => CallAsync(name, null, token)));
        }
    }

    private async Task<JToken> CallAsync(string method, JToken? argument, CancellationToken cancellationToken)
    {
        var completion = new EngineCompletion();
        // The first call copies the project's output and creates its domain. That must not stop this connection from reading its next message.
        var server = await Task.Run(() => Server(method, argument), cancellationToken).ConfigureAwait(false);
        try
        {
            var cancellation = server.Invoke(method, argument?.ToString(Formatting.None), completion);
            using (cancellationToken.Register(() =>
            {
                // The engine disconnects the cancellation when its call ends, which can be just before this registration is removed.
                try { cancellation.Cancel(); }
                catch (System.Runtime.Remoting.RemotingException) { }
            }))
                // Raw, because the engine has already produced exactly the text this call must answer with.
                return new JRaw(await completion.Task.ConfigureAwait(false));
        }
        // The completion has no lease, so remoting keeps it, and with it the whole response text, until it is disconnected.
        // The engine has finished with it by now: the task above completes only through the engine's last call on it.
        finally { System.Runtime.Remoting.RemotingServices.Disconnect(completion); }
    }

    private FrameworkEngineServer Server(string method, JToken? argument)
    {
        lock (_gate)
        {
            if (_server is not null) return _server;
            try
            {
                return _server = FrameworkPreviewDomain.Create(method == nameof(IPreviewRpc.RenderAsync) ? argument?.ToObject<PreviewRequest>(Serializer) : null,
                    shadowDirectory, shadowToken, sessionId, parentProcessId, new FrameworkHostServices(ReadLibrary), ReadTargetFramework);
            }
            // The domain's own setup runs through reflection, which would otherwise be all the editor gets to see.
            catch (Exception exception)
            {
                throw new InvalidOperationException("The preview could not set up the project's application domain: " + exception.GetBaseException(), exception);
            }
        }
    }

    public async Task ShutdownAsync(TimeSpan deadline)
    {
        FrameworkEngineServer? server;
        lock (_gate) server = _server;
        if (server is null) return;
        await Task.WhenAny(Task.Run(server.Shutdown), Task.Delay(deadline)).ConfigureAwait(false);
    }

    private static string? ReadLibrary(string path) =>
        XamlNamespaceLibraryReader.Read(path) is { } library ? JToken.FromObject(library, Serializer).ToString(Formatting.None) : null;

    /// <summary>The moniker the project was built for, which selects WPF's compatibility behaviour in its domain.</summary>
    private static string? ReadTargetFramework(string assembly)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                if (metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name) != nameof(TargetFrameworkAttribute)) continue;
                var value = metadata.GetBlobReader(attribute.Value);
                return value.ReadUInt16() == 1 ? value.ReadSerializedString() : null;
            }
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or InvalidOperationException) { }
        return null;
    }
}
