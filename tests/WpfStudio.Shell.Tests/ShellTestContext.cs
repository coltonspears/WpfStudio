using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

internal sealed class ShellTestContext : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-ShellTests-" + Guid.NewGuid().ToString("N"));
    public FakeDialogs Dialogs { get; } = new();
    public InlineDispatcher Dispatcher { get; } = new();
    public DocumentStore Store { get; }
    public WorkspaceClient Workspace { get; }
    public XamlCompletionService Xaml { get; }
    public ShellViewModel Shell { get; }
    private readonly ServiceProvider _services;
    internal FakeDesktop Desktop { get; } = new();
    internal FakeTheme Theme { get; } = new();
    public ShellTestContext(WpfStudio.Runtime.Design.IPreviewClient? previewClient = null, Action<IServiceCollection>? configure = null)
    {
        Directory.CreateDirectory(Root);
        var services = new ServiceCollection().AddWpfStudio(Dispatcher);
        services.Configure<StudioPaths>(paths => paths.DataDirectory = Path.Combine(Root, "state"));
        services.AddSingleton<IFileDialogService>(Dialogs); services.AddSingleton<IUserDialogService>(Dialogs);
        services.AddSingleton<IDesktopService>(Desktop); services.AddSingleton<IClipboardService>(Desktop); services.AddSingleton<IThemeService>(Theme);
        if (previewClient != null) services.AddSingleton(previewClient);
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
        Shell = _services.GetRequiredService<ShellViewModel>();
        Store = _services.GetRequiredService<DocumentStore>();
        Workspace = _services.GetRequiredService<WorkspaceClient>();
        Xaml = _services.GetRequiredService<XamlCompletionService>();
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
        if (Shell.IsPreviewOpen) Shell.ChangeReview.CancelCommand.Execute(null);
        await _services.DisposeAsync();
        await DeleteRootAsync();
    }
    private async Task DeleteRootAsync()
    {
        string root = Path.GetFullPath(Root);
        string temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(root), temporaryDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("WpfStudio-ShellTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean a path outside this test's temporary directory.");
        // Worker shutdown requests process-tree termination but does not await
        // exit. Allow transient Windows handles to close; persistent I/O errors
        // still fail cleanup after seven attempts (3.15 seconds of total delay).
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException) when (attempt < 6)
            { await Task.Delay(50 << attempt).ConfigureAwait(false); }
        }
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
    /// <summary>Runs while the named confirmation is open, as dispatcher work does behind a modal message box.</summary>
    public Func<string, Task>? WhileConfirming { get; set; }
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (WhileConfirming is { } work) await work(title);
        return Confirmations.TryDequeue(out bool answer) && answer;
    }
    /// <summary>Runs while the save question is open, as dispatcher work does behind a modal message box.</summary>
    public Func<Task>? WhileAsking { get; set; }
    public async Task<SaveDecision> AskSaveAsync(string documentName)
    {
        SaveQuestions.Add(documentName);
        if (WhileAsking is { } work) await work();
        return SaveDecisions.TryDequeue(out SaveDecision answer) ? answer : SaveDecision.Cancel;
    }
    public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult(Prompts.TryDequeue(out string? value) ? value : defaultValue);
    public Task ShowErrorAsync(string title, string message) { Errors.Add(title + ": " + message); return Task.CompletedTask; }
    public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult(SavePaths.TryDequeue(out string? path) ? path : null);
    public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
}
