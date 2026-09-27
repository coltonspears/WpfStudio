using Microsoft.Extensions.Logging.Abstractions;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Database.Services;
using WpfStudio.Database.ViewModels;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

internal sealed class ShellTestContext : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-ShellTests-" + Guid.NewGuid().ToString("N"));
    public FakeDialogs Dialogs { get; } = new();
    public InlineDispatcher Dispatcher { get; } = new();
    public DocumentStore Store { get; }
    public WorkspaceClient Workspace { get; } = new();
    public XamlCompletionService Xaml { get; } = new();
    public ShellViewModel Shell { get; }
    private readonly DebuggerViewModel _debugger;
    private readonly TerminalViewModel _terminal;
    public ShellTestContext()
    {
        Directory.CreateDirectory(Root);
        Store = new DocumentStore(Path.Combine(Root, "state"));
        var settings = new SettingsStore(Path.Combine(Root, "state"));
        var database = new DatabasePaneViewModel(new SqlDatabaseService(), new ConnectionProfileStore(Path.Combine(Root, "sql.json")), Dialogs, Dialogs);
        _debugger = new DebuggerViewModel(new DebugSession(), Dispatcher);
        _terminal = new TerminalViewModel(Dispatcher);
        Shell = new ShellViewModel(Store, settings, Workspace, new BuildService(), new WpfIndexService(), new ScaffoldingService(), new WorkspaceEditTransaction(Store), Xaml, Dialogs, Dialogs, Dispatcher, _debugger, _terminal, database, NullLogger<ShellViewModel>.Instance);
    }
    public async Task<string> CreateFileAsync(string name, string content)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
    public async ValueTask DisposeAsync()
    {
        if (Shell.IsPreviewOpen) Shell.CancelPreviewCommand.Execute(null);
        await Shell.DisposeAsync();
        await _debugger.DisposeAsync();
        await _terminal.DisposeAsync();
        Directory.Delete(Root, true);
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
}

internal sealed class FakeDialogs : IFileDialogService, IUserDialogService
{
    public Queue<SaveDecision> SaveDecisions { get; } = new();
    public Queue<bool> Confirmations { get; } = new();
    public Queue<string?> Prompts { get; } = new();
    public Queue<string?> SavePaths { get; } = new();
    public List<string> Errors { get; } = [];
    public List<string> SaveQuestions { get; } = [];
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirmations.TryDequeue(out bool answer) && answer);
    public Task<SaveDecision> AskSaveAsync(string documentName)
    {
        SaveQuestions.Add(documentName);
        return Task.FromResult(SaveDecisions.TryDequeue(out SaveDecision answer) ? answer : SaveDecision.Cancel);
    }
    public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult(Prompts.TryDequeue(out string? value) ? value : defaultValue);
    public Task ShowErrorAsync(string title, string message) { Errors.Add(title + ": " + message); return Task.CompletedTask; }
    public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult(SavePaths.TryDequeue(out string? path) ? path : null);
    public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
}
