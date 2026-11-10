using System.Globalization;

namespace WpfStudio.Core.Git;

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
    public int AvatarIndex => GitText.AvatarIndex(Author);
    public bool IsMerge => ParentCount > 1;
    /// <summary>Branch and tag names decorating this commit; the checked-out branch is marked with HEAD.</summary>
    public IReadOnlyList<GitRef> RefList => Refs.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(GitRef.Parse).ToArray();
}

public sealed record GitRef(string Name, bool IsHead, bool IsTag, bool IsRemote)
{
    /// <summary>
    /// One decoration as <c>git log</c> prints it. History asks for full names (<c>--decorate=full</c>), which say what each is. A short
    /// name does not: the local branch <c>feature/login</c> and the remote <c>origin/main</c> look alike, so a short name with a slash
    /// can only be guessed to be remote.
    /// </summary>
    public static GitRef Parse(string text)
    {
        if (text.StartsWith("HEAD -> ", StringComparison.Ordinal)) return Parse(text[8..]) with { IsHead = true };
        if (text.StartsWith("tag: ", StringComparison.Ordinal)) return new(Strip(text[5..], "refs/tags/") ?? text[5..], false, true, false);
        if (Strip(text, "refs/heads/") is { } local) return new(local, false, false, false);
        if (Strip(text, "refs/remotes/") is { } remote) return new(remote, false, false, true);
        if (Strip(text, "refs/tags/") is { } tag) return new(tag, false, true, false);
        if (text.StartsWith("refs/", StringComparison.Ordinal)) return new(text[5..], false, false, false);
        return new(text, text == "HEAD", false, text.Contains('/'));
        static string? Strip(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : null;
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

public static class GitText
{
    /// <summary>How many avatar colours the Git pane offers.</summary>
    public const int AvatarCount = 8;

    /// <summary>A stable colour slot per author name (string.GetHashCode is randomised per process, so hash by hand).</summary>
    public static int AvatarIndex(string author)
    {
        var hash = 17u;
        foreach (var c in author.Trim().ToLowerInvariant()) hash = hash * 31 + c;
        return (int)(hash % AvatarCount);
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

/// <summary>Git's machine-readable output, parsed without running Git.</summary>
public static class GitOutput
{
    // The -z porcelain format emits destination before source for renamed/copied paths.
    public static IReadOnlyList<GitChange> ParseStatus(string status)
    {
        var fields = status.Split('\0');
        var result = new List<GitChange>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length == 0) continue;
            if (field.Length < 4 || field[2] != ' ') throw new InvalidOperationException("Git returned an invalid status record.");
            string? original = null;
            if (field[0] is 'R' or 'C' || field[1] is 'R' or 'C')
            {
                if (++index >= fields.Length || fields[index].Length == 0) throw new InvalidOperationException("Git returned an incomplete rename record.");
                original = fields[index];
            }
            result.Add(new(field[3..], original, field[0], field[1]));
        }
        return result;
    }

    /// <summary>Joins diff-tree's -z name-status and numstat records. Binary files have no line counts.</summary>
    public static IReadOnlyList<GitCommitFile> ParseCommitFiles(string nameStatus, string numstat)
    {
        var counts = new Dictionary<string, (int? Added, int? Deleted)>(StringComparer.Ordinal);
        var records = numstat.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var parts = records[index].Split('\t', 3);
            if (parts.Length < 3) continue;
            var path = parts[2];
            // Renames and copies leave the path empty and follow with the source and destination records.
            if (path.Length == 0)
            {
                index += 2;
                if (index >= records.Length) break;
                path = records[index];
            }
            counts[path] = (int.TryParse(parts[0], out var added) ? added : null, int.TryParse(parts[1], out var deleted) ? deleted : null);
        }
        var fields = nameStatus.Split('\0');
        var files = new List<GitCommitFile>();
        for (var index = 0; index < fields.Length; index++)
        {
            var status = fields[index];
            if (status.Length == 0) continue;
            if (++index >= fields.Length) break;
            string? original = null;
            var path = fields[index];
            if (status[0] is 'R' or 'C')
            {
                original = path;
                if (++index >= fields.Length) break;
                path = fields[index];
            }
            var (added, deleted) = counts.TryGetValue(path, out var count) ? count : (null, null);
            files.Add(new(path, original, status[0], added, deleted));
        }
        return files;
    }

    /// <summary>Recognizes the messages Git and credential helpers print when stored credentials are missing or rejected.</summary>
    public static bool IsAuthenticationFailure(string message) =>
        new[] { "Authentication failed", "could not read Username", "could not read Password", "terminal prompts disabled", "Logon failed",
                "returned error: 401", "returned error: 403", "HTTP 401", "HTTP 403", "Permission denied (publickey)", "TF401019",
                "fatal: credential", "Unauthorized" }
            .Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses the NUL-separated history format: id, short id, author, date, subject, decorations, parents.</summary>
    public static IReadOnlyList<GitCommit> ParseHistory(string text)
    {
        var fields = text.Split('\0');
        var commits = new List<GitCommit>();
        for (var i = 0; i + 6 < fields.Length; i += 7)
        {
            var parents = fields[i + 6].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            commits.Add(new(fields[i].TrimStart('\r', '\n'), fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5], parents));
        }
        return commits;
    }
}
