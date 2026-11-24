using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.App.Features.Git;
using WpfStudio.Contracts;
using WpfStudio.Core.Git;
using WpfStudio.Core.Threading;

namespace WpfStudio.App.ViewModels;

/// <summary>Git state for one editor: changes against the last commit and per-line blame of the current text.</summary>
public sealed partial class EditorViewModel
{
    private GitService? _git;
    private readonly SerialDebouncedWork _gitChanges, _gitBlame;
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
    /// <param name="pass">Shared by the editors refreshed together, so each repository is asked about itself once.</param>
    public async Task RefreshGitAsync(GitService git, CancellationToken token = default, GitService.FileBasePass? pass = null)
    {
        _git = git;
        if (_disposed || IsReadOnly || !System.IO.File.Exists(State.Path)) return;
        var revision = ++_gitBaseRevision;
        try
        {
            var gitBase = await git.GetFileBaseAsync(State.Path, token, pass, GitBase);
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
        var gitBase = GitBase;
        var text = State.Content;
        _gitChanges.ScheduleBackgroundAsync(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250), async token =>
        {
            var changes = gitBase is null ? [] : gitBase.IsTracked && gitBase.HeadText is null && gitBase.HeadCommit is not null
                ? [] // Binary or oversized committed file: no comparison.
                : WpfStudio.Core.Git.LineChanges.Compute(gitBase.IsTracked ? gitBase.HeadText ?? "" : null, text);
            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed || !ReferenceEquals(gitBase, GitBase) || text != State.Content) return;
                LineChanges = changes;
                GitChanged?.Invoke();
            }, token);
        }).Forget();
    }

    private void ScheduleBlame()
    {
        _gitBlame.Cancel();
        // Blame is shown above C# declarations only.
        if (!ShowInlineBlame || !IsCSharp || _git is not { } git || GitBase is not { IsTracked: true, HeadCommit: not null } gitBase)
        {
            if (Blame.Count > 0 || BlameVersion >= 0)
            {
                Blame = [];
                BlameVersion = -1;
                GitChanged?.Invoke();
            }
            return;
        }
        var text = State.Content;
        var version = State.Version;
        _gitBlame.ScheduleBackgroundAsync(BlameVersion < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1200), async token =>
        {
            var blame = await git.BlameAsync(gitBase.Root, gitBase.RelativePath, text, token, State.Encoding);
            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed || version != State.Version || !ReferenceEquals(gitBase, GitBase)) return;
                Blame = blame; BlameVersion = version;
                GitChanged?.Invoke();
            }, token);
        }).Forget();
    }

    private void DisposeGit()
    {
        _gitChanges.Dispose();
        _gitBlame.Dispose();
    }
}
