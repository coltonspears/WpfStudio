using System.IO;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewDocument
{
    private readonly HashSet<(int Line, int Column)> _bindingOrigins = [];
    private Uri? _bindingDocumentUri;

    private void RememberBindingOrigins(XElement root, string path)
    {
        if (Path.IsPathFullyQualified(path)) _bindingDocumentUri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        var pending = new Stack<XElement>();
        pending.Push(root);
        int visited = 0;
        while (pending.Count > 0 && visited++ < 65536 && _bindingOrigins.Count < 65536)
        {
            var element = pending.Pop();
            // An explicit design-only object may be retained under a rewritten
            // runtime property. It is not an authored runtime binding declaration.
            // Skip its entire subtree rather than repeatedly walking ancestors.
            if (element.Name.Namespace == Design) continue;
            if (element.Name.LocalName is "Binding" or "MultiBinding" or "PriorityBinding") Add(element);
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace == Design) continue;
                string value = attribute.Value.TrimStart();
                if (value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal)) Add(attribute);
            }
            for (XNode? child = element.LastNode; child is not null; child = child.PreviousNode)
                if (child is XElement childElement) pending.Push(childElement);
        }

        void Add(XObject source)
        {
            var info = (IXmlLineInfo)source;
            if (_bindingOrigins.Count < 65536 && info.HasLineInfo() && info.LineNumber > 0 && info.LinePosition > 0)
                _bindingOrigins.Add((info.LineNumber, info.LinePosition));
        }
    }

    internal BindingSourceDeclaration MapBindingSource(BindingSourceDeclaration declaration)
    {
        if (declaration.Source is not { } source) return declaration;
        string? reason = null;
        if (_bindingDocumentUri is null || !Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) || uri != _bindingDocumentUri)
            reason = "This binding was loaded from another source document. Its location is not verified against the current preview buffer.";
        else if (!_bindingOrigins.Contains((source.Line, source.Column)))
            reason = "This binding has no retained authored runtime declaration at its loader position. Generated and design-only declarations cannot be navigated as runtime XAML.";
        return reason is null ? declaration : declaration with { Source = null, UnavailableReason = reason };
    }
}
