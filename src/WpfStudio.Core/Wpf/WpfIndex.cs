using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Wpf;

public sealed record WpfItem(string Kind, string Name, string Path, int Line, string? Key = null)
{
    public string Display => $"{Kind}  ·  {Name}";
}
public sealed record ResourceDeclaration(string Key, string Path, int Line, int ValueStart, int ValueLength, string Scope);
public sealed record ResourceUsage(string Key, string Path, int Line, int Start, int Length, bool IsDynamic, string? ResolvedPath, int? ResolvedDeclarationStart);
public sealed record WpfIndexSnapshot(IReadOnlyList<WpfItem> Items, IReadOnlyList<ResourceDeclaration> Resources, IReadOnlyList<ResourceUsage> Usages, IReadOnlyList<WorkspaceDiagnostic> Diagnostics, IReadOnlyDictionary<string, string> Texts);

public sealed partial class WpfIndexService
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private sealed record Parsed(string Path, string Text, XDocument Xml, IReadOnlyList<string> ProjectPaths);
    private sealed record Candidate(ResourceDeclaration Declaration, XElement Element);
    [GeneratedRegex(@"\{(StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?([^{}\s,]+)\s*\}")]
    private static partial Regex ResourcePattern();
    [GeneratedRegex(@"\b(?:partial\s+)?class\s+(\w+)(?:\s*:\s*([^\r\n{]+))?")]
    private static partial Regex ClassPattern();

    public async Task<WpfIndexSnapshot> IndexAsync(WorkspaceSnapshot workspace, IReadOnlyDictionary<string, string>? buffers = null, CancellationToken token = default)
    {
        var items = new List<WpfItem>();
        var declarations = new List<Candidate>();
        var usages = new List<ResourceUsage>();
        var diagnostics = new List<WorkspaceDiagnostic>();
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new Dictionary<string, Parsed>(StringComparer.OrdinalIgnoreCase);
        var ownership = workspace.Projects.SelectMany(project => project.Files.Where(file => !file.IsGenerated).Select(file => (project.ProjectPath, File: file)))
            .GroupBy(entry => entry.File.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var owners in ownership)
        {
            var file = owners.First().File;
            var projectPaths = owners.Select(owner => owner.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            token.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file.Path).ToLowerInvariant();
            if (extension is ".png" or ".jpg" or ".jpeg" or ".ico" or ".svg" or ".ttf" or ".otf")
            { items.Add(new("Asset", file.Name, file.Path, 1)); continue; }
            if (extension is not (".xaml" or ".cs")) continue;
            string text;
            try { text = buffers?.GetValueOrDefault(file.Path) ?? await File.ReadAllTextAsync(file.Path, token); }
            catch (IOException) { continue; }
            texts[file.Path] = text;
            if (extension == ".cs")
            {
                foreach (Match match in ClassPattern().Matches(text))
                {
                    var name = match.Groups[1].Value;
                    var kind = name.EndsWith("ViewModel", StringComparison.Ordinal) ? "ViewModel" : match.Groups[2].Value.Contains("ValueConverter", StringComparison.Ordinal) ? "Converter" : null;
                    if (kind != null) items.Add(new(kind, name, file.Path, LineAt(text, match.Index)));
                }
                continue;
            }
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var xml = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
                if (xml.Root == null) continue;
                parsed[file.Path] = new(file.Path, text, xml, projectPaths);
                var rootKind = xml.Root.Name.LocalName;
                items.Add(new(rootKind, xml.Root.Attribute(Xaml + "Class")?.Value ?? file.Name, file.Path, 1));
                foreach (var element in xml.Descendants())
                {
                    var key = element.Attribute(Xaml + "Key");
                    if (key != null && !key.Value.StartsWith('{'))
                    {
                        var span = AttributeValueSpan(text, key);
                        var declaration = new ResourceDeclaration(key.Value, file.Path, Line(key), span.Start, span.Length, ScopeId(element.Parent));
                        declarations.Add(new(declaration, element));
                        items.Add(new(element.Name.LocalName, key.Value, file.Path, Line(key), key.Value));
                    }
                    else if (element.Name.LocalName is "Style" or "DataTemplate" or "ControlTemplate")
                        items.Add(new(element.Name.LocalName, element.Attribute("TargetType")?.Value ?? element.Attribute("DataType")?.Value ?? "Implicit template", file.Path, Line(element)));
                }
            }
            catch (XmlException ex) { diagnostics.Add(new("XAML001", ex.Message, "Warning", file.Path, ex.LineNumber, ex.LinePosition, 0, 0)); }
        }
        foreach (var group in declarations.GroupBy(d => (d.Declaration.Path, d.Declaration.Scope, d.Declaration.Key)).Where(g => g.Count() > 1))
        foreach (var item in group.Skip(1)) diagnostics.Add(new("XAML002", $"Duplicate resource key '{item.Declaration.Key}' in the same dictionary.", "Error", item.Declaration.Path, item.Declaration.Line, 1, item.Declaration.ValueStart, item.Declaration.ValueLength));

        var edges = parsed.Values.ToDictionary(p => p.Path, p => p.Xml.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary").Select(e => e.Attribute("Source")?.Value).OfType<string>().Select(source => ResolveDictionary(p, source, workspace)).OfType<string>().ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in parsed.Values)
        {
            token.ThrowIfCancellationRequested();
            if (HasCycle(file.Path, file.Path, edges, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                diagnostics.Add(new("XAML003", "Merged resource dictionaries contain a cycle.", "Error", file.Path, 1, 1, 0, 0));
            foreach (var attribute in file.Xml.Descendants().Attributes())
            {
                if (!attribute.Value.StartsWith('{') || attribute.Value.StartsWith("{}", StringComparison.Ordinal)) continue;
                var span = AttributeValueSpan(file.Text, attribute);
                // XML attributes expose decoded values; edits must use offsets in the original escaped source.
                var rawValue = file.Text.Substring(span.Start, span.Length);
                foreach (Match match in ResourcePattern().Matches(rawValue))
                {
                    var key = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
                    var keyStart = span.Start + match.Groups[2].Index;
                    var keyLength = match.Groups[2].Length;
                    var resolved = ResolveResource(file, attribute.Parent!, key, parsed, declarations, workspace);
                    var dynamic = match.Groups[1].Value == "DynamicResource";
                    usages.Add(new(key, file.Path, Line(attribute), keyStart, keyLength, dynamic, resolved?.Path, resolved?.ValueStart));
                    if (resolved == null && !dynamic)
                        diagnostics.Add(new("XAML004", $"Resource '{key}' could not be resolved statically; it may be provided by a library or at runtime.", "Info", file.Path, Line(attribute), 1, keyStart, keyLength));
                }
            }
        }
        return new(items.OrderBy(i => i.Kind).ThenBy(i => i.Name).ToArray(), declarations.Select(c => c.Declaration).ToArray(), usages, diagnostics, texts);
    }

    public IReadOnlyList<FileChange> RenameResource(WpfIndexSnapshot index, ResourceDeclaration resource, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Any(c => char.IsWhiteSpace(c) || "{}\"'<>,=&".Contains(c))) throw new ArgumentException("Use a nonempty resource key without whitespace or markup characters.");
        if (index.Resources.Any(r => r.Path == resource.Path && r.Scope == resource.Scope && r.Key == newName)) throw new InvalidOperationException("That key already exists in the same scope.");
        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase) { [resource.Path] = [new(resource.ValueStart, resource.ValueLength, newName)] };
        foreach (var usage in index.Usages.Where(u => u.ResolvedPath == resource.Path && u.ResolvedDeclarationStart == resource.ValueStart))
        {
            if (!changes.TryGetValue(usage.Path, out var edits)) changes[usage.Path] = edits = [];
            edits.Add(new(usage.Start, usage.Length, newName));
        }
        return changes.Select(c => new FileChange(c.Key, index.Texts[c.Key], WorkspaceEditTransaction.ApplyTextEdits(index.Texts[c.Key], c.Value), $"Rename resource {resource.Key} → {newName}")).ToArray();
    }

    private static ResourceDeclaration? ResolveResource(Parsed file, XElement element, string key, Dictionary<string, Parsed> parsed, List<Candidate> declarations, WorkspaceSnapshot workspace)
    {
        foreach (var ancestor in element.AncestorsAndSelf())
        {
            if (ancestor.Name.LocalName == "ResourceDictionary")
            {
                var found = SearchDictionary(file, ancestor, key, parsed, declarations, workspace, []);
                if (found != null) return found;
            }
            var resources = ancestor.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
            if (resources != null)
            {
                var found = SearchDictionary(file, resources, key, parsed, declarations, workspace, []);
                if (found != null) return found;
            }
        }
        // A linked control can run under different Applications. Its local resources remain safe to resolve,
        // but choosing an arbitrary owner's application resources could rename the wrong key.
        if (file.ProjectPaths.Count != 1) return null;
        foreach (var app in parsed.Values.Where(p => p.ProjectPaths.Contains(file.ProjectPaths[0], StringComparer.OrdinalIgnoreCase) && p.Xml.Root?.Name.LocalName == "Application"))
        {
            var resources = app.Xml.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Application.Resources");
            if (resources != null)
            {
                var found = SearchDictionary(app, resources, key, parsed, declarations, workspace, []);
                if (found != null) return found;
            }
        }
        return null;
    }
    private static ResourceDeclaration? SearchDictionary(Parsed file, XElement container, string key, Dictionary<string, Parsed> parsed, List<Candidate> declarations, WorkspaceSnapshot workspace, HashSet<string> visited)
    {
        var id = file.Path + ":" + ScopeId(container);
        if (!visited.Add(id)) return null;
        var direct = declarations.LastOrDefault(c => c.Declaration.Path == file.Path && c.Element.Parent == container && c.Declaration.Key == key);
        if (direct != null) return direct.Declaration;
        foreach (var child in container.Elements().Where(e => e.Name.LocalName == "ResourceDictionary").Reverse())
        {
            var value = SearchDictionary(file, child, key, parsed, declarations, workspace, visited);
            if (value != null) return value;
        }
        foreach (var merged in container.Elements().Where(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries").SelectMany(e => e.Elements()).Reverse())
        {
            var value = SearchDictionary(file, merged, key, parsed, declarations, workspace, visited);
            if (value != null) return value;
        }
        if (container.Attribute("Source") is { } source && ResolveDictionary(file, source.Value, workspace) is { } path && parsed.TryGetValue(path, out var other) && other.Xml.Root != null)
            return SearchDictionary(other, other.Xml.Root, key, parsed, declarations, workspace, visited);
        return null;
    }
    private static string? ResolveDictionary(Parsed file, string source, WorkspaceSnapshot workspace)
    {
        try
        {
        source = Uri.UnescapeDataString(source.Replace("pack://application:,,,", "", StringComparison.OrdinalIgnoreCase));
        var component = source.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (component >= 0)
        {
            var assembly = source[..component].TrimStart('/');
            var matches = workspace.Projects.Where(p => string.Equals(p.AssemblyName ?? p.Name, assembly, StringComparison.OrdinalIgnoreCase)).ToArray();
            var project = matches.Length == 1 ? matches[0] : null;
            return project == null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, source[(component + 11)..].Replace('/', Path.DirectorySeparatorChar)));
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out _)) return null;
        if (source.StartsWith('/') && file.ProjectPaths.Count != 1) return null;
        return Path.GetFullPath(Path.Combine(source.StartsWith('/') ? Path.GetDirectoryName(file.ProjectPaths[0])! : Path.GetDirectoryName(file.Path)!, source.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException) { return null; }
    }
    private static bool HasCycle(string start, string current, Dictionary<string, string[]> edges, HashSet<string> visited)
    {
        if (!visited.Add(current) || !edges.TryGetValue(current, out var children)) return false;
        return children.Any(child => string.Equals(child, start, StringComparison.OrdinalIgnoreCase) || HasCycle(start, child, edges, visited));
    }
    private static string ScopeId(XElement? element) => element == null ? "root" : $"{Line(element)}:{((IXmlLineInfo)element).LinePosition}";
    private static int Line(XObject obj) => ((IXmlLineInfo)obj).LineNumber;
    public static int LineAt(string text, int offset) => text.AsSpan(0, Math.Clamp(offset, 0, text.Length)).Count('\n') + 1;
    private static (int Start, int Length) AttributeValueSpan(string text, XAttribute attribute)
    {
        var info = (IXmlLineInfo)attribute;
        var offset = 0;
        for (var line = 1; line < info.LineNumber; line++) { var next = text.IndexOf('\n', offset); if (next < 0) break; offset = next + 1; }
        offset += info.LinePosition - 1;
        var equal = text.IndexOf('=', offset);
        var quote = equal + 1;
        while (quote < text.Length && char.IsWhiteSpace(text[quote])) quote++;
        var end = quote < text.Length ? text.IndexOf(text[quote], quote + 1) : -1;
        return (quote + 1, Math.Max(0, end - quote - 1));
    }
}
