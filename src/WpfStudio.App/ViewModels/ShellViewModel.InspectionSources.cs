using System.IO;
using System.Security.Cryptography;
using System.Text;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IReadOnlyList<XamlBuildSource> _inspectionBuildSources = [];
    private WorkspaceSnapshot? _inspectionSourceWorkspace;

    internal sealed record VerifiedInspectionSource(DocumentStore Store, DocumentState Document, string Text, XamlRuntimeSourceResult Location,
        InspectionModule Module, Func<bool> ContextIsCurrent, FileStream ReadLease) : IDisposable
    {
        private readonly long _version = Document.Version;
        public bool IsCurrent() => ContextIsCurrent() && ReferenceEquals(Store.Find(Document.Path), Document)
            && Document.Version == _version && string.Equals(Document.Content, Text, StringComparison.Ordinal);
        public void Dispose() => ReadLease.Dispose();
    }

    private async Task<string> NavigateInspectionSourceAsync(InspectionSourceRequest request, CancellationToken token)
    {
        var verification = await VerifyInspectionSourceAsync(request.Node, request.Modules, request.IsCurrent, token);
        if (verification.Source is not { } source) return verification.Status;
        using (source) return NavigateVerifiedInspectionSource(source);
    }

    internal string NavigateVerifiedInspectionSource(VerifiedInspectionSource source, bool selectDeclaration = false)
    {
        if (!source.IsCurrent()) return "Source navigation cancelled because the source buffer or inspection context changed.";
        var target = source.Location.Location!;
        // Activating an editor can synchronously trigger selection/context and
        // buffer changes. Recheck the version captured by the verification lease.
        AddDocument(source.Document);
        var editor = ActiveDocument;
        if (editor?.State != source.Document || !Documents.Contains(editor) || !source.IsCurrent())
            return "Source navigation cancelled because the source buffer or inspection context changed.";
        // No awaits between the final buffer check and navigation.
        if (selectDeclaration)
        {
            if (!SelectVerifiedBindingSpan(editor, target, source.IsCurrent))
                return "Source navigation cancelled because the source buffer or inspection context changed.";
        }
        else editor.Navigate(target.Start);
        return $"Opened verified source location: {source.Document.Path}:{target.Line}:{target.Column}. Module, compiled resource, build checksum and editor buffer matched at navigation. Show XAML again to recheck after edits.";
    }

    private Task<(VerifiedInspectionSource? Source, string Status)> VerifyInspectionSourceAsync(
        InspectionNode node, InspectionModuleCatalog modules, Func<bool> isCurrent, CancellationToken token)
        => node.Source is { } hint
            ? VerifyInspectionSourceAsync(hint, modules, isCurrent,
                (path, text) => XamlRuntimeSourceLocator.Locate(path, text, hint.Line, hint.Column, node.Type, node.Name), token)
            : Task.FromResult<(VerifiedInspectionSource?, string)>((null, "WPF supplied no source location for this element."));

    private async Task<(VerifiedInspectionSource? Source, string Status)> VerifyInspectionSourceAsync(
        InspectionSourceHint hint, InspectionModuleCatalog modules, Func<bool> isCurrent,
        Func<string, string, XamlRuntimeSourceResult> locate, CancellationToken token)
    {
        var workspace = _inspectionSourceWorkspace; var sources = _inspectionBuildSources;
        bool Current() => isCurrent() && ReferenceEquals(workspace, Workspace)
            && ReferenceEquals(sources, _inspectionBuildSources);
        if (!Current() || workspace == null) return (null, "The workspace changed since this application was built. Launch with inspection again to verify source.");
        var resolved = await InspectionSourceResolver.ResolveAsync(hint, modules, token);
        if (!Current()) return (null, "Source verification cancelled because the inspection context changed.");
        if (resolved.Document is not { } document || resolved.Module is not { } module) return (null, resolved.Status);
        var source = sources.SingleOrDefault(s => s.Path.Equals(document.Path, StringComparison.OrdinalIgnoreCase));
        if (source == null) return (null, "This source file was not captured as a stable workspace input for the inspected build.");
        var expected = document.ChecksumAlgorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") ? source.Sha256
            : document.ChecksumAlgorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") ? source.Sha1 : null;
        if (expected == null || !expected.Equals(document.ChecksumHex, StringComparison.OrdinalIgnoreCase))
            return (null, "The compiled XAML checksum differs from the captured build input. Rebuild with inspection to navigate.");
        // Keep the file stable until the editor has been checked and positioned.
        // Only a path already captured from the workspace reaches this read.
        FileStream? stream = new(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length > 8 * 1024 * 1024) return (null, "The source file exceeds the navigation size limit.");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, "The XAML file changed after this application was built. Rebuild with inspection to navigate.");
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            var text = await reader.ReadToEndAsync(token);
            var state = _store.Find(source.Path) ?? await _store.OpenAsync(source.Path, token);
            if (!Current()) return (null, "Source verification cancelled because the inspection context changed.");
            if (!string.Equals(state.Content, text, StringComparison.Ordinal))
                return (null, "The open XAML buffer differs from the running build. Save and rebuild with inspection to navigate; your edits are preserved.");
            var location = locate(source.Path, text);
            if (location.Location is null) return (null, location.Status);
            var verified = new VerifiedInspectionSource(_store, state, text, location, module, Current, stream);
            stream = null;
            return (verified, location.Status);
        }
        finally { stream?.Dispose(); }
    }
}
