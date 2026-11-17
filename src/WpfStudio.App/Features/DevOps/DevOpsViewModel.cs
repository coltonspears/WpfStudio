using System.ComponentModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

public enum DevOpsConnectionState { NoRepository, Unsupported, SignedOut, Connecting, Connected, Failed }

/// <summary>Creates the provider for a target with a token, the workbench's settings applied.</summary>
public delegate IDevOpsProvider DevOpsProviderFactory(DevOpsTarget target, string token, DevOpsSettings settings);

/// <summary>
/// The DevOps workbench: the pull requests, work items (issues) and pipelines (Actions) of the repository the Git pane has open, on
/// Azure DevOps or GitHub. It follows the Git pane's remote and branch, connects only once it has been opened, and signs in with the
/// token Git Credential Manager already has for the remote, or with a personal access token saved for this Windows account.
/// </summary>
public sealed partial class DevOpsViewModel : ObservableObject, IDisposable, IDevOpsHost
{
    public const int PullRequestsTab = 0, WorkItemsTab = 1, PipelinesTab = 2, AccessTab = 3;

    private readonly IDevOpsCredentials _credentials;
    private readonly DevOpsProviderFactory _createProvider;
    private readonly IDesktopService _desktop;
    private readonly IUserDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly OperationGate _operations;
    private readonly LatestLoad _connection = new(), _branchPullRequest = new();
    private Task _activeTask = Task.CompletedTask;
    private string? _connectedKey;
    private bool _activated, _disposed;

    public DevOpsViewModel(GitViewModel git, GitService gitService, IDevOpsCredentials credentials, DevOpsProviderFactory createProvider,
        IDesktopService desktop, IUserDialogService dialogs, IClipboardService clipboard)
    {
        Git = git;
        GitService = gitService;
        _credentials = credentials;
        _createProvider = createProvider;
        _desktop = desktop;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _operations = new(busy => IsBusy = busy);
        PullRequests = new(this);
        WorkItems = new(this);
        Pipelines = new(this);
        Access = new(this);
        NewPullRequest = new(this);
        git.PropertyChanged += OnGitPropertyChanged;
        git.CreatePullRequestInWorkbench = TryStartCreatePullRequest;
        git.BranchPullRequestRequested += ShowBranchPullRequest;
    }

    public GitViewModel Git { get; }
    public GitService GitService { get; }
    public PullRequestsViewModel PullRequests { get; }
    public WorkItemsViewModel WorkItems { get; }
    public PipelinesViewModel Pipelines { get; }
    public DevOpsAccessViewModel Access { get; }
    public CreatePullRequestViewModel NewPullRequest { get; }

    /// <summary>Raised when the workbench asks to be shown: the Git pane opened a pull request in it.</summary>
    public event Action? ShowRequested;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ServiceName), nameof(Title), nameof(IsGitHub), nameof(WorkItemsTitle), nameof(PipelinesTitle), nameof(TokenHelp), nameof(TokenHost))]
    public partial DevOpsTarget? Target { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Capabilities), nameof(WorkItemsTitle), nameof(PipelinesTitle))]
    public partial IDevOpsProvider? Provider { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SignInText))] public partial DevOpsIdentity? User { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SignInText))] public partial DevOpsSignInSource? SignInSource { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConnected), nameof(NeedsSignIn), nameof(IsConnecting), nameof(HasFailed), nameof(ShowsMessage), nameof(StateMessage), nameof(SignInText))]
    public partial DevOpsConnectionState State { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "Opens the pull requests, work items and pipelines of the repository in the Git pane.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; private set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial DevOpsSettings Settings { get; private set; } = new();
    /// <summary>The active pull request from the current branch, when there is one.</summary>
    [ObservableProperty] public partial DevOpsPullRequest? BranchPullRequest { get; private set; }

    public int Version { get; private set; }
    public DevOpsCapabilities? Capabilities => Provider?.Capabilities;
    public string ServiceName => Target?.ServiceName ?? "DevOps";
    public string Title => Target?.DisplayName ?? (Git.HasRemote ? Git.Remote!.WebUrl : "No repository");
    public bool IsGitHub => Target?.Kind == DevOpsProviderKind.GitHub;
    public string WorkItemsTitle => Capabilities?.WorkItemsName ?? (IsGitHub ? "Issues" : "Work items");
    public string PipelinesTitle => Capabilities?.PipelinesName ?? (IsGitHub ? "Actions" : "Pipelines");
    public bool IsConnected => State == DevOpsConnectionState.Connected;
    public bool NeedsSignIn => State == DevOpsConnectionState.SignedOut;
    public bool IsConnecting => State == DevOpsConnectionState.Connecting;
    public bool HasFailed => State == DevOpsConnectionState.Failed;
    public bool ShowsMessage => State is DevOpsConnectionState.NoRepository or DevOpsConnectionState.Unsupported;
    public bool HasError => Error.Length > 0;
    public bool CanAct => IsConnected && !IsBusy && !_disposed;
    public string TokenHost => Target?.Host ?? "";
    public string SignInText => State != DevOpsConnectionState.Connected || User is null ? ""
        : $"{User.DisplayName} · {(SignInSource == DevOpsSignInSource.GitCredentialManager ? "signed in with Git Credential Manager" : "personal access token")}";

    public string StateMessage => State switch
    {
        DevOpsConnectionState.NoRepository => "Open a solution or folder in a Git repository to see its pull requests, work items and pipelines.",
        DevOpsConnectionState.Unsupported => Git.HasRemote
            ? $"This repository's remote is on {Git.Remote!.HostName}. The DevOps workbench works with Azure DevOps (Services and Server) and GitHub; use the Git pane's links for other hosts."
            : "This repository has no remote. Add an Azure DevOps or GitHub remote to work with its pull requests, work items and pipelines.",
        DevOpsConnectionState.SignedOut => $"Sign in to {ServiceName} to work with {Target?.DisplayName}.",
        DevOpsConnectionState.Connecting => $"Connecting to {ServiceName}…",
        DevOpsConnectionState.Failed => $"{ServiceName} could not be reached.",
        _ => ""
    };

    public string TokenHelp => IsGitHub
        ? "A classic token with the repo and workflow scopes, or a fine-grained token with access to this repository (contents, pull requests, issues, actions)."
        : "Tick Work Items (Read & write), Code (Read & write), Build (Read & execute) and Project and Team (Read) when you create the token.";

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnStateChanged(DevOpsConnectionState value) => NotifyCommands();
    partial void OnSelectedTabChanged(int value) => LoadTab();

    /// <summary>Called when the workbench is first shown: reads the settings and connects to the Git pane's remote.</summary>
    public async Task ActivateAsync()
    {
        if (!_activated)
        {
            _activated = true;
            try { Settings = await _credentials.LoadSettingsAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "DevOps settings could not be read: " + ex.Message; }
        }
        await ConnectIfChangedAsync();
    }

    private void OnGitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_activated || _disposed) return;
        switch (e.PropertyName)
        {
            case nameof(GitViewModel.Remote) or nameof(GitViewModel.RepositoryRoot):
                ConnectIfChangedAsync().Forget(ReportError);
                break;
            case nameof(GitViewModel.Branch):
                RefreshBranchPullRequest();
                NewPullRequest.BranchChanged();
                Pipelines.BranchChanged();
                break;
            case nameof(GitViewModel.Upstream) or nameof(GitViewModel.AheadCount):
                NewPullRequest.BranchChanged();
                break;
        }
    }

    private Task ConnectIfChangedAsync()
    {
        var target = DevOpsTarget.FromRemoteUrl(Git.Remote?.WebUrl);
        var key = $"{Git.RepositoryRoot}|{target?.RepositoryWebUrl}";
        if (key == _connectedKey) return Task.CompletedTask;
        _connectedKey = key;
        return ConnectAsync(target);
    }

    /// <summary>Connects to <paramref name="target"/> with <paramref name="signIn"/>, or with the saved or Git sign-in when null.</summary>
    private Task ConnectAsync(DevOpsTarget? target, DevOpsSignIn? signIn = null)
    {
        var version = ++Version;
        _operations.Cancel();
        ResetTabs();
        Provider = null;
        User = null;
        SignInSource = null;
        BranchPullRequest = null;
        Git.BranchPullRequestText = null;
        Error = "";
        Target = target;
        OnPropertyChanged(nameof(Title));
        if (Git.RepositoryRoot is not { } root)
        {
            State = DevOpsConnectionState.NoRepository;
            Status = StateMessage;
            return Task.CompletedTask;
        }
        if (target is null)
        {
            State = DevOpsConnectionState.Unsupported;
            Status = StateMessage;
            return Task.CompletedTask;
        }
        State = DevOpsConnectionState.Connecting;
        Status = StateMessage;
        return _connection.Run(async token =>
        {
            var credential = signIn ?? await _credentials.FindAsync(target, root, token);
            token.ThrowIfCancellationRequested();
            if (credential is null)
            {
                State = DevOpsConnectionState.SignedOut;
                Status = StateMessage;
                return;
            }
            var provider = _createProvider(target, credential.Token, Settings);
            DevOpsIdentity user;
            try { user = await provider.GetCurrentUserAsync(token); }
            catch (DevOpsAuthenticationException ex)
            {
                token.ThrowIfCancellationRequested();
                State = DevOpsConnectionState.SignedOut;
                Status = StateMessage;
                Error = credential.Source == DevOpsSignInSource.GitCredentialManager
                    ? $"{target.ServiceName} did not accept the sign-in Git Credential Manager has for {target.Host}. Sign in with Git Credential Manager again, or use a personal access token."
                    : ex.Message;
                return;
            }
            token.ThrowIfCancellationRequested();
            Provider = provider;
            User = user;
            SignInSource = credential.Source;
            State = DevOpsConnectionState.Connected;
            Status = $"Signed in to {target.ServiceName} as {user.DisplayName}";
            LoadTab();
            RefreshBranchPullRequest();
        }, ex =>
        {
            if (version != Version) return;
            State = DevOpsConnectionState.Failed;
            Status = StateMessage;
            Error = Describe(ex);
        });
    }

    private void ResetTabs()
    {
        PullRequests.Reset();
        WorkItems.Reset();
        Pipelines.Reset();
        Access.Reset();
        NewPullRequest.Close();
    }

    /// <summary>Loads the selected tab the first time it is shown for this connection.</summary>
    private void LoadTab()
    {
        if (!IsConnected) return;
        switch (SelectedTab)
        {
            case PullRequestsTab: PullRequests.EnsureLoaded(); break;
            case WorkItemsTab: WorkItems.EnsureLoaded(); break;
            case PipelinesTab: Pipelines.EnsureLoaded(); break;
            case AccessTab: Access.EnsureLoaded(); break;
        }
    }

    private void RefreshBranchPullRequest()
    {
        var branch = Git.Branch;
        if (Provider is not { } provider || !IsBranchName(branch))
        {
            _branchPullRequest.Cancel();
            BranchPullRequest = null;
            Git.BranchPullRequestText = null;
            return;
        }
        // A summary in the Git pane: when it cannot be read, the pane simply shows none.
        _branchPullRequest.Start(async token =>
        {
            var pullRequest = await provider.FindPullRequestAsync(branch, token);
            token.ThrowIfCancellationRequested();
            BranchPullRequest = pullRequest;
            Git.BranchPullRequestText = pullRequest is null ? null : $"!{pullRequest.Number} · {pullRequest.StatusText} · {pullRequest.ReviewText}";
            NewPullRequest.BranchChanged();
        }, _ => { });
    }

    internal static bool IsBranchName(string? branch) => !string.IsNullOrEmpty(branch) && branch is not ("Detached HEAD" or "No repository" or "Loading repository…");

    private void ShowBranchPullRequest()
    {
        if (BranchPullRequest is not { } pullRequest) return;
        ShowRequested?.Invoke();
        ShowPullRequest(pullRequest.Number);
    }

    /// <summary>The Git pane's Create pull request: true when the form opened here, false to fall back to the web page.</summary>
    private bool TryStartCreatePullRequest()
    {
        if (!IsConnected || _disposed) return false;
        ShowRequested?.Invoke();
        StartCreatePullRequest();
        return true;
    }

    // ------------------------------------------------------------------ Commands

    private bool CanRefresh() => !_disposed && !IsBusy && State is not (DevOpsConnectionState.NoRepository or DevOpsConnectionState.Unsupported or DevOpsConnectionState.Connecting);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        if (!IsConnected) return ConnectAsync(Target);
        switch (SelectedTab)
        {
            case PullRequestsTab: PullRequests.Reload(); break;
            case WorkItemsTab: WorkItems.Reload(); break;
            case PipelinesTab: Pipelines.Reload(); break;
            case AccessTab: Access.Reload(); break;
        }
        RefreshBranchPullRequest();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Cancel() => _operations.Cancel();

    private bool CanSignIn() => !_disposed && !IsBusy && Target is not null && Git.RepositoryRoot is not null && State is DevOpsConnectionState.SignedOut or DevOpsConnectionState.Failed or DevOpsConnectionState.Connected;

    /// <summary>Lets Git Credential Manager sign in (Microsoft Entra for Azure DevOps, the browser for GitHub) and uses what it stores.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInWithGitAsync()
    {
        if (Target is not { } target || Git.RepositoryRoot is not { } root || _operations.TryEnter() is not { } lease) return;
        try
        {
            Error = "";
            Status = "Waiting for Git Credential Manager…";
            var signIn = await _credentials.SignInWithGitAsync(target, root, lease.Token);
            if (signIn is null)
            {
                Error = $"Git Credential Manager did not provide a sign-in for {target.Host}. Install Git for Windows with Git Credential Manager, or sign in with a personal access token.";
                Status = StateMessage;
                return;
            }
            lease.Dispose();
            await ConnectAsync(target, signIn);
        }
        // A failed sign-in leaves the current connection, if there is one, as it was.
        catch (Exception ex) when (ex is not OperationCanceledException) { Error = Describe(ex); Status = StateMessage; }
        finally { lease.Dispose(); }
    }

    /// <summary>Checks a pasted token against the service, then saves it encrypted for this Windows account.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInWithTokenAsync(string? token)
    {
        if (Target is not { } target || _operations.TryEnter() is not { } lease) return;
        try
        {
            token = token?.Trim();
            if (string.IsNullOrEmpty(token)) { Error = "Paste a personal access token first."; return; }
            Error = "";
            Status = $"Checking the token with {target.ServiceName}…";
            await _createProvider(target, token, Settings).GetCurrentUserAsync(lease.Token);
            await _credentials.SaveTokenAsync(target, token, lease.Token);
            lease.Dispose();
            await ConnectAsync(target, new(token, DevOpsSignInSource.PersonalAccessToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DevOpsAuthenticationException ? $"{target.ServiceName} did not accept that token. Check that it was copied whole and has not expired." : Describe(ex);
            Status = IsConnected ? $"Still signed in as {User?.DisplayName}" : StateMessage;
        }
        finally { lease.Dispose(); }
    }

    private bool CanSignOut() => !_disposed && !IsBusy && Target is not null && SignInSource is not null;

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOutAsync()
    {
        if (Target is not { } target) return;
        var fromGit = SignInSource == DevOpsSignInSource.GitCredentialManager;
        await _credentials.SignOutAsync(target);
        await ConnectAsync(target);
        Status = fromGit
            ? $"WpfStudio no longer uses Git's sign-in for {target.Host}. Git itself still does, for fetch and push."
            : $"The saved token for {target.Host} was removed from this computer. It is still valid until you revoke it on {target.ServiceName}.";
    }

    /// <summary>Opens the form that creates a pull request from the Git pane's current branch, bringing the workbench forward.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void CreatePullRequest()
    {
        ShowRequested?.Invoke();
        StartCreatePullRequest();
    }

    [RelayCommand]
    private void OpenTokenPage()
    {
        if (Target is { } target) OpenUrl(target.TokenPageUrl);
    }

    [RelayCommand]
    private void OpenRepository()
    {
        if (Target is { } target) OpenUrl(target.RepositoryWebUrl);
    }

    /// <summary>Saves the workbench's preferences and applies them to the connection.</summary>
    public async Task SaveSettingsAsync(DevOpsSettings settings)
    {
        await _credentials.SaveSettingsAsync(current => settings with { ProtectedTokens = current.ProtectedTokens, GitSignInDeclined = current.GitSignInDeclined });
        var hiddenStatesChanged = settings.HiddenStates != Settings.HiddenStates;
        Settings = settings;
        Status = "DevOps settings saved";
        // The provider reads the hidden states when it is created.
        if (hiddenStatesChanged && IsConnected && Target is { } target && SignInSource is { } source)
        {
            var credential = await _credentials.FindAsync(target, Git.RepositoryRoot!, CancellationToken.None);
            if (credential is not null && credential.Source == source) Provider = _createProvider(target, credential.Token, settings);
            WorkItems.Reset();
        }
    }

    // ------------------------------------------------------------------ Host

    public Task RunAsync(string label, Func<IDevOpsProvider, DevOpsIdentity, CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (Provider is not { } provider || User is not { } user || _disposed || _operations.TryEnter(cancellationToken) is not { } lease) return Task.CompletedTask;
        return _activeTask = RunCoreAsync(label, provider, user, action, lease);
    }

    private async Task RunCoreAsync(string label, IDevOpsProvider provider, DevOpsIdentity user, Func<IDevOpsProvider, DevOpsIdentity, CancellationToken, Task> action, OperationGate.Lease lease)
    {
        var version = Version;
        Error = "";
        Status = label + "…";
        try
        {
            await action(provider, user, lease.Token);
            if (version == Version && Status == label + "…") Status = label + " · done";
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested) { if (version == Version) Status = label + " · cancelled"; }
        catch (Exception ex)
        {
            if (version != Version) return;
            ReportError(ex);
            Status = label + " · failed";
        }
        finally { lease.Dispose(); }
    }

    /// <summary>
    /// Waits for the running action and every load the tabs started, including loads those loads start (a list selects its
    /// first entry, whose details then load), until nothing new has begun.
    /// </summary>
    public async Task WhenIdleAsync()
    {
        for (var round = 0; round < 50; round++)
        {
            var pending = Pending();
            await Task.WhenAll(pending);
            if (Pending().SequenceEqual(pending)) return;
        }
    }

    private Task[] Pending() =>
        [_connection.Task, _activeTask, _branchPullRequest.Task, .. PullRequests.Loads, .. WorkItems.Loads, .. Pipelines.Loads, .. Access.Loads, .. NewPullRequest.Loads];

    public void ReportError(Exception exception)
    {
        Error = Describe(exception);
        // A token revoked or expired while the workbench was open: back to the sign-in page, with the reason.
        if (exception is DevOpsAuthenticationException && IsConnected)
        {
            Provider = null;
            User = null;
            SignInSource = null;
            State = DevOpsConnectionState.SignedOut;
        }
    }

    public void ReportStatus(string status) => Status = status;

    internal string Describe(Exception exception) => GitDiagnosticSanitizer.Redact(exception switch
    {
        HttpRequestException http => $"Could not reach {Target?.Host ?? "the service"}: {http.Message}",
        TaskCanceledException => $"{Target?.Host ?? "The service"} did not answer in time. Try again.",
        _ => exception.Message
    });

    public void OpenUrl(string url)
    {
        try { _desktop.OpenUrl(url); }
        catch (Exception ex) { Error = ex.Message; }
    }

    public void OpenFile(string path)
    {
        if (File.Exists(path)) ((IGitOperationHost)Git).OpenFile(path);
        else Error = "This file is not in your working tree. Check out the pull request's branch to open it.";
    }

    public void Copy(string text, string what) => Status = _clipboard.TrySetText(text) ? $"Copied {what}" : "The clipboard is in use by another application. Try again.";

    public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => _dialogs.PromptAsync(title, message, defaultValue);
    public Task<bool> ConfirmAsync(string title, string message) => _dialogs.ConfirmAsync(title, message);

    public void ShowWorkItem(int id)
    {
        SelectedTab = WorkItemsTab;
        WorkItems.Show(id);
    }

    public void ShowPullRequest(int number)
    {
        SelectedTab = PullRequestsTab;
        PullRequests.Show(number);
    }

    public void StartCreatePullRequest()
    {
        SelectedTab = PullRequestsTab;
        NewPullRequest.OpenAsync().Forget(ReportError);
    }

    public void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { RefreshCommand, SignInWithGitCommand, SignInWithTokenCommand, SignOutCommand, CreatePullRequestCommand })
            command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAct));
        PullRequests.NotifyCommands();
        WorkItems.NotifyCommands();
        Pipelines.NotifyCommands();
        Access.NotifyCommands();
        NewPullRequest.NotifyCommands();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operations.Cancel();
        _connection.Cancel();
        _branchPullRequest.Cancel();
        Git.PropertyChanged -= OnGitPropertyChanged;
        Git.BranchPullRequestRequested -= ShowBranchPullRequest;
        Git.CreatePullRequestInWorkbench = null;
        PullRequests.Dispose();
        WorkItems.Dispose();
        Pipelines.Dispose();
        Access.Dispose();
        NewPullRequest.Dispose();
    }
}
