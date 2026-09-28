using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>An authored name identity, scoped to one document and project snapshot.</summary>
public sealed record XamlNameDeclaration(string Name, int Start, int Length, int ElementStart, int ScopeStart,
    string ScopeKind, bool IsRootScope, string AttributeName, string? RootClass, INamedTypeSymbol? RootType,
    INamedTypeSymbol ElementType);

/// <summary>One raw UTF-16 declaration or ElementName token, linked to its exact authored declaration.</summary>
public sealed record XamlNameOccurrence(XamlNameDeclaration Declaration, int Start, int Length, string Kind);

public sealed record XamlNameOccurrenceResult(IReadOnlyList<XamlNameDeclaration> Declarations,
    IReadOnlyList<XamlNameOccurrence> Occurrences, bool IsComplete, IReadOnlyList<string> Warnings,
    bool CoverageLimited = false)
{
    public XamlNameOccurrence? GetTarget(int position)
    {
        var matches = Occurrences.Where(occurrence => position >= occurrence.Start && position <= occurrence.Start + occurrence.Length).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

public sealed record XamlNameRenameValidation(bool Success, string? Error = null);

public sealed partial class XamlNameService
{
    private const int MaximumNameOccurrences = XamlNameScopeIndex.MaximumNames * 2;
    private const int MaximumNameExtensionWork = 32768;

    /// <summary>Captures authored identity only; a generated field requires a separate verified compiler bridge.</summary>
    public XamlNameOccurrenceResult GetNameOccurrences(string text, Compilation compilation, CancellationToken token = default)
    {
        var snapshot = Capture(text, compilation, token);
        if (!snapshot.WellFormed || snapshot.Index is not { IsComplete: true } index)
            return new([], [], false, [snapshot.Status ?? "Name references require a well-formed document within the namescope analysis budget."], true);

        var declarations = new List<XamlNameDeclaration>();
        var occurrences = new List<XamlNameOccurrence>();
        var known = new Dictionary<XamlNameScopeIndex.Declaration, XamlNameDeclaration>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        bool complete = true, limited = false;
        void Warn(string message, bool coverageLimited = false)
        {
            complete = false; limited |= coverageLimited;
            if (warnings.Count < 16) warnings.Add(message);
        }

        foreach (var declaration in index.Declarations)
        {
            token.ThrowIfCancellationRequested();
            if (index.GetDeclaration(declaration.Element) != declaration.Attribute || declaration.Type is null)
            {
                Warn("Invalid, duplicate, conflicting, or uncertain name declarations prevent complete name-reference coverage.");
                continue;
            }
            var root = declaration.Element;
            while (root.Parent is { } parent) root = parent;
            var item = new XamlNameDeclaration(declaration.Name, declaration.Start, declaration.Length,
                declaration.Element.Start, declaration.Scope.Owner.Start, declaration.Scope.Kind,
                declaration.Scope.Kind == "page" && declaration.Scope.Owner.Parent is null,
                declaration.Attribute.Name, root.Attribute(Language, "Class")?.Value.Text.Trim(),
                index.GetElementType(root), declaration.Type);
            declarations.Add(item); known[declaration] = item;
            occurrences.Add(new(item, item.Start, item.Length, "Declaration"));
        }
        foreach (var reference in snapshot.References)
        {
            token.ThrowIfCancellationRequested();
            if (index.GetScope(reference.Attribute.Owner) is null) continue;
            var declaration = reference.Complete ? index.ResolveDeclaration(reference.Attribute.Owner, reference.Name) : null;
            if (declaration is null || !known.TryGetValue(declaration, out var item))
            {
                Warn("An ElementName value is unresolved or ambiguous in the authored scope; runtime registration and outer-scope lookup are not inferred.");
                continue;
            }
            if (occurrences.Count >= MaximumNameOccurrences)
            { Warn("Name references reached the bounded occurrence limit.", true); break; }
            occurrences.Add(new(item, reference.Start, reference.Length, "ElementName"));
        }

        var types = new SchemaTypeResolver(compilation, token);
        var scopes = new HashSet<XamlNameScopeIndex.Scope>();
        var declarationAttributes = known.Keys.Select(declaration => declaration.Attribute).ToHashSet();
        var referenceAttributes = snapshot.References.Select(reference => reference.Attribute).ToHashSet();
        var referenceOwners = snapshot.References.Select(reference => reference.Attribute.Owner).ToHashSet();
        int extensionWork = MaximumNameExtensionWork;
        foreach (var element in index.Elements)
        {
            token.ThrowIfCancellationRequested();
            if (!XamlSchemaService.IgnoredElement(element) && element.Namespace == Language && element.LocalName == "Code"
                && !InsideRawData(element))
                Warn("Inline x:Code is not included in authored name refactoring.");
            // Ignored/design content and raw x:XData/x:Code subtrees have no indexed
            // scope. They must not contribute either occurrences or name guesses.
            if (index.GetScope(element) is not { } scope) continue;
            if (scopes.Add(scope) && (scope.UnknownNames || scope.Kind is "custom namescope" or "unresolved type boundary"))
                Warn("Custom, unresolved, or partially declared namescopes prevent complete authored name-reference coverage.");
            if (index.IsRuntimeNamePropertyElement(element))
                Warn("Runtime-name property elements are not included in name refactoring.");
            if (element.Namespace == Language && element.LocalName == "Reference")
                Warn("x:Reference uses a different name-resolution mechanism and is not included in name refactoring.");
            if (UnsupportedNameMember(element, element.Name, index, types, propertyElement: true))
                Warn("TargetName and SourceName consumers are not included in name refactoring.");

            string? runtimeName = index.GetRuntimeNameProperty(element);
            foreach (var attribute in element.Attributes)
            {
                token.ThrowIfCancellationRequested();
                if (XamlSchemaService.IgnoredAttribute(attribute)) continue;
                if (index.IsQualifiedRuntimeNameAttribute(attribute))
                    Warn("Qualified runtime-name attributes are not included in name refactoring.");
                if ((XamlNameScopeIndex.IsDirective(attribute, "Name") || runtimeName is not null && attribute.Name == runtimeName)
                    && !declarationAttributes.Contains(attribute))
                    Warn("A name declaration outside the supported unique page/template scopes is not included in refactoring.");
                if (UnsupportedNameMember(element, attribute.Name, index, types, propertyElement: false))
                    Warn("TargetName and SourceName consumers are not included in name refactoring.");
                if (QualifiedBindingNameMember(element, attribute.Name, index, types))
                    Warn("Qualified Binding name/source attributes are not included in name refactoring.");
                if (ParseExtension(attribute.Value.Text) is not { } extension) continue;
                var pending = new Stack<(Extension Extension, int Depth)>(); pending.Push((extension, 0));
                while (pending.TryPop(out var current))
                {
                    token.ThrowIfCancellationRequested();
                    if (--extensionWork < 0 || current.Depth > 64)
                    { Warn("Name-consumer analysis reached its markup-extension budget.", true); pending.Clear(); break; }
                    if (IsExtension(element, current.Extension, Language, "Reference"))
                        Warn("x:Reference uses a different name-resolution mechanism and is not included in name refactoring.");
                    bool isBinding = index.IsBindingExtension(element, current.Extension);
                    if (isBinding
                        && current.Extension.Arguments.Any(argument => argument.Name is { } name && QualifiedBindingNameMember(element, name, index, types)))
                        Warn("Qualified Binding name/source attributes are not included in name refactoring.");
                    if (isBinding && current.Extension.Arguments.Any(argument => argument.Name == "ElementName"))
                    {
                        bool supported = current.Depth == 0 && referenceAttributes.Contains(attribute);
                        if (!supported) Warn("Nested or conflicting ElementName source selectors are not included in name refactoring.");
                    }
                    foreach (var argument in current.Extension.Arguments)
                        if (ParseExtension(argument.Value) is { } nested) pending.Push((nested, current.Depth + 1));
                }
            }
            if (index.IsBinding(element) && (element.Attribute("ElementName") is not null
                || element.Children.Any(child => child.LocalName == "Binding.ElementName"))
                && !referenceOwners.Contains(element))
                Warn("Object Binding.ElementName property elements or conflicting source selectors are not included in name refactoring.");
        }
        return new(declarations.OrderBy(declaration => declaration.Start).ToArray(),
            occurrences.OrderBy(occurrence => occurrence.Start).ThenBy(occurrence => occurrence.Length).ToArray(),
            complete, warnings.Order(StringComparer.Ordinal).ToArray(), limited);
    }

    /// <summary>Checks an exact proposed name edit against freshly captured before/after semantic snapshots.</summary>
    public XamlNameRenameValidation ValidateRename(XamlNameOccurrenceResult before, XamlNameOccurrenceResult after,
        XamlNameDeclaration selected, string newName, IReadOnlyList<TextEdit> edits, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!before.IsComplete || before.CoverageLimited || !after.IsComplete || after.CoverageLimited)
            return new(false, "Rename requires complete, unambiguous authored name-reference coverage before and after the edit.");
        if (!XamlNameScopeIndex.ValidName(newName)) return new(false, "The proposed name is not a supported WPF name identifier.");
        if (!before.Declarations.Contains(selected)) return new(false, "The selected declaration does not belong to this source snapshot.");
        if (edits.Count > MaximumNameOccurrences || before.Occurrences.Count > MaximumNameOccurrences || after.Occurrences.Count > MaximumNameOccurrences)
            return new(false, "Name rename validation reached its occurrence budget.");
        var expected = before.Occurrences.Where(occurrence => occurrence.Declaration == selected)
            .Select(occurrence => (occurrence.Start, occurrence.Length)).ToHashSet();
        var ordered = edits.OrderBy(edit => edit.Start).ToArray();
        if (ordered.Length != expected.Count) return new(false, "The proposed edits do not cover exactly the selected declaration and its references.");
        int end = -1;
        var ends = new int[ordered.Length];
        var deltas = new int[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var edit = ordered[i];
            if (edit.Start < end || edit.Length <= 0 || edit.NewText != newName || !expected.Remove((edit.Start, edit.Length)))
                return new(false, "The proposed edits overlap, modify another name, or omit a verified reference.");
            ends[i] = end = edit.Start + edit.Length;
            deltas[i] = (i == 0 ? 0 : deltas[i - 1]) + newName.Length - edit.Length;
        }
        int Map(int position)
        {
            int i = Array.BinarySearch(ends, position);
            if (i < 0) i = ~i - 1;
            return position + (i < 0 ? 0 : deltas[i]);
        }
        if (before.Declarations.Count != after.Declarations.Count || before.Occurrences.Count != after.Occurrences.Count)
            return new(false, "Rename would add, remove, or lose an authored name or reference.");
        var afterDeclarations = after.Declarations.ToLookup(declaration => declaration.Start);
        var mapped = new Dictionary<XamlNameDeclaration, XamlNameDeclaration>();
        foreach (var declaration in before.Declarations)
        {
            token.ThrowIfCancellationRequested();
            var matches = afterDeclarations[Map(declaration.Start)].Take(2).ToArray();
            bool chosen = declaration == selected;
            if (matches.Length != 1) return new(false, "Rename lost the unique authored declaration identity.");
            var candidate = matches[0];
            if (candidate.Name != (chosen ? newName : declaration.Name) || candidate.Length != (chosen ? newName.Length : declaration.Length)
                || candidate.ElementStart != Map(declaration.ElementStart) || candidate.ScopeStart != Map(declaration.ScopeStart)
                || candidate.ScopeKind != declaration.ScopeKind || candidate.IsRootScope != declaration.IsRootScope
                || candidate.AttributeName != declaration.AttributeName || candidate.RootClass != declaration.RootClass
                || !SameNameType(candidate.RootType, declaration.RootType) || !SameNameType(candidate.ElementType, declaration.ElementType))
                return new(false, "Rename would change the identity, type, or scope of an authored name.");
            mapped[declaration] = candidate;
        }
        var afterOccurrences = after.Occurrences.ToLookup(occurrence => (occurrence.Start, occurrence.Length, occurrence.Kind));
        foreach (var occurrence in before.Occurrences)
        {
            token.ThrowIfCancellationRequested();
            int length = occurrence.Declaration == selected ? newName.Length : occurrence.Length;
            var matches = afterOccurrences[(Map(occurrence.Start), length, occurrence.Kind)].Take(2).ToArray();
            if (matches.Length != 1 || !mapped.TryGetValue(occurrence.Declaration, out var declaration) || matches[0].Declaration != declaration)
                return new(false, "Rename would change or lose the declaration targeted by an edited or untouched name reference.");
        }
        return new(true);
    }

    private static bool SameNameType(INamedTypeSymbol? left, INamedTypeSymbol? right) => left is null ? right is null
        : right is not null && left.ContainingAssembly.Identity.Equals(right.ContainingAssembly.Identity)
            && left.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == right.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool InsideRawData(Element element)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
            if (parent.Namespace == Language && parent.LocalName == "XData") return true;
        return false;
    }

    private static bool UnsupportedNameMember(Element element, string name, XamlNameScopeIndex index,
        SchemaTypeResolver types, bool propertyElement)
    {
        int dot = name.LastIndexOf('.');
        if (propertyElement && dot < 0) return false;
        string memberName = dot >= 0 ? name[(dot + 1)..] : name;
        if (memberName is not ("TargetName" or "SourceName")) return false;
        var owner = dot >= 0 ? types.Resolve(element, name[..dot]) : index.GetElementType(element);
        var member = owner is null ? null : SchemaMembers.Find(owner, memberName, attached: dot >= 0);
        return member is not null && new[] { "System.Windows.Setter", "System.Windows.Trigger", "System.Windows.Condition",
            "System.Windows.EventTrigger", "System.Windows.Media.Animation.Storyboard" }
            .Any(type => index.IsFrameworkType(member.Symbol.ContainingType, type));
    }

    private static bool QualifiedBindingNameMember(Element element, string name, XamlNameScopeIndex index, SchemaTypeResolver types)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && name[(dot + 1)..] is "ElementName" or "Source" or "RelativeSource"
            && index.IsFrameworkType(types.Resolve(element, name[..dot]), "System.Windows.Data.Binding");
    }
}
