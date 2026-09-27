using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Terminal;

public sealed record TerminalShell(string Name, string Executable, string Arguments);
public sealed record TerminalSessionViewModel(string Title, ConPtySession Session);

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
