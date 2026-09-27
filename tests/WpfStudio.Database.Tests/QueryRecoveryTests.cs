using WpfStudio.Contracts;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Database.ViewModels;

namespace WpfStudio.Database.Tests;

public sealed class QueryRecoveryTests
{
    [Fact]
    public async Task DebouncedRecoveryContainsLatestScriptButNoConnectionOrResultData()
    {
        await using var test = new RecoveryTestContext();
        var model = test.Create();
        await model.InitializeCommand.ExecuteAsync(null);
        model.Password = "password-must-not-be-recovered";
        model.Server = "server-must-not-be-recovered";
        model.SelectedDocument!.Messages = "result-must-not-be-recovered";
        model.SelectedDocument.SqlText = "SELECT 1;";
        model.SelectedDocument.SqlText = "SELECT 2; -- latest";

        IReadOnlyList<QueryRecoveryDocument> saved = [];
        for (int i = 0; i < 60 && saved.Count == 0; i++) { await Task.Delay(50); saved = await test.Store.LoadAsync(); }
        Assert.Equal("SELECT 2; -- latest", Assert.Single(saved).Text);
        string json = await File.ReadAllTextAsync(test.RecoveryPath);
        Assert.DoesNotContain("must-not-be-recovered", json);
        Assert.Empty(Directory.GetFiles(test.Root, "*.tmp"));
    }

    [Fact]
    public async Task RestoringUnsavedAndFileBackedScriptsKeepsDirtyStateAndConflictBaseline()
    {
        await using var test = new RecoveryTestContext();
        var first = test.Create();
        await first.InitializeCommand.ExecuteAsync(null);
        first.SelectedDocument!.SqlText = "SELECT 'unnamed changes';";
        string path = Path.Combine(test.Root, "script.sql");
        var opened = new QueryDocumentViewModel("script.sql", "SELECT 'original';") { FilePath = path, SqlText = "SELECT 'editor changes';" };
        first.Documents.Add(opened);
        await first.FlushRecoveryAsync();
        await first.DisposeAsync(); // No successful close commit: recovery must survive.

        await File.WriteAllTextAsync(path, "SELECT 'changed outside while stopped';");
        var dialogs = new RecoveryDialogs { Recover = true };
        var recovered = test.Create(dialogs);
        await recovered.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(2, recovered.Documents.Count);
        Assert.All(recovered.Documents, document => Assert.True(document.IsDirty));
        Assert.Contains(recovered.Documents, document => document.FilePath is null && document.SqlText.Contains("unnamed changes"));
        Assert.Equal(1, dialogs.RecoveryQuestions);
        await recovered.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.RecoveryQuestions);
        recovered.SelectedDocument = recovered.Documents.Single(document => document.FilePath == path);
        dialogs.SavePaths.Enqueue(path);
        await recovered.SaveScriptCommand.ExecuteAsync(null);
        Assert.Contains("changed outside while stopped", await File.ReadAllTextAsync(path));
        Assert.Contains("external changes", recovered.Status);
        Assert.Equal(0, test.Database.Executions);
    }

    [Fact]
    public async Task DecliningRecoveryDeletesCopiesWithoutConnectingOrExecutingSql()
    {
        await using var test = new RecoveryTestContext();
        await test.Store.SaveAsync([new(Guid.NewGuid(), "Unfinished", "DELETE FROM T;", "", null)]);
        var dialogs = new RecoveryDialogs { Recover = false };
        var model = test.Create(dialogs);
        await model.InitializeCommand.ExecuteAsync(null);
        Assert.Empty(await test.Store.LoadAsync());
        Assert.False(File.Exists(test.RecoveryPath));
        Assert.False(Assert.Single(model.Documents).IsDirty);
        Assert.Equal(1, dialogs.RecoveryQuestions);
        Assert.Equal(0, test.Database.Executions);
        Assert.Equal(0, test.Database.Connections);
    }

    [Fact]
    public async Task CancelledOrUncommittedShutdownPreservesRecoveryUntilCloseCommits()
    {
        await using var test = new RecoveryTestContext();
        var dialogs = new RecoveryDialogs();
        var model = test.Create(dialogs);
        await model.InitializeCommand.ExecuteAsync(null);
        model.SelectedDocument!.SqlText = "SELECT 1;";
        model.NewQueryCommand.Execute(null);
        model.SelectedDocument!.SqlText = "SELECT 2;";
        await model.FlushRecoveryAsync();
        dialogs.Decisions.Enqueue(SaveDecision.Discard);
        dialogs.Decisions.Enqueue(SaveDecision.Cancel);
        Assert.False(await model.ConfirmCloseAllAsync());
        Assert.Equal(2, (await test.Store.LoadAsync()).Count);

        dialogs.Decisions.Enqueue(SaveDecision.Discard);
        dialogs.Decisions.Enqueue(SaveDecision.Discard);
        Assert.True(await model.ConfirmCloseAllAsync());
        Assert.Equal(2, (await test.Store.LoadAsync()).Count); // Source tabs may still cancel app shutdown.
        await model.CompleteCloseAsync();
        await model.DisposeAsync();
        Assert.Empty(await test.Store.LoadAsync());
    }

    [Fact]
    public async Task SavingAndDiscardingIndividualTabsRemoveTheirRecoveryCopies()
    {
        await using var test = new RecoveryTestContext();
        var dialogs = new RecoveryDialogs();
        var model = test.Create(dialogs);
        await model.InitializeCommand.ExecuteAsync(null);
        var savedDocument = model.SelectedDocument!;
        savedDocument.SqlText = "SELECT 1;";
        model.NewQueryCommand.Execute(null);
        var discardedDocument = model.SelectedDocument!;
        discardedDocument.SqlText = "SELECT 2;";
        await model.FlushRecoveryAsync();
        model.SelectedDocument = savedDocument;
        dialogs.SavePaths.Enqueue(Path.Combine(test.Root, "saved.sql"));
        await model.SaveScriptCommand.ExecuteAsync(null);
        Assert.False(savedDocument.IsDirty);
        Assert.Equal(discardedDocument.RecoveryId, Assert.Single(await test.Store.LoadAsync()).Id);

        model.SelectedDocument = discardedDocument;
        dialogs.Decisions.Enqueue(SaveDecision.Discard);
        await model.CloseQueryCommand.ExecuteAsync(null);
        Assert.Empty(await test.Store.LoadAsync());
        Assert.False(File.Exists(test.RecoveryPath));
    }

    [Fact]
    public async Task CancelledAtomicWritePreservesLastCompleteRecoveryFile()
    {
        await using var test = new RecoveryTestContext();
        var previous = new QueryRecoveryDocument(Guid.NewGuid(), "Previous", "SELECT 1;", "", null);
        await test.Store.SaveAsync([previous]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Store.SaveAsync([previous with { Text = "new" }], cancellation.Token));
        Assert.Equal(previous, Assert.Single(await test.Store.LoadAsync()));
        Assert.Empty(Directory.GetFiles(test.Root, "*.tmp"));
    }

    private sealed class RecoveryTestContext : IAsyncDisposable
    {
        private readonly List<DatabasePaneViewModel> _models = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-SqlRecovery-" + Guid.NewGuid().ToString("N"));
        public string RecoveryPath => Path.Combine(Root, "recovery.json");
        public QueryRecoveryStore Store { get; }
        public NoDatabase Database { get; } = new();
        public RecoveryTestContext() { Directory.CreateDirectory(Root); Store = new QueryRecoveryStore(RecoveryPath); }
        public DatabasePaneViewModel Create(RecoveryDialogs? dialogs = null)
        {
            dialogs ??= new RecoveryDialogs();
            var model = new DatabasePaneViewModel(Database, new ConnectionProfileStore(Path.Combine(Root, "profiles.json")), dialogs, dialogs, Store);
            _models.Add(model); return model;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var model in _models) await model.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }
    private sealed class RecoveryDialogs : IFileDialogService, IUserDialogService
    {
        public bool Recover; public int RecoveryQuestions;
        public Queue<SaveDecision> Decisions { get; } = new();
        public Queue<string> SavePaths { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message) { if (title == "Recover SQL scripts") { RecoveryQuestions++; return Task.FromResult(Recover); } return Task.FromResult(false); }
        public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(Decisions.TryDequeue(out var decision) ? decision : SaveDecision.Cancel);
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult(SavePaths.TryDequeue(out var path) ? path : null);
    }
    private sealed class NoDatabase : IDatabaseService
    {
        public int Executions; public int Connections;
        public Task<IReadOnlyList<SchemaItem>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken) { Connections++; return Task.FromResult<IReadOnlyList<SchemaItem>>([]); }
        public Task<IReadOnlyList<SchemaItem>> GetChildrenAsync(ConnectionProfile profile, SchemaItem parent, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SchemaItem>>([]);
        public Task<string> GetDefinitionAsync(ConnectionProfile profile, SchemaItem item, CancellationToken cancellationToken) => Task.FromResult("");
        public Task<QueryExecutionResult> ExecuteAsync(ConnectionProfile profile, string sql, int startLine, CancellationToken cancellationToken) { Executions++; return Task.FromResult(new QueryExecutionResult([], [], TimeSpan.Zero)); }
    }
}
