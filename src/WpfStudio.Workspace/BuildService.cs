using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class BuildService
{
    /// <summary>The non-package assemblies a XAML preview needs, resolved by MSBuild for the project and its project references.</summary>
    public Task<PreviewReferenceSet> ResolvePreviewReferencesAsync(string projectPath, string configuration = "Debug", string? targetFramework = null, CancellationToken cancellationToken = default) =>
        PreviewReferenceResolver.ResolveAsync(projectPath, configuration, targetFramework, cancellationToken);

    public Task<ResolvedLaunch> ResolveLaunchAsync(string projectPath, string configuration = "Debug", string? targetFramework = null, string? profileName = null, CancellationToken cancellationToken = default)
        => ResolveLaunchAsync(projectPath, configuration, targetFramework, profileName, null, null, cancellationToken);

    /// <summary>Evaluates with the MSBuild and SolutionDir that built the project, so the program is the one that build produced.</summary>
    private static async Task<ResolvedLaunch> ResolveLaunchAsync(string projectPath, string configuration, string? targetFramework, string? profileName, string? solutionPath, string? msbuildPath, CancellationToken cancellationToken)
    {
        projectPath = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(projectPath)!;
        var project = await ProjectDiscovery.EvaluateAsync(projectPath, configuration, targetFramework, cancellationToken, solutionPath, msbuildPath).ConfigureAwait(false);
        if (project.OutputType is not ("Exe" or "WinExe")) throw new InvalidOperationException("Choose an executable startup project.");
        if (string.IsNullOrWhiteSpace(targetFramework) && !string.IsNullOrWhiteSpace(project.TargetFrameworks))
        {
            var frameworks = project.TargetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (frameworks.Length > 1) throw new InvalidOperationException("Choose a target framework for the multi-target startup project.");
            if (frameworks.Length == 1) project = await ProjectDiscovery.EvaluateAsync(projectPath, configuration, frameworks[0], cancellationToken, solutionPath, msbuildPath).ConfigureAwait(false);
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
        request = request with { Path = path };
        var directory = Path.GetDirectoryName(path)!;
        var output = new BuildOutput(progress, directory);
        if (request.Operation == BuildOperation.Test || !await Task.Run(() => ProjectDiscovery.RequiresFrameworkMSBuild(path), cancellationToken).ConfigureAwait(false))
            return await RunProcessAsync(ProcessRunner.DotNet(directory, CreateArguments(request).ToArray()), output, cancellationToken).ConfigureAwait(false);
        // Legacy project files import WPF markup compilation and other .NET Framework targets that only Visual Studio's MSBuild includes.
        var build = request.Operation == BuildOperation.Run ? request with { Operation = BuildOperation.Build } : request;
        ProcessStartInfo start;
        var msbuild = await FindFrameworkMSBuildAsync(cancellationToken).ConfigureAwait(false);
        if (msbuild is not null)
        {
            output.Info("Legacy .NET Framework projects use Visual Studio MSBuild: " + msbuild);
            start = ProcessRunner.Create(msbuild, directory, CreateFrameworkMSBuildArguments(build));
        }
        else
        {
            output.Info("Visual Studio MSBuild was not found, so the .NET SDK handles this legacy .NET Framework project. It cannot compile WPF markup or restore packages.config. " +
                "Install Visual Studio or Build Tools for Visual Studio with the .NET desktop build tools workload.");
            start = ProcessRunner.DotNet(directory, CreateArguments(build).ToArray());
        }
        var result = await RunProcessAsync(start, output, cancellationToken).ConfigureAwait(false);
        if (request.Operation != BuildOperation.Run || result.ExitCode != 0 || result.Cancelled) return result;
        // `dotnet run` needs SDK-style targets, so start the built program the way it would. Only Visual Studio's MSBuild received SolutionDir.
        var launch = await ResolveLaunchAsync(path, request.Configuration, request.TargetFramework, request.LaunchProfile,
            msbuild is null ? null : request.SolutionPath, msbuild, cancellationToken).ConfigureAwait(false);
        output.Info("Starting " + launch.Program);
        return await RunProcessAsync(CreateLaunchStartInfo(launch, request.Arguments), output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BuildResult> RunProcessAsync(ProcessStartInfo start, BuildOutput output, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(start.FileName)}.");
        using var registration = cancellationToken.Register(() => ProcessRunner.Kill(process));
        async Task PumpAsync(StreamReader reader, bool error)
        {
            await foreach (var line in BoundedLineReader.ReadLinesAsync(reader).ConfigureAwait(false)) output.Report(line, error);
        }
        var stdout = PumpAsync(process.StandardOutput, false);
        var stderr = PumpAsync(process.StandardError, true);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new BuildResult(process.ExitCode, false, output.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ProcessRunner.Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new BuildResult(-1, true, output.Diagnostics);
        }
    }

    /// <summary>Forwards tool output and collects its distinct diagnostics, across every process of one operation.</summary>
    private sealed class BuildOutput(IProgress<BuildOutputEvent>? progress, string baseDirectory)
    {
        private readonly List<WorkspaceDiagnostic> _diagnostics = [];
        private readonly HashSet<WorkspaceDiagnostic> _known = [];
        private readonly object _gate = new();
        private bool _reportedLimit;
        public IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get { lock (_gate) return _diagnostics.ToArray(); } }
        public void Info(string text) => progress?.Report(new BuildOutputEvent(text));
        public void Report(string line, bool error)
        {
            var diagnostic = ParseDiagnostic(line, baseDirectory);
            var reportLimit = false;
            if (diagnostic is not null) lock (_gate)
            {
                if (_known.Contains(diagnostic)) diagnostic = null;
                else if (_diagnostics.Count < 2000) { _known.Add(diagnostic); _diagnostics.Add(diagnostic); }
                else { diagnostic = null; reportLimit = !_reportedLimit; _reportedLimit = true; }
            }
            if (reportLimit) progress?.Report(new BuildOutputEvent("Additional build diagnostics omitted after 2,000 entries. See the build output for details."));
            progress?.Report(new BuildOutputEvent(line, error, diagnostic));
        }
    }

    private static string? _frameworkMSBuild;
    /// <summary>MSBuild from Visual Studio or Build Tools for Visual Studio, located through the vswhere tool both installers include.</summary>
    private static async Task<string?> FindFrameworkMSBuildAsync(CancellationToken cancellationToken)
    {
        if (_frameworkMSBuild is { } known && File.Exists(known)) return known;
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        // Prefer a released Visual Studio; a preview still builds better than the .NET SDK can.
        foreach (var prerelease in new[] { false, true })
        {
            var arguments = new List<string> { "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe", "-utf8" };
            if (prerelease) arguments.Add("-prerelease");
            var start = ProcessRunner.Create(vswhere, Path.GetDirectoryName(vswhere)!, arguments);
            start.StandardOutputEncoding = Encoding.UTF8;
            var result = await ProcessRunner.CaptureAsync(start, cancellationToken).ConfigureAwait(false);
            if (result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(File.Exists) is { } path)
                return _frameworkMSBuild = path;
        }
        return null;
    }

    private static ProcessStartInfo CreateLaunchStartInfo(ResolvedLaunch launch, string? arguments)
    {
        // A .NET (Core) project's TargetPath is its managed entry assembly rather than an executable.
        var managed = launch.Program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed ? "dotnet" : launch.Program)
        {
            WorkingDirectory = launch.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (managed) start.ArgumentList.Add(launch.Program);
        // As with `dotnet run`, arguments given for this run replace the launch profile's.
        foreach (var argument in string.IsNullOrWhiteSpace(arguments) ? launch.Arguments : SplitArguments(arguments)) start.ArgumentList.Add(argument);
        foreach (var variable in launch.Environment) start.Environment[variable.Key] = variable.Value;
        return start;
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

    /// <summary>Visual Studio MSBuild arguments for a legacy project file, or a solution that contains one. Restore includes packages.config.</summary>
    public static IReadOnlyList<string> CreateFrameworkMSBuildArguments(BuildRequest request)
    {
        // Reused nodes would outlive the build and keep its output pipes open.
        var args = new List<string> { request.Path, "-nologo", "-maxCpuCount", "-nodeReuse:false", "-verbosity:minimal" };
        switch (request.Operation)
        {
            case BuildOperation.Restore: args.AddRange(["-t:Restore", "-p:RestorePackagesConfig=true"]); break;
            case BuildOperation.Build: args.AddRange(["-restore", "-p:RestorePackagesConfig=true", "-t:Build"]); break;
            case BuildOperation.Rebuild: args.AddRange(["-restore", "-p:RestorePackagesConfig=true", "-t:Rebuild"]); break;
            case BuildOperation.Clean: args.Add("-t:Clean"); break;
            default: throw new ArgumentOutOfRangeException(nameof(request), "Visual Studio MSBuild restores, builds, rebuilds and cleans; it does not run or test projects.");
        }
        args.Add($"-p:Configuration={request.Configuration}");
        if (!string.IsNullOrWhiteSpace(request.TargetFramework)) args.Add($"-p:TargetFramework={request.TargetFramework}");
        if (request.XamlDebuggingInformation) args.Add("-p:XamlDebuggingInformation=true");
        // Projects use SolutionDir for packages.config restore, $(SolutionDir) imports and output paths.
        if (ProjectDiscovery.SolutionDirArgument(request.Path, request.SolutionPath) is { } solutionDir) args.Add(solutionDir);
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
