namespace WpfStudio.Core.Tasks;

/// <summary>Where a task was defined. A later source wins over an earlier one when two define the same label.</summary>
public enum TaskSource { VisualStudioCode, Shared, Personal }

/// <summary>A shell task runs its command line through a shell; a process task starts its command directly.</summary>
public enum TaskKind { Shell, Process }

public enum TaskGroup { None, Build, Test }

/// <summary>How the task's output is read for problems. MSBuild reads the <c>file(line,col): error CODE: message</c> lines compilers and MSBuild write.</summary>
public enum TaskProblemMatcher { None, MSBuild }

/// <summary>A shell to run a shell task with, instead of the default one.</summary>
public sealed record TaskShell(string Executable, IReadOnlyList<string> Args);

/// <summary>
/// One entry of a tasks.json file, after the file's own defaults and its Windows overrides are applied. Values still hold
/// their <c>${variables}</c>, which are expanded when the task runs. A task without a command only runs its dependencies.
/// </summary>
public sealed record StudioTask
{
    public required string Label { get; init; }
    public TaskKind Kind { get; init; } = TaskKind.Shell;
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public TaskShell? Shell { get; init; }
    public TaskGroup Group { get; init; }
    /// <summary>The group's default task, which Run build task and Run test task start without asking.</summary>
    public bool IsDefault { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    /// <summary>Dependencies run one after another, stopping at the first failure, instead of all at once.</summary>
    public bool DependsInSequence { get; init; }
    /// <summary>A watcher or server that keeps running. Tasks that depend on it start once it has started.</summary>
    public bool IsBackground { get; init; }
    public TaskProblemMatcher ProblemMatcher { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>Kept out of the task lists, so it only runs as another task's dependency.</summary>
    public bool IsHidden { get; init; }
    public TaskSource Source { get; init; }
    public string SourcePath { get; init; } = "";
    /// <summary>The 1-based line its definition starts on, or 0 when unknown.</summary>
    public int Line { get; init; }
    /// <summary>Parts of the definition WpfStudio read but cannot honour, such as a problem matcher it does not know.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool HasCommand => Command.Length > 0;
}

/// <summary>A tasks.json file that could not be read completely; <paramref name="Line"/> is 0 when the problem has no place.</summary>
public sealed record TaskFileProblem(string Path, string Message, int Line = 0)
{
    /// <summary>The file's folder and name, and the line when known, such as <c>.wpfstudio/tasks.json, line 4</c>.</summary>
    public string Location => (Path.Length == 0 ? "tasks.json" : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) + "/" + System.IO.Path.GetFileName(Path)) + (Line > 0 ? $", line {Line}" : "");
}

public sealed record TaskFile(string Path, TaskSource Source, IReadOnlyList<StudioTask> Tasks, IReadOnlyList<TaskFileProblem> Problems)
{
    public static TaskFile Empty(string path, TaskSource source) => new(path, source, [], []);
}

/// <summary>Thrown when a task cannot run as written: an unknown variable, a missing file for <c>${file}</c>, a dependency cycle.</summary>
public sealed class TaskConfigurationException(string message) : Exception(message);

/// <summary>The tasks of every source, merged by label.</summary>
public static class TaskCatalog
{
    /// <summary>Repository files the tasks are read from, relative to the workspace folder, in increasing precedence.</summary>
    public static readonly string VisualStudioCodeFile = Path.Combine(".vscode", "tasks.json"), SharedFile = Path.Combine(".wpfstudio", "tasks.json");
    /// <summary>The personal file, kept in the WpfStudio profile and read in every workspace.</summary>
    public static string PersonalFile(string dataDirectory) => Path.Combine(dataDirectory, "tasks.json");

    /// <summary>Later files win over earlier ones for the same label, and keep the earlier task's place in the list.</summary>
    public static IReadOnlyList<StudioTask> Merge(IEnumerable<TaskFile> files)
    {
        var merged = new List<StudioTask>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var task in files.SelectMany(file => file.Tasks))
        {
            if (positions.TryGetValue(task.Label, out var position)) merged[position] = task;
            else { positions[task.Label] = merged.Count; merged.Add(task); }
        }
        return merged;
    }

    /// <summary>The group's task to run without asking: its default, or its only task. Null when there is none or the choice is not clear.</summary>
    public static StudioTask? DefaultFor(IEnumerable<StudioTask> tasks, TaskGroup group)
    {
        var members = tasks.Where(task => task.Group == group).ToArray();
        var defaults = members.Where(task => task.IsDefault).ToArray();
        return defaults.Length == 1 ? defaults[0] : defaults.Length == 0 && members.Length == 1 ? members[0] : null;
    }
}
