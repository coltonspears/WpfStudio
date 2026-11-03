using System.ComponentModel;
using System.IO;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.Features.Documents;

/// <summary>What timed saving needs from the shell: the open editors and the state that a save touches.</summary>
public interface IAutoSaveHost
{
    IReadOnlyList<EditorViewModel> Documents { get; }
    /// <summary>A change review is open; saving now could write text the user is still deciding about.</summary>
    bool IsReviewOpen { get; }
    void ReportStatus(string status);
    void AppendOutput(string line);
    /// <summary>The timer wrote this document to disk.</summary>
    void DocumentAutoSaved(EditorViewModel document);
    /// <summary>A pass wrote at least one document, so analysis that reads disk should run again.</summary>
    void AutoSaveCompleted();
    /// <summary>A save found the file changed on disk since it was loaded; the external-changes review should look.</summary>
    void ExternalChangeDetected();
}

/// <summary>
/// Saves edited files after a pause in typing. Files that changed on disk, were never saved, are read-only or are
/// part of an open change review are left for an explicit save. Whitespace is not tidied, so text you are typing
/// does not move.
/// </summary>
public sealed class AutoSaveCoordinator : IDisposable
{
    private readonly IAutoSaveHost _host;
    private readonly DocumentStore _store;
    private readonly EditorPreferences _preferences;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _timer;
    private int _suspensions;
    private bool _stopped, _disposed;
    private Task _run = Task.CompletedTask;

    public AutoSaveCoordinator(IAutoSaveHost host, DocumentStore store, EditorPreferences preferences, IUiDispatcher dispatcher)
    {
        _host = host; _store = store; _preferences = preferences; _dispatcher = dispatcher;
        _preferences.PropertyChanged += PreferencesChanged;
    }

    /// <summary>Automatic saving starts once the shell has loaded its settings and reopened its documents.</summary>
    public bool IsEnabled { get; set; }
    /// <summary>True while a suspension is in force or after <see cref="Stop"/>.</summary>
    public bool IsHeld => _suspensions > 0 || _stopped;

    private void PreferencesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(EditorPreferences.AutoSave) or nameof(EditorPreferences.AutoSaveDelaySeconds)) Schedule();
    }

    /// <summary>Restarts the pause; called on every edit.</summary>
    public void Schedule()
    {
        _timer?.Cancel(); _timer?.Dispose(); _timer = null;
        if (!_preferences.AutoSave || !IsEnabled || IsHeld || _disposed) return;
        var pending = _timer = new();
        Task.Delay(TimeSpan.FromSeconds(_preferences.AutoSaveDelaySeconds), pending.Token).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (!pending.IsCancellationRequested && !_disposed) _run = TimedSaveAsync();
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Forget();
    }

    /// <summary>
    /// Holds automatic saving while documents close. A save-or-discard prompt is modal but keeps the dispatcher
    /// running, so a timed save could otherwise write edits the user is about to discard. A save that is already
    /// writing finishes before the question is asked, so its document is then simply clean.
    /// </summary>
    public async Task<IDisposable> SuspendAsync()
    {
        _suspensions++;
        _timer?.Cancel();
        try { await _run; }
        catch (Exception exception) { _host.AppendOutput("Automatic save: " + exception.Message); }
        return new Suspension(this);
    }

    private sealed class Suspension(AutoSaveCoordinator owner) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (--owner._suspensions == 0) owner.Schedule();
        }
    }

    /// <summary>Ends automatic saving for good: documents whose edits were discarded on close must never be saved afterwards.</summary>
    public void Stop() => _stopped = true;

    /// <summary>Runs the timed save now, as the timer would.</summary>
    public Task RunNowAsync() => _run = SaveAsync();

    /// <summary>Nothing awaits a timed save unless a close happens to come along, so its failures are reported here.</summary>
    private async Task TimedSaveAsync()
    {
        try { await SaveAsync(); }
        catch (Exception exception) { _host.AppendOutput("Automatic save: " + exception.Message); }
    }

    private async Task SaveAsync()
    {
        if (IsHeld) return;
        if (_host.IsReviewOpen) { Schedule(); return; }
        var saved = 0;
        foreach (var document in _host.Documents.Where(document => document.State.IsDirty && document.State.DiskHash is not null && !document.IsReadOnly
                     && !document.State.HasExternalChange && File.Exists(document.State.Path)).ToArray())
        {
            if (IsHeld || !_host.Documents.Contains(document)) break;
            try
            {
                await _store.SaveAsync(document.State);
                _host.DocumentAutoSaved(document);
                saved++;
            }
            catch (ExternalFileChangedException) { _host.ExternalChangeDetected(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _host.AppendOutput("Automatic save: " + exception.Message); }
        }
        if (saved == 0) return;
        _host.AutoSaveCompleted();
        _host.ReportStatus(saved == 1 ? "Saved automatically" : $"Saved {saved} files automatically");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preferences.PropertyChanged -= PreferencesChanged;
        _timer?.Cancel(); _timer?.Dispose(); _timer = null;
    }
}
