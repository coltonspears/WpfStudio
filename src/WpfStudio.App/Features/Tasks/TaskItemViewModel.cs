using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;
using WpfStudio.Core.Tasks;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.App.Features.Tasks;

public enum TaskRunState { Idle, Running, Succeeded, Failed, Stopped }

/// <summary>One task in the Tasks pane, with the state of its latest run. A reload keeps the row and swaps in the new definition.</summary>
public sealed partial class TaskItemViewModel(StudioTask task) : ObservableObject
{
    private readonly HashSet<WorkspaceDiagnostic> _known = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Label), nameof(Detail), nameof(GroupText), nameof(SourceText), nameof(ToolTip), nameof(CanEdit))]
    public partial StudioTask Definition { get; set; } = task;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRunning), nameof(StateText))] public partial TaskRunState State { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] public partial int? ExitCode { get; set; }

    public string Label => Definition.Label;
    public bool IsRunning => State == TaskRunState.Running;
    public string GroupText => Definition.Group switch { TaskGroup.Build => Definition.IsDefault ? "build (default)" : "build", TaskGroup.Test => Definition.IsDefault ? "test (default)" : "test", _ => "" };
    public string SourceText => Definition.Source switch { TaskSource.Personal => "Personal", TaskSource.Shared => "Shared", _ => "VS Code" };
    public bool CanEdit => Definition.SourcePath.Length > 0;
    /// <summary>The task's own description, or else what it runs.</summary>
    public string Detail => Definition.Detail.Length > 0 ? Definition.Detail
        : Definition.HasCommand ? string.Join(' ', Definition.Args.Prepend(Definition.Command))
        : (Definition.DependsInSequence ? "Runs in order: " : "Runs together: ") + string.Join(", ", Definition.DependsOn);
    public string ToolTip => string.Join(Environment.NewLine, new[]
    {
        Definition.HasCommand ? (Definition.Kind == TaskKind.Process ? "Process: " : "Shell: ") + string.Join(' ', Definition.Args.Prepend(Definition.Command)) : "",
        Definition.DependsOn.Count > 0 ? (Definition.DependsInSequence ? "Depends on, in order: " : "Depends on: ") + string.Join(", ", Definition.DependsOn) : "",
        Definition.IsBackground ? "Keeps running in the background" : "",
        Definition.ProblemMatcher == TaskProblemMatcher.MSBuild ? "MSBuild errors and warnings go to Problems" : "",
        Definition.SourcePath.Length > 0 ? "Defined in " + Definition.SourcePath + (Definition.Line > 0 ? $" line {Definition.Line}" : "") : "",
    }.Concat(Definition.Notes).Where(line => line.Length > 0));
    public string StateText => State switch
    {
        TaskRunState.Running => "Running",
        TaskRunState.Succeeded => "Succeeded",
        TaskRunState.Failed => ExitCode is { } code ? $"Failed (exit {code})" : "Failed",
        TaskRunState.Stopped => "Stopped",
        _ => ""
    };

    /// <summary>The tab of the latest run, which a new run replaces once this one has finished.</summary>
    internal TerminalSessionViewModel? Terminal { get; set; }
    /// <summary>The latest run until its program ends; a background task's run ends when it has started.</summary>
    internal System.Threading.Tasks.Task<bool>? CurrentRun { get; set; }
    internal bool StopRequested { get; set; }
    /// <summary>Problems read from the latest run's output.</summary>
    internal List<WorkspaceDiagnostic> Diagnostics { get; } = [];
    internal bool AddDiagnostic(WorkspaceDiagnostic diagnostic)
    {
        if (Diagnostics.Count >= 2000 || !_known.Add(diagnostic)) return false;
        Diagnostics.Add(diagnostic);
        return true;
    }
    internal void ClearDiagnostics() { Diagnostics.Clear(); _known.Clear(); }
}

/// <summary>The New task form: what it holds becomes a task in the shared or the personal tasks file.</summary>
public sealed partial class TaskEditorViewModel : ObservableObject
{
    public IReadOnlyList<TaskGroup> Groups { get; } = [TaskGroup.None, TaskGroup.Build, TaskGroup.Test];
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Label { get; set; } = "";
    /// <summary>Starts the command directly instead of through PowerShell.</summary>
    [ObservableProperty] public partial bool IsProcess { get; set; }
    [ObservableProperty] public partial string Command { get; set; } = "";
    [ObservableProperty] public partial string Arguments { get; set; } = "";
    [ObservableProperty] public partial string WorkingDirectory { get; set; } = "";
    /// <summary>One <c>NAME=value</c> per line.</summary>
    [ObservableProperty] public partial string EnvironmentText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGroup))] public partial TaskGroup Group { get; set; }
    [ObservableProperty] public partial bool IsDefault { get; set; }
    [ObservableProperty] public partial bool MatchMSBuildProblems { get; set; }
    /// <summary>Saves to the personal file instead of the repository's .wpfstudio/tasks.json.</summary>
    [ObservableProperty] public partial bool SaveToPersonal { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string TestStatus { get; set; } = "";
    public bool HasGroup => Group != TaskGroup.None;
    internal TaskItemViewModel? TestRun { get; set; }

    public void Reset(bool personal)
    {
        Label = ""; IsProcess = false; Command = ""; Arguments = ""; WorkingDirectory = ""; EnvironmentText = "";
        Group = TaskGroup.None; IsDefault = false; MatchMSBuildProblems = false; SaveToPersonal = personal; Error = ""; TestStatus = "";
    }

    /// <summary>The form as a task definition; throws <see cref="TaskConfigurationException"/> with what to fix.</summary>
    public TaskDraft ToDraft()
    {
        var label = Label.Trim();
        if (label.Length == 0) throw new TaskConfigurationException("Give the task a label.");
        var command = Command.Trim();
        if (command.Length == 0) throw new TaskConfigurationException("Enter the command to run.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in EnvironmentText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) throw new TaskConfigurationException($"Write each environment variable as NAME=value: \"{line}\".");
            environment[line[..equals].Trim()] = line[(equals + 1)..];
        }
        var args = WpfStudio.Workspace.BuildService.SplitArguments(Arguments.Trim());
        return new TaskDraft(label, IsProcess ? TaskKind.Process : TaskKind.Shell, command, args, WorkingDirectory.Trim() is { Length: > 0 } directory ? directory : null,
            environment, Group, Group != TaskGroup.None && IsDefault, MatchMSBuildProblems);
    }

    /// <summary>The draft as a task that can run before it is saved.</summary>
    public static StudioTask ToTask(TaskDraft draft, string label) => new()
    {
        Label = label,
        Kind = draft.Kind,
        Command = draft.Command,
        Args = draft.Args,
        WorkingDirectory = draft.WorkingDirectory,
        Environment = draft.Environment,
        Group = draft.Group,
        IsDefault = draft.IsDefault,
        ProblemMatcher = draft.MatchMSBuildProblems ? TaskProblemMatcher.MSBuild : TaskProblemMatcher.None,
        Source = TaskSource.Personal
    };
}
