using System.Text;
using System.Text.RegularExpressions;

namespace WpfStudio.Core.Tasks;

/// <summary>What a task's <c>${variables}</c> stand for when it starts. Null members are not available, and a task that uses one fails to start.</summary>
public sealed record TaskVariableContext
{
    /// <summary>The folder of the open solution or project.</summary>
    public string? WorkspaceFolder { get; init; }
    public string? ActiveFile { get; init; }
    public int ActiveLine { get; init; }
    public string SelectedText { get; init; } = "";
    /// <summary>The folder of the project that owns the active file, or else of the startup project.</summary>
    public string? ProjectDirectory { get; init; }
    public string Configuration { get; init; } = "Debug";
}

/// <summary>Expands Visual Studio Code's task variables, plus <c>${projectDir}</c> and <c>${configuration}</c> from the WpfStudio toolbar.</summary>
public static partial class TaskVariables
{
    /// <summary>The variables a task may use, for help text and templates.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["workspaceFolder", "workspaceFolderBasename", "file", "fileBasename", "fileBasenameNoExtension", "fileDirname", "fileExtname", "relativeFile", "relativeFileDirname",
         "lineNumber", "selectedText", "projectDir", "configuration", "cwd", "userHome", "pathSeparator", "env:NAME"];

    public static string Expand(string text, TaskVariableContext context) => text.Contains("${", StringComparison.Ordinal)
        ? VariablePattern().Replace(text, match => Value(match.Groups["name"].Value, context))
        : text;

    private static string Value(string name, TaskVariableContext context)
    {
        if (name.StartsWith("env:", StringComparison.Ordinal)) return Environment.GetEnvironmentVariable(name[4..]) ?? "";
        string Need(string? value, string what) => value ?? throw new TaskConfigurationException($"${{{name}}} needs {what}.");
        string File() => Need(context.ActiveFile, "an open file");
        string Workspace() => Need(context.WorkspaceFolder, "an open solution or project");
        return name switch
        {
            "workspaceFolder" or "workspaceRoot" or "cwd" => Workspace(),
            "workspaceFolderBasename" => Path.GetFileName(Workspace().TrimEnd(Path.DirectorySeparatorChar)),
            "file" => File(),
            "fileBasename" => Path.GetFileName(File()),
            "fileBasenameNoExtension" => Path.GetFileNameWithoutExtension(File()),
            "fileDirname" => Path.GetDirectoryName(File()) ?? "",
            "fileExtname" => Path.GetExtension(File()),
            "relativeFile" => Path.GetRelativePath(Workspace(), File()),
            "relativeFileDirname" => Path.GetRelativePath(Workspace(), Path.GetDirectoryName(File()) ?? Workspace()),
            "lineNumber" => context.ActiveFile is null ? Need(null, "an open file") : context.ActiveLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "selectedText" => context.SelectedText,
            "projectDir" => Need(context.ProjectDirectory, "an open project"),
            "configuration" => context.Configuration,
            "userHome" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "pathSeparator" or "/" => Path.DirectorySeparatorChar.ToString(),
            _ => throw new TaskConfigurationException($"${{{name}}} isn't a variable WpfStudio knows. It knows " + string.Join(", ", Names.Select(known => "${" + known + "}")) + ".")
        };
    }

    [GeneratedRegex(@"\$\{(?<name>[^}]+)\}")]
    private static partial Regex VariablePattern();
}

/// <summary>A task ready for a pseudoconsole: the program, its whole argument string, where it runs and what it adds to the environment.</summary>
public sealed record TaskLaunch(string Executable, string Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, string DisplayCommand);

/// <summary>Turns a task into the command line Windows starts, quoting the way the chosen shell reads it.</summary>
public static class TaskCommandLine
{
    /// <summary>
    /// <paramref name="defaultShell"/> runs shell tasks that name no shell of their own; Visual Studio Code uses
    /// PowerShell there too. <paramref name="findExecutable"/> resolves a bare program name the way PATH does.
    /// </summary>
    public static TaskLaunch Create(StudioTask task, TaskVariableContext context, TaskShell defaultShell, Func<string, string?> findExecutable, string fallbackDirectory)
    {
        if (!task.HasCommand) throw new TaskConfigurationException($"Task '{task.Label}' has no command to run.");
        var command = TaskVariables.Expand(task.Command, context).Trim();
        var args = task.Args.Select(arg => TaskVariables.Expand(arg, context)).ToArray();
        var directory = task.WorkingDirectory is { Length: > 0 } configured ? TaskVariables.Expand(configured, context) : context.WorkspaceFolder ?? fallbackDirectory;
        directory = Path.GetFullPath(directory, context.WorkspaceFolder ?? fallbackDirectory);
        if (!Directory.Exists(directory)) throw new TaskConfigurationException($"Task '{task.Label}' runs in {directory}, which does not exist.");
        var environment = task.Environment.ToDictionary(pair => pair.Key, pair => TaskVariables.Expand(pair.Value, context), StringComparer.OrdinalIgnoreCase);
        // The terminal logger redraws its progress in place, which leaves nothing whole to read errors from.
        if (task.ProblemMatcher == TaskProblemMatcher.MSBuild && !environment.ContainsKey("MSBUILDTERMINALLOGGER")) environment["MSBUILDTERMINALLOGGER"] = "off";

        if (task.Kind == TaskKind.Process)
        {
            var program = Resolve(command, directory, findExecutable) ?? throw new TaskConfigurationException($"Task '{task.Label}': {command} was not found on PATH.");
            var display = string.Join(' ', args.Prepend(command).Select(QuoteForWindows));
            return new(program, string.Join(' ', args.Select(QuoteForWindows)), directory, environment, display);
        }
        var shell = task.Shell is { } own ? own with { Executable = TaskVariables.Expand(own.Executable, context) } : defaultShell;
        var shellProgram = Resolve(shell.Executable, directory, findExecutable) ?? throw new TaskConfigurationException($"Task '{task.Label}': the shell {shell.Executable} was not found.");
        var style = StyleOf(shellProgram);
        var line = args.Length == 0 ? command : string.Join(' ', args.Select(arg => Quote(arg, style)).Prepend(QuoteCommand(command, style)));
        var shellArgs = task.Shell is { Args.Count: > 0 } ? task.Shell.Args : DefaultArguments(style);
        // cmd /s /c drops the outer quotes and runs the rest as typed, so the line is not escaped for it.
        var arguments = style == ShellStyle.Cmd
            ? string.Join(' ', shellArgs.Select(QuoteForWindows)) + " \"" + line + "\""
            : string.Join(' ', shellArgs.Select(QuoteForWindows).Append(QuoteForWindows(line)));
        return new(shellProgram, arguments, directory, environment, line);
    }

    /// <summary>PowerShell 7 when it is on PATH, otherwise Windows PowerShell.</summary>
    public static TaskShell DefaultShell(Func<string, string?> findExecutable) =>
        new(findExecutable("pwsh.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), []);

    /// <summary>Searches PATH, trying PATHEXT's extensions for a name without one.</summary>
    public static string? FindOnPath(string name)
    {
        var extensions = Path.HasExtension(name) ? [""] : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim().Trim('"'), name + extension); } catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private enum ShellStyle { PowerShell, Cmd, Posix }
    private static ShellStyle StyleOf(string shell) => Path.GetFileNameWithoutExtension(shell).ToLowerInvariant() switch
    {
        "cmd" => ShellStyle.Cmd,
        "pwsh" or "powershell" => ShellStyle.PowerShell,
        _ => ShellStyle.Posix
    };
    private static IReadOnlyList<string> DefaultArguments(ShellStyle style) => style switch
    {
        ShellStyle.Cmd => ["/d", "/s", "/c"],
        ShellStyle.PowerShell => ["-NoLogo", "-Command"],
        _ => ["-c"]
    };

    /// <summary>A program path as given, relative to the task's folder, or found on PATH.</summary>
    private static string? Resolve(string program, string directory, Func<string, string?> findExecutable)
    {
        program = program.Trim('"');
        if (Path.IsPathRooted(program) || program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(program, directory);
            if (File.Exists(full)) return full;
            if (!Path.HasExtension(full) && File.Exists(full + ".exe")) return full + ".exe";
            return null;
        }
        return findExecutable(program);
    }

    // With arguments, the command is one word of the line; on its own it is the whole line, as Visual Studio Code reads it.
    private static string QuoteCommand(string command, ShellStyle style) => !NeedsQuotes(command) ? command : style == ShellStyle.PowerShell ? "& " + Quote(command, style) : Quote(command, style);
    private static string Quote(string value, ShellStyle style) => !NeedsQuotes(value) ? value : style switch
    {
        ShellStyle.PowerShell => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
        ShellStyle.Posix => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
        _ => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
    };
    private static bool NeedsQuotes(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace);

    /// <summary>Quotes one argument so CommandLineToArgvW, which .NET programs and most others use, reads it back unchanged.</summary>
    public static string QuoteForWindows(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') text.Append('\\', backslashes * 2 + 1);
            else text.Append('\\', backslashes);
            backslashes = 0;
            text.Append(c);
        }
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
