using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using WpfStudio.Contracts;

namespace WpfStudio.Hosting;

/// <summary>How to launch one worker. It receives <c>--pipe name</c> and then <see cref="Arguments"/>.</summary>
/// <param name="ConnectTimeout">How long the worker has to open its pipe. A worker's startup work has to fit its own budget, so these differ.</param>
public sealed record WorkerProcessOptions(string HostPath, string PipeName, TimeSpan ConnectTimeout)
{
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Defaults to the host's own directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>The dotnet host that runs a managed .dll; defaults to the one on PATH.</summary>
    public string? DotNetPath { get; init; }
    /// <summary>Killing also ends the processes the worker started.</summary>
    public bool KillProcessTree { get; init; }
    /// <summary>Last changes to the start info, such as its environment.</summary>
    public Action<ProcessStartInfo>? ConfigureStart { get; init; }
    /// <summary>Refuses a pipe served by anything but the launched process. Turn off only when that process may be a launcher, such as a shim <c>dotnet</c> on PATH that starts the real host as a child; the pipe stays limited to the current user.</summary>
    public bool VerifyServerProcess { get; init; } = true;
}

/// <summary>A worker that never opened its pipe. <see cref="ExitCode"/> is set when it exited, and null when its connect budget ran out.</summary>
public sealed class WorkerConnectionException(string message, string errorTail, int? exitCode = null) : InvalidOperationException(message)
{
    /// <summary>The last lines the worker wrote to stderr, which usually say why.</summary>
    public string ErrorTail { get; } = errorTail;
    public int? ExitCode { get; } = exitCode;
}

/// <summary>
/// One launched worker process and its named pipe. It owns the launch's OS handle, so its process id can never come to
/// mean a later process; keeps the end of stderr; connects within a budget, failing early if the worker exits first;
/// connects only to a pipe of the current user that the launched process itself serves; and ends the process on request.
/// </summary>
public sealed class WorkerProcessHost(WorkerProcessOptions options) : IDisposable
{
    private const int ErrorTailLines = 20, ErrorTailCharacters = 4000;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private readonly Queue<string> _errorLines = new();
    private int _errorCharacters;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread when the process exits, or synchronously on a thread that is checking <see cref="HasExited"/>.</summary>
    public event Action? Exited;
    /// <summary>The launched process's id, valid from <see cref="Start"/>.</summary>
    public int ProcessId { get; private set; }
    public DateTime StartTimeUtc => (_process ?? throw new InvalidOperationException("The worker has not started.")).StartTime.ToUniversalTime();
    /// <summary>Also true before the start and after <see cref="Dispose"/>.</summary>
    public bool HasExited
    {
        get
        {
            try { return _process is not { } process || process.HasExited; }
            catch (InvalidOperationException) { return true; } // A concurrent stop disposed the process.
        }
    }
    /// <summary>The connected pipe, valid after <see cref="ConnectAsync"/>.</summary>
    public NamedPipeClientStream Pipe => _pipe ?? throw new InvalidOperationException("The worker's pipe is not connected.");
    /// <summary>The last lines the worker wrote to stderr, so a crash report keeps its exception message and not only the final stack frame.</summary>
    public string ErrorTail { get { lock (_errorLines) return string.Join(Environment.NewLine, _errorLines); } }

    private void RecordError(string line)
    {
        if (line.Length > ErrorTailCharacters) line = line[^ErrorTailCharacters..];
        lock (_errorLines)
        {
            _errorLines.Enqueue(line);
            _errorCharacters += line.Length;
            while (_errorLines.Count > ErrorTailLines || _errorCharacters > ErrorTailCharacters) _errorCharacters -= _errorLines.Dequeue().Length;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null) throw new InvalidOperationException("The worker has already started.");
        var start = ProcessLaunch.Create(options.HostPath, options.WorkingDirectory ?? Path.GetDirectoryName(options.HostPath)!,
            ["--pipe", options.PipeName, .. options.Arguments], options.DotNetPath);
        options.ConfigureStart?.Invoke(start);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) RecordError(line); };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) => Exited?.Invoke();
        _process = process;
        try
        {
            process.Start();
            // Accessing Handle retains the launch's original OS object even after its id is reusable.
            _ = process.Handle;
            ProcessId = process.Id;
            process.BeginErrorReadLine(); process.BeginOutputReadLine();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Connects to the worker's pipe and checks who serves it.</summary>
    /// <exception cref="WorkerConnectionException">The worker exited first, or its budget ran out.</exception>
    /// <exception cref="InvalidDataException">Another process serves the pipe.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("The worker has not started.");
        var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _pipe = pipe;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ConnectTimeout);
        var connected = pipe.ConnectAsync(budget.Token);
        // A worker that exits, on a missing runtime say, can never connect: report that now rather than after the budget.
        var exited = process.WaitForExitAsync(budget.Token);
        await Task.WhenAny(connected, exited).ConfigureAwait(false);
        var gone = exited.IsCompletedSuccessfully && !connected.IsCompletedSuccessfully;
        await budget.CancelAsync().ConfigureAwait(false);
        exited.Forget();
        if (gone)
        {
            connected.Forget();
            throw new WorkerConnectionException($"The worker exited with code {process.ExitCode} before connecting. {ErrorTail}", ErrorTail, process.ExitCode);
        }
        try { await connected.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new WorkerConnectionException($"The worker did not connect within {options.ConnectTimeout.TotalSeconds:0.#} s. {ErrorTail}", ErrorTail); }
        if (options.VerifyServerProcess && (!NamedPipeIdentity.TryGetServerProcessId(pipe, out var server) || server != ProcessId))
            throw new InvalidDataException("The worker's pipe server is not the process launched for this session.");
    }

    /// <summary>Ends the worker. Never throws; a process that ignores the kill for <paramref name="waitForExit"/> is left to the OS.</summary>
    public void Kill(TimeSpan? waitForExit = null)
    {
        if (_process is not { } process) return;
        ProcessLaunch.TryKill(process, options.KillProcessTree);
        if (waitForExit is not { } wait) return;
        try { process.WaitForExit(wait); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }

    /// <summary>Closes the pipe, gives the worker <paramref name="grace"/> to finish on its own, then kills it and waits for the exit. Never throws.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        try
        {
            _pipe?.Dispose();
            if (_process is not { } process) return;
            if (grace > TimeSpan.Zero && await ExitedWithinAsync(process, grace).ConfigureAwait(false)) return;
            ProcessLaunch.TryKill(process, options.KillProcessTree);
            await ExitedWithinAsync(process, ExitWait).ConfigureAwait(false);
        }
        finally { Dispose(); }
    }

    private static async Task<bool> ExitedWithinAsync(Process process, TimeSpan wait)
    {
        using var deadline = new CancellationTokenSource(wait);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return true; } // A concurrent stop disposed the process.
    }

    /// <summary>Closes the pipe and kills the worker if it is still running, without waiting for its exit.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe?.Dispose();
        Kill();
        _process?.Dispose();
    }
}
