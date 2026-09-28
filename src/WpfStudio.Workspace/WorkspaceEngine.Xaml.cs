using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private readonly Dictionary<string, HashSet<ProjectId>> _xamlProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly XamlLanguageService _xamlLanguage = new();
    private readonly XamlSchemaService _xamlSchema = new();
    private readonly XamlEventService _xamlEvents = new();
    private readonly XamlNameService _xamlNames = new();

    public async Task<XamlAnalysisResult> AnalyzeXamlAsync(XamlDocumentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (project, status) = GetXamlProject(request.Path, request.ProjectPath);
        if (project is null) return new(request.Version, false, [], status);
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null) return new(request.Version, false, [], "The project's type information is unavailable.");
        ResourceSnapshot resources;
        try { resources = await CaptureXamlResourcesAsync(project.Solution, [project.Id], request.XamlOverlays, cancellationToken,
            request.Path, request.Text, request.Version).ConfigureAwait(false); }
        catch (InvalidOperationException exception) { return new(request.Version, false, [], exception.Message); }
        var events = _xamlEvents.AnalyzeDetailed(request.Path, request.Text, request.Version, compilation, token: cancellationToken);
        var names = _xamlNames.AnalyzeDetailed(request.Path, request.Text, request.Version, compilation, token: cancellationToken);
        var diagnostics = _xamlSchema.Analyze(request.Path, request.Text, request.Version, compilation, cancellationToken)
            .Concat(_xamlLanguage.Analyze(request.Path, request.Text, request.Version, compilation, cancellationToken, resources.Context(project, request.Path)))
            .Concat(events.Diagnostics).Concat(names.Diagnostics).Distinct().ToArray();
        lock (_gate)
            return IsCurrentXamlProject(project)
                ? new(request.Version, true, diagnostics, JoinResourceStatus(JoinResourceStatus(JoinResourceStatus(events.Status, names.Status), resources.Status),
                    _nameProjectionStatuses.GetValueOrDefault(Path.GetFullPath(request.Path))))
                : new(request.Version, false, [], "Project types changed; refreshing XAML analysis.");
    }

    public async Task<XamlCompletionResult> GetXamlCompletionsAsync(XamlCompletionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePosition(request.Position, request.Text.Length);
        var (project, status) = GetXamlProject(request.Path, request.ProjectPath);
        if (project is null) return new(false, null, status);
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null) return new(false, null, "The project's type information is unavailable.");
        ResourceSnapshot resources;
        try { resources = await CaptureXamlResourcesAsync(project.Solution, [project.Id], request.XamlOverlays, cancellationToken,
            request.Path, request.Text, request.Version).ConfigureAwait(false); }
        catch (InvalidOperationException exception) { return new(false, null, exception.Message); }
        var result = _xamlNames.Complete(request.Text, request.Position, request.Version, compilation, cancellationToken)
            ?? _xamlLanguage.Complete(request.Text, request.Position, request.Version, compilation, cancellationToken, resources.Context(project, request.Path))
            ?? _xamlEvents.Complete(request.Text, request.Position, request.Version, compilation, cancellationToken)
            ?? _xamlSchema.Complete(request.Text, request.Position, request.Version, compilation, cancellationToken);
        lock (_gate)
            return IsCurrentXamlProject(project)
                ? new(true, result, resources.Status)
                : new(false, null, "Project types changed; request completion again.");
    }

    public async Task<IReadOnlyList<SourceLocation>> GetXamlDefinitionAsync(XamlCompletionRequest request, CancellationToken cancellationToken)
    {
        ValidatePosition(request.Position, request.Text.Length);
        var (project, _) = GetXamlProject(request.Path, request.ProjectPath);
        if (project is null || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation) return [];
        ResourceSnapshot resources;
        try { resources = await CaptureXamlResourcesAsync(project.Solution, [project.Id], request.XamlOverlays, cancellationToken,
            request.Path, request.Text, request.Version).ConfigureAwait(false); }
        catch (InvalidOperationException) { return []; }
        var nameLocations = _xamlNames.GetDefinition(request.Path, request.Text, request.Position, compilation, cancellationToken);
        var locations = nameLocations.Count > 0 ? nameLocations :
            _xamlLanguage.GetDefinition(request.Path, request.Text, request.Position, compilation, cancellationToken, resources.Context(project, request.Path))
                .Concat(_xamlEvents.GetDefinition(request.Path, request.Text, request.Position, compilation, cancellationToken))
                .Concat(_xamlSchema.GetDefinition(request.Path, request.Text, request.Position, compilation, cancellationToken)).Distinct().ToArray();
        var resolved = new List<SourceLocation>();
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(location.Path) && compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == location.Path) is { } tree)
                resolved.Add(location with { Path = await CacheGeneratedAsync(location.Path, await tree.GetTextAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) });
            else if (File.Exists(location.Path)) resolved.Add(location);
        }
        lock (_gate) return IsCurrentXamlProject(project) ? resolved : [];
    }

    public async Task<XamlHoverInfo?> GetXamlHoverAsync(XamlCompletionRequest request, CancellationToken cancellationToken)
    {
        ValidatePosition(request.Position, request.Text.Length);
        var (project, _) = GetXamlProject(request.Path, request.ProjectPath);
        if (project is null || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation) return null;
        ResourceSnapshot resources;
        try { resources = await CaptureXamlResourcesAsync(project.Solution, [project.Id], request.XamlOverlays, cancellationToken,
            request.Path, request.Text, request.Version).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
        var hover = _xamlNames.GetHover(request.Path, request.Text, request.Position, compilation, cancellationToken)
            ?? _xamlLanguage.GetHover(request.Path, request.Text, request.Position, compilation, cancellationToken, resources.Context(project, request.Path))
            ?? _xamlEvents.GetHover(request.Path, request.Text, request.Position, compilation, cancellationToken)
            ?? _xamlSchema.GetHover(request.Path, request.Text, request.Position, compilation, cancellationToken);
        lock (_gate) return IsCurrentXamlProject(project) ? hover : null;
    }

    public async Task<IReadOnlyList<XamlCodeAction>> GetXamlCodeActionsAsync(XamlCompletionRequest request, CancellationToken cancellationToken)
    {
        ValidatePosition(request.Position, request.Text.Length);
        var (project, _) = GetXamlProject(request.Path, request.ProjectPath);
        if (project is null || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation) return [];
        ResourceSnapshot resources;
        try { resources = await CaptureXamlResourcesAsync(project.Solution, [project.Id], request.XamlOverlays, cancellationToken,
            request.Path, request.Text, request.Version).ConfigureAwait(false); }
        catch (InvalidOperationException) { return []; }
        // The semantic resolver attaches guards for dictionaries consulted by
        // this binding only; unrelated unavailable markup cannot disable a fix.
        var actions = _xamlLanguage.GetCodeActions(request.Path, request.Text, request.Position, request.Version, compilation, cancellationToken,
            resources.Context(project, request.Path)).ToList();
        actions.AddRange(_xamlNames.GetCodeActions(request.Path, request.Text, request.Position, request.Version, compilation, cancellationToken));
        var eventTarget = _xamlEvents.GetTarget(request.Text, request.Position, compilation, cancellationToken);
        if (eventTarget is not null && await CreateXamlEventHandlerActionAsync(project, request, compilation, eventTarget, cancellationToken).ConfigureAwait(false) is { } action)
            actions.Add(action);
        lock (_gate) return IsCurrentXamlProject(project) ? actions : [];
    }

    private (Project? Project, string? Status) GetXamlProject(string path, string? projectPath)
    {
        path = Path.GetFullPath(path);
        projectPath = projectPath is null ? null : Path.GetFullPath(projectPath);
        lock (_gate)
        {
            if (_solution is null) return (null, "XAML type analysis is unavailable. Check workspace loading diagnostics.");
            if (!_xamlProjects.TryGetValue(path, out var owners))
                return (null, "Open or reload the owning project to enable XAML binding analysis.");
            var projects = owners.Select(_solution.GetProject).OfType<Project>()
                .Where(p => projectPath is null || string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (projects.Length == 1 && UnavailableModelProjects(_solution).Contains(projects[0].Id))
                return (null, "Model source refresh is incomplete for this project or a referenced project; XAML type analysis is temporarily unavailable.");
            return projects.Length switch
            {
                1 => (projects[0], null),
                0 => (null, "The owning project's type information is unavailable."),
                _ => (null, "This XAML file has multiple project contexts; binding analysis requires an unambiguous owning project.")
            };
        }
    }

    private bool IsCurrentXamlProject(Project project) => ReferenceEquals(project.Solution, _solution)
        && !UnavailableModelProjects(project.Solution).Contains(project.Id);

    private static string? JoinResourceStatus(string? first, string? second) => first is null ? second : second is null ? first : first + " " + second;
}
