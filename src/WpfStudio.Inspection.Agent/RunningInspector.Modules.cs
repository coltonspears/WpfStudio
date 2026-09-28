using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    public InspectionModuleCatalog GetModuleCatalog()
    {
        const int maximumModules = 512;
        const int maximumMetadataCharacters = 256 * 1024;
        var modules = new List<InspectionModule>();
        var ownContext = AssemblyLoadContext.GetLoadContext(typeof(RunningInspector).Assembly);
        Assembly? resourceAssembly = null;
        bool resourceAssemblyUnavailable = false;
        try { resourceAssembly = Application.ResourceAssembly; }
        catch (Exception) { resourceAssemblyUnavailable = true; }

        bool truncated = false;
        bool metadataUnavailable = false;
        int characters = 0;
        var watch = Stopwatch.StartNew();
        // The runtime supplies the already-loaded instances from all contexts.
        // Read only runtime assembly metadata: do not resolve references, open
        // Location, or inspect types and application-defined attributes/getters.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (modules.Count >= maximumModules || watch.ElapsedMilliseconds >= 250)
            {
                truncated = true;
                break;
            }
            try
            {
                if (assembly.IsDynamic || AssemblyLoadContext.GetLoadContext(assembly) == ownContext) continue;
                var name = assembly.GetName().Name;
                var fullName = assembly.FullName;
                var path = assembly.Location;
                var mvid = assembly.ManifestModule.ModuleVersionId;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullName) || mvid == Guid.Empty)
                {
                    metadataUnavailable = true;
                    continue;
                }
                // Never truncate an identity/path into a different valid-looking
                // identity. Bound the aggregate too, including JSON escape overhead.
                int length = name.Length + fullName.Length + path.Length;
                if (name.Length > 1024 || fullName.Length > 4096 || path.Length > 8192 ||
                    length > maximumMetadataCharacters - characters)
                {
                    truncated = true;
                    continue;
                }
                characters += length;
                // Preserve duplicate identities in different load contexts. The
                // resolver must reject ambiguity instead of silently picking one.
                modules.Add(new(name, fullName, path, mvid, ReferenceEquals(assembly, resourceAssembly)));
            }
            catch (Exception) { metadataUnavailable = true; }
        }
        var statuses = new List<string>();
        if (truncated) statuses.Add("The loaded module catalog reached its size or time limit.");
        if (metadataUnavailable) statuses.Add("Metadata for some loaded modules was unavailable.");
        if (resourceAssemblyUnavailable) statuses.Add("The application's resource assembly was unavailable.");
        return new(modules, truncated || metadataUnavailable, statuses.Count == 0 ? null : string.Join(" ", statuses));
    }
}
