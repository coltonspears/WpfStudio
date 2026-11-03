using System.Globalization;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

public sealed record GitCommitDetails(string Id, IReadOnlyList<string> Parents, string Author, string AuthorEmail, string AuthorDate,
    string Committer, string CommitDate, string Message, IReadOnlyList<GitCommitFile> Files)
{
    public string ShortId => Id[..Math.Min(10, Id.Length)];
    public string Subject => Message.Split('\n', 2)[0].TrimEnd('\r');
    public string Body => Message.Contains('\n') ? Message.Split('\n', 2)[1].Trim() : "";
    public bool HasLongBody => Body.Length > 160 || Body.Count(c => c == '\n') >= 2;
    public string DisplayDate => DateTimeOffset.TryParse(AuthorDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? $"{date.LocalDateTime:dddd, MMM d, yyyy · HH:mm} ({GitText.Relative(date)})" : AuthorDate;
    public string ParentText => Parents.Count == 0 ? "Root commit" : (Parents.Count > 1 ? "Merge of " : "Parent ") + string.Join(", ", Parents.Select(p => p[..Math.Min(8, p.Length)]));
    public string Initials => GitText.Initials(Author);
    public int AvatarIndex => GitText.AvatarIndex(Author);
    public int Added => Files.Sum(file => file.Added ?? 0);
    public int Deleted => Files.Sum(file => file.Deleted ?? 0);
    public string Stats => $"{Files.Count} file{(Files.Count == 1 ? "" : "s")} changed" + (Added > 0 ? $" · +{Added}" : "") + (Deleted > 0 ? $" −{Deleted}" : "");
}

/// <summary>
/// A diff to display. <see cref="Text"/> is Git's patch; <see cref="Before"/>/<see cref="After"/> hold both file
/// versions for the side-by-side view (null when that side does not exist). <see cref="Message"/> replaces the
/// view for binary or oversized content.
/// </summary>
public sealed record GitDiff(string Title, string Path, string Text, string? Before = null, string? After = null,
    string? OldLabel = null, string? NewLabel = null, string? Message = null)
{
    public bool HasVersions => Before is not null || After is not null;
}

public sealed record GitSnapshot(string? Root, string Branch, string? Upstream, string Tracking,
    IReadOnlyList<GitChange> Changes, IReadOnlyList<string> Branches, IReadOnlyList<GitCommit> History, string Message,
    WpfStudio.Core.Git.GitRemote? Remote = null, GitOperation Operation = GitOperation.None)
{
    public static GitSnapshot Unavailable(string message) => new(null, "No repository", null, "", [], [], [], message);
}

/// <summary>A multi-step Git operation that stopped for conflicts and must be continued or aborted.</summary>
public enum GitOperation { None, Merge, Rebase, CherryPick, Revert }

/// <summary>
/// A file's repository, its path relative to the root, and its committed text. <see cref="IsTracked"/> means the path
/// exists in HEAD; <see cref="HeadText"/> is then null only for binary or oversized files.
/// </summary>
public sealed record GitFileBase(string Root, string RelativePath, string? HeadText, bool IsTracked, string? HeadCommit, WpfStudio.Core.Git.GitRemote? Remote);

/// <summary>A conflicted file's current text and the conflict blocks found in it.</summary>
public sealed record GitConflictFile(string Root, string RelativePath, string FullPath, string Text, IReadOnlyList<WpfStudio.Core.Git.ConflictBlock> Blocks);
