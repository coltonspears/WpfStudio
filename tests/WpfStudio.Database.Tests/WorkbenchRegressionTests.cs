using WpfStudio.Contracts;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Database.ViewModels;

namespace WpfStudio.Database.Tests;

/// <summary>Regressions from the code review: saving a profile, column completion text, and a profile with an unreadable password.</summary>
public sealed class WorkbenchRegressionTests
{
    [Fact]
    public async Task SavingTheConnectedProfileKeepsTheSchemaTree()
    {
        var model = new DatabasePaneViewModel(new OneDatabase(), new MemoryStore(), new NoDialogs(), new NoAnswers(), new NoRecovery());
        await model.ConnectCommand.ExecuteAsync(null);
        Assert.True(model.IsConnected);
        Assert.Single(model.Schema);

        model.ProfileName = "Renamed";
        await model.SaveProfileCommand.ExecuteAsync(null);

        Assert.Contains("saved", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(model.IsConnected);
        Assert.Single(model.Schema);
        Assert.Equal("Renamed", model.SelectedProfile?.Name);
    }

    [Fact]
    public async Task SelectingAProfileForAnotherServerClearsTheSchemaTree()
    {
        var model = new DatabasePaneViewModel(new OneDatabase(), new MemoryStore(), new NoDialogs(), new NoAnswers(), new NoRecovery());
        await model.ConnectCommand.ExecuteAsync(null);
        model.SelectedProfile = new ConnectionProfile { Name = "Other", Server = "other-host" };
        Assert.False(model.IsConnected);
        Assert.Empty(model.Schema);
    }

    [Fact]
    public void ColumnCompletionInsertsTheBareColumnName()
    {
        Assert.Equal("[dbo].[Users]", new SchemaItem("Users", SchemaNodeKind.Table, "app", "dbo", 1).CompletionText);
        Assert.Equal("[Id]", new SchemaItem("Id", SchemaNodeKind.Column, "app", "dbo", 1).CompletionText);
        Assert.Equal("[Odd]]Name]", new SchemaItem("Odd]Name", SchemaNodeKind.Column, "app", "dbo", 1).CompletionText);
    }

    [Fact]
    public async Task AProfileWhosePasswordCannotBeUnlockedDoesNotHideTheOthers()
    {
        string path = Path.Combine(Path.GetTempPath(), "wpfstudio-profile-" + Guid.NewGuid() + ".json");
        try
        {
            // "AAAA" is valid base64 but not a DPAPI blob, as a file copied from another account would contain.
            await File.WriteAllTextAsync(path, """
                [
                  { "Profile": { "Name": "Windows", "Server": "one", "Database": "master", "WindowsAuthentication": true }, "EncryptedPassword": null },
                  { "Profile": { "Name": "Sql", "Server": "two", "Database": "master", "WindowsAuthentication": false, "UserName": "sa", "RememberPassword": true }, "EncryptedPassword": "AAAA" }
                ]
                """);
            var loaded = await new ConnectionProfileStore(path).LoadAsync();
            Assert.Equal(["Windows", "Sql"], loaded.Select(profile => profile.Name));
            Assert.Equal("", loaded[1].Password);
        }
        finally { File.Delete(path); }
    }

    private sealed class OneDatabase : IDatabaseService
    {
        public Task<IReadOnlyList<SchemaItem>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SchemaItem>>([new SchemaItem("master", SchemaNodeKind.Database, "master")]);
        public Task<IReadOnlyList<SchemaItem>> GetChildrenAsync(ConnectionProfile profile, SchemaItem parent, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SchemaItem>>([]);
        public Task<string> GetDefinitionAsync(ConnectionProfile profile, SchemaItem item, CancellationToken cancellationToken) => Task.FromResult("");
        public Task<QueryExecutionResult> ExecuteAsync(ConnectionProfile profile, string sql, int startLine, CancellationToken cancellationToken) => Task.FromResult(new QueryExecutionResult([], [], TimeSpan.Zero));
    }
    private sealed class MemoryStore : IConnectionProfileStore
    {
        private IReadOnlyList<ConnectionProfile> _profiles = [];
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_profiles);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default) { _profiles = profiles; return Task.CompletedTask; }
    }
    private sealed class NoDialogs : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
    }
}
