using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    // Authored buffers and actual MSBuild documents are the inputs. Editor-derived
    // page partials exist only in _solution, never in the workspace or on disk.
    private Solution? _authoredSolution;
    private readonly SemaphoreSlim _pageProjectionGate = new(1, 1);
    private readonly Dictionary<string, string> _pageProjectionTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pageProjectionReadStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameProjectionStatuses = new(StringComparer.OrdinalIgnoreCase);
    private XamlPageSemanticProjectionResult? _pageProjection;
    private long _pageProjectionEpoch;
    private long _pageProjectionLoadEpoch;
    private bool _pageProjectionPending;

    private void SetAuthoredSolution(Solution solution)
    {
        _authoredSolution = solution;
        _solution = solution;
        _pageProjectionPending = true;
        _pageProjectionEpoch++;
    }

    private void ResetPageProjection()
    {
        _authoredSolution = null;
        _pageProjection = null;
        _pageProjectionTexts.Clear();
        _pageProjectionReadStatuses.Clear();
        _nameProjectionStatuses.Clear();
        _pageProjectionPending = false;
        _pageProjectionEpoch++;
        _pageProjectionLoadEpoch++;
    }

    private (ProjectId Project, string Path)[] PageProjectionInventory() => _xamlResourceInventory
        .SelectMany(pair => pair.Value.Items.Where(item => item.Kind == "Page")
            .Select(item => (pair.Key, Path.GetFullPath(item.Path))))
        .Distinct().ToArray();

    private async Task<bool> RefreshPageProjectionSourcesAsync(IReadOnlyList<string>? paths, CancellationToken token)
    {
        (string Path, string? OldText, string? OldStatus)[] selected;
        Solution? original;
        long epoch;
        lock (_gate)
        {
            original = _authoredSolution;
            if (original is null) return false;
            epoch = _pageProjectionEpoch;
            var requested = paths?.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = PageProjectionInventory().Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !_syncedTexts.ContainsKey(path) && (requested is null || requested.Contains(path)))
                .Select(path => (path, _pageProjectionTexts.GetValueOrDefault(path), _pageProjectionReadStatuses.GetValueOrDefault(path))).ToArray();
        }
        var reads = new Dictionary<string, DiskText>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var item in selected)
        {
            token.ThrowIfCancellationRequested();
            var read = characters >= 16_000_000 ? new DiskText(null, "TooLarge", "Current XAML fields exceed the workspace source budget.")
                : await ReadBoundedTextAsync(item.Path, 1_000_000, token).ConfigureAwait(false);
            characters += read.Text?.Length ?? 0;
            reads.Add(item.Path, read);
        }
        lock (_gate)
        {
            if (!ReferenceEquals(original, _authoredSolution) || epoch != _pageProjectionEpoch) return false;
            bool changed = false;
            foreach (var item in selected)
            {
                if (_syncedTexts.ContainsKey(item.Path)) continue;
                var read = reads[item.Path];
                string text = read.Text ?? "";
                string? status = read.Text is null ? read.Status ?? "The page source is unavailable for current XAML fields." : null;
                changed |= item.OldText != text || item.OldStatus != status;
                _pageProjectionTexts[item.Path] = text;
                if (status is null) _pageProjectionReadStatuses.Remove(item.Path);
                else _pageProjectionReadStatuses[item.Path] = status;
            }
            if (changed)
            {
                _pageProjectionPending = true;
                _pageProjectionEpoch++;
                _semanticRevision++;
                _completions.Clear();
                _xamlBatchCache.Clear();
            }
            return changed;
        }
    }

    private async Task EnsurePageProjectionAsync(CancellationToken token)
    {
        lock (_gate) if (!_pageProjectionPending || _authoredSolution is null) return;
        await _pageProjectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Solution authored;
                long epoch;
                XamlPageProjectionInput[] inputs;
                lock (_gate)
                {
                    if (!_pageProjectionPending || _authoredSolution is null) return;
                    authored = _authoredSolution;
                    epoch = _pageProjectionEpoch;
                    inputs = PageProjectionInventory().Select(item => new XamlPageProjectionInput(item.Project, item.Path,
                        _syncedTexts.GetValueOrDefault(item.Path) ?? _pageProjectionTexts.GetValueOrDefault(item.Path) ?? "")).ToArray();
                }
                var result = await XamlPageSemanticProjection.BuildAsync(authored, inputs, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!ReferenceEquals(authored, _authoredSolution) || epoch != _pageProjectionEpoch) continue;
                    _pageProjection = result;
                    _solution = result.Solution;
                    _pageProjectionPending = false;
                    _nameProjectionStatuses.Clear();
                    foreach (var page in result.Pages)
                    {
                        string? status = JoinResourceStatus(page.Status, _pageProjectionReadStatuses.GetValueOrDefault(page.Input.Path));
                        if (status is not null) _nameProjectionStatuses[page.Input.Path] = status;
                    }
                    _semanticRevision++;
                    _completions.Clear();
                    _xamlBatchCache.Clear();
                    return;
                }
            }
        }
        finally { _pageProjectionGate.Release(); }
    }

    private async Task<DocumentUpdateResult> UpdateXamlProjectionDocumentAsync(UpdateDocumentRequest request, CancellationToken token)
    {
        string path = Path.GetFullPath(request.Path);
        lock (_gate)
        {
            if (!_xamlProjects.ContainsKey(path)) return new(false, request.Version, []);
            if (_versions.TryGetValue(path, out long version))
            {
                if (version > request.Version) return new(false, version, []);
                if (version == request.Version && _syncedTexts.GetValueOrDefault(path) != request.Text)
                    throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
            }
            // Opening an unchanged page registers a buffer version, but the
            // loaded disk text already established these compiler fields.
            // Rebuilding here needlessly invalidates concurrent hover/F12.
            string? previousText = _syncedTexts.GetValueOrDefault(path) ?? _pageProjectionTexts.GetValueOrDefault(path);
            bool clearedReadStatus = _pageProjectionReadStatuses.Remove(path);
            if (previousText != request.Text || clearedReadStatus)
            {
                _pageProjectionPending = true;
                _pageProjectionEpoch++;
                _semanticRevision++;
                _completions.Clear();
                _xamlBatchCache.Clear();
            }
            _versions[path] = request.Version;
            _syncedTexts[path] = request.Text;
        }
        await EnsurePageProjectionAsync(token).ConfigureAwait(false);
        lock (_gate)
            return new(_versions.GetValueOrDefault(path) == request.Version && _syncedTexts.GetValueOrDefault(path) == request.Text,
                _versions.GetValueOrDefault(path), []);
    }

    private async Task CloseXamlProjectionDocumentAsync(string path, CancellationToken token)
    {
        long epoch;
        long? version;
        string? text;
        lock (_gate)
        {
            epoch = _pageProjectionLoadEpoch;
            version = _versions.TryGetValue(path, out long current) ? current : null;
            text = _syncedTexts.GetValueOrDefault(path);
        }
        var read = await ReadBoundedTextAsync(path, 1_000_000, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (epoch != _pageProjectionLoadEpoch || (_versions.TryGetValue(path, out long current) ? (long?)current : null) != version
                || _syncedTexts.GetValueOrDefault(path) != text) return;
            _versions.Remove(path);
            _syncedTexts.Remove(path);
            _pageProjectionTexts[path] = read.Text ?? "";
            if (read.Text is null) _pageProjectionReadStatuses[path] = read.Status ?? "Saved XAML is unavailable.";
            else _pageProjectionReadStatuses.Remove(path);
            _pageProjectionPending = true;
            _pageProjectionEpoch++;
            _semanticRevision++;
        }
        await EnsurePageProjectionAsync(token).ConfigureAwait(false);
    }
}
