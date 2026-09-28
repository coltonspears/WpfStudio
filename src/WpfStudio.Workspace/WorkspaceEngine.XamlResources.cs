using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private const int ResourceSnapshotFiles = 512;
    private const int ResourceSnapshotCharacters = 8_000_000;
    private readonly Dictionary<ProjectId, ResourceInventory> _xamlResourceInventory = [];
    private sealed record ResourceInventory(IReadOnlyList<ProjectDiscovery.EvaluatedXamlResource> Items, bool IsExecutable);
    private sealed record ResourceText(DiskText Read, long? Version, string? Hash);
    private sealed record ResourceProject(Project Project, string Assembly, IReadOnlyList<ProjectDiscovery.EvaluatedXamlResource> Items,
        IReadOnlySet<ProjectId> Reachable, string? ApplicationPath, bool Complete, string? Status);

    // Every operation owns this snapshot. Neither the shared language service nor
    // this engine retains request overlays or resource parse state between calls.
    private sealed class ResourceSnapshot(
        IReadOnlyDictionary<string, ResourceText> texts, IReadOnlyDictionary<ProjectId, ResourceProject> projects,
        IReadOnlyDictionary<ProjectId, XamlResourceIndex> indexes, bool complete, string? status)
    {
        private readonly ConcurrentDictionary<ProjectId, string> _fingerprints = new();
        public IReadOnlyDictionary<string, ResourceText> Texts { get; } = texts;
        public bool Complete { get; } = complete;
        public string? Status { get; } = status;

        public XamlResourceContext Context(Project project, string path, IReadOnlyDictionary<string, string>? replacements = null)
        {
            if (!projects.TryGetValue(project.Id, out var source) || !indexes.TryGetValue(project.Id, out var index))
                return new(path, project.AssemblyName ?? "", [], IsComplete: false, Status: "The evaluated resource context is unavailable.");
            var documents = new List<XamlResourceDocument>();
            foreach (var item in index.Documents)
            {
                // Unloaded identities remain in the complete index. They must not
                // become null-text snapshots or consume the loaded-document budget.
                if (!Texts.TryGetValue(item.Path, out var content)) continue;
                string? text = content.Read.Text;
                if (text is not null && replacements?.TryGetValue(item.Path, out var replacement) == true) text = replacement;
                documents.Add(item with { Text = text, Status = content.Read.Status ?? item.Status, Version = content.Version });
            }
            return new(path, source.Assembly, documents, source.ApplicationPath, index.IsComplete, index.Status, index);
        }

        public string Fingerprint(Project project) => _fingerprints.GetOrAdd(project.Id, CreateFingerprint);

        private string CreateFingerprint(ProjectId projectId)
        {
            if (!projects.TryGetValue(projectId, out var source)) return "unavailable";
            var text = new StringBuilder();
            foreach (var id in source.Reachable.OrderBy(id => id.ToString(), StringComparer.Ordinal))
            {
                if (!projects.TryGetValue(id, out var owner)) { text.Append(id).Append(":missing;"); continue; }
                text.Append(owner.Assembly).Append('|').Append(owner.Complete).Append('|').Append(owner.ApplicationPath).Append('\n');
                foreach (var item in owner.Items)
                {
                    Texts.TryGetValue(item.Path, out var content);
                    text.Append(item.Path).Append('|').Append(item.ResourcePath).Append('|').Append(item.Kind).Append('|')
                        .Append(content?.Hash).Append('|').Append(content?.Version).Append('|').Append(content?.Read.State).Append('|')
                        .Append(content?.Read.Status).Append('\n');
                }
            }
            return TextHash(text.ToString());
        }

    }

    private static Dictionary<string, XamlDocumentOverlay> ResourceOverlays(IReadOnlyList<XamlDocumentOverlay>? supplied,
        string? sourcePath = null, string? sourceText = null, long sourceVersion = 0)
    {
        if (supplied is { Count: > 2048 }) throw new InvalidOperationException("The open XAML overlay snapshot exceeds the 2,048-file limit.");
        var overlays = new Dictionary<string, XamlDocumentOverlay>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var overlay in supplied ?? [])
        {
            var path = Path.GetFullPath(overlay.Path);
            characters += overlay.Text.Length;
            if (characters > 16_000_000) throw new InvalidOperationException("The open XAML overlay snapshot exceeds the text budget.");
            if (overlays.TryGetValue(path, out var old) && (old.Text != overlay.Text || old.Version != overlay.Version))
                throw new InvalidOperationException("Conflicting open XAML snapshots were supplied for one path.");
            overlays[path] = overlay;
        }
        if (sourcePath is not null && sourceText is not null)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            if (overlays.TryGetValue(sourcePath, out var old) && (old.Text != sourceText || old.Version != sourceVersion))
                throw new InvalidOperationException("The selected XAML text differs from its open-buffer snapshot.");
            overlays[sourcePath] = new(sourcePath, sourceText, sourceVersion);
        }
        return overlays;
    }

    private async Task<ResourceSnapshot> CaptureXamlResourcesAsync(Solution solution, IEnumerable<ProjectId> roots,
        IReadOnlyList<XamlDocumentOverlay>? supplied, CancellationToken token,
        string? sourcePath = null, string? sourceText = null, long sourceVersion = 0)
    {
        var overlays = ResourceOverlays(supplied, sourcePath, sourceText, sourceVersion);
        sourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath);
        var graph = solution.GetProjectDependencyGraph();
        var rootIds = roots.Distinct().ToArray();
        var wanted = new HashSet<ProjectId>();
        foreach (var root in rootIds)
        { wanted.Add(root); wanted.UnionWith(graph.GetProjectsThatThisProjectTransitivelyDependsOn(root)); }
        Dictionary<ProjectId, ResourceInventory> inventories;
        Dictionary<ProjectId, string[]> xaml;
        HashSet<ProjectId> unavailable;
        lock (_gate)
        {
            if (!ReferenceEquals(solution, _solution)) throw new InvalidOperationException("Project types changed while capturing XAML resources.");
            inventories = _xamlResourceInventory.Where(item => wanted.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value);
            xaml = _xamlInventory.Where(item => wanted.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value);
            unavailable = UnavailableModelProjects(solution);
        }
        var projects = new Dictionary<ProjectId, ResourceProject>();
        var compilations = new Dictionary<ProjectId, Compilation?>();
        var texts = new Dictionary<string, ResourceText>(StringComparer.OrdinalIgnoreCase);
        bool complete = true;
        string? captureStatus = null;
        int characters = 0;
        foreach (var project in solution.Projects.Where(project => wanted.Contains(project.Id)).OrderBy(project => project.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            inventories.TryGetValue(project.Id, out var inventory);
            bool known = inventory is not null && xaml.ContainsKey(project.Id) && !unavailable.Contains(project.Id) &&
                solution.Projects.Count(other => string.Equals(other.FilePath, project.FilePath, StringComparison.OrdinalIgnoreCase)) == 1;
            string? reason = known ? null : "An evaluated project resource or model context is unavailable or ambiguous.";
            var compilation = known ? await project.GetCompilationAsync(token).ConfigureAwait(false) : null;
            compilations[project.Id] = compilation;
            known &= compilation is not null;
            var items = inventory?.Items ?? [];
            if (items.Any(item => item.ResourcePath is null))
            { known = false; reason = "Some evaluated XAML resources have no proven output path."; }
            var applications = items.Where(item => item.Kind == "ApplicationDefinition").ToArray();
            string? application = inventory?.IsExecutable == true && applications.Length == 1 ? applications[0].Path : null;
            if (inventory?.IsExecutable == true && applications.Length > 1) known = false;
            var reachable = graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id).Append(project.Id).ToHashSet();
            complete &= known;
            projects.Add(project.Id, new(project, compilation?.Assembly.Identity.ToString() ?? project.AssemblyName ?? "",
                items, reachable, application, known, reason));
        }

        var indexes = new Dictionary<ProjectId, XamlResourceIndex>();
        foreach (var root in rootIds)
        {
            token.ThrowIfCancellationRequested();
            if (!projects.TryGetValue(root, out var source)) { complete = false; continue; }
            var identities = new List<XamlResourceDocument>();
            bool known = true;
            var notices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in source.Reachable)
            {
                if (!projects.TryGetValue(id, out var owner)) { known = false; continue; }
                known &= owner.Complete;
                if (owner.Status is not null) notices.Add(owner.Status);
                foreach (var item in owner.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.ResourcePath is null) { known = false; continue; }
                    identities.Add(new(item.Path, null, owner.Assembly, item.ResourcePath, item.Kind, item.Status));
                    // One excess identity lets the shared index report truncation
                    // without constructing an unbounded metadata snapshot here.
                    if (identities.Count > XamlResourceIndex.MaximumDocuments) break;
                }
                if (identities.Count > XamlResourceIndex.MaximumDocuments) break;
            }
            string? status = known ? null : string.Join(" ", notices.Append("The evaluated project resource inventory is incomplete."));
            var index = new XamlResourceIndex(identities, known, status, token);
            indexes.Add(root, index);
            complete &= index.IsComplete;
            captureStatus ??= index.Status;
        }

        if (sourcePath is not null && rootIds.Length == 1 && projects.TryGetValue(rootIds[0], out var sourceProject)
            && indexes.TryGetValue(rootIds[0], out var sourceIndex))
        {
            // Ordinary editor requests load a dependency closure, not every view.
            // Identity lookup remains complete even when most files are never read.
            await ReadAsync(sourcePath).ConfigureAwait(false);
            var pending = new Queue<(XamlResourceDocument Document, int Depth, bool Current)>();
            var scheduled = new HashSet<(string Path, string Assembly, string ResourcePath, string Kind)>();
            if (sourceIndex.FindDocument(sourcePath, sourceProject.Assembly).Document is { } origin)
                Enqueue(origin, 0, true);
            if (sourceProject.ApplicationPath is { } application &&
                sourceIndex.FindDocument(application, sourceProject.Assembly, "ApplicationDefinition").Document is { } app)
                Enqueue(app, 0, false);
            while (pending.TryDequeue(out var next))
            {
                token.ThrowIfCancellationRequested();
                var document = next.Document;
                if (next.Depth >= 64)
                { complete = false; captureStatus ??= "The resource dependency closure exceeds the file or import-depth budget."; break; }
                var content = await ReadAsync(document.Path).ConfigureAwait(false);
                if (content.Read.Text is not { } text || compilations[rootIds[0]] is not { } compilation) continue;
                var discovered = XamlResourceDependencies.Discover(text, document, sourceProject.Assembly, compilation, sourceIndex, token,
                    allowIncompleteDocument: next.Current);
                if (!discovered.IsComplete)
                {
                    complete = false;
                    captureStatus ??= discovered.Warnings.FirstOrDefault() ?? "Some resource imports could not be captured.";
                }
                foreach (var dependency in discovered.Documents) Enqueue(dependency, next.Depth + 1, false);
            }

            void Enqueue(XamlResourceDocument document, int depth, bool current)
            {
                var identity = (document.Path.ToUpperInvariant(), document.AssemblyName.ToUpperInvariant(), document.ResourcePath.ToUpperInvariant(), document.Kind);
                if (scheduled.Contains(identity)) return;
                if (scheduled.Count >= ResourceSnapshotFiles)
                { complete = false; captureStatus ??= "The resource dependency closure exceeds the file budget."; return; }
                scheduled.Add(identity);
                pending.Enqueue((document, depth, current));
            }
        }
        else
        {
            // Batch analysis and rename still capture the complete evaluated scan
            // within their existing budget. Rename refuses an incomplete capture.
            int files = 0;
            foreach (var project in projects.Values.OrderBy(project => project.Project.FilePath, StringComparer.OrdinalIgnoreCase))
            foreach (var path in xaml.GetValueOrDefault(project.Project.Id, []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (++files > ResourceSnapshotFiles) { complete = false; continue; }
                await ReadAsync(path).ConfigureAwait(false);
            }
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (!ReferenceEquals(solution, _solution)) throw new InvalidOperationException("Project types changed while reading XAML resources.");
        return new(new ReadOnlyDictionary<string, ResourceText>(texts), new ReadOnlyDictionary<ProjectId, ResourceProject>(projects),
            new ReadOnlyDictionary<ProjectId, XamlResourceIndex>(indexes), complete,
            complete ? null : captureStatus ?? "Some evaluated XAML resource inputs are unavailable or exceed the snapshot limits.");

        async Task<ResourceText> ReadAsync(string path)
        {
            token.ThrowIfCancellationRequested();
            if (texts.TryGetValue(path, out var cached)) return cached;
            if (texts.Count >= ResourceSnapshotFiles)
            {
                complete = false;
                captureStatus ??= "The resource dependency closure exceeds the file budget.";
                return new(new(null, "TooLarge", captureStatus), null, null);
            }
            overlays.TryGetValue(path, out var overlay);
            int remaining = Math.Min(1_000_000, Math.Max(0, ResourceSnapshotCharacters - characters));
            var read = remaining == 0 ? new DiskText(null, "TooLarge", "The project resource text budget is exhausted.")
                : overlay is null ? await ReadBoundedTextAsync(path, remaining, token).ConfigureAwait(false)
                : overlay.Text.Length <= remaining ? new DiskText(overlay.Text, "Read")
                : new DiskText(null, "TooLarge", "The open resource text exceeds the snapshot budget.");
            characters += read.Text?.Length ?? (read.State == "TooLarge" ? remaining : 0);
            var result = new ResourceText(read, overlay?.Version, read.Text is { } content ? TextHash(content) : null);
            texts.Add(path, result);
            complete &= read.Text is not null;
            captureStatus ??= read.Status;
            return result;
        }
    }
}
