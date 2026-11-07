using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WpfStudio.Core.Tasks;

/// <summary>What the task editor knows how to write. Anything else is written in the file by hand.</summary>
public sealed record TaskDraft(string Label, TaskKind Kind, string Command, IReadOnlyList<string> Args, string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment, TaskGroup Group, bool IsDefault, bool MatchMSBuildProblems, string Detail = "");

/// <summary>
/// Adds a task to a tasks.json file as text, so its comments, layout and every property WpfStudio does not read stay
/// as they were. Only the end of the root "tasks" list changes.
/// </summary>
public static class TaskFileWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Template(TaskSource source) => source == TaskSource.Personal
        ? """
          {
            // Your own tasks, available in every workspace and kept in your WpfStudio profile. A task here wins over a
            // repository task with the same label. Run them from the Tasks pane or the command palette (Task: ...).
            // Variables: ${workspaceFolder}, ${file}, ${fileDirname}, ${projectDir}, ${configuration}, ${env:NAME}.
            "version": "2.0.0",
            "tasks": []
          }

          """
        : """
          {
            // Tasks for this repository, shared through source control. WpfStudio also reads .vscode/tasks.json;
            // a task here wins over one there with the same label. Run them from the Tasks pane or the command palette.
            // Variables: ${workspaceFolder}, ${file}, ${fileDirname}, ${projectDir}, ${configuration}, ${env:NAME}.
            "version": "2.0.0",
            "tasks": []
          }

          """;

    public static JsonObject ToJson(TaskDraft draft)
    {
        var task = new JsonObject { ["label"] = draft.Label, ["type"] = draft.Kind == TaskKind.Process ? "process" : "shell", ["command"] = draft.Command };
        if (draft.Args.Count > 0) task["args"] = new JsonArray(draft.Args.Select(arg => (JsonNode)JsonValue.Create(arg)!).ToArray());
        if (draft.WorkingDirectory is { Length: > 0 } || draft.Environment.Count > 0)
        {
            var options = new JsonObject();
            if (draft.WorkingDirectory is { Length: > 0 } directory) options["cwd"] = directory;
            if (draft.Environment.Count > 0) options["env"] = new JsonObject(draft.Environment.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))));
            task["options"] = options;
        }
        if (draft.Group != TaskGroup.None)
            task["group"] = draft.IsDefault ? new JsonObject { ["kind"] = GroupName(draft.Group), ["isDefault"] = true } : GroupName(draft.Group);
        if (draft.MatchMSBuildProblems) task["problemMatcher"] = "$msCompile";
        if (draft.Detail.Length > 0) task["detail"] = draft.Detail;
        return task;
    }

    private static string GroupName(TaskGroup group) => group == TaskGroup.Test ? "test" : "build";

    /// <summary>The file's text with <paramref name="draft"/> added at the end of its "tasks" list; an empty file becomes the template holding it.</summary>
    public static string AddTask(string text, TaskDraft draft, TaskSource source)
    {
        if (string.IsNullOrWhiteSpace(text)) text = Template(source);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var scanner = new Scanner(text);
        var root = scanner.NextSignificant(0);
        if (root < 0 || text[root] != '{') throw new InvalidOperationException("The tasks file must hold one JSON object.");
        var rootEnd = scanner.MatchingClose(root);
        if (rootEnd < 0) throw new InvalidOperationException("The tasks file's object is not closed.");
        string Json(string indent) => string.Join(newLine, ToJson(draft).ToJsonString(Indented).Split('\n').Select(line => indent + line.TrimEnd('\r')));

        string result;
        if (scanner.FindRootProperty(root, rootEnd, "tasks") is { } tasks)
        {
            var open = scanner.NextSignificant(tasks);
            if (open < 0 || text[open] != '[') throw new InvalidOperationException("\"tasks\" must be a list.");
            var close = scanner.MatchingClose(open);
            if (close < 0) throw new InvalidOperationException("The \"tasks\" list is not closed.");
            var last = scanner.PreviousSignificant(close, open);
            var listIndent = IndentOf(text, open);
            if (last == open)
            {
                var elementIndent = listIndent + "  ";
                result = text[..(open + 1)] + newLine + Json(elementIndent) + newLine + listIndent + text[close..];
            }
            else
            {
                var elementIndent = IndentOf(text, scanner.NextSignificant(open + 1));
                result = Insert(text, last, text[last] == ',' ? "" : ",", Json(elementIndent), newLine);
            }
        }
        else
        {
            var last = scanner.PreviousSignificant(rootEnd, root);
            var indent = IndentOf(text, root) + "  ";
            var list = indent + "\"tasks\": [" + newLine + Json(indent + "  ") + newLine + indent + "]";
            result = Insert(text, last, last == root || text[last] == ',' ? "" : ",", list, newLine);
        }
        var check = TaskFileReader.Read("tasks.json", result, source);
        if (check.Problems.Count > 0 || !check.Tasks.Any(task => task.Label == draft.Label))
            throw new InvalidOperationException("The task could not be added to the file as it is: " + string.Join(" ", check.Problems.Select(problem => problem.Message)));
        return result;
    }

    /// <summary>
    /// Puts <paramref name="block"/> on new lines after the token at <paramref name="last"/>, with <paramref name="separator"/> straight
    /// after that token. A comment at the end of its line stays on that line, with the entry it describes.
    /// </summary>
    private static string Insert(string text, int last, string separator, string block, string newLine)
    {
        var point = last + 1;
        var lineEnd = text.IndexOfAny(['\r', '\n'], point);
        if (lineEnd < 0) lineEnd = text.Length;
        var rest = text[point..lineEnd].Trim();
        if (rest.Length == 0 || (rest.StartsWith("//", StringComparison.Ordinal) && !rest.Contains("*/", StringComparison.Ordinal))) point = lineEnd;
        var after = text[point..];
        return text[..(last + 1)] + separator + text[(last + 1)..point] + newLine + block + (after.StartsWith('\r') || after.StartsWith('\n') ? "" : newLine) + after.TrimStart(' ', '\t');
    }

    /// <summary>The leading whitespace of the line holding <paramref name="index"/>.</summary>
    private static string IndentOf(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }

    /// <summary>Finds structure in JSON with comments, skipping strings and comments.</summary>
    private sealed class Scanner(string text)
    {
        /// <summary>The index of the first character at or after <paramref name="from"/> that is not whitespace or comment; -1 at the end.</summary>
        public int NextSignificant(int from)
        {
            var i = from;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/') { i = text.IndexOf('\n', i); if (i < 0) return -1; continue; }
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*') { i = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return -1; i += 2; continue; }
                return i;
            }
            return -1;
        }

        /// <summary>The last significant character before <paramref name="before"/>, no earlier than <paramref name="floor"/>.</summary>
        public int PreviousSignificant(int before, int floor)
        {
            var last = floor;
            for (var i = NextSignificant(floor + 1); i >= 0 && i < before; i = NextSignificant(End(i)))
                last = text[i] == '"' ? End(i) - 1 : i;
            return last;
        }

        /// <summary>One past the token that starts at <paramref name="i"/>: a whole string, or one character.</summary>
        private int End(int i)
        {
            if (text[i] != '"') return i + 1;
            for (var j = i + 1; j < text.Length; j++)
            {
                if (text[j] == '\\') { j++; continue; }
                if (text[j] == '"') return j + 1;
            }
            return text.Length;
        }

        public int MatchingClose(int open)
        {
            var depth = 0;
            for (var i = open; i >= 0 && i < text.Length; i = NextSignificant(End(i)))
            {
                if (text[i] is '{' or '[') depth++;
                else if (text[i] is '}' or ']' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>The index just after the colon of the root object's <paramref name="name"/> property.</summary>
        public int? FindRootProperty(int root, int rootEnd, string name)
        {
            var depth = 0;
            for (var i = root; i >= 0 && i < rootEnd; i = NextSignificant(End(i)))
            {
                var c = text[i];
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
                else if (c == '"' && depth == 1)
                {
                    var end = End(i);
                    var colon = NextSignificant(end);
                    if (colon >= 0 && text[colon] == ':' && JsonSerializer.Deserialize<string>(text[i..end]) == name) return colon + 1;
                }
            }
            return null;
        }
    }
}
