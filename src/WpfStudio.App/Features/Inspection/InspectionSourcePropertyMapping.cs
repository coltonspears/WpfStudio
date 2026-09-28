using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Inspection;

/// <summary>Matches property metadata to the authored XAML type, including base-type roots with x:Class.</summary>
public static class InspectionSourcePropertyMapping
{
    public static InspectionSourcePropertyTarget? Match(InspectionSourcePropertyIdentity property,
        string? elementType, string? xmlNamespace, string sourceAssembly, InspectionModuleCatalog modules)
    {
        if (modules.Truncated || string.IsNullOrWhiteSpace(elementType) || xmlNamespace is null ||
            !KnownModule(property.TargetAssembly, property.TargetModuleVersionId, modules) ||
            !KnownModule(property.OwnerAssembly, property.OwnerModuleVersionId, modules)) return null;
        var targets = (property.AuthoredTargets ?? []).Where(target =>
            KnownModule(target.Assembly, target.ModuleVersionId, modules) &&
            target.Type[(target.Type.LastIndexOf('.') + 1)..] == elementType && NamespaceMatches(target, xmlNamespace, sourceAssembly)).ToArray();
        return targets.Length == 1 ? targets[0] : null;
    }

    private static bool KnownModule(string assembly, Guid mvid, InspectionModuleCatalog modules)
    {
        var matching = modules.Modules.Where(module => module.AssemblyName.Equals(assembly, StringComparison.OrdinalIgnoreCase)).ToArray();
        return mvid != Guid.Empty && matching.Length == 1 && matching[0].ModuleVersionId == mvid;
    }

    private static bool NamespaceMatches(InspectionSourcePropertyTarget target, string uri, string sourceAssembly)
    {
        const string prefix = "clr-namespace:";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return target.XmlNamespaces.Contains(uri, StringComparer.Ordinal);
        var pieces = uri[prefix.Length..].Split(';');
        if (pieces.Length > 2 || pieces.Length == 2 && !pieces[1].StartsWith("assembly=", StringComparison.Ordinal)) return false;
        string assembly = pieces.Length == 1 ? sourceAssembly : pieces[1]["assembly=".Length..];
        int separator = target.Type.LastIndexOf('.');
        return assembly.Length > 0 && string.Equals(assembly, target.Assembly, StringComparison.OrdinalIgnoreCase)
            && pieces[0] == (separator < 0 ? "" : target.Type[..separator]);
    }
}
