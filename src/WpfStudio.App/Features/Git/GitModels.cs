namespace WpfStudio.App.Features.Git;

public sealed record GitChange(string Path, string? OriginalPath, char IndexStatus, char WorkTreeStatus)
{
    public bool IsStaged => IndexStatus is not (' ' or '?' or '!');
    public bool IsUnstaged => WorkTreeStatus is not (' ' or '!') || IndexStatus == '?';
    public bool IsConflict => IndexStatus == 'U' || WorkTreeStatus == 'U' ||
                              (IndexStatus == 'A' && WorkTreeStatus == 'A') ||
                              (IndexStatus == 'D' && WorkTreeStatus == 'D');
    public string DisplayPath => OriginalPath is null ? Path : $"{OriginalPath} → {Path}";
    public string Status => IsConflict ? "Conflict" : IndexStatus == '?' ? "New" :
        IndexStatus == 'R' || WorkTreeStatus == 'R' ? "Renamed" :
        IndexStatus == 'D' || WorkTreeStatus == 'D' ? "Deleted" :
        IndexStatus == 'A' ? "Added" : "Modified";
}

public sealed record GitCommit(string Id, string ShortId, string Author, string Date, string Subject)
{
    public string DisplayDate => DateTimeOffset.TryParse(Date, out var date) ? date.LocalDateTime.ToString("MMM d, yyyy · HH:mm") : Date;
}
public sealed record GitDiff(string Title, string Path, string Text);
public sealed record GitSnapshot(string? Root, string Branch, string? Upstream, string Tracking,
    IReadOnlyList<GitChange> Changes, IReadOnlyList<string> Branches, IReadOnlyList<GitCommit> History, string Message)
{
    public static GitSnapshot Unavailable(string message) => new(null, "No repository", null, "", [], [], [], message);
}
