using System.Collections.Concurrent;
using System.Diagnostics;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>A session attached to the scripted adapter process, which picks its behavior from an environment variable it inherits.</summary>
internal sealed class AdapterRun : IAsyncDisposable
{
    /// <summary>The variable is process-wide, so the classes that use the adapter must not run in parallel.</summary>
    public const string Collection = "Scripted debug adapter";
    private Process? _adapter;
    public DebugSession Session { get; } = new() { AdapterPath = LocateAdapter() };
    public ConcurrentBag<string> Errors { get; } = [];

    public AdapterRun(string scenario)
    {
        UseScenario(scenario);
        Session.Error += Errors.Add;
    }

    public static void UseScenario(string scenario) => Environment.SetEnvironmentVariable("WPFSTUDIO_FAKE_DAP_SCENARIO", scenario);

    public async Task AttachAsync(int seconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var attach = Session.AttachAsync(Environment.ProcessId, [], cancellationToken: deadline.Token);
        // The process starts before the session's first real await.
        if (Session.AdapterProcessId is { } id) _adapter = Process.GetProcessById(id);
        await attach;
    }

    public bool AdapterEnded() => _adapter?.WaitForExit(10_000) == true;

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        if (_adapter is null) return;
        try { if (!_adapter.HasExited) _adapter.Kill(); } catch (InvalidOperationException) { }
        _adapter.Dispose();
    }

    public static string LocateAdapter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent!.Name;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfStudio.Runtime.Tests.csproj"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "Fixtures", "FakeDapAdapter", "bin", configuration, "net10.0", "WpfStudio.FakeDapAdapter.exe");
    }
}
