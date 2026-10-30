using System.Text.RegularExpressions;

namespace WpfStudio.Core.Text;

/// <summary>A <c>TODO</c>, <c>HACK</c>, <c>FIXME</c>, <c>UNDONE</c> or <c>BUG</c> comment in a source file.</summary>
public sealed record TaskComment(string Token, string Text, string Path, int Line, int Column, string Owner = "")
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Location => $"{FileName}:{Line}";
    /// <summary>HACK, FIXME and BUG need attention sooner than a TODO.</summary>
    public bool IsHighPriority => Token is "HACK" or "FIXME" or "BUG";
}

/// <summary>Finds task comments in C#, XAML, SQL, script and style files.</summary>
public static partial class TaskComments
{
    public static readonly string[] Tokens = ["TODO", "HACK", "FIXME", "UNDONE", "BUG"];
    public static readonly string[] Extensions = [".cs", ".xaml", ".sql", ".ps1", ".js", ".ts", ".css", ".razor", ".cshtml", ".props", ".targets", ".csproj"];
    public const int MaximumFileLength = 2_000_000;

    // Tokens must be upper case and follow a comment opener, so prose such as "todo list" in a string is not a task.
    [GeneratedRegex(@"(?://+|/\*+|<!--|^\s*\*|--|#|@\*)\s*(?<token>TODO|HACK|FIXME|UNDONE|BUG)\b(?:\((?<owner>[^)]*)\))?[\s:\-]*(?<text>.*?)\s*(?:\*+/|-->|\*@)?\s*$")]
    private static partial Regex TaskPattern();

    public static bool Supports(string path) => Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<TaskComment> Find(string path, string text)
    {
        if (text.Length > MaximumFileLength) yield break;
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        int line = 0;
        foreach (var content in text.Split('\n'))
        {
            line++;
            if (content.Length == 0 || !Tokens.Any(token => content.Contains(token, StringComparison.Ordinal))) continue;
            var match = TaskPattern().Match(content.TrimEnd('\r'));
            if (!match.Success || !OpenerAllowed(extension, match.Value.TrimStart())) continue;
            var token = match.Groups["token"];
            yield return new(token.Value, match.Groups["text"].Value, path, line, token.Index + 1, match.Groups["owner"].Value);
        }
    }

    /// <summary>"--" only opens comments in SQL and "#" only in PowerShell, so C# such as <c>i--; // TODO</c> still reads its own comment.</summary>
    private static bool OpenerAllowed(string extension, string opener) => opener switch
    {
        _ when opener.StartsWith("--", StringComparison.Ordinal) && !opener.StartsWith("-->", StringComparison.Ordinal) => extension == ".sql",
        _ when opener.StartsWith('#') => extension == ".ps1",
        _ => true
    };
}
