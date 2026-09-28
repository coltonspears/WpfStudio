using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class BuildService
{
    public async Task<ResolvedLaunch> ResolveLaunchAsync(string projectPath, string configuration = "Debug", string? targetFramework = null, string? profileName = null, CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(projectPath)!;
        var project = await ProjectDiscovery.EvaluateAsync(projectPath, configuration, targetFramework, cancellationToken).ConfigureAwait(false);
        if (project.OutputType is not ("Exe" or "WinExe")) throw new InvalidOperationException("Choose an executable startup project.");
        if (string.IsNullOrWhiteSpace(targetFramework) && !string.IsNullOrWhiteSpace(project.TargetFrameworks))
        {
            var frameworks = project.TargetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (frameworks.Length > 1) throw new InvalidOperationException("Choose a target framework for the multi-target startup project.");
            if (frameworks.Length == 1) project = await ProjectDiscovery.EvaluateAsync(projectPath, configuration, frameworks[0], cancellationToken).ConfigureAwait(false);
        }
        var profiles = ReadLaunchProfiles(projectPath);
        var profile = string.IsNullOrWhiteSpace(profileName) ? null : profiles.FirstOrDefault(p => p.Name == profileName) ?? throw new InvalidOperationException($"Launch profile '{profileName}' was not found.");
        var program = project.OutputPath;
        if (profile?.CommandName == "Executable") program = profile.ExecutablePath ?? throw new InvalidOperationException("The Executable launch profile has no executablePath.");
        else if (profile?.CommandName is { } command && command != "Project") throw new NotSupportedException($"Launch profile type '{command}' is not supported for desktop debugging.");
        if (string.IsNullOrWhiteSpace(program)) throw new InvalidOperationException("The project has no evaluated TargetPath. Select a target framework and build the project.");
        program = Path.GetFullPath(Environment.ExpandEnvironmentVariables(program), directory);
        if (!File.Exists(program)) throw new FileNotFoundException("Build the startup project before launching.", program);
        var workingDirectory = string.IsNullOrWhiteSpace(profile?.WorkingDirectory) ? directory : Path.GetFullPath(Environment.ExpandEnvironmentVariables(profile.WorkingDirectory), directory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException($"Launch working directory does not exist: {workingDirectory}");
        var environment = profile?.EnvironmentVariables.ToDictionary(e => e.Key, e => Environment.ExpandEnvironmentVariables(e.Value)) ?? [];
        return new ResolvedLaunch(program, workingDirectory, string.IsNullOrWhiteSpace(profile?.CommandLineArgs) ? [] : SplitArguments(profile.CommandLineArgs), environment);
    }

    public async Task<BuildResult> RunAsync(BuildRequest request, IProgress<BuildOutputEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(request.Path);
        if (!File.Exists(path)) throw new FileNotFoundException("Build target does not exist.", path);
        if (request.Operation == BuildOperation.Run && !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an executable startup project before running.", nameof(request));
        var arguments = CreateArguments(request with { Path = path });
        using var process = new Process { StartInfo = ProcessRunner.DotNet(Path.GetDirectoryName(path)!, arguments.ToArray()), EnableRaisingEvents = true };
        var diagnostics = new List<WorkspaceDiagnostic>();
        var knownDiagnostics = new HashSet<WorkspaceDiagnostic>();
        var reportedDiagnosticLimit = false;
        var gate = new object();
        void Report(string line, bool error)
        {
            var diagnostic = ParseDiagnostic(line, Path.GetDirectoryName(path)!);
            var reportLimit = false;
            if (diagnostic is not null) lock (gate)
            {
                if (knownDiagnostics.Contains(diagnostic)) diagnostic = null;
                else if (diagnostics.Count < 2000) { knownDiagnostics.Add(diagnostic); diagnostics.Add(diagnostic); }
                else { diagnostic = null; reportLimit = !reportedDiagnosticLimit; reportedDiagnosticLimit = true; }
            }
            if (reportLimit) progress?.Report(new BuildOutputEvent("Additional build diagnostics omitted after 2,000 entries. See the build output for details."));
            progress?.Report(new BuildOutputEvent(line, error, diagnostic));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Could not start dotnet.");
        using var registration = cancellationToken.Register(() => ProcessRunner.Kill(process));
        async Task PumpAsync(StreamReader reader, bool error)
        {
            await foreach (var line in BoundedLineReader.ReadLinesAsync(reader).ConfigureAwait(false)) Report(line, error);
        }
        var stdout = PumpAsync(process.StandardOutput, false);
        var stderr = PumpAsync(process.StandardError, true);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new BuildResult(process.ExitCode, false, diagnostics.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ProcessRunner.Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new BuildResult(-1, true, diagnostics.ToArray());
        }
    }

    public async Task<IReadOnlyList<LaunchTarget>> DiscoverLaunchTargetsAsync(string workspacePath, CancellationToken cancellationToken = default, string configuration = "Debug")
    {
        var targets = new List<LaunchTarget>();
        foreach (var projectPath in ProjectDiscovery.GetProjectPaths(workspacePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = await ProjectDiscovery.EvaluateAsync(projectPath, configuration, null, cancellationToken).ConfigureAwait(false);
            if (project.OutputType is not ("Exe" or "WinExe")) continue;
            var frameworks = (string.IsNullOrWhiteSpace(project.TargetFrameworks) ? project.TargetFramework : project.TargetFrameworks).Split(';', StringSplitOptions.RemoveEmptyEntries);
            targets.Add(new LaunchTarget(projectPath, Path.GetFileNameWithoutExtension(projectPath), frameworks, ReadLaunchProfiles(projectPath)));
        }
        return targets;
    }

    public static IReadOnlyList<LaunchProfile> ReadLaunchProfiles(string projectPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties", "launchSettings.json");
        if (!File.Exists(path)) return [];
        using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!json.RootElement.TryGetProperty("profiles", out var profiles)) return [];
        return profiles.EnumerateObject().Select(profile =>
        {
            string? Read(string name) => profile.Value.TryGetProperty(name, out var value) ? value.GetString() : null;
            var environment = new Dictionary<string, string>();
            if (profile.Value.TryGetProperty("environmentVariables", out var variables))
                foreach (var variable in variables.EnumerateObject()) environment[variable.Name] = variable.Value.ToString();
            return new LaunchProfile(profile.Name, Read("commandName"), Read("commandLineArgs"), Read("workingDirectory"), environment, Read("executablePath"));
        }).ToArray();
    }

    public static IReadOnlyList<string> CreateArguments(BuildRequest request)
    {
        var args = new List<string>();
        switch (request.Operation)
        {
            case BuildOperation.Restore: args.AddRange(["restore", request.Path]); break;
            case BuildOperation.Build: args.AddRange(["build", request.Path]); break;
            case BuildOperation.Rebuild: args.AddRange(["build", request.Path, "-t:Rebuild"]); break;
            case BuildOperation.Clean: args.AddRange(["clean", request.Path]); break;
            case BuildOperation.Run: args.AddRange(["run", "--project", request.Path]); break;
            case BuildOperation.Test: args.AddRange(["test", request.Path]); break;
            default: throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (request.Operation != BuildOperation.Restore) args.AddRange(["--configuration", request.Configuration]);
        else args.Add($"-p:Configuration={request.Configuration}");
        if (!string.IsNullOrWhiteSpace(request.TargetFramework))
        {
            if (request.Operation == BuildOperation.Restore) args.Add($"-p:TargetFramework={request.TargetFramework}");
            else args.AddRange(["--framework", request.TargetFramework]);
        }
        if (request.XamlDebuggingInformation) args.Add("-p:XamlDebuggingInformation=true");
        if (request.Operation == BuildOperation.Run)
        {
            if (!string.IsNullOrWhiteSpace(request.LaunchProfile)) args.AddRange(["--launch-profile", request.LaunchProfile]);
            if (!string.IsNullOrWhiteSpace(request.Arguments)) { args.Add("--"); args.AddRange(SplitArguments(request.Arguments)); }
        }
        else args.Add("--nologo");
        return args;
    }

    public static WorkspaceDiagnostic? ParseDiagnostic(string text, string baseDirectory)
    {
        var match = DiagnosticRegex().Match(text.Trim());
        if (!match.Success) return null;
        var file = match.Groups["path"].Value.Trim();
        var line = int.TryParse(match.Groups["line"].Value, out var parsedLine) ? parsedLine : 0;
        var column = int.TryParse(match.Groups["column"].Value, out var parsedColumn) ? parsedColumn : 0;
        string? path = null;
        if (line > 0 || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            try { path = Path.GetFullPath(file, baseDirectory); }
            catch (ArgumentException) { }
        }
        return new WorkspaceDiagnostic(match.Groups["code"].Value, match.Groups["message"].Value, match.Groups["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning", path, line, column, 0, 0);
    }

    // CommandLineToArgvW-compatible quoting, without invoking a shell.
    public static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var text = new StringBuilder();
            var quoted = false;
            while (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index])))
            {
                var backslashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\') { backslashes++; index++; }
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    text.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) text.Append('"');
                    else quoted = !quoted;
                    index++;
                }
                else
                {
                    text.Append('\\', backslashes);
                    if (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index]))) text.Append(commandLine[index++]);
                }
            }
            result.Add(text.ToString());
        }
        return result;
    }

    [GeneratedRegex(@"^(?<path>.+?)(?:\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\))?\s*:\s*(?<severity>error|warning)\s+(?<code>\w+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticRegex();
}
