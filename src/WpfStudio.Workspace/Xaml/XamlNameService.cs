using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed record XamlNameAnalysis(IReadOnlyList<WorkspaceDiagnostic> Diagnostics, bool IsComplete, string? Status = null);

/// <summary>Authoring assistance for proven XAML names and local ElementName declarations.</summary>
public sealed partial class XamlNameService
{
    private sealed record Reference(XamlSyntax.Attribute Attribute, string Name, int Start, int Length, int DecodedStart, bool Complete);
    private sealed record Snapshot(XamlNameScopeIndex? Index, IReadOnlyList<Reference> References, bool WellFormed, string? Status);

    public IReadOnlyList<WorkspaceDiagnostic> Analyze(string path, string text, long version, Compilation compilation, CancellationToken token = default)
        => AnalyzeDetailed(path, text, version, compilation, 2001, token).Diagnostics;

    internal IReadOnlyList<WorkspaceDiagnostic> AnalyzeBounded(string path, string text, long version, Compilation compilation,
        int maximumDiagnostics, CancellationToken token)
        => AnalyzeDetailed(path, text, version, compilation, maximumDiagnostics, token).Diagnostics;

    public XamlNameAnalysis AnalyzeDetailed(string path, string text, long version, Compilation compilation,
        int maximumDiagnostics = 2001, CancellationToken token = default)
    {
        var snapshot = Capture(text, compilation, token);
        if (snapshot.Index is not { IsComplete: true } index || !snapshot.WellFormed)
            return new([], false, snapshot.Status ?? "Name diagnostics await a well-formed XAML document.");
        int limit = Math.Clamp(maximumDiagnostics, 0, 2001);
        var diagnostics = new List<WorkspaceDiagnostic>();
        int suggestionWork = 1_000_000;
        var source = SourceText.From(text);
        bool Add(string id, string message, int start, int length, string severity = "Error")
        {
            if (diagnostics.Count >= limit) return false;
            var line = source.Lines.GetLinePosition(start);
            diagnostics.Add(new(id, message, severity, path, line.Line + 1, line.Character + 1, start, length));
            return true;
        }
        foreach (var declaration in index.Declarations)
        {
            token.ThrowIfCancellationRequested();
            string? id = declaration.Conflict ? "XAMLNAME003" : !declaration.Valid ? "XAMLNAME001"
                : declaration.Scope.CanResolve && declaration.Scope.Names[declaration.Name].Count > 1 ? "XAMLNAME002" : null;
            if (id is null) continue;
            string message = id switch
            {
                "XAMLNAME003" => "This element declares both x:Name and its runtime-name property, or repeats a name declaration. Keep one declaration.",
                "XAMLNAME001" => "A XAML name must begin with a letter or underscore and contain only XAML identifier characters; whitespace and punctuation are not allowed.",
                _ => $"The name '{declaration.Name}' is declared more than once in this authored {declaration.Scope.Kind} namescope."
            };
            if (!Add(id, message, declaration.Start, declaration.Length)) return Limited(diagnostics);
        }
        foreach (var reference in snapshot.References)
        {
            token.ThrowIfCancellationRequested();
            if (!reference.Complete || !index.MayReportMissing(reference.Attribute.Owner, reference.Name)) continue;
            string? suggestion = Suggest(index, reference, token, ref suggestionWork);
            string message = $"ElementName '{reference.Name}' has no declaration in this authored namescope. Runtime name registration or an outer runtime scope may still supply it."
                + (suggestion is null ? "" : $" Did you mean '{suggestion}'?");
            if (!Add("XAMLNAME004", message, reference.Start, reference.Length, "Warning")) return Limited(diagnostics);
        }
        return new(diagnostics, true);
    }

    public CompletionResult? Complete(string text, int position, long version, Compilation compilation, CancellationToken token = default)
    {
        position = Math.Clamp(position, 0, text.Length);
        var snapshot = Capture(text, compilation, token, position);
        if (snapshot.Index is not { IsComplete: true } index) return null;
        var reference = SelectReference(snapshot, position);
        if (reference is null) return null;
        int count = Math.Clamp(reference.Attribute.Value.PositionAt(position) - reference.DecodedStart, 0, reference.Name.Length);
        string fragment = reference.Name[..count];
        if (fragment.Length > 0 && !XamlNameScopeIndex.ValidName(fragment)) return new(version, reference.Start, reference.Length, []);
        var candidates = index.Candidates(reference.Attribute.Owner).Where(declaration => declaration.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(declaration => declaration.Name, StringComparer.Ordinal).Take(250)
            .Select(declaration => new CompletionEntry(declaration.Name, declaration.Name, declaration.Name,
                Description(declaration), ["Field"])).ToArray();
        return new(version, reference.Start, reference.Length, candidates);
    }

    public IReadOnlyList<SourceLocation> GetDefinition(string path, string text, int position, Compilation compilation, CancellationToken token = default)
    {
        var snapshot = Capture(text, compilation, token, position, declarations: true);
        var declaration = SelectedDeclaration(snapshot, position);
        if (declaration is null || !snapshot.WellFormed) return [];
        var line = SourceText.From(text).Lines.GetLinePosition(declaration.Start);
        return [new(path, declaration.Start, declaration.Length, line.Line + 1, line.Character + 1, Description(declaration))];
    }

    public XamlHoverInfo? GetHover(string path, string text, int position, Compilation compilation, CancellationToken token = default)
    {
        var snapshot = Capture(text, compilation, token, position, declarations: true);
        if (snapshot.Index is not { IsComplete: true } index) return null;
        var reference = SelectReference(snapshot, position);
        var declaration = SelectedDeclaration(snapshot, position);
        if (declaration is not null)
            return new(reference?.Start ?? declaration.Start, reference?.Length ?? declaration.Length,
                Description(declaration) + $"\nDeclared in this authored {declaration.Scope.Kind} namescope. Runtime registrations and template instances are not evaluated.");
        return reference is null ? null : new(reference.Start, reference.Length,
            $"ElementName '{Limit(reference.Name, 512)}' could not be resolved uniquely in the authored {index.ScopeDescription(reference.Attribute.Owner)}. Template application, outer runtime scopes, or custom registrations may be required.");
    }

    public IReadOnlyList<XamlCodeAction> GetCodeActions(string path, string text, int position, long version, Compilation compilation, CancellationToken token = default)
    {
        var snapshot = Capture(text, compilation, token, position);
        if (!snapshot.WellFormed || snapshot.Index is not { IsComplete: true } index) return [];
        var reference = SelectReference(snapshot, position);
        if (reference is null || !reference.Complete || !index.MayReportMissing(reference.Attribute.Owner, reference.Name)) return [];
        int suggestionWork = 1_000_000;
        string? suggestion = Suggest(index, reference, token, ref suggestionWork);
        if (suggestion is null) return [];
        return [new($"Change ElementName '{reference.Name}' to '{suggestion}'",
            new DocumentEdits(path, version, [new TextEdit(reference.Start, reference.Length, suggestion)],
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))) )];
    }

    private static XamlNameAnalysis Limited(IReadOnlyList<WorkspaceDiagnostic> diagnostics)
        => new(diagnostics, false, "XAML namescope diagnostics reached the response budget; coverage is incomplete.");

    private static Snapshot Capture(string text, Compilation compilation, CancellationToken token, int? position = null, bool declarations = false)
    {
        token.ThrowIfCancellationRequested();
        if (text.Length > XamlNameScopeIndex.MaximumCharacters)
            return new(null, [], false, "XAML namescope analysis reached its 1,000,000-character budget.");
        bool wellFormed = true;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = XamlNameScopeIndex.MaximumCharacters });
            int elementCount = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > 256 || reader.NodeType == XmlNodeType.Element && ++elementCount > XamlNameScopeIndex.MaximumElements)
                    return new(null, [], false, "XAML namescope analysis reached its element or depth budget.");
            }
        }
        catch (XmlException) { wellFormed = false; }
        var elements = Read(text, token);
        // Only a syntactic exclusion fast path: every retained context must still
        // pass the compiler-backed index. Ordinary Binding.Path completion should
        // not resolve every element type merely because this service runs first.
        if (position is { } caret && !PotentialContext(elements, caret, declarations)) return new(null, [], wellFormed, null);
        var index = new XamlNameScopeIndex(elements, compilation, token);
        if (!index.IsComplete) return new(index, [], wellFormed, index.Status);
        var references = new List<Reference>();
        foreach (var element in index.Elements)
        {
            token.ThrowIfCancellationRequested();
            if (XamlSchemaService.IgnoredElement(element)) continue;
            if (index.IsBinding(element))
            {
                var names = element.Attributes.Where(attribute => attribute.Name == "ElementName").ToArray();
                if (names.Length == 1 && !element.Attributes.Any(attribute => attribute.Name is "Source" or "RelativeSource")
                    && !element.Children.Any(child => child.LocalName is "Binding.Source" or "Binding.RelativeSource"))
                    references.Add(Token(names[0], 0, names[0].Value.Text.Length, stripQuotes: false, complete: true));
            }
            foreach (var attribute in element.Attributes)
            {
                if (XamlSchemaService.IgnoredAttribute(attribute)) continue;
                var extension = ParseExtension(attribute.Value.Text);
                if (extension is null || !index.IsBindingExtension(element, extension)) continue;
                var names = extension.Arguments.Where(argument => argument.Name == "ElementName").ToArray();
                if (names.Length != 1 || extension.Arguments.Any(argument => argument.Name is "Source" or "RelativeSource")) continue;
                references.Add(Token(attribute, names[0].Start, names[0].Length, stripQuotes: true, extension.IsComplete));
            }
            if (references.Count > XamlNameScopeIndex.MaximumNames)
                return new(null, [], wellFormed, "XAML namescope analysis reached its ElementName reference budget.");
        }
        return new(index, references, wellFormed, wellFormed ? null : "Name diagnostics await a well-formed XAML document.");
    }

    private static bool PotentialContext(IReadOnlyList<Element> elements, int position, bool declarations)
    {
        var attribute = elements.SelectMany(element => element.Attributes).FirstOrDefault(attribute => position >= attribute.ValueStart && position <= attribute.ValueEnd);
        if (attribute is null) return false;
        if (attribute.Name == "ElementName") return true;
        if (ParseExtension(attribute.Value.Text) is { } extension)
        {
            foreach (var argument in extension.Arguments.Where(argument => argument.Name == "ElementName"))
            {
                var reference = Token(attribute, argument.Start, argument.Length, true, extension.IsComplete);
                if (position >= reference.Start && position <= reference.Start + reference.Length) return true;
            }
            return false;
        }
        return declarations && XamlNameScopeIndex.ValidName(attribute.Value.Text);
    }

    private static Reference Token(XamlSyntax.Attribute attribute, int start, int length, bool stripQuotes, bool complete)
    {
        string value = attribute.Value.Text;
        int end = start + length;
        while (start < end && char.IsWhiteSpace(value[start])) start++;
        while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
        if (stripQuotes && start < end && value[start] is '\'' or '"')
        {
            char quote = value[start++];
            if (end > start && value[end - 1] == quote) end--;
            else complete = false;
            while (start < end && char.IsWhiteSpace(value[start])) start++;
            while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
        }
        var span = attribute.Value.Span(start, end - start);
        return new(attribute, value[start..end], span.Start, span.Length, start, complete);
    }
    private static Reference? SelectReference(Snapshot snapshot, int position)
        => snapshot.References.FirstOrDefault(reference => position >= reference.Start && position <= reference.Start + reference.Length);
    private static XamlNameScopeIndex.Declaration? SelectedDeclaration(Snapshot snapshot, int position)
    {
        if (snapshot.Index is not { IsComplete: true } index) return null;
        if (SelectReference(snapshot, position) is { } reference)
            return index.ResolveDeclaration(reference.Attribute.Owner, reference.Name);
        var declaration = index.Declarations.FirstOrDefault(item => position >= item.Start && position <= item.Start + item.Length);
        return declaration is not null && index.GetDeclaration(declaration.Element) == declaration.Attribute ? declaration : null;
    }
    private static string Description(XamlNameScopeIndex.Declaration declaration)
        => $"{Limit(declaration.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? declaration.Element.Name, 512)} {declaration.Name}";
    private static string Limit(string value, int maximum) => value.Length > maximum ? value[..maximum] + "…" : value;
    private static string? Suggest(XamlNameScopeIndex index, Reference reference, CancellationToken token, ref int remainingWork)
    {
        if (reference.Name.Length > 128 || remainingWork <= 0) return null;
        int best = reference.Name.Length <= 3 ? 1 : 2;
        string? result = null;
        bool ambiguous = false;
        foreach (var candidate in index.Candidates(reference.Attribute.Owner).Where(candidate => candidate.Name.Length <= 128).Take(8192))
        {
            token.ThrowIfCancellationRequested();
            if (Math.Abs(reference.Name.Length - candidate.Name.Length) > best) continue;
            int work = Math.Max(1, reference.Name.Length) * Math.Max(1, candidate.Name.Length);
            // A partial scan cannot prove that another candidate would not tie.
            if (work > remainingWork) { remainingWork = 0; return null; }
            remainingWork -= work;
            int distance = Distance(reference.Name, candidate.Name);
            if (distance > best) continue;
            if (result is not null && distance == best) { ambiguous = true; continue; }
            best = distance; result = candidate.Name; ambiguous = false;
        }
        return ambiguous ? null : result;
    }
    private static int Distance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (current, previous) = (previous, current);
        }
        return previous[^1];
    }
}
