using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    // Long enough that a pause between words does not start compiling a project the next keystroke abandons.
    private static readonly TimeSpan ProjectDiagnosticsDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>A whole-project compilation contradicted an analysis reply. Nothing semantic changed: only diagnostics need requesting again.</summary>
    public event EventHandler? ProjectDiagnosticsChanged;

    /// <summary>
    /// One whole compilation's diagnostics. A semantic model binds a single file, so it never reports unused fields (CS0169,
    /// CS0414, CS0649), which need every method body bound, entry point problems (CS5001 and others) or reference and option
    /// errors, which have no file. An edit costs one file; this runs once the edit settles.
    /// </summary>
    private sealed class ProjectDiagnosticsPass(Compilation compilation)
    {
        public Compilation Compilation => compilation;
        public CancellationTokenSource Cancellation { get; } = new();
        /// <summary>Null until the compilation has finished.</summary>
        public ImmutableArray<Diagnostic>? Diagnostics { get; set; }
    }

    /// <summary>The last reply for a file, and the part of it that only a whole compilation can report.</summary>
    private sealed record ReportedDiagnostics(ProjectId Project, SourceText Text, WorkspaceDiagnostic[] All, WorkspaceDiagnostic[] CompileOnly);

    // Reads the pass and records the reply together, so a pass finishing at any moment either adds to this reply or sees what it lacked.
    private IReadOnlyList<WorkspaceDiagnostic> CompleteDiagnostics(Document document, Compilation compilation, SourceText text, ImmutableArray<Diagnostic> fileDiagnostics)
    {
        lock (_gate)
        {
            // UpdateDocumentAsync discards a reply for a superseded solution, and it must not displace the current pass.
            if (!ReferenceEquals(document.Project.Solution, CurrentSolution)) return [];
            var project = document.Project.Id;
            _projectDiagnostics.Passes.TryGetValue(project, out var pass);
            if (pass is null || !ReferenceEquals(pass.Compilation, compilation) || pass.Cancellation.IsCancellationRequested)
            {
                pass?.Cancellation.Cancel();
                _projectDiagnostics.Passes[project] = pass = new(compilation);
                RunProjectDiagnosticsAsync(project, pass).Forget(exception => Console.Error.WriteLine("Project diagnostics: " + exception.Message));
            }
            var path = document.FilePath!;
            var file = fileDiagnostics.Where(diagnostic => AppliesTo(diagnostic, path)).Select(ToDiagnostic).Distinct().ToArray();
            IEnumerable<WorkspaceDiagnostic> whole = pass.Diagnostics is { } known ? known.Where(diagnostic => AppliesTo(diagnostic, path)).Select(ToDiagnostic)
                : _projectDiagnostics.Reported.TryGetValue(path, out var previous) ? ThroughEdit(previous.CompileOnly, path, previous.Text, text) : [];
            var compileOnly = whole.Except(file).ToArray();
            WorkspaceDiagnostic[] all = [.. file, .. compileOnly];
            _projectDiagnostics.Reported[path] = new(project, text, all, compileOnly);
            return all;
        }
    }

    // Until the pass reports, the last reply's compile-only diagnostics stay, so they do not vanish and return at every pause. The
    // edit is the text between the common prefix and suffix; one it touches is dropped, because the pass may word or place it differently.
    private static IEnumerable<WorkspaceDiagnostic> ThroughEdit(WorkspaceDiagnostic[] diagnostics, string path, SourceText before, SourceText after)
    {
        int prefix = 0, suffix = 0, shortest = Math.Min(before.Length, after.Length);
        while (prefix < shortest && before[prefix] == after[prefix]) prefix++;
        while (suffix < shortest - prefix && before[before.Length - suffix - 1] == after[after.Length - suffix - 1]) suffix++;
        foreach (var diagnostic in diagnostics)
        {
            var inFile = string.Equals(diagnostic.Path, path, StringComparison.OrdinalIgnoreCase);
            if (diagnostic.Path is null || (inFile && diagnostic.Start + diagnostic.Length < prefix)) yield return diagnostic;
            else if (inFile && diagnostic.Start > before.Length - suffix)
            {
                var start = diagnostic.Start + after.Length - before.Length;
                var position = after.Lines.GetLinePosition(start);
                yield return diagnostic with { Start = start, Line = position.Line + 1, Column = position.Character + 1 };
            }
        }
    }

    private async Task RunProjectDiagnosticsAsync(ProjectId project, ProjectDiagnosticsPass pass)
    {
        var token = pass.Cancellation.Token;
        await Task.Delay(ProjectDiagnosticsDelay, token).ConfigureAwait(false);
        var diagnostics = await Task.Run(() => pass.Compilation.GetDiagnostics(token), token).ConfigureAwait(false);
        bool contradicted;
        lock (_gate)
        {
            if (!ReferenceEquals(_projectDiagnostics.Passes.GetValueOrDefault(project), pass) || token.IsCancellationRequested) return;
            var visible = diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden).ToImmutableArray();
            pass.Diagnostics = visible;
            contradicted = _projectDiagnostics.Reported.Any(reported => reported.Value.Project == project && Contradicts(visible, reported.Key, reported.Value));
        }
        if (contradicted) ProjectDiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Contradicts(ImmutableArray<Diagnostic> known, string path, ReportedDiagnostics reported)
    {
        var expected = known.Where(diagnostic => AppliesTo(diagnostic, path)).Select(ToDiagnostic).ToHashSet();
        return !expected.IsSubsetOf(reported.All) || reported.CompileOnly.Any(diagnostic => !expected.Contains(diagnostic));
    }

    private static bool AppliesTo(Diagnostic diagnostic, string path) => diagnostic.Severity != DiagnosticSeverity.Hidden
        && (!diagnostic.Location.IsInSource || string.Equals(diagnostic.Location.SourceTree?.FilePath, path, StringComparison.OrdinalIgnoreCase));
}
