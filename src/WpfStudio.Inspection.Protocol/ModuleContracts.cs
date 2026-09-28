namespace WpfStudio.Inspection.Protocol;

// These identities describe loaded modules, not the current bytes at Path.
// The IDE must verify the file's MVID and matching PDB before using its source data.
public sealed record InspectionModule(string AssemblyName, string AssemblyFullName, string Path,
    Guid ModuleVersionId, bool IsResourceAssembly = false);

public sealed record InspectionModuleCatalog(IReadOnlyList<InspectionModule> Modules,
    bool Truncated = false, string? Status = null);
