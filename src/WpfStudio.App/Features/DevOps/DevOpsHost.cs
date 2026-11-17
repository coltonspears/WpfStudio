using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

/// <summary>What the DevOps tabs need from the workbench that owns the connection.</summary>
public interface IDevOpsHost
{
    IDevOpsProvider? Provider { get; }
    DevOpsIdentity? User { get; }
    DevOpsSettings Settings { get; }
    GitViewModel Git { get; }
    GitService GitService { get; }
    /// <summary>Changes on every connection; a read started for an older one is stale.</summary>
    int Version { get; }
    /// <summary>Signed in, and no action is running.</summary>
    bool CanAct { get; }
    /// <summary>
    /// Runs a change on the service as the workbench's one action: nothing happens while another runs, the Cancel button reaches it,
    /// and a failure is reported in the status bar instead of thrown.
    /// </summary>
    Task RunAsync(string label, Func<IDevOpsProvider, DevOpsIdentity, CancellationToken, Task> action, CancellationToken cancellationToken = default);
    void ReportError(Exception exception);
    void ReportStatus(string status);
    void OpenUrl(string url);
    void OpenFile(string path);
    void Copy(string text, string what);
    Task<string?> PromptAsync(string title, string message, string defaultValue = "");
    Task<bool> ConfirmAsync(string title, string message);
    void ShowWorkItem(int id);
    void ShowPullRequest(int number);
    /// <summary>Saves the workbench's preferences (the saved tokens are kept as they are) and applies them.</summary>
    Task SaveSettingsAsync(DevOpsSettings settings);
    /// <summary>Opens the form that creates a pull request from the current branch.</summary>
    void StartCreatePullRequest();
    /// <summary>Called by a tab after its commands' availability may have changed, so the others' follow.</summary>
    void NotifyCommands();
}

/// <summary>
/// One read that a newer one replaces: starting a load cancels the previous one, and only the newest reports that it finished.
/// Used on the UI thread only. The load checks its token after each await before it publishes anything.
/// </summary>
public sealed class LatestLoad
{
    private CancellationTokenSource? _current;

    /// <summary>The newest load, for tests and for callers that must wait for it.</summary>
    public Task Task { get; private set; } = Task.CompletedTask;

    /// <summary>Starts a load nobody waits for. It cannot fault: failures go to <paramref name="failed"/>.</summary>
    public void Start(Func<CancellationToken, Task> load, Action<Exception> failed, Action<bool>? loading = null) => _ = Run(load, failed, loading);

    /// <summary>As <see cref="Start"/>, returning the load to wait for.</summary>
    public Task Run(Func<CancellationToken, Task> load, Action<Exception> failed, Action<bool>? loading = null)
    {
        _current?.Cancel();
        var source = _current = new CancellationTokenSource();
        return Task = RunCoreAsync(load, source, failed, loading);
    }

    public void Cancel()
    {
        _current?.Cancel();
        _current = null;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> load, CancellationTokenSource source, Action<Exception> failed, Action<bool>? loading)
    {
        loading?.Invoke(true);
        try { await load(source.Token); }
        catch (Exception ex)
        {
            // A cancelled load's failure is the cancellation's, whatever form it took.
            if (!source.IsCancellationRequested && ex is not OperationCanceledException) failed(ex);
        }
        finally
        {
            if (ReferenceEquals(_current, source)) { _current = null; loading?.Invoke(false); }
            else if (_current is null) loading?.Invoke(false);
            source.Dispose();
        }
    }
}
