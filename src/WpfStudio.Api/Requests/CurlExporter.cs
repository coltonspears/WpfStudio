using System.Text;
using WpfStudio.Api.Models;

namespace WpfStudio.Api.Requests;

/// <summary>The request as a curl command line, with variables filled in, for a terminal or a bug report.</summary>
public static class CurlExporter
{
    public static string Export(ApiRequest request, ApiCollection? collection, ApiEnvironment? environment)
    {
        var variables = new VariableResolver(collection?.Variables, environment?.Variables);
        var address = ApiRequestBuilder.ResolveAddress(request, collection, variables);
        var builder = new StringBuilder("curl");
        var method = request.Method.Trim().ToUpperInvariant();
        if (method != "GET" || request.BodyMode != BodyMode.None) builder.Append(" -X ").Append(method);
        builder.Append(' ').Append(Quote(address.AbsoluteUri));
        var explicitHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in request.Headers.Where(row => row.Enabled && row.Key.Trim().Length > 0))
        {
            var name = variables.Resolve(row.Key).Trim();
            explicitHeaders.Add(name);
            Line(builder).Append("-H ").Append(Quote(name + ": " + variables.Resolve(row.Value)));
        }
        var auth = ApiRequestBuilder.EffectiveAuth(request, collection);
        switch (auth.Kind)
        {
            case AuthKind.Bearer when !explicitHeaders.Contains("Authorization"):
                Line(builder).Append("-H ").Append(Quote("Authorization: Bearer " + variables.Resolve(auth.Token).Trim()));
                break;
            case AuthKind.Basic when !explicitHeaders.Contains("Authorization"):
                Line(builder).Append("-u ").Append(Quote(variables.Resolve(auth.UserName) + ":" + variables.Resolve(auth.Password)));
                break;
            case AuthKind.ApiKey when auth.KeyLocation == ApiKeyLocation.Header && !explicitHeaders.Contains(auth.KeyName):
                Line(builder).Append("-H ").Append(Quote(variables.Resolve(auth.KeyName) + ": " + variables.Resolve(auth.KeyValue)));
                break;
        }
        var contentType = request.BodyMode switch { BodyMode.Json => "application/json", BodyMode.Xml => "application/xml", BodyMode.Text => "text/plain", _ => null };
        if (contentType is not null && !explicitHeaders.Contains("Content-Type")) Line(builder).Append("-H ").Append(Quote("Content-Type: " + contentType));
        switch (request.BodyMode)
        {
            case BodyMode.Json or BodyMode.Xml or BodyMode.Text:
                Line(builder).Append("--data-raw ").Append(Quote(variables.Resolve(request.Body)));
                break;
            case BodyMode.FormUrlEncoded:
                foreach (var row in request.Form.Where(row => row.Enabled && row.Key.Length > 0))
                    Line(builder).Append("--data-urlencode ").Append(Quote(variables.Resolve(row.Key) + "=" + variables.Resolve(row.Value)));
                break;
            case BodyMode.Multipart:
                foreach (var row in request.Form.Where(row => row.Enabled && row.Key.Length > 0))
                    Line(builder).Append("-F ").Append(Quote(variables.Resolve(row.Key) + "=" + (row.IsFile ? "@" : "") + variables.Resolve(row.Value)));
                break;
            case BodyMode.File when request.BodyFilePath.Length > 0:
                Line(builder).Append("--data-binary ").Append(Quote("@" + variables.Resolve(request.BodyFilePath)));
                break;
        }
        if (!request.Settings.VerifyCertificates) Line(builder).Append("--insecure");
        if (request.Settings.FollowRedirects) Line(builder).Append("--location");
        return builder.ToString();
    }

    private static StringBuilder Line(StringBuilder builder) => builder.Append(" \\\n  ");

    /// <summary>Single quotes for POSIX shells: nothing inside them is special except the quote itself.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
