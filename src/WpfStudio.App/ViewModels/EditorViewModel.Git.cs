using WpfStudio.App.Features.Git;
using WpfStudio.Core.Git;

namespace WpfStudio.App.ViewModels;

/// <summary>Git state for one editor: changes against the last commit and per-line blame of the current text.</summary>
public sealed partial class EditorViewModel
{
    private GitService? _git;
    private CancellationTokenSource? _gitChanges, _gitBlame;
    private long _gitBaseRevision;

    /// <summary>The committed version of this file; null outside a repository or before it is read.</summary>
    public GitFileBase? GitBase { get; private set; }
    public IReadOnlyList<LineChange> LineChanges { get; private set; } = [];
    /// <summary>Blame for <see cref="BlameVersion"/>; empty while not computed or when inline blame is off.</summary>
    public IReadOnlyList<BlameLine> Blame { get; private set; } = [];
    public long BlameVersion { get; private set; } = -1;
    public event Action? GitChanged;
    public event Action<BlameLine>? BlameCommitRequested;
    public bool ShowInlineBlame
    {
        get => _showInlineBlame;
        set { if (_showInlineBlame == value) return; _showInlineBlame = value; OnPropertyChanged(); if (value) ScheduleBlame(); else { Blame = []; GitChanged?.Invoke(); } }
    }
    private bool _showInlineBlame = true;

    /// <summary>Connects the editor to Git and reads the committed text; call again after commits, checkouts and saves.</summary>
    public async Task RefreshGitAsync(GitService git, CancellationToken token = default)
    {
        _git = git;
        if (_disposed || IsReadOnly || !System.IO.File.Exists(State.Path)) return;
        var revision = ++_gitBaseRevision;
        try
        {
            var gitBase = await git.GetFileBaseAsync(State.Path, token);
            if (_disposed || revision != _gitBaseRevision) return;
            GitBase = gitBase;
            ScheduleGitChanges(immediate: true);
            ScheduleBlame();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { GitBase = null; LineChanges = []; Blame = []; GitChanged?.Invoke(); }
    }

    public void RequestBlameCommit(BlameLine line) => BlameCommitRequested?.Invoke(line);

    // Called on every content change: changes are cheap and follow typing; blame runs Git and waits for a pause.
    private void OnGitContentChanged()
    {
        if (GitBase is null) return;
        ScheduleGitChanges(immediate: false);
        ScheduleBlame();
    }

    private void ScheduleGitChanges(bool immediate)
    {
        _gitChanges?.Cancel(); _gitChanges?.Dispose();
        _gitChanges = new();
        var token = _gitChanges.Token;
        var gitBase = GitBase;
        var text = State.Content;
        _ = Task.Run(async () =>
        {
            if (!immediate) await Task.Delay(250, token);
            var changes = gitBase is null ? [] : gitBase.IsTracked && gitBase.HeadText is null && gitBase.HeadCommit is not null
                ? [] // Binary or oversized committed file: no comparison.
                : WpfStudio.Core.Git.LineChanges.Compute(gitBase.IsTracked ? gitBase.HeadText ?? "" : null, text);
            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed || !ReferenceEquals(gitBase, GitBase) || text != State.Content) return;
                LineChanges = changes;
                GitChanged?.Invoke();
            }, token);
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void ScheduleBlame()
    {
        _gitBlame?.Cancel(); _gitBlame?.Dispose(); _gitBlame = null;
        if (!_showInlineBlame || _git is not { } git || GitBase is not { IsTracked: true, HeadCommit: not null } gitBase) return;
        _gitBlame = new();
        var token = _gitBlame.Token;
        var text = State.Content;
        var version = State.Version;
        _ = Task.Run(async () =>
        {
            await Task.Delay(BlameVersion < 0 ? 0 : 1200, token); // Blame the first time at once, then after typing pauses.
            var blame = await git.BlameAsync(gitBase.Root, gitBase.RelativePath, text, token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed || version != State.Version || !ReferenceEquals(gitBase, GitBase)) return;
                Blame = blame; BlameVersion = version;
                GitChanged?.Invoke();
            }, token);
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void DisposeGit()
    {
        _gitChanges?.Cancel(); _gitBlame?.Cancel();
    }
}
