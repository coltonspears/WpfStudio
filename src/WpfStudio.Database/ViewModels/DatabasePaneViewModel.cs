using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.ViewModels;

public partial class DatabasePaneViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDatabaseService _database;
    private readonly IConnectionProfileStore _profiles;
    private readonly IFileDialogService _files;
    private readonly IUserDialogService? _dialogs;
    private readonly IQueryRecoveryStore? _recovery;
    private readonly CancellationTokenSource _databaseLifetime = new();
    private readonly HashSet<QueryDocumentViewModel> _observedDocuments = [];
    private CancellationTokenSource? _recoveryDelay;
    private Task _pendingRecovery = Task.CompletedTask;
    private bool _recoveryReady;
    private bool _recoveryClosed;
    private bool _disposed;
    private bool _initialized;
    private int _queryNumber;
    private ConnectionProfile? _schemaProfile;
    public DatabasePaneViewModel(IDatabaseService database, IConnectionProfileStore profiles, IFileDialogService files, IUserDialogService? dialogs = null, IQueryRecoveryStore? recovery = null)
    {
        _database = database; _profiles = profiles; _files = files; _dialogs = dialogs; _recovery = recovery;
        Documents.CollectionChanged += DocumentsChanged;
        NewQuery();
    }
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<SchemaNodeViewModel> Schema { get; } = [];
    public ObservableCollection<QueryDocumentViewModel> Documents { get; } = [];
    public ObservableCollection<string> CompletionItems { get; } = [];
    [ObservableProperty] public partial ConnectionProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial QueryDocumentViewModel? SelectedDocument { get; set; }
    [ObservableProperty] public partial SchemaNodeViewModel? SelectedSchema { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "Local SQL Server";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveTarget))] public partial string Server { get; set; } = "localhost";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveTarget))] public partial string DatabaseName { get; set; } = "master";
    [ObservableProperty] public partial bool WindowsAuthentication { get; set; } = true;
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool RememberPassword { get; set; }
    [ObservableProperty] public partial bool TrustServerCertificate { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Connect to browse a server; use Execute to run SQL.";
    [ObservableProperty] public partial bool IsConnected { get; set; }
    public string ActiveTarget => $"{Server}  /  {DatabaseName}";

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is null) return;
        ProfileName = value.Name; Server = value.Server; DatabaseName = value.Database; WindowsAuthentication = value.WindowsAuthentication;
        UserName = value.UserName; Password = value.Password; RememberPassword = value.RememberPassword; TrustServerCertificate = value.TrustServerCertificate;
        Schema.Clear(); CompletionItems.Clear(); IsConnected = false;
    }

    [RelayCommand]
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            foreach (ConnectionProfile profile in await _profiles.LoadAsync(cancellationToken)) Profiles.Add(profile);
            SelectedProfile = Profiles.FirstOrDefault();
        }
        catch (Exception exception) { Status = "Could not load saved connections: " + exception.Message; }
        if (_recovery is null) return;
        try
        {
            var documents = await _recovery.LoadAsync(cancellationToken);
            if (documents.Count > 0)
            {
                if (_dialogs is null) { Status = "SQL recovery data is available; a recovery dialog service is required to restore it."; return; }
                if (await _dialogs.ConfirmAsync("Recover SQL scripts", $"Recover {documents.Count} unsaved SQL script(s) from the previous session? Choose No to discard their recovery copies."))
                {
                    if (Documents.Count == 1 && !Documents[0].IsDirty && Documents[0].FilePath is null) Documents.Clear();
                    foreach (var saved in documents)
                    {
                        var document = new QueryDocumentViewModel(saved.Title, saved.SavedText) { RecoveryId = saved.Id, FilePath = saved.FilePath, SqlText = saved.Text };
                        Documents.Add(document); SelectedDocument = document;
                    }
                    Status = $"Recovered {documents.Count} SQL script(s). Save them to keep your changes.";
                }
                else Status = "SQL recovery copies discarded.";
            }
            _recoveryReady = true;
            await FlushRecoveryAsync();
        }
        catch (Exception exception) { Status = "Could not restore SQL scripts: " + exception.Message; }
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
        if (!_recoveryReady || _recoveryClosed || _recovery is null) return;
        _recoveryDelay?.Cancel(); _recoveryDelay?.Dispose();
        _recoveryDelay = new CancellationTokenSource();
        _pendingRecovery = PersistAfterDelayAsync(_recoveryDelay.Token);
    }
    private async Task PersistAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(750, token);
            if (!_recoveryClosed) await _recovery!.SaveAsync(RecoverySnapshot(), token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = "Could not save SQL recovery: " + exception.Message; }
    }
    private QueryRecoveryDocument[] RecoverySnapshot() => Documents.Where(document => document.IsDirty)
        .Select(document => new QueryRecoveryDocument(document.RecoveryId, document.Title, document.SqlText, document.SavedText, document.FilePath)).ToArray();

    public async Task FlushRecoveryAsync()
    {
        if (!_recoveryReady || _recoveryClosed || _recovery is null) return;
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
        try { if (_recoveryReady && _recovery is not null) await _recovery.SaveAsync([]); }
        catch { _recoveryClosed = false; ScheduleRecovery(); throw; }
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null; ProfileName = "New connection"; Server = "localhost"; DatabaseName = "master";
        WindowsAuthentication = true; UserName = ""; Password = ""; RememberPassword = false; TrustServerCertificate = false;
        Schema.Clear(); CompletionItems.Clear(); IsConnected = false;
    }

    [RelayCommand]
    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConnectionProfile profile = CurrentProfile();
            if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Enter a connection name.");
            _ = SqlDatabaseService.BuildConnectionString(profile);
            List<ConnectionProfile> updated = Profiles.Where(item => item.Id != profile.Id).Append(profile).ToList();
            await _profiles.SaveAsync(updated, cancellationToken);
            Profiles.Clear(); foreach (ConnectionProfile item in updated) Profiles.Add(item);
            SelectedProfile = profile; Status = "Connection saved. Password is protected for this Windows user when Remember is enabled.";
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null) return;
        try
        {
            var remaining = Profiles.Where(profile => profile.Id != SelectedProfile.Id).ToList();
            await _profiles.SaveAsync(remaining, cancellationToken);
            Profiles.Clear(); foreach (ConnectionProfile profile in remaining) Profiles.Add(profile);
            SelectedProfile = remaining.FirstOrDefault(); Status = "Saved connection removed.";
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _databaseLifetime.Token);
        cancellationToken = lifetime.Token;
        try
        {
            Status = "Loading databases…";
            ConnectionProfile profile = CurrentProfile();
            IReadOnlyList<SchemaItem> databases = await _database.GetDatabasesAsync(profile, cancellationToken);
            _schemaProfile = profile;
            Schema.Clear(); CompletionItems.Clear();
            // Capture credentials/server for this tree, even when the edit form changes later.
            foreach (SchemaItem item in databases)
                Schema.Add(new SchemaNodeViewModel(item, async (parent, token) =>
                {
                    using var loadLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _databaseLifetime.Token);
                    var children = await _database.GetChildrenAsync(profile, parent, loadLifetime.Token);
                    loadLifetime.Token.ThrowIfCancellationRequested();
                    foreach (SchemaItem child in children.Where(child => child.Kind is SchemaNodeKind.Table or SchemaNodeKind.View or SchemaNodeKind.Procedure or SchemaNodeKind.Column))
                        if (!CompletionItems.Contains(child.QualifiedName)) CompletionItems.Add(child.QualifiedName);
                    return children;
                }));
            IsConnected = true; Status = $"{databases.Count} databases available. Expand to browse.";
        }
        catch (OperationCanceledException) { Status = "Connection cancelled."; }
        catch (Exception exception) { IsConnected = false; Status = exception.Message; }
    }

    [RelayCommand]
    private void UseDatabase()
    {
        if (SelectedSchema is null) return;
        ApplySchemaConnection();
        DatabaseName = SelectedSchema.Item.Database;
        Status = "Query target changed to " + ActiveTarget;
    }

    [RelayCommand]
    private void NewQuery()
    {
        var document = new QueryDocumentViewModel($"Query {++_queryNumber}", "-- Select text to execute only the selection.\nSELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName;\n");
        Documents.Add(document); SelectedDocument = document;
    }

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
        if (_dialogs is null) { Status = "Save the modified query before closing it."; return false; }
        SaveDecision decision = await _dialogs.AskSaveAsync(document.Title);
        if (decision == SaveDecision.Cancel) return false;
        return decision != SaveDecision.Save || await SaveDocumentAsync(document, CancellationToken.None);
    }

    [RelayCommand]
    private async Task OpenDefinitionAsync(CancellationToken cancellationToken)
    {
        if (SelectedSchema?.Item.HasDefinition != true) { Status = "Select a table, view, or stored procedure."; return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _databaseLifetime.Token);
        cancellationToken = lifetime.Token;
        try
        {
            SchemaItem item = SelectedSchema.Item;
            string definition = await _database.GetDefinitionAsync(_schemaProfile ?? CurrentProfile(), item, cancellationToken);
            var document = new QueryDocumentViewModel(item.Name + ".sql", definition);
            Documents.Add(document); SelectedDocument = document; ApplySchemaConnection(); DatabaseName = item.Database;
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _databaseLifetime.Token);
        cancellationToken = lifetime.Token;
        QueryDocumentViewModel? document = SelectedDocument;
        if (document is null) return;
        ConnectionProfile profile = CurrentProfile();
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
        if (result is null) { Status = "Select a result set to export."; return; }
        string? path = await _files.SaveFileAsync("Export displayed SQL results", "CSV files|*.csv", result.Name + ".csv");
        if (path is null) return;
        try { await CsvExporter.WriteAsync(result, path, cancellationToken); Status = "Displayed rows exported to " + path; }
        catch (Exception exception) { Status = exception.Message; }
    }

    [RelayCommand]
    private async Task OpenScriptAsync(CancellationToken cancellationToken)
    {
        string? path = await _files.OpenFileAsync("Open SQL script", "SQL files|*.sql|All files|*.*");
        if (path is null) return;
        try
        {
            var document = new QueryDocumentViewModel(Path.GetFileName(path), await File.ReadAllTextAsync(path, cancellationToken)) { FilePath = path };
            Documents.Add(document); SelectedDocument = document;
        }
        catch (Exception exception) { Status = exception.Message; }
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
        string? temporary = null;
        try
        {
            if (document.FilePath is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken) != document.SavedText)
            {
                if (_dialogs is null || !await _dialogs.ConfirmAsync("SQL script changed outside WpfStudio", $"Overwrite the external changes to {Path.GetFileName(path)} with the editor content?"))
                {
                    Status = "Save cancelled: the SQL script has external changes.";
                    return false;
                }
            }
            path = Path.GetFullPath(path);
            temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            document.MarkSaved(text);
            document.FilePath = path;
            await FlushRecoveryAsync();
            Status = document.IsDirty ? "The SQL script changed while saving. Save again before closing it." : "Script saved to " + path;
            return !document.IsDirty;
        }
        catch (Exception exception) { Status = exception.Message; return false; }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }

    private ConnectionProfile CurrentProfile() => new()
    {
        Id = SelectedProfile?.Id ?? Guid.NewGuid(), Name = ProfileName, Server = Server, Database = DatabaseName,
        WindowsAuthentication = WindowsAuthentication, UserName = UserName, Password = Password,
        RememberPassword = RememberPassword, TrustServerCertificate = TrustServerCertificate
    };

    private void ApplySchemaConnection()
    {
        if (_schemaProfile is null) return;
        Server = _schemaProfile.Server; WindowsAuthentication = _schemaProfile.WindowsAuthentication;
        UserName = _schemaProfile.UserName; Password = _schemaProfile.Password; TrustServerCertificate = _schemaProfile.TrustServerCertificate;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _databaseLifetime.Cancel();
        var operations = new List<Task>();
        foreach (var command in new IAsyncRelayCommand[] { ExecuteCommand, ConnectCommand, OpenDefinitionCommand })
        {
            command.Cancel();
            if (command.ExecutionTask is { IsCompleted: false } task) operations.Add(task);
        }
        foreach (var node in Schema) operations.AddRange(node.CancelPendingLoads());
        try { await FlushRecoveryAsync(); }
        finally
        {
            _recoveryClosed = true;
            _recoveryDelay?.Cancel(); _recoveryDelay?.Dispose(); _recoveryDelay = null;
            Documents.CollectionChanged -= DocumentsChanged;
            foreach (var document in _observedDocuments) document.PropertyChanged -= DocumentChanged;
            _observedDocuments.Clear();
            try { await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Status = "SQL shutdown: " + exception.Message; }
            _databaseLifetime.Dispose();
        }
    }
}
