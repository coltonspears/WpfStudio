using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WpfStudio.Core.DevOps;

public static partial class DevOpsText
{
    /// <summary>A WIQL string literal: single-quoted, with embedded quotes doubled.</summary>
    public static string WiqlString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Plain text of the HTML Azure DevOps stores in descriptions and comments: block elements become line breaks, list items get
    /// a bullet, entities are decoded, and no markup or script survives. Plain text passes through unchanged apart from entities.
    /// </summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = Unsafe().Replace(html, "");
        text = Breaks().Replace(text, "\n");
        text = ListItem().Replace(text, "\n• ");
        text = BlockEnd().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = SpacesBeforeBreak().Replace(text, "\n");
        text = ManyBreaks().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>Escapes text for an HTML comment or description, keeping its line breaks.</summary>
    public static string TextToHtml(string text) =>
        string.Join("<br/>", WebUtility.HtmlEncode(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n'));

    /// <summary>
    /// A branch name for a work item from a template such as <c>feature/{id}-{title}</c>. The title becomes a lower-case slug of at
    /// most 40 characters; the result is checked against Git's rules for branch names by Git itself before it is created.
    /// </summary>
    public static string BranchName(string template, int id, string title, string type)
    {
        var slug = Slug(title, 40);
        var result = (string.IsNullOrWhiteSpace(template) ? "feature/{id}-{title}" : template)
            .Replace("{id}", id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", slug, StringComparison.OrdinalIgnoreCase)
            .Replace("{type}", Slug(type, 20), StringComparison.OrdinalIgnoreCase);
        result = BranchUnsafe().Replace(result, "-");
        result = Regex.Replace(result, "-{2,}", "-");
        result = Regex.Replace(result, "/{2,}", "/");
        result = string.Join('/', result.Split('/').Select(part => part.Trim('-', '.')).Where(part => part.Length > 0));
        return result.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ? result[..^5] : result;
    }

    public static string Slug(string text, int max)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > max) slug = slug[..max].TrimEnd('-');
        return slug;
    }

    /// <summary>
    /// Work item numbers mentioned in text: <c>AB#123</c> (Azure Boards), <c>#123</c>, and a number that starts a branch name
    /// segment (<c>feature/123-login</c>, <c>bugfix/123_login</c>). Each number once, in the order found.
    /// </summary>
    public static IReadOnlyList<int> WorkItemReferences(params string?[] texts)
    {
        var found = new List<int>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match match in HashReference().Matches(text)) Add(match.Groups["id"].Value);
            foreach (Match match in BranchReference().Matches(text)) Add(match.Groups["id"].Value);
        }
        return found;
        void Add(string value)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && !found.Contains(id)) found.Add(id);
        }
    }

    /// <summary>A pull request title from a branch name: <c>feature/123-fix-login</c> reads <c>Fix login</c>.</summary>
    public static string TitleFromBranch(string branch)
    {
        var name = branch.Split('/')[^1];
        name = LeadingNumber().Replace(name, "");
        name = name.Replace('-', ' ').Replace('_', ' ').Trim();
        return name.Length == 0 ? branch : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>A work item as Markdown, to paste into a chat, an issue or a commit message.</summary>
    public static string WorkItemMarkdown(DevOpsWorkItemDetails details, int maxComments = 5)
    {
        var item = details.Item;
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"## {item.Type} {item.Id}: {item.Title}\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"- State: {item.State}\n");
        builder.Append(CultureInfo.InvariantCulture, $"- Assigned to: {(string.IsNullOrEmpty(item.AssignedTo) ? "Unassigned" : item.AssignedTo)}\n");
        if (!string.IsNullOrEmpty(item.IterationPath)) builder.Append(CultureInfo.InvariantCulture, $"- Iteration: {item.IterationPath}\n");
        if (item.Priority is { } priority) builder.Append(CultureInfo.InvariantCulture, $"- Priority: {priority}\n");
        if (item.TagList.Count > 0) builder.Append(CultureInfo.InvariantCulture, $"- Tags: {string.Join(", ", item.TagList)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"- Link: {item.WebUrl}\n");
        if (details.Description.Length > 0) builder.Append("\n### Description\n\n").Append(details.Description).Append('\n');
        foreach (var (name, value) in details.ExtraFields.Where(field => field.Value.Length > 0))
            builder.Append(CultureInfo.InvariantCulture, $"\n### {name}\n\n{value}\n");
        if (details.Comments.Count > 0)
        {
            builder.Append("\n### Recent comments\n");
            foreach (var comment in details.Comments.Take(maxComments))
                builder.Append(CultureInfo.InvariantCulture, $"\n**{comment.Author.DisplayName}** ({comment.Created:yyyy-MM-dd}):\n{comment.Text}\n");
        }
        return builder.ToString();
    }

    /// <summary>
    /// A pipeline log as the log view shows it: without the ISO timestamp agents put before each line unless asked to keep it,
    /// with <c>##[group]</c> markers shown as section headings and <c>##[error]</c> and <c>##[warning]</c> as labels.
    /// </summary>
    public static string FormatLog(string log, bool keepTimestamps)
    {
        var builder = new StringBuilder(log.Length);
        foreach (var raw in log.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = keepTimestamps ? raw : LogTimestamp().Replace(raw, "", 1);
            var bare = keepTimestamps ? LogTimestamp().Replace(line, "", 1) : line;
            var prefix = line[..(line.Length - bare.Length)];
            if (bare.StartsWith("##[group]", StringComparison.Ordinal)) line = prefix + "▸ " + bare[9..];
            else if (bare.StartsWith("##[endgroup]", StringComparison.Ordinal)) continue;
            else if (bare.StartsWith("##[section]", StringComparison.Ordinal)) line = prefix + "── " + bare[11..];
            else if (bare.StartsWith("##[error]", StringComparison.Ordinal)) line = prefix + "ERROR: " + bare[9..];
            else if (bare.StartsWith("##[warning]", StringComparison.Ordinal)) line = prefix + "WARNING: " + bare[11..];
            else if (bare.StartsWith("##[command]", StringComparison.Ordinal)) line = prefix + "> " + bare[11..];
            else if (bare.StartsWith("##[debug]", StringComparison.Ordinal)) line = prefix + "debug: " + bare[9..];
            builder.Append(AnsiEscape().Replace(line, "")).Append('\n');
        }
        return builder.ToString().TrimEnd('\n');
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Unsafe();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();
    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();
    [GeneratedRegex(@"</(p|div|li|ul|ol|h[1-6]|tr|table|blockquote|pre)\s*>|<(p|div|h[1-6]|tr|blockquote|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex SpacesBeforeBreak();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBreaks();
    [GeneratedRegex(@"[^A-Za-z0-9/_.\-]")]
    private static partial Regex BranchUnsafe();
    [GeneratedRegex(@"(?<![\w&])(?:AB)?#(?<id>\d{1,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex HashReference();
    [GeneratedRegex(@"(?:^|/)(?<id>\d{1,9})(?=[-_]|$)")]
    private static partial Regex BranchReference();
    [GeneratedRegex(@"^\d+[-_ ]*")]
    private static partial Regex LeadingNumber();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z ?")]
    private static partial Regex LogTimestamp();
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();
}
