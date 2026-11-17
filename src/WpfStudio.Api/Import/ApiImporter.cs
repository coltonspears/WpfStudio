using System.Text.Json;
using System.Text.Json.Nodes;
using WpfStudio.Api.Models;

namespace WpfStudio.Api.Import;

/// <summary>What a file or address turned out to hold.</summary>
public sealed record ImportResult(ApiCollection? Collection, ApiEnvironment? Environment, string? Specification, IReadOnlyList<string> Warnings, string Format);

/// <summary>
/// Recognizes what is being imported, OpenAPI or Swagger in JSON or YAML, a Postman collection or environment, or a
/// collection exported from WpfStudio, and reads it.
/// </summary>
public static class ApiImporter
{
    public const string ExportFormat = "wpfstudio-api-collection";

    public static ImportResult Import(string text, string location)
    {
        var document = StructuredText.Parse(text);
        if (document.Text("format") == ExportFormat)
        {
            var exported = document.Object("collection")?.Deserialize<ApiCollection>(ApiJson.Options) ?? throw new FormatException("The exported file holds no collection.");
            // The original may still be open beside it: folders and requests get new ids too, so nothing keyed by them is shared.
            exported.Id = ApiRequest.NewId();
            foreach (var (request, _) in exported.AllRequests()) request.Id = ApiRequest.NewId();
            Reidentify(exported.Folders);
            var specification = document.Text("specification");
            exported.HasSpecification = specification is not null;
            return new ImportResult(exported, null, specification, [], "WpfStudio collection");
        }
        if (OpenApiImporter.IsOpenApi(document))
        {
            var result = OpenApiImporter.Import(document, location);
            return new ImportResult(result.Collection, null, result.Specification, result.Warnings, result.Collection.Source!.Format);
        }
        if (PostmanImporter.IsCollection(document))
        {
            var (collection, warnings) = PostmanImporter.ImportCollection(document, location);
            return new ImportResult(collection, null, null, warnings, collection.Source!.Format);
        }
        if (PostmanImporter.IsEnvironment(document)) return new ImportResult(null, PostmanImporter.ImportEnvironment(document), null, [], "Postman environment");
        throw new FormatException("This is not an OpenAPI or Swagger document, a Postman collection or environment, or an exported WpfStudio collection.");
    }

    private static void Reidentify(List<ApiFolder> folders)
    {
        foreach (var folder in folders)
        {
            folder.Id = ApiRequest.NewId();
            Reidentify(folder.Folders);
        }
    }

    /// <summary>
    /// A collection as a file to share or keep in source control. Secret variables, and credentials typed straight into a
    /// collection or request rather than through a variable, are left out: the file says where they go, not what they are.
    /// </summary>
    public static string Export(ApiCollection collection, string? specification)
    {
        var copy = ApiJson.Clone(collection);
        foreach (var variable in copy.Variables.Where(variable => variable.Secret)) variable.Value = "";
        Scrub(copy.Auth); ScrubProxy(copy.Proxy);
        foreach (var (request, _) in copy.AllRequests()) { Scrub(request.Auth); ScrubProxy(request.Proxy); }
        var document = new JsonObject
        {
            ["format"] = ExportFormat,
            ["version"] = 1,
            ["collection"] = JsonSerializer.SerializeToNode(copy, ApiJson.Options),
        };
        if (specification is not null) document["specification"] = specification;
        return document.ToJsonString(ApiJson.Options);

        static void Scrub(ApiAuth auth)
        {
            if (!IsReference(auth.Token)) auth.Token = "";
            if (!IsReference(auth.Password)) auth.Password = "";
            if (!IsReference(auth.KeyValue)) auth.KeyValue = "";
        }
        static void ScrubProxy(ProxyOverride proxy) { if (!IsReference(proxy.Password)) proxy.Password = ""; }
        static bool IsReference(string value) => value.Trim() is var trimmed && (trimmed.Length == 0 || trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal));
    }
}
