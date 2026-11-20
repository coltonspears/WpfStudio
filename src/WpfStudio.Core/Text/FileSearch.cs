using System.Text.RegularExpressions;
using WpfStudio.Contracts;

namespace WpfStudio.Core.Text;

/// <summary>
/// A line of a file that a text search found. <paramref name="Line"/> and <paramref name="Column"/> are one-based and
/// <paramref name="Start"/> is the offset in the file's text of the line's first match, which is <paramref name="Length"/> long.
/// </summary>
public sealed record TextMatch(string Path, int Line, int Column, int Start, int Length, string LineText);

/// <summary>What a search looks for in each line, and the lines of one text it is on.</summary>
public static class TextSearch
{
    /// <summary>The expression a search runs: the text as typed unless it is a regular expression, optionally as a whole word.</summary>
    /// <exception cref="ArgumentException">The text is not a valid regular expression.</exception>
    public static Regex Pattern(string query, bool matchCase, bool wholeWord, bool useRegex)
    {
        var expression = useRegex ? query : Regex.Escape(query);
        // \b only matches beside a word character, so it is added only on the sides where the text has one.
        if (wholeWord) expression = (WordStarts(query) || useRegex ? @"\b" : "") + "(?:" + expression + ")" + (WordEnds(query) || useRegex ? @"\b" : "");
        return new Regex(expression, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(2));
        static bool WordStarts(string text) => text.Length > 0 && (char.IsLetterOrDigit(text[0]) || text[0] == '_');
        static bool WordEnds(string text) => text.Length > 0 && (char.IsLetterOrDigit(text[^1]) || text[^1] == '_');
    }

    /// <summary>
    /// Adds one match for each line of <paramref name="text"/> that matches, at the line's first match that is not empty, until
    /// <paramref name="found"/> holds <paramref name="limit"/>. Returns how many lines match, which goes on counting past the limit.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">The expression cannot finish on one of the lines.</exception>
    public static int FindLines(string path, string text, Regex pattern, List<TextMatch>? found, int limit = int.MaxValue)
    {
        int offset = 0, number = 0, count = 0;
        var rest = text.AsSpan();
        while (true)
        {
            number++;
            int end = rest.IndexOf('\n');
            var line = end < 0 ? rest : rest[..end];
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            foreach (var match in pattern.EnumerateMatches(line))
            {
                // An expression that can match nothing, such as x*, does so at every position; only what it found is a result.
                if (match.Length == 0) continue;
                count++;
                if (found != null && found.Count < limit) found.Add(new(path, number, match.Index + 1, offset + match.Index, match.Length, line.ToString()));
                break;
            }
            if (end < 0) return count;
            offset += end + 1; rest = rest[(end + 1)..];
        }
    }
}

/// <summary>
/// Which files a search reads, from the two lists typed beside it. Each is separated by commas or semicolons, and a file is
/// read when it matches one of the terms to include, or there are none, and none of the terms to exclude. A term is matched
/// against the file's path relative to the workspace folder, without regard to case:
/// <list type="bullet">
/// <item><c>*.cs</c> or <c>.cs</c>: an extension. <c>*.{cs,xaml}</c> names two.</item>
/// <item><c>Views</c> or <c>*Tests</c>: a folder or file of that name, at any depth.</item>
/// <item><c>src/Views</c> or <c>Views/*.xaml</c>: a path, wherever it starts. <c>./src</c> starts at the workspace folder.</item>
/// <item><c>*</c> stands for any part of a name, <c>?</c> for one character and <c>**</c> for any number of folders.</item>
/// </list>
/// </summary>
public sealed class FileFilter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private readonly Regex[] _include, _exclude;
    private FileFilter(Regex[] include, Regex[] exclude) { _include = include; _exclude = exclude; }

    /// <summary>Every file.</summary>
    public static FileFilter All { get; } = new([], []);
    public bool IsEmpty => _include.Length == 0 && _exclude.Length == 0;

    public static FileFilter Parse(string? include, string? exclude)
    {
        var filter = new FileFilter(Terms(include).Select(Compile).ToArray(), Terms(exclude).Select(Compile).ToArray());
        return filter.IsEmpty ? All : filter;
    }

    /// <param name="relativePath">The file's path from the workspace folder, with either separator.</param>
    public bool Matches(string relativePath)
    {
        if (IsEmpty) return true;
        var path = relativePath.Replace('\\', '/');
        return (_include.Length == 0 || Any(_include, path)) && !Any(_exclude, path);
        static bool Any(Regex[] terms, string path) { foreach (var term in terms) if (term.IsMatch(path)) return true; return false; }
    }

    /// <summary>The terms of a list. A comma between braces belongs to its term, as in <c>*.{cs,xaml}</c>.</summary>
    public static IReadOnlyList<string> Terms(string? list)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(list)) return terms;
        int depth = 0, start = 0;
        for (int i = 0; i <= list.Length; i++)
        {
            if (i < list.Length)
            {
                if (list[i] == '{') depth++;
                else if (list[i] == '}' && depth > 0) depth--;
                if (list[i] is not (',' or ';') || depth > 0) continue;
            }
            if (list[start..i].Trim() is { Length: > 0 } term) terms.Add(term);
            start = i + 1;
        }
        // An unclosed brace made one term of the rest; its commas separate after all.
        return depth == 0 ? terms : list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Regex Compile(string term)
    {
        term = term.Replace('\\', '/');
        bool anchored = term.StartsWith("./", StringComparison.Ordinal) || term.StartsWith('/');
        if (anchored) term = term[(term.IndexOf('/') + 1)..];
        // "Views/" is the folder Views, and "**/x" is what "x" already says.
        term = term.TrimEnd('/');
        while (!anchored && term.StartsWith("**/", StringComparison.Ordinal)) term = term[3..];
        // "./" is the workspace folder itself: everything.
        if (term.Length == 0) return new Regex("^", RegexOptions.CultureInvariant, Timeout);
        string body;
        if (!anchored && term.StartsWith('.') && term.AsSpan().IndexOfAny("*?{/") < 0)
            // ".cs" is an extension. The same rule finds the folder ".github" and the file ".editorconfig".
            body = "[^/]*" + Regex.Escape(term);
        else body = Glob(term);
        return new Regex((anchored ? "^" : "(?:^|/)") + body + "(?:/|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout);
    }

    private static string Glob(string term)
    {
        // Braces that do not pair up are part of a name.
        bool braces = term.Count(c => c == '{') == term.Count(c => c == '}') && term.IndexOf('{') <= term.IndexOf('}');
        var expression = new System.Text.StringBuilder();
        int depth = 0;
        for (int i = 0; i < term.Length; i++)
        {
            char c = term[i];
            if (c == '*' && i + 1 < term.Length && term[i + 1] == '*')
            {
                while (i + 1 < term.Length && term[i + 1] == '*') i++;
                // Any number of folders, none included: "a/**/b" is also "a/b".
                if (i + 1 < term.Length && term[i + 1] == '/') { expression.Append("(?:.*/)?"); i++; }
                else expression.Append(".*");
            }
            else if (c == '*') expression.Append("[^/]*");
            else if (c == '?') expression.Append("[^/]");
            else if (c == '{' && braces) { expression.Append("(?:"); depth++; }
            else if (c == '}' && braces && depth > 0) { expression.Append(')'); depth--; }
            else if (c == ',' && depth > 0) expression.Append('|');
            else expression.Append(Regex.Escape(c.ToString()));
        }
        return expression.ToString();
    }
}

/// <summary>How many lines of how many files of one extension a search matched. The extension is lower case with its dot, or empty.</summary>
public sealed record ExtensionCount(string Extension, int Files, int Matches);

/// <summary>A search of <paramref name="Files"/>, in the order their matches are to be listed.</summary>
public sealed record FileSearchRequest(Regex Pattern, IReadOnlyList<string> Files)
{
    /// <summary>The editor's text of open documents by path, which is searched in place of what is on disk.</summary>
    public IReadOnlyDictionary<string, string>? OpenTexts { get; init; }
    /// <summary>
    /// The extensions whose matches are listed; null or empty lists every file's. The other files are still searched, so that
    /// <see cref="FileSearchResult.Extensions"/> says what narrowing to them, or widening again, would list.
    /// </summary>
    public IReadOnlySet<string>? ListedExtensions { get; init; }
    public int Limit { get; init; } = 2000;
}

/// <summary>
/// What a search found. <paramref name="Truncated"/> means it stopped at its limit, so there may be more matches than
/// <paramref name="Matches"/> and <paramref name="Extensions"/> count. <paramref name="TimedOutIn"/> is the file whose line the
/// regular expression could not finish on, where the search also stopped. <paramref name="Skipped"/> counts text files too
/// large to search; files that are not text are not counted at all.
/// </summary>
public sealed record FileSearchResult(IReadOnlyList<TextMatch> Matches, IReadOnlyList<ExtensionCount> Extensions, int FilesSearched, bool Truncated = false, string? TimedOutIn = null, int Skipped = 0);

/// <summary>Searches the text of many files at once.</summary>
public static class FileSearch
{
    /// <summary>A text file larger than this is a data file, and reading a few of them would take longer than the rest of the workspace.</summary>
    public const long MaxFileBytes = 16 * 1024 * 1024;
    private const int Batch = 64, Sniffed = 8000;
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".winmd", ".lib", ".obj", ".so", ".dylib", ".bin", ".dat", ".cache", ".nupkg", ".snupkg", ".zip", ".7z", ".gz", ".tar", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".pdf", ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".snk", ".pfx", ".cer", ".p12", ".suo", ".dmp", ".onnx", ".db", ".mdf", ".ldf", ".sqlite", ".docx", ".xlsx", ".pptx",
    };

    /// <summary>The key a file's type is counted under: its extension in lower case, or empty when its name has none.</summary>
    public static string ExtensionOf(string path) => Path.GetExtension(path).ToLowerInvariant();

    /// <summary>True for extensions that are never text, whose files are passed over without being opened.</summary>
    public static bool IsBinaryExtension(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Reads and searches the files several at a time, and lists the matches in the files' order whatever order they were read in.
    /// A file that cannot be read, or is not text, is passed over.
    /// </summary>
    public static FileSearchResult Run(FileSearchRequest request, CancellationToken token = default)
    {
        var files = request.Files;
        var listed = request.ListedExtensions is { Count: > 0 } extensions ? extensions : null;
        var matches = new List<TextMatch>();
        var tallies = new Dictionary<string, (int Files, int Matches)>(StringComparer.Ordinal);
        int searched = 0, skipped = 0;
        bool truncated = false;
        string? timedOutIn = null;
        var outcomes = new Outcome[Math.Min(Batch, Math.Max(files.Count, 1))];
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) };
        for (int from = 0; from < files.Count && !truncated && timedOutIn == null; from += Batch)
        {
            token.ThrowIfCancellationRequested();
            int start = from, count = Math.Min(Batch, files.Count - from);
            Parallel.For(0, count, options, index => outcomes[index] = Search(files[start + index], request, listed));
            for (int index = 0; index < count; index++)
            {
                var outcome = outcomes[index];
                if (outcome.State == FileState.Large) skipped++;
                if (outcome.State is not (FileState.Searched or FileState.TimedOut)) continue;
                searched++;
                if (outcome.Matches > 0)
                {
                    string extension = ExtensionOf(files[start + index]);
                    var tally = tallies.GetValueOrDefault(extension);
                    tallies[extension] = (tally.Files + 1, tally.Matches + outcome.Matches);
                }
                int room = request.Limit - matches.Count, rows = outcome.Rows?.Count ?? 0;
                if (rows > 0) matches.AddRange(rows <= room ? outcome.Rows! : outcome.Rows!.Take(room));
                if (outcome.State == FileState.TimedOut) { timedOutIn = files[start + index]; break; }
                // At the limit the rest is not read: what it holds is not known, so it is not counted either.
                if (matches.Count >= request.Limit) { truncated = rows > room || outcome.Dropped || start + index + 1 < files.Count; break; }
            }
        }
        var counts = tallies.Select(tally => new ExtensionCount(tally.Key, tally.Value.Files, tally.Value.Matches))
            .OrderByDescending(count => count.Matches).ThenBy(count => count.Extension, StringComparer.Ordinal).ToArray();
        return new(matches, counts, searched, truncated, timedOutIn, skipped);
    }

    private enum FileState { Unread, Searched, Large, TimedOut }
    /// <summary><see cref="Dropped"/> is set when the file has more matching lines of its own than the limit lets it list.</summary>
    private readonly record struct Outcome(FileState State, int Matches = 0, List<TextMatch>? Rows = null, bool Dropped = false);

    private static Outcome Search(string path, FileSearchRequest request, IReadOnlySet<string>? listed)
    {
        string? text;
        if (request.OpenTexts != null && request.OpenTexts.TryGetValue(path, out var open)) text = open;
        else
        {
            if (IsBinaryExtension(path)) return new(FileState.Unread);
            try { text = Read(path, out bool large); if (large) return new(FileState.Large); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { return new(FileState.Unread); }
        }
        if (text == null) return new(FileState.Unread);
        var rows = listed == null || listed.Contains(ExtensionOf(path)) ? new List<TextMatch>() : null;
        try
        {
            int count = TextSearch.FindLines(path, text, request.Pattern, rows, request.Limit);
            return new(FileState.Searched, count, rows, rows != null && count > rows.Count);
        }
        catch (RegexMatchTimeoutException) { return new(FileState.TimedOut, rows?.Count ?? 0, rows); }
    }

    /// <summary>The file's text as the editor would show it; null when its first bytes say it is not text.</summary>
    private static string? Read(string path, out bool large)
    {
        large = false;
        byte[] bytes;
        // Shared like the editor's own read, so a file another program is writing can still be searched.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length > MaxFileBytes) { large = true; return null; }
            bytes = new byte[stream.Length];
            int read = 0;
            while (read < bytes.Length && stream.Read(bytes, read, bytes.Length - read) is > 0 and var chunk) read += chunk;
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        return IsText(bytes) ? TextEncoding.Decode(bytes).Text : null;
    }

    /// <summary>Text has no zero byte near its start, unless a byte order mark says it is UTF-16 or UTF-32, where every other byte may be one.</summary>
    internal static bool IsText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return true;
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return true;
        return bytes[..Math.Min(bytes.Length, Sniffed)].IndexOf((byte)0) < 0;
    }
}

/// <summary>The files a search covers when it looks in less than the whole workspace.</summary>
public static class SearchScopeFiles
{
    /// <summary>
    /// The project <paramref name="path"/> belongs to: the one that lists it, or else the one whose folder is nearest above it.
    /// A file several projects list goes to the one it is filed under, so a linked file belongs to the project it lives in.
    /// </summary>
    public static WorkspaceProject? ProjectOf(string path, IReadOnlyList<WorkspaceProject> projects)
    {
        var nearest = NearestAbove(path, Folders(projects));
        if (nearest != null && Lists(nearest, path)) return nearest;
        return projects.FirstOrDefault(project => Lists(project, path)) ?? nearest;
        static bool Lists(WorkspaceProject project, string path) =>
            project.ProjectPath.Equals(path, StringComparison.OrdinalIgnoreCase) || project.Files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a search of one project reads: the files the project lists, its project file, and the other files in its folder,
    /// such as a readme or a settings file, that no project in a folder below has a nearer claim to.
    /// </summary>
    public static IReadOnlyList<string> ProjectFiles(WorkspaceProject project, IReadOnlyList<WorkspaceProject> projects, IEnumerable<string> workspaceFiles)
    {
        var files = new HashSet<string>(project.Files.Where(file => !file.IsGenerated).Select(file => file.Path), StringComparer.OrdinalIgnoreCase) { project.ProjectPath };
        var listedElsewhere = new HashSet<string>(projects.Where(other => !ReferenceEquals(other, project)).SelectMany(other => other.Files.Select(file => file.Path).Append(other.ProjectPath)), StringComparer.OrdinalIgnoreCase);
        var folders = Folders(projects);
        string folder = Path.GetDirectoryName(project.ProjectPath) ?? "";
        foreach (var path in workspaceFiles)
            if (IsUnder(path, folder) && !listedElsewhere.Contains(path) && ReferenceEquals(NearestAbove(path, folders), project)) files.Add(path);
        return [.. files];
    }

    /// <summary>The workspace's files in <paramref name="folder"/> and the folders below it.</summary>
    public static IReadOnlyList<string> FolderFiles(string folder, IEnumerable<string> workspaceFiles) => workspaceFiles.Where(path => IsUnder(path, folder)).ToArray();

    /// <summary>True when <paramref name="path"/> is in <paramref name="folder"/> or a folder below it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (folder.Length == 0 || path.Length <= folder.Length || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        // A folder named with its separator, as a drive's root is, already ends where the next name begins.
        return folder[^1] is '/' or '\\' || path[folder.Length] is '/' or '\\';
    }

    private static (WorkspaceProject Project, string Folder)[] Folders(IReadOnlyList<WorkspaceProject> projects) =>
        projects.Select(project => (project, Path.GetDirectoryName(project.ProjectPath) ?? "")).ToArray();

    private static WorkspaceProject? NearestAbove(string path, (WorkspaceProject Project, string Folder)[] projects)
    {
        WorkspaceProject? nearest = null;
        int depth = -1;
        foreach (var (project, folder) in projects)
            if (folder.Length > depth && IsUnder(path, folder)) { nearest = project; depth = folder.Length; }
        return nearest;
    }
}
