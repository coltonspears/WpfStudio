using System.Text.RegularExpressions;

namespace WpfStudio.App.Features.Git;

/// <summary>Redacts authentication material from process diagnostics, not from explicitly requested file diffs.</summary>
public static class GitDiagnosticSanitizer
{
    private static readonly Regex UrlUserInfo = new("(?<scheme>[a-z][a-z0-9+.-]*://)[^/\\s<>\"']+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex UrlSecretQuery = new("(?<name>[?&](?:access_token|oauth_token|private_token|api_key|token|password|key)=)[^&#\\s<>\"']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Authorization = new("(?<header>Authorization:\\s*(?:Basic|Bearer)\\s+)[^\\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string Redact(string text)
    {
        text = UrlUserInfo.Replace(text, "${scheme}[redacted]@");
        text = UrlSecretQuery.Replace(text, "${name}[redacted]");
        return Authorization.Replace(text, "${header}[redacted]");
    }
}
