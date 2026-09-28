using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;

namespace WpfStudio.Core.Wpf;

public sealed record XamlRuntimeSourceResult(SourceLocation? Location, string Status,
    SourceLocation? Element = null, string? ElementNamespace = null, string? ElementType = null);

public static class XamlRuntimeSourceLocator
{
    private const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static XamlRuntimeSourceResult Locate(string path, string text, int line, int column, string runtimeType, string? runtimeName)
    {
        if (string.IsNullOrWhiteSpace(path) || text is null || string.IsNullOrWhiteSpace(runtimeType))
            return Fail("The source path, exact source text, and runtime type are required.");
        if (text.Length > 8 * 1024 * 1024) return Fail("The source exceeds the navigation parsing limit.");
        if (!TryOffset(text, line, column, out int offset)) return Fail("The runtime source position is outside the current document.");
        try
        {
            var document = XamlPropertySourceDocument.Parse(text);
            var element = document.Elements.SingleOrDefault(element => element.Start == offset || element.Start + 1 == offset);
            if (element is null || element.Xml.Name.LocalName.Contains('.'))
                return Fail("The runtime source hint does not point to the exact start of an authored object element.");
            string localType = runtimeType[(Math.Max(runtimeType.LastIndexOf('.'), runtimeType.LastIndexOf('+')) + 1)..];
            bool rootClassMatches = element.Parent is null && element.Xml.Attribute(XName.Get("Class", Language))?.Value == runtimeType;
            if (localType != element.Xml.Name.LocalName && !rootClassMatches)
                return Fail("The authored element type no longer matches the runtime element.");
            if (!string.IsNullOrEmpty(runtimeName))
            {
                string? directive = element.Xml.Attribute(XName.Get("Name", Language))?.Value;
                string? name = element.Xml.Attribute("Name")?.Value;
                if (directive is null && name is null || directive is not null && directive != runtimeName || name is not null && name != runtimeName)
                    return Fail("The authored element name no longer matches the runtime element.");
            }
            int start = element.Start + 1;
            var position = XamlPropertySourceDocument.Position(text, start);
            var elementPosition = XamlPropertySourceDocument.Position(text, element.Start);
            return new(new(path, start, element.Name.Length, position.Line, position.Column, element.Name),
                "The runtime hint matches this exact authored element.",
                new(path, element.Start, element.StartTagEnd - element.Start, elementPosition.Line, elementPosition.Column, element.Name),
                element.Xml.Name.NamespaceName, element.Xml.Name.LocalName);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException)
        {
            return Fail("The current XAML cannot be mapped to an exact authored element: " +
                (exception.Message.Length <= 250 ? exception.Message : exception.Message[..250]));
        }
    }

    private static bool TryOffset(string text, int line, int column, out int offset)
    {
        offset = 0;
        if (line < 1 || column < 1) return false;
        int currentLine = 1;
        while (offset < text.Length && currentLine < line)
        {
            char character = text[offset++];
            if (character == '\r')
            {
                if (offset < text.Length && text[offset] == '\n') offset++;
                currentLine++;
            }
            else if (character == '\n') currentLine++;
        }
        if (currentLine != line) return false;
        int end = offset;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        if (column > end - offset) return false;
        offset += column - 1;
        return true;
    }

    private static XamlRuntimeSourceResult Fail(string status) => new(null, status);
}
