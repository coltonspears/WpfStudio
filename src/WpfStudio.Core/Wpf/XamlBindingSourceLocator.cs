using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;

namespace WpfStudio.Core.Wpf;

/// <summary>Maps a binding object's verified loader position, never a path or target-element guess.</summary>
public static class XamlBindingSourceLocator
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string DataNamespace = "clr-namespace:System.Windows.Data;assembly=PresentationFramework";

    public static XamlRuntimeSourceResult Locate(string path, string text, int line, int column, string kind)
    {
        if (string.IsNullOrWhiteSpace(path) || text is null || kind is not ("Binding" or "MultiBinding" or "PriorityBinding"))
            return Fail("The exact source text and a supported binding kind are required.");
        if (text.Length > 8 * 1024 * 1024) return Fail("The source exceeds the navigation parsing limit.");
        if (!TryOffset(text, line, column, out int offset)) return Fail("The binding source position is outside the document.");
        try
        {
            var document = XamlPropertySourceDocument.Parse(text);
            foreach (var element in document.Elements)
            {
                if ((element.Start == offset || element.Start + 1 == offset) &&
                    element.Xml.Name.LocalName == kind && IsBindingNamespace(element.Xml.Name.NamespaceName))
                    return Found(element.Start, element.End, element.Name);

                // WPF records the attribute NAME position, even when the markup extension spans lines.
                var attribute = element.Attributes.SingleOrDefault(candidate => candidate.Start == offset);
                if (attribute is null) continue;
                if (attribute.Xml.IsNamespaceDeclaration || !IsBindingExtension(element.Xml, attribute.Xml.Value, kind))
                    return Fail("The exact attribute does not contain the expected binding declaration.");
                return Found(attribute.ValueStart, attribute.ValueEnd, kind);
            }
            return Fail("The binding hint does not identify the exact start of a matching declaration.");
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException)
        {
            return Fail("The XAML cannot be mapped to an exact binding declaration: " +
                (exception.Message.Length <= 250 ? exception.Message : exception.Message[..250]));
        }

        XamlRuntimeSourceResult Found(int start, int end, string name)
        {
            var position = XamlPropertySourceDocument.Position(text, start);
            return new(new SourceLocation(path, start, end - start, position.Line, position.Column, name),
                "The binding object's source hint matches this exact declaration.");
        }
    }

    private static bool IsBindingNamespace(string value) => value is Presentation or DataNamespace;

    private static bool IsBindingExtension(XElement owner, string value, string kind)
    {
        if (value.Length < 3 || value[0] != '{' || value[1] == '}') return false;
        int nameEnd = 1;
        while (nameEnd < value.Length && !char.IsWhiteSpace(value[nameEnd]) && value[nameEnd] is not (',' or '}')) nameEnd++;
        string name = value[1..nameEnd];
        int colon = name.IndexOf(':');
        string prefix = colon < 0 ? "" : name[..colon];
        string local = colon < 0 ? name : name[(colon + 1)..];
        if (local != kind || colon == 0 || local.Contains(':')) return false;
        XNamespace? ns = prefix.Length == 0 ? owner.GetDefaultNamespace() : owner.GetNamespaceOfPrefix(prefix);
        if (ns is null || !IsBindingNamespace(ns.NamespaceName)) return false;

        // Validate the whole extension. A literal, truncated expression, or a nested binding
        // inside a different extension must never be rounded to an authored binding.
        int depth = 1;
        char quote = '\0';
        bool escaped = false;
        for (int i = nameEnd; i < value.Length; i++)
        {
            char character = value[i];
            if (escaped) { escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return i == value.Length - 1;
        }
        return false;
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
