using WpfStudio.Contracts;
using WpfStudio.Database.Models;
using WpfStudio.Database.ViewModels;

namespace WpfStudio.Database.Tests;

public sealed class DatabaseViewModelTests
{
    [Fact]
    public async Task DisposalCancelsAndAwaitsOwnedQueriesConnectionsAndSchemaLoads()
    {
        var service = new BlockingDatabase();
        var model = new DatabasePaneViewModel(service, new FakeStore(), new FakeDialogs());
        Task execute = model.ExecuteCommand.ExecuteAsync(null);
        Task connect = model.ConnectCommand.ExecuteAsync(null);
        bool schemaCancelled = false;
        var node = new SchemaNodeViewModel(new SchemaItem("master", SchemaNodeKind.Database, "master"), async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return []; }
            finally { schemaCancelled = token.IsCancellationRequested; }
        });
        model.Schema.Add(node);
        Task schema = node.LoadCommand.ExecuteAsync(null);
        await service.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await model.DisposeAsync();

        Assert.True(execute.IsCompleted);
        Assert.True(connect.IsCompleted);
        Assert.True(schema.IsCompleted);
        Assert.Equal(2, service.CancelledOperations);
        Assert.True(schemaCancelled);
        Assert.False(model.SelectedDocument!.IsBusy);
    }

    [Fact]
    public async Task SaveCannotCloseQueryWhenContentChangesDuringSave()
    {
        string path = Path.Combine(Path.GetTempPath(), "WpfStudio-query-save-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            var dialogs = new SaveDialogs(path);
            var model = new DatabasePaneViewModel(new FakeDatabase(), new FakeStore(), dialogs, dialogs);
            var document = model.SelectedDocument!;
            document.SqlText = "SELECT 1;";
            dialogs.BeforeSavePath = () => document.SqlText = "SELECT 2;";
            await model.CloseQueryCommand.ExecuteAsync(null);
            Assert.Contains(document, model.Documents);
            Assert.True(document.IsDirty);
            Assert.Equal("SELECT 2;", document.SqlText);
            Assert.Equal("SELECT 1;", await File.ReadAllTextAsync(path));
            Assert.Contains("changed while saving", model.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DecliningExternalScriptOverwritePreservesBothVersions()
    {
        string path = Path.Combine(Path.GetTempPath(), "WpfStudio-query-conflict-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            await File.WriteAllTextAsync(path, "SELECT 'external';");
            var dialogs = new SaveDialogs(path);
            var model = new DatabasePaneViewModel(new FakeDatabase(), new FakeStore(), dialogs, dialogs);
            var document = new QueryDocumentViewModel("script.sql", "SELECT 'original';") { FilePath = path };
            document.SqlText = "SELECT 'editor';";
            model.Documents.Add(document); model.SelectedDocument = document;
            await model.SaveScriptCommand.ExecuteAsync(null);
            Assert.Equal("SELECT 'external';", await File.ReadAllTextAsync(path));
            Assert.Equal("SELECT 'editor';", document.SqlText);
            Assert.True(document.IsDirty);
            Assert.Contains("external changes", model.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ClosingModifiedQueryWithoutDialogCannotDiscardWork()
    {
        var model = new DatabasePaneViewModel(new FakeDatabase(), new FakeStore(), new FakeDialogs());
        QueryDocumentViewModel document = model.SelectedDocument!;
        document.SqlText += "\nSELECT 42";
        await model.CloseQueryCommand.ExecuteAsync(null);
        Assert.Same(document, model.SelectedDocument);
        Assert.True(document.IsDirty);
        Assert.EndsWith(" *", document.DisplayTitle);
    }

    [Fact]
    public async Task InitializationDoesNotConnectOrExecuteSql()
    {
        var service = new FakeDatabase();
        var model = new DatabasePaneViewModel(service, new FakeStore(), new FakeDialogs());
        await model.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Executions);
        Assert.Equal(0, service.Connections);
        Assert.Single(model.Documents);
    }

    [Fact]
    public async Task ExecuteUsesSelectionAndItsOriginalLine()
    {
        var service = new FakeDatabase();
        var model = new DatabasePaneViewModel(service, new FakeStore(), new FakeDialogs());
        model.SelectedDocument!.SqlText = "SELECT 1;\nSELECT 2;";
        model.SelectedDocument.SelectedText = "SELECT 2;";
        model.SelectedDocument.SelectionStartLine = 2;
        await model.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal("SELECT 2;", service.Sql);
        Assert.Equal(2, service.StartLine);
        Assert.False(model.SelectedDocument.IsBusy);
    }

    [Fact]
    public async Task FailureIsVisibleAndResetsBusyState()
    {
        var service = new FakeDatabase { Fail = true };
        var model = new DatabasePaneViewModel(service, new FakeStore(), new FakeDialogs());
        await model.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal("Failed", model.SelectedDocument!.Status);
        Assert.Contains("test failure", model.SelectedDocument.Messages);
        Assert.False(model.SelectedDocument.IsBusy);
    }

    private sealed class FakeDatabase : IDatabaseService
    {
        public int Executions; public int Connections; public string? Sql; public int StartLine; public bool Fail;
        public Task<IReadOnlyList<SchemaItem>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken) { Connections++; return Task.FromResult<IReadOnlyList<SchemaItem>>([]); }
        public Task<IReadOnlyList<SchemaItem>> GetChildrenAsync(ConnectionProfile profile, SchemaItem parent, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SchemaItem>>([]);
        public Task<string> GetDefinitionAsync(ConnectionProfile profile, SchemaItem item, CancellationToken cancellationToken) => Task.FromResult("");
        public Task<QueryExecutionResult> ExecuteAsync(ConnectionProfile profile, string sql, int startLine, CancellationToken cancellationToken)
        {
            Executions++; Sql = sql; StartLine = startLine;
            return Fail ? Task.FromException<QueryExecutionResult>(new InvalidOperationException("test failure")) : Task.FromResult(new QueryExecutionResult([], [], TimeSpan.Zero));
        }
    }
    private sealed class BlockingDatabase : IDatabaseService
    {
        public TaskCompletionSource QueryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelledOperations;
        public async Task<IReadOnlyList<SchemaItem>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            ConnectStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return []; }
            finally { if (cancellationToken.IsCancellationRequested) Interlocked.Increment(ref CancelledOperations); }
        }
        public async Task<QueryExecutionResult> ExecuteAsync(ConnectionProfile profile, string sql, int startLine, CancellationToken cancellationToken)
        {
            QueryStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return new([], [], TimeSpan.Zero); }
            finally { if (cancellationToken.IsCancellationRequested) Interlocked.Increment(ref CancelledOperations); }
        }
        public Task<IReadOnlyList<SchemaItem>> GetChildrenAsync(ConnectionProfile profile, SchemaItem parent, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SchemaItem>>([]);
        public Task<string> GetDefinitionAsync(ConnectionProfile profile, SchemaItem item, CancellationToken cancellationToken) => Task.FromResult("");
    }
    private sealed class FakeStore : IConnectionProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FakeDialogs : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
    }
    private sealed class SaveDialogs(string path) : IFileDialogService, IUserDialogService
    {
        public Action? BeforeSavePath { get; set; }
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) { BeforeSavePath?.Invoke(); return Task.FromResult<string?>(path); }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(SaveDecision.Save);
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }
}
