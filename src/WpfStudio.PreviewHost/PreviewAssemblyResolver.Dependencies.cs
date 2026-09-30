using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using WpfStudio.Contracts;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewAssemblyResolver
{
    private readonly Dictionary<string, string> _missingDependencies = new(StringComparer.OrdinalIgnoreCase);

    private void StageDependencies(PreviewRequest request)
    {
        foreach (var asset in PreviewDependencyCatalog.Read(request).Assets)
        {
            string destination = PreviewDependencyCatalog.Within(_directory!, asset.RelativePath);
            if (File.Exists(destination)) continue;
            if (!File.Exists(asset.Source))
            {
                _missingDependencies[Path.GetFileNameWithoutExtension(asset.RelativePath)] = asset.Source;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset.Source, destination);
        }
    }

    public void PrepareXamlNamespaces(string text)
    {
        if (_directory is null) return;
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                    if ((reader.Prefix == "xmlns" || reader.Name == "xmlns") && !reader.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                        && reader.Value is not ("http://schemas.microsoft.com/winfx/2006/xaml/presentation" or "http://schemas.microsoft.com/winfx/2006/xaml"
                            or "http://schemas.microsoft.com/expression/blend/2008" or "http://schemas.openxmlformats.org/markup-compatibility/2006")) namespaces.Add(reader.Value);
                reader.MoveToElement();
            }
        if (namespaces.Count == 0) return;
        // XAML-only references may have no CLR AssemblyRef. Inspect assembly
        // metadata without constructing unrelated library types or attributes.
        foreach (string path in Directory.EnumerateFiles(_directory, "*.dll", SearchOption.AllDirectories))
        {
            bool matches = false;
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var metadata = pe.GetMetadataReader();
                if (!metadata.IsAssembly) continue;
                foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var attribute = metadata.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                    var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                    if (metadata.GetString(type.Namespace) != "System.Windows.Markup" || metadata.GetString(type.Name) != "XmlnsDefinitionAttribute") continue;
                    var value = metadata.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() == 1 && value.ReadSerializedString() is { } uri && namespaces.Contains(uri)) { matches = true; break; }
                }
            }
            catch (BadImageFormatException) { continue; }
            if (matches) Assembly.Load(AssemblyName.GetAssemblyName(path));
        }
    }
}
