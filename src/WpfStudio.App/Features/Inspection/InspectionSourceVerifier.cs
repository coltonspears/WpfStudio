using System.IO;
using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.Features.Inspection;

/// <summary>What source verification needs from the shell: the workspace the inspected build belongs to.</summary>
public interface IInspectionSourceHost
{
    WorkspaceSnapshot? Workspace { get; }
}

/// <summary>
/// A source location proven to match the running application: the open buffer equals the file the build consumed,
/// and the read lease keeps the file stable until the editor has been positioned. Dispose releases the lease.
/// </summary>
public sealed record VerifiedInspectionSource(DocumentStore Store, DocumentState Document, string Text, XamlRuntimeSourceResult Location,
    InspectionModule Module, Func<bool> ContextIsCurrent, FileStream ReadLease) : IDisposable
{
    private readonly long _version = Document.Version;
    public bool IsCurrent() => ContextIsCurrent() && ReferenceEquals(Store.Find(Document.Path), Document)
        && Document.Version == _version && string.Equals(Document.Content, Text, StringComparison.Ordinal);
    public void Dispose() => ReadLease.Dispose();
}

/// <summary>
/// Proves that a source location reported by the running application still describes what is on disk and in the
/// editor: the compiled checksum matches the build input captured at launch, the file still hashes the same, and the
/// open buffer equals that text. Only then may navigation or a source edit trust the reported offsets.
/// </summary>
public sealed class InspectionSourceVerifier(IInspectionSourceHost host, DocumentStore store)
{
    private static readonly Guid Sha256Algorithm = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1Algorithm = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private const long MaximumSourceBytes = 8 * 1024 * 1024;
    private IReadOnlyList<XamlBuildSource> _buildSources = [];
    private WorkspaceSnapshot? _workspace;

    /// <summary>Records the stable build inputs of the application that was just launched with inspection.</summary>
    public void Capture(IReadOnlyList<XamlBuildSource> buildSources, WorkspaceSnapshot? workspace)
    {
        _buildSources = buildSources; _workspace = workspace;
    }

    public Task<(VerifiedInspectionSource? Source, string Status)> VerifyAsync(InspectionNode node, InspectionModuleCatalog modules, Func<bool> isCurrent, CancellationToken token)
        => node.Source is { } hint
            ? VerifyAsync(hint, modules, isCurrent, (path, text) => XamlRuntimeSourceLocator.Locate(path, text, hint.Line, hint.Column, node.Type, node.Name), token)
            : Task.FromResult<(VerifiedInspectionSource?, string)>((null, "WPF supplied no source location for this element."));

    public async Task<(VerifiedInspectionSource? Source, string Status)> VerifyAsync(InspectionSourceHint hint, InspectionModuleCatalog modules, Func<bool> isCurrent,
        Func<string, string, XamlRuntimeSourceResult> locate, CancellationToken token)
    {
        var workspace = _workspace; var sources = _buildSources;
        // The same solution or project, as opposed to the same snapshot of it: the project model is refreshed while the
        // application runs, and what proves a location is the build inputs captured at launch, checked below.
        bool Current() => isCurrent() && string.Equals(workspace?.Path, host.Workspace?.Path, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(sources, _buildSources);
        if (!Current() || workspace == null) return (null, "The workspace changed since this application was built. Launch with inspection again to verify source.");
        var resolved = await InspectionSourceResolver.ResolveAsync(hint, modules, token);
        if (!Current()) return (null, "Source verification cancelled because the inspection context changed.");
        if (resolved.Document is not { } document || resolved.Module is not { } module) return (null, resolved.Status);
        var source = sources.SingleOrDefault(s => s.Path.Equals(document.Path, StringComparison.OrdinalIgnoreCase));
        if (source == null) return (null, "This source file was not captured as a stable workspace input for the inspected build.");
        var expected = document.ChecksumAlgorithm == Sha256Algorithm ? source.Sha256 : document.ChecksumAlgorithm == Sha1Algorithm ? source.Sha1 : null;
        if (expected == null || !expected.Equals(document.ChecksumHex, StringComparison.OrdinalIgnoreCase))
            return (null, "The compiled XAML checksum differs from the captured build input. Rebuild with inspection to navigate.");
        // Keep the file stable until the editor has been checked and positioned.
        // Only a path already captured from the workspace reaches this read.
        FileStream? stream = new(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length > MaximumSourceBytes) return (null, "The source file exceeds the navigation size limit.");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, "The XAML file changed after this application was built. Rebuild with inspection to navigate.");
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            var text = await reader.ReadToEndAsync(token);
            var state = store.Find(source.Path) ?? await store.OpenAsync(source.Path, token);
            if (!Current()) return (null, "Source verification cancelled because the inspection context changed.");
            if (!string.Equals(state.Content, text, StringComparison.Ordinal))
                return (null, "The open XAML buffer differs from the running build. Save and rebuild with inspection to navigate; your edits are preserved.");
            var location = locate(source.Path, text);
            if (location.Location is null) return (null, location.Status);
            var verified = new VerifiedInspectionSource(store, state, text, location, module, Current, stream);
            stream = null;
            return (verified, location.Status);
        }
        finally { stream?.Dispose(); }
    }
}
