using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.IO;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.ViewModels;

/// <summary>The open SQL scripts: running them, saving them, and keeping recovery copies of their unsaved text.</summary>
public partial class QueryDocumentsViewModel : ObservableObject
{
    private readonly IDatabaseService _database;
    private readonly ConnectionProfilesViewModel _connections;
    private readonly IFileDialogService _files;
    private readonly IUserDialogService _dialogs;
    private readonly IQueryRecoveryStore _recovery;
    private readonly CancellationToken _lifetime;
    private readonly Action<string> _report;
    private readonly HashSet<QueryDocumentViewModel> _observedDocuments = [];
    private CancellationTokenSource? _recoveryDelay;
    private Task _pendingRecovery = Task.CompletedTask;
    private bool _recoveryReady;
    private bool _recoveryClosed;
    private int _queryNumber;
    public QueryDocumentsViewModel(IDatabaseService database, ConnectionProfilesViewModel connections, IFileDialogService files, IUserDialogService dialogs, IQueryRecoveryStore recovery, CancellationToken lifetime, Action<string> report)
    {
        _database = database; _connections = connections; _files = files; _dialogs = dialogs; _recovery = recovery; _lifetime = lifetime; _report = report;
        Documents.CollectionChanged += DocumentsChanged;
        NewQuery();
    }
    public ObservableCollection<QueryDocumentViewModel> Documents { get; } = [];
    [ObservableProperty] public partial QueryDocumentViewModel? SelectedDocument { get; set; }

    public void Open(QueryDocumentViewModel document)
    {
        Documents.Add(document); SelectedDocument = document;
    }

    /// <summary>Offers the previous session's unsaved scripts, and starts keeping recovery copies from here on.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var documents = await _recovery.LoadAsync(cancellationToken);
            if (documents.Count > 0)
            {
                if (await _dialogs.ConfirmAsync("Recover SQL scripts", $"Recover {documents.Count} unsaved SQL script(s) from the previous session? Choose No to discard their recovery copies."))
                {
                    if (Documents.Count == 1 && !Documents[0].IsDirty && Documents[0].FilePath is null) Documents.Clear();
                    foreach (var saved in documents)
                        Open(new QueryDocumentViewModel(saved.Title, saved.SavedText) { RecoveryId = saved.Id, FilePath = saved.FilePath, SqlText = saved.Text });
                    _report($"Recovered {documents.Count} SQL script(s). Save them to keep your changes.");
                }
                else _report("SQL recovery copies discarded.");
            }
            _recoveryReady = true;
            await FlushRecoveryAsync();
        }
        catch (Exception exception) { _report("Could not restore SQL scripts: " + exception.Message); }
    }

    private void DocumentsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var removed in _observedDocuments.Where(document => !Documents.Contains(document)).ToArray())
        {
            removed.PropertyChanged -= DocumentChanged; _observedDocuments.Remove(removed);
        }
        foreach (var document in Documents)
            if (_observedDocuments.Add(document)) document.PropertyChanged += DocumentChanged;
        ScheduleRecovery();
    }
    private void DocumentChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(QueryDocumentViewModel.SqlText)) ScheduleRecovery();
    }
    private void ScheduleRecovery()
    {
        if (!_recoveryReady || _recoveryClosed) return;
        _recoveryDelay?.Cancel(); _recoveryDelay?.Dispose();
        _recoveryDelay = new CancellationTokenSource();
        _pendingRecovery = PersistAfterDelayAsync(_recoveryDelay.Token);
    }
    private async Task PersistAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(750, token);
            if (!_recoveryClosed) await _recovery.SaveAsync(RecoverySnapshot(), token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _report("Could not save SQL recovery: " + exception.Message); }
    }
    private QueryRecoveryDocument[] RecoverySnapshot() => Documents.Where(document => document.IsDirty)
        .Select(document => new QueryRecoveryDocument(document.RecoveryId, document.Title, document.SqlText, document.SavedText, document.FilePath)).ToArray();

    public async Task FlushRecoveryAsync()
    {
        if (!_recoveryReady || _recoveryClosed) return;
        _recoveryDelay?.Cancel();
        await _pendingRecovery;
        await _recovery.SaveAsync(RecoverySnapshot());
    }

    /// <summary>Call only after all application-wide close decisions have succeeded.</summary>
    public async Task CompleteCloseAsync()
    {
        _recoveryClosed = true;
        _recoveryDelay?.Cancel();
        await _pendingRecovery;
        try { if (_recoveryReady) await _recovery.SaveAsync([]); }
        catch { _recoveryClosed = false; ScheduleRecovery(); throw; }
    }

    /// <summary>Stops watching the documents for good, for disposal.</summary>
    public void StopRecovery()
    {
        _recoveryClosed = true;
        _recoveryDelay?.Cancel(); _recoveryDelay?.Dispose(); _recoveryDelay = null;
        Documents.CollectionChanged -= DocumentsChanged;
        foreach (var document in _observedDocuments) document.PropertyChanged -= DocumentChanged;
        _observedDocuments.Clear();
    }

    [RelayCommand]
    private void NewQuery() => Open(new QueryDocumentViewModel($"Query {++_queryNumber}", "-- Select text to execute only the selection.\nSELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName;\n"));

    [RelayCommand]
    private async Task CloseQueryAsync()
    {
        if (SelectedDocument is null || SelectedDocument.IsBusy) return;
        QueryDocumentViewModel document = SelectedDocument;
        if (!await ConfirmCloseAsync(document)) return;
        Documents.Remove(document); SelectedDocument = Documents.LastOrDefault();
        if (Documents.Count == 0) NewQuery();
        await FlushRecoveryAsync();
    }

    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (QueryDocumentViewModel document in Documents.ToArray())
            if (!await ConfirmCloseAsync(document)) return false;
        ExecuteCommand.Cancel();
        return true;
    }

    private async Task<bool> ConfirmCloseAsync(QueryDocumentViewModel document)
    {
        if (!document.IsDirty) return true;
        SaveDecision decision = await _dialogs.AskSaveAsync(document.Title);
        if (decision == SaveDecision.Cancel) return false;
        return decision != SaveDecision.Save || await SaveDocumentAsync(document, CancellationToken.None);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = lifetime.Token;
        QueryDocumentViewModel? document = SelectedDocument;
        if (document is null) return;
        ConnectionProfile profile = _connections.CurrentProfile();
        string sql = string.IsNullOrWhiteSpace(document.SelectedText) ? document.SqlText : document.SelectedText;
        int line = string.IsNullOrWhiteSpace(document.SelectedText) ? 1 : document.SelectionStartLine;
        if (string.IsNullOrWhiteSpace(sql)) { document.Status = "Enter SQL to execute."; return; }
        try
        {
            document.IsBusy = true; document.Status = "Executing…"; document.Target = $"{profile.Server} / {profile.Database}";
            document.Results.Clear(); document.Messages = "";
            // Includes row decoding: keep all result processing off the dispatcher thread.
            QueryExecutionResult result = await Task.Run(() => _database.ExecuteAsync(profile, sql, line, cancellationToken), cancellationToken);
            foreach (QueryResultSet table in result.Results) document.Results.Add(table);
            document.SelectedResult = document.Results.FirstOrDefault();
            document.Messages = string.Join(Environment.NewLine, result.Messages);
            document.Status = $"{(result.WasCancelled ? "Cancelled" : result.Messages.Any(message => message.IsError) ? "Failed" : "Completed")} in {result.Elapsed.TotalSeconds:N2}s · {result.Results.Count} result set(s)" + (result.DisplayTruncated ? " · display truncated (see Messages)" : "");
        }
        catch (OperationCanceledException) { document.Status = "Cancelled"; }
        catch (Exception exception) { document.Status = "Failed"; document.Messages = exception.Message; }
        finally { document.IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportCsvAsync(CancellationToken cancellationToken)
    {
        QueryResultSet? result = SelectedDocument?.SelectedResult;
        if (result is null) { _report("Select a result set to export."); return; }
        string? path = await _files.SaveFileAsync("Export displayed SQL results", "CSV files|*.csv", result.Name + ".csv");
        if (path is null) return;
        try { await CsvExporter.WriteAsync(result, path, cancellationToken); _report("Displayed rows exported to " + path); }
        catch (Exception exception) { _report(exception.Message); }
    }

    [RelayCommand]
    private async Task OpenScriptAsync(CancellationToken cancellationToken)
    {
        string? path = await _files.OpenFileAsync("Open SQL script", "SQL files|*.sql|All files|*.*");
        if (path is null) return;
        try { Open(new QueryDocumentViewModel(Path.GetFileName(path), await File.ReadAllTextAsync(path, cancellationToken)) { FilePath = path }); }
        catch (Exception exception) { _report(exception.Message); }
    }

    [RelayCommand]
    private async Task SaveScriptAsync(CancellationToken cancellationToken)
    {
        if (SelectedDocument is null) return;
        await SaveDocumentAsync(SelectedDocument, cancellationToken);
    }

    private async Task<bool> SaveDocumentAsync(QueryDocumentViewModel document, CancellationToken cancellationToken)
    {
        string text = document.SqlText;
        string? path = await _files.SaveFileAsync("Save SQL script", "SQL files|*.sql", document.FilePath ?? document.Title + (document.Title.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? "" : ".sql"));
        if (path is null) return false;
        try
        {
            if (document.FilePath is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase)
                && await Task.Run(() => File.Exists(path), cancellationToken) && await File.ReadAllTextAsync(path, cancellationToken) != document.SavedText)
            {
                if (!await _dialogs.ConfirmAsync("SQL script changed outside WpfStudio", $"Overwrite the external changes to {Path.GetFileName(path)} with the editor content?"))
                {
                    _report("Save cancelled: the SQL script has external changes.");
                    return false;
                }
            }
            path = Path.GetFullPath(path);
            await AtomicFile.WriteAsync(path, new System.Text.UTF8Encoding(false).GetBytes(text), cancellationToken, writeThrough: true);
            document.MarkSaved(text);
            document.FilePath = path;
            await FlushRecoveryAsync();
            _report(document.IsDirty ? "The SQL script changed while saving. Save again before closing it." : "Script saved to " + path);
            return !document.IsDirty;
        }
        catch (Exception exception) { _report(exception.Message); return false; }
    }
}
