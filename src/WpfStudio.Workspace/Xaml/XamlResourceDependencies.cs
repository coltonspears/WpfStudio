using System.Xml;
using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed record XamlResourceDependencyResult(IReadOnlyList<XamlResourceDocument> Documents, bool IsComplete,
    bool CoverageLimited, IReadOnlyList<string> Warnings);

/// <summary>Discovers direct imports only; the worker owns text IO and dependency traversal.</summary>
public static class XamlResourceDependencies
{
    public static XamlResourceDependencyResult Discover(string text, XamlResourceDocument origin, string sourceAssembly,
        Compilation compilation, XamlResourceIndex index, CancellationToken token = default, bool allowIncompleteDocument = false)
    {
        token.ThrowIfCancellationRequested();
        var documents = new List<XamlResourceDocument>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        bool complete = true, limited = false;
        void Warn(string message, bool coverage = false)
        {
            complete = false; limited |= coverage;
            if (warnings.Count < 32) warnings.Add(message.Length <= 512 ? message : message[..512]);
        }
        XamlResourceDependencyResult Result() => new(documents.AsReadOnly(), complete, limited, warnings.ToArray());
        if (text.Length > 1_000_000) { Warn("The resource dependency text budget was reached.", true); return Result(); }
        if (!allowIncompleteDocument)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
                int visited = 0;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 65_536 || reader.Depth > 256) { Warn("The resource dependency syntax budget was reached.", true); return Result(); }
                }
            }
            catch (XmlException) { Warn("Imports cannot be established from a resource document that is not well formed."); return Result(); }
        }
        var schema = new XamlResourceSchema(compilation, token);
        int elements = 0;
        var seen = new HashSet<XamlResourceDocument>();
        foreach (var element in Read(text, token))
        {
            token.ThrowIfCancellationRequested();
            if (++elements > 65_536) { Warn("The resource dependency element budget was reached.", true); break; }
            int depth = 0;
            for (var parent = element.Parent; parent is not null; parent = parent.Parent)
                if (++depth > 256) { Warn("The resource dependency nesting budget was reached.", true); return Result(); }
            if (XamlSchemaService.IgnoredElement(element) || !schema.IsElement(element, "ResourceDictionary", origin.AssemblyName)) continue;
            if (element.Attribute("Source") is not { } source) continue;
            if (!element.IsClosed || element.HasSignificantText || element.Children.Any(child => !XamlSchemaService.IgnoredElement(child))
                || element.Attribute(Language, "Class") is not null || element.Attribute(Language, "FactoryMethod") is not null)
            { Warn("An unfinished, code-backed, or mixed Source dictionary cannot establish a static import."); continue; }
            if (schema.HasBaseOverride(element)) { Warn("A dictionary import has an inherited or local xml:base override."); continue; }
            var resolution = index.ResolveSource(origin, source.Value.Text, sourceAssembly, token);
            if (resolution.Document is not { } document)
            { Warn(resolution.Status ?? "A resource import is not available in the evaluated catalog.", resolution.CoverageLimited); continue; }
            if (seen.Add(document)) documents.Add(document);
        }
        return Result();
    }
}
