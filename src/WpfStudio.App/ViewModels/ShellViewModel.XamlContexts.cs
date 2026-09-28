using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private void ConfigureXamlContext(EditorViewModel editor)
    {
        editor.SetXamlContextUnavailable(_projectReloadRequired ? "Project files changed. Reload the workspace to refresh XAML types."
            : _projectTypesPending ? "Checking changed project source files…" : null);
        editor.SetXamlProjects(Workspace?.Projects.Where(project => project.Files.Any(file => !file.IsGenerated
            && file.Path.Equals(editor.State.Path, StringComparison.OrdinalIgnoreCase))) ?? []);
    }

    private void RefreshXamlContexts()
    {
        foreach (var editor in Documents.Where(document => document.IsXaml)) ConfigureXamlContext(editor);
    }

    private static void SelectXamlDiagnosticContext(EditorViewModel editor, WorkspaceDiagnostic diagnostic)
    {
        if (!editor.IsXaml || diagnostic.ProjectPath is null) return;
        // A project path that identifies more than one evaluated context remains
        // ambiguous. Do not choose a framework based on list order.
        var choices = editor.XamlProjects.Where(project => project.ProjectPath.Equals(diagnostic.ProjectPath,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        editor.XamlProject = choices.Length == 1 ? choices[0] : null;
    }
}
