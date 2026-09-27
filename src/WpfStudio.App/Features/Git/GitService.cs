using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WpfStudio.App.Features.Git;

/// <summary>Small Git CLI boundary. It never invokes a shell and never discards working files.</summary>
public sealed class GitService
{
    private const int OutputLimit = 2 * 1024 * 1024;

    public async Task<GitSnapshot> LoadAsync(string? workspacePath, CancellationToken cancellationToken = default)
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
            var log = await RunAsync(root, ["log", "-30", "--format=%H%x00%h%x00%an%x00%aI%x00%s%x00"], cancellationToken);
            history = ParseHistory(log.Output);
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
        return new(root, branch, upstream, tracking, changes, branches, history,
            changes.Count == 0 ? "Working tree clean" : $"{changes.Count} changed file{(changes.Count == 1 ? "" : "s")}");
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

    public async Task<GitDiff> GetDiffAsync(string root, GitChange change, bool staged, CancellationToken cancellationToken = default)
    {
        var paths = ChangePaths(root, change);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, change.Path));
        if (!staged && change.IndexStatus == '?')
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var bytes = new byte[Math.Min(stream.Length, 256 * 1024)];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
            var text = bytes.AsSpan(0, count).Contains((byte)0) ? "Binary file — no text preview available." : Encoding.UTF8.GetString(bytes, 0, count);
            if (stream.Length > count) text += "\n\n[Preview limited to 256 KiB.]";
            return new($"New file · {change.Path}", path, text);
        }
        var result = await RunAsync(root, ["diff", "--no-ext-diff", "--no-textconv", "--unified=3", .. (staged ? new[] { "--cached" } : []), "--", .. paths], cancellationToken);
        return new($"{(staged ? "Staged" : "Working tree")} · {change.Path}", path,
            string.IsNullOrWhiteSpace(result.Output) ? "No text differences. File metadata or submodule state may have changed." : result.Output);
    }

    public async Task<GitDiff> GetCommitDiffAsync(string root, string id, CancellationToken cancellationToken = default)
    {
        if (id.Length != 40 || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("A full commit ID is required.", nameof(id));
        var result = await RunAsync(root, ["show", "--no-ext-diff", "--no-textconv", "--format=fuller", "--stat", "--patch", id, "--"], cancellationToken);
        return new($"Commit · {id[..8]}", root, result.Output);
    }

    public async Task<string> FetchAsync(string root, CancellationToken cancellationToken = default) =>
        (await RunAsync(root, ["fetch"], cancellationToken)).Display;

    public async Task<string> PullAsync(string root, CancellationToken cancellationToken = default)
    {
        await RequireCleanAsync(root, cancellationToken);
        return (await RunAsync(root, ["pull", "--ff-only"], cancellationToken)).Display;
    }

    public async Task<string> PushAsync(string root, CancellationToken cancellationToken = default) =>
        (await RunAsync(root, ["push"], cancellationToken)).Display;

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

    private static string[] ChangePaths(string root, GitChange change)
    {
        var paths = change.OriginalPath is null ? new[] { change.Path } : [change.Path, change.OriginalPath];
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
            if (System.IO.Path.IsPathRooted(path) || !full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected file is outside this Git repository.");
        }
        return paths;
    }

    private static GitCommit[] ParseHistory(string text)
    {
        var fields = text.Split('\0');
        var commits = new List<GitCommit>();
        for (var i = 0; i + 4 < fields.Length; i += 5)
            commits.Add(new(fields[i].TrimStart('\r', '\n'), fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4]));
        return commits.ToArray();
    }

    private static async Task<GitResult> RunAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken,
        bool allowFailure = false, string? input = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "--no-pager", "--literal-pathspecs", "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "credential.interactive=never" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
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
        if (output.Truncated || error.Truncated) throw new InvalidOperationException("Git output exceeded the 2 MiB display limit. Narrow the operation or use the terminal.");
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

    private sealed record GitResult(int ExitCode, string Output, string Error)
    {
        public string Display => GitDiagnosticSanitizer.Redact((Output + (Output.Length > 0 && Error.Length > 0 ? "\n" : "") + Error).Trim());
    }
}
