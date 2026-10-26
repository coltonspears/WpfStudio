using System.Globalization;

namespace WpfStudio.Core.Git;

/// <summary>Who last changed one line. Lines not yet committed have <see cref="IsUncommitted"/> set.</summary>
public sealed record BlameLine(string Commit, string Author, string AuthorEmail, DateTimeOffset Time, string Summary)
{
    public bool IsUncommitted => Commit.All(c => c == '0');
    public string ShortCommit => Commit[..Math.Min(8, Commit.Length)];

    /// <summary>An inline annotation such as "Ada Lovelace, 3 days ago · Fix the parser".</summary>
    public string Describe(DateTimeOffset now) => IsUncommitted ? "You · Uncommitted change" : $"{Author}, {Relative(Time, now)} · {Summary}";

    public static string Relative(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return Plural((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Plural((int)age.TotalHours, "hour");
        if (age < TimeSpan.FromDays(30)) return Plural((int)age.TotalDays, "day");
        if (age < TimeSpan.FromDays(365)) return Plural((int)(age.TotalDays / 30), "month");
        return Plural((int)(age.TotalDays / 365), "year");
        static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }
}

public static class GitBlame
{
    /// <summary>
    /// Parses <c>git blame --porcelain</c>: one entry per final line, 1-based in order. Commit details are given on a
    /// commit's first line only and reused for its later lines.
    /// </summary>
    public static IReadOnlyList<BlameLine> Parse(string porcelain)
    {
        var commits = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var lines = new SortedDictionary<int, string>();
        var text = porcelain.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < text.Length; index++)
        {
            var header = text[index].Split(' ');
            if (header.Length < 3 || header[0].Length is not (40 or 64) || !header[0].All(Uri.IsHexDigit)) continue;
            var commit = header[0];
            if (!int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out var finalLine)) continue;
            if (!commits.TryGetValue(commit, out var fields)) commits[commit] = fields = new(StringComparer.Ordinal);
            // Key-value lines follow until the tab-prefixed source line.
            while (++index < text.Length && !text[index].StartsWith('\t'))
            {
                var space = text[index].IndexOf(' ');
                if (space > 0) fields[text[index][..space]] = text[index][(space + 1)..];
                else if (text[index].Length > 0) fields[text[index]] = "";
            }
            lines[finalLine] = commit;
        }
        return lines.Values.Select(commit =>
        {
            var fields = commits[commit];
            var time = long.TryParse(fields.GetValueOrDefault("author-time"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.MinValue;
            return new BlameLine(commit, fields.GetValueOrDefault("author") ?? "", fields.GetValueOrDefault("author-mail")?.Trim('<', '>') ?? "",
                time, fields.GetValueOrDefault("summary") ?? "");
        }).ToArray();
    }
}
