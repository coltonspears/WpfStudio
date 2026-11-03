using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private void SetAuthoredSolution(Solution solution)
    {
        _pages.Authored = solution;
        CurrentSolution = solution;
        _pages.Invalidate();
        _projectDiagnostics.CancelRunning();
    }

    private (ProjectId Project, string Path)[] PageProjectionInventory() => _ownership.Resources
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
            original = _pages.Authored;
            if (original is null) return false;
            epoch = _pages.Epoch;
            var requested = paths?.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = PageProjectionInventory().Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !_documents.IsOpen(path) && (requested is null || requested.Contains(path)))
                .Select(path => (path, _pages.Texts.GetValueOrDefault(path), _pages.ReadStatuses.GetValueOrDefault(path))).ToArray();
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
            if (!ReferenceEquals(original, _pages.Authored) || epoch != _pages.Epoch) return false;
            bool changed = false;
            foreach (var item in selected)
            {
                if (_documents.IsOpen(item.Path)) continue;
                var read = reads[item.Path];
                string text = read.Text ?? "";
                string? status = read.Text is null ? read.Status ?? "The page source is unavailable for current XAML fields." : null;
                changed |= item.OldText != text || item.OldStatus != status;
                _pages.Texts[item.Path] = text;
                if (status is null) _pages.ReadStatuses.Remove(item.Path);
                else _pages.ReadStatuses[item.Path] = status;
            }
            if (changed)
            {
                _pages.Invalidate();
                SemanticsChanged();
            }
            return changed;
        }
    }

    private async Task EnsurePageProjectionAsync(CancellationToken token)
    {
        lock (_gate) if (!_pages.Pending || _pages.Authored is null) return;
        await _pages.BuildGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Solution authored;
                long epoch;
                XamlPageProjectionInput[] inputs;
                lock (_gate)
                {
                    if (!_pages.Pending || _pages.Authored is null) return;
                    authored = _pages.Authored;
                    epoch = _pages.Epoch;
                    inputs = PageProjectionInventory().Select(item => new XamlPageProjectionInput(item.Project, item.Path,
                        _documents.Text(item.Path) ?? _pages.Texts.GetValueOrDefault(item.Path) ?? "")).ToArray();
                }
                var result = await XamlPageSemanticProjection.BuildAsync(authored, inputs, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!ReferenceEquals(authored, _pages.Authored) || epoch != _pages.Epoch) continue;
                    _pages.Published(result);
                    CurrentSolution = result.Solution;
                    foreach (var page in result.Pages)
                    {
                        string? status = JoinResourceStatus(page.Status, _pages.ReadStatuses.GetValueOrDefault(page.Input.Path));
                        if (status is not null) _pages.NameStatuses[page.Input.Path] = status;
                    }
                    SemanticsChanged();
                    return;
                }
            }
        }
        finally { _pages.BuildGate.Release(); }
    }

    private async Task<DocumentUpdateResult> UpdateXamlProjectionDocumentAsync(UpdateDocumentRequest request, CancellationToken token)
    {
        string path = Path.GetFullPath(request.Path);
        lock (_gate)
        {
            if (!_ownership.Owners.ContainsKey(path)) return new(false, request.Version, []);
            if (_documents.OpenVersion(path) is { } version)
            {
                if (version > request.Version) return new(false, version, []);
                if (version == request.Version && _documents.Text(path) != request.Text)
                    throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
            }
            // Opening an unchanged page registers a buffer version, but the
            // loaded disk text already established these compiler fields.
            // Rebuilding here needlessly invalidates concurrent hover/F12.
            string? previousText = _documents.Text(path) ?? _pages.Texts.GetValueOrDefault(path);
            bool clearedReadStatus = _pages.ReadStatuses.Remove(path);
            if (previousText != request.Text || clearedReadStatus)
            {
                _pages.Invalidate();
                SemanticsChanged();
            }
            _documents.Set(path, request.Version, request.Text);
        }
        await EnsurePageProjectionAsync(token).ConfigureAwait(false);
        lock (_gate)
            return new(_documents.Version(path) == request.Version && _documents.Text(path) == request.Text,
                _documents.Version(path), []);
    }

    private async Task CloseXamlProjectionDocumentAsync(string path, CancellationToken token)
    {
        long epoch;
        long? version;
        string? text;
        lock (_gate)
        {
            epoch = _pages.LoadEpoch;
            version = _documents.OpenVersion(path);
            text = _documents.Text(path);
        }
        var read = await ReadBoundedTextAsync(path, 1_000_000, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (epoch != _pages.LoadEpoch || _documents.OpenVersion(path) != version || _documents.Text(path) != text) return;
            _documents.Remove(path);
            _pages.Texts[path] = read.Text ?? "";
            if (read.Text is null) _pages.ReadStatuses[path] = read.Status ?? "Saved XAML is unavailable.";
            else _pages.ReadStatuses.Remove(path);
            _pages.Invalidate();
            SemanticRevision++;
        }
        await EnsurePageProjectionAsync(token).ConfigureAwait(false);
    }
}
