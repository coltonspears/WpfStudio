using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.ViewModels;

/// <summary>The pane: its three parts, the one status line they share, and the lifetime that ends their database work.</summary>
public partial class DatabasePaneViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _databaseLifetime = new();
    private bool _disposed;
    private bool _initialized;
    public DatabasePaneViewModel(IDatabaseService database, IConnectionProfileStore profiles, IFileDialogService files, IUserDialogService dialogs, IQueryRecoveryStore recovery)
    {
        Connections = new ConnectionProfilesViewModel(profiles, Report);
        Queries = new QueryDocumentsViewModel(database, Connections, files, dialogs, recovery, _databaseLifetime.Token, Report);
        Explorer = new SchemaExplorerViewModel(database, Connections, Queries, _databaseLifetime.Token, Report);
    }
    public ConnectionProfilesViewModel Connections { get; }
    public QueryDocumentsViewModel Queries { get; }
    public SchemaExplorerViewModel Explorer { get; }
    [ObservableProperty] public partial string Status { get; set; } = "Connect to browse a server; use Execute to run SQL.";
    private void Report(string status) => Status = status;

    [RelayCommand]
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        _initialized = true;
        await Connections.LoadAsync(cancellationToken);
        await Queries.RecoverAsync(cancellationToken);
    }

    public Task<bool> ConfirmCloseAllAsync() => Queries.ConfirmCloseAllAsync();
    /// <summary>Call only after all application-wide close decisions have succeeded.</summary>
    public Task CompleteCloseAsync() => Queries.CompleteCloseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _databaseLifetime.Cancel();
        var operations = new List<Task>();
        foreach (var command in new IAsyncRelayCommand[] { Queries.ExecuteCommand, Explorer.ConnectCommand, Explorer.OpenDefinitionCommand })
        {
            command.Cancel();
            if (command.ExecutionTask is { IsCompleted: false } task) operations.Add(task);
        }
        foreach (var node in Explorer.Schema) operations.AddRange(node.CancelPendingLoads());
        try { await Queries.FlushRecoveryAsync(); }
        finally
        {
            Queries.StopRecovery();
            try { await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Status = "SQL shutdown: " + exception.Message; }
            _databaseLifetime.Dispose();
        }
    }
}
