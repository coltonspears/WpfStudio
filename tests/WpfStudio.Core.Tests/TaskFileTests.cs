using System.Text;
using WpfStudio.Core.Tasks;
using WpfStudio.Workspace;

namespace WpfStudio.Core.Tests;

/// <summary>tasks.json in Visual Studio Code's format: reading, merging, variables, command lines, output and adding a task.</summary>
public sealed class TaskFileTests
{
    private const string Path = @"C:\repo\.vscode\tasks.json";

    private const string VsCodeFile = """
        {
          // Comments and trailing commas are allowed, as in Visual Studio Code.
          "version": "2.0.0",
          "options": { "cwd": "${workspaceFolder}/src", "env": { "SHARED": "yes" } },
          "tasks": [
            {
              "label": "build",
              "type": "shell",
              "command": "dotnet",
              "args": ["build", { "value": "My App.sln", "quoting": "strong" }],
              "group": { "kind": "build", "isDefault": true },
              "problemMatcher": "$msCompile",
            },
            {
              "label": "watch",
              "type": "process",
              "command": "dotnet",
              "args": ["watch"],
              "isBackground": true,
              "problemMatcher": ["$tsc", "$msCompile"],
              "options": { "env": { "DOTNET_WATCH": "1" } },
              "windows": { "options": { "cwd": "C:\\work" } },
            },
            {
              "label": "all",
              "dependsOn": ["build", { "label": "watch" }],
              "dependsOrder": "sequence",
              "detail": "Build, then watch",
            },
            { "label": "helper", "command": "echo hi", "hide": true, "group": "test" },
          ],
        }
        """;

    [Fact]
    public void AVisualStudioCodeFileReadsWithItsDefaultsOverridesAndLines()
    {
        var file = TaskFileReader.Read(Path, VsCodeFile, TaskSource.VisualStudioCode);

        Assert.Empty(file.Problems);
        Assert.Equal(["build", "watch", "all", "helper"], file.Tasks.Select(task => task.Label));
        var build = file.Tasks[0];
        Assert.Equal((TaskKind.Shell, "dotnet", TaskGroup.Build, true, TaskProblemMatcher.MSBuild), (build.Kind, build.Command, build.Group, build.IsDefault, build.ProblemMatcher));
        Assert.Equal(["build", "My App.sln"], build.Args);
        Assert.Equal("${workspaceFolder}/src", build.WorkingDirectory);
        Assert.Equal("yes", build.Environment["SHARED"]);
        Assert.Equal((TaskSource.VisualStudioCode, Path, 6), (build.Source, build.SourcePath, build.Line));

        var watch = file.Tasks[1];
        Assert.Equal((TaskKind.Process, true, TaskProblemMatcher.MSBuild), (watch.Kind, watch.IsBackground, watch.ProblemMatcher));
        Assert.Equal(@"C:\work", watch.WorkingDirectory);
        Assert.Equal(("yes", "1"), (watch.Environment["SHARED"], watch.Environment["DOTNET_WATCH"]));
        Assert.Contains("$tsc", Assert.Single(watch.Notes));

        var all = file.Tasks[2];
        Assert.False(all.HasCommand);
        Assert.Equal(["build", "watch"], all.DependsOn);
        Assert.True(all.DependsInSequence);
        Assert.Equal("Build, then watch", all.Detail);
        Assert.True(file.Tasks[3].IsHidden);
        Assert.Equal(TaskGroup.Test, file.Tasks[3].Group);
    }

    [Fact]
    public void ATaskThatCannotRunIsReportedAndTheRestOfTheFileStillLoads()
    {
        var file = TaskFileReader.Read(Path, """
            {
              "tasks": [
                { "label": "lint", "type": "npm", "script": "lint" },
                { "type": "shell", "command": "echo" },
                { "label": "empty" },
                { "label": "ok", "command": "echo ok" },
                { "label": "ok", "command": "echo again" },
                5
              ]
            }
            """, TaskSource.VisualStudioCode);

        Assert.Equal("ok", Assert.Single(file.Tasks).Label);
        Assert.Equal("echo ok", file.Tasks[0].Command);
        Assert.Collection(file.Problems,
            problem => Assert.Equal((3, true), (problem.Line, problem.Message.Contains("type \"npm\""))),
            problem => Assert.Equal((4, true), (problem.Line, problem.Message.Contains("needs a \"label\""))),
            problem => Assert.Equal((5, true), (problem.Line, problem.Message.Contains("needs a \"command\" or a \"dependsOn\""))),
            problem => Assert.Equal((7, true), (problem.Line, problem.Message.Contains("Two tasks are labelled 'ok'"))),
            problem => Assert.Equal(8, problem.Line));
    }

    [Fact]
    public void AFileOptionThatCannotBeReadIsAProblemOfThatFileAndOffersNoneOfItsTasks()
    {
        var file = TaskFileReader.Read(Path, """{ "options": { "env": { "FOO": null } }, "tasks": [ { "label": "ok", "command": "echo ok" } ] }""", TaskSource.VisualStudioCode);

        Assert.Empty(file.Tasks);
        Assert.Contains("FOO must be text", Assert.Single(file.Problems).Message);
    }

    [Theory]
    [InlineData("{ \"tasks\": [ { \"label\": \"x\" ", 1)]
    [InlineData("{\n\n  \"tasks\": [ } ]\n}", 3)]
    public void InvalidJsonIsOneProblemWithItsLine(string text, int line)
    {
        var file = TaskFileReader.Read(Path, text, TaskSource.Shared);
        Assert.Empty(file.Tasks);
        var problem = Assert.Single(file.Problems);
        Assert.StartsWith("The file is not valid JSON", problem.Message);
        Assert.Equal(line, problem.Line);
        Assert.EndsWith($".vscode/tasks.json, line {line}", problem.Location);
    }

    [Fact]
    public void AnEmptyFileOrOneWithoutTasksHasNoProblems()
    {
        Assert.Empty(TaskFileReader.Read(Path, "  ", TaskSource.Shared).Problems);
        Assert.Empty(TaskFileReader.Read(Path, "{ \"version\": \"2.0.0\" }", TaskSource.Shared).Problems);
        Assert.Single(TaskFileReader.Read(Path, "[]", TaskSource.Shared).Problems);
    }

    [Fact]
    public void LaterSourcesWinForTheSameLabelAndKeepItsPlace()
    {
        static TaskFile File(TaskSource source, params string[] labels) =>
            new("", source, labels.Select(label => new StudioTask { Label = label, Command = source + " " + label, Source = source }).ToArray(), []);
        var merged = TaskCatalog.Merge([File(TaskSource.VisualStudioCode, "build", "test"), File(TaskSource.Shared, "test", "pack"), File(TaskSource.Personal, "build")]);

        Assert.Equal(["build", "test", "pack"], merged.Select(task => task.Label));
        Assert.Equal([TaskSource.Personal, TaskSource.Shared, TaskSource.Shared], merged.Select(task => task.Source));
    }

    [Fact]
    public void TheGroupDefaultIsItsMarkedTaskOrItsOnlyOne()
    {
        StudioTask Task(string label, TaskGroup group, bool isDefault = false) => new() { Label = label, Command = "x", Group = group, IsDefault = isDefault };
        Assert.Equal("b2", TaskCatalog.DefaultFor([Task("b1", TaskGroup.Build), Task("b2", TaskGroup.Build, true)], TaskGroup.Build)?.Label);
        Assert.Equal("t", TaskCatalog.DefaultFor([Task("b1", TaskGroup.Build), Task("t", TaskGroup.Test)], TaskGroup.Test)?.Label);
        Assert.Null(TaskCatalog.DefaultFor([Task("b1", TaskGroup.Build), Task("b2", TaskGroup.Build)], TaskGroup.Build));
        Assert.Null(TaskCatalog.DefaultFor([Task("b1", TaskGroup.Build, true), Task("b2", TaskGroup.Build, true)], TaskGroup.Build));
        Assert.Null(TaskCatalog.DefaultFor([Task("b1", TaskGroup.Build)], TaskGroup.Test));
    }

    private static readonly TaskVariableContext Context = new()
    {
        WorkspaceFolder = @"C:\repo", ActiveFile = @"C:\repo\src\App\Main.cs", ActiveLine = 12, SelectedText = "Hello",
        ProjectDirectory = @"C:\repo\src\App", Configuration = "Release"
    };

    [Theory]
    [InlineData("${workspaceFolder}", @"C:\repo")]
    [InlineData("${workspaceFolderBasename}", "repo")]
    [InlineData("${file}", @"C:\repo\src\App\Main.cs")]
    [InlineData("${relativeFile}", @"src\App\Main.cs")]
    [InlineData("${fileBasenameNoExtension}${fileExtname}", "Main.cs")]
    [InlineData("${fileDirname}", @"C:\repo\src\App")]
    [InlineData("${relativeFileDirname}", @"src\App")]
    [InlineData("line ${lineNumber}: ${selectedText}", "line 12: Hello")]
    [InlineData("${projectDir}/bin/${configuration}", @"C:\repo\src\App/bin/Release")]
    [InlineData("no variables", "no variables")]
    public void VariablesExpand(string text, string expected) => Assert.Equal(expected, TaskVariables.Expand(text, Context));

    [Fact]
    public void EnvironmentVariablesExpandAndMissingOnesAreEmpty()
    {
        Environment.SetEnvironmentVariable("WPFSTUDIO_TASK_VARIABLE", "set");
        try { Assert.Equal("set-", TaskVariables.Expand("${env:WPFSTUDIO_TASK_VARIABLE}-${env:WPFSTUDIO_TASK_UNSET}", Context)); }
        finally { Environment.SetEnvironmentVariable("WPFSTUDIO_TASK_VARIABLE", null); }
    }

    [Fact]
    public void AnUnknownVariableOrOneWithoutAValueStopsTheTask()
    {
        Assert.Contains("isn't a variable WpfStudio knows", Assert.Throws<TaskConfigurationException>(() => TaskVariables.Expand("${input:pick}", Context)).Message);
        Assert.Equal("${file} needs an open file.", Assert.Throws<TaskConfigurationException>(() => TaskVariables.Expand("${file}", Context with { ActiveFile = null })).Message);
        Assert.Contains("needs an open solution", Assert.Throws<TaskConfigurationException>(() => TaskVariables.Expand("${workspaceFolder}", new TaskVariableContext())).Message);
    }

    // Shells and programs are looked for on disk, so stand-ins are made for them.
    private static readonly string Tools = MakeTools();
    private static string MakeTools()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WpfStudio-TaskTools");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "pwsh.exe", "cmd.exe", "dotnet.exe" }) File.WriteAllText(System.IO.Path.Combine(folder, name), "");
        return folder;
    }
    private static string Tool(string name) => System.IO.Path.Combine(Tools, name);
    private static readonly TaskShell PowerShell = new(Tool("pwsh.exe"), []);
    private static string? Find(string name) => name switch { "dotnet" or "dotnet.exe" => Tool("dotnet.exe"), "cmd.exe" => Tool("cmd.exe"), "pwsh.exe" => Tool("pwsh.exe"), _ => null };
    private static TaskLaunch Launch(StudioTask task) => TaskCommandLine.Create(task, Context with { WorkspaceFolder = System.IO.Path.GetTempPath().TrimEnd('\\') }, PowerShell, Find, System.IO.Path.GetTempPath());

    [Fact]
    public void AProcessTaskStartsItsProgramFromPathWithQuotedArguments()
    {
        var launch = Launch(new StudioTask { Label = "p", Kind = TaskKind.Process, Command = "dotnet", Args = ["build", "My App.sln", "-p:Text=\"a b\""] });
        Assert.Equal(Tool("dotnet.exe"), launch.Executable);
        Assert.Equal("build \"My App.sln\" \"-p:Text=\\\"a b\\\"\"", launch.Arguments);
        Assert.Equal(["build", "My App.sln", "-p:Text=\"a b\""], BuildService.SplitArguments(launch.Arguments));
        Assert.Contains("not found on PATH", Assert.Throws<TaskConfigurationException>(() => Launch(new StudioTask { Label = "p", Kind = TaskKind.Process, Command = "nope" })).Message);
    }

    [Fact]
    public void AShellTaskRunsItsLineThroughPowerShellQuotedForIt()
    {
        var whole = Launch(new StudioTask { Label = "s", Command = "dotnet build | Out-Host" });
        Assert.Equal(PowerShell.Executable, whole.Executable);
        Assert.Equal("dotnet build | Out-Host", whole.DisplayCommand);
        Assert.Equal(["-NoLogo", "-Command", "dotnet build | Out-Host"], BuildService.SplitArguments(whole.Arguments));

        var parts = Launch(new StudioTask { Label = "s", Command = @"C:\Program Files\tool.exe", Args = ["it's here", "plain"] });
        Assert.Equal(@"& 'C:\Program Files\tool.exe' 'it''s here' plain", parts.DisplayCommand);
        Assert.Equal(["-NoLogo", "-Command", parts.DisplayCommand], BuildService.SplitArguments(parts.Arguments));
    }

    [Fact]
    public void ACmdShellGetsTheLineAsTypedInsideOneQuotedCommand()
    {
        var launch = Launch(new StudioTask { Label = "c", Command = "echo", Args = ["a b", "c"], Shell = new TaskShell("cmd.exe", []) });
        Assert.Equal(Tool("cmd.exe"), launch.Executable);
        Assert.Equal("/d /s /c \"echo \"a b\" c\"", launch.Arguments);
    }

    [Fact]
    public void TheWorkingFolderAndEnvironmentExpandAndTheMSBuildMatcherTurnsOffTheLiveLogger()
    {
        var folder = System.IO.Path.GetTempPath().TrimEnd('\\');
        var launch = Launch(new StudioTask { Label = "w", Command = "dotnet build", WorkingDirectory = "${workspaceFolder}", Environment = new Dictionary<string, string> { ["MODE"] = "${configuration}" }, ProblemMatcher = TaskProblemMatcher.MSBuild });
        Assert.Equal(folder, launch.WorkingDirectory.TrimEnd('\\'));
        Assert.Equal("Release", launch.Environment["MODE"]);
        Assert.Equal("off", launch.Environment["MSBUILDTERMINALLOGGER"]);
        var own = Launch(new StudioTask { Label = "w", Command = "x", ProblemMatcher = TaskProblemMatcher.MSBuild, Environment = new Dictionary<string, string> { ["MSBUILDTERMINALLOGGER"] = "on" } });
        Assert.Equal("on", own.Environment["MSBUILDTERMINALLOGGER"]);
        Assert.Contains("does not exist", Assert.Throws<TaskConfigurationException>(() => Launch(new StudioTask { Label = "w", Command = "x", WorkingDirectory = @"C:\does\not\exist\here" })).Message);
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    public void WindowsQuotingReadsBackUnchanged(string value, string quoted)
    {
        Assert.Equal(quoted, TaskCommandLine.QuoteForWindows(value));
        Assert.Equal([value], BuildService.SplitArguments(TaskCommandLine.QuoteForWindows(value)));
    }

    private static List<string> Lines(params string[] chunks)
    {
        var lines = new List<string>();
        var reader = new TaskOutputLines(lines.Add);
        foreach (var chunk in chunks) reader.Write(Encoding.UTF8.GetBytes(chunk));
        reader.Flush();
        return lines;
    }

    [Fact]
    public void ColoursAndTitlesAreDroppedAndLinesEndAtLineBreaks()
    {
        Assert.Equal(["Program.cs(3,5): error CS1002: ; expected", "done"],
            Lines("\u001b]0;C:\\WINDOWS\\system32\\cmd.exe\u0007\u001b[?25l\u001b[31mProgram.cs(3,5): \u001b[1merror\u001b[0m CS1002: ; expected\r\n", "done"));
    }

    [Fact]
    public void ACarriageReturnRewritesTheLineAndCursorMovesBreakOrSpaceIt()
    {
        Assert.Equal(["100%", "a   b", "next row", "after"], Lines("10%\r50%\r100%\r\n", "a\u001b[3Cb", "\u001b[5;1Hnext row\u001b[6;1H", "after"));
    }

    [Fact]
    public void ACharacterSplitAcrossChunksIsDecodedWhole()
    {
        var bytes = Encoding.UTF8.GetBytes("Ünïcode ✓\n");
        var lines = new List<string>();
        var reader = new TaskOutputLines(lines.Add);
        foreach (var b in bytes) reader.Write([b]);
        Assert.Equal(["Ünïcode ✓"], lines);
    }

    private static readonly TaskDraft Draft = new("pack", TaskKind.Shell, "dotnet pack", [], null, new Dictionary<string, string>(), TaskGroup.None, false, true);

    [Fact]
    public void AddingToAnEmptyFileStartsFromTheTemplate()
    {
        var text = TaskFileWriter.AddTask("", Draft, TaskSource.Shared);
        var file = TaskFileReader.Read(Path, text, TaskSource.Shared);
        Assert.Empty(file.Problems);
        var task = Assert.Single(file.Tasks);
        Assert.Equal(("pack", "dotnet pack", TaskProblemMatcher.MSBuild), (task.Label, task.Command, task.ProblemMatcher));
        Assert.Contains("// Tasks for this repository", text);
    }

    [Fact]
    public void AddingKeepsCommentsTrailingCommasAndLineEndings()
    {
        var original = "{\r\n  // keep me\r\n  \"version\": \"2.0.0\",\r\n  \"tasks\": [\r\n    { \"label\": \"build\", \"command\": \"dotnet build\" }, // and me\r\n  ],\r\n  \"other\": { \"tasks\": [] }\r\n}\r\n";
        var draft = Draft with
        {
            Kind = TaskKind.Process, Command = "dotnet", Args = ["pack", "-c", "Release"], WorkingDirectory = "${workspaceFolder}/src",
            Environment = new Dictionary<string, string> { ["A"] = "1" }, Group = TaskGroup.Build, IsDefault = true, MatchMSBuildProblems = false
        };
        var text = TaskFileWriter.AddTask(original, draft, TaskSource.Shared);

        Assert.Contains("// keep me", text);
        Assert.Contains("// and me", text);
        Assert.Contains("\"other\": { \"tasks\": [] }", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
        var file = TaskFileReader.Read(Path, text, TaskSource.Shared);
        Assert.Empty(file.Problems);
        Assert.Equal(["build", "pack"], file.Tasks.Select(task => task.Label));
        var pack = file.Tasks[1];
        Assert.Equal((TaskKind.Process, "dotnet", TaskGroup.Build, true, "${workspaceFolder}/src", "1"), (pack.Kind, pack.Command, pack.Group, pack.IsDefault, pack.WorkingDirectory, pack.Environment["A"]));
        Assert.Equal(["pack", "-c", "Release"], pack.Args);
        Assert.Contains("\r\n    {\r\n      \"label\": \"pack\",", text);
    }

    [Fact]
    public void AddingToAnEmptyListOrAFileWithoutOneCreatesIt()
    {
        var empty = TaskFileWriter.AddTask("{\n  \"version\": \"2.0.0\",\n  \"tasks\": []\n}\n", Draft, TaskSource.Personal);
        Assert.Equal("pack", Assert.Single(TaskFileReader.Read(Path, empty, TaskSource.Personal).Tasks).Label);
        Assert.Contains("\"tasks\": [\n    {\n      \"label\": \"pack\"", empty);

        var none = TaskFileWriter.AddTask("{\n  \"version\": \"2.0.0\"\n}\n", Draft, TaskSource.Personal);
        Assert.Equal("pack", Assert.Single(TaskFileReader.Read(Path, none, TaskSource.Personal).Tasks).Label);
        Assert.Contains("\"version\": \"2.0.0\",\n  \"tasks\": [", none);

        var bare = TaskFileWriter.AddTask("{}", Draft, TaskSource.Personal);
        Assert.Equal("pack", Assert.Single(TaskFileReader.Read(Path, bare, TaskSource.Personal).Tasks).Label);
    }

    [Fact]
    public void AFileThatIsNotATasksObjectIsNotChanged()
    {
        Assert.Throws<InvalidOperationException>(() => TaskFileWriter.AddTask("[1, 2]", Draft, TaskSource.Shared));
        Assert.Throws<InvalidOperationException>(() => TaskFileWriter.AddTask("{ \"tasks\": 5 }", Draft, TaskSource.Shared));
        Assert.Throws<InvalidOperationException>(() => TaskFileWriter.AddTask("{ \"tasks\": [ { \"label\": \"pack\", \"command\": \"x\" } ] }", Draft, TaskSource.Shared));
    }
}
