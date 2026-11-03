using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Features.ColtonGpt;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Features.Packages;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

/// <summary>What the optional tools need from the shell when they change workspace files.</summary>
public interface IWorkspaceMutationHost
{
    /// <summary>
    /// Saves every open document before a tool rewrites project files; false when the user cancelled. Throws while a
    /// build, debug session or change review is in progress.
    /// </summary>
    Task<bool> SaveBeforeMutationAsync();
    /// <summary>A tool changed workspace files (a package install, a checkout): re-check open files and reload the project model.</summary>
    Task WorkspaceChangedAsync();
}

/// <summary>Everything the optional tools ask of the shell. They reach it only through <see cref="StudioFeatures"/>.</summary>
public interface IStudioToolsHost : IWorkspaceMutationHost
{
    void OpenFile(string path);
    /// <summary>A commit, checkout or merge changed the repository.</summary>
    void GitRepositoryChanged();
    /// <summary>A Git operation rewrote working files.</summary>
    void GitWorkspaceFilesChanged();
    void OpenAssistantSettings();
    void RefreshAssistantContext();
}

/// <summary>Composition and workspace coordination for optional tools.</summary>
public sealed class StudioFeatures : IDisposable
{
    private readonly IMessenger _messenger;
    private IStudioToolsHost? _host;
    private bool _disposed;
    public StudioFeatures(INuGetPackageService packages, GitService git, AssistantViewModel assistant, IUserDialogService dialogs, IDesktopService desktop, IMessenger messenger)
    {
        _messenger = messenger;
        Assistant = assistant;
        GitService = git;
        Packages = new(packages, dialogs, () => Git?.IsBusy == true ? Task.FromException<bool>(new InvalidOperationException("Wait for the Git operation to finish.")) : Host.SaveBeforeMutationAsync(), () => Host.WorkspaceChangedAsync());
        Git = new(git, desktop, _ => Packages.IsBusy ? Task.FromException<bool>(new InvalidOperationException("Wait for the package operation to finish.")) : Host.SaveBeforeMutationAsync()) { AutoSelect = true };
        Git.OpenFileRequested += path => _host?.OpenFile(path);
        Git.RepositoryChanged += () => _host?.GitRepositoryChanged();
        Git.WorkspaceFilesChanged += () => _host?.GitWorkspaceFilesChanged();
        assistant.SettingsRequested += () => _host?.OpenAssistantSettings();
        assistant.RefreshContextRequested += () => _host?.RefreshAssistantContext();
        messenger.Register<StudioFeatures, WorkspaceChangedMessage>(this, static (features, message) => features.Packages.SetWorkspace(message.Workspace));
    }
    private IStudioToolsHost Host => _host ?? throw new InvalidOperationException("The shell has not attached to the workspace tools yet.");
    /// <summary>Connects the tools to the shell that owns the documents and the project model; the shell calls this once.</summary>
    public void Attach(IStudioToolsHost host) => _host = host;
    public PackagesViewModel Packages { get; }
    public GitViewModel Git { get; }
    /// <summary>The Git CLI boundary shared by the Git pane and editors (change margin and blame).</summary>
    public GitService GitService { get; }
    public AssistantViewModel Assistant { get; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messenger.UnregisterAll(this);
        _host = null;
        Packages.Dispose(); Git.Dispose(); Assistant.Dispose();
    }
}
