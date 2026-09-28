using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Runtime.Inspection;

public sealed record InspectionResolvedSource(InspectionSourceDocument? Document, string Status, InspectionModule? Module = null);

/// <summary>Resolves a WPF resource hint using the running module and compiled resource mappings.</summary>
public static class InspectionSourceResolver
{
    public static async Task<InspectionResolvedSource> ResolveAsync(InspectionSourceHint hint,
        InspectionModuleCatalog catalog, CancellationToken cancellationToken = default,
        Func<InspectionModule, CancellationToken, Task<InspectionSourceSymbolsResult>>? readSymbols = null)
    {
        if (hint.Line < 1 || hint.Column < 1) return Unavailable("WPF did not provide a precise source position.");
        if (catalog.Truncated) return Unavailable("The loaded module list is incomplete; source ownership cannot be verified.");
        if (!TryResource(hint.Uri, out var assembly, out var resource))
            return Unavailable("This source URI is not a supported application XAML resource location.");
        var owners = catalog.Modules.Where(m => assembly == null ? m.IsResourceAssembly
            : string.Equals(m.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (owners.Length != 1) return Unavailable(owners.Length == 0
            ? "The source resource's assembly is not present in the running module list."
            : "More than one loaded module could own this resource; its source is ambiguous.");
        var owner = owners[0];
        var symbols = await (readSymbols ?? InspectionSourceSymbols.ReadAsync)(owner, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (symbols.Module != owner || symbols.Truncated) return Unavailable("The source symbol information is incomplete or belongs to another module.");
        var paths = (symbols.Resources ?? []).Where(r => TryResource(r.Uri, out var name, out var path)
            && string.Equals(name, owner.AssemblyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, resource, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DocumentPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != 1) return Unavailable(paths.Length > 1
            ? "The compiled resource maps to multiple source documents."
            : "No verified compiled resource-to-source mapping is available. " + symbols.Status);
        var documents = symbols.Documents.Where(d => string.Equals(d.Path, paths[0], StringComparison.OrdinalIgnoreCase)).ToArray();
        return documents.Length == 1 ? new(documents[0], "The running module and compiled XAML source symbols match.", owner)
            : Unavailable("The compiled resource has no unique supported source checksum.");
    }

    private static InspectionResolvedSource Unavailable(string status) => new(null, status);

    private static bool TryResource(string uri, out string? assembly, out string resource)
    {
        assembly = null; resource = "";
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 8192) return false;
        const string prefix = "pack://application:,,,/";
        if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) uri = uri[prefix.Length..];
        else if (uri.StartsWith('/')) uri = uri[1..];
        if (uri.IndexOfAny([':', '?', '#', '\\', '\0']) >= 0) return false;
        int marker = uri.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            assembly = uri[..marker]; uri = uri[(marker + 11)..];
            // Version/public-key qualified hints need more evidence than a simple
            // name comparison. Refuse them until that matching is implemented.
            if (assembly.Length == 0 || assembly.IndexOfAny([';', '/', '%']) >= 0) return false;
        }
        else if (uri.Contains(';')) return false;
        var segments = uri.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            // Unescape exactly once and reject encoded separators, traversal and
            // malformed escapes rather than normalizing them to a different file.
            for (int j = 0; j < segment.Length; j++)
                if (segment[j] == '%' && (j + 2 >= segment.Length || !Uri.IsHexDigit(segment[++j]) || !Uri.IsHexDigit(segment[++j]))) return false;
            segment = Uri.UnescapeDataString(segment);
            if (segment is "" or "." or ".." || segment.IndexOfAny(['/', '\\', ':', '?', '#', '\0', '%']) >= 0) return false;
            segments[i] = segment;
        }
        resource = string.Join('/', segments);
        return resource.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }
}
