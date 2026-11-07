using System.Text;
using System.Text.Json;

namespace WpfStudio.Core.Tasks;

/// <summary>
/// Reads tasks.json in the subset of Visual Studio Code's schema that runs outside it: shell and process tasks with
/// their options, groups, dependencies and the MSBuild problem matcher. Comments and trailing commas are allowed, as
/// they are there. A task that cannot be read is left out and reported; the rest of the file still loads.
/// </summary>
public static class TaskFileReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly string[] MSBuildMatchers = ["$msCompile", "$msbuild", "msbuild", "$dotnet"];

    public static async Task<TaskFile> ReadFileAsync(string path, TaskSource source, CancellationToken token = default)
    {
        try { return File.Exists(path) ? Read(path, await File.ReadAllTextAsync(path, token).ConfigureAwait(false), source) : TaskFile.Empty(path, source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(path, source, [], [new(path, "The file could not be read: " + ex.Message)]); }
    }

    public static TaskFile Read(string path, string text, TaskSource source)
    {
        if (string.IsNullOrWhiteSpace(text)) return TaskFile.Empty(path, source);
        var problems = new List<TaskFileProblem>();
        var bytes = Encoding.UTF8.GetBytes(text.TrimStart('﻿'));
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, DocumentOptions); }
        catch (JsonException ex) { return new(path, source, [], [new(path, "The file is not valid JSON: " + FirstSentence(ex.Message), (int)(ex.LineNumber ?? -1) + 1)]); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(path, source, [], [new(path, "The file must hold one JSON object with a \"tasks\" list.", 1)]);
            if (!root.TryGetProperty("tasks", out var list)) return new(path, source, [], []);
            if (list.ValueKind != JsonValueKind.Array) return new(path, source, [], [new(path, "\"tasks\" must be a list.", 1)]);
            var lines = TaskLines(bytes);
            var defaults = ReadDefaults(root);
            var tasks = new List<StudioTask>();
            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                var line = index < lines.Count ? lines[index] : 0;
                index++;
                if (element.ValueKind != JsonValueKind.Object) { problems.Add(new(path, "Each entry of \"tasks\" must be an object.", line)); continue; }
                try
                {
                    var task = ReadTask(element, defaults) with { Source = source, SourcePath = path, Line = line };
                    if (tasks.Any(existing => existing.Label == task.Label)) problems.Add(new(path, $"Two tasks are labelled '{task.Label}'. The first one is used.", line));
                    else tasks.Add(task);
                }
                catch (TaskConfigurationException ex) { problems.Add(new(path, ex.Message, line)); }
            }
            return new(path, source, tasks, problems);
        }
    }

    /// <summary>File-wide options, with the file's Windows options applied over them.</summary>
    private sealed record FileDefaults(string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TaskShell? Shell);

    private static FileDefaults ReadDefaults(JsonElement root)
    {
        var options = Object(root, "options");
        var windows = Object(Object(root, "windows") ?? default, "options");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddEnvironment(environment, options, "options.env");
        AddEnvironment(environment, windows, "windows.options.env");
        return new(Text(windows ?? default, "cwd") ?? Text(options ?? default, "cwd"), environment, ReadShell(windows) ?? ReadShell(options));
    }

    private static StudioTask ReadTask(JsonElement element, FileDefaults defaults)
    {
        // A task's "windows" block overrides its own properties on Windows, and its options are merged over the task's.
        var windows = Object(element, "windows");
        JsonElement? Property(string name) => windows is { } platform && platform.TryGetProperty(name, out var value) ? value : element.TryGetProperty(name, out var own) ? own : null;
        var label = (Property("label") ?? Property("taskName")) is { ValueKind: JsonValueKind.String } named ? named.GetString()!.Trim() : "";
        if (label.Length == 0) throw new TaskConfigurationException("A task needs a \"label\".");
        string Where(string name) => $"Task '{label}': \"{name}\"";

        var command = Property("command") switch
        {
            null or { ValueKind: JsonValueKind.Null } => "",
            { ValueKind: JsonValueKind.String } text => text.GetString()!,
            { ValueKind: JsonValueKind.Array } parts => string.Join(' ', parts.EnumerateArray().Select(part => StringValue(part, Where("command")))),
            { ValueKind: JsonValueKind.Object } quoted => StringOrList(quoted, "value", Where("command")),
            _ => throw new TaskConfigurationException(Where("command") + " must be text.")
        };
        var type = Property("type") is { ValueKind: JsonValueKind.String } typed ? typed.GetString()! : "shell";
        var kind = type.ToLowerInvariant() switch
        {
            "shell" => TaskKind.Shell,
            "process" => TaskKind.Process,
            _ => throw new TaskConfigurationException($"Task '{label}' has type \"{type}\", which needs a Visual Studio Code extension. WpfStudio runs \"shell\" and \"process\" tasks.")
        };
        var args = Property("args") switch
        {
            null or { ValueKind: JsonValueKind.Null } => [],
            { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Select(arg => arg.ValueKind == JsonValueKind.Object ? StringOrList(arg, "value", Where("args")) : StringValue(arg, Where("args"))).ToArray(),
            _ => throw new TaskConfigurationException(Where("args") + " must be a list.")
        };

        var options = Object(element, "options");
        var windowsOptions = Object(windows ?? default, "options");
        var environment = new Dictionary<string, string>(defaults.Environment, StringComparer.OrdinalIgnoreCase);
        AddEnvironment(environment, options, Where("options.env"));
        AddEnvironment(environment, windowsOptions, Where("windows.options.env"));
        var workingDirectory = Text(windowsOptions ?? default, "cwd") ?? Text(options ?? default, "cwd") ?? defaults.WorkingDirectory;

        var (group, isDefault) = Property("group") switch
        {
            null or { ValueKind: JsonValueKind.Null } => (TaskGroup.None, false),
            { ValueKind: JsonValueKind.String } name => (Group(name.GetString()!), false),
            // isDefault may also be a file glob in newer schemas; any value but false makes it the default.
            { ValueKind: JsonValueKind.Object } detail => (Group(Text(detail, "kind") ?? "none"), detail.TryGetProperty("isDefault", out var flag) && flag.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)),
            _ => throw new TaskConfigurationException(Where("group") + " must be \"build\", \"test\" or an object with a \"kind\".")
        };
        var dependsOn = Property("dependsOn") switch
        {
            null or { ValueKind: JsonValueKind.Null } => [],
            { ValueKind: JsonValueKind.String } one => [one.GetString()!],
            { ValueKind: JsonValueKind.Array } many => many.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object && Text(item, "label") is { } dependency ? dependency : StringValue(item, Where("dependsOn"))).ToArray(),
            _ => throw new TaskConfigurationException(Where("dependsOn") + " must be a label or a list of labels.")
        };
        if (command.Length == 0 && dependsOn.Length == 0) throw new TaskConfigurationException($"Task '{label}' needs a \"command\" or a \"dependsOn\" list.");

        var notes = new List<string>();
        var matcher = TaskProblemMatcher.None;
        foreach (var name in MatcherNames(Property("problemMatcher")))
        {
            if (MSBuildMatchers.Contains(name, StringComparer.OrdinalIgnoreCase)) matcher = TaskProblemMatcher.MSBuild;
            else notes.Add($"The problem matcher {name} isn't supported, so its output isn't read for problems.");
        }
        return new StudioTask
        {
            Label = label,
            Kind = kind,
            Command = command,
            Args = args,
            WorkingDirectory = workingDirectory,
            Environment = environment,
            Shell = ReadShell(windowsOptions) ?? ReadShell(options) ?? defaults.Shell,
            Group = group,
            IsDefault = isDefault,
            DependsOn = dependsOn,
            DependsInSequence = Property("dependsOrder") is { ValueKind: JsonValueKind.String } order && order.GetString()!.Equals("sequence", StringComparison.OrdinalIgnoreCase),
            IsBackground = Property("isBackground")?.ValueKind == JsonValueKind.True,
            ProblemMatcher = matcher,
            Detail = Property("detail") is { ValueKind: JsonValueKind.String } detailText ? detailText.GetString()! : "",
            IsHidden = Property("hide")?.ValueKind == JsonValueKind.True,
            Notes = notes
        };
    }

    private static TaskGroup Group(string name) => name.ToLowerInvariant() switch
    {
        "build" => TaskGroup.Build,
        "test" => TaskGroup.Test,
        _ => TaskGroup.None
    };

    /// <summary>A matcher is a name, an object based on a name, or a list of either.</summary>
    private static IEnumerable<string> MatcherNames(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } name => [name.GetString()!],
        { ValueKind: JsonValueKind.Object } custom => [Text(custom, "base") ?? Text(custom, "name") ?? "(custom pattern)"],
        { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().SelectMany(item => MatcherNames(item)),
        _ => []
    };

    private static TaskShell? ReadShell(JsonElement? options)
    {
        if (Object(options ?? default, "shell") is not { } shell || Text(shell, "executable") is not { Length: > 0 } executable) return null;
        var args = shell.TryGetProperty("args", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(arg => StringValue(arg, "\"options.shell.args\"")).ToArray() : null;
        return new(executable, args ?? []);
    }

    private static void AddEnvironment(Dictionary<string, string> environment, JsonElement? options, string where)
    {
        if (Object(options ?? default, "env") is not { } variables) return;
        foreach (var variable in variables.EnumerateObject())
            environment[variable.Name] = variable.Value.ValueKind switch
            {
                JsonValueKind.String => variable.Value.GetString()!,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => variable.Value.GetRawText(),
                _ => throw new TaskConfigurationException($"{where}: {variable.Name} must be text.")
            };
    }

    private static string StringValue(JsonElement value, string where) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => throw new TaskConfigurationException(where + " must hold text.")
    };
    private static string StringOrList(JsonElement value, string property, string where) => value.TryGetProperty(property, out var inner) && inner.ValueKind == JsonValueKind.Array
        ? string.Join(' ', inner.EnumerateArray().Select(part => StringValue(part, where))) : inner.ValueKind == JsonValueKind.Undefined ? throw new TaskConfigurationException(where + " needs a \"value\".") : StringValue(inner, where);
    private static JsonElement? Object(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.Object ? member : null;
    private static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;
    private static string FirstSentence(string message) => message.Split(". ", 2)[0].TrimEnd('.') + ".";

    /// <summary>The line each entry of the root "tasks" list starts on, in order.</summary>
    private static List<int> TaskLines(byte[] bytes)
    {
        var starts = new List<int>();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var inTasks = false;
        var tasksDepth = -1;
        while (reader.Read())
        {
            if (!inTasks)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("tasks"u8)
                    && reader.Read() && reader.TokenType == JsonTokenType.StartArray) { inTasks = true; tasksDepth = reader.CurrentDepth; }
                continue;
            }
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == tasksDepth) break;
            if (reader.CurrentDepth == tasksDepth + 1 && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
            {
                starts.Add(LineAt(bytes, (int)reader.TokenStartIndex));
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            }
        }
        return starts;
    }
    private static int LineAt(byte[] bytes, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < bytes.Length; i++) if (bytes[i] == (byte)'\n') line++;
        return line;
    }
}
