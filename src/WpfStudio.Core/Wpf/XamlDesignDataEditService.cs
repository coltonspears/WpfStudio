using System.Text;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Wpf;

/// <summary>
/// The view model to declare as a view's design-time DataContext. <paramref name="AssemblyName"/> is given only when
/// the type lives outside the view's own project, whose markup names its own types without one.
/// </summary>
public sealed record XamlDesignDataRequest(string Path, string Text, long Version, string TypeName, string? AssemblyName = null);

/// <summary>
/// Prepares the reviewable edit that writes <c>d:DataContext="{d:DesignInstance Type=vm:OrdersViewModel}"</c> on a
/// view's root element, with the namespace declarations it needs and the design namespace marked ignorable so the
/// declaration has no effect on a build. It changes no editor buffer.
/// </summary>
public static class XamlDesignDataEditService
{
    private const string Design = "http://schemas.microsoft.com/expression/blend/2008";
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static XamlPropertyEditResult CreateEdit(XamlDesignDataRequest request)
    {
        if (request is null || request.Text is null || string.IsNullOrWhiteSpace(request.Path)) return Fail("The view's source text and path are required.");
        int dot = request.TypeName?.LastIndexOf('.') ?? -1;
        if (dot <= 0 || dot == request.TypeName!.Length - 1) return Fail("The view model needs a namespace to be named from XAML.");
        string clrNamespace = request.TypeName[..dot], typeName = request.TypeName[(dot + 1)..];
        try
        {
            // A nested or generic type has no plain XAML name.
            XmlConvert.VerifyNCName(typeName);
            foreach (string part in clrNamespace.Split('.')) XmlConvert.VerifyNCName(part);
            if (request.AssemblyName is { } assembly && (assembly.Length == 0 || assembly.IndexOfAny([';', '"', '\'', '<', '>', '&', '=']) >= 0 || assembly.Any(char.IsWhiteSpace)))
                return Fail("The view model's assembly name cannot be written in a namespace mapping.");
            var root = XamlPropertySourceDocument.Parse(request.Text).Elements[0];
            XNamespace design = Design, compatibility = Compatibility;
            if (root.Xml.Attribute(design + "DataContext") is not null
                || root.Xml.Elements().Any(child => child.Name.Namespace == design && child.Name.LocalName.EndsWith(".DataContext", StringComparison.Ordinal)))
                return Fail("This view already declares a design-time DataContext.");

            var prefixes = root.Xml.Attributes().Where(attribute => attribute.IsNamespaceDeclaration)
                .ToDictionary(attribute => attribute.Name.LocalName == "xmlns" ? "" : attribute.Name.LocalName, attribute => attribute.Value, StringComparer.Ordinal);
            var added = new List<(string Name, string Value)>();
            string Prefix(string uri, params string[] preferred)
            {
                if (prefixes.Where(pair => pair.Key.Length > 0 && pair.Value == uri).OrderBy(pair => Array.IndexOf(preferred, pair.Key) is var index and >= 0 ? index : int.MaxValue)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).FirstOrDefault() is { } existing) return existing;
                string chosen = preferred.FirstOrDefault(candidate => !prefixes.ContainsKey(candidate))
                    ?? Enumerable.Range(2, 64).Select(number => preferred[0] + number).First(candidate => !prefixes.ContainsKey(candidate));
                prefixes[chosen] = uri;
                added.Add(("xmlns:" + chosen, uri));
                return chosen;
            }
            string designPrefix = Prefix(Design, "d", "design");
            string compatibilityPrefix = Prefix(Compatibility, "mc", "compat");
            string modelPrefix = Prefix("clr-namespace:" + clrNamespace + (request.AssemblyName is null ? "" : ";assembly=" + request.AssemblyName), "vm", "viewModels", "local");

            var edits = new List<TextEdit>();
            // Without mc:Ignorable the build would try to resolve the design namespace and fail.
            var ignorable = root.Attributes.FirstOrDefault(attribute => attribute.Xml.Name == compatibility + "Ignorable");
            if (ignorable is null) added.Add((compatibilityPrefix + ":Ignorable", designPrefix));
            else if (!ignorable.Xml.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(designPrefix, StringComparer.Ordinal))
                edits.Add(new TextEdit(ignorable.ValueEnd, 0, (string.IsNullOrWhiteSpace(ignorable.Xml.Value) ? "" : " ") + designPrefix));
            added.Add((designPrefix + ":DataContext", "{" + designPrefix + ":DesignInstance Type=" + modelPrefix + ":" + typeName + "}"));

            // Follow the start tag's own layout: one attribute to a line where the author wrote them so, otherwise in line.
            var last = root.Attributes.LastOrDefault();
            string separator = last is not null && request.Text[last.LeadingStart..last.Start] is var leading && leading.Contains('\n') ? leading : " ";
            char quote = last?.Quote ?? '"';
            var text = new StringBuilder();
            foreach (var (name, value) in added) text.Append(separator).Append(name).Append('=').Append(quote).Append(value).Append(quote);
            edits.Add(new TextEdit(last?.End ?? root.NameEnd, 0, text.ToString()));

            string explanation = $"Declares {typeName} as this view's design-time DataContext. The designer draws the view with sample values generated from it, without running its constructor, "
                + "and the editor completes and checks the bindings beneath it. The declaration is ignored when the application is built and run.";
            return new XamlPropertyEditResult(new DocumentEdits(request.Path, request.Version, [.. edits.OrderBy(edit => edit.Start)],
                DocumentStore.Hash(Encoding.UTF8.GetBytes(request.Text))), explanation);
        }
        catch (XmlException exception) { return Fail("The view's XAML could not be read: " + exception.Message); }
    }

    private static XamlPropertyEditResult Fail(string message) => new(null, message, message);
}
