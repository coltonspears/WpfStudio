using System.Text;
using System.Text.RegularExpressions;

namespace WpfStudio.Core.Updates;

/// <summary>
/// Release notes as plain text for a TextBlock. GitHub writes them in Markdown (generated notes list each pull request as
/// <c>* Title by @someone in https://…</c>), so headings lose their marks, list items become bullets, links keep their text,
/// and emphasis, code marks and HTML comments are dropped. Anything else is shown as written.
/// </summary>
public static partial class ReleaseNotesText
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var text = Comment().Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), "");
        var output = new StringBuilder();
        var blank = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { if (output.Length > 0 && ++blank == 1) output.Append('\n'); continue; }
            blank = 0;
            if (Heading().Match(line) is { Success: true } heading) line = heading.Groups[1].Value;
            else if (Bullet().Match(line) is { Success: true } bullet) line = bullet.Groups[1].Value + "• " + bullet.Groups[2].Value;
            line = Link().Replace(line, "$1");
            line = Emphasis().Replace(line, "$2");
            line = line.Replace("`", "", StringComparison.Ordinal);
            output.Append(line).Append('\n');
        }
        return output.ToString().TrimEnd();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)] private static partial Regex Comment();
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)[*+-]\s+(.*)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"(\*\*|__)(.+?)\1")] private static partial Regex Emphasis();
}
