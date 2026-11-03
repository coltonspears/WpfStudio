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
        await model.Explorer.ConnectCommand.ExecuteAsync(null);
        Assert.True(model.Explorer.IsConnected);
        Assert.Single(model.Explorer.Schema);

        model.Connections.ProfileName = "Renamed";
        await model.Connections.SaveProfileCommand.ExecuteAsync(null);

        Assert.Contains("saved", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(model.Explorer.IsConnected);
        Assert.Single(model.Explorer.Schema);
        Assert.Equal("Renamed", model.Connections.SelectedProfile?.Name);
    }

    [Fact]
    public async Task SelectingAProfileForAnotherServerClearsTheSchemaTree()
    {
        var model = new DatabasePaneViewModel(new OneDatabase(), new MemoryStore(), new NoDialogs(), new NoAnswers(), new NoRecovery());
        await model.Explorer.ConnectCommand.ExecuteAsync(null);
        model.Connections.SelectedProfile = new ConnectionProfile { Name = "Other", Server = "other-host" };
        Assert.False(model.Explorer.IsConnected);
        Assert.Empty(model.Explorer.Schema);
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
            var store = new ConnectionProfileStore(path);
            var loaded = await store.LoadAsync();
            Assert.Equal(["Windows", "Sql"], loaded.Select(profile => profile.Name));
            Assert.Equal("", loaded[1].Password);
            Assert.Equal("", store.UnlockPassword(loaded[1]));

            // Saving other changes leaves that password as it was, in case its own account opens the file again.
            await store.SaveAsync(loaded);
            Assert.Contains("\"EncryptedPassword\": \"AAAA\"", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OnlyTheProfileBeingUsedHasItsPasswordUnlocked()
    {
        string path = Path.Combine(Path.GetTempPath(), "wpfstudio-profile-" + Guid.NewGuid() + ".json");
        try
        {
            var writer = new ConnectionProfileStore(path);
            ConnectionProfile Sql(string name) => new() { Name = name, WindowsAuthentication = false, UserName = "sa", Password = name + "-secret", RememberPassword = true };
            await writer.SaveAsync([Sql("first"), Sql("second")]);

            var store = new CountingStore(new ConnectionProfileStore(path));
            await using var model = new DatabasePaneViewModel(new OneDatabase(), store, new NoDialogs(), new NoAnswers(), new NoRecovery());
            await model.InitializeCommand.ExecuteAsync(null);
            Assert.Equal(["first"], store.Unlocked);
            Assert.Equal("first-secret", model.Connections.Password);
            Assert.All(model.Connections.Profiles, profile => Assert.Equal("", profile.Password));

            model.Connections.SelectedProfile = model.Connections.Profiles[1];
            Assert.Equal(["first", "second"], store.Unlocked);
            Assert.Equal("second-secret", model.Connections.Password);

            // Saving the selected profile re-encrypts what was typed and keeps the other profile's password.
            model.Connections.Password = "changed";
            await model.Connections.SaveProfileCommand.ExecuteAsync(null);
            var reloaded = new ConnectionProfileStore(path);
            var profiles = await reloaded.LoadAsync();
            Assert.Equal("first-secret", reloaded.UnlockPassword(profiles.Single(profile => profile.Name == "first")));
            Assert.Equal("changed", reloaded.UnlockPassword(profiles.Single(profile => profile.Name == "second")));
        }
        finally { File.Delete(path); }
    }

    private sealed class CountingStore(IConnectionProfileStore inner) : IConnectionProfileStore
    {
        public List<string> Unlocked { get; } = [];
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default) => inner.SaveAsync(profiles, cancellationToken);
        public string UnlockPassword(ConnectionProfile profile) { Unlocked.Add(profile.Name); return inner.UnlockPassword(profile); }
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
