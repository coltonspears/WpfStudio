using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.BindingSources;

/// <summary>A selectable, observed expression. Labels never serve as navigation identity.</summary>
public sealed record BindingDeclarationItem(string Property, string OwnerType, string OwnerAssembly,
    string? PropertyId, string BindingId, BindingSourceDeclaration Declaration)
{
    public string Label => $"{Property} · {(Declaration.ParentExpressionId is null ? "Root" : "Child " + (Declaration.ChildIndex + 1))} {Declaration.Kind} · {Declaration.Path ?? Declaration.XPath ?? "(entire source)"}";
    public string Description => Declaration.Source is { } source
        ? $"Declaration hint: {source.Uri}:{source.Line}:{source.Column}. Source and binding identity are verified before navigation."
        : Declaration.UnavailableReason ?? "WPF supplied no source location for this binding declaration. Element navigation remains available.";

    public bool SameIdentity(BindingDeclarationItem? other) => other is not null && Property == other.Property
        && OwnerType == other.OwnerType && OwnerAssembly == other.OwnerAssembly && PropertyId == other.PropertyId
        && BindingId == other.BindingId && SameDeclaration(Declaration, other.Declaration);
    public static bool SameDeclaration(BindingSourceDeclaration left, BindingSourceDeclaration right) =>
        left.ExpressionId == right.ExpressionId && left.DeclarationId == right.DeclarationId
        && left.ParentExpressionId == right.ParentExpressionId && left.ChildIndex == right.ChildIndex
        && left.Kind == right.Kind && left.Path == right.Path && left.XPath == right.XPath && left.Source == right.Source;
}
