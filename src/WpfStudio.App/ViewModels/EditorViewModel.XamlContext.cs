using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public sealed partial class EditorViewModel
{
    private bool _updatingXamlProjects;
    private long _xamlProjectRevision;
    private string? _xamlContextUnavailable;
    public ObservableCollection<WorkspaceProject> XamlProjects { get; } = [];
    [ObservableProperty] public partial WorkspaceProject? XamlProject { get; set; }
    public string? XamlProjectPath => XamlProject is { } project && XamlProjects.Contains(project) ? project.ProjectPath : null;
    public long XamlContextRevision => Volatile.Read(ref _semanticRevision);
    internal long XamlProjectRevision => Volatile.Read(ref _xamlProjectRevision);
    public bool HasXamlProjects => IsXaml && XamlProjects.Count > 0;

    public void SetXamlProjects(IEnumerable<WorkspaceProject> projects)
    {
        if (!IsXaml || _disposed) return;
        var choices = projects.ToArray();
        var selected = XamlProject;
        _updatingXamlProjects = true;
        try
        {
            XamlProjects.Clear();
            foreach (var project in choices) XamlProjects.Add(project);
            var previous = choices.Where(project => selected is not null
                && project.ProjectPath.Equals(selected.ProjectPath, StringComparison.OrdinalIgnoreCase)
                && project.TargetFramework == selected.TargetFramework).ToArray();
            XamlProject = (previous.Length == 1 ? previous[0] : null)
                ?? (choices.Length == 1 ? choices[0] : null);
        }
        finally { _updatingXamlProjects = false; }
        OnPropertyChanged(nameof(HasXamlProjects));
        InvalidateXamlProject();
    }

    partial void OnXamlProjectChanged(WorkspaceProject? value)
    {
        if (!_updatingXamlProjects) InvalidateXamlProject();
    }

    private void InvalidateXamlProject()
    {
        Interlocked.Increment(ref _xamlProjectRevision);
        Interlocked.Increment(ref _semanticRevision);
        OnPropertyChanged(nameof(XamlProjectPath));
        OnPropertyChanged(nameof(XamlContextRevision));
        if (!_disposed) _ = StartAnalysisAsync(debounce: true);
    }

    public void SetXamlContextUnavailable(string? reason)
    {
        if (_xamlContextUnavailable == reason) return;
        _xamlContextUnavailable = reason;
        InvalidateXamlProject();
    }

    public async Task<IReadOnlyList<SourceLocation>> XamlDefinitionAsync(int offset, CancellationToken token = default)
    {
        if (!IsXaml || _disposed || !_workspace.IsConnected || _xamlContextUnavailable is not null) return [];
        long version = State.Version, revision = XamlContextRevision;
        var resources = CaptureResources();
        var result = await _workspace.GetXamlDefinitionAsync(new(State.Path, State.Content,
            Math.Clamp(offset, 0, State.Content.Length), version, XamlProjectPath, resources.Overlays), token);
        return IsCurrentAnalysis(version, revision, resources.Generation, token) && _workspace.IsConnected ? result : [];
    }

    internal TextEdit XamlCompletionEdit(CompletionEntry entry, int start, int length, long contextRevision)
    {
        if (_disposed || !IsXaml || contextRevision != XamlContextRevision)
            throw new InvalidOperationException("The XAML project context or its types changed. Request completion again; typed input was kept.");
        return new(start, length, entry.InsertText);
    }
}
