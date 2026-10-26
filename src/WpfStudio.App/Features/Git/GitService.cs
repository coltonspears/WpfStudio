using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WpfStudio.App.Features.Git;

/// <summary>Small Git CLI boundary. It never invokes a shell and never discards working files.</summary>
public sealed class GitService
{
    private const int OutputLimit = 2 * 1024 * 1024;
    private const string HistoryFormat = "--format=%H%x00%h%x00%an%x00%aI%x00%s%x00%D%x00%P%x00";
    private const string BinaryMessage = "Binary file — there is no text to compare.";
    private const string TooLargeMessage = "This file is larger than 2 MiB. Open it in the editor or use the terminal to compare it.";

    /// <param name="historyLimit">How many recent commits to include; the pane asks for more as the reader pages.</param>
    public async Task<GitSnapshot> LoadAsync(string? workspacePath, CancellationToken cancellationToken = default, int historyLimit = 50)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return GitSnapshot.Unavailable("Open a solution or project to see its Git repository.");
        var directory = Directory.Exists(workspacePath) ? workspacePath : System.IO.Path.GetDirectoryName(workspacePath);
        if (directory is null || !Directory.Exists(directory)) return GitSnapshot.Unavailable("The workspace folder is no longer available.");
        GitResult discovery;
        try { discovery = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken, allowFailure: true); }
        catch (Win32Exception) { return GitSnapshot.Unavailable("Git was not found. Install Git for Windows, then restart WpfStudio."); }
        if (discovery.ExitCode != 0)
        {
            if (discovery.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                return GitSnapshot.Unavailable("This workspace is not in a Git repository. Initialize or clone a repository in the terminal, then refresh.");
            throw new InvalidOperationException(GitDiagnosticSanitizer.Redact(discovery.Error.Trim()));
        }
        var root = System.IO.Path.GetFullPath(discovery.Output.TrimEnd('\r', '\n'));
        var status = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        var branchResult = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowFailure: true);
        var branch = branchResult.ExitCode == 0 ? branchResult.Output.Trim() : "Detached HEAD";
        var branchList = await RunAsync(root, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], cancellationToken);
        var branches = branchList.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasHead = await HasHeadAsync(root, cancellationToken);
        GitCommit[] history = [];
        string? upstream = null;
        var tracking = hasHead ? "No upstream configured" : "No commits yet";
        if (hasHead)
        {
            var log = await RunAsync(root, ["log", $"-{Math.Max(1, historyLimit)}", "--no-show-signature", HistoryFormat], cancellationToken, readOnly: true);
            history = [.. ParseHistory(log.Output)];
            var upstreamResult = await RunAsync(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], cancellationToken, allowFailure: true);
            if (upstreamResult.ExitCode == 0)
            {
                upstream = upstreamResult.Output.Trim();
                var counts = await RunAsync(root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"], cancellationToken);
                var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                tracking = $"{upstream} · {parts[0]} ahead · {parts[1]} behind";
            }
        }
        var changes = ParseStatus(status.Output);
        var remote = await GetRemoteAsync(root, upstream, cancellationToken);
        var operation = await GetOperationAsync(root, cancellationToken);
        return new(root, branch, upstream, tracking, changes, branches, history,
            changes.Count == 0 ? "Working tree clean" : $"{changes.Count} changed file{(changes.Count == 1 ? "" : "s")}", remote, operation);
    }

    /// <summary>The web location of the upstream's remote, else origin, else the only remote.</summary>
    public async Task<WpfStudio.Core.Git.GitRemote?> GetRemoteAsync(string root, string? upstream, CancellationToken cancellationToken = default)
    {
        var names = (await RunAsync(root, ["remote"], cancellationToken, allowFailure: true, readOnly: true)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var preferred = upstream?.Split('/', 2)[0];
        var name = names.FirstOrDefault(n => n == preferred) ?? names.FirstOrDefault(n => n == "origin") ?? (names.Length == 1 ? names[0] : null);
        if (name is null) return null;
        var url = await RunAsync(root, ["remote", "get-url", "--", name], cancellationToken, allowFailure: true, readOnly: true);
        return url.ExitCode == 0 ? WpfStudio.Core.Git.GitRemote.Parse(url.Output.Trim()) : null;
    }

    public async Task<GitOperation> GetOperationAsync(string root, CancellationToken cancellationToken = default)
    {
        async Task<bool> Exists(string name)
        {
            var path = await RunAsync(root, ["rev-parse", "--git-path", name], cancellationToken, allowFailure: true, readOnly: true);
            if (path.ExitCode != 0) return false;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path.Output.Trim()));
            return File.Exists(full) || Directory.Exists(full);
        }
        if (await Exists("rebase-merge") || await Exists("rebase-apply")) return GitOperation.Rebase;
        if (await Exists("MERGE_HEAD")) return GitOperation.Merge;
        if (await Exists("CHERRY_PICK_HEAD")) return GitOperation.CherryPick;
        if (await Exists("REVERT_HEAD")) return GitOperation.Revert;
        return GitOperation.None;
    }

    /// <summary>The committed version of a working file for the editor's change margin and blame; null outside a repository.</summary>
    public async Task<GitFileBase?> GetFileBaseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
        if (directory is null || !Directory.Exists(directory)) return null;
        GitResult discovery;
        try { discovery = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken, allowFailure: true, readOnly: true); }
        catch (Win32Exception) { return null; }
        if (discovery.ExitCode != 0) return null;
        var root = System.IO.Path.GetFullPath(discovery.Output.TrimEnd('\r', '\n'));
        var relative = System.IO.Path.GetRelativePath(root, System.IO.Path.GetFullPath(filePath)).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal)) return null;
        var head = await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken, allowFailure: true, readOnly: true);
        // Committed means present in HEAD. A newly staged file is in the index only, so all its lines are additions.
        var tracked = head.ExitCode == 0 && (await RunAsync(root, ["cat-file", "-e", $"HEAD:{relative}"], cancellationToken, allowFailure: true, readOnly: true)).ExitCode == 0;
        string? text = null;
        if (tracked && head.ExitCode == 0)
        {
            try { text = await ReadBlobAsync(root, $"HEAD:{relative}", cancellationToken); }
            catch (GitOutputLimitException) { text = null; }
            if (IsBinary(text)) text = null;
        }
        var upstream = await RunAsync(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], cancellationToken, allowFailure: true, readOnly: true);
        var remote = await GetRemoteAsync(root, upstream.ExitCode == 0 ? upstream.Output.Trim() : null, cancellationToken);
        return new(root, relative, text, tracked, head.ExitCode == 0 ? head.Output.Trim() : null, remote);
    }

    /// <summary>Blame for the editor's current (possibly unsaved) text, so line numbers match the buffer.</summary>
    public async Task<IReadOnlyList<WpfStudio.Core.Git.BlameLine>> BlameAsync(string root, string relativePath, string contents, CancellationToken cancellationToken = default)
    {
        ValidatePaths(root, [relativePath]);
        var result = await RunAsync(root, ["blame", "--porcelain", "--contents", "-", "--", relativePath], cancellationToken, input: contents, readOnly: true);
        return WpfStudio.Core.Git.GitBlame.Parse(result.Output);
    }

    /// <summary>Merges a branch into the current one. Conflicts are not an error: they are left for the resolver.</summary>
    public async Task<string> MergeAsync(string root, string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-')) throw new InvalidOperationException("Choose a branch to merge.");
        await RequireCleanAsync(root, cancellationToken);
        var result = await RunAsync(root, ["merge", "--no-edit", "--no-ff", "--", branch], cancellationToken, allowFailure: true);
        if (result.ExitCode == 0) return result.Display;
        if (await GetOperationAsync(root, cancellationToken) == GitOperation.Merge)
            return result.Display + "\nResolve the conflicts, then choose Continue. Abort returns the branch to where it was.";
        throw new InvalidOperationException(result.Display);
    }

    public async Task<string> AbortOperationAsync(string root, CancellationToken cancellationToken = default)
    {
        var operation = await GetOperationAsync(root, cancellationToken);
        string[] arguments = operation switch
        {
            GitOperation.Merge => ["merge", "--abort"], GitOperation.Rebase => ["rebase", "--abort"],
            GitOperation.CherryPick => ["cherry-pick", "--abort"], GitOperation.Revert => ["revert", "--abort"],
            _ => throw new InvalidOperationException("No merge, rebase, cherry-pick or revert is in progress.")
        };
        return (await RunAsync(root, arguments, cancellationToken)).Display;
    }

    /// <summary>Commits a resolved merge, or continues a rebase, cherry-pick or revert, keeping Git's prepared message.</summary>
    public async Task<string> ContinueOperationAsync(string root, CancellationToken cancellationToken = default)
    {
        var status = ParseStatus((await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=no"], cancellationToken)).Output);
        if (status.Any(change => change.IsConflict)) throw new InvalidOperationException("Resolve and stage every conflicted file first.");
        var operation = await GetOperationAsync(root, cancellationToken);
        // GIT_EDITOR=":" (set by noEditor) is Git's own "accept the prepared message" editor on every platform.
        string[] arguments = operation switch
        {
            GitOperation.Merge => ["commit", "--no-edit"],
            GitOperation.Rebase => ["rebase", "--continue"],
            GitOperation.CherryPick => ["cherry-pick", "--continue"],
            GitOperation.Revert => ["revert", "--continue"],
            _ => throw new InvalidOperationException("No merge, rebase, cherry-pick or revert is in progress.")
        };
        var result = await RunAsync(root, arguments, cancellationToken, allowFailure: true, noEditor: true);
        if (result.ExitCode != 0 && await GetOperationAsync(root, cancellationToken) == GitOperation.None) throw new InvalidOperationException(result.Display);
        return result.Display;
    }

    public async Task<GitConflictFile> ReadConflictAsync(string root, GitChange change, CancellationToken cancellationToken = default)
    {
        ValidatePaths(root, [change.Path]);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, change.Path));
        var file = await ReadWorkingFileAsync(path, cancellationToken);
        if (file.Text is null) throw new InvalidOperationException(file.Message ?? "The conflicted file was deleted on one side. Choose a side in the terminal (git add or git rm).");
        return new(root, change.Path, path, file.Text, WpfStudio.Core.Git.MergeConflicts.Parse(file.Text));
    }

    /// <summary>Writes the resolved text and stages it, unless the file changed since it was read.</summary>
    public async Task<string> ResolveConflictAsync(GitConflictFile conflict, string resolved, CancellationToken cancellationToken = default)
    {
        ValidatePaths(conflict.Root, [conflict.RelativePath]);
        var current = await ReadWorkingFileAsync(conflict.FullPath, cancellationToken);
        if (current.Text != conflict.Text) throw new InvalidOperationException("The file changed since the conflicts were loaded. Reload it and resolve again.");
        if (WpfStudio.Core.Git.MergeConflicts.Parse(resolved).Count > 0) throw new InvalidOperationException("The result still contains conflict markers.");
        var bytes = await File.ReadAllBytesAsync(conflict.FullPath, cancellationToken);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        await File.WriteAllTextAsync(conflict.FullPath, resolved, new UTF8Encoding(bom), cancellationToken);
        return (await RunAsync(conflict.Root, ["add", "--", conflict.RelativePath], cancellationToken)).Display;
    }

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

    public async Task<string> StageAsync(string root, GitChange? change, CancellationToken cancellationToken = default)
    {
        var paths = change is null ? ["."] : ChangePaths(root, change);
        return (await RunAsync(root, ["add", "--all", "--", .. paths], cancellationToken)).Display;
    }

    public async Task<string> UnstageAsync(string root, GitChange? change, CancellationToken cancellationToken = default)
    {
        var paths = change is null ? ["."] : ChangePaths(root, change);
        // In an unborn repository there is no HEAD to restore from. --cached changes only the index.
        var arguments = await HasHeadAsync(root, cancellationToken)
            ? new[] { "restore", "--staged", "--" }.Concat(paths).ToArray()
            : new[] { "rm", "--cached", "--force", "-r", "--" }.Concat(paths).ToArray();
        return (await RunAsync(root, arguments, cancellationToken)).Display;
    }

    public async Task<string> CommitAsync(string root, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Write a commit message first.");
        var status = ParseStatus((await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken)).Output);
        if (status.Any(x => x.IsConflict)) throw new InvalidOperationException("Resolve all merge conflicts before committing.");
        if (!status.Any(x => x.IsStaged)) throw new InvalidOperationException("Stage at least one change before committing.");
        return (await RunAsync(root, ["commit", "--file=-"], cancellationToken, input: message)).Display;
    }

    /// <summary>
    /// Reads a working-tree or staged change: Git's patch plus both file versions for the side-by-side view.
    /// Staged compares HEAD with the index; working compares the index (or HEAD for conflicts) with the file on disk.
    /// </summary>
    public async Task<GitDiff> GetDiffAsync(string root, GitChange change, bool staged, CancellationToken cancellationToken = default)
    {
        var paths = ChangePaths(root, change);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, change.Path));
        if (!staged && change.IndexStatus == '?')
        {
            var file = await ReadWorkingFileAsync(path, cancellationToken);
            return new($"New file · {change.Path}", path, file.Text ?? file.Message ?? "", null, file.Text,
                "Not tracked", "Working tree", file.Message);
        }
        var result = await RunAsync(root, ["diff", "--no-ext-diff", "--no-textconv", "--unified=3", .. (staged ? new[] { "--cached" } : []), "--", .. paths], cancellationToken, readOnly: true);
        var patch = string.IsNullOrWhiteSpace(result.Output) ? "No text differences. File metadata or submodule state may have changed." : result.Output;
        string? before = null, after = null, message = null;
        // A conflicted path has no single staged version, so both lists compare HEAD with the file and its markers.
        var oldLabel = staged && !change.IsConflict ? "Last commit (HEAD)" : change.IsConflict || !change.IsStaged ? "Last commit (HEAD)" : "Staged";
        var newLabel = change.IsConflict ? "Working tree (conflicted)" : staged ? "Staged" : "Working tree";
        try
        {
            if (staged && !change.IsConflict)
            {
                if (change.IndexStatus is not ('A' or 'C')) before = await ReadBlobAsync(root, $"HEAD:{change.OriginalPath ?? change.Path}", cancellationToken);
                if (change.IndexStatus != 'D') after = await ReadBlobAsync(root, $":0:{change.Path}", cancellationToken);
            }
            else
            {
                before = await ReadBlobAsync(root, change.IsConflict ? $"HEAD:{change.Path}" : $":0:{change.Path}", cancellationToken);
                if (change.WorkTreeStatus != 'D' || change.IsConflict)
                {
                    var file = await ReadWorkingFileAsync(path, cancellationToken);
                    after = file.Text;
                    message = file.Message;
                }
            }
        }
        catch (GitOutputLimitException) { message = TooLargeMessage; }
        if (message is null && (IsBinary(before) || IsBinary(after))) message = BinaryMessage;
        if (message is null && before is null && after is null) message = "There is no text to compare for this change. It may be a submodule or a special file.";
        if (message is not null) before = after = null;
        return new($"{(staged ? "Staged" : "Working tree")} · {change.Path}", path, patch, before, after, oldLabel, newLabel, message);
    }

    /// <summary>The whole patch of a commit as Git shows it.</summary>
    public async Task<GitDiff> GetCommitDiffAsync(string root, string id, CancellationToken cancellationToken = default)
    {
        RequireCommitId(id);
        var result = await RunAsync(root, ["show", "--no-ext-diff", "--no-textconv", "--no-show-signature", "--format=fuller", "--stat", "--patch", id, "--"], cancellationToken, readOnly: true);
        return new($"Commit · {id[..8]}", root, result.Output);
    }

    /// <summary>Loads older commits for paging through history.</summary>
    public async Task<IReadOnlyList<GitCommit>> LoadHistoryAsync(string root, int skip, int count, CancellationToken cancellationToken = default)
    {
        var log = await RunAsync(root, ["log", $"--skip={Math.Max(0, skip)}", $"-{Math.Max(1, count)}", "--no-show-signature", HistoryFormat], cancellationToken, readOnly: true);
        return ParseHistory(log.Output);
    }

    /// <summary>Reads a commit's full message, authorship and the files it changed relative to its first parent.</summary>
    public async Task<GitCommitDetails> GetCommitDetailsAsync(string root, string id, CancellationToken cancellationToken = default)
    {
        RequireCommitId(id);
        var header = await RunAsync(root, ["show", "-s", "--no-show-signature", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%cI%x00%B", id, "--"], cancellationToken, readOnly: true);
        var fields = header.Output.Split('\0');
        if (fields.Length < 8) throw new InvalidOperationException("Git returned an invalid commit record.");
        var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A merge is shown against its first parent, like most code review tools; a root commit against the empty tree.
        string[] range = parents.Length == 0 ? ["--root", id] : [parents[0], id];
        var names = await RunAsync(root, ["diff-tree", "-r", "-M", "-z", "--no-commit-id", "--no-ext-diff", "--name-status", .. range, "--"], cancellationToken, readOnly: true);
        var counts = await RunAsync(root, ["diff-tree", "-r", "-M", "-z", "--no-commit-id", "--no-ext-diff", "--numstat", .. range, "--"], cancellationToken, readOnly: true);
        return new(fields[0].Trim(), parents, fields[2], fields[3], fields[4], fields[5], fields[6], fields[7].TrimEnd('\r', '\n'),
            ParseCommitFiles(names.Output, counts.Output));
    }

    /// <summary>Both versions of one file in a commit, compared with the commit's first parent.</summary>
    public async Task<GitDiff> GetCommitFileDiffAsync(string root, GitCommitDetails commit, GitCommitFile file, CancellationToken cancellationToken = default)
    {
        RequireCommitId(commit.Id);
        ValidatePaths(root, file.OriginalPath is null ? new[] { file.Path } : [file.Path, file.OriginalPath]);
        var parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        string? before = null, after = null, message = file.IsBinary ? BinaryMessage : null;
        if (message is null)
        {
            try
            {
                if (parent is not null && file.Status != 'A') before = await ReadBlobAsync(root, $"{parent}:{file.OriginalPath ?? file.Path}", cancellationToken);
                if (file.Status != 'D') after = await ReadBlobAsync(root, $"{commit.Id}:{file.Path}", cancellationToken);
            }
            catch (GitOutputLimitException) { message = TooLargeMessage; }
            if (message is null && (IsBinary(before) || IsBinary(after))) message = BinaryMessage;
            if (message is null && before is null && after is null) message = "There is no text to compare for this entry. It may be a submodule or a special file.";
            if (message is not null) before = after = null;
        }
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, file.Path));
        return new($"{commit.ShortId} · {file.Path}", path, "", before, after,
            parent is null ? "Empty (root commit)" : $"Parent {parent[..Math.Min(8, parent.Length)]}", $"Commit {commit.Id[..Math.Min(8, commit.Id.Length)]}", message);
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

    /// <param name="signIn">
    /// Lets Git Credential Manager show its sign-in window (Microsoft Entra for Azure DevOps, a browser for GitHub).
    /// Without it, network operations use only stored credentials and never prompt.
    /// </param>
    public async Task<string> FetchAsync(string root, CancellationToken cancellationToken = default, bool signIn = false) =>
        (await RunAsync(root, ["fetch"], cancellationToken, signIn: signIn)).Display;

    public async Task<string> PullAsync(string root, CancellationToken cancellationToken = default, bool signIn = false)
    {
        await RequireCleanAsync(root, cancellationToken);
        return (await RunAsync(root, ["pull", "--ff-only"], cancellationToken, signIn: signIn)).Display;
    }

    public async Task<string> PushAsync(string root, CancellationToken cancellationToken = default, bool signIn = false) =>
        (await RunAsync(root, ["push"], cancellationToken, signIn: signIn)).Display;

    /// <summary>Recognizes the messages Git and credential helpers print when stored credentials are missing or rejected.</summary>
    public static bool IsAuthenticationFailure(string message) =>
        new[] { "Authentication failed", "could not read Username", "could not read Password", "terminal prompts disabled", "Logon failed",
                "returned error: 401", "returned error: 403", "HTTP 401", "HTTP 403", "Permission denied (publickey)", "TF401019",
                "fatal: credential", "Unauthorized" }
            .Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public async Task<string> SwitchBranchAsync(string root, string branch, bool create = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-')) throw new InvalidOperationException("Enter a valid branch name.");
        await RunAsync(root, ["check-ref-format", "--branch", branch], cancellationToken);
        await RequireCleanAsync(root, cancellationToken);
        return (await RunAsync(root, create ? ["switch", "--no-guess", "-c", branch] : ["switch", "--no-guess", "--", branch], cancellationToken)).Display;
    }

    private async Task RequireCleanAsync(string root, CancellationToken cancellationToken)
    {
        var status = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (ParseStatus(status.Output).Count != 0)
            throw new InvalidOperationException("Commit or stash your changes in the terminal before switching branches or pulling. WpfStudio keeps all working files intact.");
    }

    private async Task<bool> HasHeadAsync(string root, CancellationToken cancellationToken) =>
        (await RunAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken, allowFailure: true)).ExitCode == 0;

    private static string[] ChangePaths(string root, GitChange change) =>
        ValidatePaths(root, change.OriginalPath is null ? new[] { change.Path } : [change.Path, change.OriginalPath]);

    private static string[] ValidatePaths(string root, string[] paths)
    {
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
            if (System.IO.Path.IsPathRooted(path) || !full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected file is outside this Git repository.");
        }
        return paths;
    }

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

    private static void RequireCommitId(string id)
    {
        if (id.Length is not (40 or 64) || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("A full commit ID is required.", nameof(id));
    }

    private static bool IsBinary(string? text) => text is not null && text.Contains('\0');

    /// <summary>Reads a blob such as <c>HEAD:path</c> or <c>:0:path</c>; null when that version does not exist.</summary>
    private static async Task<string?> ReadBlobAsync(string root, string spec, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["cat-file", "blob", spec], cancellationToken, allowFailure: true, readOnly: true);
        return result.ExitCode == 0 ? result.Output : null;
    }

    private static async Task<(string? Text, string? Message)> ReadWorkingFileAsync(string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path)) return (null, "This entry is a folder or submodule. There is no text to compare.");
        if (!File.Exists(path)) return (null, null);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length > OutputLimit) return (null, TooLargeMessage);
        var bytes = new byte[stream.Length];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
        if (bytes.AsSpan(0, count).Contains((byte)0)) return (null, BinaryMessage);
        // Honour a byte order mark the same way Git's output reader does, so an unchanged file compares equal.
        using var reader = new StreamReader(new MemoryStream(bytes, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(cancellationToken), null);
    }

    /// <param name="readOnly">Adds --no-optional-locks so background reads never hold the index lock a user action needs.</param>
    private static async Task<GitResult> RunAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken,
        bool allowFailure = false, string? input = null, bool readOnly = false, bool signIn = false, bool noEditor = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (readOnly) start.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in new[] { "--no-pager", "--literal-pathspecs", "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "credential.interactive=" + (signIn ? "auto" : "never") }.Concat(arguments))
            start.ArgumentList.Add(argument);
        // There is no console to type into. Sign-in uses Git Credential Manager's own window instead.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = signIn ? "Auto" : "Never";
        // GIT_EDITOR overrides core.editor, and Git never launches the editor ":".
        if (noEditor) start.Environment["GIT_EDITOR"] = ":";
        // A developer's shell tracing preferences must not leak HTTP credentials
        // into this pane's operation log.
        foreach (var trace in new[] { "GIT_TRACE", "GIT_TRACE_CURL", "GIT_CURL_VERBOSE", "GIT_TRACE_PACKET", "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE2_PERF" })
            start.Environment[trace] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        var outputTask = ReadBoundedAsync(process.StandardOutput);
        var errorTask = ReadBoundedAsync(process.StandardError);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        if (output.Truncated || error.Truncated) throw new GitOutputLimitException();
        var result = new GitResult(process.ExitCode, output.Text, error.Text);
        if (result.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Display) ? $"Git exited with code {result.ExitCode}." : result.Display);
        return result;
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var keep = Math.Min(count, OutputLimit - builder.Length);
            builder.Append(buffer, 0, keep);
            truncated |= keep != count;
        }
        return (builder.ToString(), truncated);
    }

    private sealed class GitOutputLimitException() : InvalidOperationException("Git output exceeded the 2 MiB display limit. Narrow the operation or use the terminal.") { }

    private sealed record GitResult(int ExitCode, string Output, string Error)
    {
        public string Display => GitDiagnosticSanitizer.Redact((Output + (Output.Length > 0 && Error.Length > 0 ? "\n" : "") + Error).Trim());
    }
}
