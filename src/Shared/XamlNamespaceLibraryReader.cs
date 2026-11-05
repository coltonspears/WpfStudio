using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WpfStudio.PreviewDependencies;

internal static class XamlNamespaceLibraryReader
{
    /// <summary>
    /// Reads an assembly's XAML namespace declarations without loading it. Returns null for files that are not
    /// assemblies, reference assemblies, Visual Studio design-time assemblies, and assemblies that declare no namespace.
    /// </summary>
    public static XamlNamespaceLibrary? Read(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return null;
            // Design-time parts reference the Visual Studio designer, which is never present at run time.
            foreach (var handle in metadata.AssemblyReferences)
            {
                string reference = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (reference.StartsWith("Microsoft.VisualStudio.DesignTools.", StringComparison.Ordinal)
                    || reference.StartsWith("Microsoft.Windows.Design.", StringComparison.Ordinal)) return null;
            }
            var namespaces = new List<string>();
            var compatible = new List<KeyValuePair<string, string>>();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                string typeNamespace = metadata.GetString(type.Namespace), typeName = metadata.GetString(type.Name);
                if (typeNamespace == "System.Runtime.CompilerServices" && typeName == "ReferenceAssemblyAttribute") return null;
                if (typeNamespace != "System.Windows.Markup") continue;
                var value = metadata.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1) continue;
                if (typeName == "XmlnsDefinitionAttribute" && value.ReadSerializedString() is { Length: > 0 } uri) namespaces.Add(uri);
                else if (typeName == "XmlnsCompatibleWithAttribute" && value.ReadSerializedString() is { Length: > 0 } old
                    && value.ReadSerializedString() is { Length: > 0 } current) compatible.Add(new(old, current));
            }
            if (namespaces.Count == 0 && compatible.Count == 0) return null;
            string name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            return new(path, name, namespaces.Distinct(StringComparer.Ordinal).ToArray(), compatible);
        }
        catch (Exception exception) when (exception is BadImageFormatException or System.IO.IOException or UnauthorizedAccessException) { return null; }
    }
}
