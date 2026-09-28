using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace WpfStudio.Runtime.Inspection;

public static partial class InspectionSourceSymbols
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> IlCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    private static IReadOnlyList<InspectionSourceResource> ReadResources(PEReader pe, MetadataReader metadata,
        MetadataReader symbols, IReadOnlyList<InspectionSourceDocument> documents, CancellationToken token, out bool truncated)
    {
        var paths = documents.Select(document => document.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, InspectionSourceResource>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        truncated = false;
        int methods = 0;
        foreach (var handle in metadata.MethodDefinitions)
        {
            token.ThrowIfCancellationRequested();
            if (++methods > 65_536 || resources.Count >= MaximumDocuments) { truncated = true; break; }
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != "InitializeComponent" || method.RelativeVirtualAddress == 0) continue;
            try
            {
                var debug = symbols.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(handle)));
                var sourceHandles = debug.GetSequencePoints().Select(point => point.Document).Where(document => !document.IsNil).Distinct().Take(2).ToArray();
                if (sourceHandles.Length != 1) continue;
                string path = symbols.GetString(symbols.GetDocument(sourceHandles[0]).Name);
                if (!IsLocalAbsolutePath(path)) continue;
                path = System.IO.Path.GetFullPath(path);
                if (!paths.Contains(path)) continue;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                if (body.Size > 65_536) { truncated = true; continue; }
                if (!TryReadComponentUri(body.GetILBytes() ?? [], metadata, out var uri)) continue;
                if (ambiguous.Contains(uri)) continue;
                var candidate = new InspectionSourceResource(uri, path);
                if (resources.TryGetValue(uri, out var previous) && !string.Equals(previous.DocumentPath, path, StringComparison.OrdinalIgnoreCase))
                { resources.Remove(uri); ambiguous.Add(uri); }
                else resources[uri] = candidate;
            }
            catch (Exception exception) when (IsReadFailure(exception)) { }
        }
        return resources.Values.ToArray();
    }

    private static bool TryReadComponentUri(byte[] il, MetadataReader metadata, out string uri)
    {
        uri = "";
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        int loadComponentCalls = 0;
        for (int offset = 0; offset < il.Length;)
        {
            ushort value = il[offset++];
            if (value == 0xfe)
            {
                if (offset == il.Length) return false;
                value = (ushort)(0xfe00 | il[offset++]);
            }
            if (!IlCodes.TryGetValue(value, out var code)) return false;
            int length = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                    or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch when il.Length - offset >= 4 && BitConverter.ToUInt32(il, offset) <= (uint)(il.Length - offset - 4) / 4
                    => 4 + (int)BitConverter.ToUInt32(il, offset) * 4,
                _ => -1
            };
            if (length < 0 || length > il.Length - offset) return false;
            if (code == OpCodes.Ldstr)
            {
                int stringToken = BitConverter.ToInt32(il, offset);
                if ((stringToken & unchecked((int)0xff000000)) != 0x70000000) return false;
                string text = metadata.GetUserString(MetadataTokens.UserStringHandle(stringToken & 0x00ffffff));
                if (text.Length <= 8192 && text.StartsWith('/') && text.Contains(";component/", StringComparison.OrdinalIgnoreCase)
                    && text.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) candidates.Add(text);
            }
            else if (code == OpCodes.Call && IsApplicationLoadComponent(metadata, BitConverter.ToInt32(il, offset))) loadComponentCalls++;
            offset += length;
        }
        if (loadComponentCalls != 1 || candidates.Count != 1) return false;
        uri = candidates.Single();
        return true;
    }

    private static bool IsApplicationLoadComponent(MetadataReader metadata, int token)
    {
        if ((token & unchecked((int)0xff000000)) != 0x0a000000) return false;
        var method = metadata.GetMemberReference(MetadataTokens.MemberReferenceHandle(token & 0x00ffffff));
        if (metadata.GetString(method.Name) != "LoadComponent" || method.Parent.Kind != HandleKind.TypeReference) return false;
        var type = metadata.GetTypeReference((TypeReferenceHandle)method.Parent);
        if (metadata.GetString(type.Name) != "Application" || metadata.GetString(type.Namespace) != "System.Windows"
            || type.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
        return metadata.GetString(assembly.Name) == "PresentationFramework"
            && metadata.GetBlobBytes(assembly.PublicKeyOrToken).AsSpan().SequenceEqual(new byte[] { 0x31, 0xbf, 0x38, 0x56, 0xad, 0x36, 0x4e, 0x35 });
    }
}
