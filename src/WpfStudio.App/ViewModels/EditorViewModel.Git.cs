using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.App.Features.Git;
using WpfStudio.Contracts;
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
    /// <summary>Blame for <see cref="BlameVersion"/>; empty while not computed, when Git history is hidden, or outside C#.</summary>
    public IReadOnlyList<BlameLine> Blame { get; private set; } = [];
    public long BlameVersion { get; private set; } = -1;
    public event Action? GitChanged;
    public event Action<BlameLine>? BlameCommitRequested;
    [ObservableProperty] public partial bool ShowInlineBlame { get; set; } = true;
    partial void OnShowInlineBlameChanged(bool value)
    {
        if (value) ScheduleBlame();
        else { Blame = []; GitChanged?.Invoke(); }
        UpdateLensRows();
    }

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
            // Every save, window activation and Git pane action asks again for every open file, mostly with nothing changed.
            // The margin and blame follow from the committed text and the buffer, and the buffer's own edits already refresh them.
            if (gitBase == GitBase) return;
            GitBase = gitBase;
            ScheduleGitChanges(immediate: true);
            ScheduleBlame();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A newer refresh may already have succeeded; an older failure must not wipe its result.
            if (_disposed || revision != _gitBaseRevision) return;
            GitBase = null; LineChanges = []; Blame = []; GitChanged?.Invoke();
        }
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
        Task.Run(async () =>
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
        }, token).Forget();
    }

    private void ScheduleBlame()
    {
        _gitBlame?.Cancel(); _gitBlame?.Dispose(); _gitBlame = null;
        // Blame is shown above C# declarations only.
        if (!ShowInlineBlame || !IsCSharp || _git is not { } git || GitBase is not { IsTracked: true, HeadCommit: not null } gitBase) return;
        _gitBlame = new();
        var token = _gitBlame.Token;
        var text = State.Content;
        var version = State.Version;
        Task.Run(async () =>
        {
            await Task.Delay(BlameVersion < 0 ? 0 : 1200, token); // Blame the first time at once, then after typing pauses.
            var blame = await git.BlameAsync(gitBase.Root, gitBase.RelativePath, text, token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed || version != State.Version || !ReferenceEquals(gitBase, GitBase)) return;
                Blame = blame; BlameVersion = version;
                GitChanged?.Invoke();
            }, token);
        }, token).Forget();
    }

    private void DisposeGit()
    {
        _gitChanges?.Cancel(); _gitChanges?.Dispose(); _gitChanges = null;
        _gitBlame?.Cancel(); _gitBlame?.Dispose(); _gitBlame = null;
    }
}
