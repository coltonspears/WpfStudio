using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.ViewModels;

/// <summary>The object tree of the connected server, and the completion words it feeds to the SQL editor.</summary>
public partial class SchemaExplorerViewModel : ObservableObject
{
    private readonly IDatabaseService _database;
    private readonly ConnectionProfilesViewModel _connections;
    private readonly QueryDocumentsViewModel _queries;
    private readonly CancellationToken _lifetime;
    private readonly Action<string> _report;
    private ConnectionProfile? _schemaProfile;
    public SchemaExplorerViewModel(IDatabaseService database, ConnectionProfilesViewModel connections, QueryDocumentsViewModel queries, CancellationToken lifetime, Action<string> report)
    {
        _database = database; _connections = connections; _queries = queries; _lifetime = lifetime; _report = report;
        _connections.ConnectionChanged += OnConnectionChanged;
    }
    public ObservableCollection<SchemaNodeViewModel> Schema { get; } = [];
    public ObservableCollection<string> CompletionItems { get; } = [];
    // What CompletionItems holds, for a constant-time test: searching the list for each child made expanding a node with
    // thousands of tables or procedures quadratic, on the UI thread.
    private readonly HashSet<string> _completionTexts = new(StringComparer.Ordinal);
    [ObservableProperty] public partial SchemaNodeViewModel? SelectedSchema { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }

    private void OnConnectionChanged(ConnectionProfile? profile)
    {
        // Saving the connected profile re-selects its new record; the schema tree loaded from that same connection stays.
        if (profile is not null && _schemaProfile is { } loaded && SameConnection(loaded, profile)) return;
        Schema.Clear(); CompletionItems.Clear(); _completionTexts.Clear(); IsConnected = false; _schemaProfile = null;
    }
    private static bool SameConnection(ConnectionProfile left, ConnectionProfile right) =>
        string.Equals(left.Server, right.Server, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Database, right.Database, StringComparison.OrdinalIgnoreCase)
        && left.WindowsAuthentication == right.WindowsAuthentication
        && (left.WindowsAuthentication || string.Equals(left.UserName, right.UserName, StringComparison.Ordinal))
        && left.TrustServerCertificate == right.TrustServerCertificate;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = lifetime.Token;
        try
        {
            _report("Loading databases…");
            ConnectionProfile profile = _connections.CurrentProfile();
            IReadOnlyList<SchemaItem> databases = await _database.GetDatabasesAsync(profile, cancellationToken);
            _schemaProfile = profile;
            Schema.Clear(); CompletionItems.Clear(); _completionTexts.Clear();
            // Capture credentials/server for this tree, even when the edit form changes later.
            foreach (SchemaItem item in databases)
                Schema.Add(new SchemaNodeViewModel(item, async (parent, token) =>
                {
                    using var loadLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
                    var children = await _database.GetChildrenAsync(profile, parent, loadLifetime.Token);
                    loadLifetime.Token.ThrowIfCancellationRequested();
                    foreach (SchemaItem child in children.Where(child => child.Kind is SchemaNodeKind.Table or SchemaNodeKind.View or SchemaNodeKind.Procedure or SchemaNodeKind.Column))
                        if (_completionTexts.Add(child.CompletionText)) CompletionItems.Add(child.CompletionText);
                    return children;
                }));
            IsConnected = true; _report($"{databases.Count} databases available. Expand to browse.");
        }
        catch (OperationCanceledException) { _report("Connection cancelled."); }
        catch (Exception exception) { IsConnected = false; _report(exception.Message); }
    }

    [RelayCommand]
    private void UseDatabase()
    {
        if (SelectedSchema is null) return;
        if (_schemaProfile is { } connected) _connections.UseServerOf(connected);
        _connections.DatabaseName = SelectedSchema.Item.Database;
        _report("Query target changed to " + _connections.ActiveTarget);
    }

    [RelayCommand]
    private async Task OpenDefinitionAsync(CancellationToken cancellationToken)
    {
        if (SelectedSchema?.Item.HasDefinition != true) { _report("Select a table, view, or stored procedure."); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = lifetime.Token;
        try
        {
            SchemaItem item = SelectedSchema.Item;
            string definition = await _database.GetDefinitionAsync(_schemaProfile ?? _connections.CurrentProfile(), item, cancellationToken);
            _queries.Open(new QueryDocumentViewModel(item.Name + ".sql", definition));
            if (_schemaProfile is { } connected) _connections.UseServerOf(connected);
            _connections.DatabaseName = item.Database;
        }
        catch (Exception exception) { _report(exception.Message); }
    }
}
