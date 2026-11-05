using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WpfStudio.Runtime.Design;

/// <summary>The process a project's build has to be previewed in.</summary>
public enum PreviewRuntime
{
    /// <summary>The .NET host. Also used when there is no build to look at, for XAML that only needs WPF itself.</summary>
    DotNet,
    NetFramework,
    NetFramework32
}

/// <summary>
/// Chooses a preview host from the built assembly itself, not the project file: it is what will actually be loaded, and it is
/// the only source that knows a legacy application's Prefer32Bit or a multi-target project's selected framework.
/// </summary>
public static class PreviewHostSelection
{
    public static PreviewRuntime Select(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)) return PreviewRuntime.DotNet;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } header) return PreviewRuntime.DotNet;
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly || !TargetsNetFramework(metadata)) return PreviewRuntime.DotNet;
            // Prefer32Bit sets Requires32Bit as well, and a process has one bitness: code built for 32 bits, or
            // mixed-mode code compiled for x86, cannot load in a 64-bit host.
            bool native32 = pe.PEHeaders.CoffHeader.Machine == Machine.I386 && (header.Flags & CorFlags.ILOnly) == 0;
            return native32 || (header.Flags & CorFlags.Requires32Bit) != 0 ? PreviewRuntime.NetFramework32 : PreviewRuntime.NetFramework;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return PreviewRuntime.DotNet;
        }
    }

    private static bool TargetsNetFramework(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Name) != "TargetFrameworkAttribute" || metadata.GetString(type.Namespace) != "System.Runtime.Versioning") continue;
            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) break;
            return value.ReadSerializedString()?.StartsWith(".NETFramework,", StringComparison.OrdinalIgnoreCase) == true;
        }
        // Older compilers wrote no attribute. mscorlib without a netstandard or System.Runtime facade means .NET Framework.
        var references = metadata.AssemblyReferences.Select(reference => metadata.GetString(metadata.GetAssemblyReference(reference).Name)).ToArray();
        return references.Contains("mscorlib") && !references.Contains("netstandard") && !references.Contains("System.Runtime");
    }
}
