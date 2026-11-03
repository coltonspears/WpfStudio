using System.Windows.Input;
using WpfStudio.App.Services;
using WpfStudio.Core.Git;

namespace WpfStudio.App.ViewModels;

/// <summary>One row of the history popup. The uncommitted-lines row has no commit, so nothing to show or copy.</summary>
public sealed record BlameHistoryRow(string Title, string Detail, BlameLine? Commit = null, ICommand? ShowChangeCommand = null);

/// <summary>The commits behind a declaration, newest first, each opening its change to this file in Git history.</summary>
public sealed record BlameHistoryCard(string Name, string Scope, IReadOnlyList<BlameHistoryRow> Rows, string? OlderCommits, ICommand CloseCommand)
{
    private const int MaximumCommits = 12;

    public static BlameHistoryCard Create(string name, int firstLine, int lastLine, BlameHistory history, DateTimeOffset now, ICommand showChange, ICommand close)
    {
        var rows = new List<BlameHistoryRow>();
        if (history.UncommittedLines > 0)
            rows.Add(new("Uncommitted changes", $"{Count(history.UncommittedLines, "line")} not committed yet · use the change bars in the margin to compare or revert"));
        foreach (var change in history.Changes.Take(MaximumCommits))
        {
            var commit = change.Commit;
            rows.Add(new(commit.Summary.Length == 0 ? commit.ShortCommit : commit.Summary,
                $"{commit.Author} · {commit.Time.LocalDateTime:MMM d, yyyy} ({BlameLine.Relative(commit.Time, now)}) · {commit.ShortCommit} · {Count(change.Lines, "line")}", commit, showChange));
        }
        return new(name, $"Lines {firstLine}–{lastLine} · {Count(history.Changes.Count, "commit")} by {Count(history.Authors, "author")}", rows,
            history.Changes.Count > MaximumCommits ? $"{history.Changes.Count - MaximumCommits} older commits are in Git history." : null, close);
    }

    private static string Count(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";
}

/// <summary>A changed block from the Git margin with its committed text. Lines that are simply new have none to compare.</summary>
public sealed record LineChangeCard(string Heading, string? CommittedText, string RevertLabel, bool CanRevert, ICommand RevertCommand, ICommand CloseCommand)
{
    /// <summary>The committed text is shown in the editor's font.</summary>
    public EditorPreferences? Preferences { get; init; }
    public static LineChangeCard Create(LineChange change, bool canRevert, ICommand revert, ICommand close) => new(
        change.Kind switch { LineChangeKind.Added => "Added since the last commit", LineChangeKind.Deleted => "Deleted since the last commit", _ => "Changed since the last commit" },
        change.OriginalText.Length > 0 ? change.OriginalText : change.Kind != LineChangeKind.Added ? "(empty line)" : null,
        change.Kind == LineChangeKind.Added ? "Remove added lines" : "Revert change", canRevert, revert, close);
}
