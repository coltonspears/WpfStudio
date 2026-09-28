using System.Reflection;
using System.Runtime.Loader;

// The runtime requires this exact global type and method signature.
internal static class StartupHook
{
    private const string PipeVariable = "WPFSTUDIO_INSPECTION_PIPE";
    private const string TokenVariable = "WPFSTUDIO_INSPECTION_TOKEN";
    private const string OwnerVariable = "WPFSTUDIO_INSPECTION_OWNER_PID";
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        AgentLoadContext? context = null;
        try
        {
            string? pipe = Environment.GetEnvironmentVariable(PipeVariable);
            string? token = Environment.GetEnvironmentVariable(TokenVariable);
            string? owner = Environment.GetEnvironmentVariable(OwnerVariable);
            // Only this launch's hook and session data are removed. Other startup
            // hooks and the application's environment retain their original values.
            Environment.SetEnvironmentVariable(PipeVariable, null);
            Environment.SetEnvironmentVariable(TokenVariable, null);
            Environment.SetEnvironmentVariable(OwnerVariable, null);
            RemoveOwnHook();
            if (string.IsNullOrWhiteSpace(pipe) || pipe.Length > 200 || pipe.IndexOfAny(['\\', '/', ':']) >= 0 ||
                string.IsNullOrWhiteSpace(token) || token.Length > 512 || !int.TryParse(owner, out int ownerPid) || ownerPid < 1)
                return;

            string directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            string agentPath = Path.Combine(directory, "WpfStudio.Inspection.Agent.dll");
            if (!File.Exists(agentPath)) return;
            context = new AgentLoadContext(directory);
            Assembly agent = context.LoadFromAssemblyPath(agentPath);
            MethodInfo initialize = agent.GetType("WpfStudio.Inspection.Agent.AgentEntry", throwOnError: true)!
                .GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string), typeof(int)])
                ?? throw new MissingMethodException("The inspection agent entry point is unavailable.");
            // Initialization installs its bounded trace listener before Main, then
            // queues I/O. It never waits for a pipe, Application or dispatcher.
            initialize.Invoke(null, [pipe, token, ownerPid]);
            // Keep the collectible context alive until AgentEntry has captured
            // ownership for its asynchronous connection and cleanup lifetime.
            GC.KeepAlive(context);
        }
        catch (Exception)
        {
            // An optional inspector must never prevent the application starting.
            context?.Unload();
        }
    }

    private static void RemoveOwnHook()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        if (configured is null) return;
        string ownPath = Path.GetFullPath(typeof(StartupHook).Assembly.Location);
        string ownName = typeof(StartupHook).Assembly.GetName().Name!;
        var remaining = configured.Split(Path.PathSeparator).Where(value =>
        {
            if (string.Equals(value, ownName, StringComparison.OrdinalIgnoreCase)) return false;
            try { return !Path.IsPathFullyQualified(value) || !string.Equals(Path.GetFullPath(value), ownPath, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return true; }
        });
        string result = string.Join(Path.PathSeparator, remaining);
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", result.Length == 0 ? null : result);
    }

    private sealed class AgentLoadContext(string directory) : AssemblyLoadContext("WpfStudio inspection agent", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "WpfStudio.Inspection.Agent" or "WpfStudio.Inspection.Protocol" or "WpfStudio.Wpf.PropertyEditing" or "WpfStudio.Wpf.Diagnostics")
                return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
            // WPF and framework identities must be shared with the inspected app.
            // No arbitrary adjacent dependency is probed or installed in Default.
            return null;
        }
    }
}
