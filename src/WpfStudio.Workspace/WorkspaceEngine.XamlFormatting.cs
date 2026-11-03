using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    public async Task<WorkspaceEditResult> FormatXamlAsync(XamlFormattingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Project? project;
        Solution? solution;
        long revision;
        lock (_gate)
        {
            if (_lifetime.IsInventoryMissing)
                return new([], ["Workspace content metadata is loading or unavailable. Complete or reload the workspace before formatting."]);
            solution = CurrentSolution;
            revision = SemanticRevision;
            var context = GetXamlProject(request.Path, request.ProjectPath);
            project = context.Project;
            if (project is null && (request.ProjectPath is not null || _ownership.Unavailable.Count != 0
                || _ownership.Owners.TryGetValue(Path.GetFullPath(request.Path), out var owners) && owners.Count != 0))
                return new([], [context.Status ?? "The owning project's content metadata is unavailable. Reload the workspace before formatting."]);
        }
        var compilation = project is null ? null : await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (project is not null && compilation is null)
            return new([], ["The owning project's content metadata is unavailable. Reload the workspace before formatting."]);
        var formatted = new XamlFormattingService().Format(request.Text, compilation, request.Options, cancellationToken);
        if (!formatted.Accepted) return new([], formatted.Warnings);
        lock (_gate)
        {
            if (_lifetime.IsLoading || !ReferenceEquals(solution, CurrentSolution) || revision != SemanticRevision
                || project is not null && !IsCurrentXamlProject(project))
                return new([], ["Project types changed during XAML formatting. Format the current document again."]);
        }
        return new([new(Path.GetFullPath(request.Path), request.Version, formatted.Edits, TextHash(request.Text))], formatted.Warnings);
    }
}
