using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    // Long enough that a pause between words does not start compiling a project the next keystroke abandons.
    private static readonly TimeSpan ProjectDiagnosticsDelay = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<ProjectId, ProjectDiagnosticsPass> _projectDiagnostics = [];
    private readonly Dictionary<string, (ProjectId Project, WorkspaceDiagnostic[] Diagnostics)> _reportedDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One whole compilation's diagnostics. A semantic model binds a single file, so it never reports unused fields (CS0169,
    /// CS0414, CS0649), which need every method body bound, entry point problems (CS5001 and others) or reference and option
    /// errors, which have no file. An edit costs one file; this runs once the edit settles, and a notification asks editors
    /// to analyse again and receive the complete set.
    /// </summary>
    private sealed class ProjectDiagnosticsPass(Compilation compilation, ImmutableArray<Diagnostic> known)
    {
        public Compilation Compilation => compilation;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Completed { get; private set; }
        /// <summary>Everything once completed. Until then only what has no source position, which an edit cannot have moved.</summary>
        public ImmutableArray<Diagnostic> Known { get; private set; } = known;
        public ImmutableArray<Diagnostic> ProjectLevel => Known.Where(diagnostic => !diagnostic.Location.IsInSource).ToImmutableArray();
        public void Complete(ImmutableArray<Diagnostic> diagnostics) { Known = diagnostics; Completed = true; }
    }

    // Reads the pass and records the reply together, so a pass finishing at any moment either adds to this reply or sees what it lacked.
    private IReadOnlyList<WorkspaceDiagnostic> CompleteDiagnostics(Document document, Compilation compilation, ImmutableArray<Diagnostic> fileDiagnostics)
    {
        lock (_gate)
        {
            // UpdateDocumentAsync discards a reply for a superseded solution, and it must not displace the current pass.
            if (!ReferenceEquals(document.Project.Solution, _solution)) return [];
            var project = document.Project.Id;
            _projectDiagnostics.TryGetValue(project, out var pass);
            if (pass is null || !ReferenceEquals(pass.Compilation, compilation) || pass.Cancellation.IsCancellationRequested)
            {
                pass?.Cancellation.Cancel();
                _projectDiagnostics[project] = pass = new(compilation, pass?.ProjectLevel ?? []);
                RunProjectDiagnosticsAsync(project, pass).Forget(exception => Console.Error.WriteLine("Project diagnostics: " + exception.Message));
            }
            var path = document.FilePath!;
            var diagnostics = fileDiagnostics.AddRange(pass.Known).Where(diagnostic => AppliesTo(diagnostic, path)).Select(ToDiagnostic).Distinct().ToArray();
            _reportedDiagnostics[path] = (project, diagnostics);
            return diagnostics;
        }
    }

    private async Task RunProjectDiagnosticsAsync(ProjectId project, ProjectDiagnosticsPass pass)
    {
        var token = pass.Cancellation.Token;
        await Task.Delay(ProjectDiagnosticsDelay, token).ConfigureAwait(false);
        var diagnostics = await Task.Run(() => pass.Compilation.GetDiagnostics(token), token).ConfigureAwait(false);
        bool outdated;
        lock (_gate)
        {
            if (!ReferenceEquals(_projectDiagnostics.GetValueOrDefault(project), pass) || token.IsCancellationRequested) return;
            pass.Complete(diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden).ToImmutableArray());
            outdated = _reportedDiagnostics.Any(reported => reported.Value.Project == project && IsOutdated(reported.Key, reported.Value.Diagnostics, pass.Known));
        }
        if (outdated) SemanticStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsOutdated(string path, WorkspaceDiagnostic[] reported, ImmutableArray<Diagnostic> known)
    {
        var expected = known.Where(diagnostic => AppliesTo(diagnostic, path)).Select(ToDiagnostic).ToHashSet();
        return !expected.IsSubsetOf(reported) || reported.Any(diagnostic => diagnostic.Path is null && !expected.Contains(diagnostic));
    }

    private static bool AppliesTo(Diagnostic diagnostic, string path) => diagnostic.Severity != DiagnosticSeverity.Hidden
        && (!diagnostic.Location.IsInSource || string.Equals(diagnostic.Location.SourceTree?.FilePath, path, StringComparison.OrdinalIgnoreCase));

    // Called with _gate held. A pass that finished stays valid for its compilation; one still running would only be discarded.
    private void CancelProjectDiagnostics()
    {
        foreach (var pass in _projectDiagnostics.Values)
            if (!pass.Completed) pass.Cancellation.Cancel();
    }

    // Called with _gate held.
    private void ResetProjectDiagnostics()
    {
        CancelProjectDiagnostics();
        _projectDiagnostics.Clear();
        _reportedDiagnostics.Clear();
    }
}
