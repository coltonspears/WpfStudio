using System.Diagnostics;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

internal sealed class FixtureApplication : IAsyncDisposable
{
    private Process? _process;
    private Task<string>? _output;
    private Task<string>? _error;
    public FixtureApplication(string framework, bool appHost = false)
    {
        Program = Path.Combine(AppContext.BaseDirectory, "Fixtures", framework,
            "WpfStudio.InspectionFixture." + (appHost ? "exe" : "dll"));
        Assert.True(File.Exists(Program), "The real WPF inspection fixture must be built: " + Program);
        DirectoryPath = Path.Combine(Path.GetTempPath(), "WpfStudio-InspectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }
    public string Program { get; }
    public string DirectoryPath { get; }
    public int ProcessId => _process!.Id;
    public bool HasExited => _process!.HasExited;
    public ResolvedLaunch Launch(IReadOnlyDictionary<string, string>? environment = null) =>
        new(Program, Path.GetDirectoryName(Program)!, [DirectoryPath], environment ?? new Dictionary<string, string>());

    public void Start(IReadOnlyDictionary<string, string> environment)
    {
        _process = Process.Start(InspectionLaunch.CreateStartInfo(Launch(), environment))!;
        _output = _process.StandardOutput.ReadToEndAsync();
        _error = _process.StandardError.ReadToEndAsync();
    }

    public async Task<FixtureReady> WaitForReadyAsync(CancellationToken token)
    {
        var path = Path.Combine(DirectoryPath, "ready.json");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
                throw new InvalidOperationException("Inspection fixture exited before ready. " + await OutputAsync());
            try
            {
                if (File.Exists(path))
                {
                    var result = JsonSerializer.Deserialize<FixtureReady>(await File.ReadAllTextAsync(path, token))!;
                    _process ??= Process.GetProcessById(result.ProcessId);
                    return result;
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(25, token);
        }
    }

    public async Task<string> SendAsync(string action, CancellationToken token)
    {
        var id = await SubmitAsync(action, token);
        return await WaitForAcknowledgementAsync(id, token);
    }

    public async Task<string> SubmitAsync(string action, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(DirectoryPath, id + ".json");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { Id = id, Action = action }), token);
        File.Move(temporary, Path.Combine(DirectoryPath, "command.json"));
        return id;
    }

    public async Task<string> WaitForAcknowledgementAsync(string id, CancellationToken token)
    {
        var path = Path.Combine(DirectoryPath, id + ".ack");
        while (!File.Exists(path))
        {
            if (HasExited) throw new InvalidOperationException("Inspection fixture exited while awaiting " + id + ". " + await OutputAsync());
            await Task.Delay(25, token);
        }
        while (true)
        {
            try { return await File.ReadAllTextAsync(path, token); }
            catch (IOException) { await Task.Delay(10, token); }
        }
    }

    private async Task<string> OutputAsync() => (_output is null ? "" : await _output) + (_error is null ? "" : await _error);

    public async ValueTask DisposeAsync()
    {
        if (_process is { } process)
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await SendAsync("shutdown", timeout.Token); await process.WaitForExitAsync(timeout.Token); }
                catch (Exception) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            await process.WaitForExitAsync();
            process.Dispose();
        }
        // This exact random directory is created and owned by this fixture instance.
        try { Directory.Delete(DirectoryPath, recursive: true); } catch (IOException) { }
    }
}

internal sealed record FixtureReady(int ProcessId, int RuntimeMajor, int MainThreadId, int ChildThreadId,
    string? ExistingHookRan, string? ExistingHookCount, string? ProfileValue, string? RemainingHooks, string? InspectionPipe,
    string? InspectionToken, string? InspectionOwner, string? AgentPath);
