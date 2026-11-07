using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Terminal;

public sealed record TerminalShell(string Name, string Executable, string Arguments);

/// <summary>One tab of the terminal pane: an interactive shell, or a task's program.</summary>
public sealed partial class TerminalSessionViewModel(string title, ConPtySession session, string? taskLabel = null) : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = title;
    public ConPtySession Session { get; } = session;
    /// <summary>The task this tab runs; null for a shell.</summary>
    public string? TaskLabel { get; } = taskLabel;
    public bool IsTask => TaskLabel is not null;
}

/// <summary>A task program for its own terminal tab. <paramref name="Replacing"/> is a finished tab of the same task, which the new one takes the place of.</summary>
public sealed record TerminalTaskStart(string TaskLabel, string Title, string Executable, string Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null, Action<byte[]>? ObserveOutput = null, TerminalSessionViewModel? Replacing = null);

public sealed partial class TerminalViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IUiDispatcher dispatcher;
    private int sequence;
    private int disposed;
    public TerminalViewModel(IUiDispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        var pwsh = FindExecutable("pwsh.exe");
        if (pwsh is not null) Shells.Add(new TerminalShell("PowerShell 7", pwsh, "-NoLogo"));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell)) Shells.Add(new TerminalShell("Windows PowerShell", powershell, "-NoLogo"));
        Shells.Add(new TerminalShell("Command Prompt", Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"), ""));
        SelectedShell = Shells[0];
    }
    public ObservableCollection<TerminalShell> Shells { get; } = [];
    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = [];
    [ObservableProperty] public partial TerminalShell? SelectedShell { get; set; }
    [ObservableProperty] public partial TerminalSessionViewModel? SelectedSession { get; set; }
    [ObservableProperty] public partial string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string Error { get; set; } = "";
    [RelayCommand] private async Task NewSessionAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        TerminalShell? shell = null;
        string directory = "";
        await dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            shell = SelectedShell; directory = WorkingDirectory; Error = "";
        });
        if (shell is null) return;
        try
        {
            var session = await Task.Run(() => new ConPtySession(shell.Executable, shell.Arguments, directory));
            if (Volatile.Read(ref disposed) != 0) { await session.DisposeAsync(); return; }
            session.Error += value => dispatcher.Post(() => Error = value);
            bool accepted = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref disposed) != 0) return;
                var viewModel = new TerminalSessionViewModel($"{shell.Name} {++sequence}", session);
                Sessions.Add(viewModel); SelectedSession = viewModel; accepted = true;
            });
            if (!accepted) await session.DisposeAsync();
        }
        catch (Exception ex) { await dispatcher.InvokeAsync(() => Error = ex.Message); }
    }
    /// <summary>
    /// Starts a task's program in a new tab and selects it. Output keeps flowing while the tab has never been shown, so a
    /// task in the background is not held up by a renderer that does not exist yet. Null when the terminal was disposed.
    /// </summary>
    public async Task<TerminalSessionViewModel?> StartTaskAsync(TerminalTaskStart start)
    {
        if (Volatile.Read(ref disposed) != 0) return null;
        var options = new ConPtyOptions { Environment = start.Environment, ObserveOutput = start.ObserveOutput, KeepOutputFlowing = true };
        var session = await Task.Run(() => new ConPtySession(start.Executable, start.Arguments, start.WorkingDirectory, options));
        session.Error += value => dispatcher.Post(() => Error = value);
        TerminalSessionViewModel? accepted = null;
        TerminalSessionViewModel? replaced = null;
        await dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            accepted = new TerminalSessionViewModel(start.Title, session, start.TaskLabel);
            var index = start.Replacing is { } previous ? Sessions.IndexOf(previous) : -1;
            if (index >= 0) { replaced = start.Replacing; Sessions[index] = accepted; }
            else Sessions.Add(accepted);
            SelectedSession = accepted; Error = "";
        });
        if (accepted is null) { await session.DisposeAsync(); return null; }
        if (replaced is not null) await replaced.Session.DisposeAsync();
        return accepted;
    }
    /// <summary>Closes a tab, ending its program.</summary>
    public async Task CloseAsync(TerminalSessionViewModel tab)
    {
        var removed = false;
        await dispatcher.InvokeAsync(() =>
        {
            removed = Sessions.Remove(tab);
            if (removed && (SelectedSession is null || ReferenceEquals(SelectedSession, tab))) SelectedSession = Sessions.LastOrDefault();
        });
        if (removed) await tab.Session.DisposeAsync();
    }
    [RelayCommand] private async Task CloseSessionAsync()
    {
        TerminalSessionViewModel? selected = null;
        await dispatcher.InvokeAsync(() =>
        {
            selected = SelectedSession;
            if (selected is null) return;
            Sessions.Remove(selected); SelectedSession = Sessions.LastOrDefault();
        });
        if (selected is not null) await selected.Session.DisposeAsync();
    }
    [RelayCommand] private async Task InterruptAsync()
    {
        if (SelectedSession is not { } selected) return;
        try { await selected.Session.WriteAsync("\u0003"); } catch (Exception ex) { Error = ex.Message; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        TerminalSessionViewModel[] sessions = [];
        Task[] pendingOperations = [];
        await dispatcher.InvokeAsync(() =>
        {
            sessions = Sessions.ToArray();
            Sessions.Clear(); SelectedSession = null;
            pendingOperations = new[] { NewSessionCommand.ExecutionTask, CloseSessionCommand.ExecutionTask }.OfType<Task>().ToArray();
        }).ConfigureAwait(false);
        // A creation already running observes disposed and tears down its new process before returning.
        await Task.WhenAll(pendingOperations).ConfigureAwait(false);
        foreach (var session in sessions) await session.Session.DisposeAsync().ConfigureAwait(false);
    }
    private static string? FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory.Trim('"'), name)).FirstOrDefault(File.Exists);
}
