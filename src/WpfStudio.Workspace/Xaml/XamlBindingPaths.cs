using Microsoft.CodeAnalysis;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlLanguageService
{
    private enum SegmentKind { Property, QualifiedProperty, Indexer, CurrentItem }
    private readonly record struct Segment(string Name, int Start, int Length, SegmentKind Kind = SegmentKind.Property,
        string? Owner = null, int OwnerStart = 0, int OwnerLength = 0);
    private sealed record BindingSourceInfo(ITypeSymbol? Type, XamlSyntax.Element? Element, string Reason)
    {
        public static BindingSourceInfo Unknown(string reason) => new(null, null, reason);
    }
    private sealed record BindingStep(Segment Segment, BindingSourceInfo Source, BindingSourceInfo Result, ISymbol? Symbol = null,
        bool IsMissing = false, string? Suggestion = null, INamedTypeSymbol? OwnerSymbol = null);

    private static bool TrySegments(string path, out IReadOnlyList<Segment> segments)
    {
        var found = new List<Segment>();
        segments = found;
        if (path.Length == 0 || path == ".") return true;
        var i = 0;
        var hasValue = false;
        while (i < path.Length)
        {
            if (char.IsWhiteSpace(path[i])) { i++; continue; }
            if (path[i] == '/')
            {
                found.Add(new Segment("/", i++, 1, SegmentKind.CurrentItem));
                hasValue = false;
                if (i < path.Length && path[i] is '/' or '.') return false;
                continue;
            }
            if (path[i] == '(')
            {
                if (hasValue) return false;
                int open = i++, close = path.IndexOf(')', i);
                if (close < 0 || path.AsSpan(i, close - i).Contains('(')) return false;
                int separator = close > i ? path.LastIndexOf('.', close - 1, close - i) : -1;
                int qualifiedStart = separator >= i ? separator + 1 : i, qualifiedEnd = close;
                // WPF trims the two halves of (Owner.Member), but (Member) only
                // strips parentheses. Interior padding is not an ordinary member name.
                if (separator >= i) TrimRange(path, ref qualifiedStart, ref qualifiedEnd);
                string qualifiedName = path[qualifiedStart..qualifiedEnd];
                if (!IsIdentifier(qualifiedName, allowEmpty: false)) return false; // Includes (0) path parameters.
                string? owner = null;
                int ownerStart = open + 1, ownerEnd = separator;
                if (separator >= i)
                {
                    TrimRange(path, ref ownerStart, ref ownerEnd);
                    owner = path[ownerStart..ownerEnd];
                    if (!IsTypeName(owner)) return false;
                }
                found.Add(new Segment(qualifiedName, qualifiedStart, qualifiedEnd - qualifiedStart, SegmentKind.QualifiedProperty,
                    owner, ownerStart, owner is null ? 0 : ownerEnd - ownerStart));
                i = close + 1;
                hasValue = true;
                continue;
            }
            if (path[i] == '.')
            {
                if (!hasValue || ++i >= path.Length) return false;
                hasValue = false;
                continue;
            }
            if (path[i] == '[')
            {
                var start = i++;
                var quote = '\0';
                for (; i < path.Length; i++)
                {
                    var c = path[i];
                    if (c == '^' && i + 1 < path.Length) { i++; continue; }
                    if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                    if (c is '\'' or '"') { quote = c; continue; }
                    if (c == ']') break;
                    if (c is '[' or '(' or ')') return false;
                }
                if (i >= path.Length) return false;
                i++;
                found.Add(new Segment(path[(start + 1)..(i - 1)], start, i - start, SegmentKind.Indexer));
                hasValue = true;
                continue;
            }
            if (hasValue) return false;
            var memberStart = i;
            while (i < path.Length && path[i] is not ('.' or '/' or '[')) i++;
            int memberEnd = i;
            TrimRange(path, ref memberStart, ref memberEnd);
            var name = path[memberStart..memberEnd];
            if (!IsIdentifier(name, allowEmpty: false)) return false;
            found.Add(new Segment(name, memberStart, name.Length));
            hasValue = true;
        }
        return hasValue || found.Count == 0 || found[^1].Kind == SegmentKind.CurrentItem;
    }

    private sealed record QualifiedCompletion(bool OwnerPosition, string? Owner, int PrefixEnd);

    private static bool CompletionMember(string path, int caret, out int start, out int end, out QualifiedCompletion? qualified)
    {
        start = 0; end = path.Length; qualified = null;
        var brackets = 0;
        var quote = '\0';
        int parenthesis = -1;
        for (var i = 0; i < caret; i++)
        {
            var c = path[i];
            if (c == '^' && brackets > 0 && i + 1 < caret) { i++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (brackets > 0 && c is '\'' or '"') { quote = c; continue; }
            if (c == '[') brackets++;
            else if (c == ']') brackets--;
            else if (brackets == 0 && c == '(') { if (parenthesis >= 0) return false; parenthesis = i; }
            else if (brackets == 0 && c == ')') { if (parenthesis < 0) return false; parenthesis = -1; }
            else if (brackets == 0 && parenthesis < 0 && c is '.' or '/') start = i + 1;
        }
        if (brackets != 0 || quote != '\0') return false;
        if (parenthesis >= 0)
        {
            int close = path.IndexOf(')', parenthesis + 1);
            if (close < 0) close = path.Length;
            if (path.AsSpan(parenthesis + 1, close - parenthesis - 1).Contains('(')) return false;
            int dot = path.LastIndexOf('.', Math.Max(parenthesis, close - 1), close - parenthesis - 1);
            bool ownerPosition = dot < 0 || caret <= dot;
            int ownerStart = parenthesis + 1, ownerEnd = dot < 0 ? close : dot;
            TrimRange(path, ref ownerStart, ref ownerEnd);
            qualified = new(ownerPosition, dot < 0 ? null : path[ownerStart..ownerEnd], parenthesis);
            start = ownerPosition ? ownerStart : dot + 1;
            end = ownerPosition ? ownerEnd : close;
            TrimRange(path, ref start, ref end);
            return caret >= start && caret <= end;
        }
        for (var i = caret; i < path.Length; i++)
            if (path[i] is '.' or '/' or '[') { end = i; break; }
        TrimRange(path, ref start, ref end);
        return caret >= start && caret <= end;
    }

    private static void TrimRange(string text, ref int start, ref int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
    }

    private static bool IsTypeName(string name)
    {
        int colon = name.IndexOf(':');
        return colon < 0 ? IsIdentifier(name, false) : IsIdentifier(name[..colon], false) && IsIdentifier(name[(colon + 1)..], false);
    }

    private static ITypeSymbol? ItemType(ITypeSymbol? symbol)
    {
        if (symbol is IArrayTypeSymbol array) return array.ElementType;
        if (symbol is not INamedTypeSymbol type || type.SpecialType == SpecialType.System_String) return null;
        var candidates = type.AllInterfaces.Prepend(type)
            .Where(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            .Select(i => i.TypeArguments[0]).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
    private static IEnumerable<IPropertySymbol> CompletionProperties(ITypeSymbol? source)
    {
        if (source is null) return [];
        return Properties(source).Concat(ItemType(source) is { } item ? Properties(item) : [])
            .DistinctBy(p => p.Name, StringComparer.Ordinal);
    }
    private static string MissingMessage(BindingStep step)
    {
        var message = $"Property '{step.Segment.Name}' was not found as a public readable instance property on the declared binding source '{step.Source.Type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'.";
        if (step.Suggestion is not null) message += $" Did you mean '{step.Suggestion}'?";
        return message;
    }
}
