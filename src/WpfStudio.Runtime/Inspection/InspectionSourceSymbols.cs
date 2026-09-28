using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Runtime.Inspection;

public sealed record InspectionSourceDocument(string Path, Guid ChecksumAlgorithm, string ChecksumHex);
public sealed record InspectionSourceResource(string Uri, string DocumentPath);
public sealed record InspectionSourceSymbolsResult(InspectionModule Module,
    IReadOnlyList<InspectionSourceDocument> Documents, string? Status = null, bool Truncated = false,
    IReadOnlyList<InspectionSourceResource>? Resources = null);

/// <summary>Reads symbols as data; application assemblies are never loaded into the IDE.</summary>
public static partial class InspectionSourceSymbols
{
    private const int MaximumPeBytes = 64 * 1024 * 1024;
    private const int MaximumPdbBytes = 32 * 1024 * 1024;
    private const int MaximumDocuments = 512;
    private const int MaximumDocumentRows = 16_384;
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    public static Task<InspectionSourceSymbolsResult> ReadAsync(InspectionModule module, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(module, cancellationToken), cancellationToken);

    private static InspectionSourceSymbolsResult Read(InspectionModule module, CancellationToken token)
    {
        try
        {
            if (!IsLocalAbsolutePath(module.Path)) return Unavailable("The loaded module has no absolute local file path.");
            token.ThrowIfCancellationRequested();
            using var pe = new PEReader(ImmutableArray.Create(ReadBounded(module.Path, MaximumPeBytes, token)));
            if (!pe.HasMetadata) return Unavailable("The module is not a managed PE image.");
            var metadata = pe.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != module.ModuleVersionId || module.ModuleVersionId == Guid.Empty)
                return Unavailable("The module file no longer matches the running application's module identity.");
            if (!metadata.IsAssembly || !MatchesAssembly(metadata, module))
                return Unavailable("The module file's assembly identity does not match the running application.");

            var entries = pe.ReadDebugDirectory();
            var codeViews = entries.Where(entry => entry.Type == DebugDirectoryEntryType.CodeView && entry.IsPortableCodeView).ToArray();
            // Never follow the path recorded by a build machine. Only the PDB
            // beside this exact module, with the same basename, is considered.
            var adjacent = System.IO.Path.ChangeExtension(module.Path, ".pdb");
            string? failure = null;
            if (codeViews.Length == 1 && File.Exists(adjacent))
            {
                try
                {
                    using var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(ReadBounded(adjacent, MaximumPdbBytes, token)));
                    var symbols = provider.GetMetadataReader();
                    if (MatchesPdb(pe, codeViews[0], symbols))
                        return Documents(module, pe, metadata, symbols, "Matched portable symbols beside the loaded module.", token);
                    failure = "The adjacent portable PDB does not match the module's debug identity.";
                }
                catch (Exception exception) when (IsReadFailure(exception)) { failure = "The adjacent PDB is unavailable or is not a valid portable PDB."; }
            }

            var embedded = entries.Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
            if (embedded.Length == 1)
            {
                // The framework decoder allocates the advertised expanded size.
                // Bound it before asking the decoder to decompress any bytes.
                var header = pe.GetSectionData(embedded[0].DataRelativeVirtualAddress).GetReader(0, 8);
                if (header.ReadUInt32() != 0x4244504d) return Unavailable("The embedded PDB header is invalid.");
                int expandedSize = header.ReadInt32();
                if (expandedSize <= 0 || expandedSize > MaximumPdbBytes)
                    return Unavailable("The embedded portable PDB exceeds the symbol-reading size limit.");
                using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
                var symbols = provider.GetMetadataReader();
                if (codeViews.Length > 1 || codeViews.Length == 1 && !MatchesPdb(pe, codeViews[0], symbols))
                    return Unavailable("The embedded PDB does not match the module's debug identity.");
                return Documents(module, pe, metadata, symbols, "Matched portable symbols embedded in the loaded module.", token);
            }
            return Unavailable(failure ?? (embedded.Length > 1 || codeViews.Length > 1
                ? "The module contains ambiguous symbol debug entries."
                : "No matching portable or embedded PDB is available. Windows PDBs are not supported for XAML source verification."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Unavailable("Source symbols could not be verified: " + Limit(exception.Message, 350));
        }

        InspectionSourceSymbolsResult Unavailable(string status) => new(module, [], status);
    }

    private static InspectionSourceSymbolsResult Documents(InspectionModule module, PEReader pe, MetadataReader metadata,
        MetadataReader reader, string provenance, CancellationToken token)
    {
        var documents = new List<InspectionSourceDocument>();
        var seen = new Dictionary<string, InspectionSourceDocument>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool truncated = false;
        int inspected = 0, skipped = 0;
        foreach (var handle in reader.Documents)
        {
            token.ThrowIfCancellationRequested();
            if (++inspected > MaximumDocumentRows || documents.Count >= MaximumDocuments) { truncated = true; break; }
            var document = reader.GetDocument(handle);
            var path = reader.GetString(document.Name);
            if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length > 8192 || !IsLocalAbsolutePath(path)) { skipped++; continue; }
            path = System.IO.Path.GetFullPath(path);
            var algorithm = reader.GetGuid(document.HashAlgorithm);
            var hash = reader.GetBlobReader(document.Hash);
            int expectedLength = algorithm == Sha256 ? 32 : algorithm == Sha1 ? 20 : 0;
            if (expectedLength == 0 || hash.Length != expectedLength) { skipped++; continue; }
            var candidate = new InspectionSourceDocument(path, algorithm, Convert.ToHexString(hash.ReadBytes(hash.Length)));
            if (ambiguous.Contains(path)) continue;
            if (seen.TryGetValue(path, out var existing))
            {
                if (candidate != existing) { documents.Remove(existing); seen.Remove(path); ambiguous.Add(path); }
                continue;
            }
            seen.Add(path, candidate);
            documents.Add(candidate);
        }
        string status = provenance;
        if (documents.Count == 0) status += " No verifiable absolute XAML document checksums were found.";
        if (skipped + ambiguous.Count > 0) status += " Some document paths or checksums were unsupported or ambiguous.";
        if (truncated) status += " The source-document limit was reached; additional documents are unavailable.";
        var resources = ReadResources(pe, metadata, reader, documents, token, out bool resourceLimit);
        if (resourceLimit) status += " The compiled-resource mapping limit was reached.";
        return new(module, documents.ToArray(), status, truncated || resourceLimit, resources);
    }

    private static bool MatchesAssembly(MetadataReader metadata, InspectionModule observed)
    {
        var definition = metadata.GetAssemblyDefinition();
        var actual = new AssemblyName
        {
            Name = metadata.GetString(definition.Name), Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : metadata.GetString(definition.Culture)
        };
        if (!definition.PublicKey.IsNil) actual.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
        var expected = new AssemblyName(observed.AssemblyFullName);
        return string.Equals(actual.Name, observed.AssemblyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase)
            && actual.Version == expected.Version
            && string.Equals(actual.CultureName ?? "", expected.CultureName ?? "", StringComparison.OrdinalIgnoreCase)
            && (actual.GetPublicKeyToken() ?? []).AsSpan().SequenceEqual(expected.GetPublicKeyToken() ?? []);
    }

    private static bool MatchesPdb(PEReader pe, DebugDirectoryEntry entry, MetadataReader symbols)
    {
        var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
        return codeView.Age == 1 && symbols.DebugMetadataHeader is { } header
            && new BlobContentId(header.Id) == new BlobContentId(codeView.Guid, entry.Stamp);
    }

    private static byte[] ReadBounded(string path, int maximum, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException("The file exceeds the symbol-reading size limit or is empty.");
        var bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
            if (read == 0) throw new EndOfStreamException("The file changed while its symbols were being read.");
            offset += read;
        }
        return bytes;
    }

    private static bool IsLocalAbsolutePath(string? path) => !string.IsNullOrWhiteSpace(path)
        && System.IO.Path.IsPathFullyQualified(path) && path.Length >= 3 && char.IsAsciiLetter(path[0])
        && path[1] == ':' && path[2] is '\\' or '/';
    private static bool IsReadFailure(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static string Limit(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
}
