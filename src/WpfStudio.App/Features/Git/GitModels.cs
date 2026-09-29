using System.Globalization;

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
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => GitText.Folder(Path) + (OriginalPath is null ? "" : $"  ← {OriginalPath}");
    /// <summary>One-letter status for the list badge: M, A, D, R, N (new, untracked) or ! (conflict).</summary>
    public string Letter => IsConflict ? "!" : IndexStatus == '?' ? "N" : Status[..1];
}

public sealed record GitCommit(string Id, string ShortId, string Author, string Date, string Subject, string Refs = "", int ParentCount = 1)
{
    public string DisplayDate => DateTimeOffset.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.LocalDateTime.ToString("MMM d, yyyy · HH:mm") : Date;
    public string RelativeDate => DateTimeOffset.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? GitText.Relative(date) : Date;
    public string Initials => GitText.Initials(Author);
    public System.Windows.Media.Brush AvatarBrush => GitText.Avatar(Author);
    public bool IsMerge => ParentCount > 1;
    /// <summary>Branch and tag names decorating this commit; the checked-out branch is marked with HEAD.</summary>
    public IReadOnlyList<GitRef> RefList => Refs.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(GitRef.Parse).ToArray();
}

public sealed record GitRef(string Name, bool IsHead, bool IsTag, bool IsRemote)
{
    public static GitRef Parse(string text)
    {
        if (text.StartsWith("HEAD -> ", StringComparison.Ordinal)) return new(text[8..], true, false, false);
        if (text.StartsWith("tag: ", StringComparison.Ordinal)) return new(text[5..], false, true, false);
        return new(text, text == "HEAD", false, text.Contains('/'));
    }
}

/// <summary>A file changed by a commit, relative to its first parent. Binary files have no line counts.</summary>
public sealed record GitCommitFile(string Path, string? OriginalPath, char Status, int? Added, int? Deleted)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => GitText.Folder(Path) + (OriginalPath is null ? "" : $"  ← {OriginalPath}");
    public string Letter => Status.ToString();
    public bool IsBinary => Added is null;
    public string AddedText => Added is > 0 ? $"+{Added}" : IsBinary ? "binary" : "";
    public string RemovedText => Deleted is > 0 ? $"−{Deleted}" : "";
}

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
    public System.Windows.Media.Brush AvatarBrush => GitText.Avatar(Author);
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
    IReadOnlyList<GitChange> Changes, IReadOnlyList<string> Branches, IReadOnlyList<GitCommit> History, string Message)
{
    public static GitSnapshot Unavailable(string message) => new(null, "No repository", null, "", [], [], [], message);
}

internal static class GitText
{
    // Mid-tone avatar colours that keep white initials readable in both themes.
    private static readonly System.Windows.Media.Brush[] Avatars = new[]
    {
        "#5B7FE0", "#D46F4D", "#3F9E78", "#9A6BD6", "#C08A2E", "#3B98AD", "#C95A8A", "#7A8A45"
    }.Select(hex =>
    {
        var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return (System.Windows.Media.Brush)brush;
    }).ToArray();

    /// <summary>A stable colour per author name (string.GetHashCode is randomised per process, so hash by hand).</summary>
    public static System.Windows.Media.Brush Avatar(string author)
    {
        var hash = 17u;
        foreach (var c in author.Trim().ToLowerInvariant()) hash = hash * 31 + c;
        return Avatars[hash % (uint)Avatars.Length];
    }

    public static string Folder(string path)
    {
        // Files at the repository root show no folder.
        return System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
    }

    public static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0]))
        };
    }

    public static string Relative(DateTimeOffset date)
    {
        var age = DateTimeOffset.Now - date;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(2)) return "yesterday";
        if (age < TimeSpan.FromDays(30)) return $"{(int)age.TotalDays} days ago";
        return date.LocalDateTime.ToString(age < TimeSpan.FromDays(365) ? "MMM d" : "MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
