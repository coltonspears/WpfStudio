using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlLanguageService
{
    private sealed record QualifiedMember(string Name, ISymbol Symbol, ITypeSymbol ValueType, bool DependencyProperty);

    private sealed partial class SourceResolver
    {
        private BindingStep QualifiedStep(BindingSourceInfo source, Segment segment, Element syntaxOwner)
        {
            if (segment.Owner is null)
            {
                var ordinary = Step(source, segment with { Kind = SegmentKind.Property }, syntaxOwner);
                return ordinary with { Segment = segment };
            }
            var owner = ResolveType(syntaxOwner, segment.Owner);
            if (owner is null || !SchemaMembers.IsComplete(owner))
                return new(segment, source, BindingSourceInfo.Unknown("The qualified property owner is unavailable or ambiguous."));
            var member = FindQualifiedMember(owner, segment.Name, source.Type);
            if (member is null)
            {
                // DP lookup uses WPF's runtime registration table, not a Get/Set method call.
                // A missing conventional wrapper is therefore not proof of a missing DP.
                return new(segment, source, BindingSourceInfo.Unknown("The qualified member has no unambiguous readable metadata applicable to the declared source; runtime dependency-property registrations are not evaluated."), OwnerSymbol: owner);
            }
            if (member.Symbol is IPropertySymbol { IsStatic: false } property && property.Name == "DataContext"
                && property.ContainingType.ToDisplayString() is "System.Windows.FrameworkElement" or "System.Windows.FrameworkContentElement"
                && source.Element is { } element)
                return new(segment, source, Context(element, 0), property, OwnerSymbol: owner);
            var reason = source.Reason + " → " + segment.Owner + "." + member.Name;
            if (member.DependencyProperty) reason += " (declared dependency-property value type; registration is not executed)";
            return new(segment, source, new BindingSourceInfo(member.ValueType, null, reason), member.Symbol, OwnerSymbol: owner);
        }

        public IEnumerable<QualifiedMember> QualifiedMembers(Element syntaxOwner, string ownerName, ITypeSymbol? source)
        {
            var owner = ResolveType(syntaxOwner, ownerName);
            if (owner is null || !SchemaMembers.IsComplete(owner)) yield break;
            var names = SchemaMembers.Instance(owner).Where(member => !member.IsEvent).Select(member => member.Name)
                .Concat(SchemaMembers.Attached(owner).Where(member => !member.IsEvent).Select(member => member.Name))
                .Concat(TypeHierarchy(owner).SelectMany(type => type.GetMembers().OfType<IPropertySymbol>())
                    .Where(property => property.IsStatic).Select(property => property.Name));
            int examined = 0;
            foreach (string name in names.Distinct(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (++examined > 4096) yield break;
                if (FindQualifiedMember(owner, name, source) is { } member) yield return member;
            }
        }

        public IEnumerable<INamedTypeSymbol> QualifiedOwnerTypes(Element syntaxOwner, string prefix)
        {
            var uri = syntaxOwner.LookupNamespace(prefix);
            return uri is null ? [] : _types.Types(SchemaTypeResolver.WithDeclaringAssembly(uri, _resources.DeclaringAssembly(syntaxOwner)));
        }

        private QualifiedMember? FindQualifiedMember(INamedTypeSymbol owner, string name, ITypeSymbol? source)
        {
            token.ThrowIfCancellationRequested();
            if (source is not null && Normalize(source) is null) return null;
            var dpType = compilation.GetTypeByMetadataName("System.Windows.DependencyProperty");
            var dependencyObject = compilation.GetTypeByMetadataName("System.Windows.DependencyObject");
            var fields = FirstDeclaredMembers(owner, name + "Property").OfType<IFieldSymbol>().ToArray();
            var properties = FirstDeclaredMembers(owner, name).OfType<IPropertySymbol>()
                .Where(property => !property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public
                    && property.GetMethod is { DeclaredAccessibility: Accessibility.Public }).Take(2).ToArray();
            IPropertySymbol? property = properties.Length == 1 ? properties[0] : null;
            if (fields.Length != 0)
            {
                if (fields.Length != 1 || fields[0] is not { IsStatic: true, IsReadOnly: true, DeclaredAccessibility: Accessibility.Public }
                    || dpType is null || !SymbolEqualityComparer.Default.Equals(fields[0].Type, dpType)
                    || dependencyObject is null || !AcceptsSource(source, dependencyObject)) return null;
                var getters = FirstDeclaredMembers(owner, "Get" + name).OfType<IMethodSymbol>()
                    .Where(method => method.IsStatic && !method.IsGenericMethod && method.DeclaredAccessibility == Accessibility.Public
                        && !method.ReturnsVoid && method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None).Take(2).ToArray();
                if (getters.Length == 1 && AcceptsSource(source, getters[0].Parameters[0].Type)
                    && AcceptsSource(getters[0].Parameters[0].Type, dependencyObject) && UsableType(getters[0].ReturnType))
                    return new(name, getters[0], getters[0].ReturnType, true);
                if (getters.Length == 0 && property is { IsStatic: false } && AcceptsSource(source, owner) && UsableType(property.Type))
                    return new(name, property, property.Type, true);
                return null;
            }
            if (property is null || !UsableType(property.Type) || !property.IsStatic && !AcceptsSource(source, owner)) return null;
            // A CLR property is a real readable symbol. Runtime custom type descriptors are
            // still an uncertainty boundary, just as for an unqualified property segment.
            if (!property.IsStatic && Normalize(source) is null) return null;
            return new(name, property, property.Type, false);
        }

        private bool AcceptsSource(ITypeSymbol? source, ITypeSymbol target)
        {
            if (source is null || source.TypeKind is TypeKind.Error or TypeKind.Dynamic || target.TypeKind is TypeKind.Error or TypeKind.Dynamic) return false;
            var conversion = compilation.ClassifyCommonConversion(source, target);
            return conversion.IsIdentity || conversion.IsReference && conversion.IsImplicit;
        }

        private static bool UsableType(ITypeSymbol type, int depth = 0) => depth < 32
            && type.TypeKind is not (TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer)
            && (type is not IArrayTypeSymbol array || UsableType(array.ElementType, depth + 1))
            && (type is not INamedTypeSymbol named || named.TypeArguments.All(argument => UsableType(argument, depth + 1)));

        private static IEnumerable<ISymbol> FirstDeclaredMembers(INamedTypeSymbol owner, string name)
        {
            foreach (var type in TypeHierarchy(owner))
            {
                var members = type.GetMembers(name);
                if (members.Length > 0) return members;
            }
            return [];
        }
    }
}
