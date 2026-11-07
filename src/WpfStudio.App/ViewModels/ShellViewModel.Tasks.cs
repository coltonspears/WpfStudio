using System.IO;
using WpfStudio.App.Features.Tasks;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Tasks;

namespace WpfStudio.App.ViewModels;

/// <summary>The shell as the Tasks pane's host, and the task commands of the menus and the command palette.</summary>
public sealed partial class ShellViewModel : ITaskHost
{
    public TasksViewModel Tasks { get; }

    string ITaskHost.DataDirectory => _paths.DataDirectory;
    string ITaskHost.FallbackDirectory => Directory.Exists(Terminal.WorkingDirectory) ? Terminal.WorkingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    void ITaskHost.ShowTerminal() => ToolRequested?.Invoke(Workbenches.Terminal);
    void ITaskHost.TaskDiagnosticsChanged() => RefreshEditorDiagnostics();

    TaskVariableContext ITaskHost.VariableContext()
    {
        // A tool pane takes the focus from the editor, so the editor last used still counts as the active file.
        var editor = ActiveDocument ?? (Documents.Contains(_assistantContextDocument!) ? _assistantContextDocument : null);
        var path = editor?.State.Path;
        var owner = path is null ? null : Projects.FirstOrDefault(project => project.Files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
        var project = owner ?? StartupProject;
        return new()
        {
            WorkspaceFolder = Workspace is null ? null : WorkspaceRoot,
            ActiveFile = path,
            ActiveLine = editor?.State.CaretLine ?? 0,
            SelectedText = editor?.SelectedText ?? "",
            ProjectDirectory = project is null ? null : Path.GetDirectoryName(project.ProjectPath),
            Configuration = Configuration
        };
    }

    async Task<bool> ITaskHost.ReviewAndSaveAsync(string path, string before, string after, string title)
    {
        var change = new FileChange(path, before, after, before.Length == 0 ? "New tasks file" : "Add a task");
        if (!await PreviewAsync(title, [change], "")) return false;
        await ApplyChangesAsync([change]);
        return Documents.FirstOrDefault(document => document.State.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is not { } editor || await SaveDocumentAsync(editor);
    }

    async Task ITaskHost.OpenTasksFileAsync(string path, string template, int line)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && _store.Find(path) is null)
        {
            // A new file starts as an unsaved tab, so nothing is written to the repository until it is saved.
            AddDocument(_store.Create(path, template));
            Status = $"New tasks file {path}. Save it to keep it.";
            return;
        }
        await NavigateAsync(path, Math.Max(1, line), 1);
    }

    private IEnumerable<PaletteEntry> TaskCommands()
    {
        yield return Command("Tasks", "Run task…", RunTaskPaletteCommand, icon: "Play", detail: "Pick a task from tasks.json");
        // Only where they can run something, so they do not crowd out Build and Rebuild in a workspace without tasks.
        if (Tasks.All.Any(item => item.Definition.Group == TaskGroup.Build))
            yield return Command("Tasks", "Run build task", Tasks.RunBuildTaskCommand, icon: "Build", detail: "The default task of the build group");
        if (Tasks.All.Any(item => item.Definition.Group == TaskGroup.Test))
            yield return Command("Tasks", "Run test task", Tasks.RunTestTaskCommand, icon: "Flask", detail: "The default task of the test group");
        yield return Command("Tasks", "New task…", Tasks.NewTaskCommand, icon: "Plus", detail: "Fill in a form, try it, then save it to the repository or your profile");
        yield return Command("Tasks", "Edit shared tasks (.wpfstudio/tasks.json)", Tasks.EditSharedCommand, icon: "Braces");
        yield return Command("Tasks", "Edit personal tasks", Tasks.EditPersonalCommand, icon: "Braces", detail: "Your own tasks, in every workspace");
        yield return Command("Tasks", "Stop all tasks", Tasks.StopAllCommand, icon: "Stop");
        yield return Command("View", "Tasks", ShowToolCommand, Workbenches.Tasks, icon: "Play", detail: "Tasks from .vscode/tasks.json, .wpfstudio/tasks.json and your profile");
        foreach (var item in Tasks.All.Where(item => !item.Definition.IsHidden))
            yield return new PaletteEntry("Task: " + item.Label, item.Detail, () => Tasks.RunByLabelAsync(item.Label), "Tasks", Icon: "Play");
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand] private void RunTaskPalette() => Palette.OpenCommands("> Task: ");
}
